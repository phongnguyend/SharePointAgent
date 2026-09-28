using System.Net;
using System.Text;
using Azure.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ContentSafetyTests
{
    private sealed class Handler(int severity, HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("secret", request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
            Assert.Contains("api-version=2024-09-01", request.RequestUri!.Query);
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var categories = new[] { "Hate", "Sexual", "Violence", "SelfHarm" }.Select(category => new { category, severity });
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { categoriesAnalysis = categories }), Encoding.UTF8, "application/json")
            };
        }
    }

    [Theory]
    [InlineData(0, 200, "Allowed", 0)]
    [InlineData(3, 200, "Allowed", 0)]
    [InlineData(4, 200, "Blocked", 422)]
    [InlineData(7, 200, "Blocked", 422)]
    [InlineData(0, 429, "Failed", 503)]
    [InlineData(8, 200, "Failed", 503)]
    public async Task EnforcesThresholdsAndRecordsOutcomes(int severity, int status, string outcome, int error)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        await using var db = new SharePointIndexDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new SharePointIndexDbContext(options));
        using var handler = new Handler(severity, (HttpStatusCode)status);
        using var http = new HttpClient(handler);
        var service = new ContentSafetyService(http, Substitute.For<TokenCredential>(), factory,
            Options.Create(new ContentSafetyOptions { Enabled = true, Endpoint = "https://test.example", UseManagedIdentity = false, ApiKey = "secret" }));
        var user = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        if (error > 0)
        {
            var exception = await Assert.ThrowsAsync<ContentSafetyRejectedException>(() => service.CheckAsync("hello", user, conversation, default));
            Assert.Equal(error, exception.StatusCode);
        }
        else
        {
            var assessment = await service.CheckAsync("hello", user, conversation, default);
            var question = Guid.NewGuid();
            await service.LinkQuestionAsync(assessment, question, default);
            Assert.Equal(question, (await db.ContentSafetyUsage.SingleAsync()).QuestionId);
        }
        var row = await db.ContentSafetyUsage.SingleAsync();
        Assert.Equal(outcome, row.Status);
        Assert.Equal(user, row.UserId);
        Assert.Equal(conversation, row.ConversationId);
        Assert.Equal(5, row.CharacterCount);
        Assert.Equal(1, row.EstimatedTextRecords);
        Assert.Equal(status, row.HttpStatusCode);
        Assert.Empty(await db.UserTokenUsage.ToListAsync());
    }

    [Fact]
    public async Task SplitsLongTextAndPreservesAttachmentAttribution()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        await using var db = new SharePointIndexDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new SharePointIndexDbContext(options));
        using var handler = new Handler(0, HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var service = new ContentSafetyService(http, Substitute.For<TokenCredential>(), factory,
            Options.Create(new ContentSafetyOptions { Enabled = true, Endpoint = "https://test.example", UseManagedIdentity = false, ApiKey = "secret" }));
        var attachment = Guid.NewGuid();
        var text = new string('a', 9999) + "😀tail";
        var assessment = await service.CheckAsync(text, null, null, default, "AttachmentText", attachmentId: attachment);
        var rows = await db.ContentSafetyUsage.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(text.Length, rows.Sum(x => x.CharacterCount));
        Assert.All(rows, row =>
        {
            Assert.Equal(assessment, row.AssessmentId);
            Assert.Equal(attachment, row.AttachmentId);
            Assert.Equal("AttachmentText", row.Operation);
        });
        var submitted = handler.Bodies.Select(body => System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("text").GetString());
        Assert.Equal(text, string.Concat(submitted));
    }

    [Fact]
    public async Task DisabledChecksDoNotCallProviderOrDatabase()
    {
        var service = new ContentSafetyService(null!, null!, null!, Options.Create(new ContentSafetyOptions()));
        Assert.Null(await service.CheckAsync("text", null, null, default));
    }
}

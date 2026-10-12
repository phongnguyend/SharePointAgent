using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Options;
using SharePointAgent.Api;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class SystemOneUsageTests
{
    [Theory]
    [InlineData("Jev")]
    [InlineData("MicrosoftDecision")]
    public async Task RecordsUsageWithDatabaseIdAndReportsFiltersAndPagination(string provider)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var databaseId = Guid.NewGuid();
        connection.CreateFunction("NEWSEQUENTIALID", () => databaseId.ToString().ToUpperInvariant());
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection)
            .ReplaceService<IModelCustomizer, SortableDates>().Options;
        await using var db = new SharePointIndexDbContext(options);
        await db.Database.EnsureCreatedAsync();
        using var activity = new Activity("jev-test").Start();
        using var http = new HttpClient(new Handler());
        var client = new JevClient(http, Options.Create(new JevOptions { ApiKey = "test" }), new Factory(options));
        var userId = Guid.NewGuid();
        var context = new SystemOneUsageContext(userId, Guid.NewGuid(), Guid.NewGuid());
        var questions = new Dictionary<string, JevQuestion> { ["check"] = new JevNoulQuestion("Yes?") };
        if (provider == "Jev")
        {
            await client.EvaluateAsync("private state", questions, context);
        }
        else
        {
            var microsoft = new MicrosoftDecisionClient(http, Options.Create(new MicrosoftDecisionOptions
            {
                Endpoint = "https://decision.example/providers/microsoft/v1/systemone", ApiKey = "test"
            }), contextFactory: new Factory(options));
            await microsoft.EvaluateAsync("private state", new Dictionary<string, MicrosoftDecisionQuestion>
            {
                ["check"] = new MicrosoftDecisionNoulQuestion("Yes?")
            }, context);
        }
        var row = await db.SystemOneUsage.SingleAsync();
        Assert.Equal(databaseId, row.Id);
        Assert.Equal(provider, row.Provider);
        Assert.Equal(userId, row.UserId);
        Assert.Equal(context.QuestionId, row.QuestionId);
        Assert.Equal(activity.TraceId.ToString(), row.TraceId);
        Assert.Equal("jev-resolved", row.ModelId);
        Assert.Equal(123, row.InputTokens);
        Assert.Equal(7, row.OutputTokens);
        Assert.Equal(130, row.TotalTokens);
        Assert.Equal(1, row.QuestionCount);
        Assert.Equal(row.CreatedAtUtc.Year * 10000 + row.CreatedAtUtc.Month * 100 + row.CreatedAtUtc.Day, row.Day);

        var day = DateOnly.FromDateTime(row.CreatedAtUtc.UtcDateTime);
        var report = await Read(db, day, user: userId.ToString(), trace: row.TraceId);
        Assert.Equal(130, report.GetProperty("summary").GetProperty("totalTokens").GetInt64());
        Assert.Equal(1, report.GetProperty("daily").GetArrayLength());
        Assert.Equal(1, report.GetProperty("models").GetArrayLength());
        Assert.Equal(1, report.GetProperty("items").GetArrayLength());
        Assert.Equal(provider, report.GetProperty("items")[0].GetProperty("provider").GetString());
        var filtered = await SystemOneUsageEndpoints.ReadAsync(db, day, day, provider: "other");
        var filteredJson = JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<IValueHttpResult>(filtered).Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(0, filteredJson.GetProperty("summary").GetProperty("calls").GetInt32());
        Assert.Equal(0, (await Read(db, day, model: "other")).GetProperty("summary").GetProperty("calls").GetInt32());
        Assert.Equal(0, (await Read(db, day, trace: "other")).GetProperty("items").GetArrayLength());
        Assert.Equal(0, (await Read(db, day, skip: 1)).GetProperty("items").GetArrayLength());
        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await SystemOneUsageEndpoints.ReadAsync(db, day, day, top: 0)).StatusCode);
        await client.ListModelsAsync();
        Assert.Equal(1, await db.SystemOneUsage.CountAsync());
    }

    private static async Task<JsonElement> Read(SharePointIndexDbContext db, DateOnly day, string? user = null,
        string? model = null, string? trace = null, int skip = 0)
    {
        var response = await SystemOneUsageEndpoints.ReadAsync(db, day, day, model, user, trace, skip);
        return JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<IValueHttpResult>(response).Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class Factory(DbContextOptions<SharePointIndexDbContext> options) : IDbContextFactory<SharePointIndexDbContext>
    {
        public SharePointIndexDbContext CreateDbContext() => new(options);
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Get ? "{\"models\":[]}" :
                    """{"model":"jev-resolved","answers":{"check":{"type":"noul","noul":0.9}},"usage":{"input_tokens":123,"output_tokens":7}}""",
                    System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class SortableDates(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(x => x.GetProperties())
                .Where(x => x.ClrType == typeof(DateTimeOffset) || x.ClrType == typeof(DateTimeOffset?)))
            {
                property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
            }
        }
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class EmbeddingTokenUsageTests
{
    [Fact]
    public async Task RecordsEveryCallWithAttributionAndProviderModelWithoutChargingChatQuota()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        await using var db = new SharePointIndexDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new SharePointIndexDbContext(options));
        var inner = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        inner.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1 }) { ModelId = "embedding-model" }])
            { Usage = new UsageDetails { InputTokenCount = 12, TotalTokenCount = 12 } });
        using var generator = new TrackedEmbeddingGenerator(inner, factory, "embedding-deployment");
        var userId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;
        using (EmbeddingUsageScope.Begin(new(UserId: userId, QuestionId: questionId, ConversationId: conversationId)))
        {
            using (EmbeddingUsageScope.Begin(new(Operation: "AttachmentIndex", AttachmentId: attachmentId, ChunkNumber: 0)))
            {
                await generator.GenerateAsync(["text"]);
            }
            using (EmbeddingUsageScope.Begin(new(Operation: "SharePointReindex", DriveId: "drive", FileId: "file", ChunkNumber: 1)))
            {
                await generator.GenerateAsync(["text"]);
            }
        }
        var rows = await db.EmbeddingTokenUsage.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(userId, row.UserId);
            Assert.Equal(questionId, row.QuestionId);
            Assert.Equal(conversationId, row.ConversationId);
            Assert.Equal("embedding-model", row.EmbeddingModelId);
            Assert.Equal("embedding-deployment", row.DeploymentId);
            Assert.Equal(12, row.TotalTokens);
            Assert.InRange(row.CreatedAtUtc, before, DateTimeOffset.UtcNow);
        });
        var attachment = Assert.Single(rows, x => x.Operation == "AttachmentIndex");
        Assert.Equal(attachmentId, attachment.AttachmentId);
        var file = Assert.Single(rows, x => x.Operation == "SharePointReindex");
        Assert.Equal("file", file.FileId);
        Assert.Equal("drive", file.DriveId);
        Assert.Null(file.AttachmentId);
        Assert.Empty(await db.UserTokenUsage.ToListAsync());
        Assert.Null(EmbeddingUsageScope.Current.UserId);
    }

    [Fact]
    public async Task RecordsUnknownUsageAndDeploymentFallbackAfterCallerCancellation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        await using var db = new SharePointIndexDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new SharePointIndexDbContext(options);
        });
        using var cancellation = new CancellationTokenSource();
        var inner = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        inner.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1 })]);
            });
        using var generator = new TrackedEmbeddingGenerator(inner, factory, "deployment");
        using var scope = EmbeddingUsageScope.Begin(new(Operation: "SharePointVectorSearch"));
        await generator.GenerateAsync(["query"], cancellationToken: cancellation.Token);
        var row = await db.EmbeddingTokenUsage.SingleAsync();
        Assert.Equal("deployment", row.EmbeddingModelId);
        Assert.Null(row.InputTokens);
        Assert.Null(row.TotalTokens);
        Assert.Null(row.UserId);
        inner.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GeneratedEmbeddings<Embedding<float>>>(new InvalidOperationException("Provider failed")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => generator.GenerateAsync(["query"]));
        Assert.Equal(1, await db.EmbeddingTokenUsage.CountAsync());
    }

    [Fact]
    public async Task ConcurrentScopesKeepTheirOwnAttribution()
    {
        async Task<EmbeddingUsageContext> Capture(Guid userId)
        {
            using var scope = EmbeddingUsageScope.Begin(new(UserId: userId));
            await Task.Yield();
            using var nested = EmbeddingUsageScope.Begin(new(Operation: "AttachmentVectorSearch"));
            await Task.Yield();
            return EmbeddingUsageScope.Current;
        }
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var results = await Task.WhenAll(Capture(first), Capture(second));
        Assert.Equal(first, results[0].UserId);
        Assert.Equal(second, results[1].UserId);
        Assert.Null(EmbeddingUsageScope.Current.UserId);
    }
}

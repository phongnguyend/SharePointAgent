using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.AI;
using SharePointAgent.Api;
using SharePointAgent.Infrastructure;
using SharePointAgent.Infrastructure.KnowledgeGraph;
using SharePointAgent.Persistence;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class GraphChatUsageTests
{
    [Fact]
    public async Task GraphRequestsPersistAndCountWithoutInventingConversationTurns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection)
            .ReplaceService<IModelCustomizer, SortableDates>().Options;
        await using var db = new SharePointIndexDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var recorder = new GraphChatUsageRecorder(new Factory(options));
        using var scope = new GraphModelUsageScope("graph.extraction_playground");
        await recorder.RecordAsync("extraction-model", "graph.extraction", new ChatRequestUsage(0, [], [], [],
            new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20, TotalTokenCount = 120 }), default);
        await recorder.RecordAsync("extraction-model", "graph.extraction", new ChatRequestUsage(1, [], [], [], null), default);

        var rows = await db.ChatTokenUsage.OrderBy(x => x.Sequence).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.NotEqual(Guid.Empty, row.Id);
            Assert.Null(row.ConversationId);
            Assert.Null(row.QuestionId);
            Assert.Null(row.UserId);
            Assert.Equal("graph.extraction_playground", row.Operation);
        });
        Assert.Equal(120, rows[0].TotalTokens);
        Assert.Null(rows[1].TotalTokens);
        Assert.Equal(2, scope.Requests.Count);
        Assert.Equal("extraction-model", scope.Requests[0].ModelId);
        Assert.Null(scope.Requests[1].TotalTokens);

        var report = Assert.IsAssignableFrom<IValueHttpResult>(await TokenUsageEndpoints.ReadAsync(db, default));
        var json = JsonSerializer.SerializeToElement(report.Value);
        Assert.Equal(0, json.GetProperty("Summary").GetProperty("Turns").GetInt32());
        Assert.Equal(2, json.GetProperty("Summary").GetProperty("Requests").GetInt32());
        Assert.Equal(120, json.GetProperty("Summary").GetProperty("TotalTokens").GetInt64());
    }

    private sealed class Factory(DbContextOptions<SharePointIndexDbContext> options) : IDbContextFactory<SharePointIndexDbContext>
    {
        public SharePointIndexDbContext CreateDbContext() => new(options);
    }

    // SQL Server supports DateTimeOffset sorting; SQLite needs a test-only conversion.
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

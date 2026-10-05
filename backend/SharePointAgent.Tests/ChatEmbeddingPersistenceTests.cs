using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using SharePointAgent.Domain;
using SharePointAgent.Persistence;
using SharePointAgent.Persistence.Repositories;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ChatEmbeddingPersistenceTests
{
    [Fact]
    public async Task ResponsesAndBranchesKeepEmbeddingCountsSeparateFromChatTotals()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        var agentId = Guid.NewGuid();
        await using (var db = new SharePointIndexDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.AgentDefinitions.Add(new AgentDefinitionEntity { Id = agentId, Name = "Test" });
            await db.SaveChangesAsync();
        }
        var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new SharePointIndexDbContext(options));
        var repository = new ChatRepository(factory);
        var conversation = await repository.CreateConversationAsync("Test", null, agentId, null, default);
        await repository.AppendMessageAsync(conversation.Id, ChatMessageRole.User, "question", [], null, null, [], null, default);
        var first = await repository.AppendMessageAsync(conversation.Id, ChatMessageRole.Assistant, "answer", [], new(10, 5, 15, 7), "model", [], null, default);
        await repository.AppendMessageAsync(conversation.Id, ChatMessageRole.Assistant, "more", [], new(20, 10, 30, 11), "model", [], null, default);
        Assert.Equal(15, first.TotalTokenCount);
        Assert.Equal(7, first.EmbeddingTokenCount);
        var loaded = await repository.GetConversationAsync(conversation.Id, default);
        Assert.Equal(45, loaded!.TotalTokenCount);
        Assert.Equal(18, loaded.EmbeddingTokenCount);
        var messages = await repository.ListMessagesAsync(conversation.Id, default);
        Assert.Equal(new long[] { 0, 7, 11 }, messages.Select(x => x.EmbeddingTokenCount));
        var branch = await repository.BranchConversationAsync(conversation.Id, first.Id, default);
        Assert.Equal(15, branch!.TotalTokenCount);
        Assert.Equal(7, branch.EmbeddingTokenCount);
        Assert.Equal(7, (await repository.ListMessagesAsync(branch.Id, default)).Last().EmbeddingTokenCount);
    }
}

using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using SharePointAgent.Domain;
using SharePointAgent.Persistence;
using SharePointAgent.Persistence.Repositories;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ChatTracePersistenceTests
{
    [Fact]
    public async Task QuestionAnswerAndBranchPreserveOriginalTrace()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        var agent = new AgentDefinitionEntity { Name = "Test" };
        await using (var db = new SharePointIndexDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.AgentDefinitions.Add(agent);
            await db.SaveChangesAsync();
        }
        var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new SharePointIndexDbContext(options));
        var repository = new ChatRepository(factory);
        var conversation = await repository.CreateConversationAsync("Test", null, agent.Id, null, default);

        string traceId;
        ChatMessageRecord answer;
        using (var activity = new Activity("chat-request").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            traceId = activity.TraceId.ToString();
            var question = await repository.AppendMessageAsync(conversation.Id, ChatMessageRole.User,
                "question", [], null, null, [], null, default);
            Assert.Equal(traceId, question.TraceId);
            using var child = new Activity("save-answer").Start();
            answer = await repository.AppendMessageAsync(conversation.Id, ChatMessageRole.Assistant,
                "answer", [], null, null, [], traceId, default);
            Assert.Equal(traceId, answer.TraceId);
        }
        var loaded = await repository.ListMessagesAsync(conversation.Id, default);
        Assert.All(loaded, message => Assert.Equal(traceId, message.TraceId));

        using (var branchActivity = new Activity("branch-request").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            Assert.NotEqual(traceId, branchActivity.TraceId.ToString());
            var branch = await repository.BranchConversationAsync(conversation.Id, answer.Id, default);
            Assert.NotNull(branch);
            Assert.All(await repository.ListMessagesAsync(branch.Id, default),
                message => Assert.Equal(traceId, message.TraceId));
        }

        var previous = Activity.Current;
        try
        {
            Activity.Current = null;
            var untraced = await repository.AppendMessageAsync(conversation.Id, ChatMessageRole.User,
                "outside a request", [], null, null, [], null, default);
            Assert.Null(untraced.TraceId);
        }
        finally
        {
            Activity.Current = previous;
        }
    }
}

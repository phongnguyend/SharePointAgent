using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Persistence;
using SharePointAgent.Persistence.Repositories;
using Xunit;

namespace SharePointAgent.Tests;

/// <summary>
/// A workspace is the unit a sandbox is bound to. These cover what that changes: conversations in one
/// reach the same files, conversations outside one are unaffected, and moving between the two swaps
/// which binding is in force.
/// </summary>
public sealed class ChatWorkspaceTests : IAsyncLifetime
{
    private const string Endpoint = "https://foundry.example/invocations";

    private SqliteConnection _connection = null!;

    private DbContextOptions<SharePointIndexDbContext> _options = null!;

    private IDbContextFactory<SharePointIndexDbContext> _factory = null!;

    private Guid _agentId;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());

        // EnsureCreated writes the SET NULL foreign key, but SQLite ignores every foreign key unless
        // the connection asks for them, and this test opens the connection itself.
        await using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON";
            await pragma.ExecuteNonQueryAsync();
        }

        _options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(_connection).Options;
        _agentId = Guid.NewGuid();
        await using (var db = new SharePointIndexDbContext(_options))
        {
            await db.Database.EnsureCreatedAsync();
            db.AgentDefinitions.Add(new AgentDefinitionEntity { Id = _agentId, Name = "Test" });
            await db.SaveChangesAsync();
        }

        _factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        _factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new SharePointIndexDbContext(_options));
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task ConversationsInOneWorkspaceShareASandbox()
    {
        var workspaces = new ChatWorkspaceRepository(_factory);
        var chats = new ChatRepository(_factory);
        var sessions = new FoundrySessionRepository(_factory);

        var workspace = await workspaces.CreateAsync("Quarterly report", null, default);
        var first = await chats.CreateConversationAsync("First", null, _agentId, workspace.Id, default);
        var second = await chats.CreateConversationAsync("Second", null, _agentId, workspace.Id, default);

        Assert.Null(await sessions.GetAsync(first.Id, Endpoint, default));
        await sessions.SaveAsync(first.Id, Endpoint, "sandbox-1", default);

        // The second conversation was never invoked, so only the workspace can be supplying this.
        Assert.Equal("sandbox-1", await sessions.GetAsync(second.Id, Endpoint, default));

        // Saving the same ID again is the ordinary case: every later turn re-reports it.
        await sessions.SaveAsync(second.Id, Endpoint, "sandbox-1", default);
        Assert.Equal("sandbox-1", await sessions.GetAsync(first.Id, Endpoint, default));
    }

    [Fact]
    public async Task AConversationOutsideAWorkspaceKeepsItsOwnSandbox()
    {
        var workspaces = new ChatWorkspaceRepository(_factory);
        var chats = new ChatRepository(_factory);
        var sessions = new FoundrySessionRepository(_factory);

        var workspace = await workspaces.CreateAsync("Quarterly report", null, default);
        var grouped = await chats.CreateConversationAsync("Grouped", null, _agentId, workspace.Id, default);
        var alone = await chats.CreateConversationAsync("Alone", null, _agentId, null, default);

        await sessions.SaveAsync(grouped.Id, Endpoint, "sandbox-workspace", default);
        await sessions.SaveAsync(alone.Id, Endpoint, "sandbox-alone", default);

        Assert.Equal("sandbox-workspace", await sessions.GetAsync(grouped.Id, Endpoint, default));
        Assert.Equal("sandbox-alone", await sessions.GetAsync(alone.Id, Endpoint, default));

        // Another endpoint is a different sandbox entirely, for a workspace as for a conversation.
        Assert.Null(await sessions.GetAsync(grouped.Id, "https://elsewhere.example/invocations", default));
    }

    [Fact]
    public async Task MembershipIsFixedWhenTheConversationIsCreated()
    {
        var workspaces = new ChatWorkspaceRepository(_factory);
        var chats = new ChatRepository(_factory);
        var sessions = new FoundrySessionRepository(_factory);

        var workspace = await workspaces.CreateAsync("Quarterly report", null, default);
        var inside = await chats.CreateConversationAsync("Inside", null, _agentId, workspace.Id, default);
        var outside = await chats.CreateConversationAsync("Outside", null, _agentId, null, default);
        await sessions.SaveAsync(inside.Id, Endpoint, "sandbox-workspace", default);
        await sessions.SaveAsync(outside.Id, Endpoint, "sandbox-own", default);

        Assert.Equal(workspace.Id, (await chats.GetConversationAsync(inside.Id, default))!.WorkspaceId);
        Assert.Null((await chats.GetConversationAsync(outside.Id, default))!.WorkspaceId);

        // Appending turns is the one thing that writes to a conversation row afterwards; it must not
        // disturb which row the sandbox binding is read from.
        await chats.AppendMessageAsync(inside.Id, ChatMessageRole.User, "question", [], null, null, [], default);
        await chats.AppendMessageAsync(outside.Id, ChatMessageRole.User, "question", [], null, null, [], default);

        Assert.Equal(workspace.Id, (await chats.GetConversationAsync(inside.Id, default))!.WorkspaceId);
        Assert.Equal("sandbox-workspace", await sessions.GetAsync(inside.Id, Endpoint, default));
        Assert.Null((await chats.GetConversationAsync(outside.Id, default))!.WorkspaceId);
        Assert.Equal("sandbox-own", await sessions.GetAsync(outside.Id, Endpoint, default));
    }

    [Fact]
    public async Task ASecondSandboxForTheSameWorkspaceIsRejected()
    {
        var workspaces = new ChatWorkspaceRepository(_factory);
        var chats = new ChatRepository(_factory);
        var sessions = new FoundrySessionRepository(_factory);

        var workspace = await workspaces.CreateAsync("Quarterly report", null, default);
        var first = await chats.CreateConversationAsync("First", null, _agentId, workspace.Id, default);
        var second = await chats.CreateConversationAsync("Second", null, _agentId, workspace.Id, default);
        await sessions.SaveAsync(first.Id, Endpoint, "sandbox-1", default);

        // Two turns racing from an unbound workspace must not silently split its files in two.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sessions.SaveAsync(second.Id, Endpoint, "sandbox-2", default));
        Assert.Equal("sandbox-1", await sessions.GetAsync(second.Id, Endpoint, default));
    }

    [Fact]
    public async Task ABranchInheritsTheWorkspaceAndItsFiles()
    {
        var workspaces = new ChatWorkspaceRepository(_factory);
        var chats = new ChatRepository(_factory);
        var sessions = new FoundrySessionRepository(_factory);

        var workspace = await workspaces.CreateAsync("Quarterly report", null, default);
        var conversation = await chats.CreateConversationAsync("Source", null, _agentId, workspace.Id, default);
        await sessions.SaveAsync(conversation.Id, Endpoint, "sandbox-1", default);
        var question = await chats.AppendMessageAsync(
            conversation.Id, ChatMessageRole.User, "question", [], null, null, [], default);

        var branch = await chats.BranchConversationAsync(conversation.Id, question.Id, default);

        Assert.Equal(workspace.Id, branch!.WorkspaceId);
        Assert.Equal("sandbox-1", await sessions.GetAsync(branch.Id, Endpoint, default));
    }

    [Fact]
    public async Task DeletingAWorkspaceReleasesItsConversationsInsteadOfRemovingThem()
    {
        var workspaces = new ChatWorkspaceRepository(_factory);
        var chats = new ChatRepository(_factory);
        var sessions = new FoundrySessionRepository(_factory);

        var workspace = await workspaces.CreateAsync("Quarterly report", null, default);
        var conversation = await chats.CreateConversationAsync("Kept", null, _agentId, workspace.Id, default);
        await chats.AppendMessageAsync(conversation.Id, ChatMessageRole.User, "question", [], null, null, [], default);
        await sessions.SaveAsync(conversation.Id, Endpoint, "sandbox-1", default);

        Assert.Equal(1, (await workspaces.GetAsync(workspace.Id, default))!.ConversationCount);
        Assert.True(await workspaces.DeleteAsync(workspace.Id, default));
        Assert.False(await workspaces.DeleteAsync(workspace.Id, default));

        var released = await chats.GetConversationAsync(conversation.Id, default);
        Assert.NotNull(released);
        Assert.Null(released!.WorkspaceId);
        Assert.Single(await chats.ListMessagesAsync(conversation.Id, default));

        // With the shared binding gone the conversation starts a sandbox of its own on the next turn.
        Assert.Null(await sessions.GetAsync(conversation.Id, Endpoint, default));
    }

    [Fact]
    public async Task AWorkspaceIsReachableOnlyByItsOwnerAndRisesWhenItIsUsed()
    {
        var workspaces = new ChatWorkspaceRepository(_factory);
        var chats = new ChatRepository(_factory);
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        await using (var db = new SharePointIndexDbContext(_options))
        {
            db.Users.Add(new ApplicationUser { Id = mine, UserName = "mine@example.com" });
            db.Users.Add(new ApplicationUser { Id = theirs, UserName = "theirs@example.com" });
            await db.SaveChangesAsync();
        }

        var workspace = await workspaces.CreateAsync("Quarterly report", null, default, mine);

        // The owner filter is what stops one user attaching a conversation to another user's sandbox.
        Assert.Null(await workspaces.GetAsync(workspace.Id, default, theirs));
        Assert.NotNull(await workspaces.GetAsync(workspace.Id, default, mine));
        Assert.NotNull(await workspaces.GetAsync(workspace.Id, default));

        // The sidebar orders by UpdatedAtUtc, so a turn has to move the workspace as well as the chat.
        var conversation = await chats.CreateConversationAsync("Chat", null, _agentId, workspace.Id, default);
        await chats.AppendMessageAsync(conversation.Id, ChatMessageRole.User, "question", [], null, null, [], default);
        var used = await workspaces.GetAsync(workspace.Id, default);
        Assert.True(used!.UpdatedAtUtc > workspace.UpdatedAtUtc);
        Assert.Equal(1, used.ConversationCount);

        Assert.Equal("Renamed", (await workspaces.UpdateAsync(workspace.Id, "Renamed", null, default))!.Name);
        Assert.Null(await workspaces.UpdateAsync(Guid.NewGuid(), "Nothing", null, default));
    }

    [Fact]
    public async Task TheBindingDescribesWhichRowHoldsItAndHowManyShareIt()
    {
        var workspaces = new ChatWorkspaceRepository(_factory);
        var chats = new ChatRepository(_factory);
        var sessions = new FoundrySessionRepository(_factory);

        var workspace = await workspaces.CreateAsync("Quarterly report", null, default);
        var grouped = await chats.CreateConversationAsync("Grouped", null, _agentId, workspace.Id, default);
        await chats.CreateConversationAsync("Alongside", null, _agentId, workspace.Id, default);
        var alone = await chats.CreateConversationAsync("Alone", null, _agentId, null, default);

        // Before the first turn there is a scope but no session, which is what "not bound yet" looks like.
        var unbound = await sessions.DescribeAsync(grouped.Id, default);
        Assert.Equal(workspace.Id, unbound!.WorkspaceId);
        Assert.Null(unbound.SessionId);

        await sessions.SaveAsync(grouped.Id, Endpoint, "sandbox-1", default);
        await sessions.SaveAsync(alone.Id, Endpoint, "sandbox-alone", default);

        var shared = await sessions.DescribeAsync(grouped.Id, default);
        Assert.Equal("Quarterly report", shared!.WorkspaceName);
        Assert.Equal("sandbox-1", shared.SessionId);
        Assert.Equal(Endpoint, shared.Endpoint);
        Assert.Equal(2, shared.ConversationCount);

        var own = await sessions.DescribeAsync(alone.Id, default);
        Assert.Null(own!.WorkspaceId);
        Assert.Equal("sandbox-alone", own.SessionId);
        Assert.Equal(1, own.ConversationCount);

        Assert.Null(await sessions.DescribeAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task RulesAreStoredOnTheWorkspaceAndClearedByAnEmptyUpdate()
    {
        var workspaces = new ChatWorkspaceRepository(_factory);
        var created = await workspaces.CreateAsync("Quarterly report", "  Answer in British English.  ", default);

        // Stored trimmed, so the composed prompt does not carry the textarea's stray whitespace.
        Assert.Equal("Answer in British English.", created.Instructions);
        Assert.Equal("Answer in British English.", (await workspaces.GetAsync(created.Id, default))!.Instructions);

        var updated = await workspaces.UpdateAsync(created.Id, "Quarterly report", "Cite the file name.", default);
        Assert.Equal("Cite the file name.", updated!.Instructions);

        // Blank is one state, not two: it reads back as no rules rather than as an empty rule.
        Assert.Null((await workspaces.UpdateAsync(created.Id, "Quarterly report", "   ", default))!.Instructions);
        Assert.Null((await workspaces.CreateAsync("Bare", null, default)).Instructions);
    }

    [Fact]
    public async Task ATurnRunsOnTheAgentInstructionsPlusTheWorkspaceRules()
    {
        var workspaces = new ChatWorkspaceRepository(_factory);
        var chats = new ChatRepository(_factory);
        var agents = new AgentRepository(_factory, Options.Create(new OpenAiOptions { ChatDeployment = "model" }));
        await agents.CreateAsync(AgentDefaults.Name, "model", "Agent instructions.", default);
        var agent = await agents.GetByNameAsync(AgentDefaults.Name, default);

        var workspace = await workspaces.CreateAsync("Quarterly report", "Answer in British English.", default);
        var inside = await chats.CreateConversationAsync("Inside", null, agent!.Id, workspace.Id, default);
        var outside = await chats.CreateConversationAsync("Outside", null, agent.Id, null, default);
        var loader = new ChatAgentContextLoader(chats, agents, workspaces);

        var insideQuestion = await chats.AppendMessageAsync(
            inside.Id, ChatMessageRole.User, "question", [], null, null, [], default);
        var composed = (await loader.LoadAsync(new(inside.Id, insideQuestion.Id), default)).Instructions;
        Assert.StartsWith("Agent instructions.", composed);
        Assert.Contains("# Workspace rules", composed);
        Assert.Contains("Quarterly report", composed);
        Assert.Contains("Answer in British English.", composed);

        // A conversation outside a workspace is given exactly what the agent says, with nothing added.
        var outsideQuestion = await chats.AppendMessageAsync(
            outside.Id, ChatMessageRole.User, "question", [], null, null, [], default);
        var plain = await loader.LoadAsync(new(outside.Id, outsideQuestion.Id), default);
        Assert.Equal("Agent instructions.", plain.Instructions);

        // Editing the rules reaches conversations that already exist, on their next turn.
        await workspaces.UpdateAsync(workspace.Id, "Quarterly report", "Cite the file name.", default);
        var next = await chats.AppendMessageAsync(
            inside.Id, ChatMessageRole.User, "another question", [], null, null, [], default);
        Assert.Contains("Cite the file name.", (await loader.LoadAsync(new(inside.Id, next.Id), default)).Instructions);

        // Clearing them puts the conversation back on the agent's instructions alone.
        await workspaces.UpdateAsync(workspace.Id, "Quarterly report", null, default);
        var last = await chats.AppendMessageAsync(
            inside.Id, ChatMessageRole.User, "a third question", [], null, null, [], default);
        Assert.Equal("Agent instructions.", (await loader.LoadAsync(new(inside.Id, last.Id), default)).Instructions);
    }

    [Fact]
    public void WorkspaceMigrationMatchesTheRuntimeModel()
    {
        using var db = new SharePointIndexDbContext(new DbContextOptionsBuilder<SharePointIndexDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var script = db.GetService<IMigrator>().GenerateScript();
        Assert.Contains("CREATE TABLE [ChatWorkspaces]", script);
        Assert.Contains("[Instructions] nvarchar(max) NULL", script);
        Assert.Contains("[WorkspaceId] uniqueidentifier NULL", script);
        Assert.Contains("ON DELETE SET NULL", script);
    }
}

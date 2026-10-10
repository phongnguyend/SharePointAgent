using System.ComponentModel.DataAnnotations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ChatAgentRegistrationTests
{
    [Theory]
    [InlineData("Local")]
    [InlineData("Foundry")]
    public async Task RegistersBothAgentsBehindTheRouterWhenFoundryIsConfigured(string defaultMode)
    {
        await using var provider = BuildServices(defaultMode, "https://example.com/invocations?api-version=v1");

        Assert.IsType<RoutingChatAgentExecutor>(provider.GetRequiredService<IChatAgentExecutor>());
        Assert.IsType<RoutingAgentFileBrowser>(provider.GetRequiredService<IAgentFileBrowser>());
        Assert.IsType<ChatAgentService>(provider.GetRequiredKeyedService<IChatAgentExecutor>(ChatAgentExecutionMode.Local));
        Assert.IsType<FoundryChatAgentExecutor>(provider.GetRequiredKeyedService<IChatAgentExecutor>(ChatAgentExecutionMode.Foundry));
        Assert.IsType<FoundryAgentFileBrowser>(provider.GetRequiredKeyedService<IAgentFileBrowser>(ChatAgentExecutionMode.Foundry));
        Assert.Equal([ChatAgentExecutionMode.Local, ChatAgentExecutionMode.Foundry], provider.GetRequiredService<IChatAgentModeSelector>().AvailableModes);
    }

    [Fact]
    public async Task WithoutAFoundryEndpointOnlyTheApisOwnAgentIsOffered()
    {
        await using var provider = BuildServices("Local", foundryEndpoint: null);

        Assert.Null(provider.GetKeyedService<IChatAgentExecutor>(ChatAgentExecutionMode.Foundry));
        Assert.Equal([ChatAgentExecutionMode.Local], provider.GetRequiredService<IChatAgentModeSelector>().AvailableModes);
    }

    [Theory]
    [InlineData("https://example.com/invocations", false, true)]
    [InlineData("http://localhost:8088/invocations", true, true)]
    [InlineData("https://example.com/invocations", true, false)]
    [InlineData("http://example.com/invocations", false, false)]
    [InlineData("https://example.com/invocations?agent_session_id=shared", false, false)]
    [InlineData("", false, false)]
    public void ValidatesRemoteEndpointAndDevelopmentAuthentication(string endpoint, bool unauthenticated, bool valid)
    {
        var options = new ChatAgentHostingOptions
        {
            Mode = ChatAgentExecutionMode.Foundry,
            Foundry = new() { Endpoint = endpoint, AllowUnauthenticatedLocalhost = unauthenticated },
        };
        Assert.Equal(valid, Validator.TryValidateObject(options, new(options), [], validateAllProperties: true));
    }

    [Fact]
    public void AFoundryEndpointIsValidatedEvenWhenLocalIsTheDefault()
    {
        var options = new ChatAgentHostingOptions { Foundry = new() { Endpoint = "http://example.com/invocations" } };

        Assert.False(Validator.TryValidateObject(options, new(options), [], validateAllProperties: true));
    }

    [Fact]
    public async Task AWorkspacesAgentModeIsSharedByItsConversationsAndFallsBackWhenFoundryIsRemoved()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        var workspaceId = Guid.NewGuid();
        var inWorkspace = Guid.NewGuid();
        var alone = Guid.NewGuid();
        await using (var db = new SharePointIndexDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.ChatWorkspaces.Add(new ChatWorkspaceEntity { Id = workspaceId, Name = "Team" });
            db.ChatConversations.Add(new ChatConversationEntity { Id = inWorkspace, Title = "a", WorkspaceId = workspaceId });
            db.ChatConversations.Add(new ChatConversationEntity { Id = alone, Title = "b" });
            await db.SaveChangesAsync();
        }
        var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new SharePointIndexDbContext(options));
        ChatAgentModeSelector Selector(string? endpoint) => new(factory,
            Options.Create(new ChatAgentHostingOptions { Foundry = new() { Endpoint = endpoint ?? "" } }), NullLogger<ChatAgentModeSelector>.Instance);
        var selector = Selector("https://example.com/invocations");

        Assert.Equal(new ChatAgentModeChoice(ChatAgentExecutionMode.Local, IsDefault: true), await selector.ResolveAsync(inWorkspace, default));
        Assert.True(await selector.SetModeAsync(inWorkspace, ChatAgentExecutionMode.Foundry, default));

        Assert.Equal(new ChatAgentModeChoice(ChatAgentExecutionMode.Foundry, IsDefault: false), await selector.ResolveAsync(inWorkspace, default));
        Assert.Equal(new ChatAgentModeChoice(ChatAgentExecutionMode.Local, IsDefault: true), await selector.ResolveAsync(alone, default));
        Assert.Equal(new ChatAgentModeChoice(ChatAgentExecutionMode.Local, IsDefault: true), await Selector(null).ResolveAsync(inWorkspace, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Selector(null).SetModeAsync(alone, ChatAgentExecutionMode.Foundry, default));
        Assert.False(await selector.SetModeAsync(Guid.NewGuid(), null, default));
    }

    [Fact]
    public async Task EachTurnAndFileRequestGoesToTheAgentTheConversationChose()
    {
        var conversationId = Guid.NewGuid();
        var modes = Substitute.For<IChatAgentModeSelector>();
        modes.ResolveAsync(conversationId, Arg.Any<CancellationToken>()).Returns(new ChatAgentModeChoice(ChatAgentExecutionMode.Foundry, false));
        var local = Substitute.For<IChatAgentExecutor>();
        var foundry = Substitute.For<IChatAgentExecutor>();
        var localFiles = Substitute.For<IAgentFileBrowser>();
        var foundryFiles = Substitute.For<IAgentFileBrowser>();
        var services = new ServiceCollection()
            .AddKeyedSingleton(ChatAgentExecutionMode.Local, local)
            .AddKeyedSingleton(ChatAgentExecutionMode.Foundry, foundry)
            .AddKeyedSingleton(ChatAgentExecutionMode.Local, localFiles)
            .AddKeyedSingleton(ChatAgentExecutionMode.Foundry, foundryFiles)
            .BuildServiceProvider();
        var request = new ChatAgentRequest(conversationId, Guid.NewGuid());

        await new RoutingChatAgentExecutor(modes, services).RunStreamingAsync(request, (_, _) => ValueTask.CompletedTask, (_, _) => ValueTask.CompletedTask, default);
        await new RoutingAgentFileBrowser(modes, services).ListAsync(conversationId, ".", recursive: false, default);

        await foundry.ReceivedWithAnyArgs(1).RunStreamingAsync(default!, default!, default!, default);
        await local.DidNotReceiveWithAnyArgs().RunStreamingAsync(default!, default!, default!, default);
        await foundryFiles.Received(1).ListAsync(conversationId, ".", false, Arg.Any<CancellationToken>());
        await localFiles.DidNotReceiveWithAnyArgs().ListAsync(default, default, default, default);
    }

    private static ServiceProvider BuildServices(string defaultMode, string? foundryEndpoint)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ChatAgent:Mode"] = defaultMode,
            ["ChatAgent:Foundry:Endpoint"] = foundryEndpoint,
            ["SqlServer:ConnectionString"] = "Server=unused;Database=unused;Integrated Security=true",
            ["SqlServer:AutoMigrate"] = "false",
            ["AzureOpenAI:Endpoint"] = "https://example.openai.azure.com",
            ["AzureOpenAI:ApiKey"] = "test",
            ["AzureSearch:Endpoint"] = "https://example.search.windows.net",
            ["AzureSearch:ApiKey"] = "test",
            ["SharePoint:TenantId"] = Guid.NewGuid().ToString(),
            ["SharePoint:ClientId"] = Guid.NewGuid().ToString(),
            ["SharePoint:ClientSecret"] = "test",
            ["SharePoint:SiteHostname"] = "example.sharepoint.com",
            ["SharePoint:SitePath"] = "/sites/test",
            ["SharePoint:DocumentLibraryName"] = "Documents",
            ["SharePoint:NotificationUrl"] = "https://example.com/webhook",
            ["SharePoint:ClientState"] = "test-client-state",
            ["Uploads:ConnectionString"] = "UseDevelopmentStorage=true",
            ["MarkItDown:Endpoint"] = "http://localhost:8000",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWebhookServices(configuration);
        services.AddSearchQueryServices(configuration);
        services.AddAttachmentFileServices(configuration);
        services.AddChatServices(configuration);
        return services.BuildServiceProvider();
    }
}

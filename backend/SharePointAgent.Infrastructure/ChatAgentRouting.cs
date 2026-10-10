using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure;

/// <summary>
/// Where each conversation's agent runs, stored on its workspace (so all of the workspace's conversations move
/// together) or on the conversation outside one, defaulting to <see cref="ChatAgentHostingOptions.Mode"/>.
/// A chosen mode that is no longer configured falls back to the default rather than failing every turn.
/// </summary>
public sealed class ChatAgentModeSelector(
    IDbContextFactory<SharePointIndexDbContext> contextFactory,
    IOptions<ChatAgentHostingOptions> options,
    ILogger<ChatAgentModeSelector> logger) : IChatAgentModeSelector
{
    public IReadOnlyList<ChatAgentExecutionMode> AvailableModes =>
        options.Value.IsFoundryConfigured
            ? [ChatAgentExecutionMode.Local, ChatAgentExecutionMode.Foundry]
            : [ChatAgentExecutionMode.Local];

    public async Task<ChatAgentModeChoice> ResolveAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var chosen = await context.ChatConversations.Where(conversation => conversation.Id == conversationId)
            .Select(conversation => conversation.Workspace == null ? conversation.AgentMode : conversation.Workspace.AgentMode)
            .SingleOrDefaultAsync(cancellationToken);
        var fallback = options.Value.Mode;
        if (chosen is { } mode && !AvailableModes.Contains(mode))
        {
            logger.LogWarning("Conversation {ConversationId} chose to run its agent in {Mode}, which is not configured; using {Default}.", conversationId, mode, fallback);
            return new ChatAgentModeChoice(fallback, IsDefault: true);
        }
        return chosen is { } stored ? new ChatAgentModeChoice(stored, IsDefault: false) : new ChatAgentModeChoice(fallback, IsDefault: true);
    }

    public async Task<bool> SetModeAsync(Guid conversationId, ChatAgentExecutionMode? mode, CancellationToken cancellationToken)
    {
        if (mode is { } chosen && !AvailableModes.Contains(chosen))
        {
            throw new ArgumentException($"{chosen} is not configured on this deployment. Choose one of: {string.Join(", ", AvailableModes)}.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var conversation = await context.ChatConversations.Where(row => row.Id == conversationId)
            .Select(row => new { row.WorkspaceId }).SingleOrDefaultAsync(cancellationToken);
        if (conversation is null)
        {
            return false;
        }

        // The Foundry session binding and the API's environment both stay recorded, so switching back reuses them.
        if (conversation.WorkspaceId is { } workspaceId)
        {
            await context.ChatWorkspaces.Where(row => row.Id == workspaceId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AgentMode, mode), cancellationToken);
            return true;
        }

        await context.ChatConversations.Where(row => row.Id == conversationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AgentMode, mode), cancellationToken);
        return true;
    }
}

/// <summary>
/// Runs each turn where the conversation's scope has chosen: the API's own agent, or the Foundry hosted agent.
/// Each is registered as a keyed <see cref="IChatAgentExecutor"/> under its <see cref="ChatAgentExecutionMode"/>.
/// </summary>
public sealed class RoutingChatAgentExecutor(IChatAgentModeSelector modes, IServiceProvider services) : IChatAgentExecutor
{
    public async Task<ChatTurn> RunStreamingAsync(
        ChatAgentRequest request,
        Func<string, CancellationToken, ValueTask> onText,
        Func<string, CancellationToken, ValueTask> onStatus,
        CancellationToken cancellationToken)
    {
        var choice = await modes.ResolveAsync(request.ConversationId, cancellationToken);
        var executor = services.GetRequiredKeyedService<IChatAgentExecutor>(choice.Mode);
        return await executor.RunStreamingAsync(request, onText, onStatus, cancellationToken);
    }
}

/// <summary>
/// Browses and changes the working directory of the agent the conversation's scope has chosen, so the files a
/// user sees are the ones that agent works on. Each is registered as a keyed <see cref="IAgentFileBrowser"/>.
/// </summary>
public sealed class RoutingAgentFileBrowser(IChatAgentModeSelector modes, IServiceProvider services) : IAgentFileBrowser
{
    public async Task<FileSystemListing> ListAsync(Guid conversationId, string? path, bool recursive, CancellationToken cancellationToken) =>
        await (await BrowserAsync(conversationId, cancellationToken)).ListAsync(conversationId, path, recursive, cancellationToken);

    public async Task<FileContent> ReadAsync(Guid conversationId, string path, CancellationToken cancellationToken) =>
        await (await BrowserAsync(conversationId, cancellationToken)).ReadAsync(conversationId, path, cancellationToken);

    public async Task<WorkspaceFileChangeResult> ManageAsync(Guid conversationId, WorkspaceFileChange change, CancellationToken cancellationToken) =>
        await (await BrowserAsync(conversationId, cancellationToken)).ManageAsync(conversationId, change, cancellationToken);

    private async Task<IAgentFileBrowser> BrowserAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var choice = await modes.ResolveAsync(conversationId, cancellationToken);
        return services.GetRequiredKeyedService<IAgentFileBrowser>(choice.Mode);
    }
}

using System.Net.Http.Headers;
using Azure.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure.Workspaces;

/// <summary>
/// Gives each conversation the working directory of its scope: the chat workspace it belongs to, so a
/// workspace's conversations share files as they do in Foundry, or the conversation itself. The scope is a
/// database ID, never something a user supplies, and it is the only thing that selects an environment.
/// <para>
/// Each scope chooses where its files live — this host's disk, a dynamic session, or a sandbox — and the
/// choice is stored on its row, defaulting to <see cref="AgentWorkspaceOptions.Mode"/>. The dynamic session
/// or sandbox serving a scope is recorded on the same row, apart from any Foundry session, and each mode
/// keeps its own environment, so switching back finds the files left there.
/// </para>
/// </summary>
public sealed class IsolatedAgentWorkspaceProvider(
    IHttpClientFactory httpClientFactory,
    IDbContextFactory<SharePointIndexDbContext> contextFactory,
    IOptions<AgentWorkspaceOptions> options,
    IOptions<LocalWorkingDirectoryOptions> limits,
    ILogger<SandboxHostWorkspace> logger,
    TokenCredential? sessionCredential = null,
    IWorkspaceSnapshotStore? snapshots = null,
    ISandboxProvisioner? sandboxes = null,
    ISandboxRegistry? bindings = null,
    AgentFileSystem? local = null) : IAgentWorkspaceProvider
{
    public const string HttpClientName = "AgentWorkspace";

    private static readonly TokenRequestContext SessionScope = new(["https://dynamicsessions.io/.default"]);

    /// <summary>
    /// Local wherever this host's directory is registered, each isolated mode whose settings and services are
    /// present, and always the configured default, which startup validation has already checked.
    /// </summary>
    public IReadOnlyList<AgentWorkspaceMode> AvailableModes => Enum.GetValues<AgentWorkspaceMode>().Where(IsAvailable).ToArray();

    public async Task<IAgentWorkspace> GetAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var owner = await ScopeAsync(conversationId, cancellationToken);
        var scope = owner.Scope.ToString("N");
        var settings = options.Value;
        var http = httpClientFactory.CreateClient(HttpClientName);
        var mode = ModeOf(owner);
        switch (mode)
        {
            case AgentWorkspaceMode.Local:
                return local ?? throw new InvalidOperationException("This host's working directory is not registered.");

            case AgentWorkspaceMode.DynamicSessions:
                var pool = new Uri(settings.DynamicSessions.PoolManagementEndpoint!);
                var credential = sessionCredential ?? throw new InvalidOperationException("A credential for the session pool is not registered.");

                // The session is named by its own recorded ID; the snapshot stays keyed by scope, so a new
                // session still gets the scope's files back.
                var identifier = await SessionIdAsync(owner, cancellationToken);
                return new SandboxHostWorkspace(http, new DynamicSessionEndpoint(pool, identifier, credential), snapshots, scope, settings, limits.Value, logger);

            case AgentWorkspaceMode.Sandboxes:
                // Created on the workspace's first use, resumed on later ones: one sandbox per workspace scope.
                var binding = await (sandboxes ?? throw new InvalidOperationException("No sandbox provisioner is registered."))
                    .AcquireAsync(scope, cancellationToken);
                await RecordSandboxAsync(owner, binding.SandboxId, cancellationToken);
                var endpoint = new SandboxEndpoint(binding.Endpoint, binding.ApiKey, TimeSpan.FromSeconds(settings.Sandboxes.HealthWaitSeconds));

                // A sandbox keeps its own disk across suspend and resume, so it needs no snapshots.
                return new SandboxHostWorkspace(http, endpoint, null, scope, settings, limits.Value, logger);

            default:
                throw new InvalidOperationException($"{mode} is not a workspace mode.");
        }
    }

    public async Task<IAgentWorkspace?> FindAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var owner = await ScopeAsync(conversationId, cancellationToken);
        var scope = owner.Scope.ToString("N");
        var exists = ModeOf(owner) switch
        {
            AgentWorkspaceMode.Local => local is not null,

            // A session's files outlive it only as a snapshot, which every changing turn saves.
            AgentWorkspaceMode.DynamicSessions => snapshots is not null && await snapshots.ExistsAsync(scope, cancellationToken),
            AgentWorkspaceMode.Sandboxes => options.Value.Sandboxes.UsesSharedSandbox
                || (bindings is not null && await bindings.GetAsync(scope, cancellationToken) is not null),
            _ => false
        };
        return exists ? await GetAsync(conversationId, cancellationToken) : null;
    }

    public async Task<AgentWorkspaceEnvironment?> DescribeAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var owner = await ScopeAsync(conversationId, cancellationToken);
        var mode = ModeOf(owner);
        var isDefault = owner.Mode is null || mode != owner.Mode;
        return mode switch
        {
            AgentWorkspaceMode.DynamicSessions => new AgentWorkspaceEnvironment(mode, owner.DynamicSessionId, isDefault),
            AgentWorkspaceMode.Sandboxes => new AgentWorkspaceEnvironment(mode, options.Value.Sandboxes.UsesSharedSandbox ? null : owner.SandboxId, isDefault),
            _ => new AgentWorkspaceEnvironment(mode, null, isDefault)
        };
    }

    public async Task<bool> ResetAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var owner = await ScopeAsync(conversationId, cancellationToken);
        var scope = owner.Scope.ToString("N");
        switch (ModeOf(owner))
        {
            case AgentWorkspaceMode.DynamicSessions:
                // Without its snapshot, a new session ID starts empty; the old session is stopped rather than
                // left to its cooldown.
                if (snapshots is not null)
                {
                    await snapshots.DeleteAsync(scope, cancellationToken);
                }
                await SetDynamicSessionIdAsync(owner, null, cancellationToken);
                if (owner.DynamicSessionId is { } previous)
                {
                    await StopSessionAsync(previous, cancellationToken);
                }
                return true;

            case AgentWorkspaceMode.Sandboxes when !options.Value.Sandboxes.UsesSharedSandbox:
                await (sandboxes ?? throw new InvalidOperationException("No sandbox provisioner is registered."))
                    .ReleaseAsync(scope, cancellationToken);
                await SetSandboxIdAsync(owner, null, cancellationToken);
                return true;

            default:
                // This host's directory is shared by every scope that uses it, so no conversation may wipe it.
                return false;
        }
    }

    public async Task<bool> SetModeAsync(Guid conversationId, AgentWorkspaceMode? mode, CancellationToken cancellationToken)
    {
        if (mode is { } chosen && !IsAvailable(chosen))
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

        // The environment IDs stay as they are: each belongs to its own mode, so switching back reuses it.
        if (conversation.WorkspaceId is { } workspaceId)
        {
            await context.ChatWorkspaces.Where(row => row.Id == workspaceId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.WorkspaceMode, mode), cancellationToken);
            return true;
        }

        await context.ChatConversations.Where(row => row.Id == conversationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.WorkspaceMode, mode), cancellationToken);
        return true;
    }

    /// <summary>
    /// The scope's chosen mode while it is still available, otherwise the default. A mode whose settings were
    /// later removed falls back rather than failing every turn; the choice is kept for when it returns.
    /// </summary>
    private AgentWorkspaceMode ModeOf(ScopeOwner owner)
    {
        if (owner.Mode is { } chosen && !IsAvailable(chosen))
        {
            logger.LogWarning("Workspace {Scope} chose {Mode}, which is not configured; using {Default}.", owner.Scope, chosen, options.Value.Mode);
            return options.Value.Mode;
        }
        return owner.Mode ?? options.Value.Mode;
    }

    private bool IsAvailable(AgentWorkspaceMode mode)
    {
        var settings = options.Value;
        return mode == settings.Mode || mode switch
        {
            AgentWorkspaceMode.Local => local is not null,
            AgentWorkspaceMode.DynamicSessions => settings.DynamicSessions.IsConfigured && sessionCredential is not null && snapshots is not null,
            AgentWorkspaceMode.Sandboxes => settings.Sandboxes.IsConfigured && sandboxes is not null,
            _ => false
        };
    }

    private async Task<string> SessionIdAsync(ScopeOwner owner, CancellationToken cancellationToken)
    {
        if (owner.DynamicSessionId is { } existing)
        {
            return existing;
        }

        // A create-only write, so concurrent first turns settle on one session.
        var created = Guid.NewGuid().ToString("N");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (owner.IsWorkspace)
        {
            await context.ChatWorkspaces.Where(workspace => workspace.Id == owner.Scope && workspace.DynamicSessionId == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(workspace => workspace.DynamicSessionId, created), cancellationToken);
            return await context.ChatWorkspaces.Where(workspace => workspace.Id == owner.Scope)
                .Select(workspace => workspace.DynamicSessionId).SingleOrDefaultAsync(cancellationToken) ?? created;
        }

        await context.ChatConversations.Where(conversation => conversation.Id == owner.Scope && conversation.DynamicSessionId == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(conversation => conversation.DynamicSessionId, created), cancellationToken);
        return await context.ChatConversations.Where(conversation => conversation.Id == owner.Scope)
            .Select(conversation => conversation.DynamicSessionId).SingleOrDefaultAsync(cancellationToken) ?? created;
    }

    private async Task RecordSandboxAsync(ScopeOwner owner, string? sandboxId, CancellationToken cancellationToken)
    {
        // A shared development sandbox has no ID of its own, and an unchanged one needs no write.
        if (sandboxId is null || sandboxId == owner.SandboxId)
        {
            return;
        }
        await SetSandboxIdAsync(owner, sandboxId, cancellationToken);
    }

    private async Task SetDynamicSessionIdAsync(ScopeOwner owner, string? dynamicSessionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (owner.IsWorkspace)
        {
            await context.ChatWorkspaces.Where(workspace => workspace.Id == owner.Scope)
                .ExecuteUpdateAsync(setters => setters.SetProperty(workspace => workspace.DynamicSessionId, dynamicSessionId), cancellationToken);
            return;
        }

        await context.ChatConversations.Where(conversation => conversation.Id == owner.Scope)
            .ExecuteUpdateAsync(setters => setters.SetProperty(conversation => conversation.DynamicSessionId, dynamicSessionId), cancellationToken);
    }

    private async Task SetSandboxIdAsync(ScopeOwner owner, string? sandboxId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (owner.IsWorkspace)
        {
            await context.ChatWorkspaces.Where(workspace => workspace.Id == owner.Scope)
                .ExecuteUpdateAsync(setters => setters.SetProperty(workspace => workspace.SandboxId, sandboxId), cancellationToken);
            return;
        }

        await context.ChatConversations.Where(conversation => conversation.Id == owner.Scope)
            .ExecuteUpdateAsync(setters => setters.SetProperty(conversation => conversation.SandboxId, sandboxId), cancellationToken);
    }

    private async Task StopSessionAsync(string identifier, CancellationToken cancellationToken)
    {
        if (sessionCredential is null)
        {
            return;
        }

        try
        {
            var pool = new Uri(options.Value.DynamicSessions.PoolManagementEndpoint!);
            var uri = QueryHelpers.AddQueryString(DynamicSessionEndpoint.Combine(pool, ".management/stopSession"),
                new Dictionary<string, string?> { ["api-version"] = "2025-02-02-preview", ["identifier"] = identifier });
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            var token = await sessionCredential.GetTokenAsync(SessionScope, cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Could not stop dynamic session {Identifier} ({StatusCode}); it ends after its cooldown.", identifier, (int)response.StatusCode);
            }
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Could not stop dynamic session {Identifier}; it ends after its cooldown.", identifier);
        }
    }

    private async Task<ScopeOwner> ScopeAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var owner = await context.ChatConversations
            .Where(conversation => conversation.Id == conversationId)
            .Select(conversation => conversation.Workspace == null
                ? new ScopeOwner(conversation.Id, false, conversation.WorkspaceMode, conversation.DynamicSessionId, conversation.SandboxId)
                : new ScopeOwner(conversation.Workspace.Id, true, conversation.Workspace.WorkspaceMode, conversation.Workspace.DynamicSessionId, conversation.Workspace.SandboxId))
            .SingleOrDefaultAsync(cancellationToken);

        // An unknown conversation still gets a scope of its own, as before, with nothing recorded for it.
        return owner ?? new ScopeOwner(conversationId, false, null, null, null);
    }

    /// <summary>The row that owns a scope's environment: the workspace, or the conversation outside one.</summary>
    private sealed record ScopeOwner(Guid Scope, bool IsWorkspace, AgentWorkspaceMode? Mode, string? DynamicSessionId, string? SandboxId);
}

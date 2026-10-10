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
/// Gives each conversation the isolated working directory of its scope: the chat workspace it belongs to,
/// so a workspace's conversations share files as they do in Foundry, or the conversation itself. The scope
/// is a database ID, never something a user supplies, and it is the only thing that selects an environment.
/// The dynamic session or sandbox serving a scope is recorded on its row, apart from any Foundry session,
/// so the API's own agent has its environment tracked whether or not Foundry is involved.
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
    ISandboxRegistry? bindings = null) : IAgentWorkspaceProvider
{
    public const string HttpClientName = "AgentWorkspace";

    private static readonly TokenRequestContext SessionScope = new(["https://dynamicsessions.io/.default"]);

    public async Task<IAgentWorkspace> GetAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var owner = await ScopeAsync(conversationId, cancellationToken);
        var scope = owner.Scope.ToString("N");
        var settings = options.Value;
        var http = httpClientFactory.CreateClient(HttpClientName);
        switch (settings.Mode)
        {
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
                throw new InvalidOperationException($"{settings.Mode} is not an isolated workspace mode.");
        }
    }

    public async Task<IAgentWorkspace?> FindAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var scope = (await ScopeAsync(conversationId, cancellationToken)).Scope.ToString("N");
        var exists = options.Value.Mode switch
        {
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
        var mode = options.Value.Mode;
        return mode switch
        {
            AgentWorkspaceMode.DynamicSessions => new AgentWorkspaceEnvironment(mode, owner.DynamicSessionId),
            AgentWorkspaceMode.Sandboxes => new AgentWorkspaceEnvironment(mode, options.Value.Sandboxes.UsesSharedSandbox ? null : owner.SandboxId),
            _ => null
        };
    }

    public async Task<bool> ResetAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var owner = await ScopeAsync(conversationId, cancellationToken);
        var scope = owner.Scope.ToString("N");
        switch (options.Value.Mode)
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
                return false;
        }
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
                ? new ScopeOwner(conversation.Id, false, conversation.DynamicSessionId, conversation.SandboxId)
                : new ScopeOwner(conversation.Workspace.Id, true, conversation.Workspace.DynamicSessionId, conversation.Workspace.SandboxId))
            .SingleOrDefaultAsync(cancellationToken);

        // An unknown conversation still gets a scope of its own, as before, with nothing recorded for it.
        return owner ?? new ScopeOwner(conversationId, false, null, null);
    }

    /// <summary>The row that owns a scope's environment: the workspace, or the conversation outside one.</summary>
    private sealed record ScopeOwner(Guid Scope, bool IsWorkspace, string? DynamicSessionId, string? SandboxId);
}

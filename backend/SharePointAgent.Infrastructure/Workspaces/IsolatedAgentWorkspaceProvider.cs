using Azure.Core;
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
/// </summary>
public sealed class IsolatedAgentWorkspaceProvider(
    IHttpClientFactory httpClientFactory,
    IDbContextFactory<SharePointIndexDbContext> contextFactory,
    IOptions<AgentWorkspaceOptions> options,
    IOptions<LocalWorkingDirectoryOptions> limits,
    ILogger<SandboxHostWorkspace> logger,
    TokenCredential? sessionCredential = null,
    IWorkspaceSnapshotStore? snapshots = null,
    ISandboxRegistry? sandboxes = null) : IAgentWorkspaceProvider
{
    public const string HttpClientName = "AgentWorkspace";

    public async Task<IAgentWorkspace> GetAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var scope = (await ScopeAsync(conversationId, cancellationToken)).ToString("N");
        var settings = options.Value;
        var http = httpClientFactory.CreateClient(HttpClientName);
        switch (settings.Mode)
        {
            case AgentWorkspaceMode.DynamicSessions:
                var pool = new Uri(settings.DynamicSessions.PoolManagementEndpoint!);
                var credential = sessionCredential ?? throw new InvalidOperationException("A credential for the session pool is not registered.");
                return new SandboxHostWorkspace(http, new DynamicSessionEndpoint(pool, scope, credential), snapshots, scope, settings, limits.Value, logger);

            case AgentWorkspaceMode.Sandboxes:
                var binding = await (sandboxes ?? throw new InvalidOperationException("No sandbox registry is registered.")).GetAsync(scope, cancellationToken)
                    ?? throw new AgentWorkspaceUnavailableException(
                        "No sandbox is set up for this conversation yet. Ask an administrator to provision one, then try again.");
                var endpoint = new SandboxEndpoint(binding.Endpoint, binding.ApiKey, TimeSpan.FromSeconds(settings.Sandboxes.HealthWaitSeconds));

                // A sandbox keeps its own disk across suspend and resume, so it needs no snapshots.
                return new SandboxHostWorkspace(http, endpoint, null, scope, settings, limits.Value, logger);

            default:
                throw new InvalidOperationException($"{settings.Mode} is not an isolated workspace mode.");
        }
    }

    private async Task<Guid> ScopeAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var workspaceId = await context.ChatConversations
            .Where(conversation => conversation.Id == conversationId)
            .Select(conversation => conversation.WorkspaceId)
            .SingleOrDefaultAsync(cancellationToken);
        return workspaceId ?? conversationId;
    }
}

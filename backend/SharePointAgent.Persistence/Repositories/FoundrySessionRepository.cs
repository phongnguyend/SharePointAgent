using Microsoft.EntityFrameworkCore;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Persistence.Repositories;

/// <summary>
/// Persists sandbox affinity across API restarts; branches start with no sandbox binding. A
/// conversation in a workspace reads and writes the binding on the workspace row, so every
/// conversation in that workspace reaches the same files; one outside a workspace uses its own row.
/// </summary>
public sealed class FoundrySessionRepository(IDbContextFactory<SharePointIndexDbContext> factory) : IFoundrySessionRepository
{
    public async Task<string?> GetAsync(Guid conversationId, string endpoint, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ChatConversations.Where(c => c.Id == conversationId)
            .Select(c => c.WorkspaceId == null
                ? c.FoundryEndpoint == endpoint ? c.FoundrySessionId : null
                : c.Workspace!.FoundryEndpoint == endpoint ? c.Workspace.FoundrySessionId : null)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<FoundrySessionBinding?> DescribeAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ChatConversations.Where(c => c.Id == conversationId)
            .Select(c => c.Workspace == null
                ? new FoundrySessionBinding(null, null, c.FoundryEndpoint, c.FoundrySessionId, 1)
                : new FoundrySessionBinding(
                    c.Workspace.Id,
                    c.Workspace.Name,
                    c.Workspace.FoundryEndpoint,
                    c.Workspace.FoundrySessionId,
                    c.Workspace.Conversations.Count))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task SaveAsync(Guid conversationId, string endpoint, string sessionId, CancellationToken cancellationToken)
    {
        if (sessionId.Length > 200 || endpoint.Length > 2048)
        {
            throw new InvalidOperationException("The Foundry session binding exceeds the supported size.");
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var workspaceId = await db.ChatConversations.Where(c => c.Id == conversationId)
            .Select(c => c.WorkspaceId).SingleOrDefaultAsync(cancellationToken);
        if (workspaceId is { } workspace)
        {
            var bound = await db.ChatWorkspaces.Where(w => w.Id == workspace
                    && (w.FoundryEndpoint != endpoint || w.FoundrySessionId == null || w.FoundrySessionId == sessionId))
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.FoundryEndpoint, endpoint)
                    .SetProperty(w => w.FoundrySessionId, sessionId), cancellationToken);
            if (bound != 1)
            {
                throw new InvalidOperationException(
                    "The workspace was deleted or another conversation in it established a different Foundry session.");
            }

            return;
        }

        var count = await db.ChatConversations.Where(c => c.Id == conversationId
                && (c.FoundryEndpoint != endpoint || c.FoundrySessionId == null || c.FoundrySessionId == sessionId))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FoundryEndpoint, endpoint)
                .SetProperty(c => c.FoundrySessionId, sessionId), cancellationToken);
        if (count != 1)
        {
            throw new InvalidOperationException("The conversation was deleted or another request established a different Foundry session.");
        }
    }
}

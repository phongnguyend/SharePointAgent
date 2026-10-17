using Microsoft.EntityFrameworkCore;
using SharePointAgent.Application;

namespace SharePointAgent.Persistence.Repositories;

public sealed class WorkspaceInstanceLinks(IDbContextFactory<SharePointIndexDbContext> factory) : IWorkspaceInstanceLinks
{
    public async Task<IReadOnlySet<string>> FindLinkedAsync(string provider, string[] ids, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var conversations = provider switch
        {
            "foundry-sessions" => db.ChatConversations.Select(c => c.FoundrySessionId),
            "dynamic-sessions" => db.ChatConversations.Select(c => c.DynamicSessionId),
            "sandboxes" => db.ChatConversations.Select(c => c.SandboxId),
            _ => throw new ArgumentException("Unknown workspace provider.", nameof(provider))
        };
        var workspaces = provider switch
        {
            "foundry-sessions" => db.ChatWorkspaces.Select(w => w.FoundrySessionId),
            "dynamic-sessions" => db.ChatWorkspaces.Select(w => w.DynamicSessionId),
            _ => db.ChatWorkspaces.Select(w => w.SandboxId)
        };
        var linked = await conversations.Concat(workspaces)
            .Where(id => id != null && ids.Contains(id)).Distinct().ToListAsync(cancellationToken);
        return linked.Select(id => id!).ToHashSet(StringComparer.Ordinal);
    }
}

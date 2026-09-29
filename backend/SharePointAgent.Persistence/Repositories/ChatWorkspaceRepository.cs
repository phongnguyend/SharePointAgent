using Microsoft.EntityFrameworkCore;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Persistence.Repositories;

/// <summary>
/// Workspaces in the same SQL Server database as the conversations they group. The row also carries
/// the Foundry sandbox binding its conversations share, which <see cref="FoundrySessionRepository"/>
/// reads and writes.
/// </summary>
public sealed class ChatWorkspaceRepository(IDbContextFactory<SharePointIndexDbContext> contextFactory) : IChatWorkspaceRepository
{
    public async Task<IReadOnlyList<ChatWorkspace>> ListAsync(CancellationToken cancellationToken, Guid? createdById = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ChatWorkspaces
            .AsNoTracking()
            .Where(w => createdById == null || w.CreatedById == createdById)
            .OrderByDescending(w => w.UpdatedAtUtc)
            .Select(w => new ChatWorkspace(
                w.Id, w.Name, w.Instructions, w.CreatedAtUtc, w.UpdatedAtUtc, w.Conversations.Count))
            .ToListAsync(cancellationToken);
    }

    public async Task<ChatWorkspace?> GetAsync(Guid id, CancellationToken cancellationToken, Guid? createdById = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ChatWorkspaces
            .AsNoTracking()
            .Where(w => w.Id == id && (createdById == null || w.CreatedById == createdById))
            .Select(w => new ChatWorkspace(
                w.Id, w.Name, w.Instructions, w.CreatedAtUtc, w.UpdatedAtUtc, w.Conversations.Count))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ChatWorkspace> CreateAsync(
        string name,
        string? instructions,
        CancellationToken cancellationToken,
        Guid? createdById = null)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new ChatWorkspaceEntity
        {
            Name = Truncate(name, 200),
            Instructions = NormalizeInstructions(instructions),
            CreatedById = createdById,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.ChatWorkspaces.Add(entity);
        await context.SaveChangesAsync(cancellationToken);

        return new ChatWorkspace(entity.Id, entity.Name, entity.Instructions, now, now, 0);
    }

    public async Task<ChatWorkspace?> UpdateAsync(Guid id, string name, string? instructions, CancellationToken cancellationToken)
    {
        var trimmed = Truncate(name, 200);
        var rules = NormalizeInstructions(instructions);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var updated = await context.ChatWorkspaces
            .Where(w => w.Id == id)
            .ExecuteUpdateAsync(w => w
                .SetProperty(p => p.Name, trimmed)
                .SetProperty(p => p.Instructions, rules), cancellationToken);
        return updated == 0 ? null : await GetAsync(id, cancellationToken);
    }

    /// <summary>Blank rules are stored as null, so "no rules" is one state in the column rather than two.</summary>
    private static string? NormalizeInstructions(string? instructions) =>
        string.IsNullOrWhiteSpace(instructions)
            ? null
            : Truncate(instructions.Trim(), SharePointIndexDbContext.WorkspaceInstructionsLength);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The conversations stay: the foreign key clears their WorkspaceId, which leaves each one using
        // the sandbox binding on its own row again.
        return await context.ChatWorkspaces.Where(w => w.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    private static string Truncate(string value, int length) =>
        value.Length > length ? value[..length] : value;
}

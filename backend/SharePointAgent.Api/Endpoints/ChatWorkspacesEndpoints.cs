using SharePointAgent.Application;
using SharePointAgent.Persistence;

namespace SharePointAgent.Api;

public static class ChatWorkspacesEndpoints
{
    /// <summary>The longest workspace name the column holds; the repository truncates past it.</summary>
    private const int NameLength = 200;

    /// <summary>
    /// The longest rule text. Every turn in the workspace carries it, so an over-long one is rejected
    /// here rather than silently truncated into something that reads as a half-finished instruction.
    /// </summary>
    private const int InstructionsLength = SharePointIndexDbContext.WorkspaceInstructionsLength;

    public static void MapChatWorkspacesEndpoints(this WebApplication app)
    {
        // A workspace groups conversations so they share one agent sandbox. Everything the agent
        // downloads, edits, or writes in one conversation is on the same disk in the next, instead of
        // each conversation starting from an empty one. It can also carry rules that are added to the
        // agent's instructions for every conversation in it.
        app.MapGet("/api/chat/workspaces", (
            HttpContext context,
            IChatWorkspaceRepository store,
            CancellationToken cancellationToken) => store.ListAsync(cancellationToken,
                !AppAccess.CanReadAdministration(context.AppUser().Roles) ? context.AppUser().Id : null));

        app.MapGet("/api/chat/workspaces/{id:guid}", async (
            Guid id,
            IChatWorkspaceRepository store,
            CancellationToken cancellationToken) =>
        {
            var workspace = await store.GetAsync(id, cancellationToken);
            return workspace is null ? Results.NotFound() : Results.Ok(workspace);
        });

        app.MapPost("/api/chat/workspaces", async (
            HttpContext context,
            ChatWorkspaceRequest? body,
            IChatWorkspaceRepository store,
            CancellationToken cancellationToken) =>
        {
            var name = body?.Name?.Trim() ?? "";
            var error = Validate(name, body?.Instructions);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            var created = await store.CreateAsync(name, body?.Instructions, cancellationToken, context.AppUser().Id);
            return Results.Created($"/api/chat/workspaces/{created.Id}", created);
        });

        app.MapPut("/api/chat/workspaces/{id:guid}", async (
            Guid id,
            ChatWorkspaceRequest? body,
            IChatWorkspaceRepository store,
            CancellationToken cancellationToken) =>
        {
            var name = body?.Name?.Trim() ?? "";
            var error = Validate(name, body?.Instructions);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            // Conversations already in the workspace pick the new rules up on their next turn; what
            // has already been answered is not revisited.
            var updated = await store.UpdateAsync(id, name, body?.Instructions, cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        // Deleting a workspace releases its conversations rather than removing them. Their history is
        // untouched; each goes back to a sandbox of its own. The remote session is not deleted here,
        // so Foundry retention is managed separately, as it is for a deleted conversation.
        app.MapDelete("/api/chat/workspaces/{id:guid}", async (
            Guid id,
            IChatWorkspaceRepository store,
            CancellationToken cancellationToken) =>
            await store.DeleteAsync(id, cancellationToken)
                ? Results.Ok(new { deleted = id })
                : Results.NotFound());
    }

    private static string? Validate(string name, string? instructions)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "A non-empty 'name' is required.";
        }

        if (name.Length > NameLength)
        {
            return $"'name' cannot exceed {NameLength} characters.";
        }

        return instructions?.Trim().Length > InstructionsLength
            ? $"'instructions' cannot exceed {InstructionsLength} characters."
            : null;
    }
}

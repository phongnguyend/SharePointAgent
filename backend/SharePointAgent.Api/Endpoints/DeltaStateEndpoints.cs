using SharePointAgent.Application;

namespace SharePointAgent.Api;

public static class DeltaStateEndpoints
{
    public static void MapDeltaStateEndpoints(this WebApplication app)
    {
        app.MapGet("/api/state/delta", (
            IIndexStateRepository reader,
            CancellationToken cancellationToken) => reader.ListDeltaStateAsync(cancellationToken));

        app.MapPost("/api/state/delta/{driveId}/reset", async (
            string driveId,
            IDeltaStateRepository deltaState,
            CancellationToken cancellationToken) =>
            await deltaState.ResetAsync(driveId, cancellationToken)
                ? Results.Ok(new { reset = driveId })
                : Results.NotFound(new { error = "Delta state record not found." }));

        app.MapDelete("/api/state/delta/{driveId}", async (
            string driveId,
            IDeltaStateRepository deltaState,
            CancellationToken cancellationToken) =>
            await deltaState.DeleteAsync(driveId, cancellationToken)
                ? Results.Ok(new { deleted = driveId })
                : Results.NotFound(new { error = "Delta state record not found." }));
    }
}

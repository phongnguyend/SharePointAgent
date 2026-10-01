using Microsoft.EntityFrameworkCore;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class SignatureEndpoints
{
    public static void MapSignatureEndpoints(this WebApplication app)
    {
        app.MapGet("/api/attachment-files/signing-options", (SignatureRequestService signing) => Results.Ok(signing.EnabledProviders));
        var group = app.MapGroup("/api/attachment-files/{id:guid}/signatures");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try
            {
                return await next(context);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (DbUpdateException)
            {
                return Results.Conflict(new { error = "The signing request could not be saved. Refresh the request list before trying again." });
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
            catch (OperationCanceledException) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                return Results.Json(new { error = "The signing provider timed out. Refresh the request list before trying again." }, statusCode: 504);
            }
        });
        group.MapGet("", async (Guid id, HttpContext context, SignatureRequestService signing, CancellationToken ct) =>
            Results.Ok(await signing.ListAsync(id, context.AppUser().Id, AppAccess.CanReadAdministration(context.AppUser().Roles), ct)));
        group.MapPost("", async (Guid id, SignatureInput input, HttpContext context, SignatureRequestService signing, CancellationToken ct) =>
            Results.Ok(await signing.CreateAsync(id, context.AppUser().Id, input, ct)));
        group.MapPost("/{requestId:guid}/prepare", async (Guid id, Guid requestId, HttpContext context, SignatureRequestService signing, CancellationToken ct) =>
        {
            var row = await signing.FindAsync(id, requestId, context.AppUser().Id, AppAccess.CanReadAdministration(context.AppUser().Roles), ct);
            return Results.Ok(new { url = await signing.PrepareAsync(row, ct) });
        });
        group.MapPost("/{requestId:guid}/refresh", async (Guid id, Guid requestId, HttpContext context, SignatureRequestService signing, CancellationToken ct) =>
        {
            var row = await signing.FindAsync(id, requestId, context.AppUser().Id, AppAccess.CanReadAdministration(context.AppUser().Roles), ct);
            return Results.Ok(await signing.RefreshAsync(row, ct));
        });
        group.MapGet("/{requestId:guid}/download", async (Guid id, Guid requestId, HttpContext context, SignatureRequestService signing, CancellationToken ct, bool audit = false) =>
        {
            var row = await signing.FindAsync(id, requestId, context.AppUser().Id, AppAccess.CanReadAdministration(context.AppUser().Roles), ct);
            return Results.File(await signing.DownloadAsync(row, audit, ct), "application/pdf", $"{(audit ? "audit" : "signed")}-{row.Id}.pdf");
        });
    }
}

using SharePointAgent.Infrastructure.DocumentSigning;

namespace SharePointAgent.Api;

public static class SigningAuthorizationEndpoints
{
    public static void MapSigningAuthorizationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/document-signing/adobe");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            if (!context.HttpContext.AppUser().Roles.Contains(SharePointAgent.Domain.AppRoles.GlobalAdmin))
            {
                return Results.Forbid();
            }
            try
            {
                return await next(context);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
            catch (OperationCanceledException) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                return Results.Json(new { error = "Adobe timed out. Authorize again." }, statusCode: 504);
            }
        });
        group.MapGet("", (AdobeSignAuthorizationService service) => Results.Ok(service.Configuration));
        group.MapPost("/authorize", (HttpContext context, AdobeSignAuthorizationService service) => Results.Ok(service.Begin(context.AppUser().Id)));
        group.MapPost("/exchange", async (AdobeAuthorizationInput input, HttpContext context, AdobeSignAuthorizationService service, CancellationToken ct) =>
            Results.Ok(await service.ExchangeAsync(context.AppUser().Id, input, ct)));
    }
}

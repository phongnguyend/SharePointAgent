using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class SitePermissionEndpoints
{
    public static void MapSitePermissionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/site-permissions", async (SitePermissionRequest input, HttpContext context,
            SitePermissionService service, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.AppUser().Roles.Contains(AppRoles.GlobalAdmin))
            {
                return Results.Forbid();
            }
            try
            {
                return Results.Ok(await service.SaveAsync(input, cancellationToken));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException)
            {
                return Results.Conflict(new { error = "Multiple grants exist for this application. Resolve duplicate site permissions before saving." });
            }
            catch (HttpRequestException)
            {
                return Results.Json(new { error = "Could not configure site access. Check the privileged app credentials, Sites.FullControl.All admin consent, and site URL. Check existing permissions before retrying." }, statusCode: 502);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Results.Json(new { error = "Microsoft Graph timed out. Check existing site permissions before retrying." }, statusCode: 504);
            }
            finally
            {
                input.ClientSecret = "";
            }
        });
    }
}

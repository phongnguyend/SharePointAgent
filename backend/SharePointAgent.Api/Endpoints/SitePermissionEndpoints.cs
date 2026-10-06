using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class SitePermissionEndpoints
{
    public static void MapSitePermissionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/site-permissions/defaults", (HttpContext context, IConfiguration configuration) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.AppUser().Roles.Contains(AppRoles.GlobalAdmin))
            {
                return Results.Forbid();
            }
            var hostname = configuration["SharePoint:SiteHostname"]?.Trim().TrimEnd('/');
            var path = configuration["SharePoint:SitePath"]?.Trim().TrimStart('/') ?? "";
            return Results.Ok(new
            {
                tenantId = configuration["SharePoint:TenantId"] ?? "",
                siteUrl = string.IsNullOrWhiteSpace(hostname) ? "" : $"https://{hostname}/{path}",
                targetClientId = configuration["SharePoint:ClientId"] ?? ""
            });
        });

        app.MapPost("/api/admin/site-permissions", (SitePermissionRequest input, HttpContext context,
            SitePermissionService service, CancellationToken cancellationToken) =>
            HandleAsync(input, context, service, "save", cancellationToken));

        // Credentials stay in the body, never in a URL or query string.
        app.MapPost("/api/admin/site-permissions/list", (SitePermissionRequest input, HttpContext context,
            SitePermissionService service, CancellationToken cancellationToken) =>
            HandleAsync(input, context, service, "list", cancellationToken));

        app.MapPost("/api/admin/site-permissions/delete", (SitePermissionRequest input, HttpContext context,
            SitePermissionService service, CancellationToken cancellationToken) =>
            HandleAsync(input, context, service, "delete", cancellationToken));
    }

    private static async Task<IResult> HandleAsync(SitePermissionRequest input, HttpContext context,
        SitePermissionService service, string operation, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.AppUser().Roles.Contains(AppRoles.GlobalAdmin))
        {
            return Results.Forbid();
        }
        try
        {
            if (operation == "delete")
            {
                await service.DeleteAsync(input, cancellationToken);
                return Results.Ok(new { deleted = input.PermissionId });
            }
            return operation == "list" ? Results.Ok(await service.ListAsync(input, cancellationToken))
                : Results.Ok(await service.SaveAsync(input, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException)
        {
            return Results.Conflict(new { error = "Multiple grants exist for this application. Resolve duplicate site permissions before saving." });
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { error = "Could not access site permissions. Check the privileged app credentials, Sites.FullControl.All admin consent, and site URL. Check existing permissions before retrying a save." }, statusCode: 502);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(new { error = "Microsoft Graph timed out. Check existing site permissions before retrying." }, statusCode: 504);
        }
        finally
        {
            input.ClientSecret = "";
        }
    }
}

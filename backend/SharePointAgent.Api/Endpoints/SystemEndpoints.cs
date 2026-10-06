using SharePointAgent.Infrastructure.Monitoring;

namespace SharePointAgent.Api;

public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

        app.MapGet("/api/admin/service-health", async (HttpContext context, ServiceHealthMonitor monitor,
            BackgroundHealthMonitor background, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!AppAccess.CanReadAdministration(context.AppUser().Roles))
            {
                return Results.Forbid();
            }
            var apis = monitor.CheckAsync(cancellationToken);
            var worker = background.CheckAsync(cancellationToken);
            await Task.WhenAll(apis, worker);
            return Results.Ok((await apis).Append(await worker));
        });

        app.MapGet("/api/auth/config", (HttpContext context, IConfiguration configuration) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var clientId = Guid.Parse(configuration["SharePoint:ClientId"]!).ToString();
            return Results.Ok(new
            {
                tenantId = Guid.Parse(configuration["SharePoint:TenantId"]!).ToString(),
                clientId,
                scope = $"api://{clientId}/{EntraAuthentication.Scope}"
            });
        }).AllowAnonymous();
    }
}

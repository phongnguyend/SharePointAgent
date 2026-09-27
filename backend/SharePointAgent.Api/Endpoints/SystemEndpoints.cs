namespace SharePointAgent.Api;

public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

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

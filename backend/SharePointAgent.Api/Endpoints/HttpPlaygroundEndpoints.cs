using Microsoft.AspNetCore.Mvc;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class HttpPlaygroundEndpoints
{
    public static void MapHttpPlaygroundEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/http-playground", async (HttpContext context, HttpPlaygroundRequest input, HttpPlayground service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.AppUser().Roles.Contains(AppRoles.GlobalAdmin))
            {
                return Results.StatusCode(403);
            }
            try
            {
                return Results.Ok(await service.SendAsync(input, ct));
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Json(new { error = "The HTTP request timed out." }, statusCode: 504);
            }
            catch (HttpRequestException)
            {
                return Results.Json(new { error = "The request failed. Check the public destination, TLS certificate, and network availability. Private addresses are blocked." }, statusCode: 502);
            }
            catch (IOException)
            {
                return Results.Json(new { error = "The response could not be read." }, statusCode: 502);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(4 * 1024 * 1024));

    }
}

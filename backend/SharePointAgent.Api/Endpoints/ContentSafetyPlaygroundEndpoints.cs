using Microsoft.AspNetCore.Mvc;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class ContentSafetyPlaygroundEndpoints
{
    public static void MapContentSafetyPlaygroundEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/content-safety-playground");

        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!context.HttpContext.AppUser().Roles.Contains(AppRoles.GlobalAdmin))
            {
                return Results.StatusCode(403);
            }
            try
            {
                return await next(context);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException)
            {
                return Results.Conflict(new { error = "Content safety is unavailable. Check the ContentSafety endpoint and credentials." });
            }
            catch (ContentSafetyRejectedException exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: exception.StatusCode);
            }
            catch (OperationCanceledException) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                return Results.Json(new { error = "Content safety timed out. Try again." }, statusCode: 504);
            }
        });

        group.MapGet("/options", (HttpContext context) => Results.Ok(new
        {
            configured = context.RequestServices.GetRequiredService<ContentSafetyService>().Configured,
            maxCharacters = 10000
        }));

        group.MapPost("", async (HttpContext context, AssessmentRequest request, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 10000)
            {
                return Results.BadRequest(new { error = "Enter between 1 and 10,000 characters." });
            }
            return Results.Ok(await context.RequestServices.GetRequiredService<ContentSafetyService>()
                .EvaluateAsync(request.Text, context.AppUser().Id, ct));
        }).WithMetadata(new RequestSizeLimitAttribute(128 * 1024));
    }

    public sealed record AssessmentRequest(string? Text);
}

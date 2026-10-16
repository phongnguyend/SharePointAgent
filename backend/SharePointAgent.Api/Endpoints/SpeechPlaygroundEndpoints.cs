using Azure.Identity;
using Microsoft.AspNetCore.Mvc;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class SpeechPlaygroundEndpoints
{
    public static void MapSpeechPlaygroundEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/speech-playground");

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
                return Results.Conflict(new { error = "Configure Speech endpoint and credentials on the server." });
            }
            catch (AuthenticationFailedException)
            {
                return Results.Conflict(new { error = "Azure Speech authentication failed. Check the server's Azure credentials." });
            }
            catch (HttpRequestException)
            {
                return Results.Json(new { error = "Azure Speech request failed. Check the resource, Speech roles, and quota." }, statusCode: 502);
            }
            catch (OperationCanceledException) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                return Results.Json(new { error = "Speech synthesis timed out. Try a shorter text." }, statusCode: 504);
            }
        });

        group.MapGet("/options", async (HttpContext context, CancellationToken ct) =>
        {
            var service = context.RequestServices.GetRequiredService<SpeechPlaygroundService>();
            return Results.Ok(new { configured = service.Configured, maxCharacters = SpeechPlaygroundService.MaxCharacters,
                voices = service.Configured ? await service.GetVoicesAsync(ct) : [] });
        });

        group.MapPost("", async (HttpContext context, SynthesisRequest request, CancellationToken ct) =>
        {
            var service = context.RequestServices.GetRequiredService<SpeechPlaygroundService>();
            var audio = await service.SynthesizeAsync(request.Text, request.Voice, request.Format, ct);
            return Results.File(audio, request.Format == "wav" ? "audio/wav" : "audio/mpeg", $"speech.{request.Format}");
        }).WithMetadata(new RequestSizeLimitAttribute(64 * 1024));
    }

    public sealed record SynthesisRequest(string? Text, string? Voice, string? Format);
}

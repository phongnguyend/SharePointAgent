using Microsoft.AspNetCore.Http.Features;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class ChatTranscriptionEndpoints
{
    public static void MapChatTranscriptionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/chat/transcriptions");

        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
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
                return Results.Json(new { error = "Transcription timed out. Try a shorter recording, or type your message instead." }, statusCode: 504);
            }
        });

        group.MapGet("/options", (ChatTranscriptionService transcription) => Results.Ok(transcription.View));

        // The body is the raw recording, with the recorder's audio content type.
        group.MapPost("", async (HttpContext context, ChatTranscriptionService transcription, CancellationToken ct, double? durationSeconds = null) =>
        {
            const long limit = ChatTranscriptionService.MaxBytes;
            var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false })
            {
                bodyLimit.MaxRequestBodySize = limit + 1;
            }
            if (context.Request.ContentLength > limit)
            {
                return Results.BadRequest(new { error = "The recording is too long. Keep dictation under five minutes." });
            }
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > limit)
                {
                    return Results.BadRequest(new { error = "The recording is too long. Keep dictation under five minutes." });
                }
            }
            return Results.Ok(await transcription.TranscribeAsync(context.AppUser().Id, buffer.ToArray(), context.Request.ContentType, durationSeconds, ct));
        });
    }
}

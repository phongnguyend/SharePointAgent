using System.Diagnostics;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class ImagePlaygroundEndpoints
{
    private const int MaxFileBytes = 10 * 1024 * 1024;

    public static void MapImagePlaygroundEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/image-playground/describe", (HttpContext context, CancellationToken cancellationToken) =>
            ProcessAsync(context, AttachmentImageService.DescriptionExtensions, async (fileName, bytes) =>
            {
                var result = await context.RequestServices.GetRequiredService<ImageExtractor>()
                    .DescribeAsync(fileName, bytes, context.AppUser().Id, cancellationToken);
                return new ImageResult(result.Description, result.ModelId, result.UsageReported ? result.Usage : null);
            }, "Check OpenAi configuration and the vision-capable ChatDeployment.", cancellationToken))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxFileBytes + 1024 * 1024));

        app.MapPost("/api/admin/image-playground/extract", (HttpContext context, CancellationToken cancellationToken) =>
            ProcessAsync(context, AttachmentImageService.TextExtensions, async (_, bytes) =>
            {
                var text = await context.RequestServices.GetRequiredService<DocumentIntelligenceClient>().ExtractAsync(bytes, cancellationToken);
                var modelId = context.RequestServices.GetRequiredService<IOptions<DocumentIntelligenceOptions>>().Value.ModelId;
                return new ImageResult(text, modelId, null);
            }, "Check DocumentIntelligence configuration and the image format.", cancellationToken))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxFileBytes + 1024 * 1024));
    }

    private static async Task<IResult> ProcessAsync(HttpContext context, string[] extensions,
        Func<string, byte[], Task<ImageResult>> process, string configurationHint, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.AppUser().Roles.Contains(AppRoles.GlobalAdmin))
        {
            return Results.StatusCode(403);
        }
        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest(new { error = "Select one image using multipart field 'file'." });
        }
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null || form.Files.Count != 1 || file.Length is <= 0 or > MaxFileBytes ||
            !extensions.Contains(Path.GetExtension(file.FileName).ToLowerInvariant()))
        {
            return Results.BadRequest(new { error = $"Select one image up to 10 MB: {string.Join(", ", extensions)}." });
        }
        using var content = new MemoryStream();
        await file.CopyToAsync(content, cancellationToken);
        var timer = Stopwatch.StartNew();
        try
        {
            var result = await process(Path.GetFileName(file.FileName), content.ToArray());
            return Results.Ok(new { result.Text, result.ModelId, result.Usage, elapsedMs = timer.ElapsedMilliseconds, traceId = Activity.Current?.TraceId.ToString() });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(new { error = "Image processing timed out. Try again." }, statusCode: 504);
        }
        catch (TimeoutException)
        {
            return Results.Json(new { error = "Image processing timed out. Try again." }, statusCode: 504);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OptionsValidationException)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ImagePlayground").LogWarning(ex, "Image playground {Path} failed", context.Request.Path);
            return Results.Json(new { error = $"Could not process this image. {configurationHint}" }, statusCode: 502);
        }
        catch (Exception ex) when (ex is HttpRequestException or Azure.RequestFailedException or System.ClientModel.ClientResultException or Azure.Identity.AuthenticationFailedException)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ImagePlayground").LogWarning(ex, "Image provider request failed for {Path}", context.Request.Path);
            return Results.Json(new { error = "The image provider request failed. Check the image and server credentials, or retry. See server logs for details." }, statusCode: 502);
        }

    }

    private sealed record ImageResult(string Text, string? ModelId, ChatTokenUsage? Usage);
}

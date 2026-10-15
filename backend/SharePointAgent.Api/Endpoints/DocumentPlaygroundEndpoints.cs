using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class DocumentPlaygroundEndpoints
{
    private const int MaxFileBytes = 25 * 1024 * 1024;

    public static void MapDocumentPlaygroundEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/markitdown-playground/convert", (HttpContext context, CancellationToken cancellationToken) =>
            ProcessAsync(context, "MarkItDown", async (_, file, bytes) =>
            {
                var options = context.RequestServices.GetRequiredService<IOptions<MarkItDownOptions>>().Value;
                if (!options.IsConfigured)
                {
                    throw new InvalidOperationException("MarkItDown endpoint is not configured.");
                }
                var markdown = await context.RequestServices.GetRequiredService<MarkItDownClient>()
                    .ConvertAsync(Path.GetFileName(file.FileName), bytes, file.ContentType, cancellationToken);
                return new { markdown };
            }, cancellationToken))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxFileBytes + 1024 * 1024));

        app.MapPost("/api/admin/pageindex-playground/index", (HttpContext context, CancellationToken cancellationToken) =>
            ProcessAsync(context, "PageIndex", async (form, file, bytes) =>
            {
                if (Path.GetExtension(file.FileName).ToLowerInvariant() is not (".pdf" or ".md" or ".markdown"))
                {
                    throw new ArgumentException("PageIndex accepts PDF or UTF-8 Markdown files.");
                }
                var includeText = ReadFlag(form, "include_text", true);
                var includeSummaries = ReadFlag(form, "include_summaries", false);
                return await context.RequestServices.GetRequiredService<PageIndexClient>()
                    .IndexAsync(Path.GetFileName(file.FileName), bytes, file.ContentType, cancellationToken, includeText, includeSummaries);
            }, cancellationToken))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxFileBytes + 1024 * 1024));
    }

    private static bool ReadFlag(IFormCollection form, string name, bool fallback)
    {
        if (!form.ContainsKey(name))
        {
            return fallback;
        }
        if (form[name].Count != 1 || !bool.TryParse(form[name], out var value))
        {
            throw new ArgumentException($"{name} must be true or false.");
        }
        return value;
    }

    private static async Task<IResult> ProcessAsync(HttpContext context, string provider,
        Func<IFormCollection, IFormFile, byte[], Task<object>> process, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.AppUser().Roles.Contains(AppRoles.GlobalAdmin))
        {
            return Results.StatusCode(403);
        }
        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest(new { error = "Upload one document using multipart field 'file'." });
        }
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null || form.Files.Count != 1 || file.Length is <= 0 or > MaxFileBytes)
        {
            return Results.BadRequest(new { error = "Select one non-empty document up to 25 MB." });
        }
        using var content = new MemoryStream();
        await file.CopyToAsync(content, cancellationToken);
        var timer = Stopwatch.StartNew();
        try
        {
            var result = await process(form, file, content.ToArray());
            return Results.Ok(new { result, elapsedMs = timer.ElapsedMilliseconds, traceId = Activity.Current?.TraceId.ToString() });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(new { error = $"{provider} request timed out." }, statusCode: 504);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is InvalidOperationException or OptionsValidationException or HttpRequestException or JsonException)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DocumentPlayground")
                .LogWarning(ex, "{Provider} playground request failed", provider);
            return Results.Json(new { error = $"{provider} could not process the document. Check the file, service availability, and the API host's {provider} configuration. See server logs for details." }, statusCode: 502);
        }
    }
}

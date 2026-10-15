using System.Diagnostics;
using DocumentParsers;
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
        MapParser(app, "pdf", async (context, stream, skipImages, enrichImages, token) =>
        {
            var parser = new PdfDocumentParser();
            var result = await parser.ParseAsync(stream, token);
            await EnrichImagesAsync(context, result.Elements.OfType<ImageElement>(), result.Warnings, skipImages || !enrichImages, token);
            return new { markdown = parser.ConvertToMarkdown(result, token, skipImages), warnings = result.Warnings };
        });

        MapParser(app, "docx", async (context, stream, skipImages, enrichImages, token) =>
        {
            var parser = new DocxDocumentParser();
            var result = await parser.ParseAsync(stream, token);
            await EnrichImagesAsync(context, result.BodyElements.OfType<ImageElement>(), result.Warnings, skipImages || !enrichImages, token);
            return new { markdown = parser.ConvertToMarkdown(result, token, skipImages), warnings = result.Warnings };
        });

        MapParser(app, "xlsx", async (context, stream, skipImages, enrichImages, token) =>
        {
            var parser = new XlsxDocumentParser();
            var result = await parser.ParseAsync(stream, token);
            await EnrichImagesAsync(context, result.Worksheets.SelectMany(sheet => sheet.Images), result.Warnings, skipImages || !enrichImages, token);
            return new { markdown = parser.ConvertToMarkdown(result, token, skipImages), warnings = result.Warnings };
        });

        MapParser(app, "pptx", async (context, stream, skipImages, enrichImages, token) =>
        {
            var parser = new PptxDocumentParser();
            var result = await parser.ParseAsync(stream, token);
            await EnrichImagesAsync(context, result.Slides.SelectMany(slide => slide.Elements).OfType<ImageElement>(), result.Warnings, skipImages || !enrichImages, token);
            return new { markdown = parser.ConvertToMarkdown(result, token, skipImages), warnings = result.Warnings };
        });

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

    private static void MapParser(WebApplication app, string format,
        Func<HttpContext, Stream, bool, bool, CancellationToken, Task<object>> parse)
    {
        app.MapPost($"/api/admin/{format}-playground/convert", (HttpContext context, CancellationToken cancellationToken) =>
            ProcessAsync(context, format.ToUpperInvariant(), async (form, file, bytes) =>
            {
                if (!string.Equals(Path.GetExtension(file.FileName), $".{format}", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Select a .{format} document.");
                }
                var skipImages = ReadFlag(form, "skip_images", true);
                var enrichImages = ReadFlag(form, "enrich_images", false);
                using var stream = new MemoryStream(bytes, writable: false);
                try
                {
                    return await parse(context, stream, skipImages, enrichImages, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DocumentPlayground")
                        .LogWarning(ex, "{Format} parser rejected a document", format);
                    throw new ArgumentException("The document could not be parsed. It may be invalid, encrypted, unsupported, or exceed parser limits.");
                }
            }, cancellationToken))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxFileBytes + 1024 * 1024));
    }

    private static async Task EnrichImagesAsync(HttpContext context, IEnumerable<ImageElement> images,
        List<DocumentParseWarning> warnings, bool skipImages, CancellationToken cancellationToken)
    {
        if (skipImages)
        {
            return;
        }
        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AnalyzeAsync("Description", async () =>
            {
                var extension = image.ContentType.ToLowerInvariant() switch
                {
                    "image/png" => ".png",
                    "image/jpeg" => ".jpg",
                    "image/webp" => ".webp",
                    "image/gif" => ".gif",
                    _ => throw new NotSupportedException("Unsupported image format for description.")
                };
                var result = await context.RequestServices.GetRequiredService<ImageExtractor>()
                    .DescribeAsync("document-image" + extension, image.Data, context.AppUser().Id, cancellationToken);
                image.Description = result.Description;
            });
            await AnalyzeAsync("OCR", async () =>
            {
                image.ExtractedText = await context.RequestServices.GetRequiredService<DocumentIntelligenceClient>()
                    .ExtractAsync(image.Data, cancellationToken);
            });
        }

        async Task AnalyzeAsync(string operation, Func<Task> analyze)
        {
            try
            {
                await analyze();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DocumentPlayground")
                    .LogWarning(ex, "Document image {Operation} failed", operation);
                warnings.Add(new(operation + "Failed", $"Image {operation} failed; other extracted content remains available."));
            }
        }
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

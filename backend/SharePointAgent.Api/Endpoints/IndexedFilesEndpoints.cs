using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class IndexedFilesEndpoints
{
    public static void MapIndexedFilesEndpoints(this WebApplication app)
    {
        // Operator views and checkpoint actions over the worker's SQL Server state. Like the search
        // endpoints, these require Entra sign-in but remain shared operator views over the whole index.
        app.MapGet("/api/sensitivity-labels", async (SensitivityLabelCatalog catalog, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                return Results.Ok(await catalog.ReadAsync(cancellationToken));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                return Results.Json(new { error = ex.ResponseStatusCode is 401 or 403
                    ? "Cannot read sensitivity label names. Grant the client application Microsoft Graph SensitivityLabels.Read.All application permission with admin consent."
                    : "Microsoft Graph could not return the sensitivity label catalog. Retry to refresh label names." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapGet("/api/state/summary", (
            IIndexStateRepository reader,
            CancellationToken cancellationToken) => reader.GetSummaryAsync(cancellationToken));

        app.MapGet("/api/state/indexed-files", async (
            IIndexStateRepository reader,
            CancellationToken cancellationToken,
            string? search = null,
            string? driveId = null,
            string? sort = null,
            bool desc = true,
            int skip = 0,
            int top = 25) =>
        {
            if (top is < 1 or > 200)
            {
                return Results.BadRequest(new { error = "'top' must be between 1 and 200." });
            }

            if (skip < 0)
            {
                return Results.BadRequest(new { error = "'skip' must not be negative." });
            }

            var page = await reader.ListFilesAsync(new IndexedFileQuery(search, driveId, sort, desc, skip, top), cancellationToken);
            return Results.Ok(page);
        });

        app.MapGet("/api/state/indexed-files/{driveId}/{itemId}", async (
            string driveId,
            string itemId,
            IIndexStateRepository reader,
            CancellationToken cancellationToken) =>
        {
            var file = await reader.GetFileAsync(driveId, itemId, cancellationToken);
            return file is null ? Results.NotFound() : Results.Ok(file);
        });

        app.MapPost("/api/state/indexed-files/{driveId}/{itemId}/reindex", async (
            string driveId,
            string itemId,
            ISharePointChangeProcessor processor,
            HttpContext httpContext,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
            var logger = loggerFactory.CreateLogger("IndexedFileReindex");
            httpContext.Response.Headers["X-Trace-Id"] = traceId;
            logger.LogInformation("UI reindex requested: TraceId={TraceId}, DriveId={DriveId}, ItemId={ItemId}.", traceId, driveId, itemId);
            try
            {
                var file = await processor.ReindexAsync(driveId, itemId, cancellationToken);
                logger.LogInformation("UI reindex finished: TraceId={TraceId}, Found={Found}.", traceId, file is not null);
                return file is null
                    ? Results.NotFound(new { error = "Indexed file not found in the configured SharePoint library." })
                    : Results.Ok(file);
            }
            catch (MarkItDownConversionException ex)
            {
                logger.LogWarning("UI reindex failed during MarkItDown conversion: TraceId={TraceId}, StatusCode={StatusCode}.", traceId, ex.StatusCode);
                return Results.Json(new { error = $"{ex.Message} (Trace ID: {traceId})", code = "markitdown_conversion_failed", traceId },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (ProtectedDocumentAccessDeniedException ex)
            {
                return Results.Json(new { error = ex.Message, code = "protected_document_access_denied" },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (FileNoLongerIndexableException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (FileTooLargeException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
            catch (HttpRequestException ex)
            {
                if (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return Results.NotFound(new { error = "File no longer exists in SharePoint." });
                }
                return Results.Json(new { error = $"Microsoft Graph rejected the request: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // The browser downloads the original Office bytes and renders them locally. Only indexed files in the
        // configured library can be requested, and the existing download limit bounds the response in memory.
        app.MapGet("/api/state/indexed-files/{driveId}/{itemId}/content", async (
            string driveId,
            string itemId,
            HttpContext context,
            IIndexStateRepository reader,
            SharePointClient sharePointClient,
            IOptions<DownloadOptions> downloadOptions,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var file = await reader.GetFileAsync(driveId, itemId, cancellationToken);
            if (file is null)
            {
                return Results.NotFound(new { error = "Indexed file not found." });
            }

            var extension = Path.GetExtension(file.Name);
            var contentType = extension.ToLowerInvariant() switch
            {
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                _ => null
            };
            if (contentType is null)
            {
                return Results.BadRequest(new { error = "Preview supports DOCX, XLSX, and PPTX files." });
            }

            try
            {
                if (!string.Equals(driveId, await sharePointClient.GetDriveIdAsync(cancellationToken), StringComparison.Ordinal))
                {
                    return Results.NotFound(new { error = "File is outside the configured SharePoint library." });
                }

                var bytes = await sharePointClient.DownloadReadableContentAsync(itemId, file.Name, downloadOptions.Value.MaxFileBytes, cancellationToken);
                return Results.File(bytes, contentType);
            }
            catch (ProtectedDocumentAccessDeniedException ex)
            {
                return Results.Json(new { error = ex.Message, code = "protected_document_access_denied" },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (FileTooLargeException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
            catch (HttpRequestException ex)
            {
                if (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return Results.NotFound(new { error = "File no longer exists in SharePoint." });
                }
                return Results.Json(new { error = $"Microsoft Graph rejected the request: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapGet("/api/state/indexed-files/{driveId}/{itemId}/markdown", async (
            string driveId,
            string itemId,
            HttpContext context,
            IIndexStateRepository reader,
            SharePointClient sharePointClient,
            MarkItDownClient markItDown,
            IOptions<DownloadOptions> downloadOptions,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var file = await reader.GetFileAsync(driveId, itemId, cancellationToken);
            if (file is null)
            {
                return Results.NotFound(new { error = "Indexed file not found." });
            }

            try
            {
                if (!string.Equals(driveId, await sharePointClient.GetDriveIdAsync(cancellationToken), StringComparison.Ordinal))
                {
                    return Results.NotFound(new { error = "File is outside the configured SharePoint library." });
                }

                var bytes = await sharePointClient.DownloadReadableContentAsync(itemId, file.Name, downloadOptions.Value.MaxFileBytes, cancellationToken);
                var markdown = await markItDown.ConvertAsync(file.Name, bytes, file.MimeType, cancellationToken);
                return Results.Ok(new { markdown });
            }
            catch (ProtectedDocumentAccessDeniedException ex)
            {
                return Results.Json(new { error = ex.Message, code = "protected_document_access_denied" },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (FileTooLargeException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }
}

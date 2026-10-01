using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using SharePointAgent.Persistence;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class AttachmentFilesEndpoints
{
    public static void MapAttachmentFilesEndpoints(this WebApplication app)
    {
        app.MapGet("/api/attachment-files/options", (IOptions<UploadOptions> options) =>
            Results.Ok(new
            {
                allowedFileExtensions = options.Value.GetAllowedFileExtensions(),
                maxFileBytes = options.Value.MaxFileBytes,
                textFileExtensions = options.Value.GetTextFileExtensions(),
                imageFileExtensions = options.Value.GetImageFileExtensions(),
                imageDescriptionExtensions = options.Value.GetImageFileExtensions().Intersect(AttachmentImageService.DescriptionExtensions),
                imageTextExtensions = options.Value.GetImageFileExtensions().Intersect(AttachmentImageService.TextExtensions)
            }));

        app.MapPost("/api/attachment-files", async (
            HttpRequest request,
            ChatMessageAttachmentFileService files,
            CancellationToken cancellationToken,
            bool index = true) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Upload one file as multipart/form-data." });
            }
            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file");
            if (file is null)
            {
                return Results.BadRequest(new { error = "A 'file' form part is required." });
            }
            try
            {
                await using var content = file.OpenReadStream();
                var created = await files.CreateAsync(file.FileName, file.ContentType, file.Length, content, cancellationToken, request.HttpContext.AppUser().Id, indexAfterUpload: index);
                return Results.Created($"/api/attachment-files/{created.Id}", created);
            }
            catch (Exception ex) when (ex is UploadTooLargeException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/attachment-files", async (
            HttpContext context,
            ChatMessageAttachmentFileService files,
            CancellationToken cancellationToken,
            string? search = null,
            int skip = 0,
            int top = 25) =>
        {
            if (skip < 0 || top is < 1 or > 200)
            {
                return Results.BadRequest(new { error = "'skip' must be non-negative and 'top' must be between 1 and 200." });
            }
            return Results.Ok(await files.ListAsync(search, skip, top, cancellationToken,
                !AppAccess.CanReadAdministration(context.AppUser().Roles) ? context.AppUser().Id : null));
        });

        app.MapGet("/api/attachment-files/{id:guid}/download", async (
            Guid id,
            ChatMessageAttachmentFileService files,
            CancellationToken cancellationToken) =>
        {
            var file = await files.DownloadAsync(id, cancellationToken);
            return file is null
                ? Results.NotFound()
                : Results.Stream(file.Content, file.ContentType, file.FileName, enableRangeProcessing: true);
        });

        app.MapGet("/api/attachment-files/{id:guid}/markdown", async (
            Guid id,
            HttpContext context,
            ChatMessageAttachmentFileService files,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var markdown = await files.GetIndexedMarkdownAsync(id, cancellationToken);
                return markdown is null
                    ? Results.NotFound(new { error = "Attachment file not found." })
                    : Results.Ok(new { markdown });
            }
            catch (AttachmentMarkdownUnavailableException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (UploadTooLargeException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/attachment-files/{id:guid}/convert-to-markdown", async (
            Guid id,
            HttpContext context,
            ChatMessageAttachmentFileService files,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var markdown = await files.ConvertToMarkdownAsync(id, cancellationToken);
                return markdown is null
                    ? Results.NotFound(new { error = "Attachment file not found." })
                    : Results.Ok(new { markdown });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (UploadTooLargeException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/attachment-files/{id:guid}/describe-image", async (
            Guid id,
            HttpContext context,
            AttachmentImageService images,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var text = await images.DescribeAsync(id, context.AppUser().Id, cancellationToken);
                return text is null
                    ? Results.NotFound(new { error = "Attachment file not found." })
                    : Results.Ok(new { text });
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
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/attachment-files/{id:guid}/extract-text", async (
            Guid id,
            HttpContext context,
            AttachmentImageService images,
            AttachmentPdfService pdfs,
            IDbContextFactory<SharePointIndexDbContext> contextFactory,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
                var name = await db.ChatMessageAttachmentFiles.Where(x => x.Id == id)
                    .Select(x => x.FileName).SingleOrDefaultAsync(cancellationToken);
                var text = Path.GetExtension(name)?.Equals(".pdf", StringComparison.OrdinalIgnoreCase) == true
                    ? await pdfs.ExtractTextAsync(id, cancellationToken)
                    : await images.ExtractTextAsync(id, cancellationToken);
                return text is null
                    ? Results.NotFound(new { error = "Attachment file not found." })
                    : Results.Ok(new { text });
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
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/attachment-files/{id:guid}/reindex", async (
            Guid id,
            ChatMessageAttachmentFileService files,
            CancellationToken cancellationToken) =>
        {
            var result = await files.ReindexAsync(id, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapDelete("/api/attachment-files/{id:guid}", async (
            Guid id,
            ChatMessageAttachmentFileService files,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return await files.DeleteOrphanAsync(id, cancellationToken)
                    ? Results.Ok(new { deleted = id })
                    : Results.NotFound();
            }
            catch (AttachmentFileIsLinkedException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // Microsoft Graph webhook subscriptions. Unlike the endpoints above these change tenant state: removing
        // a subscription stops change notifications, leaving the drive to the scheduled synchronization alone.
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.Kiota.Abstractions;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class BrowseEndpoints
{
    public sealed record FolderRequest(string ParentId, string Name);

    public sealed record ChangeRequest(string Name, string ETag, string? DestinationId);

    public static void MapBrowseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/browse");
        group.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ApiException ex)
            {
                return GraphError(ex.ResponseStatusCode);
            }
            catch (HttpRequestException ex)
            {
                return GraphError((int?)ex.StatusCode ?? 502);
            }
            catch (InvalidDataException)
            {
                return Results.Json(new { error = "SharePoint returned an incomplete response. Refresh before retrying." }, statusCode: 502);
            }
        });
        group.MapGet("/", async (string? folderId, SharePointClient client, CancellationToken ct) =>
            Results.Ok(await client.BrowseAsync(folderId, ct)));
        group.MapGet("/recycle-bin", async (SharePointClient client, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await client.BrowseRecycleBinAsync(ct));
            }
            catch (ApiException ex) when (ex.ResponseStatusCode is 403 or 404 or 501)
            {
                return Results.Json(new
                {
                    error = ex.ResponseStatusCode == 403
                        ? "Microsoft Graph denied recycle bin access. This preview API documents Files.Read.All or Sites.Read.All application permission (or their ReadWrite equivalents), with admin consent; Sites.Selected alone is not listed as supported. Ask your administrator to review the application's permissions."
                        : "The SharePoint recycle bin preview API is unavailable for this site. Open the site's recycle bin in SharePoint."
                }, statusCode: ex.ResponseStatusCode == 403 ? 403 : 502);
            }
        });
        group.MapPost("/folders", async (FolderRequest body, SharePointClient client, CancellationToken ct) =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(body.ParentId);
            return Results.Ok(await client.CreateBrowseFolderAsync(body.ParentId, body.Name, ct));
        });
        group.MapPatch("/{id}", async (string id, ChangeRequest body, SharePointClient client, CancellationToken ct) =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(body.ETag);
            await client.RenameBrowseItemAsync(id, body.Name, body.ETag, ct);
            return Results.Ok(new { completed = true });
        });
        group.MapDelete("/{id}", async (string id, string etag, SharePointClient client, CancellationToken ct) =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(etag);
            await client.DeleteBrowseItemAsync(id, etag, ct);
            return Results.Ok(new { completed = true });
        });
        group.MapPost("/{id}/{operation}", async (string id, string operation, ChangeRequest body, SharePointClient client, CancellationToken ct) =>
        {
            if (operation is not ("copy" or "move"))
            {
                return Results.NotFound();
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(body.DestinationId);
            ArgumentException.ThrowIfNullOrWhiteSpace(body.ETag);
            await client.TransferBrowseItemAsync(id, body.DestinationId, body.Name, body.ETag, operation == "copy", ct);
            return Results.Json(new { completed = operation == "move", accepted = true }, statusCode: operation == "copy" ? 202 : 200);
        });
        group.MapPut("/upload", UploadAsync)
            .WithMetadata(new RequestSizeLimitAttribute(SharePointClient.BrowseUploadLimitBytes));
    }

    private static async Task<IResult> UploadAsync(string parentId, string name, HttpRequest request, SharePointClient client, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentId);
        SharePointClient.ValidateBrowseName(name);
        if (request.ContentLength > SharePointClient.BrowseUploadLimitBytes)
        {
            return Results.Json(new { error = "Files must be 100 MB or smaller." }, statusCode: 413);
        }
        // Seekable disk staging supports Graph upload sessions without buffering entire files in RAM.
        var path = Path.GetTempFileName();
        try
        {
            await using var content = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);
            var buffer = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (content.Length + read > SharePointClient.BrowseUploadLimitBytes)
                {
                    return Results.Json(new { error = "Files must be 100 MB or smaller." }, statusCode: 413);
                }
                await content.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            content.Position = 0;
            return Results.Ok(await client.UploadBrowseFileAsync(parentId, name, content, ct));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IResult GraphError(int status) => Results.Json(new
    {
        error = status switch
        {
            403 => "SharePoint denied access. The application's configured library grant must allow this operation.",
            404 => "This file or folder no longer exists. Refresh the folder.",
            409 => "An item with that name already exists. Choose another name.",
            412 => "This item changed since it was loaded. Refresh before trying again.",
            429 => "SharePoint is busy. Wait a moment before trying again.",
            400 => "SharePoint rejected the name or destination. Check the request and try again.",
            _ => "Could not complete the SharePoint request. Refresh before retrying."
        }
    }, statusCode: status is 400 or 403 or 404 or 409 or 412 or 429 ? status : 502);
}

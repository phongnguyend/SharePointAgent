using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace SharePointAgent.SandboxHost.Endpoints;

/// <summary>
/// File operations on the workspace. Paths are query parameters on GET, PUT, and DELETE, and JSON fields
/// on POST; either way they are relative to the workspace root.
/// </summary>
public static class FileEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapFileEndpoints(this IEndpointRouteBuilder routes)
    {
        var files = routes.MapGroup("/files");

        files.MapGet("", (SandboxWorkspace workspace, string? path, bool? recursive, string? pattern) =>
            Results.Ok(workspace.List(path, recursive ?? false, pattern)));

        files.MapGet("/info", (SandboxWorkspace workspace, string? path) => Results.Ok(workspace.Describe(path)));

        files.MapGet("/content", (SandboxWorkspace workspace, string? path) =>
        {
            var full = workspace.ResolveFile(path);
            if (!ContentTypes.TryGetContentType(full, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            return Results.File(full, contentType, enableRangeProcessing: true);
        });

        // The body is the file's raw bytes, in any content type.
        files.MapPut("/content", async (HttpContext context, SandboxWorkspace workspace, IOptions<SandboxHostOptions> options, string? path, bool? overwrite, CancellationToken ct) =>
        {
            var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false })
            {
                bodyLimit.MaxRequestBodySize = options.Value.MaxFileBytes + 1;
            }

            return Results.Ok(await workspace.WriteAsync(path, context.Request.Body, overwrite ?? true, ct));
        });

        files.MapGet("/text", async (SandboxWorkspace workspace, string? path, int? startLine, int? lineCount, CancellationToken ct) =>
            Results.Ok(await workspace.ReadTextAsync(path, startLine, lineCount, ct)));

        files.MapPut("/text", async (SandboxWorkspace workspace, WriteTextRequest request, CancellationToken ct) =>
            Results.Ok(await workspace.WriteTextAsync(request, ct)));

        files.MapPost("/edit", async (SandboxWorkspace workspace, EditTextRequest request, CancellationToken ct) =>
            Results.Ok(await workspace.EditTextAsync(request, ct)));

        files.MapPost("/directories", (SandboxWorkspace workspace, CreateDirectoryRequest request) =>
            Results.Ok(workspace.CreateDirectory(request.Path)));

        files.MapPost("/move", (SandboxWorkspace workspace, TransferRequest request) => Results.Ok(workspace.Move(request)));

        files.MapPost("/copy", (SandboxWorkspace workspace, TransferRequest request) => Results.Ok(workspace.Copy(request)));

        files.MapPost("/search", async (SandboxWorkspace workspace, SearchRequest request, CancellationToken ct) =>
            Results.Ok(await workspace.SearchAsync(request, ct)));

        files.MapPost("/zip", (SandboxWorkspace workspace, ZipRequest request) => Results.Ok(workspace.Zip(request)));

        files.MapPost("/unzip", (SandboxWorkspace workspace, UnzipRequest request) => Results.Ok(workspace.Unzip(request)));

        files.MapDelete("", (SandboxWorkspace workspace, string? path, bool? recursive) =>
        {
            workspace.Delete(path, recursive ?? false);
            return Results.NoContent();
        });
    }
}

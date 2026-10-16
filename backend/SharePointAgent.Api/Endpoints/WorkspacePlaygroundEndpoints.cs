using Azure;
using Azure.Identity;
using Microsoft.AspNetCore.Mvc;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Api;

public static class WorkspacePlaygroundEndpoints
{
    public static void MapWorkspacePlaygroundEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/workspace-playground/{provider}");

        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (context.HttpContext.Items[typeof(AppUserView)] is not AppUserView user || !user.Roles.Contains(AppRoles.GlobalAdmin))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            var provider = context.HttpContext.Request.RouteValues["provider"]?.ToString();
            if (provider is not ("dynamic-sessions" or "sandboxes"))
            {
                return Results.NotFound();
            }

            try
            {
                return await next(context);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "The instance or file no longer exists. Refresh before retrying." });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (AgentWorkspaceUnavailableException exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: 502);
            }
            catch (AuthenticationFailedException)
            {
                return Results.Json(new { error = "Azure authentication failed. Sign in with az login locally or configure the hosting managed identity." }, statusCode: 502);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or RequestFailedException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return Results.Json(new { error = "The workspace request failed. Check resource permissions and SandboxHost availability, then refresh; a mutation may already have completed." }, statusCode: 502);
            }
            catch (OperationCanceledException)
            {
                return Results.Json(new { error = "The workspace request timed out or was cancelled. Refresh before retrying; the operation may have completed." }, statusCode: 504);
            }
        });

        group.MapGet("/instances", async (string provider, IWorkspacePlayground playground, CancellationToken ct) =>
            Results.Ok(await playground.ListAsync(provider, ct)));

        group.MapPost("/instances", async (string provider, IWorkspacePlayground playground, CancellationToken ct) =>
            Results.Ok(await playground.CreateAsync(provider, ct)));

        group.MapPost("/instances/{id}/actions/{action}", async (string provider, string id, string action,
            IWorkspacePlayground playground, CancellationToken ct) =>
        {
            await playground.ChangeStateAsync(provider, id, action, ct);
            return Results.NoContent();
        });

        group.MapGet("/instances/{id}/files", async (string provider, string id, string? path,
            IWorkspacePlayground playground, CancellationToken ct) =>
        {
            var workspace = await playground.OpenAsync(provider, id, ct);
            return Results.Ok(await workspace.ListAsync(path, false, ct));
        });

        group.MapGet("/instances/{id}/files/content", async (string provider, string id, string path,
            IWorkspacePlayground playground, CancellationToken ct) =>
        {
            var workspace = await playground.OpenAsync(provider, id, ct);
            var file = await workspace.ReadAsync(path, ct);
            // Always download, so uploaded HTML/scripts cannot execute on the API's origin.
            return Results.File(file.Content, "application/octet-stream", file.Name);
        });

        group.MapPost("/instances/{id}/files", async (string provider, string id, WorkspaceFileChange change,
            IWorkspacePlayground playground, CancellationToken ct) =>
        {
            var workspace = await playground.OpenAsync(provider, id, ct);
            return Results.Ok(await workspace.ManageAsync(change, ct));
        }).WithMetadata(new RequestSizeLimitAttribute(8 * 1024 * 1024));

        group.MapPut("/instances/{id}/files/text", async (string provider, string id, PlaygroundTextFile file,
            IWorkspacePlayground playground, CancellationToken ct) =>
        {
            if (file.Content is null || file.Content.Length > 1_000_000 || string.IsNullOrWhiteSpace(file.Path))
            {
                return Results.BadRequest(new { error = "Provide a file path and text up to 1,000,000 characters." });
            }
            var workspace = await playground.OpenAsync(provider, id, ct);
            return Results.Ok(await workspace.WriteTextAsync(file.Path, file.Content, file.Overwrite, ct));
        }).WithMetadata(new RequestSizeLimitAttribute(6 * 1024 * 1024));

        group.MapPost("/instances/{id}/files/archive", async (string provider, string id, PlaygroundArchive archive,
            IWorkspacePlayground playground, CancellationToken ct) =>
        {
            if (archive.Paths is null || archive.Paths.Count is < 1 or > 100 ||
                archive.Paths.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(archive.Destination) ||
                archive.Operation is not ("zip" or "unzip") || (archive.Operation == "unzip" && archive.Paths.Count != 1))
            {
                return Results.BadRequest(new { error = "Choose 1–100 paths to zip, or one ZIP file to extract, and a destination path." });
            }
            var workspace = await playground.OpenAsync(provider, id, ct);
            var result = archive.Operation == "zip"
                ? await workspace.ZipAsync(archive.Paths, archive.Destination, false, ct)
                : await workspace.UnzipAsync(archive.Paths[0], archive.Destination, false, ct);
            return Results.Ok(result);
        }).WithMetadata(new RequestSizeLimitAttribute(64 * 1024));

        group.MapPost("/instances/{id}/executions", async (string provider, string id, WorkspaceExecution execution,
            IWorkspacePlayground playground, CancellationToken ct) =>
        {
            if (execution.Language is not ("python" or "node" or "powershell" or "bash") ||
                string.IsNullOrWhiteSpace(execution.Code) == string.IsNullOrWhiteSpace(execution.ScriptPath) ||
                execution.Code?.Length > 100_000 || execution.Arguments is null || execution.Arguments.Count > 100 ||
                execution.Arguments.Any(argument => argument is null || argument.Length > 4096) ||
                execution.TimeoutSeconds is < 1 or > 220)
            {
                return Results.BadRequest(new { error = "Choose Python, Node.js, PowerShell, or Bash, provide code or a script path, and use a timeout of 1–220 seconds. Code is limited to 100,000 characters and arguments to 100 strings of 4,096 characters." });
            }
            var workspace = await playground.OpenAsync(provider, id, ct);
            return Results.Ok(await workspace.ExecuteAsync(execution, ct));
        }).WithMetadata(new RequestSizeLimitAttribute(1024 * 1024));
    }

    public sealed record PlaygroundTextFile(string Path, string Content, bool Overwrite = false);

    public sealed record PlaygroundArchive(string Operation, IReadOnlyList<string> Paths, string Destination);
}

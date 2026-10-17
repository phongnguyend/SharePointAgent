using Microsoft.AspNetCore.Mvc;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using Microsoft.Extensions.Options;
using SharePointAgent.Infrastructure;
using SharePointAgent.Infrastructure.Workspaces;

namespace SharePointAgent.Api;

public static class WorkspaceFileEndpoints
{
    public static void MapWorkspaceFileEndpoints(this WebApplication app)
    {
        app.MapPost("/api/chat/conversations/{id:guid}/files/operations", async (
            Guid id, FoundryWorkspaceRequest request, IChatRepository chats, IChatAgentModeSelector modes,
            IFoundrySessionRepository sessions, IOptions<ChatAgentHostingOptions> hosting,
            IServiceProvider services, CancellationToken ct) =>
        {
            if (await chats.GetConversationAsync(id, ct) is null)
            {
                return Results.NotFound();
            }

            if (request.Operation is not ("text" or "zip" or "unzip" or "execute"))
            {
                return Results.BadRequest(new { error = "Unknown workspace operation." });
            }
            if (request.Content?.Length > 1_000_000 ||
                (request.Operation is "zip" or "unzip" &&
                    (request.Paths is null || request.Paths.Count is < 1 or > 100 ||
                        (request.Operation == "unzip" && request.Paths.Count != 1))))
            {
                return Results.BadRequest(new { error = "Text is limited to 1 MB. Select 1–100 paths for Zip or one archive for extraction." });
            }
            if (request.Operation == "execute" && (request.Execution is not { } execution ||
                execution.Language is not ("python" or "node" or "powershell" or "bash") ||
                string.IsNullOrWhiteSpace(execution.Code) == string.IsNullOrWhiteSpace(execution.ScriptPath) ||
                execution.Code?.Length > 100_000 || execution.TimeoutSeconds is < 1 or > 220 ||
                execution.Arguments is null || execution.Arguments.Count > 100 ||
                execution.Arguments.Any(argument => argument is null || argument.Length > 4096)))
            {
                return Results.BadRequest(new { error = "Provide code or a script, valid arguments, and a timeout of 1–220 seconds." });
            }

            try
            {
                IAgentWorkspace workspace;
                if ((await modes.ResolveAsync(id, ct)).Mode == ChatAgentExecutionMode.Foundry)
                {
                    var endpoint = hosting.Value.Foundry.Endpoint;
                    var sessionId = await sessions.GetAsync(id, endpoint!, ct);
                    if (sessionId is null)
                    {
                        return Results.Conflict(new { error = "Send a chat message first to start the Foundry session." });
                    }
                    workspace = new FoundrySessionWorkspace(services.GetRequiredService<FoundryWorkspacePlayground>(), sessionId);
                }
                else
                {
                    workspace = await services.GetRequiredService<IAgentWorkspaceProvider>().GetAsync(id, ct);
                }

                if (request.Operation == "execute" && !workspace.IsIsolated)
                {
                    return Results.Conflict(new { error = "Script execution requires a Dynamic Session, Sandbox, or Foundry session." });
                }
                try
                {
                    object result = request.Operation switch
                    {
                        "text" => await workspace.WriteTextAsync(request.Path ?? "", request.Content ?? "", request.Overwrite, ct),
                        "zip" => await workspace.ZipAsync(request.Paths!, request.Destination ?? "", false, ct),
                        "unzip" => await workspace.UnzipAsync(request.Paths![0], request.Destination ?? "", false, ct),
                        _ => await workspace.ExecuteAsync(request.Execution!, ct)
                    };
                    return Results.Ok(result);
                }
                finally
                {
                    await workspace.SaveAsync(ct);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or AgentWorkspaceUnavailableException)
            {
                return Results.Json(new { error = "The workspace operation failed. Refresh before retrying; it may already have completed." }, statusCode: 502);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(8 * 1024 * 1024));

        // Existing identity middleware checks conversation ownership for every mutation on this route.
        app.MapPost("/api/chat/conversations/{id:guid}/files/manage", async (
            Guid id, WorkspaceFileChange change, IChatRepository chats, IAgentFileBrowser browser, CancellationToken ct) =>
        {
            if (await chats.GetConversationAsync(id, ct) is null)
            {
                return Results.NotFound(new { error = "The conversation no longer exists." });
            }
            try
            {
                return Results.Ok(await browser.ManageAsync(id, change, ct));
            }
            catch (InvalidDataException)
            {
                return Results.Json(new { error = "The workspace returned an invalid response. Refresh before retrying." }, statusCode: 502);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
            {
                return Results.Json(new { error = "The workspace could not complete the request. Refresh before retrying; the operation may already have completed." }, statusCode: 502);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(8 * 1024 * 1024));
    }
}

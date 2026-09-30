using Microsoft.AspNetCore.Mvc;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Api;

public static class SandboxFileEndpoints
{
    public static void MapSandboxFileEndpoints(this WebApplication app)
    {
        // Existing identity middleware checks conversation ownership for every mutation on this route.
        app.MapPost("/api/chat/conversations/{id:guid}/files/manage", async (
            Guid id, SandboxFileChange change, IChatRepository chats, IAgentFileBrowser browser, CancellationToken ct) =>
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
                return Results.Json(new { error = "The sandbox returned an invalid response. Refresh before retrying." }, statusCode: 502);
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
                return Results.Json(new { error = "The sandbox could not complete the request. Refresh before retrying; the operation may already have completed." }, statusCode: 502);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(8 * 1024 * 1024));
    }
}

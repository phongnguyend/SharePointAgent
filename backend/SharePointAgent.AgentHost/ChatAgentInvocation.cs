using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using Azure.AI.AgentServer.Invocations;

namespace SharePointAgent.AgentHost;

public sealed class ChatAgentInvocation(
    IChatAgentExecutor executor,
    AgentFileSystem workingDirectory,
    ILogger<ChatAgentInvocation> logger) : InvocationHandler
{
    public override async Task HandleAsync(HttpRequest httpRequest, HttpResponse response,
        InvocationContext context, CancellationToken token)
    {
        // Several kinds of request arrive at this one endpoint, and the header says which before the
        // body is read. No header is a chat turn, which is what every caller that predates the file
        // operations sends. Neither file operation reaches the model.
        switch (Operation(httpRequest))
        {
            case AgentInvocation.ListFilesOperation:
                await ListFilesAsync(httpRequest, response, token);
                return;
            case AgentInvocation.ReadFileOperation:
                await ReadFileAsync(httpRequest, response, token);
                return;
            case AgentInvocation.ManageFilesOperation:
                await ManageFilesAsync(httpRequest, response, token);
                return;
        }

        var request = await httpRequest.ReadFromJsonAsync<ChatAgentRequest>(token);
        if (request is null || request.ConversationId == Guid.Empty || request.QuestionId == Guid.Empty)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsJsonAsync(new { error = "conversationId and questionId are required." }, token);
            return;
        }

        using var writer = new ChatStreamWriter<ChatAgentEvent>(response);
        writer.Start();
        try
        {
            var turn = await executor.RunStreamingAsync(request,
                (text, ct) => writer.WriteAsync(new("delta", Text: text), ct),
                (status, ct) => writer.WriteAsync(new("status", Message: status), ct), token);
            await writer.WriteAsync(new("completed", Turn: turn), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Closing the caller's stream cancels the agent and its tools; don't emit a success event.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hosted chat turn failed for {ConversationId}.", request.ConversationId);
            await writer.WriteAsync(new("error", Message: "The hosted agent could not complete the turn."), token);
        }
    }

    /// <summary>The operation asked for, in the casing this handler switches on, or null for a turn.</summary>
    private static string? Operation(HttpRequest request) =>
        request.Headers[AgentInvocation.OperationHeader].ToString() switch
        {
            var value when string.Equals(value, AgentInvocation.ListFilesOperation, StringComparison.OrdinalIgnoreCase)
                => AgentInvocation.ListFilesOperation,
            var value when string.Equals(value, AgentInvocation.ReadFileOperation, StringComparison.OrdinalIgnoreCase)
                => AgentInvocation.ReadFileOperation,
            var value when string.Equals(value, AgentInvocation.ManageFilesOperation, StringComparison.OrdinalIgnoreCase)
                => AgentInvocation.ManageFilesOperation,
            _ => null,
        };

    /// <summary>
    /// Reads the session's working directory and returns it as plain JSON. No model is called, so this
    /// costs nothing and leaves the conversation exactly as it was.
    /// </summary>
    private async Task ListFilesAsync(HttpRequest httpRequest, HttpResponse response, CancellationToken token)
    {
        var request = await httpRequest.ReadFromJsonAsync<AgentFileListingRequest>(token);
        await GuardedAsync(response, token, async () =>
            await response.WriteAsJsonAsync(workingDirectory.List(request?.Path, request?.Recursive ?? false), token));
    }

    private async Task ManageFilesAsync(HttpRequest httpRequest, HttpResponse response, CancellationToken token)
    {
        var limit = httpRequest.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = 8 * 1024 * 1024;
        }
        if (httpRequest.ContentLength > 8 * 1024 * 1024)
        {
            response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        await GuardedAsync(response, token, async () =>
        {
            var request = await httpRequest.ReadFromJsonAsync<AgentFileChangeRequest>(token);
            if (request?.Change is null || request.ConversationId == Guid.Empty)
            {
                throw new ArgumentException("A conversation and file operation are required.");
            }
            var result = await workingDirectory.ManageAsync(request.Change, token);
            await response.WriteAsJsonAsync(result, token);
        });
    }

    /// <summary>
    /// Sends one file out of the session as its own bytes, with the type worked out from its name so
    /// the caller can show it without guessing. No model is called here either.
    /// </summary>
    private async Task ReadFileAsync(HttpRequest httpRequest, HttpResponse response, CancellationToken token)
    {
        var request = await httpRequest.ReadFromJsonAsync<AgentFileReadRequest>(token);
        await GuardedAsync(response, token, async () =>
        {
            var file = await workingDirectory.ReadAsync(request?.Path ?? "", token);
            response.ContentType = file.ContentType;
            response.ContentLength = file.Content.Length;
            await response.Body.WriteAsync(file.Content, token);
        });
    }

    /// <summary>
    /// Turns the file errors a caller can act on into a 400 carrying the message, and leaves anything
    /// else to fail the request. The sandbox's own wording is what tells the reader what went wrong.
    /// </summary>
    private static async Task GuardedAsync(HttpResponse response, CancellationToken token, Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                   && !response.HasStarted)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsJsonAsync(new { error = ex.Message }, token);
        }
    }
}

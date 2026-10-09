using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

/// <summary>
/// Browses the working directory the agent uses for a conversation, wherever it is. In <c>Local</c> mode
/// that is the API's own disk, shared by every conversation; in an isolated mode it is the conversation's
/// workspace session or sandbox, and a change made here is saved like a change the agent makes.
/// </summary>
public sealed class WorkspaceAgentFileBrowser(IAgentWorkspaceProvider workspaces) : IAgentFileBrowser
{
    public async Task<SandboxFileChangeResult> ManageAsync(Guid conversationId, SandboxFileChange change, CancellationToken cancellationToken)
    {
        var workspace = await workspaces.GetAsync(conversationId, cancellationToken);
        var result = await workspace.ManageAsync(change, cancellationToken);
        await workspace.SaveAsync(cancellationToken);
        return result;
    }

    public async Task<FileSystemListing> ListAsync(
        Guid conversationId,
        string? path,
        bool recursive,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaces.FindAsync(conversationId, cancellationToken);
        return workspace is null
            ? new FileSystemListing(".", 0, false, [], SandboxStarted: false)
            : await workspace.ListAsync(path, recursive, cancellationToken);
    }

    public async Task<FileContent> ReadAsync(Guid conversationId, string path, CancellationToken cancellationToken)
    {
        var workspace = await workspaces.FindAsync(conversationId, cancellationToken)
            ?? throw new ArgumentException("This conversation has no files yet.");
        return await workspace.ReadAsync(path, cancellationToken);
    }
}

/// <summary>
/// Lists the working directory inside a Foundry session sandbox, which is on a disk this process
/// cannot see. The listing travels over the same Invocations endpoint a turn does, but the host
/// answers it without calling the model, so it costs no tokens and writes no history.
/// </summary>
public sealed class FoundryAgentFileBrowser(
    HttpClient http,
    TokenCredential credential,
    IFoundrySessionRepository sessions,
    IOptions<ChatAgentHostingOptions> options) : IAgentFileBrowser
{
    /// <summary>A listing is a directory read; it should not wait as long as a turn may.</summary>
    private const int TimeoutSeconds = 60;

    private readonly FoundryChatAgentOptions _options = options.Value.Foundry;

    public async Task<SandboxFileChangeResult> ManageAsync(Guid conversationId, SandboxFileChange change, CancellationToken cancellationToken)
    {
        var sessionId = await sessions.GetAsync(conversationId, _options.Endpoint, cancellationToken)
            ?? throw new InvalidOperationException("This conversation has no sandbox yet. Send a question first.");
        return await SendAsync<AgentFileChangeRequest, SandboxFileChangeResult>(sessionId,
            AgentInvocation.ManageFilesOperation, new(conversationId, change), "application/json",
            async (response, token) => await response.Content.ReadFromJsonAsync<SandboxFileChangeResult>(ChatStreamWriter<SandboxFileChangeResult>.Json, token)
                ?? throw new InvalidDataException("The hosted agent returned an empty file operation response."), cancellationToken);
    }

    public async Task<FileSystemListing> ListAsync(
        Guid conversationId,
        string? path,
        bool recursive,
        CancellationToken cancellationToken)
    {
        var sessionId = await sessions.GetAsync(conversationId, _options.Endpoint, cancellationToken);
        if (sessionId is null)
        {
            // Asking without a session would have Foundry start a sandbox, and this conversation would
            // then be looking at a fresh one it never ran in. Report that there is nothing yet instead.
            return new(".", 0, false, [], SandboxStarted: false);
        }

        return await SendAsync<AgentFileListingRequest, FileSystemListing>(
            sessionId,
            AgentInvocation.ListFilesOperation,
            new AgentFileListingRequest(conversationId, path, recursive),
            "application/json",
            async (response, token) => await response.Content.ReadFromJsonAsync<FileSystemListing>(
                    ChatStreamWriter<FileSystemListing>.Json, token)
                ?? throw new InvalidDataException("The hosted agent returned an empty listing."),
            cancellationToken);
    }

    public async Task<FileContent> ReadAsync(Guid conversationId, string path, CancellationToken cancellationToken)
    {
        var sessionId = await sessions.GetAsync(conversationId, _options.Endpoint, cancellationToken)
            ?? throw new InvalidOperationException(
                "This conversation has no sandbox yet, so there is no file to read.");

        return await SendAsync<AgentFileReadRequest, FileContent>(
            sessionId,
            AgentInvocation.ReadFileOperation,
            new AgentFileReadRequest(conversationId, path),
            "*/*",
            async (response, token) =>
            {
                // The sandbox worked the type out from the name; the name is in the path we asked for.
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                var name = path.Split('/').Last();
                return new FileContent(path, name, contentType, await response.Content.ReadAsByteArrayAsync(token));
            },
            cancellationToken);
    }

    /// <summary>
    /// One invocation of an operation that does not run the model, with the session pinned and the
    /// sandbox's own refusal preserved. <paramref name="read"/> consumes the body inside the timeout,
    /// which is why the caller hands it in rather than being handed the response.
    /// </summary>
    private async Task<T> SendAsync<TRequest, T>(
        string sessionId,
        string operation,
        TRequest payload,
        string accept,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(TimeoutSeconds, _options.TimeoutSeconds)));
        var sent = timeout.Token;
        try
        {
            var endpoint = QueryHelpers.AddQueryString(_options.Endpoint, "agent_session_id", sessionId);
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload, options: ChatStreamWriter<TRequest>.Json),
            };
            message.Headers.Add(AgentInvocation.OperationHeader, operation);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (!_options.AllowUnauthenticatedLocalhost)
            {
                var accessToken = await credential.GetTokenAsync(
                    new TokenRequestContext(["https://ai.azure.com/.default"]), sent);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
            }

            using var response = await http.SendAsync(message, sent);
            if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound)
            {
                // The sandbox rejected the path. Its message is the useful one.
                var problem = await response.Content.ReadFromJsonAsync<SandboxError>(sent);
                throw new ArgumentException(problem?.Error ?? "The path could not be read.");
            }

            response.EnsureSuccessStatusCode();

            // A different sandbox means Foundry did not honour the session, so this would be somebody
            // else's files presented as this conversation's.
            if (response.Headers.TryGetValues("x-agent-session-id", out var values)
                && values.Single() is { Length: > 0 } returned && returned != sessionId)
            {
                throw new InvalidDataException("Foundry answered from a different sandbox than this conversation's.");
            }

            return await read(response, sent);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && sent.IsCancellationRequested)
        {
            throw new TimeoutException("The sandbox did not answer in time.");
        }
    }

    private sealed record SandboxError(string? Error);
}

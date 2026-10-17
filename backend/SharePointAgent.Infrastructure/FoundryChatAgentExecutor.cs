using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

/// <summary>Streams the shared agent's events over Invocations, without replaying a side-effecting turn.</summary>
public sealed class FoundryChatAgentExecutor(
    HttpClient http,
    TokenCredential credential,
    IFoundrySessionRepository sessions,
    IOptions<ChatAgentHostingOptions> options) : IChatAgentExecutor
{
    private readonly FoundryChatAgentOptions _options = options.Value.Foundry;

    public async Task<ChatTurn> RunStreamingAsync(
        ChatAgentRequest request,
        Func<string, CancellationToken, ValueTask> onText,
        Func<string, CancellationToken, ValueTask> onStatus,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var token = timeout.Token;
        try
        {
            var sessionId = await sessions.GetAsync(request.ConversationId, _options.Endpoint, token);
            var endpoint = sessionId is null ? _options.Endpoint
                : QueryHelpers.AddQueryString(_options.Endpoint, "agent_session_id", sessionId);
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(request),
            };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-ndjson"));
            if (!_options.AllowUnauthenticatedLocalhost)
            {
                var accessToken = await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), token);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
            }

            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode == 424)
            {
                throw await FailedDependencyAsync(response, token);
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType != "application/x-ndjson")
            {
                throw new InvalidDataException("The hosted agent did not return the expected NDJSON stream.");
            }

            // The SDK supplies the sandbox ID before the body, so cancellation doesn't lose the binding.
            if (response.Headers.TryGetValues("x-agent-session-id", out var values))
            {
                var returnedId = values.Single();
                if (string.IsNullOrWhiteSpace(returnedId))
                {
                    throw new InvalidDataException("The hosted agent returned an empty session ID.");
                }

                if (sessionId is not null && sessionId != returnedId)
                {
                    throw new InvalidDataException("Foundry returned a different sandbox for this conversation.");
                }

                await sessions.SaveAsync(request.ConversationId, _options.Endpoint, returnedId, token);
            }
            else if (!_options.AllowUnauthenticatedLocalhost && sessionId is null)
            {
                throw new InvalidDataException("The hosted agent did not return x-agent-session-id; sandbox affinity cannot be saved.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var item = JsonSerializer.Deserialize<ChatAgentEvent>(line, ChatStreamWriter<ChatAgentEvent>.Json)
                    ?? throw new InvalidDataException("The hosted agent returned an empty event.");
                switch (item)
                {
                    case { Type: "delta", Text: not null }:
                        await onText(item.Text, token);
                        break;
                    case { Type: "status", Message: not null }:
                        await onStatus(item.Message, token);
                        break;
                    case { Type: "completed", Turn: not null }:
                        return item.Turn;
                    case { Type: "error" }:
                        throw new InvalidOperationException(item.Message ?? "The hosted agent failed.");
                    default:
                        throw new InvalidDataException($"The hosted agent returned an invalid '{item.Type}' event.");
                }
            }
            throw new EndOfStreamException("The hosted agent disconnected before completing the turn.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The hosted agent exceeded the configured execution timeout.");
        }
    }

    private static async Task<HttpRequestException> FailedDependencyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var details = new List<string>();
        // Keep only diagnostic identifiers; upstream messages may contain configuration or document data.
        static bool IsIdentifier(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 200
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[16 * 1024];
            var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: ct);
            using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && IsIdentifier(code.GetString()))
            {
                details.Add($"Code: {code.GetString()}.");
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // An empty, oversized or non-JSON error must still report the original 424.
        }
        foreach (var header in new[] { "x-ms-request-id", "apim-request-id", "x-agent-session-id" })
        {
            if (response.Headers.TryGetValues(header, out var values) && values.FirstOrDefault() is { } value && IsIdentifier(value))
            {
                details.Add($"{header}: {value}.");
            }
        }
        return new HttpRequestException("Foundry could not invoke the hosted agent (HTTP 424 Failed Dependency). "
            + string.Join(" ", details) + " Check this session's container logs and AgentHost startup/readiness. "
            + "An active agent version alone does not confirm that its session container is ready.", null, response.StatusCode);
    }
}

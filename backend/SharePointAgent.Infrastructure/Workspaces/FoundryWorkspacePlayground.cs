using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure.Workspaces;

public sealed class FoundryWorkspacePlayground(IHttpClientFactory clients, TokenCredential credential, IOptions<ChatAgentHostingOptions> options)
{
    public const string Operation = "workspacePlayground";

    private FoundryChatAgentOptions Settings => options.Value.Foundry;

    internal Uri SessionsUri()
    {
        if (!Uri.TryCreate(Settings.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https"
            || !endpoint.AbsolutePath.EndsWith("/endpoint/protocols/invocations", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Configure ChatAgent:Foundry:Endpoint with the deployed HTTPS Invocations endpoint to manage Foundry sessions.");
        }
        var builder = new UriBuilder(endpoint)
        {
            Path = endpoint.AbsolutePath[..^"protocols/invocations".Length] + "sessions",
            Query = "api-version=v1"
        };
        return builder.Uri;
    }

    private Uri SessionUri(string id, string suffix = "")
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
        {
            throw new ArgumentException("Invalid Foundry session identifier.");
        }
        var root = SessionsUri();
        return new UriBuilder(root) { Path = root.AbsolutePath + "/" + id + suffix }.Uri;
    }

    public async Task<WorkspacePlaygroundListing> ListAsync(CancellationToken ct)
    {
        Uri first;
        try
        {
            first = SessionsUri();
        }
        catch (InvalidOperationException ex)
        {
            return new(false, ex.Message, []);
        }
        Uri? next = first;
        var seen = new HashSet<string>();
        var instances = new List<WorkspacePlaygroundInstance>();
        for (var page = 0; page < 20 && next is not null; page++)
        {
            if (next.GetLeftPart(UriPartial.Path) != first.GetLeftPart(UriPartial.Path) || !seen.Add(next.AbsoluteUri))
            {
                throw new InvalidDataException("Foundry returned an invalid continuation URL.");
            }
            using var response = await SendAsync(HttpMethod.Get, next, null, ct);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var items = body.TryGetProperty("data", out var data) ? data : body.GetProperty("value");
            instances.AddRange(items.EnumerateArray().Select(View));
            var link = body.TryGetProperty("nextLink", out var n) ? n.GetString()
                : body.TryGetProperty("next_link", out n) ? n.GetString() : null;
            next = string.IsNullOrWhiteSpace(link) ? null : new Uri(first, link);
            if (next is null && body.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True)
            {
                return new(true, null, instances.DistinctBy(x => x.Id).ToArray(), true);
            }
        }
        return new(true, null, instances.DistinctBy(x => x.Id).ToArray(), next is not null);
    }

    public async Task<WorkspacePlaygroundInstance> CreateAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, SessionsUri(), new { }, ct);
        return View(await response.Content.ReadFromJsonAsync<JsonElement>(ct));
    }

    public async Task ChangeStateAsync(string id, string action, CancellationToken ct)
    {
        if (action is not ("stop" or "delete"))
        {
            throw new ArgumentException("Foundry sessions support stop and delete.");
        }
        using var response = await SendAsync(action == "delete" ? HttpMethod.Delete : HttpMethod.Post,
            SessionUri(id, action == "stop" ? ":stop" : ""), null, ct);
    }

    public async Task<IAgentWorkspace> OpenAsync(string id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, SessionUri(id), null, ct);
        var session = View(await response.Content.ReadFromJsonAsync<JsonElement>(ct));
        if (session.Id != id || !session.CanAccessFiles)
        {
            throw new InvalidOperationException("Wait for this Foundry session to become active, then refresh.");
        }
        return new FoundrySessionWorkspace(this, id);
    }

    public async Task<T> InvokeAsync<T>(string id, FoundryWorkspaceRequest payload, CancellationToken ct)
    {
        _ = SessionUri(id);
        var uri = new Uri(QueryHelpers.AddQueryString(Settings.Endpoint, "agent_session_id", id));
        using var response = await SendAsync(HttpMethod.Post, uri, payload, ct, id);
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, object? payload, CancellationToken ct, string? sessionId = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Settings.TimeoutSeconds));
        using var request = new HttpRequestMessage(method, uri);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }
        if (sessionId is not null)
        {
            request.Headers.Add(AgentInvocation.OperationHeader, Operation);
        }
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), timeout.Token);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        var response = await clients.CreateClient(WorkspacePlayground.HttpClientName).SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            if (status == HttpStatusCode.BadRequest && sessionId is not null)
            {
                try
                {
                    var problem = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                    if (problem.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    {
                        throw new ArgumentException(error.GetString());
                    }
                }
                finally
                {
                    response.Dispose();
                }
            }
            response.Dispose();
            if (status == HttpStatusCode.Forbidden)
            {
                throw new AgentWorkspaceUnavailableException("Foundry denied access (HTTP 403). Check the identity selected by DefaultAzureCredential: locally this may be your Azure CLI or IDE account; in Azure it is the API managed identity. Verify its Foundry project permissions and session access. Foundry User (formerly Azure AI User) permits managing sessions across identities. Deploying AgentHost alone does not grant the caller access.");
            }
            if (status == HttpStatusCode.NotFound)
            {
                throw new KeyNotFoundException();
            }
            if ((int)status is 424 or 502 or 503)
            {
                throw new AgentWorkspaceUnavailableException($"Foundry could not reach a healthy workspace handler (HTTP {(int)status}). Check this session's AgentHost container logs and startup configuration, including KnowledgeGraph:Cosmos:Endpoint. An Active session does not prove that workspace invocations work. After deploying a fix, create a new session to use the new agent version.");
            }
            throw new AgentWorkspaceUnavailableException($"Foundry workspace request failed (HTTP {(int)status}). Check Foundry permissions and deploy the latest AgentHost for workspace operations.");
        }
        if (sessionId is not null && (!response.Headers.TryGetValues("x-agent-session-id", out var ids) || ids.SingleOrDefault() != sessionId))
        {
            response.Dispose();
            throw new InvalidDataException("Foundry did not confirm the requested session.");
        }
        return response;
    }

    private static WorkspacePlaygroundInstance View(JsonElement item)
    {
        var id = item.GetProperty("agent_session_id").GetString() ?? throw new InvalidDataException("Missing session ID.");
        var state = item.GetProperty("status").GetString() ?? "Unknown";
        var active = state.Equals("active", StringComparison.OrdinalIgnoreCase) || state.Equals("running", StringComparison.OrdinalIgnoreCase);
        return new(id, active ? "Running" : state, active);
    }
}

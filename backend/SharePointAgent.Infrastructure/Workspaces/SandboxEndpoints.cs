using System.Net.Http.Headers;
using Azure.Core;
using Microsoft.AspNetCore.WebUtilities;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure.Workspaces;

/// <summary>How requests reach one SharePointAgent.SandboxHost instance: its address, its credentials, and its readiness.</summary>
public interface ISandboxEndpoint
{
    /// <summary>A stable, non-secret name for logs.</summary>
    string Description { get; }

    Uri BuildUri(string path, IReadOnlyDictionary<string, string?> query);

    ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>Waits until the host answers, for environments that start or resume on demand.</summary>
    ValueTask WaitUntilReadyAsync(HttpClient http, CancellationToken cancellationToken);
}

/// <summary>
/// A session in an Azure Container Apps dynamic session pool. The pool forwards the path unchanged to the
/// session named by <c>identifier</c>, allocating one on first use, and authenticates callers itself: an
/// Entra token for <c>https://dynamicsessions.io</c> from an identity with the Session Executor role.
/// </summary>
public sealed class DynamicSessionEndpoint(Uri poolManagementEndpoint, string identifier, TokenCredential credential) : ISandboxEndpoint
{
    private static readonly TokenRequestContext Scope = new(["https://dynamicsessions.io/.default"]);

    public string Description => $"dynamic session {identifier}";

    public Uri BuildUri(string path, IReadOnlyDictionary<string, string?> query)
    {
        var parameters = new Dictionary<string, string?>(query) { ["identifier"] = identifier };
        return new Uri(QueryHelpers.AddQueryString(Combine(poolManagementEndpoint, path), parameters));
    }

    public async ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(Scope, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    // The pool allocates a session on the first request for a new identifier and holds it until it is ready.
    public ValueTask WaitUntilReadyAsync(HttpClient http, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    internal static string Combine(Uri baseAddress, string path) =>
        baseAddress.ToString().TrimEnd('/') + "/" + path.TrimStart('/');
}

/// <summary>
/// A SandboxHost running in an Azure Container Apps sandbox, on the port the sandbox exposes. The port is
/// guarded by an IP allow list and the per-sandbox API key; a stopped sandbox may take a moment to resume,
/// so the health check is awaited before the first request.
/// </summary>
public sealed class SandboxEndpoint(Uri baseAddress, string apiKey, TimeSpan healthWait) : ISandboxEndpoint
{
    public string Description => $"sandbox {baseAddress.Host}";

    public Uri BuildUri(string path, IReadOnlyDictionary<string, string?> query) =>
        new(QueryHelpers.AddQueryString(DynamicSessionEndpoint.Combine(baseAddress, path), query));

    public ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Add("X-Api-Key", apiKey);
        return ValueTask.CompletedTask;
    }

    public async ValueTask WaitUntilReadyAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + healthWait;
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(BuildUri("health", new Dictionary<string, string?>()), cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (DateTimeOffset.UtcNow < deadline)
            {
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new AgentWorkspaceUnavailableException("The sandbox did not become ready. Try again shortly.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }
}

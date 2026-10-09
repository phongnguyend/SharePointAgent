using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SharePointAgent.Infrastructure.Monitoring;

public sealed record ServiceHealthStatus(string Name, string Status, string Message, long? ResponseTimeMs, DateTimeOffset CheckedAtUtc,
    DateTimeOffset? LastHeartbeatUtc = null, DateTimeOffset? LastSyncSucceededUtc = null,
    DateTimeOffset? LastFailureUtc = null, DateTimeOffset? SubscriptionExpiresUtc = null);

public sealed class ServiceHealthMonitor(HttpClient http, IConfiguration configuration)
{
    public Task<ServiceHealthStatus[]> CheckAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(
            CheckServiceAsync("MarkItDown", "/health", IsStatusOk, cancellationToken),
            CheckServiceAsync("PageIndex", "/health", IsStatusOk, cancellationToken),
            CheckServiceAsync("Ollaya", "/", IsOllayaRunning, cancellationToken));

    private async Task<ServiceHealthStatus> CheckServiceAsync(string name, string defaultHealthPath,
        Func<string, bool> isHealthy, CancellationToken cancellationToken)
    {
        var endpoint = configuration[$"{name}:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new(name, "not-configured", $"Set {name}:Endpoint in the API configuration.", null, DateTimeOffset.UtcNow);
        }
        var path = configuration[$"{name}:HealthPath"] ?? defaultHealthPath;
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/" + path.TrimStart('/'), UriKind.Absolute, out var url) ||
            url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)
        {
            return new(name, "misconfigured", $"Check {name}:Endpoint and {name}:HealthPath in the API configuration.", null, DateTimeOffset.UtcNow);
        }

        var timer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            // Every service exposes an anonymous health endpoint. No service API key is needed.
            using var response = await http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Result("unhealthy", $"Health endpoint returned HTTP {(int)response.StatusCode}.");
            }
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!isHealthy(body))
            {
                return Result("unhealthy", "Health endpoint returned an unexpected response.");
            }
            return Result("healthy", "Health endpoint is responding.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result("unhealthy", "Health check timed out after 10 seconds.");
        }
        catch (HttpRequestException)
        {
            return Result("unhealthy", "Could not reach the health endpoint.");
        }
        catch (JsonException)
        {
            return Result("unhealthy", "Health endpoint did not return valid JSON.");
        }

        ServiceHealthStatus Result(string status, string message) => new(name, status, message, timer.ElapsedMilliseconds, DateTimeOffset.UtcNow);
    }

    /// <summary>MarkItDown and PageIndex answer <c>{"status":"ok"}</c>; a hello page or any other JSON is not healthy.</summary>
    private static bool IsStatusOk(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.String && status.GetString() == "ok";
    }

    /// <summary>Ollaya's liveness route answers in plain text rather than JSON.</summary>
    private static bool IsOllayaRunning(string body)
    {
        return body.Contains("Ollaya is running", StringComparison.Ordinal);
    }
}

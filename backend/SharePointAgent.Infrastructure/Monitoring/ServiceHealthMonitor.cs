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
        Task.WhenAll(CheckServiceAsync("MarkItDown", cancellationToken), CheckServiceAsync("PageIndex", cancellationToken));

    private async Task<ServiceHealthStatus> CheckServiceAsync(string name, CancellationToken cancellationToken)
    {
        var endpoint = configuration[$"{name}:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new(name, "not-configured", $"Set {name}:Endpoint in the API configuration.", null, DateTimeOffset.UtcNow);
        }
        var path = configuration[$"{name}:HealthPath"] ?? "/health";
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
            // Both services expose an anonymous health endpoint. No service API key is needed.
            using var response = await http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Result("unhealthy", $"Health endpoint returned HTTP {(int)response.StatusCode}.");
            }
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            if (body.RootElement.ValueKind != JsonValueKind.Object ||
                !body.RootElement.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String || status.GetString() != "ok")
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
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure.Workspaces;

/// <summary>Finds or creates the running sandbox that serves a workspace scope.</summary>
public interface ISandboxProvisioner
{
    Task<SandboxBinding> AcquireAsync(string scope, CancellationToken cancellationToken);

    /// <summary>Deletes the scope's sandbox and its binding, so the next acquire creates a new one.</summary>
    Task ReleaseAsync(string scope, CancellationToken cancellationToken);
}

/// <summary>
/// Creates one Azure Container Apps sandbox per workspace scope on first use, and brings it back on every
/// later use:
/// <list type="bullet">
/// <item>A missing binding creates a sandbox from the SandboxHost disk image, with a fresh random API key,
/// port 8080 exposed with on-demand activation, auto-suspend, and the scope as a label.</item>
/// <item>A stopped or suspended sandbox is resumed and awaited.</item>
/// <item>A deleted or failed one is replaced, with its files gone, which is logged.</item>
/// </list>
/// Requests mirror the Sandboxes data plane as the official <c>azure-containerapps-sandbox</c> SDK sends
/// them. The token is for <c>https://dynamicsessions.io</c>, and the identity needs SandboxGroup Data Owner.
/// </summary>
public sealed class AcaSandboxProvisioner(
    IHttpClientFactory httpClientFactory,
    TokenCredential credential,
    ISandboxRegistry registry,
    IOptions<AgentWorkspaceOptions> options,
    ILogger<AcaSandboxProvisioner> logger) : ISandboxProvisioner
{
    public const string HttpClientName = "SandboxesDataPlane";

    public const int SandboxHostPort = 8080;

    public const string ScopeLabel = "sharepointagent-scope";

    private static readonly TokenRequestContext Scope = new(["https://dynamicsessions.io/.default"]);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> Resumable = new(StringComparer.OrdinalIgnoreCase) { "Stopped", "Suspended", "Idle" };

    private static readonly HashSet<string> Terminal = new(StringComparer.OrdinalIgnoreCase) { "Deleting", "Failed" };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>How often a starting sandbox's state is checked.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    private SandboxesWorkspaceOptions Settings => options.Value.Sandboxes;

    public async Task<SandboxBinding> AcquireAsync(string scope, CancellationToken cancellationToken)
    {
        var settings = Settings;
        if (settings.UsesSharedSandbox)
        {
            return new SandboxBinding(null, new Uri(settings.SharedEndpoint!), settings.SharedApiKey!);
        }

        // One provisioning at a time per scope in this process; the registry's create-only write settles
        // races between processes.
        var gate = _locks.GetOrAdd(scope, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.ProvisionTimeoutSeconds));
            try
            {
                return await AcquireCoreAsync(scope, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AgentWorkspaceUnavailableException("The sandbox did not start in time. Try again shortly.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReleaseAsync(string scope, CancellationToken cancellationToken)
    {
        if (Settings.UsesSharedSandbox)
        {
            return;
        }

        // Under the scope's gate, so a turn starting in this process waits and then creates a new sandbox.
        var gate = _locks.GetOrAdd(scope, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await registry.GetAsync(scope, cancellationToken);
            await registry.DeleteBindingAsync(scope, cancellationToken);
            if (existing?.SandboxId is { } sandboxId)
            {
                await DeleteSandboxAsync(sandboxId, cancellationToken);
                logger.LogInformation("Deleted sandbox {SandboxId} of workspace {Scope} on request.", sandboxId, scope);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SandboxBinding> AcquireCoreAsync(string scope, CancellationToken cancellationToken)
    {
        var existing = await registry.GetAsync(scope, cancellationToken);
        if (existing?.SandboxId is { } sandboxId)
        {
            var sandbox = await GetSandboxAsync(sandboxId, cancellationToken);
            if (sandbox is not null && !Terminal.Contains(sandbox.State ?? ""))
            {
                sandbox = await EnsureRunningAsync(sandbox, cancellationToken);
                var current = existing with { Endpoint = PortUrl(sandbox) ?? existing.Endpoint };
                if (current.Endpoint != existing.Endpoint)
                {
                    await registry.SaveAsync(scope, current, cancellationToken);
                }
                return current;
            }

            logger.LogWarning("The sandbox for workspace {Scope} is gone ({State}); creating a new one without its files.", scope, sandbox?.State ?? "deleted");
            await registry.DeleteBindingAsync(scope, cancellationToken);
        }

        return await CreateAsync(scope, cancellationToken);
    }

    private async Task<SandboxBinding> CreateAsync(string scope, CancellationToken cancellationToken)
    {
        var settings = Settings;
        var apiKey = Base64Url(RandomNumberGenerator.GetBytes(32));
        var created = await SendAsync<SandboxResource>(HttpMethod.Put, $"{GroupPath}/sandboxes", CreateBody(scope, apiKey), cancellationToken, allowPostFallback: true);
        var sandbox = await EnsureRunningAsync(created, cancellationToken);
        var endpoint = PortUrl(sandbox) ?? throw new AgentWorkspaceUnavailableException("The new sandbox did not expose its port.");
        var binding = new SandboxBinding(sandbox.Id, endpoint, apiKey);

        if (!await registry.TryAddAsync(scope, binding, cancellationToken))
        {
            // Another instance bound a sandbox to this workspace first. Use that one and remove ours.
            await DeleteSandboxAsync(sandbox.Id, cancellationToken);
            return await registry.GetAsync(scope, cancellationToken)
                ?? throw new AgentWorkspaceUnavailableException("The sandbox binding changed while it was created. Try again.");
        }

        logger.LogInformation("Created sandbox {SandboxId} for workspace {Scope} in group {SandboxGroup}.", sandbox.Id, scope, settings.SandboxGroup);
        return binding;
    }

    internal object CreateBody(string scope, string apiKey)
    {
        var settings = Settings;
        var lifecycle = new Dictionary<string, object>
        {
            ["autoSuspendPolicy"] = new { enabled = true, interval = settings.AutoSuspendSeconds, mode = settings.AutoSuspendMode }
        };
        if (settings.AutoDeleteAfterDays > 0)
        {
            lifecycle["autoDeletePolicy"] = new { enabled = true, deleteIntervalInSeconds = settings.AutoDeleteAfterDays * 86400 };
        }

        var cidrs = settings.AllowedSourceCidrs.Where(cidr => !string.IsNullOrWhiteSpace(cidr)).ToList();
        return new
        {
            sourcesRef = new { diskImage = new { id = settings.DiskImageId } },
            resources = new { cpu = settings.Cpu, memory = settings.Memory },
            lifecycle,
            labels = new Dictionary<string, string> { [ScopeLabel] = scope },

            // SandboxHost strips Sandbox__* variables from the scripts it runs.
            environment = new Dictionary<string, string> { ["Sandbox__ApiKey"] = apiKey },
            ports = new object[]
            {
                new
                {
                    port = SandboxHostPort,
                    auth = new { anonymous = true },
                    activationMode = "OnDemand",
                    ipAccessControl = cidrs.Count == 0
                        ? null
                        : new
                        {
                            defaultAction = "Deny",
                            rules = new[] { new { name = "application", action = "Allow", priority = 10, sourceCidrs = cidrs } }
                        }
                }
            },
            entrypoint = settings.Entrypoint.Count == 0 ? null : settings.Entrypoint
        };
    }

    private async Task<SandboxResource> EnsureRunningAsync(SandboxResource sandbox, CancellationToken cancellationToken)
    {
        if (string.Equals(sandbox.State, "Running", StringComparison.OrdinalIgnoreCase) && PortUrl(sandbox) is not null)
        {
            return sandbox;
        }

        if (Resumable.Contains(sandbox.State ?? ""))
        {
            if (string.Equals(sandbox.StateDetails?.StoppedReason, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentWorkspaceUnavailableException("This workspace's sandbox has been disabled by an administrator.");
            }

            try
            {
                await SendAsync<JsonElement?>(HttpMethod.Post, $"{GroupPath}/sandboxes/{sandbox.Id}/resume", null, cancellationToken);
            }
            catch (AgentWorkspaceUnavailableException)
            {
                // It may already be resuming; the wait below decides.
            }
        }

        while (true)
        {
            await Task.Delay(PollInterval, cancellationToken);
            var current = await GetSandboxAsync(sandbox.Id, cancellationToken)
                ?? throw new AgentWorkspaceUnavailableException("The sandbox was deleted while it was starting.");
            if (Terminal.Contains(current.State ?? ""))
            {
                throw new AgentWorkspaceUnavailableException("The sandbox failed to start.");
            }

            if (string.Equals(current.State, "Running", StringComparison.OrdinalIgnoreCase) && PortUrl(current) is not null)
            {
                return current;
            }
        }
    }

    private async Task<SandboxResource?> GetSandboxAsync(string sandboxId, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, $"{GroupPath}/sandboxes/{Uri.EscapeDataString(sandboxId)}", null, cancellationToken);
        using var response = await Client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response, "read");
        return await response.Content.ReadFromJsonAsync<SandboxResource>(Json, cancellationToken);
    }

    private async Task DeleteSandboxAsync(string sandboxId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = await CreateRequestAsync(HttpMethod.Delete, $"{GroupPath}/sandboxes/{Uri.EscapeDataString(sandboxId)}", null, cancellationToken);
            using var _ = await Client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Could not delete the sandbox {SandboxId}.", sandboxId);
        }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken, bool allowPostFallback = false)
    {
        using var request = await CreateRequestAsync(method, path, body, cancellationToken);
        using var response = await Client.SendAsync(request, cancellationToken);

        // Newer data-plane versions create with POST where 2026-02-01-preview uses PUT.
        if (allowPostFallback && response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            return await SendAsync<T>(HttpMethod.Post, path, body, cancellationToken);
        }

        EnsureSuccess(response, method == HttpMethod.Get ? "read" : "change");
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
        {
            return default!;
        }
        return (await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken))!;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var uri = $"{Settings.DataPlaneEndpoint.TrimEnd('/')}{path}?api-version={Uri.EscapeDataString(Settings.ApiVersion)}";
        var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        var token = await credential.GetTokenAsync(Scope, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return request;
    }

    private void EnsureSuccess(HttpResponseMessage response, string action)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // The data plane's own error text can name resources; only the status goes to the log.
        logger.LogWarning("The Sandboxes data plane refused to {Action} a sandbox: {StatusCode}.", action, (int)response.StatusCode);
        throw new AgentWorkspaceUnavailableException(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
            ? "This application is not allowed to manage sandboxes. Check its SandboxGroup Data Owner role."
            : "The sandbox service is unavailable. Try again shortly.");
    }

    private static Uri? PortUrl(SandboxResource sandbox)
    {
        var url = sandbox.Ports?.FirstOrDefault(port => port.Port == SandboxHostPort)?.Url;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    private string GroupPath =>
        $"/subscriptions/{Uri.EscapeDataString(Settings.SubscriptionId!)}/resourceGroups/{Uri.EscapeDataString(Settings.ResourceGroup!)}/sandboxGroups/{Uri.EscapeDataString(Settings.SandboxGroup!)}";

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal sealed record SandboxResource(string Id, string? State, SandboxStateDetails? StateDetails, IReadOnlyList<SandboxPortResource>? Ports);

    internal sealed record SandboxStateDetails(string? StoppedReason);

    internal sealed record SandboxPortResource(int Port, string? Url);
}

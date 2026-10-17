using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure.Workspaces;

/// <summary>Admin operations against the configured Azure resources, independent of chat ownership.</summary>
public sealed class WorkspacePlayground(
    IHttpClientFactory clients,
    IOptions<AgentWorkspaceOptions> options,
    IOptions<LocalWorkingDirectoryOptions> limits,
    TokenCredential credential,
    Func<ISandboxRegistry> registryFactory,
    ILoggerFactory loggers,
    FoundryWorkspacePlayground? foundry = null) : IWorkspacePlayground
{
    public const string HttpClientName = "WorkspacePlayground";

    private static readonly TokenRequestContext TokenScope = new(["https://dynamicsessions.io/.default"]);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private AgentWorkspaceOptions Settings => options.Value;

    private readonly Lazy<AcaSandboxProvisioner> _provisioner = new(() => new(clients, credential, registryFactory(), options,
        loggers.CreateLogger<AcaSandboxProvisioner>()));

    private AcaSandboxProvisioner Provisioner => _provisioner.Value;

    public async Task<WorkspacePlaygroundListing> ListAsync(string provider, CancellationToken cancellationToken)
    {
        if (provider == "foundry-sessions" && foundry is not null)
        {
            return await foundry.ListAsync(cancellationToken);
        }
        ValidateProvider(provider);
        var configuration = ConfigurationError(provider);
        if (configuration is not null)
        {
            return new(false, configuration, []);
        }

        var sessions = provider == "dynamic-sessions";
        var first = sessions ? SessionUri("listSessions", null) : SandboxCollectionUri();
        Uri? next = first;
        var instances = new List<WorkspacePlaygroundInstance>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < 20 && next is not null; page++)
        {
            if (!seen.Add(next.AbsoluteUri))
            {
                throw new InvalidDataException("The service returned a repeated page.");
            }

            using var response = await SendAsync(sessions ? HttpMethod.Post : HttpMethod.Get, next, cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
            var values = body.ValueKind == JsonValueKind.Array ? body
                : body.TryGetProperty("value", out var value) ? value
                : body.TryGetProperty("items", out var items) ? items
                : throw new InvalidDataException("The service returned an invalid list.");
            foreach (var item in values.EnumerateArray())
            {
                if (sessions)
                {
                    instances.Add(SessionView(item));
                }
                else
                {
                    var sandbox = item.Deserialize<AcaSandboxProvisioner.SandboxResource>(Json)
                        ?? throw new InvalidDataException("The service returned an invalid sandbox.");
                    var canAccess = Settings.Snapshots.IsConfigured && await Provisioner.FindBindingAsync(sandbox, cancellationToken) is not null;
                    instances.Add(new(sandbox.Id, sandbox.State ?? "Unknown", canAccess));
                }
            }

            var link = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("nextLink", out var continuation)
                ? continuation.GetString() : null;
            next = string.IsNullOrWhiteSpace(link) ? null : ValidateContinuation(first, link);
        }

        return new(true, null, instances.DistinctBy(instance => instance.Id).ToArray(), next is not null);
    }

    public async Task<WorkspacePlaygroundInstance> CreateAsync(string provider, CancellationToken cancellationToken)
    {
        if (provider == "foundry-sessions" && foundry is not null)
        {
            return await foundry.CreateAsync(cancellationToken);
        }
        EnsureConfigured(provider);
        // This is an Azure session identifier / blob scope, not a persistence entity primary key.
        var identifier = "playground-" + Guid.NewGuid().ToString("N");
        if (provider == "dynamic-sessions")
        {
            await SessionWorkspace(identifier).ListAsync("", false, cancellationToken);
            return new(identifier, "Running", true);
        }

        if (!Settings.Snapshots.IsConfigured)
        {
            throw new InvalidOperationException("Configure AgentWorkspace:Snapshots storage before creating sandboxes.");
        }

        var binding = await Provisioner.AcquireAsync(identifier, cancellationToken);
        return new(binding.SandboxId!, "Running", true);
    }

    public async Task ChangeStateAsync(string provider, string id, string action, CancellationToken cancellationToken)
    {
        if (provider == "foundry-sessions" && foundry is not null)
        {
            await foundry.ChangeStateAsync(id, action, cancellationToken);
            return;
        }
        EnsureConfigured(provider);
        ValidateIdentifier(id);
        if (provider == "dynamic-sessions" && action == "stop")
        {
            using var response = await SendAsync(HttpMethod.Post, SessionUri("stopSession", id), cancellationToken, allowNotFound: true);
            return;
        }

        if (provider == "sandboxes" && action == "resume")
        {
            await Provisioner.ResumeAsync(id, cancellationToken);
            return;
        }

        if (provider == "sandboxes" && action == "stop")
        {
            await Provisioner.StopFromPlaygroundAsync(id, cancellationToken);
            return;
        }

        if (provider == "sandboxes" && action == "delete")
        {
            await Provisioner.DeleteFromPlaygroundAsync(id, cancellationToken);
            return;
        }

        throw new ArgumentException("Unsupported lifecycle action.");
    }

    public async Task<IAgentWorkspace> OpenAsync(string provider, string id, CancellationToken cancellationToken)
    {
        if (provider == "foundry-sessions" && foundry is not null)
        {
            return await foundry.OpenAsync(id, cancellationToken);
        }
        EnsureConfigured(provider);
        ValidateIdentifier(id);
        if (provider == "dynamic-sessions")
        {
            // Check metadata first: browsing an expired identifier must not intentionally allocate it again.
            using var response = await SendAsync(HttpMethod.Post, SessionUri("getSession", id), cancellationToken);
            var session = SessionView(await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken));
            if (session.Id != id || session.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new KeyNotFoundException("The session has expired. Refresh the list and create a new session.");
            }
            return SessionWorkspace(id);
        }

        var sandbox = await Provisioner.InspectAsync(id, cancellationToken);
        if (!string.Equals(sandbox.State, "Running", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resume this sandbox before browsing files or running scripts.");
        }

        if (!Settings.Snapshots.IsConfigured)
        {
            throw new InvalidOperationException("Configure AgentWorkspace:Snapshots to access sandbox bindings.");
        }
        var binding = await Provisioner.FindBindingAsync(sandbox, cancellationToken)
            ?? throw new InvalidOperationException("This sandbox has no matching SandboxHost binding in this application's storage. File access and script execution are unavailable.");
        return Workspace(new SandboxEndpoint(binding.Endpoint, binding.ApiKey, TimeSpan.FromSeconds(Settings.Sandboxes.HealthWaitSeconds)), id);
    }

    private IAgentWorkspace SessionWorkspace(string id) => Workspace(
        new DynamicSessionEndpoint(new Uri(Settings.DynamicSessions.PoolManagementEndpoint!), id, credential), id);

    private IAgentWorkspace Workspace(ISandboxEndpoint endpoint, string id) => new SandboxHostWorkspace(
        clients.CreateClient(HttpClientName), endpoint, null, id, Settings, limits.Value, loggers.CreateLogger<SandboxHostWorkspace>());

    private string? ConfigurationError(string provider)
    {
        if (provider == "dynamic-sessions")
        {
            return Settings.DynamicSessions.IsConfigured ? null : "Configure AgentWorkspace:DynamicSessions:PoolManagementEndpoint to use this playground.";
        }
        if (Settings.Sandboxes.UsesSharedSandbox)
        {
            return "This playground manages Azure sandboxes. Clear SharedEndpoint and configure a sandbox group to use it.";
        }
        return Settings.Sandboxes.IsConfigured ? null : "Configure the sandbox subscription, resource group, sandbox group, and disk image in AgentWorkspace:Sandboxes.";
    }

    private void EnsureConfigured(string provider)
    {
        ValidateProvider(provider);
        if (ConfigurationError(provider) is { } error)
        {
            throw new InvalidOperationException(error);
        }
    }

    internal static void ValidateProvider(string provider)
    {
        if (provider is not ("dynamic-sessions" or "sandboxes"))
        {
            throw new ArgumentException("Unknown workspace playground.");
        }
    }

    internal static void ValidateIdentifier(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new ArgumentException("Use an instance identifier containing at most 128 letters, digits, hyphens, or underscores.");
        }
    }

    internal static Uri ValidateContinuation(Uri first, string link)
    {
        var next = new Uri(first, link);
        if (next.Scheme != first.Scheme || next.Authority != first.Authority || next.AbsolutePath != first.AbsolutePath ||
            next.UserInfo.Length != 0 || next.Fragment.Length != 0)
        {
            throw new InvalidDataException("The service returned an invalid continuation URL.");
        }
        if (!QueryHelpers.ParseQuery(next.Query).ContainsKey("api-version") &&
            QueryHelpers.ParseQuery(first.Query).TryGetValue("api-version", out var version))
        {
            next = new Uri(QueryHelpers.AddQueryString(next.AbsoluteUri, "api-version", version.ToString()));
        }
        return next;
    }

    private Uri SessionUri(string operation, string? id) => new(QueryHelpers.AddQueryString(
        DynamicSessionEndpoint.Combine(new Uri(Settings.DynamicSessions.PoolManagementEndpoint!), ".management/" + operation),
        new Dictionary<string, string?> { ["api-version"] = "2025-02-02-preview", ["identifier"] = id }));

    private Uri SandboxCollectionUri()
    {
        var settings = Settings.Sandboxes;
        return new Uri($"{settings.DataPlaneEndpoint.TrimEnd('/')}/subscriptions/{Uri.EscapeDataString(settings.SubscriptionId!)}/resourceGroups/{Uri.EscapeDataString(settings.ResourceGroup!)}/sandboxGroups/{Uri.EscapeDataString(settings.SandboxGroup!)}/sandboxes?api-version={Uri.EscapeDataString(settings.ApiVersion)}");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, CancellationToken cancellationToken, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, uri);
        var token = await credential.GetTokenAsync(TokenScope, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Settings.RequestTimeoutSeconds));
        var response = await clients.CreateClient(HttpClientName).SendAsync(request, timeout.Token);
        if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound))
        {
            return response;
        }
        var status = response.StatusCode;
        response.Dispose();
        if (status == HttpStatusCode.NotFound)
        {
            throw new KeyNotFoundException("The selected instance no longer exists. Refresh the list.");
        }
        throw new AgentWorkspaceUnavailableException($"Azure workspace request failed (HTTP {(int)status}). Check the configured resource and the application's Azure roles.");
    }

    private static WorkspacePlaygroundInstance SessionView(JsonElement value) => new(
        value.GetProperty("identifier").GetString() ?? throw new InvalidDataException("Missing session identifier."),
        "Running", true, Date(value, "createdAt"), Date(value, "expiresAt"));

    private static DateTimeOffset? Date(JsonElement value, string property) =>
        value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String && field.TryGetDateTimeOffset(out var date) ? date : null;
}

public static class WorkspacePlaygroundRegistration
{
    public static IServiceCollection AddWorkspacePlayground(this IServiceCollection services)
    {
        services.AddHttpClient(WorkspacePlayground.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient(AcaSandboxProvisioner.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IWorkspacePlayground>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<AgentWorkspaceOptions>>();
            return new WorkspacePlayground(sp.GetRequiredService<IHttpClientFactory>(), settings,
                sp.GetRequiredService<IOptions<LocalWorkingDirectoryOptions>>(), new DefaultAzureCredential(),
                () => settings.Value.Snapshots.IsConfigured ? new BlobWorkspaceStorage(settings) : new NoSandboxRegistry(),
                sp.GetRequiredService<ILoggerFactory>(),
                new FoundryWorkspacePlayground(sp.GetRequiredService<IHttpClientFactory>(), new DefaultAzureCredential(),
                    sp.GetRequiredService<IOptions<ChatAgentHostingOptions>>()));
        });
        return services;
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure.Workspaces;
using Xunit;

namespace SharePointAgent.Tests;

/// <summary>
/// One sandbox per workspace scope, created on first use and brought back on later ones, against a fake
/// Sandboxes data plane that answers the way the official SDK expects.
/// </summary>
public sealed class SandboxProvisionerTests
{
    private const string Group = "/subscriptions/sub-1/resourceGroups/rg-1/sandboxGroups/group-1";

    private readonly FakeDataPlane _plane = new();
    private readonly InMemoryBindings _bindings = new();

    private AcaSandboxProvisioner Provisioner(Action<SandboxesWorkspaceOptions>? configure = null)
    {
        var options = new AgentWorkspaceOptions
        {
            Mode = AgentWorkspaceMode.Sandboxes,
            Sandboxes = new SandboxesWorkspaceOptions
            {
                SubscriptionId = "sub-1",
                ResourceGroup = "rg-1",
                SandboxGroup = "group-1",
                DiskImageId = "disk-sandboxhost",
                ProvisionTimeoutSeconds = 30
            }
        };
        configure?.Invoke(options.Sandboxes);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_plane));
        var credential = Substitute.For<TokenCredential>();
        credential.GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(call => new AccessToken("token-for-" + call.Arg<TokenRequestContext>().Scopes.Single(), DateTimeOffset.UtcNow.AddHours(1)));
        return new AcaSandboxProvisioner(factory, credential, _bindings, Options.Create(options), NullLogger<AcaSandboxProvisioner>.Instance)
        {
            PollInterval = TimeSpan.FromMilliseconds(5)
        };
    }

    [Fact]
    public async Task TheFirstUseOfAWorkspaceCreatesItsOwnSandbox()
    {
        var binding = await Provisioner().AcquireAsync("scope-a", default);

        var create = Assert.Single(_plane.Requests, request => request.Method == "PUT");
        Assert.Equal($"https://management.azuredevcompute.io{Group}/sandboxes?api-version=2026-02-01-preview", create.Uri);
        Assert.Equal("Bearer token-for-https://dynamicsessions.io/.default", create.Authorization);
        var body = JsonDocument.Parse(create.Body!).RootElement;
        Assert.Equal("disk-sandboxhost", body.GetProperty("sourcesRef").GetProperty("diskImage").GetProperty("id").GetString());
        Assert.Equal("scope-a", body.GetProperty("labels").GetProperty(AcaSandboxProvisioner.ScopeLabel).GetString());
        Assert.Equal(binding.ApiKey, body.GetProperty("environment").GetProperty("Sandbox__ApiKey").GetString());
        var port = body.GetProperty("ports")[0];
        Assert.Equal(8080, port.GetProperty("port").GetInt32());
        Assert.Equal("OnDemand", port.GetProperty("activationMode").GetString());
        Assert.True(port.GetProperty("auth").GetProperty("anonymous").GetBoolean());
        Assert.False(port.TryGetProperty("ipAccessControl", out _));
        Assert.True(body.GetProperty("lifecycle").GetProperty("autoSuspendPolicy").GetProperty("enabled").GetBoolean());
        Assert.False(body.GetProperty("lifecycle").TryGetProperty("autoDeletePolicy", out _));

        Assert.Equal("https://sbx-1-8080.example/", binding.Endpoint.ToString());
        Assert.True(binding.ApiKey.Length >= 40);
        Assert.Equal(binding, await _bindings.GetAsync("scope-a", default));
    }

    [Fact]
    public async Task EachWorkspaceGetsAUniqueSandboxAndKey()
    {
        var provisioner = Provisioner();

        var first = await provisioner.AcquireAsync("scope-a", default);
        var second = await provisioner.AcquireAsync("scope-b", default);
        var again = await provisioner.AcquireAsync("scope-a", default);

        Assert.NotEqual(first.SandboxId, second.SandboxId);
        Assert.NotEqual(first.ApiKey, second.ApiKey);
        Assert.Equal(first, again);
        Assert.Equal(2, _plane.Requests.Count(request => request.Method == "PUT"));
    }

    [Fact]
    public async Task AStoppedSandboxIsResumedWithItsDisk()
    {
        var provisioner = Provisioner();
        var binding = await provisioner.AcquireAsync("scope-a", default);
        _plane.Stop(binding.SandboxId!);

        var resumed = await provisioner.AcquireAsync("scope-a", default);

        Assert.Equal(binding.SandboxId, resumed.SandboxId);
        Assert.Contains(_plane.Requests, request => request.Method == "POST" && request.Uri.Contains($"/sandboxes/{binding.SandboxId}/resume"));
        Assert.Single(_plane.Requests, request => request.Method == "PUT");
    }

    [Fact]
    public async Task ADeletedSandboxIsReplaced()
    {
        var provisioner = Provisioner();
        var binding = await provisioner.AcquireAsync("scope-a", default);
        _plane.Delete(binding.SandboxId!);

        var replaced = await provisioner.AcquireAsync("scope-a", default);

        Assert.NotEqual(binding.SandboxId, replaced.SandboxId);
        Assert.Equal(replaced, await _bindings.GetAsync("scope-a", default));
    }

    [Fact]
    public async Task ADisabledSandboxIsNotResumed()
    {
        var provisioner = Provisioner();
        var binding = await provisioner.AcquireAsync("scope-a", default);
        _plane.Stop(binding.SandboxId!, reason: "Disabled");

        var error = await Assert.ThrowsAsync<AgentWorkspaceUnavailableException>(() => provisioner.AcquireAsync("scope-a", default));

        Assert.Contains("disabled", error.Message);
        Assert.DoesNotContain(_plane.Requests, request => request.Uri.Contains("/resume"));
    }

    [Fact]
    public async Task NewerApiVersionsThatCreateWithPostAreFollowed()
    {
        _plane.RejectPut = true;

        var binding = await Provisioner(options => options.ApiVersion = "2026-09-01-preview").AcquireAsync("scope-a", default);

        Assert.NotNull(binding.SandboxId);
        Assert.Contains(_plane.Requests, request => request.Method == "POST" && request.Uri.EndsWith("/sandboxes?api-version=2026-09-01-preview"));
    }

    [Fact]
    public async Task WhenAnotherInstanceBindsFirstTheSurplusSandboxIsDeleted()
    {
        var winner = new SandboxBinding("sbx-winner", new Uri("https://winner.example/"), "winner-key");
        _bindings.BeforeAdd = scope => _bindings.Put(scope, winner);

        var binding = await Provisioner().AcquireAsync("scope-a", default);

        Assert.Equal(winner, binding);
        Assert.Single(_plane.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task ThePortCanBeLimitedToTheApplicationsAddresses()
    {
        await Provisioner(options =>
        {
            options.AllowedSourceCidrs = ["20.1.2.0/24"];
            options.AutoDeleteAfterDays = 7;
        }).AcquireAsync("scope-a", default);

        var body = JsonDocument.Parse(_plane.Requests.Single(request => request.Method == "PUT").Body!).RootElement;
        var acl = body.GetProperty("ports")[0].GetProperty("ipAccessControl");
        Assert.Equal("Deny", acl.GetProperty("defaultAction").GetString());
        Assert.Equal("20.1.2.0/24", acl.GetProperty("rules")[0].GetProperty("sourceCidrs")[0].GetString());
        Assert.Equal(7 * 86400, body.GetProperty("lifecycle").GetProperty("autoDeletePolicy").GetProperty("deleteIntervalInSeconds").GetInt32());
    }

    [Fact]
    public async Task ARefusedDataPlaneReportsTheRoleNotTheDetails()
    {
        _plane.Forbid = true;

        var error = await Assert.ThrowsAsync<AgentWorkspaceUnavailableException>(() => Provisioner().AcquireAsync("scope-a", default));

        Assert.Contains("SandboxGroup Data Owner", error.Message);
    }

    [Fact]
    public async Task ASharedDevelopmentSandboxSkipsProvisioning()
    {
        var binding = await Provisioner(options =>
        {
            options.SharedEndpoint = "https://shared.example/";
            options.SharedApiKey = "shared";
        }).AcquireAsync("scope-a", default);

        Assert.Null(binding.SandboxId);
        Assert.Empty(_plane.Requests);
    }

    [Fact]
    public async Task ReleasingAWorkspaceDeletesItsSandboxAndTheNextUseCreatesAnother()
    {
        var provisioner = Provisioner();
        var binding = await provisioner.AcquireAsync("scope-a", default);

        await provisioner.ReleaseAsync("scope-a", default);

        Assert.Contains(_plane.Requests, request => request.Method == "DELETE" && request.Uri.Contains($"/sandboxes/{binding.SandboxId}?"));
        Assert.Null(await _bindings.GetAsync("scope-a", default));
        var replacement = await provisioner.AcquireAsync("scope-a", default);
        Assert.NotEqual(binding.SandboxId, replacement.SandboxId);
        Assert.NotEqual(binding.ApiKey, replacement.ApiKey);
    }

    private sealed record RecordedRequest(string Method, string Uri, string? Authorization, string? Body);

    /// <summary>Creates sandboxes that report Creating once, then Running with their port URL.</summary>
    private sealed class FakeDataPlane : HttpMessageHandler
    {
        private readonly Dictionary<string, (string State, string? Reason, int Polls)> _sandboxes = [];
        private int _next;

        public List<RecordedRequest> Requests { get; } = [];

        public bool RejectPut { get; set; }

        public bool Forbid { get; set; }

        public void Stop(string id, string reason = "Idle") => _sandboxes[id] = ("Stopped", reason, 0);

        public void Delete(string id) => _sandboxes.Remove(id);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            if (Forbid)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":\"secret detail\"}") };
            }

            var path = request.RequestUri.AbsolutePath;
            if (path == $"{Group}/sandboxes")
            {
                if (request.Method == HttpMethod.Put && RejectPut)
                {
                    return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
                }

                var id = $"sbx-{++_next}";
                _sandboxes[id] = ("Creating", null, 0);
                return Json(HttpStatusCode.Created, Resource(id));
            }

            var parts = path[(Group.Length + "/sandboxes/".Length)..].Split('/');
            var sandboxId = parts[0];
            if (!_sandboxes.TryGetValue(sandboxId, out var sandbox))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Delete)
            {
                _sandboxes.Remove(sandboxId);
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            if (parts.Length == 2 && parts[1] == "resume")
            {
                _sandboxes[sandboxId] = ("Resuming", null, 0);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            // Each poll moves a starting sandbox along to Running.
            if (sandbox.State is "Creating" or "Resuming")
            {
                _sandboxes[sandboxId] = sandbox.Polls >= 0 ? ("Running", null, 0) : sandbox with { Polls = sandbox.Polls + 1 };
            }
            return Json(HttpStatusCode.OK, Resource(sandboxId));
        }

        private object Resource(string id)
        {
            var (state, reason, _) = _sandboxes[id];
            return new
            {
                id,
                state,
                stateDetails = reason is null ? null : new { stoppedReason = reason },
                ports = state == "Running" ? new[] { new { port = 8080, url = $"https://{id}-8080.example/" } } : []
            };
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }

    private sealed class InMemoryBindings : ISandboxRegistry
    {
        private readonly Dictionary<string, SandboxBinding> _bindings = [];

        public Action<string>? BeforeAdd { get; set; }

        public void Put(string scope, SandboxBinding binding) => _bindings[scope] = binding;

        public Task<SandboxBinding?> GetAsync(string scope, CancellationToken cancellationToken) =>
            Task.FromResult(_bindings.GetValueOrDefault(scope));

        public Task<bool> TryAddAsync(string scope, SandboxBinding binding, CancellationToken cancellationToken)
        {
            BeforeAdd?.Invoke(scope);
            return Task.FromResult(_bindings.TryAdd(scope, binding));
        }

        public Task SaveAsync(string scope, SandboxBinding binding, CancellationToken cancellationToken)
        {
            _bindings[scope] = binding;
            return Task.CompletedTask;
        }

        public Task DeleteBindingAsync(string scope, CancellationToken cancellationToken)
        {
            _bindings.Remove(scope);
            return Task.CompletedTask;
        }
    }
}

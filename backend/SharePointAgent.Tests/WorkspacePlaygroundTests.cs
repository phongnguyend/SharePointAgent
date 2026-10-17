using System.Net;
using System.Net.Http.Json;
using Azure.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.Api;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.Workspaces;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class WorkspacePlaygroundTests
{
    [Theory]
    [InlineData(AppRoles.User)]
    [InlineData(AppRoles.GlobalReaderAdmin)]
    public async Task EveryOperationRequiresGlobalAdmin(string role)
    {
        var playground = Substitute.For<IWorkspacePlayground>();
        await using var app = await StartAsync(playground, role);
        using var client = app.GetTestClient();
        var paths = new[]
        {
            (HttpMethod.Get, "instances", (object?)null),
            (HttpMethod.Post, "instances", null),
            (HttpMethod.Post, "instances/target/actions/delete", null),
            (HttpMethod.Get, "instances/target/files", null),
            (HttpMethod.Get, "instances/target/files/content?path=test.txt", null),
            (HttpMethod.Post, "instances/target/files", new WorkspaceFileChange("mkdir", "folder")),
            (HttpMethod.Put, "instances/target/files/text", new { path = "file.txt", content = "test" }),
            (HttpMethod.Post, "instances/target/files/archive", new { operation = "zip", paths = new[] { "a" }, destination = "a.zip" }),
            (HttpMethod.Post, "instances/target/executions", Execution())
        };
        foreach (var provider in new[] { "dynamic-sessions", "sandboxes", "foundry-sessions" })
        {
            foreach (var (method, path, body) in paths)
            {
                using var request = new HttpRequestMessage(method, $"/api/admin/workspace-playground/{provider}/{path}");
                if (body is not null)
                {
                    request.Content = JsonContent.Create(body);
                }
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.True(response.Headers.CacheControl!.NoStore);
            }
        }
        Assert.Empty(playground.ReceivedCalls());
    }

    [Theory]
    [InlineData("dynamic-sessions")]
    [InlineData("sandboxes")]
    [InlineData("foundry-sessions")]
    public async Task FileArchivesAndExecutionRouteToSelectedInstance(string provider)
    {
        var playground = Substitute.For<IWorkspacePlayground>();
        var workspace = Substitute.For<IAgentWorkspace>();
        playground.OpenAsync(provider, "selected-123", Arg.Any<CancellationToken>()).Returns(workspace);
        workspace.ExecuteAsync(Arg.Any<WorkspaceExecution>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceExecutionResult(7, false, "output", "error"));
        workspace.ZipAsync(Arg.Any<IReadOnlyList<string>>(), "bundle.zip", false, Arg.Any<CancellationToken>())
            .Returns(new FileSystemEntry("bundle.zip", false, 10, DateTimeOffset.UtcNow));
        workspace.UnzipAsync("bundle.zip", "expanded", false, Arg.Any<CancellationToken>())
            .Returns(new FileSystemEntry("expanded", true, null, DateTimeOffset.UtcNow));
        workspace.ReadAsync("bundle.zip", Arg.Any<CancellationToken>(), null)
            .Returns(new FileContent("bundle.zip", "bundle.zip", "application/zip", [1, 2, 3]));
        await using var app = await StartAsync(playground);
        using var client = app.GetTestClient();
        var root = $"/api/admin/workspace-playground/{provider}/instances/selected-123";

        using var zip = await client.PostAsJsonAsync(root + "/files/archive", new { operation = "zip", paths = new[] { "folder", "test.py" }, destination = "bundle.zip" });
        Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
        await workspace.Received(1).ZipAsync(Arg.Is<IReadOnlyList<string>>(paths => paths.SequenceEqual(new[] { "folder", "test.py" })), "bundle.zip", false, Arg.Any<CancellationToken>());

        using var unzip = await client.PostAsJsonAsync(root + "/files/archive", new { operation = "unzip", paths = new[] { "bundle.zip" }, destination = "expanded" });
        Assert.Equal(HttpStatusCode.OK, unzip.StatusCode);
        await workspace.Received(1).UnzipAsync("bundle.zip", "expanded", false, Arg.Any<CancellationToken>());

        using var download = await client.GetAsync(root + "/files/content?path=bundle.zip");
        Assert.Equal(new byte[] { 1, 2, 3 }, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);

        using var execution = await client.PostAsJsonAsync(root + "/executions", Execution());
        Assert.Equal(HttpStatusCode.OK, execution.StatusCode);
        var result = await execution.Content.ReadFromJsonAsync<WorkspaceExecutionResult>();
        Assert.Equal(7, result!.ExitCode);
        Assert.Equal("output", result.Stdout);
        await workspace.Received(1).ExecuteAsync(Arg.Is<WorkspaceExecution>(request => request.Code == "print('hello')" && request.Language == "python"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, "python", "print(1)", null)]
    [InlineData(221, "python", "print(1)", null)]
    [InlineData(60, "invalid", "print(1)", null)]
    [InlineData(60, "python", "print(1)", "test.py")]
    [InlineData(60, "python", null, null)]
    public async Task InvalidExecutionNeverOpensInstance(int timeout, string language, string? code, string? script)
    {
        var playground = Substitute.For<IWorkspacePlayground>();
        await using var app = await StartAsync(playground);
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/admin/workspace-playground/dynamic-sessions/instances/target/executions",
            new WorkspaceExecution(language, code, script, [], null, timeout));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(playground.ReceivedCalls());
    }

    [Fact]
    public async Task InvalidArchiveNeverOpensInstance()
    {
        var playground = Substitute.For<IWorkspacePlayground>();
        await using var app = await StartAsync(playground);
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/admin/workspace-playground/sandboxes/instances/target/files/archive",
            new { operation = "unzip", paths = new[] { "one.zip", "two.zip" }, destination = "output" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(playground.ReceivedCalls());
    }

    [Fact]
    public async Task DynamicSessionListingFollowsSafePaginationWithoutLeakingMetadata()
    {
        var calls = new List<Uri>();
        using var http = new HttpClient(new Handler(request =>
        {
            calls.Add(request.RequestUri!);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return calls.Count == 1
                ? Json("""{"value":[{"identifier":"first","createdAt":"2026-01-01T00:00:00Z","etag":"private"}],"nextLink":"https://pool.example/.management/listSessions?skip=1"}""")
                : Json("""{"value":[{"identifier":"second"}],"nextLink":null}""");
        }));
        var service = Service(http);
        var listing = await service.ListAsync("dynamic-sessions", default);
        Assert.Equal(new[] { "first", "second" }, listing.Instances.Select(instance => instance.Id));
        Assert.Equal(2, calls.Count);
        Assert.Contains("api-version=2025-02-02-preview", calls[1].Query);
        Assert.False(listing.Truncated);
    }

    [Theory]
    [InlineData("https://evil.example/.management/listSessions?skip=1")]
    [InlineData("http://pool.example/.management/listSessions?skip=1")]
    [InlineData("https://pool.example/different-path")]
    public void ContinuationsCannotForwardCredentialsElsewhere(string link)
    {
        Assert.Throws<InvalidDataException>(() => WorkspacePlayground.ValidateContinuation(
            new Uri("https://pool.example/.management/listSessions"), link));
    }

    [Fact]
    public async Task MissingSessionDoesNotIssueHostRequestOrCreateNewSession()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service(http).OpenAsync("dynamic-sessions", "missing", default));
        Assert.Equal(new[] { "/.management/getSession" }, requests);
    }

    [Fact]
    public async Task SandboxArrayListingReportsUnboundInstancesWithoutCredentials()
    {
        using var http = new HttpClient(new Handler(_ => Json("""[{"id":"sandbox-1","state":"Running","environment":{"Sandbox__ApiKey":"secret"}}]""")));
        var listing = await Service(http).ListAsync("sandboxes", default);
        Assert.Equal("sandbox-1", Assert.Single(listing.Instances).Id);
        Assert.False(listing.Instances[0].CanAccessFiles);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(listing));
    }

    [Fact]
    public async Task FailedSandboxDeletePreservesBindingForRetry()
    {
        using var http = new HttpClient(new Handler(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Json("""{"id":"sandbox-1","state":"Running","labels":{"sharepointagent-scope":"scope-1"}}""")));
        var bindings = Substitute.For<ISandboxRegistry>();
        await Assert.ThrowsAsync<AgentWorkspaceUnavailableException>(() => Service(http, bindings).ChangeStateAsync("sandboxes", "sandbox-1", "delete", default));
        await bindings.DidNotReceive().DeleteBindingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SandboxStopUsesStopEndpointAndRetainsBinding()
    {
        var paths = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post)
            {
                Assert.EndsWith("/sandboxes/target/stop", request.RequestUri.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return Json("""{"id":"target","state":"Running"}""");
        }));
        var bindings = Substitute.For<ISandboxRegistry>();
        await Service(http, bindings).ChangeStateAsync("sandboxes", "target", "stop", default);
        Assert.Equal(2, paths.Count);
        await bindings.DidNotReceive().DeleteBindingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunningUnboundSandboxCanBeResumedWithoutRequiringHostPort()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            requests++;
            return Json("""{"id":"target","state":"Running"}""");
        }));
        await Service(http).ChangeStateAsync("sandboxes", "target", "resume", default);
        Assert.Equal(1, requests);
    }

    private static WorkspaceExecution Execution() => new("python", "print('hello')", null, [], "", 60);

    private static async Task<WebApplication> StartAsync(IWorkspacePlayground playground, string role = AppRoles.GlobalAdmin)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(playground);
        var links = Substitute.For<IWorkspaceInstanceLinks>();
        links.FindLinkedAsync(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>());
        builder.Services.AddSingleton(links);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Items[typeof(AppUserView)] = new AppUserView(Guid.Empty, "admin@example.test", "Admin", [role], true, true, DateTimeOffset.UtcNow, null, "");
            return next(context);
        });
        app.MapWorkspacePlaygroundEndpoints();
        await app.StartAsync();
        return app;
    }

    private static WorkspacePlayground Service(HttpClient http, ISandboxRegistry? registry = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        var settings = new AgentWorkspaceOptions
        {
            DynamicSessions = new() { PoolManagementEndpoint = "https://pool.example" },
            Sandboxes = new() { SubscriptionId = "subscription", ResourceGroup = "group", SandboxGroup = "sandbox-group", DiskImageId = "image" }
        };
        return new WorkspacePlayground(factory, Options.Create(settings), Options.Create(new LocalWorkingDirectoryOptions()),
            new Credential(), () => registry ?? new NoSandboxRegistry(), NullLoggerFactory.Instance);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}

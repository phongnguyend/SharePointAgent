using System.Net;
using System.Net.Http.Json;
using Azure.Core;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using SharePointAgent.Infrastructure.Workspaces;
using SharePointAgent.AgentHost;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class FoundryWorkspacePlaygroundTests
{
    [Fact]
    public async Task ListsCreatesAndStopsSessionsOnConfiguredAgentOnly()
    {
        var paths = new List<string>();
        var client = Create(request =>
        {
            paths.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            var session = new { agent_session_id = "session-1", status = "Active" };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.Method == HttpMethod.Get ? JsonContent.Create(new { data = new[] { session } }) : JsonContent.Create(session)
            };
        });
        var list = await client.ListAsync(default);
        Assert.Equal("Running", Assert.Single(list.Instances).State);
        Assert.Equal("session-1", (await client.CreateAsync(default)).Id);
        await client.ChangeStateAsync("session-1", "stop", default);
        Assert.Equal("GET /api/projects/project/agents/agent/endpoint/sessions?api-version=v1", paths[0]);
        Assert.EndsWith("/session-1:stop?api-version=v1", paths[2]);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ChangeStateAsync("../other", "delete", default));
        Assert.Equal(3, paths.Count);
    }

    [Fact]
    public async Task RejectsForeignContinuationBeforeSendingCredentials()
    {
        var calls = 0;
        var client = Create(_ =>
        {
            calls++;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = Array.Empty<object>(), nextLink = "https://other.invalid/sessions" }) };
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync(default));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("session-1", false)]
    [InlineData("other-session", true)]
    [InlineData(null, true)]
    public async Task InvocationPinsAndVerifiesSession(string? returned, bool rejected)
    {
        var client = Create(request =>
        {
            Assert.Contains("agent_session_id=session-1", request.RequestUri!.Query);
            Assert.Equal(FoundryWorkspacePlayground.Operation, request.Headers.GetValues(AgentInvocation.OperationHeader).Single());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { path = "." }) };
            if (returned is not null)
            {
                response.Headers.Add("x-agent-session-id", returned);
            }
            return response;
        });
        if (rejected)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => client.InvokeAsync<object>("session-1", new("list"), default));
        }
        else
        {
            Assert.NotNull(await client.InvokeAsync<object>("session-1", new("list"), default));
        }
    }

    [Fact]
    public async Task HostWritesArchivesAndReadsWithinWorkingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "foundry-workspace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new AgentFileSystem(Options.Create(new LocalWorkingDirectoryOptions { Directory = root }));
            await FoundryWorkspaceOperations.RunAsync(files, new("text", Path: "folder/test.txt", Content: "hello"), default);
            await FoundryWorkspaceOperations.RunAsync(files, new("zip", Paths: ["folder"], Destination: "files.zip"), default);
            await FoundryWorkspaceOperations.RunAsync(files, new("unzip", Path: "files.zip", Destination: "expanded"), default);
            var read = Assert.IsType<FileContent>(await FoundryWorkspaceOperations.RunAsync(files, new("read", Path: "expanded/folder/test.txt"), default));
            Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(read.Content));
            var execution = new WorkspaceExecution("python", "import os,sys; print(os.path.basename(os.getcwd())); print(sys.argv[1]); print('err', file=sys.stderr)", null, ["argument with spaces"], "folder", 10);
            var result = Assert.IsType<WorkspaceExecutionResult>(await FoundryWorkspaceOperations.RunAsync(files, new("execute", Execution: execution), default));
            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.Contains("folder", result.Stdout);
            Assert.Contains("argument with spaces", result.Stdout);
            Assert.Contains("err", result.Stderr);
            var timeout = Assert.IsType<WorkspaceExecutionResult>(await FoundryWorkspaceOperations.RunAsync(files,
                new("execute", Execution: execution with { Code = "import time; time.sleep(30)", TimeoutSeconds = 1 }), default));
            Assert.True(timeout.TimedOut);
            Assert.Empty(Directory.GetFiles(root, ".agent-execution-*"));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => FoundryWorkspaceOperations.RunAsync(files, new("text", Path: "../../outside.txt", Content: "bad"), default));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static FoundryWorkspacePlayground Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(new Handler(respond)));
        return new(factory, new Credential(), Options.Create(new ChatAgentHostingOptions
        {
            Foundry = new() { Endpoint = "https://account.services.ai.azure.com/api/projects/project/agents/agent/endpoint/protocols/invocations?api-version=v1" }
        }));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) => new("test", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) => ValueTask.FromResult(GetToken(context, ct));
    }
}

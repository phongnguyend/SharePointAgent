using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.AI.AgentServer.Invocations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.AgentHost;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ChatAgentStreamingTests
{
    private static readonly ChatTurn Turn = new("Answer", [new("File", null, "https://example.com/file", 1, 0.9)], new(10, 5, 15, 7), "model");

    [Fact]
    public async Task ActualInvocationsHostStreamsStatusBeforeCompletionAndPreservesTurnMetadata()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new ChatAgentRequest(Guid.NewGuid(), Guid.NewGuid());
        var executor = new StubExecutor(async (input, text, status, ct) =>
        {
            Assert.Equal(request, input);
            await status("Searching documents…", ct);
            await release.Task.WaitAsync(ct);
            await text("Answer", ct);
            return Turn;
        });
        await using var server = await CreateServer(executor);
        using var http = server.GetTestClient();
        var sessions = EmptySessions();
        var proxy = new FoundryChatAgentExecutor(http, new TestCredential(), sessions, Options.Create(new ChatAgentHostingOptions
        {
            Mode = ChatAgentExecutionMode.Foundry,
            Foundry = new() { Endpoint = "http://localhost/invocations" },
        }));
        var textReceived = new StringBuilder();
        var running = proxy.RunStreamingAsync(request,
            (text, ct) =>
{
    textReceived.Append(text);
    return ValueTask.CompletedTask;
},
            (status, ct) =>
{
    Assert.Equal("Searching documents…", status);
    observed.SetResult();
    return ValueTask.CompletedTask;
}, default);
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(running.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Turn.Text, result.Text);
        Assert.Equal(Turn.Citations, result.Citations);
        Assert.Equal(Turn.Usage, result.Usage);
        Assert.Equal(Turn.ModelId, result.ModelId);
        Assert.Equal("Answer", textReceived.ToString());
        await sessions.Received(1).SaveAsync(request.ConversationId, "http://localhost/invocations", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendsOnlyIdentifiersAndReusesSavedSandboxWithBearerAuthentication()
    {
        var request = new ChatAgentRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        var sessions = Substitute.For<IFoundrySessionRepository>();
        const string endpoint = "https://example.com/invocations?api-version=v1";
        sessions.GetAsync(request.ConversationId, endpoint, Arg.Any<CancellationToken>()).Returns("saved-session");
        using var http = new HttpClient(new StubHttpHandler(async (message, ct) =>
        {
            Assert.Contains("api-version=v1&agent_session_id=saved-session", message.RequestUri!.Query);
            Assert.Equal("Bearer test-token", message.Headers.Authorization!.ToString());
            using var json = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
            // Identifiers plus the turn's start time, which the sandbox needs so the usage rows it
            // writes land in the day and month the API checked the quota against. Nothing else.
            Assert.Equal(4, json.RootElement.EnumerateObject().Count());
            Assert.Equal(request.QuestionId, json.RootElement.GetProperty("questionId").GetGuid());
            Assert.Equal(request.UserId, json.RootElement.GetProperty("userId").GetGuid());
            Assert.Equal(request.StartedAtUtc, json.RootElement.GetProperty("startedAtUtc").GetDateTimeOffset());
            return Response(new("completed", Turn: Turn), "saved-session");
        }));
        var credential = new TestCredential();
        var result = await Proxy(http, sessions, endpoint, credential).RunStreamingAsync(request, Ignore, Ignore, default);
        Assert.Equal(Turn.Text, result.Text);
        Assert.Equal("https://ai.azure.com/.default", Assert.Single(credential.Scopes!));
    }

    [Theory]
    [InlineData("{\"type\":\"status\",\"message\":\"Working\"}\n", typeof(EndOfStreamException))]
    [InlineData("{\"type\":\"error\",\"message\":\"Tool failed\"}\n", typeof(InvalidOperationException))]
    [InlineData("{\"type\":\"completed\"}\n", typeof(InvalidDataException))]
    [InlineData("not-json\n", typeof(JsonException))]
    public async Task FailedOrTruncatedStreamsNeverReturnACompletedTurn(string body, Type errorType)
    {
        var calls = 0;
        using var http = new HttpClient(new StubHttpHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson") };
            response.Headers.Add("x-agent-session-id", "session");
            return Task.FromResult(response);
        }));
        var error = await Record.ExceptionAsync(() => Proxy(http).RunStreamingAsync(new(Guid.NewGuid(), Guid.NewGuid()), Ignore, Ignore, default));
        Assert.IsAssignableFrom(errorType, error);
        Assert.Equal(1, calls); // No automatic replay of file edits/uploads.
    }

    [Fact]
    public async Task CallerCancellationReachesTheHostedExecutor()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await CreateServer(new StubExecutor(async (_, _, status, ct) =>
        {
            try
            {
                await status("Working", ct);
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return Turn;
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }
        }));
        using var http = server.GetTestClient();
        using var cts = new CancellationTokenSource();
        var running = Proxy(http, endpoint: "http://localhost/invocations")
            .RunStreamingAsync(new(Guid.NewGuid(), Guid.NewGuid()), Ignore, Ignore, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task HostReportsFailuresAsErrorEvents()
    {
        await using var server = await CreateServer(new StubExecutor((_, _, _, _) => throw new InvalidOperationException("private details")));
        using var http = server.GetTestClient();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Proxy(http, endpoint: "http://localhost/invocations").RunStreamingAsync(new(Guid.NewGuid(), Guid.NewGuid()), Ignore, Ignore, default));
        Assert.Equal("The hosted agent could not complete the turn.", error.Message);
    }

    [Fact]
    public async Task TheHostListsItsWorkingDirectoryWithoutRunningATurn()
    {
        var root = Path.Combine(Path.GetTempPath(), "invocation-listing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Downloads", "SharePoint", "item-1"));
        await File.WriteAllTextAsync(Path.Combine(root, "Downloads", "SharePoint", "item-1", "report.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(root, "summary.md"), "notes");

        // An executor that fails if it is reached: a listing must never become a model turn.
        var executor = new StubExecutor((_, _, _, _) => throw new InvalidOperationException("the model was called"));
        await using var server = await CreateServer(executor, root);
        using var http = server.GetTestClient();
        var sessions = Substitute.For<IFoundrySessionRepository>();
        sessions.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("sandbox-1");
        var browser = new FoundryAgentFileBrowser(http, new TestCredential(), sessions,
            Options.Create(new ChatAgentHostingOptions
            {
                Mode = ChatAgentExecutionMode.Foundry,
                Foundry = new() { Endpoint = "http://localhost/invocations" },
            }));

        var listing = await browser.ListAsync(Guid.NewGuid(), null, recursive: false, default);

        Assert.True(listing.SandboxStarted);
        Assert.Equal(["Downloads", "summary.md"], listing.Entries.Select(e => e.Path));

        var nested = await browser.ListAsync(Guid.NewGuid(), "Downloads/SharePoint/item-1", recursive: false, default);
        Assert.Equal(["Downloads/SharePoint/item-1/report.txt"], nested.Entries.Select(e => e.Path));

        // A path outside the sandbox comes back as a refusal the caller can show, not a crash.
        await Assert.ThrowsAsync<ArgumentException>(() => browser.ListAsync(Guid.NewGuid(), "../..", false, default));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task TheOperationHeaderIsWhatIdentifiesAListing()
    {
        var root = Path.Combine(Path.GetTempPath(), "invocation-header-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "summary.md"), "notes");
        var executor = new StubExecutor((_, _, _, _) => throw new InvalidOperationException("the model was called"));
        await using var server = await CreateServer(executor, root);
        using var http = server.GetTestClient();

        using var message = new HttpRequestMessage(HttpMethod.Post, "http://localhost/invocations")
        {
            Content = JsonContent.Create(new { conversationId = Guid.NewGuid(), recursive = false }),
        };
        message.Headers.Add(AgentInvocation.OperationHeader, AgentInvocation.ListFilesOperation);

        using var response = await http.SendAsync(message);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listing = await response.Content.ReadFromJsonAsync<FileSystemListing>(ChatStreamWriter<FileSystemListing>.Json);
        Assert.Equal(["summary.md"], listing!.Entries.Select(e => e.Path));

        // Without the header the same body is a turn, and an incomplete one, so it is refused rather
        // than listed. That is the failure a gateway stripping the header would produce.
        using var unmarked = new HttpRequestMessage(HttpMethod.Post, "http://localhost/invocations")
        {
            Content = JsonContent.Create(new { conversationId = Guid.NewGuid(), recursive = false }),
        };
        using var refused = await http.SendAsync(unmarked);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task TheHostSendsOneFileBackWithoutRunningATurn()
    {
        var root = Path.Combine(Path.GetTempPath(), "invocation-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Downloads", "SharePoint", "item-1"));
        await File.WriteAllTextAsync(Path.Combine(root, "Downloads", "SharePoint", "item-1", "report.md"), "# Report");

        var executor = new StubExecutor((_, _, _, _) => throw new InvalidOperationException("the model was called"));
        await using var server = await CreateServer(executor, root);
        using var http = server.GetTestClient();
        var sessions = Substitute.For<IFoundrySessionRepository>();
        sessions.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("sandbox-1");
        var browser = new FoundryAgentFileBrowser(http, new TestCredential(), sessions,
            Options.Create(new ChatAgentHostingOptions
            {
                Mode = ChatAgentExecutionMode.Foundry,
                Foundry = new() { Endpoint = "http://localhost/invocations" },
            }));

        var file = await browser.ReadAsync(Guid.NewGuid(), "Downloads/SharePoint/item-1/report.md", default);

        Assert.Equal("report.md", file.Name);
        Assert.Equal("text/markdown", file.ContentType);
        Assert.Equal("# Report", Encoding.UTF8.GetString(file.Content));

        // The same refusals a listing gives, so a reader never receives someone else's file.
        await Assert.ThrowsAsync<ArgumentException>(() => browser.ReadAsync(Guid.NewGuid(), "../escaped.txt", default));
        await Assert.ThrowsAsync<ArgumentException>(() => browser.ReadAsync(Guid.NewGuid(), "Downloads", default));
        await Assert.ThrowsAsync<ArgumentException>(() => browser.ReadAsync(Guid.NewGuid(), "missing.txt", default));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ReadingBeforeTheFirstTurnSaysThereIsNoSandbox()
    {
        var handler = new ThrowingHandler();
        using var http = new HttpClient(handler);
        var browser = new FoundryAgentFileBrowser(http, new TestCredential(), EmptySessions(),
            Options.Create(new ChatAgentHostingOptions
            {
                Mode = ChatAgentExecutionMode.Foundry,
                Foundry = new() { Endpoint = "https://example.com/invocations" },
            }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => browser.ReadAsync(Guid.NewGuid(), "notes.md", default));
        Assert.False(handler.Called);
    }

    [Fact]
    public async Task HostedFileManagementUsesExistingSessionWithoutCallingModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "invocation-manage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executor = new StubExecutor((_, _, _, _) => throw new InvalidOperationException("the model was called"));
            await using var server = await CreateServer(executor, root);
            using var http = server.GetTestClient();
            var sessions = Substitute.For<IFoundrySessionRepository>();
            sessions.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("sandbox-1");
            var browser = new FoundryAgentFileBrowser(http, new TestCredential(), sessions,
                Options.Create(new ChatAgentHostingOptions
                {
                    Mode = ChatAgentExecutionMode.Foundry,
                    Foundry = new() { Endpoint = "http://localhost/invocations" }
                }));
            var conversation = Guid.NewGuid();
            await browser.ManageAsync(conversation, new("mkdir", "folder"), default);
            await browser.ManageAsync(conversation, new("upload", "folder/data.bin", Content: [1, 2, 255]), default);
            await browser.ManageAsync(conversation, new("copy", "folder", "copy"), default);
            await browser.ManageAsync(conversation, new("rename", "copy", "renamed"), default);
            await browser.ManageAsync(conversation, new("move", "renamed", "folder/renamed"), default);
            Assert.Equal(new byte[] { 1, 2, 255 }, (await browser.ReadAsync(conversation, "folder/renamed/data.bin", default)).Content);
            await Assert.ThrowsAsync<ArgumentException>(() => browser.ManageAsync(conversation, new("delete", "../outside"), default));
            await browser.ManageAsync(conversation, new("delete", "folder"), default);
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FileManagementDoesNotCreateANewSandbox()
    {
        var handler = new ThrowingHandler();
        using var http = new HttpClient(handler);
        var browser = new FoundryAgentFileBrowser(http, new TestCredential(), EmptySessions(),
            Options.Create(new ChatAgentHostingOptions
            {
                Mode = ChatAgentExecutionMode.Foundry,
                Foundry = new() { Endpoint = "https://example.com/invocations" }
            }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => browser.ManageAsync(Guid.NewGuid(), new("mkdir", "folder"), default));
        Assert.False(handler.Called);
    }

    [Fact]
    public async Task FileManagementPinsSessionAndRejectsADifferentResponseSession()
    {
        using var http = new HttpClient(new StubHttpHandler(async (request, ct) =>
        {
            Assert.Contains("agent_session_id=sandbox-1", request.RequestUri!.Query);
            Assert.Equal(AgentInvocation.ManageFilesOperation, request.Headers.GetValues(AgentInvocation.OperationHeader).Single());
            var payload = await request.Content!.ReadFromJsonAsync<AgentFileChangeRequest>(cancellationToken: ct);
            Assert.Equal("mkdir", payload!.Change.Operation);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SandboxFileChangeResult("folder"))
            };
            response.Headers.Add("x-agent-session-id", "other-session");
            return response;
        }));
        var sessions = Substitute.For<IFoundrySessionRepository>();
        sessions.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("sandbox-1");
        var browser = new FoundryAgentFileBrowser(http, new TestCredential(), sessions,
            Options.Create(new ChatAgentHostingOptions
            {
                Mode = ChatAgentExecutionMode.Foundry,
                Foundry = new() { Endpoint = "https://example.com/invocations" }
            }));
        await Assert.ThrowsAsync<InvalidDataException>(() => browser.ManageAsync(Guid.NewGuid(), new("mkdir", "folder"), default));
    }

    [Fact]
    public async Task ListingBeforeTheFirstTurnDoesNotStartASandbox()
    {
        var handler = new ThrowingHandler();
        using var http = new HttpClient(handler);
        var browser = new FoundryAgentFileBrowser(http, new TestCredential(), EmptySessions(),
            Options.Create(new ChatAgentHostingOptions
            {
                Mode = ChatAgentExecutionMode.Foundry,
                Foundry = new() { Endpoint = "https://example.com/invocations" },
            }));

        var listing = await browser.ListAsync(Guid.NewGuid(), null, false, default);

        Assert.False(listing.SandboxStarted);
        Assert.Empty(listing.Entries);
        Assert.False(handler.Called);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public bool Called { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Called = true;
            throw new InvalidOperationException("No request should be sent without a session.");
        }
    }

    [Fact]
    public async Task ConcurrentCallbacksProduceWholeJsonRecords()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        using var writer = new ChatStreamWriter<ChatAgentEvent>(context.Response);
        writer.Start();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => writer.WriteAsync(new("status", Message: i.ToString()), default).AsTask()));
        context.Response.Body.Position = 0;
        var lines = (await new StreamReader(context.Response.Body).ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(100, lines.Length);
        Assert.Equal(100, lines.Select(l => JsonSerializer.Deserialize<ChatAgentEvent>(l, ChatStreamWriter<ChatAgentEvent>.Json)!.Message).Distinct().Count());
    }

    private static async Task<WebApplication> CreateServer(IChatAgentExecutor executor, string? workingDirectory = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddInvocationsServer();
        builder.Services.AddSingleton(executor);

        // The handler answers directory listings itself, so it needs the working directory even in
        // the cases here that only run turns.
        builder.Services.AddSingleton(new AgentFileSystem(Options.Create(new LocalWorkingDirectoryOptions
        {
            Directory = workingDirectory ?? Path.Combine(Path.GetTempPath(), "invocation-tests-" + Guid.NewGuid().ToString("N")),
        })));
        builder.Services.AddScoped<InvocationHandler, ChatAgentInvocation>();
        var app = builder.Build();
        app.MapInvocationsServer();
        await app.StartAsync();
        return app;
    }

    private static IFoundrySessionRepository EmptySessions()
    {
        var sessions = Substitute.For<IFoundrySessionRepository>();
        sessions.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        return sessions;
    }

    private static FoundryChatAgentExecutor Proxy(HttpClient http, IFoundrySessionRepository? sessions = null,
        string endpoint = "https://example.com/invocations", TestCredential? credential = null) =>
        new(http, credential ?? new TestCredential(), sessions ?? EmptySessions(),
            Options.Create(new ChatAgentHostingOptions { Mode = ChatAgentExecutionMode.Foundry, Foundry = new() { Endpoint = endpoint } }));

    private static HttpResponseMessage Response(ChatAgentEvent item, string session)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(item, ChatStreamWriter<ChatAgentEvent>.Json) + "\n", Encoding.UTF8, "application/x-ndjson"),
        };
        response.Headers.Add("x-agent-session-id", session);
        return response;
    }

    private static ValueTask Ignore(string _, CancellationToken ct) => ValueTask.CompletedTask;

    private sealed class StubExecutor(Func<ChatAgentRequest, Func<string, CancellationToken, ValueTask>, Func<string, CancellationToken, ValueTask>, CancellationToken, Task<ChatTurn>> run) : IChatAgentExecutor
    {
        public Task<ChatTurn> RunStreamingAsync(ChatAgentRequest request, Func<string, CancellationToken, ValueTask> onText,
            Func<string, CancellationToken, ValueTask> onStatus, CancellationToken cancellationToken) => run(request, onText, onStatus, cancellationToken);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TestCredential : TokenCredential
    {
        public string[]? Scopes { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = requestContext.Scopes;
            return new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(GetToken(requestContext, cancellationToken));
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class MicrosoftDecisionClientTests
{
    [Fact]
    public async Task SendsContentLengthMatchingUtf8Body()
    {
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            var contentLength = request.Content!.Headers.ContentLength;
            Assert.NotNull(contentLength);
            var bytes = await request.Content.ReadAsByteArrayAsync(token);
            Assert.Equal((long)bytes.Length, contentLength.Value);
            Assert.NotEqual(true, request.Headers.TransferEncodingChunked);
            Assert.Equal("application/json", request.Content.Headers.ContentType!.MediaType);
            using var json = JsonDocument.Parse(bytes);
            Assert.Equal("Xin chào 世界", json.RootElement.GetProperty("state").GetString());
            return Response("""{"model":"decision","answers":{"check":{"type":"noul","noul":0.9}},"usage":{"input_tokens":10,"output_tokens":0}}""");
        }));
        await Client(http).EvaluateAsync("Xin chào 世界", Questions());
    }

    [Fact]
    public async Task LogsFoundryErrorWithoutExposingBodyInException()
    {
        const string body = "{\"error\":{\"message\":\"Unknown deployment Decision-1\"}}";
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body)
        })));
        var logger = new CapturingLogger();
        var client = new MicrosoftDecisionClient(http, Options.Create(new MicrosoftDecisionOptions
        {
            Endpoint = "https://decision.example/providers/microsoft/v1/systemone", ApiKey = "secret-key", Model = "Decision-1"
        }), logger: logger);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.EvaluateAsync("state", Questions()));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains(body, logger.Message);
        Assert.Contains("400", logger.Message);
        Assert.DoesNotContain("secret-key", logger.Message);
        Assert.DoesNotContain(body, error.Message);
    }

    private sealed class CapturingLogger : ILogger<MicrosoftDecisionClient>
    {
        public string Message { get; private set; } = "";

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Message = formatter(state, exception);
        }
    }

    [Theory]
    [InlineData("https://decision.example/providers/microsoft/v1/systemone")]
    [InlineData("https://decision.example/providers/microsoft/v1/systemone?api-version=test")]
    public async Task UsesConfiguredFullUrlWithoutAppendingPath(string endpoint)
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(endpoint, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response("""{"model":"decision","answers":{"check":{"type":"noul","noul":0.9}},"usage":{"input_tokens":10,"output_tokens":0}}"""));
        }));
        var client = new MicrosoftDecisionClient(http, Options.Create(new MicrosoftDecisionOptions
        {
            Endpoint = endpoint, ApiKey = "test-key"
        }));
        await client.EvaluateAsync("state", Questions());
    }

    [Fact]
    public async Task SendsMixedQuestionsAndReadsTypedAnswersAndUsage()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal("https://decision.example/providers/microsoft/v1/systemone", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("api-key")));
            Assert.Null(request.Headers.Authorization);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("decision-deployment", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("text", body.RootElement.GetProperty("state").GetProperty("content").GetString());
            Assert.Equal("choice", body.RootElement.GetProperty("questions").GetProperty("route").GetProperty("type").GetString());
            Assert.False(body.RootElement.GetProperty("questions").GetProperty("urgent").TryGetProperty("criteria", out _));
            return Response("""
                {"model":"jev-test","answers":{
                  "route":{"choice":"search","probabilities":{"search":1},"confidence":1,"type":"choice"},
                  "quality":{"type":"score","score":0.8,"legend":{"0":"low","1":"high"},"probabilities":{"0":0.2,"1":0.8},"confidence":0.7},
                  "urgent":{"type":"noul","noul":0.9}},"usage":{"input_tokens":100,"output_tokens":20}}
                """);
        });
        using var http = new HttpClient(handler);
        var result = await Client(http).EvaluateAsync(new { content = "text" }, new Dictionary<string, MicrosoftDecisionQuestion>
        {
            ["route"] = new MicrosoftDecisionChoiceQuestion("Where?", new Dictionary<string, object?> { ["search"] = null }),
            ["quality"] = new MicrosoftDecisionScoreQuestion(new { question = "Quality?" }, ["low", "high"]),
            ["urgent"] = new MicrosoftDecisionNoulQuestion("Urgent?")
        });
        Assert.Equal("search", Assert.IsType<MicrosoftDecisionChoiceAnswer>(result.Answers["route"]).Choice);
        Assert.Equal(0.8, Assert.IsType<MicrosoftDecisionScoreAnswer>(result.Answers["quality"]).Score);
        Assert.Equal(0.9, Assert.IsType<MicrosoftDecisionNoulAnswer>(result.Answers["urgent"]).Noul);
        Assert.Equal("jev-test", result.Model);
        Assert.Equal(100, result.Usage.InputTokens);
        Assert.Equal(20, result.Usage.OutputTokens);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(422)]
    [InlineData(429)]
    [InlineData(529)]
    public async Task ErrorsPreserveStatusWithoutDisclosingResponse(int status)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("private document")
        })));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Client(http).EvaluateAsync("state", Questions()));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.DoesNotContain("private document", error.Message);
    }

    [Fact]
    public async Task UsesEntraTokenAndHonorsCancellation()
    {
        using var http = new HttpClient(new Handler((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal("Bearer entra-token", request.Headers.Authorization!.ToString());
            Assert.False(request.Headers.Contains("api-key"));
            return Task.FromResult(Response("""{"model":"microsoft-decision-1","answers":{"check":{"type":"noul","noul":0.9}},"usage":{"input_tokens":10,"output_tokens":0}}"""));
        }));
        var client = new MicrosoftDecisionClient(http, Options.Create(new MicrosoftDecisionOptions
        {
            Endpoint = "https://decision.example/providers/microsoft/v1/systemone", Model = "deployment"
        }), new Credential());
        Assert.Equal(10, (await client.EvaluateAsync("state", Questions())).Usage.InputTokens);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EvaluateAsync("state", Questions(), cancellation.Token));
    }

    private sealed class Credential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("https://cognitiveservices.azure.com/.default", Assert.Single(requestContext.Scopes));
            return new Azure.Core.AccessToken("entra-token", DateTimeOffset.UtcNow.AddMinutes(5));
        }

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }

    private static Dictionary<string, MicrosoftDecisionQuestion> Questions() => new()
    {
        ["check"] = new MicrosoftDecisionNoulQuestion("Yes?")
    };

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"model\":\"jev\",\"answers\":{},\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}")]
    public async Task RejectsIncompleteResponse(string json)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(json))));
        await Assert.ThrowsAsync<JsonException>(() => Client(http).EvaluateAsync("state",
            new Dictionary<string, MicrosoftDecisionQuestion> { ["check"] = new MicrosoftDecisionNoulQuestion("Yes?") }));
    }

    [Fact]
    public async Task RejectsInvalidInputAndMissingEndpointBeforeSending()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Must not send")));
        await Assert.ThrowsAsync<ArgumentException>(() => Client(http).EvaluateAsync("state", new Dictionary<string, MicrosoftDecisionQuestion>()));
        await Assert.ThrowsAsync<ArgumentException>(() => Client(http).EvaluateAsync("state",
            new Dictionary<string, MicrosoftDecisionQuestion> { ["score"] = new MicrosoftDecisionScoreQuestion("Rate", ["one"]) }));
        var missingKey = new MicrosoftDecisionClient(http, Options.Create(new MicrosoftDecisionOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => missingKey.EvaluateAsync("state", Questions()));
    }

    [Fact]
    public void RegistrationBindsTimeoutAndAllowsUnconfiguredClient()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MicrosoftDecision:TimeoutSeconds"] = "12"
        }).Build();
        using var provider = new ServiceCollection().AddMicrosoftDecisionClient(config).BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<MicrosoftDecisionClient>());
        Assert.Equal(12, provider.GetRequiredService<IOptions<MicrosoftDecisionOptions>>().Value.TimeoutSeconds);
    }

    private static MicrosoftDecisionClient Client(HttpClient http) => new(http, Options.Create(new MicrosoftDecisionOptions { ApiKey = "test-key", Endpoint = "https://decision.example/providers/microsoft/v1/systemone", Model = "decision-deployment" }));

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}

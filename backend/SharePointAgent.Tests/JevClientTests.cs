using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class JevClientTests
{
    [Theory]
    [InlineData("https://jev.example/custom/systemone")]
    [InlineData("https://jev.example/custom/systemone?version=test")]
    public async Task UsesFullEvaluationUrlAndSiblingModelsUrl(string endpoint)
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(endpoint, request.RequestUri!.AbsoluteUri);
                return Task.FromResult(Response("""{"model":"jev","answers":{"check":{"type":"noul","noul":0.9}},"usage":{"input_tokens":10,"output_tokens":0}}"""));
            }
            Assert.Equal("https://jev.example/custom/models", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response("""{"models":[]}"""));
        }));
        var client = new JevClient(http, Options.Create(new JevOptions { Endpoint = endpoint, ApiKey = "test-key" }));
        await client.EvaluateAsync("state", new Dictionary<string, JevQuestion> { ["check"] = new JevNoulQuestion("Yes?") });
        await client.ListModelsAsync();
    }

    [Fact]
    public async Task SendsMixedQuestionsAndReadsTypedAnswersAndUsage()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal("https://api.typesafe.ai/v1/systemone", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer test-key", request.Headers.Authorization!.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("jev-latest", body.RootElement.GetProperty("model").GetString());
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
        var result = await Client(http).EvaluateAsync(new { content = "text" }, new Dictionary<string, JevQuestion>
        {
            ["route"] = new JevChoiceQuestion("Where?", new Dictionary<string, object?> { ["search"] = null }),
            ["quality"] = new JevScoreQuestion(new { question = "Quality?" }, ["low", "high"]),
            ["urgent"] = new JevNoulQuestion("Urgent?")
        });
        Assert.Equal("search", Assert.IsType<JevChoiceAnswer>(result.Answers["route"]).Choice);
        Assert.Equal(0.8, Assert.IsType<JevScoreAnswer>(result.Answers["quality"]).Score);
        Assert.Equal(0.9, Assert.IsType<JevNoulAnswer>(result.Answers["urgent"]).Noul);
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
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Client(http).ListModelsAsync());
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.DoesNotContain("private document", error.Message);
    }

    [Fact]
    public async Task ReadsModelsAndHonorsCancellation()
    {
        using var http = new HttpClient(new Handler((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/v1/models", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response("""{"models":[{"name":"jev-latest","description":"Decision model","release_date":"2026-01-01"}]}"""));
        }));
        Assert.Equal("jev-latest", Assert.Single((await Client(http).ListModelsAsync()).Models).Name);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).ListModelsAsync(cancellation.Token));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"model\":\"jev\",\"answers\":{},\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}")]
    public async Task RejectsIncompleteResponse(string json)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(json))));
        await Assert.ThrowsAsync<JsonException>(() => Client(http).EvaluateAsync("state",
            new Dictionary<string, JevQuestion> { ["check"] = new JevNoulQuestion("Yes?") }));
    }

    [Fact]
    public async Task RejectsInvalidInputAndMissingKeyBeforeSending()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Must not send")));
        await Assert.ThrowsAsync<ArgumentException>(() => Client(http).EvaluateAsync("state", new Dictionary<string, JevQuestion>()));
        await Assert.ThrowsAsync<ArgumentException>(() => Client(http).EvaluateAsync("state",
            new Dictionary<string, JevQuestion> { ["score"] = new JevScoreQuestion("Rate", ["one"]) }));
        var missingKey = new JevClient(http, Options.Create(new JevOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => missingKey.ListModelsAsync());
    }

    [Fact]
    public void RegistrationBindsTimeoutAndAllowsUnconfiguredClient()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jev:TimeoutSeconds"] = "12"
        }).Build();
        using var provider = new ServiceCollection().AddJevClient(config).BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<JevClient>());
        Assert.Equal(12, provider.GetRequiredService<IOptions<JevOptions>>().Value.TimeoutSeconds);
    }

    private static JevClient Client(HttpClient http) => new(http, Options.Create(new JevOptions { ApiKey = "test-key" }));

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}

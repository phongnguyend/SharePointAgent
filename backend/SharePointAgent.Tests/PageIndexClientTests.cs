using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class PageIndexClientTests
{
    [Theory]
    [InlineData(null, true, false)]
    [InlineData("service-key", false, true)]
    public async Task SendsMultipartOptionsAndReadsNestedTree(string? key, bool text, bool summaries)
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://index.example/base/index", request.RequestUri!.AbsoluteUri);
            Assert.Equal(key, request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : null);
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var parts = form.ToDictionary(part => part.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal("report.md", parts["file"].Headers.ContentDisposition!.FileName!.Trim('"'));
            Assert.Equal("text/markdown", parts["file"].Headers.ContentType!.MediaType);
            Assert.Equal("# Report", await parts["file"].ReadAsStringAsync(token));
            Assert.Equal(text ? "true" : "false", await parts["include_text"].ReadAsStringAsync(token));
            Assert.Equal(summaries ? "true" : "false", await parts["include_summaries"].ReadAsStringAsync(token));
            return Response(HttpStatusCode.OK, """
                {"doc_name":"report","source_line_offset":2,"warnings":["fallback"],
                 "structure":[{"title":"Report","node_id":"0000","line_num":3,"text":"Body",
                   "prefix_summary":"Intro","nodes":[{"title":"Child","start_index":1,"end_index":2,"summary":"Summary"}]}]}
                """);
        });
        using var http = new HttpClient(handler);
        var client = CreateClient(http, key);
        var result = await client.IndexAsync("report.md", "# Report"u8.ToArray(), "text/markdown", default, text, summaries);
        Assert.Equal("report", result.DocumentName);
        Assert.Equal(2, result.SourceLineOffset);
        Assert.Equal("fallback", Assert.Single(result.Warnings));
        var node = Assert.Single(result.Structure);
        Assert.Equal("0000", node.NodeId);
        Assert.Equal(3, node.LineNumber);
        Assert.Equal("Body", node.Text);
        Assert.Equal("Intro", node.PrefixSummary);
        var child = Assert.Single(node.Nodes);
        Assert.Equal(1, child.StartPage);
        Assert.Equal(2, child.EndPage);
        Assert.Equal("Summary", child.Summary);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(422)]
    [InlineData(502)]
    [InlineData(504)]
    public async Task PreservesFailureStatusAndDetail(int status)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response((HttpStatusCode)status, "failure detail")));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<PageIndexIndexingException>(() =>
            CreateClient(http).IndexAsync("test.md", [65], null, default));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains("failure detail", error.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"doc_name\":\"test\",\"structure\":null}")]
    public async Task RejectsInvalidSuccessResponse(string body)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body)));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<JsonException>(() => CreateClient(http).IndexAsync("test.md", [65], null, default));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(503)]
    public async Task HealthProbeUsesPublicEndpoint(int status)
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://index.example/base/health", request.RequestUri!.AbsoluteUri);
            Assert.False(request.Headers.Contains("X-Api-Key"));
            return Task.FromResult(Response((HttpStatusCode)status, "health"));
        });
        using var http = new HttpClient(handler);
        var client = CreateClient(http, "secret");
        if (status == 200)
        {
            await client.CheckHealthAsync(default);
        }
        else
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.CheckHealthAsync(default));
            Assert.Equal((HttpStatusCode)status, error.StatusCode);
        }
    }

    [Fact]
    public async Task CancellationReachesHttpRequest()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(HttpStatusCode.OK, "{}");
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClient(http).IndexAsync("test.md", [65], null, cancellation.Token));
    }

    [Fact]
    public void RegistrationBindsOptionsAndTimeout()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PageIndex:Endpoint"] = "http://localhost:8001",
            ["PageIndex:ApiKey"] = "test-key",
            ["PageIndex:TimeoutSeconds"] = "420",
        }).Build();
        using var provider = new ServiceCollection().AddPageIndexClient(configuration).BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<PageIndexClient>());
        Assert.Equal("test-key", provider.GetRequiredService<IOptions<PageIndexOptions>>().Value.ApiKey);
        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PageIndexClient));
        Assert.Equal(TimeSpan.FromSeconds(420), http.Timeout);
    }

    private static PageIndexClient CreateClient(HttpClient http, string? key = null) =>
        new(http, Options.Create(new PageIndexOptions { Endpoint = "https://index.example/base/", ApiKey = key }));

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}

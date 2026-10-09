using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class OllayaClientTests
{
    [Fact]
    public async Task SendsTheKeyAndConfiguredModelAndReadsTheAnswers()
    {
        JsonElement? sent = null;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal("https://ollaya.test/v1/systemone", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("secret-key", request.Headers.Authorization?.Parameter);
            sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)).RootElement.Clone();
            return Json(HttpStatusCode.OK, """
                {"model":"winnow:e4b","answers":{"complaint":{"type":"noul","noul":{"yes":0.93,"no":0.05,"unknown":0.02}}},"usage":{"input_tokens":42}}
                """);
        }));
        var client = new OllayaClient(http, Options.Create(new OllayaOptions { Endpoint = "https://ollaya.test/", ApiKey = "secret-key" }));

        var decision = await client.DecideAsync("The package arrived late.",
            new Dictionary<string, OllayaQuestion> { ["complaint"] = new("noul", "Is this a complaint?") }, CancellationToken.None);

        Assert.Equal("winnow:e4b", sent!.Value.GetProperty("model").GetString());
        Assert.Equal("noul", sent.Value.GetProperty("questions").GetProperty("complaint").GetProperty("type").GetString());
        Assert.False(sent.Value.GetProperty("questions").GetProperty("complaint").TryGetProperty("criteria", out _));
        Assert.Equal("winnow:e4b", decision.Model);
        Assert.Equal(0.93, decision.Answers["complaint"].GetProperty("noul").GetProperty("yes").GetDouble());
    }

    [Fact]
    public async Task AnUnconfiguredClientRefusesToCall()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("No network expected.")));
        var client = new OllayaClient(http, Options.Create(new OllayaOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DecideAsync("state",
            new Dictionary<string, OllayaQuestion> { ["q"] = new("noul", "?") }, CancellationToken.None));
    }

    [Fact]
    public async Task ErrorsCarryTheServerStatus()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(HttpStatusCode.Unauthorized, "{\"error\":\"UNAUTHORIZED\"}"))));
        var client = new OllayaClient(http, Options.Create(new OllayaOptions { Endpoint = "https://ollaya.test" }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.DecideAsync("state",
            new Dictionary<string, OllayaQuestion> { ["q"] = new("noul", "?") }, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}

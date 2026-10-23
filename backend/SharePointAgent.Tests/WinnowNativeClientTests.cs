using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class WinnowNativeClientTests
{
    [Fact]
    public async Task UsesOwnConfigurationAndPreservesRequestControls()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"WinnowNative:Endpoint"] = "https://native.example/",
            [$"WinnowNative:ApiKey"] = "test-key",
            [$"LayaNative:Endpoint"] = "not-a-url"
        }).Build();
        var services = new ServiceCollection();
        services.AddWinnowNativeClient(configuration);
        services.AddHttpClient<WinnowNativeClient>().ConfigurePrimaryHttpMessageHandler(() => new Handler(async request =>
        {
            Assert.Equal("https://native.example/v1/systemone", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("Winnow-E4B", body.RootElement.GetProperty("model").GetString());
            Assert.True(body.RootElement.GetProperty("winnow").GetProperty("diagnostics").GetBoolean());
            Assert.Equal("incident", body.RootElement.GetProperty("state").GetProperty("kind").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"model":"native","answers":{"urgent":{"type":"noul","noul":0.9}},"usage":{"input_tokens":5,"output_tokens":0}}""")
            };
        }));
        using var container = services.BuildServiceProvider();
        using var input = JsonDocument.Parse("""{"state":{"kind":"incident"},"questions":{"urgent":{"type":"noul","instructions":"Urgent?"}},"winnow":{"diagnostics":true}}""");
        var result = await container.GetRequiredService<WinnowNativeClient>().EvaluateAsync(input.RootElement, CancellationToken.None);
        Assert.Equal(0.9, result.GetProperty("answers").GetProperty("urgent").GetProperty("noul").GetDouble());
    }

    [Fact]
    public async Task UnconfiguredServiceFailsWithoutCallingNetwork()
    {
        var services = new ServiceCollection();
        services.AddWinnowNativeClient(new ConfigurationBuilder().Build());
        using var container = services.BuildServiceProvider();
        using var input = JsonDocument.Parse("""{"state":"test","questions":{}}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => container.GetRequiredService<WinnowNativeClient>()
            .EvaluateAsync(input.RootElement, CancellationToken.None));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return send(request);
        }
    }
}

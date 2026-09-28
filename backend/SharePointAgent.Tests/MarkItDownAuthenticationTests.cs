using System.Net;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class MarkItDownAuthenticationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("test-converter-key")]
    public async Task ConversionSendsConfiguredKeyWithoutChangingHealthProbe(string? key)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = new MarkItDownClient(http, Options.Create(new MarkItDownOptions
        {
            Endpoint = "https://converter.example",
            ApiKey = key,
        }));
        Assert.Equal("converted", await client.ConvertAsync("test.txt", [65], "text/plain", default));
        Assert.Equal(key, handler.LastKey);
        await client.CheckHealthAsync(default);
        Assert.Null(handler.LastKey);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? LastKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastKey = request.Headers.TryGetValues("X-Api-Key", out var values) ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("converted") });
        }
    }
}

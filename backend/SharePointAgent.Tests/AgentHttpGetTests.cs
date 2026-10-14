using System.Net;
using System.Text;
using System.Text.Json;
using NSubstitute;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class AgentHttpGetTests
{
    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("https://example.com/#fragment")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    public void RejectsUnsafeUrls(string url)
    {
        Assert.False(AgentHttpGet.TryValidateUrl(url, out _));
    }

    [Theory]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("168.63.129.16", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void BlocksNonPublicResolvedAddresses(string value, bool allowed)
    {
        Assert.Equal(allowed, AgentHttpGet.IsPublicAddress(IPAddress.Parse(value)));
    }

    [Fact]
    public async Task ReturnsStatusAndBoundedBodyWithoutCredentials()
    {
        var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(new string('a', AgentHttpGet.MaxResponseBytes + 100), Encoding.UTF8, "application/json")
        });
        var tool = Create(handler);
        var result = JsonSerializer.SerializeToElement(await tool.GetAsync("https://example.com/data?q=test", default));
        Assert.Equal(404, result.GetProperty("statusCode").GetInt32());
        Assert.Equal(AgentHttpGet.MaxResponseBytes, result.GetProperty("body").GetString()!.Length);
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.True(result.GetProperty("untrustedContent").GetBoolean());
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("?q=test", handler.Uri!.Query);
        Assert.False(handler.HasCredentials);
        Assert.Contains(ChatAgentService.GetTools(), tool => tool.Name == ChatAgentToolNames.HttpGet);
    }

    [Fact]
    public async Task RejectsBinaryAndDoesNotSendInvalidRequests()
    {
        var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 1, 2]) });
        var tool = Create(handler);
        var blocked = JsonSerializer.SerializeToElement(await tool.GetAsync("http://127.0.0.1/", default));
        Assert.True(blocked.TryGetProperty("error", out _));
        Assert.Equal(0, handler.Calls);
        var binary = JsonSerializer.SerializeToElement(await tool.GetAsync("https://example.com/image", default));
        Assert.True(binary.TryGetProperty("error", out _));
        Assert.False(binary.TryGetProperty("body", out _));
    }

    [Fact]
    public void HandlerDisablesRedirectsProxyAndCookiesAndChecksConnections()
    {
        using var handler = AgentHttpGet.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ConnectCallback);
    }

    private static AgentHttpGet Create(Handler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AgentHttpGet.ClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new AgentHttpGet(factory);
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public HttpMethod? Method { get; private set; }

        public Uri? Uri { get; private set; }

        public bool HasCredentials { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Method = request.Method;
            Uri = request.RequestUri;
            HasCredentials = request.Headers.Authorization is not null || request.Headers.Contains("Cookie");
            return Task.FromResult(response());
        }
    }
}

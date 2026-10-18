using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharePointAgent.Api;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class HttpPlaygroundTests
{
    [Theory]
    [InlineData(AppRoles.User, false)]
    [InlineData(AppRoles.GlobalReaderAdmin, false)]
    [InlineData(AppRoles.GlobalAdmin, true)]
    public async Task EndpointRequiresGlobalAdminAndDoesNotCache(string role, bool allowed)
    {
        var handler = new Handler();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(Create(handler));
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Items[typeof(AppUserView)] = new AppUserView(Guid.Empty, "admin@example.test", "Admin", [role], true, true, DateTimeOffset.UtcNow, null, "");
            return next(context);
        });
        app.MapHttpPlaygroundEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/admin/http-playground", new HttpPlaygroundRequest("GET", "https://example.com", [], null));
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(allowed ? 1 : 0, handler.Calls);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://169.254.169.254")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("file:///tmp/test")]
    [InlineData("https://example.com:8443")]
    public async Task RejectsUnsafeDestinationsBeforeSending(string url)
    {
        var handler = new Handler();
        await Assert.ThrowsAsync<ArgumentException>(() => Create(handler).SendAsync(new("GET", url, [], null), default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("Host", "example.com")]
    [InlineData("Content-Length", "10")]
    [InlineData("Connection", "keep-alive")]
    [InlineData("X-Test", "ok\r\nHost: internal")]
    [InlineData("Bad Header", "test")]
    public async Task RejectsUnsafeHeaders(string name, string value)
    {
        var handler = new Handler();
        await Assert.ThrowsAsync<ArgumentException>(() => Create(handler).SendAsync(new("POST", "https://example.com", [new(name, value)], "body"), default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SendsExplicitCredentialsAndBodyAndReturnsErrorResponse()
    {
        var handler = new Handler { Status = HttpStatusCode.BadRequest, Body = "{\"error\":\"invalid\"}" };
        var result = await Create(handler).SendAsync(new("PATCH", "https://example.com/test?q=1",
            [new("Authorization", "Bearer test"), new("Content-Type", "application/json"), new("X-Test", "one")], "{\"value\":2}"), default);
        Assert.Equal(400, result.StatusCode);
        Assert.Equal(handler.Body, result.Body);
        Assert.Equal(HttpMethod.Patch, handler.Method);
        Assert.Equal("Bearer test", handler.Authorization);
        Assert.Equal("application/json", handler.ContentType);
        Assert.Equal("{\"value\":2}", handler.RequestBody);
        Assert.Equal("?q=1", handler.Uri!.Query);
        Assert.Contains(result.Headers, h => h.Name == "X-Result" && h.Value == "ok");
        Assert.Equal(result.Body, Encoding.UTF8.GetString(Convert.FromBase64String(result.Base64)));
    }

    [Fact]
    public async Task BoundsResponseAndDoesNotForwardApplicationCredentials()
    {
        var handler = new Handler { Body = new string('a', HttpPlayground.MaxBytes + 10) };
        var result = await Create(handler).SendAsync(new("GET", "https://example.com", [], null), default);
        Assert.True(result.Truncated);
        Assert.Equal(HttpPlayground.MaxBytes, result.SizeBytes);
        Assert.Equal(HttpPlayground.MaxBytes, Convert.FromBase64String(result.Base64).Length);
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task RejectsUnsupportedMethodsBodyAndTimeout()
    {
        var service = Create(new Handler());
        await Assert.ThrowsAsync<ArgumentException>(() => service.SendAsync(new("CONNECT", "https://example.com", [], null), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SendAsync(new("GET", "https://example.com", [], "body"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SendAsync(new("GET", "https://example.com", [], null, 121), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SendAsync(new("POST", "https://example.com", [], new string('x', HttpPlayground.MaxBytes + 1)), default));
    }

    [Fact]
    public void TransportDisablesRedirectsCookiesAndProxies()
    {
        using var handler = AgentHttpGet.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
    }

    private static HttpPlayground Create(Handler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(HttpPlayground.ClientName).Returns(_ => new HttpClient(handler, false));
        return new(factory);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public string Body { get; init; } = "ok";

        public HttpMethod? Method { get; private set; }

        public Uri? Uri { get; private set; }

        public string? Authorization { get; private set; }

        public string? ContentType { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Method = request.Method;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            ContentType = request.Content?.Headers.ContentType?.ToString();
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(Status) { Content = new StringContent(Body) };
            response.Headers.Add("X-Result", "ok");
            return response;
        }
    }
}

using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using SharePointAgent.Api;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.Monitoring;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ServiceHealthTests
{
    [Theory]
    [InlineData(AppRoles.GlobalAdmin, true)]
    [InlineData(AppRoles.GlobalReaderAdmin, true)]
    [InlineData(AppRoles.User, false)]
    public void HealthIsRestrictedToAdministrators(string role, bool allowed)
    {
        Assert.Equal(allowed, AppAccess.Allows([role], "GET", "/api/admin/service-health"));
    }

    [Fact]
    public async Task ReportsEachServiceIndependentlyWithoutSendingKeys()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-Api-Key"));
            Assert.EndsWith("/health", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(request.RequestUri.Host == "markitdown.test"
                ? Json(HttpStatusCode.OK, "{\"status\":\"ok\"}")
                : Json(HttpStatusCode.ServiceUnavailable, "sensitive upstream error"));
        }));
        var results = await new ServiceHealthMonitor(http, Config()).CheckAsync(CancellationToken.None);
        Assert.Equal("healthy", results[0].Status);
        Assert.NotNull(results[0].ResponseTimeMs);
        Assert.Equal("unhealthy", results[1].Status);
        Assert.Contains("503", results[1].Message);
        Assert.DoesNotContain("sensitive", results[1].Message);
    }

    [Theory]
    [InlineData("<html>Placeholder image</html>")]
    [InlineData("{\"status\":\"failed\"}")]
    [InlineData("[]")]
    public async Task SuccessfulHttpStatusAloneDoesNotMeanHealthy(string body)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, body))));
        var results = await new ServiceHealthMonitor(http, Config()).CheckAsync(CancellationToken.None);
        Assert.All(results, result => Assert.Equal("unhealthy", result.Status));
    }

    [Fact]
    public async Task MissingOrInvalidConfigurationDoesNotSendRequests()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("No network expected.")));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PageIndex:Endpoint"] = "ftp://pageindex.test"
        }).Build();
        var results = await new ServiceHealthMonitor(http, config).CheckAsync(CancellationToken.None);
        Assert.Equal("not-configured", results[0].Status);
        Assert.Equal("misconfigured", results[1].Status);
        Assert.All(results, result => Assert.Null(result.ResponseTimeMs));
    }

    [Fact]
    public async Task NetworkFailuresAreReportedWithoutExceptionDetails()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new HttpRequestException("private network details")));
        var results = await new ServiceHealthMonitor(http, Config()).CheckAsync(CancellationToken.None);
        Assert.All(results, result =>
        {
            Assert.Equal("unhealthy", result.Status);
            Assert.DoesNotContain("private", result.Message);
        });
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new Exception("Expected cancellation.");
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ServiceHealthMonitor(http, Config()).CheckAsync(cancellation.Token));
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["MarkItDown:Endpoint"] = "https://markitdown.test",
        ["MarkItDown:ApiKey"] = "not-sent",
        ["PageIndex:Endpoint"] = "https://pageindex.test",
        ["PageIndex:ApiKey"] = "not-sent"
    }).Build();

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}

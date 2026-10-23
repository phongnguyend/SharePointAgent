using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SharePointAgent.Api;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class DecisionPlaygroundTests
{
    [Theory]
    [InlineData("jev")]
    [InlineData("microsoft-decision")]
    public async Task InvalidOtherProviderDoesNotBlockSelectedProvider(string selected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddJevClient(builder.Configuration);
        builder.Services.AddMicrosoftDecisionClient(builder.Configuration);
        builder.Services.Configure<JevOptions>(options => options.Endpoint = "http://invalid.example");
        builder.Services.Configure<MicrosoftDecisionOptions>(options => options.Endpoint = "https://invalid.example");
        using var http = new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"model":"test-model","answers":{"urgent":{"type":"noul","noul":0.9}},"usage":{"input_tokens":12,"output_tokens":0}}""")
        }));
        if (selected == "jev")
        {
            builder.Services.AddSingleton(new JevClient(http, Options.Create(new JevOptions { ApiKey = "test" })));
        }
        else
        {
            builder.Services.AddSingleton(new MicrosoftDecisionClient(http, Options.Create(new MicrosoftDecisionOptions
            {
                Endpoint = "https://decision.example/providers/microsoft/v1/systemone", ApiKey = "test"
            })));
        }
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Items[typeof(AppUserView)] = new AppUserView(Guid.Empty, "admin@example.test", "Admin",
                [AppRoles.GlobalAdmin], true, true, DateTimeOffset.UtcNow, null, "");
            return next(context);
        });
        app.MapDecisionPlaygroundEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var body = JsonDocument.Parse("""{"state":"test","questions":{"urgent":{"type":"noul","instructions":"Urgent?"}}}""");
        using var success = await client.PostAsJsonAsync($"/api/admin/decision-playground/{selected}", body.RootElement);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        var other = selected == "jev" ? "microsoft-decision" : "jev";
        using var failure = await client.PostAsJsonAsync($"/api/admin/decision-playground/{other}", body.RootElement);
        Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
        using var error = JsonDocument.Parse(await failure.Content.ReadAsStringAsync());
        Assert.Contains("configuration", error.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("winow-native", AppRoles.GlobalAdmin, false, false, 200)]
    [InlineData("laya-native", AppRoles.GlobalAdmin, false, false, 200)]
    [InlineData("winow-native", AppRoles.User, false, false, 403)]
    [InlineData("laya-native", AppRoles.User, false, false, 403)]
    [InlineData("winow-native", AppRoles.GlobalAdmin, true, false, 400)]
    [InlineData("laya-native", AppRoles.GlobalAdmin, true, false, 400)]
    [InlineData("winow-native", AppRoles.GlobalAdmin, false, true, 502)]
    [InlineData("laya-native", AppRoles.GlobalAdmin, false, true, 502)]
    [InlineData("ollaya", AppRoles.GlobalAdmin, false, false, 200)]
    [InlineData("ollaya", AppRoles.User, false, false, 403)]
    [InlineData("ollaya", AppRoles.GlobalAdmin, true, false, 400)]
    [InlineData("ollaya", AppRoles.GlobalAdmin, false, true, 502)]
    [InlineData("jev", AppRoles.GlobalAdmin, false, false, 200)]
    [InlineData("microsoft-decision", AppRoles.GlobalAdmin, false, false, 200)]
    [InlineData("jev", AppRoles.GlobalReaderAdmin, false, false, 403)]
    [InlineData("microsoft-decision", AppRoles.User, false, false, 403)]
    [InlineData("jev", AppRoles.GlobalAdmin, true, false, 400)]
    [InlineData("microsoft-decision", AppRoles.GlobalAdmin, false, true, 502)]
    public async Task EnforcesAccessValidatesInputAndReturnsResults(string provider, string role, bool invalid, bool upstreamFailure, int status)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(() =>
        {
            calls++;
            return new HttpResponseMessage(upstreamFailure ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            {
                Content = new StringContent(upstreamFailure ? "secret-provider-error" :
                    """{"model":"test-model","answers":{"urgent":{"type":"noul","noul":0.9}},"usage":{"input_tokens":12,"output_tokens":0}}""")
            };
        }));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new OllayaClient(http, Options.Create(new OllayaOptions
        {
            Endpoint = "https://ollaya.example", ApiKey = "secret-key"
        })));
        builder.Services.AddSingleton(new WinnowNativeClient(http, Options.Create(new WinnowNativeOptions
        {
            Endpoint = "https://winnow.example", ApiKey = "secret-key"
        })));
        builder.Services.AddSingleton(new LayaNativeClient(http, Options.Create(new LayaNativeOptions
        {
            Endpoint = "https://laya.example", ApiKey = "secret-key"
        })));
        builder.Services.AddSingleton(new JevClient(http, Options.Create(new JevOptions { ApiKey = "secret-key" })));
        builder.Services.AddSingleton(new MicrosoftDecisionClient(http, Options.Create(new MicrosoftDecisionOptions
        {
            Endpoint = "https://decision.example/providers/microsoft/v1/systemone", ApiKey = "secret-key"
        })));
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Items[typeof(AppUserView)] = new AppUserView(Guid.Empty, "admin@example.test", "Admin",
                [role], true, true, DateTimeOffset.UtcNow, null, "");
            return next(context);
        });
        app.MapDecisionPlaygroundEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var body = JsonDocument.Parse(invalid ? """{"state":"test","questions":{"urgent":{"type":"invalid"}}}""" :
            """{"state":"test","questions":{"urgent":{"type":"noul","instructions":"Urgent?"}}}""");
        using var response = await client.PostAsJsonAsync($"/api/admin/decision-playground/{provider}", body.RootElement);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", text);
        if (upstreamFailure && status == 502)
        {
            Assert.Contains("HTTP 401", text);
        }
        Assert.Equal(status is 200 or 502 ? 1 : 0, calls);
        if (status == 200)
        {
            using var json = JsonDocument.Parse(text);
            Assert.Equal(12, json.RootElement.GetProperty("result").GetProperty("usage").GetProperty("input_tokens").GetInt32());
            Assert.Equal("noul", json.RootElement.GetProperty("result").GetProperty("answers").GetProperty("urgent").GetProperty("type").GetString());
        }
    }

    private sealed class Handler(Func<HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(send());
        }
    }
}

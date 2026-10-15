using System.Net;
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

public sealed class DocumentPlaygroundTests
{
    [Theory]
    [InlineData("markitdown", "convert", "example.docx")]
    [InlineData("pageindex", "index", "example.md")]
    public async Task UsesOnlySelectedServiceAndReturnsResult(string provider, string action, string name)
    {
        using var handler = new Handler(provider);
        using var http = new HttpClient(handler);
        await using var app = await CreateApp(AppRoles.GlobalAdmin, services =>
        {
            if (provider == "markitdown")
            {
                var options = Options.Create(new MarkItDownOptions { Endpoint = "https://markitdown.test", ApiKey = "key" });
                services.AddSingleton<IOptions<MarkItDownOptions>>(options);
                services.AddSingleton(new MarkItDownClient(http, options));
            }
            else
            {
                services.AddSingleton(new PageIndexClient(http, Options.Create(new PageIndexOptions { Endpoint = "https://pageindex.test", ApiKey = "key" })));
            }
        });
        using var client = app.GetTestClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("# Example"), "file", name);
        form.Add(new StringContent("false"), "include_text");
        form.Add(new StringContent("true"), "include_summaries");
        using var response = await client.PostAsync($"/api/admin/{provider}-playground/{action}", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = json.RootElement.GetProperty("result");
        Assert.True(json.RootElement.TryGetProperty("traceId", out _));
        Assert.True(json.RootElement.TryGetProperty("elapsedMs", out _));
        Assert.Equal("key", handler.Key);
        if (provider == "markitdown")
        {
            Assert.Equal("# Converted", result.GetProperty("markdown").GetString());
        }
        else
        {
            Assert.Equal("Example", result.GetProperty("doc_name").GetString());
            Assert.Equal(12, result.GetProperty("usage").GetProperty("input_tokens").GetInt64());
            Assert.Equal(4, result.GetProperty("usage").GetProperty("output_tokens").GetInt64());
            Assert.Equal("test-model", result.GetProperty("usage").GetProperty("model_id").GetString());
            Assert.Equal("false", handler.Fields["include_text"]);
            Assert.Equal("true", handler.Fields["include_summaries"]);
        }
    }

    [Theory]
    [InlineData("markitdown/convert", AppRoles.GlobalReaderAdmin, "a.docx", "true", 403)]
    [InlineData("pageindex/index", AppRoles.User, "a.md", "true", 403)]
    [InlineData("pageindex/index", AppRoles.GlobalAdmin, "a.docx", "true", 400)]
    [InlineData("pageindex/index", AppRoles.GlobalAdmin, "a.md", "invalid", 400)]
    public async Task RejectsUnauthorizedOrInvalidRequests(string target, string role, string name, string flag, int expected)
    {
        await using var app = await CreateApp(role);
        using var client = app.GetTestClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("sample"), "file", name);
        form.Add(new StringContent(flag), "include_text");
        var parts = target.Split('/');
        using var response = await client.PostAsync($"/api/admin/{parts[0]}-playground/{parts[1]}", form);
        Assert.Equal(expected, (int)response.StatusCode);
    }

    private static async Task<WebApplication> CreateApp(string role, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        configure?.Invoke(builder.Services);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Items[typeof(AppUserView)] = new AppUserView(Guid.Empty, "admin@example.test", "Admin", [role], true, true, DateTimeOffset.UtcNow, null, "");
            return next(context);
        });
        app.MapDocumentPlaygroundEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class Handler(string provider) : HttpMessageHandler
    {
        public string? Key { get; private set; }

        public Dictionary<string, string> Fields { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Key = request.Headers.GetValues("X-Api-Key").Single();
            foreach (var field in (MultipartFormDataContent)request.Content!)
            {
                Fields[field.Headers.ContentDisposition!.Name!.Trim('"')] = await field.ReadAsStringAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(provider == "markitdown" ? "# Converted" :
                    """{"doc_name":"Example","structure":[],"usage":{"model_id":"test-model","prompt_tokens":12,"completion_tokens":4,"total_tokens":16}}""")
            };
        }
    }
}

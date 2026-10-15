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

public sealed class ImagePlaygroundTests
{
    [Theory]
    [InlineData("describe", AppRoles.GlobalReaderAdmin, "image.png", 1, 403)]
    [InlineData("extract", AppRoles.User, "image.png", 1, 403)]
    [InlineData("describe", AppRoles.GlobalAdmin, "scan.tiff", 1, 400)]
    [InlineData("extract", AppRoles.GlobalAdmin, "picture.webp", 1, 400)]
    [InlineData("describe", AppRoles.GlobalAdmin, "image.png", 0, 400)]
    [InlineData("extract", AppRoles.GlobalAdmin, "image.png", 10485761, 400)]
    [InlineData("unknown", AppRoles.GlobalAdmin, "image.png", 1, 404)]
    public async Task RejectsUnauthorizedOrInvalidUploadsBeforeResolvingProviders(string operation, string role, string name, int size, int expected)
    {
        await using var app = await CreateApp(role);
        using var client = app.GetTestClient();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[size]), "file", name);
        using var response = await client.PostAsync($"/api/admin/image-playground/{operation}", form);
        Assert.Equal(expected, (int)response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractUsesOnlyOcrAndReturnsTextOrSanitizedProviderError(bool fail)
    {
        using var http = new HttpClient(new OcrHandler(fail));
        var options = Options.Create(new DocumentIntelligenceOptions { Endpoint = "https://ocr.test", ApiKey = "test", ModelId = "prebuilt-read" });
        await using var app = await CreateApp(AppRoles.GlobalAdmin, services =>
        {
            services.AddSingleton<IOptions<DocumentIntelligenceOptions>>(options);
            services.AddSingleton(new DocumentIntelligenceClient(http, options));
        });
        using var client = app.GetTestClient();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3]), "file", "scan.PNG");
        using var response = await client.PostAsync("/api/admin/image-playground/extract", form);
        Assert.Equal(fail ? HttpStatusCode.BadGateway : HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-provider-details", body);
        if (!fail)
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal("Invoice 42", json.RootElement.GetProperty("text").GetString());
            Assert.Equal("prebuilt-read", json.RootElement.GetProperty("modelId").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("usage").ValueKind);
            Assert.True(json.RootElement.TryGetProperty("traceId", out _));
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
    }

    private static async Task<WebApplication> CreateApp(string role, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        configure?.Invoke(builder.Services);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Items[typeof(AppUserView)] = new AppUserView(Guid.Empty, "admin@example.test", "Admin",
                [role], true, true, DateTimeOffset.UtcNow, null, "");
            return next(context);
        });
        app.MapImagePlaygroundEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class OcrHandler(bool fail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (fail)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("private-provider-details") });
            }
            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Add("Operation-Location", "https://ocr.test/result");
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { status = "succeeded", analyzeResult = new { content = "Invoice 42" } })
            });
        }
    }
}

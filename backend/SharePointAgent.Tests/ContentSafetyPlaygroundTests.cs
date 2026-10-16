using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using SharePointAgent.Api;
using SharePointAgent.Domain;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ContentSafetyPlaygroundTests
{
    [Theory]
    [InlineData(AppRoles.User)]
    [InlineData(AppRoles.GlobalReaderAdmin)]
    public async Task RequiresGlobalAdminForOptionsAndAnalysis(string role)
    {
        await using var app = await CreateApp(role);
        using var client = app.GetTestClient();
        using var options = await client.GetAsync("/api/admin/content-safety-playground/options");
        Assert.Equal(HttpStatusCode.Forbidden, options.StatusCode);
        using var analysis = await client.PostAsJsonAsync("/api/admin/content-safety-playground", new { text = "example" });
        Assert.Equal(HttpStatusCode.Forbidden, analysis.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10001)]
    public async Task RejectsInvalidTextBeforeResolvingService(int length)
    {
        await using var app = await CreateApp(AppRoles.GlobalAdmin);
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/admin/content-safety-playground", new { text = new string('a', length) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    private static async Task<WebApplication> CreateApp(string role)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Items[typeof(AppUserView)] = new AppUserView(Guid.Empty, "admin@example.test", "Admin",
                [role], true, true, DateTimeOffset.UtcNow, null, "");
            return next(context);
        });
        app.MapContentSafetyPlaygroundEndpoints();

        await app.StartAsync();
        return app;
    }
}

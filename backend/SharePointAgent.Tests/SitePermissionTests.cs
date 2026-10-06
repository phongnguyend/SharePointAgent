using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharePointAgent.Api;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class SitePermissionTests
{
    private const string Target = "33333333-3333-3333-3333-333333333333";

    [Theory]
    [InlineData(AppRoles.GlobalAdmin, true)]
    [InlineData(AppRoles.GlobalReaderAdmin, false)]
    [InlineData(AppRoles.User, false)]
    public void OnlyGlobalAdminCanSave(string role, bool allowed)
    {
        Assert.Equal(allowed, AppAccess.Allows([role], "POST", "/api/admin/site-permissions"));
        Assert.Equal(allowed, AppAccess.Allows([role], "POST", "/api/admin/site-permissions/list"));
        Assert.Equal(allowed, AppAccess.Allows([role], "POST", "/api/admin/site-permissions/delete"));
        Assert.Equal(allowed, AppAccess.Allows([role], "GET", "/api/admin/site-permissions/defaults"));
    }

    [Theory]
    [InlineData(false, "Target application")]
    [InlineData(true, "Target application")]
    [InlineData(false, "")]
    [InlineData(false, null)]
    public async Task CreatesOrUpdatesOnlyTheTargetApplication(bool existing, string? displayName)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal("login.microsoftonline.com", request.RequestUri!.Host);
                Assert.Null(request.Headers.Authorization);
                var form = await request.Content!.ReadAsStringAsync();
                Assert.Contains("client_secret=test-secret", form);
                return Json("{\"access_token\":\"access-token\"}");
            }
            Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
            Assert.Equal("access-token", request.Headers.Authorization!.Parameter);
            if (calls == 2)
            {
                Assert.EndsWith("/sites/contoso.sharepoint.com:/sites/team", request.RequestUri.AbsoluteUri);
                return Json("{\"id\":\"site-id\"}");
            }
            if (calls == 3)
            {
                return Json("{\"value\":[{\"id\":\"unrelated\",\"grantedToIdentitiesV2\":[{\"application\":{\"id\":\"44444444-4444-4444-4444-444444444444\"}}]}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/sites/site-id/permissions?$skiptoken=next\"}");
            }
            if (calls == 4)
            {
                Assert.Contains("$skiptoken=next", request.RequestUri.Query);
                return Json(existing
                    ? JsonSerializer.Serialize(new { value = new[] { new { id = "existing", grantedToIdentities = new[] { new { application = new { id = Target } } } } } })
                    : "{\"value\":[]}");
            }
            Assert.Equal(5, calls);
            Assert.Equal(existing ? HttpMethod.Patch : HttpMethod.Post, request.Method);
            Assert.EndsWith(existing ? "/permissions/existing" : "/permissions", request.RequestUri.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("write", body.RootElement.GetProperty("roles")[0].GetString());
            if (!existing)
            {
                Assert.Equal(Target, body.RootElement.GetProperty("grantedToIdentities")[0].GetProperty("application").GetProperty("id").GetString());
                Assert.Equal(string.IsNullOrWhiteSpace(displayName) ? Target : displayName,
                    body.RootElement.GetProperty("grantedToIdentities")[0].GetProperty("application").GetProperty("displayName").GetString());
            }
            return Json("{\"id\":\"saved\"}");
        }));
        var input = Input();
        input.TargetDisplayName = displayName!;
        var result = await new SitePermissionService(http).SaveAsync(input, CancellationToken.None);
        Assert.Equal(existing, result.Updated);
        Assert.Equal(Target, result.TargetClientId);
        Assert.Equal(5, calls);
    }

    [Theory]
    [InlineData("http://contoso.sharepoint.com/sites/team")]
    [InlineData("https://attacker.example/sites/team")]
    [InlineData("https://contoso.sharepoint.com/sites/team?query=value")]
    public async Task RejectsInvalidSiteBeforeUsingCredentials(string url)
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("No network expected.")));
        var input = Input();
        input.SiteUrl = url;
        await Assert.ThrowsAsync<ArgumentException>(() => new SitePermissionService(http).SaveAsync(input, CancellationToken.None));
    }

    [Fact]
    public async Task AuthenticationFailureDoesNotExposeProviderBodyOrSecret()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("provider-details test-secret")
        })));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => new SitePermissionService(http).SaveAsync(Input(), CancellationToken.None));
        Assert.DoesNotContain("test-secret", exception.ToString());
        Assert.DoesNotContain("provider-details", exception.ToString());
        Assert.DoesNotContain("test-secret", Input().ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListsApplicationGrantsWithoutTargetFieldsOrGraphWrites(bool empty)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(Json("{\"access_token\":\"access-token\"}"));
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            if (calls == 2)
            {
                return Task.FromResult(Json("{\"id\":\"site-id\"}"));
            }
            if (calls == 3)
            {
                return Task.FromResult(Json(empty ? "{\"value\":[]}" : """
                    {"value":[{"id":"first","roles":["read"],"grantedToIdentitiesV2":[{"application":{"id":"app-one","displayName":"Reader"}}]}],
                     "@odata.nextLink":"https://graph.microsoft.com/v1.0/sites/site-id/permissions?$skiptoken=next"}
                    """));
            }
            Assert.Equal(4, calls);
            return Task.FromResult(Json("""
                {"value":[{"id":"second","roles":["write"],"grantedToIdentities":[{"application":{"id":"app-two","displayName":"Writer"}}]}]}
                """));
        }));
        var input = Input();
        input.TargetClientId = "";
        input.TargetDisplayName = "";
        var listing = await new SitePermissionService(http).ListAsync(input, default);
        Assert.Equal("site-id", listing.SiteId);
        Assert.Equal(empty ? 0 : 2, listing.Permissions.Count);
        if (!empty)
        {
            Assert.Equal("Reader", listing.Permissions[0].DisplayName);
            Assert.Equal("read", Assert.Single(listing.Permissions[0].Roles));
            Assert.Equal("app-two", listing.Permissions[1].ClientId);
            Assert.Equal("second", listing.Permissions[1].PermissionId);
            Assert.Equal("write", Assert.Single(listing.Permissions[1].Roles));
        }
        var json = JsonSerializer.Serialize(listing);
        Assert.DoesNotContain("access-token", json);
        Assert.DoesNotContain("test-secret", json);
    }

    [Theory]
    [InlineData("match")]
    [InlineData("missing")]
    [InlineData("wrong-app")]
    [InlineData("rejected")]
    public async Task DeleteVerifiesGrantAndHandlesEmptySuccessResponse(string scenario)
    {
        var calls = 0;
        var deletes = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(Json("{\"access_token\":\"access-token\"}"));
            }
            if (calls == 2)
            {
                return Task.FromResult(Json("{\"id\":\"site-id\"}"));
            }
            if (calls == 3)
            {
                var appId = scenario == "wrong-app" ? "44444444-4444-4444-4444-444444444444" : Target;
                return Task.FromResult(Json(scenario == "missing" ? "{\"value\":[]}" : JsonSerializer.Serialize(new
                {
                    value = new[] { new { id = "grant-id", roles = new[] { "read" }, grantedToIdentitiesV2 = new[] { new { application = new { id = appId } } } } }
                })));
            }
            deletes++;
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("https://graph.microsoft.com/v1.0/sites/site-id/permissions/grant-id", request.RequestUri!.AbsoluteUri);
            Assert.Equal("access-token", request.Headers.Authorization!.Parameter);
            Assert.Null(request.Content);
            return Task.FromResult(new HttpResponseMessage(scenario == "rejected" ? HttpStatusCode.Forbidden : HttpStatusCode.NoContent));
        }));
        var input = Input();
        input.PermissionId = "grant-id";
        var service = new SitePermissionService(http);
        if (scenario == "missing")
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteAsync(input, default));
        }
        else if (scenario == "wrong-app")
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync(input, default));
        }
        else if (scenario == "rejected")
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => service.DeleteAsync(input, default));
        }
        else
        {
            await service.DeleteAsync(input, default);
        }
        Assert.Equal(scenario is "match" or "rejected" ? 1 : 0, deletes);
    }

    [Fact]
    public async Task DefaultsContainOnlyConfiguredPublicFields()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SharePoint:TenantId"] = "configured-tenant",
            ["SharePoint:ClientId"] = Target,
            ["SharePoint:ClientSecret"] = "must-not-expose",
            ["SharePoint:SiteHostname"] = "contoso.sharepoint.com",
            ["SharePoint:SitePath"] = "/sites/team"
        });
        using var http = new HttpClient(new Handler(_ => throw new Exception("No Graph call expected.")));
        builder.Services.AddSingleton(new SitePermissionService(http));
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Items[typeof(AppUserView)] = new AppUserView(Guid.Empty, "admin@example.test", "Admin",
                [AppRoles.GlobalAdmin], true, true, DateTimeOffset.UtcNow, null, "");
            return next(context);
        });
        app.MapSitePermissionEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/admin/site-permissions/defaults");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl!.NoStore);
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
        Assert.Equal("configured-tenant", json.RootElement.GetProperty("tenantId").GetString());
        Assert.Equal("https://contoso.sharepoint.com/sites/team", json.RootElement.GetProperty("siteUrl").GetString());
        Assert.Equal(Target, json.RootElement.GetProperty("targetClientId").GetString());
        Assert.DoesNotContain("must-not-expose", text);
    }

    private static SitePermissionRequest Input() => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        ClientId = "22222222-2222-2222-2222-222222222222",
        ClientSecret = "test-secret",
        TargetClientId = Target,
        TargetDisplayName = "Target application",
        SiteUrl = "https://contoso.sharepoint.com/sites/team",
        Role = "write"
    };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
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
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatesOrUpdatesOnlyTheTargetApplication(bool existing)
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
            }
            return Json("{\"id\":\"saved\"}");
        }));
        var result = await new SitePermissionService(http).SaveAsync(Input(), CancellationToken.None);
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

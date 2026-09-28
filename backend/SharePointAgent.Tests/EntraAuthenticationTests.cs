using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SharePointAgent.Api;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class EntraAuthenticationTests
{
    private const string Tenant = "00000000-0000-0000-0000-000000000001";
    private const string Client = "00000000-0000-0000-0000-000000000002";
    private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
    private static readonly SymmetricSecurityKey Key = new("test-signing-key-at-least-thirty-two-bytes-long"u8.ToArray());

    [Theory]
    [InlineData("valid", HttpStatusCode.OK)]
    [InlineData("missing", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("wrong audience", HttpStatusCode.Unauthorized)]
    [InlineData("wrong issuer", HttpStatusCode.Unauthorized)]
    [InlineData("wrong signature", HttpStatusCode.Unauthorized)]
    [InlineData("wrong tenant", HttpStatusCode.Forbidden)]
    [InlineData("wrong client", HttpStatusCode.Forbidden)]
    [InlineData("no scope", HttpStatusCode.Forbidden)]
    [InlineData("similar scope", HttpStatusCode.Forbidden)]
    [InlineData("no user", HttpStatusCode.Forbidden)]
    public async Task ApiRequiresValidTenantAccessTokenAndDelegatedScope(string scenario, HttpStatusCode expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SharePoint:TenantId"] = Tenant,
            ["SharePoint:ClientId"] = Client
        });
        builder.Services.AddEntraAuthentication(builder.Configuration);
        builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            // Offline issuer metadata: exercise actual JWT validation without contacting Entra.
            options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            options.Configuration.SigningKeys.Add(Key);
        });
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/private", () => "protected");
        app.MapPost("/private", () => "protected");
        app.MapGet("/health", () => "healthy").AllowAnonymous();
        await app.StartAsync();
        using var http = app.GetTestClient();
        if (scenario != "missing")
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(scenario));
        }

        Assert.Equal(expected, (await http.GetAsync("/private")).StatusCode);
        Assert.Equal(expected, (await http.PostAsync("/private", null)).StatusCode);
        http.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
    }

    private static string Token(string scenario)
    {
        var claims = new List<Claim>
        {
            new("tid", scenario == "wrong tenant" ? Guid.NewGuid().ToString() : Tenant),
            new("azp", scenario == "wrong client" ? Guid.NewGuid().ToString() : Client)
        };
        if (scenario != "no user")
        {
            claims.Add(new("oid", Guid.NewGuid().ToString()));
        }

        if (scenario != "no scope")
        {
            claims.Add(new("scp", scenario == "similar scope" ? "access_as_user_extra" : "other access_as_user"));
        }

        var key = scenario == "wrong signature" ? new SymmetricSecurityKey("another-test-key-at-least-thirty-two-bytes-long"u8.ToArray()) : Key;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: scenario == "wrong issuer" ? "https://untrusted.example" : Issuer,
            audience: scenario == "wrong audience" ? "https://graph.microsoft.com" : Client,
            claims: claims,
            notBefore: DateTime.UtcNow.AddHours(-1),
            expires: scenario == "expired" ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
    }
}

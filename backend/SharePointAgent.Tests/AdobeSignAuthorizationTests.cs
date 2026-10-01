using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure.DocumentSigning;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class AdobeSignAuthorizationTests
{
    private static DocumentSigningOptions Settings() => new()
    {
        AdobeSign = new()
        {
            ClientId = "client", ClientSecret = "secret", OAuthRedirectUri = "https://app.example.com/adobe-sign-callback.html",
            AccessTokenUrl = "https://api.sg1.adobesign.com/oauth/v2/token"
        }
    };

    [Theory]
    [InlineData("test-private-secret", true)]
    [InlineData("", false)]
    public void ConfigurationDisplaysSettingsWithoutExposingClientSecret(string secret, bool configured)
    {
        var settings = Settings();
        settings.AdobeSign.ClientSecret = secret;
        settings.AdobeSign.AccessTokenUrl = "https://api.sg1.adobesign.com/oauth/v2/token";
        using var http = new HttpClient();
        var service = new AdobeSignAuthorizationService(http, Options.Create(settings));
        var result = JsonSerializer.SerializeToElement(service.Configuration);
        Assert.Equal(settings.AdobeSign.ClientId, result.GetProperty("clientId").GetString());
        Assert.Equal(settings.AdobeSign.OAuthRedirectUri, result.GetProperty("redirectUri").GetString());
        Assert.Equal(settings.AdobeSign.AuthUrl, result.GetProperty("authUrl").GetString());
        Assert.Equal(settings.AdobeSign.AccessTokenUrl, result.GetProperty("accessTokenUrl").GetString());
        Assert.Equal(configured, result.GetProperty("clientSecretConfigured").GetBoolean());
        Assert.False(result.TryGetProperty("clientSecret", out _));
        Assert.DoesNotContain("test-private-secret", result.GetRawText());
    }

    [Theory]
    [InlineData("https://api.sg1.adobesign.com/oauth/v2/token", "https://untrusted.example/")]
    [InlineData("https://api.sg1.adobesign.com/oauth/v2/token", "https://api.eu1.adobesign.com/")]
    [InlineData("https://api.sg1.adobesign.com/oauth/v2/token", null)]
    [InlineData("https://api.sg1.adobesign.com/oauth/v2/token", "")]
    public async Task DisabledProviderCanObtainTokensWithoutChangingConfiguration(string tokenUrl, string? callbackOrigin)
    {
        var settings = Settings();
        settings.AdobeSign.AccessTokenUrl = tokenUrl;
        using var handler = new Handler(async request =>
        {
            Assert.Equal(tokenUrl, request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            var form = await request.Content.ReadAsStringAsync();
            Assert.Contains("client_secret=secret", form);
            Assert.Contains("grant_type=authorization_code", form);
            Assert.Contains("code=authorization-code", form);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { access_token = "access", refresh_token = "refresh", api_access_point = "https://api.eu1.adobesign.com/", expires_in = 3600, token_type = "Bearer", web_access_point = "https://secure.eu1.adobesign.com/", additional_field = new { value = "preserved" } })
            };
        });
        using var http = new HttpClient(handler);
        var service = new AdobeSignAuthorizationService(http, Options.Create(settings));
        var user = Guid.NewGuid();
        var begin = JsonSerializer.SerializeToElement(service.Begin(user));
        Assert.Contains("user_login%3Aself", begin.GetProperty("url").GetString());
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = await service.ExchangeAsync(user, new(begin.GetProperty("state").GetString()!, "authorization-code", callbackOrigin), default);
        Assert.Equal("refresh", result.RefreshToken);
        Assert.Equal("access", result.AccessToken);
        Assert.Equal("https://api.eu1.adobesign.com", result.ApiAccessPoint);
        Assert.Equal(3600, result.ExpiresIn);
        Assert.Equal("Bearer", result.TokenType);
        Assert.Equal("https://secure.eu1.adobesign.com/", result.WebAccessPoint);
        Assert.Equal(tokenUrl, result.AccessTokenUrl);
        Assert.Equal(settings.AdobeSign.ClientId, result.ClientId);
        Assert.Equal(settings.AdobeSign.ClientSecret, result.ClientSecret);
        Assert.Equal(settings.AdobeSign.AuthUrl, result.AuthUrl);
        Assert.Equal(settings.AdobeSign.OAuthRedirectUri, result.RedirectUri);
        Assert.Contains("user_login:self", result.RequestedScope);
        Assert.InRange(result.Timestamp, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal("preserved", result.ProviderResponse.GetProperty("additional_field").GetProperty("value").GetString());
        Assert.Equal("access", result.ProviderResponse.GetProperty("access_token").GetString());
        Assert.Equal("", settings.AdobeSign.RefreshToken);
        Assert.False(settings.AdobeSign.Enabled);
    }

    [Fact]
    public async Task MissingTokenUrlDoesNotFallBackToCallbackOrigin()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Must not call Adobe."));
        using var http = new HttpClient(handler);
        var settings = Settings();
        var service = new AdobeSignAuthorizationService(http, Options.Create(settings));
        var user = Guid.NewGuid();
        var begin = JsonSerializer.SerializeToElement(service.Begin(user));
        settings.AdobeSign.AccessTokenUrl = "";
        Assert.False(service.Configured);
        Assert.Throws<InvalidOperationException>(() => service.Begin(user));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExchangeAsync(user,
            new(begin.GetProperty("state").GetString()!, "authorization-code", "https://api.eu1.adobesign.com/"), default));
    }

    [Theory]
    [InlineData("https://secure.sg1.adobesign.com/public/oauth/v2", true)]
    [InlineData("https://secure.adobesign.com/public/oauth/v2", true)]
    [InlineData("https://secure.eu1.echosign.com/public/oauth/v2", true)]
    [InlineData("https://secure.sg1.adobesign.com.evil.example/public/oauth/v2", false)]
    [InlineData("http://secure.sg1.adobesign.com/public/oauth/v2", false)]
    [InlineData("https://secure.sg1.adobesign.com/public/oauth/v2?client_id=other", false)]
    [InlineData("https://secure.sg1.adobesign.com:8443/public/oauth/v2", false)]
    [InlineData("https://user@secure.sg1.adobesign.com/public/oauth/v2", false)]
    [InlineData("https://secure.sg1.adobesign.com/public/oauth/v2#fragment", false)]
    public void AuthorizationUsesConfiguredTrustedEndpoint(string url, bool valid)
    {
        var settings = Settings();
        settings.AdobeSign.AuthUrl = url;
        using var http = new HttpClient();
        var service = new AdobeSignAuthorizationService(http, Options.Create(settings));
        Assert.Equal(valid, service.Configured);
        if (valid)
        {
            var result = JsonSerializer.SerializeToElement(service.Begin(Guid.NewGuid()));
            Assert.StartsWith(url + "?response_type=code&client_id=client", result.GetProperty("url").GetString());
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => service.Begin(Guid.NewGuid()));
        }
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://api.na1.adobesign.com.evil.example")]
    [InlineData("http://api.na1.adobesign.com")]
    [InlineData("https://api.na1.adobesign.com:8443")]
    [InlineData("https://api.na1.adobesign.com/api/rest/v6")]
    [InlineData("https://user@api.na1.adobesign.com")]
    [InlineData("https://api.na1.adobesign.com/?redirect=evil")]
    public void UnsafeTokenExchangeOriginsAreRejected(string url)
    {
        Assert.Throws<ArgumentException>(() => AdobeSignAuthorizationService.ValidateApiOrigin(url));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("https://api.sg1.adobesign.com/oauth/v2/token", true)]
    [InlineData("https://api.eu1.echosign.com/oauth/v2/token", true)]
    [InlineData("http://api.sg1.adobesign.com/oauth/v2/token", false)]
    [InlineData("https://api.sg1.adobesign.com.evil.example/oauth/v2/token", false)]
    [InlineData("https://api.sg1.adobesign.com/oauth/v2/token?redirect=evil", false)]
    [InlineData("https://user@api.sg1.adobesign.com/oauth/v2/token", false)]
    [InlineData("https://api.sg1.adobesign.com:8443/oauth/v2/token", false)]
    [InlineData("https://api.sg1.adobesign.com/oauth/v2/refresh", false)]
    public void TokenEndpointOverridesMustBeTrusted(string url, bool valid)
    {
        var settings = Settings();
        settings.AdobeSign.AccessTokenUrl = url;
        using var http = new HttpClient();
        var service = new AdobeSignAuthorizationService(http, Options.Create(settings));
        Assert.Equal(valid, service.Configured);
        if (!valid)
        {
            Assert.Throws<InvalidOperationException>(() => service.Begin(Guid.NewGuid()));
        }
    }

    [Theory]
    [InlineData("wrong-user")]
    [InlineData("tampered")]
    [InlineData("expired")]
    [InlineData("redirect-changed")]
    public async Task InvalidAuthorizationStateNeverCallsAdobe(string mode)
    {
        using var handler = new Handler(_ => throw new Exception("HTTP must not be called"));
        using var http = new HttpClient(handler);
        var settings = Settings();
        var service = new AdobeSignAuthorizationService(http, Options.Create(settings));
        var user = Guid.NewGuid();
        var state = JsonSerializer.SerializeToElement(service.Begin(user)).GetProperty("state").GetString()!;
        if (mode == "wrong-user")
        {
            user = Guid.NewGuid();
        }
        if (mode == "tampered")
        {
            state += "00";
        }
        if (mode == "redirect-changed")
        {
            settings.AdobeSign.OAuthRedirectUri = "https://other.example/callback";
        }
        if (mode == "expired")
        {
            var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
            {
                UserId = user, Expires = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), Nonce = "test", RedirectUri = settings.AdobeSign.OAuthRedirectUri
            }));
            state = payload + "." + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes("AdobeSign.OAuth.State.v1:client:" + payload)));
        }
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExchangeAsync(user, new(state, "code", "https://api.na1.adobesign.com"), default));
    }

    [Fact]
    public async Task ProviderErrorDoesNotExposeTokens()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new { error = "sensitive-provider-response", refresh_token = "secret-token" })
        }));
        using var http = new HttpClient(handler);
        var service = new AdobeSignAuthorizationService(http, Options.Create(Settings()));
        var user = Guid.NewGuid();
        var state = JsonSerializer.SerializeToElement(service.Begin(user)).GetProperty("state").GetString()!;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.ExchangeAsync(user, new(state, "code", "https://api.na1.adobesign.com"), default));
        Assert.DoesNotContain("secret-token", error.ToString());
        Assert.DoesNotContain("sensitive-provider-response", error.ToString());
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}

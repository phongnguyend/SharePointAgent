using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure.DocumentSigning;

public sealed record AdobeAuthorizationInput(string State, string Code, string? ApiAccessPoint);

public sealed record AdobeAuthorizationTokens(
    string AccessToken, string RefreshToken, string ApiAccessPoint, int? ExpiresIn,
    string? TokenType, string? WebAccessPoint, string AccessTokenUrl, string ClientId,
    string ClientSecret, long Timestamp, string AuthUrl, string RedirectUri, string RequestedScope, JsonElement ProviderResponse);

public sealed class AdobeSignAuthorizationService(HttpClient http, IOptions<DocumentSigningOptions> options)
{
    private readonly AdobeSignOptions settings = options.Value.AdobeSign;
    private const string Scopes = "agreement_read:self agreement_write:self agreement_send:self user_login:self";

    public bool Configured => !string.IsNullOrWhiteSpace(settings.ClientId) && !string.IsNullOrWhiteSpace(settings.ClientSecret)
        && ValidAuthUrl(settings.AuthUrl)
        && ValidAccessTokenUrl(settings.AccessTokenUrl)
        && Uri.TryCreate(settings.OAuthRedirectUri, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public object Configuration => new
    {
        configured = Configured,
        redirectUri = settings.OAuthRedirectUri,
        clientId = settings.ClientId,
        clientSecretConfigured = !string.IsNullOrWhiteSpace(settings.ClientSecret),
        authUrl = settings.AuthUrl,
        accessTokenUrl = settings.AccessTokenUrl
    };

    public object Begin(Guid userId)
    {
        if (!Configured)
        {
            throw new InvalidOperationException("Configure AdobeSign ClientId, ClientSecret, valid Adobe AuthUrl and AccessTokenUrl, and an HTTPS OAuthRedirectUri before authorizing.");
        }
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new AuthorizationState(
            userId, DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), settings.OAuthRedirectUri)));
        var state = payload + "." + Convert.ToHexString(Sign(payload));
        return new
        {
            state,
            url = settings.AuthUrl + "?response_type=code&client_id=" + Uri.EscapeDataString(settings.ClientId)
                + "&redirect_uri=" + Uri.EscapeDataString(settings.OAuthRedirectUri) + "&scope=" + Uri.EscapeDataString(Scopes)
                + "&state=" + Uri.EscapeDataString(state)
        };
    }

    public async Task<AdobeAuthorizationTokens> ExchangeAsync(Guid userId, AdobeAuthorizationInput input, CancellationToken ct)
    {
        ValidateState(userId, input.State);
        if (string.IsNullOrWhiteSpace(input.Code) || input.Code.Length > 4000)
        {
            throw new ArgumentException("Adobe did not return a valid authorization code. Authorize again.");
        }
        var tokenUrl = settings.AccessTokenUrl;
        var origin = ValidateApiOrigin(new Uri(tokenUrl).GetLeftPart(UriPartial.Authority));
        // Adobe authorization codes are short-lived and single-use. Neither codes nor tokens are persisted.
        var result = await SigningHttp.JsonAsync(http, HttpMethod.Post, tokenUrl, null,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = input.Code, ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret, ["redirect_uri"] = settings.OAuthRedirectUri
            }), ct, "Adobe Sign token exchange");
        if (!result.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(access.GetString())
            || !result.TryGetProperty("refresh_token", out var refresh) || refresh.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(refresh.GetString()))
        {
            throw new InvalidOperationException("Adobe did not return both tokens. Authorize again with the required scopes.");
        }
        if (result.TryGetProperty("api_access_point", out var api))
        {
            origin = ValidateApiOrigin(api.GetString() ?? "");
        }
        return new(access.GetString()!, refresh.GetString()!, origin,
            result.TryGetProperty("expires_in", out var expiry) && expiry.ValueKind == JsonValueKind.Number && expiry.TryGetInt32(out var seconds) ? seconds : null,
            result.TryGetProperty("token_type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null,
            result.TryGetProperty("web_access_point", out var web) && web.ValueKind == JsonValueKind.String ? web.GetString() : null,
            tokenUrl, settings.ClientId, settings.ClientSecret, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            settings.AuthUrl, settings.OAuthRedirectUri, Scopes, result.Clone());
    }

    public static bool ValidAuthUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && uri.AbsolutePath == "/public/oauth/v2" && uri.Host.StartsWith("secure.", StringComparison.OrdinalIgnoreCase)
        && (uri.Host.EndsWith(".adobesign.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".echosign.com", StringComparison.OrdinalIgnoreCase));

    public static string ValidateApiOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/" || !uri.Host.StartsWith("api.", StringComparison.OrdinalIgnoreCase)
            || !(uri.Host.EndsWith(".adobesign.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".echosign.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Adobe returned an invalid regional API origin. Authorize again.");
        }
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static bool ValidAccessTokenUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && uri.AbsolutePath == "/oauth/v2/token" && uri.Host.StartsWith("api.", StringComparison.OrdinalIgnoreCase)
        && (uri.Host.EndsWith(".adobesign.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".echosign.com", StringComparison.OrdinalIgnoreCase));

    private void ValidateState(Guid userId, string state)
    {
        try
        {
            if (!Configured || string.IsNullOrWhiteSpace(state) || state.Length > 8000)
            {
                throw new FormatException();
            }
            var parts = state.Split('.');
            if (parts.Length != 2 || !CryptographicOperations.FixedTimeEquals(Sign(parts[0]), Convert.FromHexString(parts[1])))
            {
                throw new FormatException();
            }
            var payload = JsonSerializer.Deserialize<AuthorizationState>(Convert.FromBase64String(parts[0]));
            if (payload is null || payload.UserId != userId || payload.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() || payload.RedirectUri != settings.OAuthRedirectUri)
            {
                throw new FormatException();
            }
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw new ArgumentException("The authorization attempt expired or does not match this administrator. Authorize again.");
        }
    }

    private byte[] Sign(string payload) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.ClientSecret),
        Encoding.UTF8.GetBytes("AdobeSign.OAuth.State.v1:" + settings.ClientId + ":" + payload));

    private sealed record AuthorizationState(Guid UserId, long Expires, string Nonce, string RedirectUri);
}

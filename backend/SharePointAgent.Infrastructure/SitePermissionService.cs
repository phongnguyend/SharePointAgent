using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SharePointAgent.Infrastructure;

// Deliberately not a record: generated ToString() must never include the secret.
public sealed class SitePermissionRequest
{
    public string TenantId { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";

    public string SiteUrl { get; set; } = "";

    public string TargetClientId { get; set; } = "";

    public string TargetDisplayName { get; set; } = "";

    public string Role { get; set; } = "read";
}

public sealed record SitePermissionResult(string SiteId, string TargetClientId, string Role, bool Updated);

public sealed class SitePermissionService(HttpClient http)
{
    private const string Graph = "https://graph.microsoft.com/v1.0/";

    public async Task<SitePermissionResult> SaveAsync(SitePermissionRequest input, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(input.TenantId, out var tenant) || tenant == Guid.Empty ||
            !Guid.TryParse(input.ClientId, out var client) || client == Guid.Empty ||
            !Guid.TryParse(input.TargetClientId, out var target) || target == Guid.Empty)
        {
            throw new ArgumentException("Tenant ID and both application client IDs must be non-empty GUIDs.");
        }
        if (string.IsNullOrWhiteSpace(input.ClientSecret) || input.ClientSecret.Length > 4096)
        {
            throw new ArgumentException("Enter a valid privileged application client secret.");
        }
        if (input.Role is not ("read" or "write"))
        {
            throw new ArgumentException("Permission must be read or write.");
        }
        if (string.IsNullOrWhiteSpace(input.TargetDisplayName) || input.TargetDisplayName.Length > 256)
        {
            throw new ArgumentException("Enter a target application name of at most 256 characters.");
        }
        if (!Uri.TryCreate(input.SiteUrl, UriKind.Absolute, out var site) || site.Scheme != "https" ||
            !site.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase) || !site.IsDefaultPort ||
            site.UserInfo.Length != 0 || site.Query.Length != 0 || site.Fragment.Length != 0)
        {
            throw new ArgumentException("Enter an HTTPS SharePoint site URL without a query or fragment, for example https://contoso.sharepoint.com/sites/team.");
        }

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{tenant:D}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = client.ToString("D"),
                ["client_secret"] = input.ClientSecret,
                ["grant_type"] = "client_credentials",
                ["scope"] = "https://graph.microsoft.com/.default"
            })
        };
        using var tokenResponse = await http.SendAsync(tokenRequest, cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Privileged application authentication failed. Check the tenant ID, client ID and secret.");
        }
        using var tokenJson = await JsonDocument.ParseAsync(await tokenResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var token = tokenJson.RootElement.GetProperty("access_token").GetString()
            ?? throw new HttpRequestException("The identity provider returned no access token.");

        async Task<JsonDocument> SendAsync(HttpMethod method, string url, object? body = null)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Provider bodies can contain sensitive data. Return only a status and setup guidance.
                throw new HttpRequestException($"Microsoft Graph returned {(int)response.StatusCode}. Verify the site URL and admin consent for Sites.FullControl.All on the privileged application.");
            }
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }

        var sitePath = site.AbsolutePath == "/" ? "/" : site.AbsolutePath.TrimEnd('/');
        using var siteJson = await SendAsync(HttpMethod.Get, $"{Graph}sites/{site.Host}:{sitePath}");
        var siteId = siteJson.RootElement.GetProperty("id").GetString()
            ?? throw new HttpRequestException("Microsoft Graph returned no site ID.");
        var permissionsUrl = $"{Graph}sites/{Uri.EscapeDataString(siteId)}/permissions";
        string? nextUrl = permissionsUrl;
        var matches = new List<string>();
        while (nextUrl is not null)
        {
            using var permissions = await SendAsync(HttpMethod.Get, nextUrl);
            foreach (var permission in permissions.RootElement.GetProperty("value").EnumerateArray())
            {
                var identities = permission.TryGetProperty("grantedToIdentitiesV2", out var v2) ? v2 :
                    permission.TryGetProperty("grantedToIdentities", out var legacy) ? legacy : default;
                if (identities.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                if (identities.EnumerateArray().Any(identity => identity.TryGetProperty("application", out var application) &&
                    application.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), out var existing) && existing == target))
                {
                    matches.Add(permission.GetProperty("id").GetString()!);
                }
            }
            nextUrl = permissions.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            if (nextUrl is not null && !nextUrl.StartsWith(permissionsUrl + "?", StringComparison.Ordinal))
            {
                throw new HttpRequestException("Microsoft Graph returned an unexpected pagination URL.");
            }
        }
        if (matches.Count > 1)
        {
            throw new InvalidOperationException("Multiple grants exist for this application. Resolve the duplicate site permissions before saving.");
        }
        if (matches.Count == 1)
        {
            using var updated = await SendAsync(HttpMethod.Patch, $"{permissionsUrl}/{Uri.EscapeDataString(matches[0])}", new { roles = new[] { input.Role } });
        }
        else
        {
            using var created = await SendAsync(HttpMethod.Post, permissionsUrl, new
            {
                roles = new[] { input.Role },
                grantedToIdentities = new[] { new { application = new { id = target.ToString("D"), displayName = input.TargetDisplayName.Trim() } } }
            });
        }
        return new(siteId, target.ToString("D"), input.Role, matches.Count == 1);
    }
}

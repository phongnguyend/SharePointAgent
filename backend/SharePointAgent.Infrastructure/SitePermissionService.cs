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

    public string PermissionId { get; set; } = "";
}

public sealed record SitePermissionResult(string SiteId, string TargetClientId, string Role, bool Updated);

public sealed record SitePermissionGrant(string PermissionId, string ClientId, string DisplayName, IReadOnlyList<string> Roles);

public sealed record SitePermissionListing(string SiteId, IReadOnlyList<SitePermissionGrant> Permissions);

public sealed class SitePermissionService(HttpClient http)
{
    private const string Graph = "https://graph.microsoft.com/v1.0/";

    public async Task DeleteAsync(SitePermissionRequest input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.PermissionId) || input.PermissionId.Length > 2048 ||
            !Guid.TryParse(input.TargetClientId, out var target) || target == Guid.Empty)
        {
            throw new ArgumentException("A permission ID and valid target client ID are required.");
        }
        var session = await LoadAsync(input, cancellationToken);
        var grants = session.Permissions.Where(grant => grant.PermissionId == input.PermissionId).ToList();
        if (grants.Count == 0)
        {
            throw new KeyNotFoundException("Permission no longer exists on this site. Refresh the permissions list.");
        }
        if (grants.Any(grant => !Guid.TryParse(grant.ClientId, out var id) || id != target))
        {
            throw new ArgumentException("Permission does not belong exclusively to the selected application. Refresh the permissions list.");
        }
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            $"{Graph}sites/{Uri.EscapeDataString(session.SiteId)}/permissions/{Uri.EscapeDataString(input.PermissionId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Microsoft Graph rejected the permission deletion. Refresh permissions before retrying.");
        }
    }

    public async Task<SitePermissionListing> ListAsync(SitePermissionRequest input, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(input, cancellationToken);
        return new(session.SiteId, session.Permissions);
    }

    public async Task<SitePermissionResult> SaveAsync(SitePermissionRequest input, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(input.TargetClientId, out var target) || target == Guid.Empty)
        {
            throw new ArgumentException("Target client ID must be a non-empty GUID.");
        }
        if (input.Role is not ("read" or "write"))
        {
            throw new ArgumentException("Permission must be read or write.");
        }
        if (input.TargetDisplayName?.Length > 256)
        {
            throw new ArgumentException("Target application name must be at most 256 characters.");
        }
        var session = await LoadAsync(input, cancellationToken);
        var matches = session.Permissions.Where(grant => Guid.TryParse(grant.ClientId, out var id) && id == target)
            .Select(grant => grant.PermissionId).Distinct().ToList();
        var permissionsUrl = $"{Graph}sites/{Uri.EscapeDataString(session.SiteId)}/permissions";
        if (matches.Count > 1)
        {
            throw new InvalidOperationException("Multiple grants exist for this application. Resolve the duplicate site permissions before saving.");
        }
        if (matches.Count == 1)
        {
            using var updated = await SendAsync(session.Token, HttpMethod.Patch, $"{permissionsUrl}/{Uri.EscapeDataString(matches[0])}", new { roles = new[] { input.Role } }, cancellationToken);
        }
        else
        {
            using var created = await SendAsync(session.Token, HttpMethod.Post, permissionsUrl, new
            {
                roles = new[] { input.Role },
                grantedToIdentities = new[] { new { application = new { id = target.ToString("D"),
                    displayName = string.IsNullOrWhiteSpace(input.TargetDisplayName) ? target.ToString("D") : input.TargetDisplayName.Trim() } } }
            }, cancellationToken);
        }
        return new(session.SiteId, target.ToString("D"), input.Role, matches.Count == 1);
    }

    private sealed class Session(string token, string siteId, IReadOnlyList<SitePermissionGrant> permissions)
    {
        public string Token { get; } = token;

        public string SiteId { get; } = siteId;

        public IReadOnlyList<SitePermissionGrant> Permissions { get; } = permissions;
    }

    private async Task<Session> LoadAsync(SitePermissionRequest input, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(input.TenantId, out var tenant) || tenant == Guid.Empty ||
            !Guid.TryParse(input.ClientId, out var client) || client == Guid.Empty)
        {
            throw new ArgumentException("Tenant ID and privileged client ID must be non-empty GUIDs.");
        }
        if (string.IsNullOrWhiteSpace(input.ClientSecret) || input.ClientSecret.Length > 4096)
        {
            throw new ArgumentException("Enter a valid privileged application client secret.");
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

        var sitePath = site.AbsolutePath == "/" ? "/" : site.AbsolutePath.TrimEnd('/');
        using var siteJson = await SendAsync(token, HttpMethod.Get, $"{Graph}sites/{site.Host}:{sitePath}", null, cancellationToken);
        var siteId = siteJson.RootElement.GetProperty("id").GetString()
            ?? throw new HttpRequestException("Microsoft Graph returned no site ID.");
        var permissionsUrl = $"{Graph}sites/{Uri.EscapeDataString(siteId)}/permissions";
        string? nextUrl = permissionsUrl;
        var grants = new List<SitePermissionGrant>();
        while (nextUrl is not null)
        {
            using var permissions = await SendAsync(token, HttpMethod.Get, nextUrl, null, cancellationToken);
            foreach (var permission in permissions.RootElement.GetProperty("value").EnumerateArray())
            {
                var identities = permission.TryGetProperty("grantedToIdentitiesV2", out var v2) ? v2 :
                    permission.TryGetProperty("grantedToIdentities", out var legacy) ? legacy : default;
                if (identities.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                var roles = permission.TryGetProperty("roles", out var roleValues) && roleValues.ValueKind == JsonValueKind.Array
                    ? roleValues.EnumerateArray().Select(role => role.GetString() ?? "").ToArray() : [];
                foreach (var identity in identities.EnumerateArray())
                {
                    if (identity.TryGetProperty("application", out var application) && application.TryGetProperty("id", out var id))
                    {
                        grants.Add(new(permission.GetProperty("id").GetString()!, id.GetString() ?? "",
                            application.TryGetProperty("displayName", out var name) ? name.GetString() ?? "" : "", roles));
                    }
                }
            }
            nextUrl = permissions.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            if (nextUrl is not null && !nextUrl.StartsWith(permissionsUrl + "?", StringComparison.Ordinal))
            {
                throw new HttpRequestException("Microsoft Graph returned an unexpected pagination URL.");
            }
        }
        return new(token, siteId, grants);
    }

    private async Task<JsonDocument> SendAsync(string token, HttpMethod method, string url, object? body, CancellationToken cancellationToken)
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
}

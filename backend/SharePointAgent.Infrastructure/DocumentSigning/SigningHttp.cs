using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SharePointAgent.Infrastructure.DocumentSigning;

internal static class SigningHttp
{
    public static async Task<JsonElement> JsonAsync(HttpClient http, HttpMethod method, string url, string? token, HttpContent? content, CancellationToken ct)
    {
        using var response = await SendAsync(http, method, url, token, content, ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement.Clone();
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpMethod method, string url, string? token, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            // Do not expose provider bodies, which may contain account details or credentials.
            throw new HttpRequestException($"Signing provider returned HTTP {(int)status}. Check the shared account configuration and provider request status before retrying.", null, status);
        }
        return response;
    }

    public static string PreparationUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !new[] { "docusign.net", "docusign.com", "adobesign.com", "echosign.com" }
                .Any(domain => uri.Host == domain || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The provider did not return a trusted preparation URL.");
        }
        return url;
    }
}

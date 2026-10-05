using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

/// <summary>Builds a document tree using the PageIndex HTTP service.</summary>
public sealed class PageIndexClient(
    HttpClient httpClient,
    IOptions<PageIndexOptions> options,
    ILogger<PageIndexClient>? logger = null)
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(10);

    private readonly PageIndexOptions _options = options.Value;

    public Task<PageIndexResult> IndexAsync(DriveItemChange item, byte[] content,
        CancellationToken cancellationToken, bool includeText = true, bool includeSummaries = false) =>
        IndexAsync(item.Name, content, item.MimeType, cancellationToken, includeText, includeSummaries);

    /// <summary>Uploads Markdown or PDF. Summaries are opt-in and use the server's model configuration.</summary>
    public async Task<PageIndexResult> IndexAsync(string fileName, byte[] content, string? mimeType,
        CancellationToken cancellationToken, bool includeText = true, bool includeSummaries = false)
    {
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.TryParse(mimeType, out var parsed)
            ? parsed
            : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(includeText ? "true" : "false"), "include_text");
        form.Add(new StringContent(includeSummaries ? "true" : "false"), "include_summaries");

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(_options.IndexPath)) { Content = form };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Add("X-Api-Key", _options.ApiKey);
        }
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            logger?.LogWarning("PageIndex indexing failed: StatusCode={StatusCode}.", (int)response.StatusCode);
            throw new PageIndexIndexingException($"PageIndex returned {(int)response.StatusCode}: {detail}", response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<PageIndexResult>(cancellationToken);
        if (result?.DocumentName is null || result.Structure is null)
        {
            throw new JsonException("PageIndex returned an invalid document tree.");
        }
        return result;
    }

    public async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HealthTimeout);
        using var response = await httpClient.GetAsync(BuildUrl(_options.HealthPath), timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(timeout.Token);
            throw new HttpRequestException($"PageIndex returned {(int)response.StatusCode}: {detail}", null, response.StatusCode);
        }
    }

    private string BuildUrl(string path)
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException("PageIndex:Endpoint is required to call the PageIndex service.");
        }
        return $"{_options.Endpoint!.TrimEnd('/')}/{path.TrimStart('/')}";
    }
}

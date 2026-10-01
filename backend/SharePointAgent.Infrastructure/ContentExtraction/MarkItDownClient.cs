using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

/// <summary>
/// Converts a file to markdown with a MarkItDown HTTP service, such as the
/// <c>markitdown</c> container that exposes <c>POST /convert</c>.
/// </summary>
public sealed class MarkItDownClient(
    HttpClient httpClient,
    IOptions<MarkItDownOptions> options,
    ILogger<MarkItDownClient>? logger = null)
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(10);

    private readonly MarkItDownOptions _options = options.Value;

    public Task<string> ConvertAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken) =>
        ConvertAsync(item.Name, content, item.MimeType, cancellationToken);

    /// <summary>
    /// Posts the file as <c>multipart/form-data</c> and returns the markdown from the response body. The
    /// file name travels with the file part, because MarkItDown selects its converter from the extension.
    /// </summary>
    public async Task<string> ConvertAsync(string fileName, byte[] content, string? mimeType, CancellationToken cancellationToken)
    {
        var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString();
        var container = content.AsSpan().StartsWith(new byte[] { 0x50, 0x4b, 0x03, 0x04 }) ? "ZIP"
            : content.AsSpan().StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }) ? "OLE"
            : content.Length == 0 ? "empty" : "other";
        logger?.LogInformation("MarkItDown conversion: TraceId={TraceId}, FileName={FileName}, Bytes={Bytes}, Container={Container}, MimeType={MimeType}.",
            traceId, fileName, content.Length, container, mimeType);
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.TryParse(mimeType, out var parsed)
            ? parsed
            : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(fileName), "name");

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(_options.ConvertPath)) { Content = form };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Add("X-Api-Key", _options.ApiKey);
        }
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            logger?.LogWarning("MarkItDown conversion failed: TraceId={TraceId}, FileName={FileName}, StatusCode={StatusCode}.",
                traceId, fileName, (int)response.StatusCode);
            throw new MarkItDownConversionException($"MarkItDown returned {(int)response.StatusCode}: {detail}", response.StatusCode);
        }
        logger?.LogInformation("MarkItDown conversion succeeded: TraceId={TraceId}, FileName={FileName}.", traceId, fileName);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// Probes the service's health endpoint. Returns normally when it answers with a success status and
    /// throws otherwise, so a caller can log why the service is unavailable. The probe carries its own
    /// short timeout rather than the conversion timeout, which is minutes long.
    /// </summary>
    public async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HealthTimeout);
        using var response = await httpClient.GetAsync(BuildUrl(_options.HealthPath), timeout.Token);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private string BuildUrl(string path) => $"{_options.Endpoint!.TrimEnd('/')}/{path.TrimStart('/')}";

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new HttpRequestException($"MarkItDown returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}", null, response.StatusCode);
    }
}

using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace SharePointAgent.Infrastructure;

public sealed record HttpPlaygroundHeader(string Name, string Value);

public sealed record HttpPlaygroundRequest(string Method, string Url, HttpPlaygroundHeader[] Headers, string? Body, int TimeoutSeconds = 30);

public sealed record HttpPlaygroundResponse(int StatusCode, string Reason, string Version, long DurationMs,
    HttpPlaygroundHeader[] Headers, string Body, string Base64, int SizeBytes, bool Truncated);

public sealed class HttpPlayground(IHttpClientFactory clients)
{
    internal const string ClientName = "HttpPlayground";
    internal const int MaxBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"
    };
    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Upgrade", "Expect", "Trailer", "TE",
        "Proxy-Authorization", "Proxy-Connection", "Accept-Encoding"
    };

    public async Task<HttpPlaygroundResponse> SendAsync(HttpPlaygroundRequest input, CancellationToken ct)
    {
        if (!Methods.Contains(input.Method ?? "") || !AgentHttpGet.TryValidateUrl(input.Url, out var uri))
        {
            throw new ArgumentException("Choose a supported method and a public HTTP(S) URL on its standard port, without credentials or a fragment.");
        }
        if (input.TimeoutSeconds is < 1 or > 120 || Encoding.UTF8.GetByteCount(input.Body ?? "") > MaxBytes)
        {
            throw new ArgumentException("Timeout must be 1–120 seconds and the request body must be at most 2 MB.");
        }
        if (input.Headers is null || input.Headers.Length > 100 || input.Headers.Any(h => h is null ||
            string.IsNullOrWhiteSpace(h.Name) || h.Name.Length > 256 || h.Value is null || h.Value.Length > 8192 ||
            h.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)) ||
            h.Value.Any(c => c is '\r' or '\n' or '\0') || BlockedHeaders.Contains(h.Name)))
        {
            throw new ArgumentException("Supply at most 100 valid headers. Host, transport, proxy, and content-length headers are managed automatically.");
        }
        if (input.Method is "GET" or "HEAD" && !string.IsNullOrEmpty(input.Body))
        {
            throw new ArgumentException("GET and HEAD requests cannot include a body.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(input.TimeoutSeconds));
        using var request = new HttpRequestMessage(new HttpMethod(input.Method!), uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        if (input.Body is not null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(input.Body));
        }
        foreach (var header in input.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                request.Content ??= new ByteArrayContent([]);
                if (!request.Content.Headers.TryAddWithoutValidation(header.Name, header.Value))
                {
                    throw new ArgumentException($"Unsupported header: {header.Name}.");
                }
            }
        }
        using var client = clients.CreateClient(ClientName);
        var watch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var bytes = new byte[MaxBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), timeout.Token);
            if (read == 0)
            {
                break;
            }
            length += read;
        }
        var size = Math.Min(length, MaxBytes);
        var encoding = Encoding.UTF8;
        try
        {
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
            if (!string.IsNullOrWhiteSpace(charset))
            {
                encoding = Encoding.GetEncoding(charset);
            }
        }
        catch (ArgumentException)
        {
            // Unknown charsets are displayed as UTF-8; downloads retain the original bytes.
        }
        return new((int)response.StatusCode, response.ReasonPhrase ?? "", response.Version.ToString(), watch.ElapsedMilliseconds,
            response.Headers.Concat(response.Content.Headers).SelectMany(h => h.Value.Select(v => new HttpPlaygroundHeader(h.Key, v))).ToArray(),
            encoding.GetString(bytes, 0, size), Convert.ToBase64String(bytes, 0, size), size, length > MaxBytes);
    }
}

public static class HttpPlaygroundRegistration
{
    public static IServiceCollection AddHttpPlayground(this IServiceCollection services)
    {
        services.AddHttpClient(HttpPlayground.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(AgentHttpGet.CreateHandler)
            .RemoveAllLoggers();
        services.AddSingleton<HttpPlayground>();
        return services;
    }
}

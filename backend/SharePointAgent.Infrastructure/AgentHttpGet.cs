using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SharePointAgent.Infrastructure;

/// <summary>Unauthenticated public-web GET requests, isolated from application HTTP credentials.</summary>
public sealed class AgentHttpGet(IHttpClientFactory clients)
{
    internal const string ClientName = "AgentHttpGet";
    internal const int MaxResponseBytes = 64 * 1024;

    public async Task<object> GetAsync(string url, CancellationToken cancellationToken)
    {
        if (!TryValidateUrl(url, out var uri))
        {
            return new { error = "Use an absolute public HTTP or HTTPS URL on its standard port, without credentials or a fragment (maximum 8192 characters)." };
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var client = clients.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.Accept.ParseAdd("text/*, application/json, application/xml");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!IsText(contentType))
            {
                return new { url = uri!.AbsoluteUri, statusCode = (int)response.StatusCode, contentType, error = "The response is not a supported text, JSON, or XML document." };
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[MaxResponseBytes + 1];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
                if (read == 0)
                {
                    break;
                }
                length += read;
            }
            return new
            {
                url = uri!.AbsoluteUri,
                statusCode = (int)response.StatusCode,
                contentType,
                body = Encoding.UTF8.GetString(buffer, 0, Math.Min(length, MaxResponseBytes)),
                truncated = length > MaxResponseBytes,
                untrustedContent = true
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new { error = "The HTTP GET request timed out after 30 seconds." };
        }
        catch (HttpRequestException)
        {
            return new { error = "The HTTP GET request failed or its destination was blocked. Only public internet addresses are allowed." };
        }
        catch (IOException)
        {
            return new { error = "The HTTP response could not be read." };
        }
    }

    internal static bool TryValidateUrl(string? url, out Uri? uri)
    {
        uri = null;
        return !string.IsNullOrWhiteSpace(url) && url.Length <= 8192 && Uri.TryCreate(url, UriKind.Absolute, out uri)
            && uri.Scheme is "http" or "https" && uri.IsDefaultPort
            && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
            && uri.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
            && (!IPAddress.TryParse(uri.DnsSafeHost, out var address) || IsPublicAddress(address));
    }

    private static bool IsText(string? type) => type is not null &&
        (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
         type.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
         type.Equals("application/xml", StringComparison.OrdinalIgnoreCase) ||
         type.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
         type.EndsWith("+xml", StringComparison.OrdinalIgnoreCase));

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Only global unicast; exclude special-purpose and transition ranges.
            return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] is 0x01 or 0x02);
        }
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        return !(bytes[0] is 0 or 10 or 127 || bytes[0] >= 224 ||
            (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
            (bytes[0] == 169 && bytes[1] == 254) ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && (bytes[1] is 0 or 168 || (bytes[1] == 88 && bytes[2] == 99))) ||
            (bytes[0] == 198 && (bytes[1] is 18 or 19 || (bytes[1] == 51 && bytes[2] == 100))) ||
            (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) ||
            address.Equals(IPAddress.Parse("168.63.129.16")));
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxResponseHeadersLength = 16,
        ConnectCallback = ConnectPublicAsync
    };

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
        {
            throw new HttpRequestException("Non-public destinations are blocked.");
        }
        // Connect to the validated IP, not the hostname, so DNS cannot change between validation and connection.
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("Could not connect to the public destination.");
    }
}

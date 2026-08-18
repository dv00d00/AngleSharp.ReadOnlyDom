using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace AngleSharp.ReadOnlyDom.HackerNews.Upstream;

/// <summary>
/// Fetches upstream HTML for the sample. The preview endpoint follows a URL chosen by the page, so this
/// is also the sample's outbound boundary: only http/https on their default ports, redirects followed by
/// hand, and every socket connected to an address that is checked at connect time rather than at parse
/// time — a name that resolves to a private address is refused even if it resolved publicly a moment ago.
/// </summary>
internal sealed class UpstreamFetcher : IDisposable
{
    private const int MaximumRedirects = 4;

    private readonly HttpClient _client;

    internal UpstreamFetcher()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectToPublicAddressAsync,
        };

        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "AngleSharp.ReadOnlyDom-streaming-sample/1.0 (+https://github.com/AngleSharp/AngleSharp.ReadOnlyDom)"
        );
        _client.DefaultRequestHeaders.Accept.ParseAdd("text/html, application/xhtml+xml;q=0.9, */*;q=0.1");
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Parses a caller-supplied URL and rejects everything the sample will not dial.</summary>
    internal static bool TryParseTarget(string? url, out Uri target, out string error)
    {
        target = null!;
        if (String.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var candidate))
        {
            error = "The url parameter must be an absolute URL.";
            return false;
        }

        if (!IsAllowedTarget(candidate))
        {
            error = "Only http and https URLs on their default ports can be previewed.";
            return false;
        }

        target = candidate;
        error = String.Empty;
        return true;
    }

    /// <summary>
    /// Fetches <paramref name="target"/>, following redirects by hand. <paramref name="configure"/> runs for
    /// every hop, so conditional headers survive a redirect chain.
    /// </summary>
    internal async Task<HttpResponseMessage> GetAsync(
        Uri target,
        CancellationToken cancellationToken,
        Action<HttpRequestMessage>? configure = null
    )
    {
        var current = target;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            configure?.Invoke(request);
            var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode) || hop == MaximumRedirects)
                return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new HttpRequestException("The upstream redirect did not include a location.");

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (!IsAllowedTarget(current))
                throw new UpstreamBlockedException($"The redirect target '{current}' is not allowed.");
        }
    }

    /// <summary>
    /// Separates "the sample refused to dial this" from "the far end misbehaved". A blocked target is the
    /// caller's problem, so it answers 400 rather than reporting a gateway failure that never happened.
    /// </summary>
    internal static (int StatusCode, string Message) Describe(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is UpstreamBlockedException blocked)
                return (StatusCodes.Status400BadRequest, blocked.Message);
        }

        return (StatusCodes.Status502BadGateway, $"The upstream request failed: {exception.Message}");
    }

    /// <summary>Reads the transport-declared charset, which outranks any declaration inside the document.</summary>
    internal static string? ReadCharset(HttpContentHeaders headers) => headers.ContentType?.CharSet?.Trim('"', '\'');

    internal static bool IsHtml(HttpContentHeaders headers)
    {
        var mediaType = headers.ContentType?.MediaType;
        return mediaType is null
            || mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status
            is HttpStatusCode.MovedPermanently
                or HttpStatusCode.Found
                or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect;

    private static bool IsAllowedTarget(Uri target) =>
        target.IsAbsoluteUri
        && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps)
        && target.IsDefaultPort
        && String.IsNullOrEmpty(target.UserInfo);

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken
    )
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false);
        var routable = Array.FindAll(addresses, IsPubliclyRoutable);
        if (routable.Length == 0)
            throw new UpstreamBlockedException($"'{context.DnsEndPoint.Host}' does not resolve to a public address.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(routable, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsPubliclyRoutable(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (
            IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast
            || address.IsIPv6UniqueLocal
        )
        {
            return false;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return true;

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            0 or 10 or 127 or >= 224 => false, // this network, private, loopback, multicast and reserved
            100 => octets[1] is < 64 or > 127, // carrier-grade NAT
            169 => octets[1] != 254, // link-local
            172 => octets[1] is < 16 or > 31, // private
            192 => octets[1] != 168 && !(octets[1] == 0 && octets[2] == 0), // private and IETF protocol assignments
            198 => octets[1] is not (18 or 19), // benchmarking
            _ => true,
        };
    }
}

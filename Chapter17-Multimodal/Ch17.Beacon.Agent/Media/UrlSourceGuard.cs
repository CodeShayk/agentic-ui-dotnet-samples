using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Ch17.Beacon.Agent.Media;

/// <summary>Resolves a host name to its addresses. Tests replace it; production uses DNS.</summary>
public delegate Task<IPAddress[]> HostResolver(string host, CancellationToken cancellationToken);

/// <summary>Thrown inside the connection callback when a host resolves to an address Beacon won't connect to.</summary>
public sealed class MediaFetchRefusedException(string reason) : Exception(reason);

/// <summary>
/// Decides whether Beacon may fetch a URL source. A URL in <c>RunAgentInput</c> is an instruction from
/// the client to make Beacon's server send a request, so it's checked twice: the URL itself (HTTPS, an
/// allow-listed host, no IP literals, no credentials, the default port) and every address the host
/// resolves to, which must be public. The second check runs again at connect time, on the address
/// actually used, so a DNS answer that changes between the two can't slip a private address through.
/// </summary>
public sealed class UrlSourceGuard(IOptions<MediaOptions> options, HostResolver resolver)
{
    /// <summary>The system resolver.</summary>
    public static readonly HostResolver SystemDns = (host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);

    /// <summary>The URL-level rules. No network access.</summary>
    /// <returns>Why the URL is refused, or <see langword="null"/>.</returns>
    public string? CheckUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!url.IsAbsoluteUri)
        {
            return "the URL isn't absolute";
        }

        if (url.Scheme != Uri.UriSchemeHttps)
        {
            return "Beacon fetches attachments only over HTTPS";
        }

        if (url.UserInfo.Length > 0)
        {
            return "the URL carries credentials";
        }

        if (url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return "Beacon doesn't fetch attachments from IP addresses";
        }

        if (!url.IsDefaultPort)
        {
            return "Beacon fetches attachments only from the standard HTTPS port";
        }

        var host = url.IdnHost;
        if (!options.Value.AllowedUrlHosts.Any(allowed => string.Equals(allowed, host, StringComparison.OrdinalIgnoreCase)))
        {
            return "the URL's host isn't one Beacon fetches attachments from";
        }

        return null;
    }

    /// <summary>Resolves the host and checks every address it has.</summary>
    /// <returns>Why the host is refused, or <see langword="null"/>.</returns>
    public async Task<string?> CheckAddressesAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            await ResolvePublicAsync(host, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (MediaFetchRefusedException refused)
        {
            return refused.Message;
        }
        catch (SocketException)
        {
            return "the URL's host name doesn't resolve";
        }
    }

    /// <summary>
    /// The connection callback for Beacon's media <see cref="HttpClient"/>: resolve, refuse any
    /// non-public address, then connect to the addresses that were checked and to nothing else.
    /// </summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await ResolvePublicAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task<IPAddress[]> ResolvePublicAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = await resolver(host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new MediaFetchRefusedException("the URL's host name doesn't resolve");
        }

        // Every address, not just the first: the connection may use any of them.
        if (addresses.Any(a => !AddressPolicy.IsPublic(a)))
        {
            throw new MediaFetchRefusedException("the URL's host resolves to a private, loopback or reserved address");
        }

        return addresses;
    }
}

/// <summary>Which IP addresses Beacon's server may connect to on a client's behalf: public unicast only.</summary>
public static class AddressPolicy
{
    private static readonly IPNetwork[] NotPublic =
    [
        // IPv4
        IPNetwork.Parse("0.0.0.0/8"),        // "this network"
        IPNetwork.Parse("10.0.0.0/8"),       // private
        IPNetwork.Parse("100.64.0.0/10"),    // carrier-grade NAT
        IPNetwork.Parse("127.0.0.0/8"),      // loopback
        IPNetwork.Parse("169.254.0.0/16"),   // link-local, including cloud metadata endpoints
        IPNetwork.Parse("172.16.0.0/12"),    // private
        IPNetwork.Parse("192.0.0.0/24"),     // protocol assignments
        IPNetwork.Parse("192.0.2.0/24"),     // documentation
        IPNetwork.Parse("192.168.0.0/16"),   // private
        IPNetwork.Parse("198.18.0.0/15"),    // benchmarking
        IPNetwork.Parse("198.51.100.0/24"),  // documentation
        IPNetwork.Parse("203.0.113.0/24"),   // documentation
        IPNetwork.Parse("224.0.0.0/4"),      // multicast
        IPNetwork.Parse("240.0.0.0/4"),      // reserved and broadcast

        // IPv6
        IPNetwork.Parse("::/128"),           // unspecified
        IPNetwork.Parse("::1/128"),          // loopback
        IPNetwork.Parse("64:ff9b::/96"),     // NAT64: an IPv4 address in disguise
        IPNetwork.Parse("100::/64"),         // discard
        IPNetwork.Parse("2001::/32"),        // Teredo
        IPNetwork.Parse("2001:db8::/32"),    // documentation
        IPNetwork.Parse("2002::/16"),        // 6to4: an IPv4 address in disguise
        IPNetwork.Parse("fc00::/7"),         // unique local
        IPNetwork.Parse("fe80::/10"),        // link-local
        IPNetwork.Parse("ff00::/8"),         // multicast
    ];

    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // ::ffff:10.0.0.5 is 10.0.0.5.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return !NotPublic.Any(network => network.Contains(address));
    }
}

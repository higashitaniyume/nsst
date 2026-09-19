using System.Net;

namespace Nsst.Server.Http;

/// <summary>
/// Works out which address to display for a request, and where that address came from.
/// </summary>
/// <remarks>
/// A proxy header is exactly as trustworthy as the proxy that sets it: with no proxy in
/// front of the server these values are trivially spoofable, which is why the source
/// travels with the address instead of being hidden. Mirrors <c>httpx.ClientIP</c>.
/// </remarks>
public static class ClientIp
{
    // Single-address headers, set by one hop directly in front of this server.
    private const string HeaderCfConnectingIp = "CF-Connecting-IP";
    private const string HeaderTrueClientIp = "True-Client-IP";
    private const string HeaderXRealIp = "X-Real-IP";

    // X-Forwarded-For is a list every hop appends to, so its left-most entry is the
    // original client.
    private const string HeaderXForwardedFor = "X-Forwarded-For";

    public const string SourceCfConnectingIp = "cf-connecting-ip";
    public const string SourceTrueClientIp = "true-client-ip";
    public const string SourceXRealIp = "x-real-ip";
    public const string SourceXForwardedFor = "x-forwarded-for";
    public const string SourceRemoteAddr = "remote-addr";

    /// <summary>
    /// Returns the client address to display and the name of the header it came from,
    /// falling back to <see cref="SourceRemoteAddr"/> when no proxy header is usable.
    /// </summary>
    public static (string Ip, string Source) Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var (header, source) in new[]
                 {
                     (HeaderCfConnectingIp, SourceCfConnectingIp),
                     (HeaderTrueClientIp, SourceTrueClientIp),
                     (HeaderXRealIp, SourceXRealIp),
                 })
        {
            var normalized = NormalizeIp(context.Request.Headers[header].ToString());
            if (normalized.Length > 0)
            {
                return (normalized, source);
            }
        }

        var chain = ForwardedFor(context);
        if (chain.Count > 0)
        {
            return (chain[0], SourceXForwardedFor);
        }

        var remote = RemoteAddr(context);
        var address = NormalizeIp(remote);
        return address.Length > 0 ? (address, SourceRemoteAddr) : (remote, SourceRemoteAddr);
    }

    /// <summary>
    /// The X-Forwarded-For chain with every entry normalized to a bare IP, oldest hop
    /// first. Unparsable entries are dropped.
    /// </summary>
    public static IReadOnlyList<string> ForwardedFor(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var raw = context.Request.Headers[HeaderXForwardedFor].ToString();
        if (raw.Length == 0)
        {
            return [];
        }

        var chain = new List<string>();
        foreach (var part in raw.Split(','))
        {
            var address = NormalizeIp(part);
            if (address.Length > 0)
            {
                chain.Add(address);
            }
        }

        return chain;
    }

    /// <summary>
    /// Reconstructs the socket peer as <c>host:port</c>, with an IPv6 host bracketed.
    /// </summary>
    /// <remarks>
    /// ASP.NET keeps the address and the port in separate properties, so the combined form
    /// has to be rebuilt. The console displays this string as <c>remote_addr</c>, so the
    /// shape it is built in is fixed.
    /// </remarks>
    public static string RemoteAddr(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return string.Empty;
        }

        // Unmap so a v4 peer reported as ::ffff:1.2.3.4 is displayed as 1.2.3.4.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var host = address.ToString();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{host}]:{context.Connection.RemotePort}"
            : $"{host}:{context.Connection.RemotePort}";
    }

    /// <summary>
    /// Accepts <c>1.2.3.4</c>, <c>1.2.3.4:5678</c>, <c>[::1]</c>, <c>[::1]:5678</c> and a
    /// bare IPv6 literal, and returns the canonical address, or <c>""</c> when the input
    /// is not an address at all.
    /// </summary>
    internal static string NormalizeIp(string? raw)
    {
        var value = raw?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value.StartsWith('['))
        {
            // Either "[::1]:5678" or a bare "[::1]".
            if (TrySplitHostPort(value, out var host))
            {
                value = host;
            }
            else
            {
                var end = value.IndexOf(']');
                if (end > 0)
                {
                    value = value[1..end];
                }
            }
        }
        else if (TrySplitHostPort(value, out var host))
        {
            // A host without a port makes the split fail, which is the common case for a
            // proxy header and must not be treated as an error.
            value = host;
        }

        value = value.Trim();
        if (value.Length == 0 || !IPAddress.TryParse(value, out var parsed))
        {
            return string.Empty;
        }

        // IPAddress.ToString() renders a mapped address as ::ffff:1.2.3.4 rather than
        // 1.2.3.4, so the mapped form is unmapped by hand.
        if (parsed.IsIPv4MappedToIPv6)
        {
            parsed = parsed.MapToIPv4();
        }

        return parsed.ToString();
    }

    /// <summary>
    /// Splits <c>host:port</c>, rejecting a value that is not a well-formed pair.
    /// </summary>
    /// <remarks>
    /// The strictness matters because this input is an attacker-controlled header. A
    /// non-numeric or out-of-range port means the value is not <c>host:port</c> at all, so
    /// <c>1.2.3.4:abc</c> falls through to being parsed as a bare address and fails;
    /// accepting the host in that case would report a spoofed header as a real address.
    /// </remarks>
    private static bool TrySplitHostPort(string value, out string host)
    {
        host = string.Empty;
        if (value.Length == 0)
        {
            return false;
        }

        int boundary;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0 || close + 1 >= value.Length || value[close + 1] != ':')
            {
                return false;
            }

            host = value[1..close];
            boundary = close + 2;
        }
        else
        {
            var colon = value.LastIndexOf(':');
            if (colon < 0)
            {
                return false;
            }

            // More than one colon means a bare IPv6 literal, which SplitHostPort refuses.
            if (value.IndexOf(':') != colon)
            {
                return false;
            }

            host = value[..colon];
            boundary = colon + 1;
        }

        var port = value[boundary..];
        if (port.Length == 0)
        {
            return false;
        }

        long number = 0;
        foreach (var c in port)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            number = (number * 10) + (c - '0');
            if (number > 65535)
            {
                return false;
            }
        }

        return true;
    }
}

namespace Nsst.Server.Http;

/// <summary>
/// Token matching inside comma-separated header values.
/// </summary>
/// <remarks>
/// The WebSocket handshake must decide whether <c>Connection</c> contains the token
/// <c>upgrade</c>, and whether <c>Upgrade</c> contains <c>websocket</c>. Both are <em>lists</em>,
/// not single values: a browser routinely sends <c>Connection: keep-alive, Upgrade</c>, so
/// comparing the whole field would reject a perfectly valid handshake with a 426 that is very
/// hard to diagnose from the client side.
///
/// Matching ignores case and per-element whitespace, per RFC 9110's definition of a
/// comma-separated list.
/// </remarks>
internal static class HeaderTokens
{
    public static bool Contains(string? headerValue, string token)
    {
        if (string.IsNullOrEmpty(headerValue))
        {
            return false;
        }

        var remaining = headerValue.AsSpan();
        while (!remaining.IsEmpty)
        {
            var comma = remaining.IndexOf(',');
            var element = comma < 0 ? remaining : remaining[..comma];

            if (element.Trim().Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (comma < 0)
            {
                return false;
            }

            remaining = remaining[(comma + 1)..];
        }

        return false;
    }
}

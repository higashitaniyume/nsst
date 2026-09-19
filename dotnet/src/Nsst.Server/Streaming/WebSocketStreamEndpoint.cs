using System.Net.WebSockets;
using Nsst.Core.Metrics;
using Nsst.Core.Protocol;
using Nsst.Core.Streaming;
using Nsst.Server.Http;

namespace Nsst.Server.Streaming;

/// <summary>
/// GET /api/stream/ws — a WebSocket endpoint streaming frame JSON text messages.
/// </summary>
public static class WebSocketStreamEndpoint
{
    /// <summary>The identifier reported in logs and by /api/info.</summary>
    public const string ProtocolName = "websocket";

    /// <summary>
    /// Bounds what a peer may send us. The client is a pure consumer: it sends nothing, so
    /// anything beyond a tiny control message is a protocol violation worth rejecting early.
    /// </summary>
    internal const int ReadLimit = 1024;

    /// <summary>
    /// Bounds the closing handshake. A peer that never answers must not be able to delay an
    /// orderly shutdown.
    /// </summary>
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    public static async Task HandleAsync(
        HttpContext context,
        ServerConfig config,
        StreamManager manager,
        Counters counters,
        CorsPolicy cors,
        IDelayStrategy delayStrategy,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger("Nsst.Server.Streaming.WebSocket");
        var remote = ClientIp.RemoteAddr(context);

        // A stream slot is reserved before the upgrade. The WebSocket endpoint applies the
        // same concurrency and draining policy as the other protocols, and a capacity error
        // is far more useful to a client as an HTTP 503 than as a close frame after a
        // successful handshake.
        //
        // The parent token is deliberately CancellationToken.None: ASP.NET cancels the
        // request token as soon as the connection is upgraded, so it cannot bound the
        // stream's lifetime. Peer disconnects are observed through the receive loop below,
        // with the per-frame write deadline as a backstop.
        var start = await StreamRequest
            .BeginAsync(context, config.Limits, manager, CancellationToken.None)
            .ConfigureAwait(false);
        if (start is null)
        {
            return;
        }

        var (parameters, lease) = start.Value;
        using (lease)
        {
            // The handshake is validated before the Origin check, so a plain GET is answered
            // 426 rather than a 403 about an Origin that never mattered. Reversing the two
            // changes which error a malformed request sees.
            if (await RejectBadHandshakeAsync(context).ConfigureAwait(false))
            {
                logger.LogDebug("websocket: handshake rejected remote={Remote}", remote);
                return;
            }

            if (!OriginAllowed(context, cors))
            {
                await WriteOriginForbiddenAsync(context).ConfigureAwait(false);
                return;
            }

            WebSocket socket;
            try
            {
                socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is WebSocketException or InvalidOperationException or IOException)
            {
                // The checks above already answered everything Kestrel would refuse, so this is
                // a genuine surprise rather than a client mistake. Logged instead of mapped
                // because there is no response left to write at this point.
                logger.LogDebug(error, "websocket: upgrade failed remote={Remote}", remote);
                return;
            }

            using (socket)
            {
                using var peerGone = CancellationTokenSource.CreateLinkedTokenSource(lease.Cancellation);
                var reader = ReadUntilClosedAsync(socket, peerGone);

                StreamLog.Started(logger, ProtocolName, remote, lease.Id, parameters);

                var sink = new FrameSink(socket, config.Limits.WriteTimeout);
                var lifetime = new LinkedLifetime(peerGone.Token, lease);
                var result = await StreamRunner.RunAsync(
                    lifetime,
                    parameters,
                    FrameEncoder.For(parameters.PayloadSize),
                    sink,
                    counters,
                    StreamHooks.None,
                    TimeProvider.System,
                    delayStrategy).ConfigureAwait(false);

                manager.Record(result);

                var (status, reason) = CloseCode(result.Reason);
                await CloseWithGraceAsync(socket, status, reason).ConfigureAwait(false);

                // Unblock the receive half if the peer is being silent, then let it finish.
                peerGone.Cancel();
                try
                {
                    await reader.WaitAsync(CloseGrace).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    socket.Abort();
                }

                StreamLog.Finished(logger, ProtocolName, remote, lease.Id, parameters, result);
            }
        }
    }

    /// <summary>
    /// Whether a browser-originated request may open a stream from <c>Origin</c>.
    /// </summary>
    /// <remarks>
    /// ASP.NET Core does not authenticate the <c>Origin</c> header for WebSockets at all — a
    /// browser does not preflight an upgrade, so the CORS middleware never sees it — and the
    /// check therefore has to be made here. With no CORS configuration the policy is
    /// same-origin: the Origin must match the Host the request arrived on.
    /// </remarks>
    internal static bool OriginAllowed(HttpContext context, CorsPolicy cors)
    {
        var origin = context.Request.Headers.Origin.ToString();

        // A non-browser client sends no Origin and there is no cross-origin request to
        // authenticate.
        if (origin.Length == 0)
        {
            return true;
        }

        if (cors.AllowAll)
        {
            return true;
        }

        var originHost = OriginHost(origin);
        if (originHost.Length == 0)
        {
            return false;
        }

        if (!cors.Empty)
        {
            foreach (var pattern in cors.OriginPatterns() ?? [])
            {
                if (HostMatches(originHost, pattern))
                {
                    return true;
                }
            }

            return false;
        }

        return string.Equals(originHost, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The <c>host[:port]</c> an Origin header refers to, or empty when unparsable.</summary>
    /// <remarks>
    /// <see cref="Uri.Authority"/> omits a default port, which is the shape the <c>Host</c>
    /// header carries too, so the two strings compare directly.
    /// </remarks>
    private static string OriginHost(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Authority : string.Empty;

    private static bool HostMatches(string host, string pattern)
    {
        if (string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // "*.example.com" accepts any single subdomain label, the only wildcard shape an
        // origin pattern supports.
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..];
            return host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Rejects a request that is not a well-formed WebSocket handshake.
    /// </summary>
    /// <remarks>
    /// Kestrel's own <c>AcceptWebSocketAsync</c> fails these but surfaces the failure as a
    /// thrown exception, and a caller that does not catch it ends up having written nothing at
    /// all — the client receives an empty 200 to a handshake that was rejected, which is worse
    /// than useless because it looks like success.
    ///
    /// So the checks are made here, up front, and each one answers with the status and wording
    /// that says what was actually wrong. Order matters: the transport-level checks run before
    /// the origin check, so a request that is not even a WebSocket upgrade is told that, rather
    /// than being told its Origin is wrong.
    /// </remarks>
    /// <returns><c>true</c> when the request was rejected and a response has been written.</returns>
    private static async Task<bool> RejectBadHandshakeAsync(HttpContext context)
    {
        var connection = context.Request.Headers.Connection.ToString();
        if (!HeaderTokens.Contains(connection, "Upgrade"))
        {
            await HttpResponses.WritePlainTextAsync(
                context,
                StatusCodes.Status426UpgradeRequired,
                $"WebSocket protocol violation: Connection header \"{connection}\" does not contain Upgrade")
                .ConfigureAwait(false);
            return true;
        }

        var upgrade = context.Request.Headers.Upgrade.ToString();
        if (!HeaderTokens.Contains(upgrade, "websocket"))
        {
            await HttpResponses.WritePlainTextAsync(
                context,
                StatusCodes.Status426UpgradeRequired,
                $"WebSocket protocol violation: Upgrade header \"{upgrade}\" does not contain websocket")
                .ConfigureAwait(false);
            return true;
        }

        var version = context.Request.Headers.SecWebSocketVersion.ToString();
        if (!string.Equals(version, "13", StringComparison.Ordinal))
        {
            // RFC 6455 requires the supported version in the rejection, otherwise the client
            // cannot tell whether retrying with a different version would help.
            context.Response.Headers["Sec-WebSocket-Version"] = "13";
            await HttpResponses.WritePlainTextAsync(
                context,
                StatusCodes.Status400BadRequest,
                $"unsupported WebSocket protocol version (only 13 is supported): \"{version}\"")
                .ConfigureAwait(false);
            return true;
        }

        var key = context.Request.Headers.SecWebSocketKey.ToString();
        if (key.Length == 0)
        {
            await HttpResponses.WritePlainTextAsync(
                context,
                StatusCodes.Status400BadRequest,
                "WebSocket protocol violation: missing Sec-WebSocket-Key")
                .ConfigureAwait(false);
            return true;
        }

        if (!IsWellFormedKey(key))
        {
            await HttpResponses.WritePlainTextAsync(
                context,
                StatusCodes.Status400BadRequest,
                $"WebSocket protocol violation: invalid Sec-WebSocket-Key \"{key}\", must be a 16 byte base64 encoded string")
                .ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>A <c>Sec-WebSocket-Key</c> is 16 random bytes, base64 encoded.</summary>
    private static bool IsWellFormedKey(string key)
    {
        Span<byte> decoded = stackalloc byte[32];
        return Convert.TryFromBase64String(key, decoded, out var written) && written == 16;
    }

    /// <summary>
    /// The Origin rejection, which distinguishes an unparsable Origin from one that merely
    /// does not match.
    /// </summary>
    /// <remarks>
    /// The two errors are distinct and the difference is observable. An Origin that does not
    /// parse as a URL with a host gets "is not a valid URL with a host" quoting the raw header
    /// — which is what <c>Origin: null</c> and <c>Origin: not a url</c> produce. Only a
    /// well-formed origin that simply does not match reaches "is not authorized", and that one
    /// quotes the parsed <em>host</em>: an Origin of <c>https://evil.example</c> is reported as
    /// <c>"evil.example"</c>. The scheme is ignored when deciding, which is why <c>http://</c>
    /// and <c>https://</c> for the same host are both accepted.
    /// </remarks>
    private static Task WriteOriginForbiddenAsync(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed) || parsed.Authority.Length == 0)
        {
            return HttpResponses.WritePlainTextAsync(
                context,
                StatusCodes.Status403Forbidden,
                $"request Origin \"{origin}\" is not a valid URL with a host");
        }

        return HttpResponses.WritePlainTextAsync(
            context,
            StatusCodes.Status403Forbidden,
            $"request Origin \"{parsed.Authority}\" is not authorized for Host \"{context.Request.Host}\"");
    }

    /// <summary>
    /// Drains the read half, which also notices a peer that closes or misbehaves.
    /// </summary>
    /// <remarks>
    /// Draining the read half is what answers control frames and cancels the stream as soon as
    /// the peer goes away; the send path alone cannot see a close frame. The client is a pure
    /// consumer, so anything it sends is discarded up to <see cref="ReadLimit"/>.
    /// </remarks>
    private static async Task ReadUntilClosedAsync(WebSocket socket, CancellationTokenSource peerGone)
    {
        var buffer = new byte[1024];
        var total = 0;

        try
        {
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var received = await socket.ReceiveAsync(buffer, peerGone.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                total += received.Count;
                if (total > ReadLimit)
                {
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.MessageTooBig,
                        "read limit exceeded",
                        CancellationToken.None).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException or InvalidOperationException)
        {
            // The peer went away, or the stream finished and the socket was closed. Either
            // way the read half has nothing left to do.
        }
        finally
        {
            if (!peerGone.IsCancellationRequested)
            {
                peerGone.Cancel();
            }
        }
    }

    /// <summary>
    /// Performs the closing handshake but never blocks longer than <see cref="CloseGrace"/>,
    /// after which the connection is torn down.
    /// </summary>
    private static async Task CloseWithGraceAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        using var deadline = new CancellationTokenSource(CloseGrace);
        try
        {
            await socket.CloseOutputAsync(status, reason, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException or InvalidOperationException)
        {
            // A peer that never answers must not be able to delay shutdown.
            socket.Abort();
        }
    }

    internal static (WebSocketCloseStatus Status, string Reason) CloseCode(EndReason reason) => reason switch
    {
        EndReason.Completed => (WebSocketCloseStatus.NormalClosure, "stream completed"),
        EndReason.ServerShutdown => (WebSocketCloseStatus.EndpointUnavailable, "server shutting down"),
        EndReason.WriteError => (WebSocketCloseStatus.InternalServerError, "stream write failure"),
        _ => (WebSocketCloseStatus.NormalClosure, "client closed"),
    };

    /// <summary>Writes one JSON text message per frame.</summary>
    private sealed class FrameSink(WebSocket socket, TimeSpan timeout) : IStreamSink
    {
        public async ValueTask WriteFrameAsync(ulong sequence, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Cancelling a pending send tears the connection down, which is exactly the
            // back-pressure behaviour wanted for a stalled reader. Unlike the HTTP paths
            // this needs no connection-level timeout feature: the WebSocket API takes the
            // token directly.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            try
            {
                await socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The per-frame deadline expired rather than the stream ending, so this is a
                // stalled reader: the runner must report a write error, not a clean close.
                throw new StreamWriteTimeoutException("websocket write deadline exceeded");
            }
        }
    }
}

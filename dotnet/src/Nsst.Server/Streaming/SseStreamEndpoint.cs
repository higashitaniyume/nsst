using System.Buffers.Text;
using System.Diagnostics;
using System.Text;
using Nsst.Core.Metrics;
using Nsst.Core.Protocol;
using Nsst.Core.Streaming;
using Nsst.Server.Http;

namespace Nsst.Server.Streaming;

/// <summary>
/// GET /api/stream/sse — Server-Sent Events carrying the same frame payload as the other
/// protocols.
/// </summary>
public static class SseStreamEndpoint
{
    /// <summary>The SSE media type.</summary>
    public const string ContentType = "text/event-stream";

    /// <summary>The identifier reported in logs and by /api/info.</summary>
    public const string ProtocolName = "sse";

    /// <summary>The SSE event name used for every data frame.</summary>
    public const string EventName = "data";

    /// <summary>
    /// How long the stream may stay silent before a comment line is emitted. It keeps
    /// intermediaries from closing an idle connection and is also how a dead peer is
    /// noticed between widely spaced frames.
    /// </summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly byte[] OpenComment = ": stream open\n\n"u8.ToArray();

    public static async Task HandleAsync(
        HttpContext context,
        ServerConfig config,
        StreamManager manager,
        Counters counters,
        IDelayStrategy delayStrategy,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger("Nsst.Server.Streaming.Sse");
        var remote = ClientIp.RemoteAddr(context);

        var start = await StreamRequest
            .BeginAsync(context, config.Limits, manager, context.RequestAborted)
            .ConfigureAwait(false);
        if (start is null)
        {
            return;
        }

        var (parameters, lease) = start.Value;
        using (lease)
        {
            var response = context.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = ContentType;
            response.Headers.CacheControl = "no-cache";
            response.Headers.Connection = "keep-alive";

            // Disable proxy buffering so intermediaries forward events immediately.
            response.Headers["X-Accel-Buffering"] = "no";

            // Opening comment: commits the response and defeats any buffering proxy.
            try
            {
                await response.Body.WriteAsync(OpenComment, context.RequestAborted).ConfigureAwait(false);
                await response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                return;
            }

            StreamLog.Started(logger, ProtocolName, remote, lease.Id, parameters);

            var sink = new SseSink(response, new FrameWriteTimeout(context, config.Limits.WriteTimeout));
            var result = await StreamRunner.RunAsync(
                lease,
                parameters,
                FrameEncoder.For(parameters.PayloadSize),
                sink,
                counters,
                StreamHooks.None,
                TimeProvider.System,
                delayStrategy).ConfigureAwait(false);

            manager.Record(result);
            StreamLog.Finished(logger, ProtocolName, remote, lease.Id, parameters, result);
        }
    }

    /// <summary>
    /// Renders frames as SSE events:
    /// <code>
    /// id: 7
    /// event: data
    /// data: {"sequence":7,...}
    /// </code>
    /// </summary>
    private sealed class SseSink(HttpResponse response, FrameWriteTimeout timeout) : IStreamSink
    {
        private static readonly byte[] KeepAlive = ": keep-alive\n\n"u8.ToArray();

        // Room for "id: " + digits + "\nevent: data\ndata: " + "\n\n".
        private const int FramingBytes = 64;

        private byte[] _buffer = new byte[512];
        private long _lastFrameAt = Stopwatch.GetTimestamp();

        public async ValueTask WriteFrameAsync(ulong sequence, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            timeout.Arm();
            try
            {
                // A long silence between frames is indistinguishable from a dead connection
                // to most intermediaries, so a comment line is sent to hold it open.
                if (Stopwatch.GetElapsedTime(_lastFrameAt) >= HeartbeatInterval)
                {
                    await response.Body.WriteAsync(KeepAlive, cancellationToken).ConfigureAwait(false);
                }

                var body = BuildEvent(sequence, frame.Span);
                await response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

                _lastFrameAt = Stopwatch.GetTimestamp();
            }
            finally
            {
                timeout.Disarm();
            }
        }

        /// <remarks>
        /// The framing is assembled by hand rather than with string concatenation: the
        /// frame bytes are already UTF-8 JSON and re-encoding them through a
        /// <see cref="string"/> would be both slower and a chance to corrupt them.
        /// </remarks>
        private ReadOnlyMemory<byte> BuildEvent(ulong sequence, ReadOnlySpan<byte> frame)
        {
            var needed = FramingBytes + frame.Length;
            if (_buffer.Length < needed)
            {
                _buffer = new byte[needed];
            }

            var span = _buffer.AsSpan();
            var offset = 0;

            "id: "u8.CopyTo(span[offset..]);
            offset += 4;

            Utf8Formatter.TryFormat(sequence, span[offset..], out var digits);
            offset += digits;

            "\nevent: "u8.CopyTo(span[offset..]);
            offset += 8;

            Encoding.ASCII.GetBytes(EventName, span[offset..]);
            offset += EventName.Length;

            "\ndata: "u8.CopyTo(span[offset..]);
            offset += 7;

            frame.CopyTo(span[offset..]);
            offset += frame.Length;

            "\n\n"u8.CopyTo(span[offset..]);
            offset += 2;

            return _buffer.AsMemory(0, offset);
        }
    }
}

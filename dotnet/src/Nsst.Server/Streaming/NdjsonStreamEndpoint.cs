using Nsst.Core.Metrics;
using Nsst.Core.Protocol;
using Nsst.Core.Streaming;
using Nsst.Server.Http;

namespace Nsst.Server.Streaming;

/// <summary>
/// GET /api/stream/http — an NDJSON stream: one JSON encoded frame per line, flushed
/// after every frame.
/// </summary>
public static class NdjsonStreamEndpoint
{
    /// <summary>The media type of the NDJSON stream.</summary>
    public const string ContentType = "application/x-ndjson";

    /// <summary>The identifier reported in logs and by /api/info.</summary>
    public const string ProtocolName = "http-stream";

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

        var logger = loggerFactory.CreateLogger("Nsst.Server.Streaming.HttpStream");
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
            response.Headers.CacheControl = "no-store";

            // Disable proxy buffering so intermediaries forward frames immediately.
            response.Headers["X-Accel-Buffering"] = "no";

            // Commit the headers before the first frame so a client sees the stream open
            // even if the first interval is long.
            await response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

            StreamLog.Started(logger, ProtocolName, remote, lease.Id, parameters);

            var sink = new NdjsonSink(response, new FrameWriteTimeout(context, config.Limits.WriteTimeout));
            var result = await StreamRunner.RunAsync(
                lease,
                parameters,
                FrameEncoder.For(parameters.PayloadSize),
                sink,
                counters,
                StreamHooks.None,
                TimeProvider.System,
                delayStrategy).ConfigureAwait(false);

            // Recorded before the lease is released so /api/metrics never shows a finished
            // stream as still active.
            manager.Record(result);
            StreamLog.Finished(logger, ProtocolName, remote, lease.Id, parameters, result);
        }
    }

    /// <summary>Writes one frame per line and flushes after every frame.</summary>
    private sealed class NdjsonSink(HttpResponse response, FrameWriteTimeout timeout) : IStreamSink
    {
        private static readonly byte[] Newline = "\n"u8.ToArray();

        public async ValueTask WriteFrameAsync(ulong sequence, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            timeout.Arm();
            try
            {
                // Both writes land in the response buffer; a single flush per frame keeps
                // the frame atomic on the wire.
                await response.Body.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                await response.Body.WriteAsync(Newline, cancellationToken).ConfigureAwait(false);
                await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                timeout.Disarm();
            }
        }
    }
}

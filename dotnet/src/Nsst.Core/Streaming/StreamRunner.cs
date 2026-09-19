using Nsst.Core.Metrics;
using Nsst.Core.Protocol;

namespace Nsst.Core.Streaming;

/// <summary>
/// The protocol independent emit loop.
/// </summary>
/// <remarks>
/// Mirrors <c>internal/stream.Run</c>. The first frame goes out immediately (t=0)
/// so the client can measure first frame latency; later frames follow a fixed
/// interval grid driven by <see cref="Pacer"/>.
/// </remarks>
public static class StreamRunner
{
    public static async ValueTask<StreamResult> RunAsync(
        IStreamLifetime lifetime,
        StreamParams parameters,
        FrameEncoder encoder,
        IStreamSink sink,
        Counters? counters,
        StreamHooks hooks,
        TimeProvider timeProvider,
        IDelayStrategy? delayStrategy = null)
    {
        var startedAt = timeProvider.GetUtcNow();
        var pacer = new Pacer(parameters.Interval, timeProvider, delayStrategy);

        // The deadline lives in the pacer's monotonic domain so that a wall clock
        // step during a long test cannot end the stream early. StartedAt stays wall
        // clock because that is what gets logged and what server_time derives from.
        var deadline = pacer.Now + parameters.Duration;

        // The frame total is decided by the grid, not by the wall clock. Relying on
        // "has the deadline passed?" alone left the count dependent on how closely the
        // delay primitive lands on the boundary: Windows overshoots by a timer tick and
        // stopped at ceil(Duration/Interval) frames, while Linux landed just inside the
        // deadline and emitted one more. The console draws its progress bar from
        // ExpectedFrames, so the two have to agree on every platform. The deadline check
        // below stays as the safety net for a machine too slow to keep the grid.
        var totalFrames = (ulong)parameters.ExpectedFrames;

        var buffer = new byte[encoder.RecommendedBufferSize];
        var token = lifetime.Cancellation;

        ulong sequence = 0;
        ulong frames = 0;
        ulong bytesSent = 0;
        var offset = 0;
        var reason = EndReason.Completed;
        Exception? error = null;

        while (true)
        {
            bool emit;
            try
            {
                emit = await pacer.WaitAsync(deadline, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                (reason, error) = Classify(lifetime, ex);
                break;
            }

            if (!emit)
            {
                reason = EndReason.Completed;
                break;
            }

            var now = timeProvider.GetUtcNow();
            sequence++;

            var serverTime = now.ToUnixTimeMilliseconds();
            int written;
            int consumed;
            while (!encoder.TryAppend(buffer, sequence, serverTime, offset, out written, out consumed))
            {
                // Only reachable when escaping expands the payload past the
                // recommended buffer. The ASCII default document never does, so this
                // loop is a safety net rather than a hot path.
                buffer = new byte[buffer.Length * 2];
            }

            offset += consumed;

            try
            {
                await sink.WriteFrameAsync(sequence, buffer.AsMemory(0, written), token).ConfigureAwait(false);
            }
            catch (StreamWriteTimeoutException ex)
            {
                // The sink's own deadline expired: the peer is still connected but
                // has stopped reading. That is a write error, not a disconnect.
                (reason, error) = (EndReason.WriteError, ex);
                break;
            }
            catch (OperationCanceledException ex)
            {
                (reason, error) = token.IsCancellationRequested
                    ? Classify(lifetime, ex)
                    : (EndReason.WriteError, ex);
                break;
            }
            catch (Exception ex)
            {
                (reason, error) = (EndReason.WriteError, ex);
                break;
            }

            // Counted only after the write succeeded, so a frame that failed to go
            // out is never reported as delivered.
            frames = sequence;
            bytesSent += (ulong)written;
            counters?.IncrementFramesSent();
            counters?.AddBytesSent((ulong)written);
            hooks.OnFrame?.Invoke(sequence, written);

            if (frames >= totalFrames)
            {
                reason = EndReason.Completed;
                break;
            }
        }

        return new StreamResult
        {
            StartedAt = startedAt,
            EndedAt = timeProvider.GetUtcNow(),
            Frames = frames,
            BytesSent = bytesSent,
            Reason = reason,
            Error = error,
        };
    }

    /// <summary>
    /// Maps a cancellation to an end reason. A cancellation with no recorded reason is
    /// treated as the peer going away, which is what the common case actually is: the
    /// request token firing because the client disconnected.
    /// </summary>
    private static (EndReason Reason, Exception? Error) Classify(IStreamLifetime lifetime, Exception cause) =>
        lifetime.CancellationReason == EndReason.ServerShutdown
            ? (EndReason.ServerShutdown, cause)
            : (EndReason.ClientClosed, cause);
}

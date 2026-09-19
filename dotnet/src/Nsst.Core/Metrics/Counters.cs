using System.Text.Json.Serialization;

namespace Nsst.Core.Metrics;

/// <summary>
/// Server wide stream statistics.
/// </summary>
/// <remarks>
/// The counters are deliberately tiny
/// and lock free: the process serves long lived streaming connections and the
/// per frame hot path must not contend on a mutex. Every field is touched with
/// <see cref="Interlocked"/>, which is what makes them safe to read from
/// <c>/api/metrics</c> while streams are writing to them.
/// </remarks>
public sealed class Counters
{
    private long _activeStreams;
    private ulong _streamsStarted;
    private ulong _streamsFinished;
    private ulong _cancelledStreams;
    private ulong _erroredStreams;
    private ulong _rejectedStreams;
    private ulong _framesSent;
    private ulong _bytesSent;

    /// <summary>Streams currently running. Signed because it is incremented and decremented.</summary>
    public long ActiveStreams => Interlocked.Read(ref _activeStreams);

    public ulong StreamsStarted => Interlocked.Read(ref _streamsStarted);

    public ulong StreamsFinished => Interlocked.Read(ref _streamsFinished);

    public ulong CancelledStreams => Interlocked.Read(ref _cancelledStreams);

    public ulong ErroredStreams => Interlocked.Read(ref _erroredStreams);

    public ulong RejectedStreams => Interlocked.Read(ref _rejectedStreams);

    public ulong FramesSent => Interlocked.Read(ref _framesSent);

    public ulong BytesSent => Interlocked.Read(ref _bytesSent);

    public void AddActiveStreams(long delta) => Interlocked.Add(ref _activeStreams, delta);

    public void IncrementStreamsStarted() => Interlocked.Increment(ref _streamsStarted);

    public void IncrementStreamsFinished() => Interlocked.Increment(ref _streamsFinished);

    public void IncrementCancelledStreams() => Interlocked.Increment(ref _cancelledStreams);

    public void IncrementErroredStreams() => Interlocked.Increment(ref _erroredStreams);

    public void IncrementRejectedStreams() => Interlocked.Increment(ref _rejectedStreams);

    public void IncrementFramesSent() => Interlocked.Increment(ref _framesSent);

    public void AddBytesSent(ulong bytes) => Interlocked.Add(ref _bytesSent, bytes);

    /// <summary>Reads every counter into a serialisable copy.</summary>
    public Snapshot TakeSnapshot() => new()
    {
        ActiveStreams = ActiveStreams,
        StreamsStarted = StreamsStarted,
        StreamsFinished = StreamsFinished,
        CancelledStreams = CancelledStreams,
        ErroredStreams = ErroredStreams,
        RejectedStreams = RejectedStreams,
        FramesSent = FramesSent,
        BytesSent = BytesSent,
    };
}

/// <summary>
/// A point in time copy of <see cref="Counters"/>, safe to serialise.
/// </summary>
/// <remarks>
/// The JSON names are part of the public contract consumed by the browser
/// console, which is what pins them here. Note that the wire name for
/// <see cref="CancelledStreams"/> is <c>streams_cancelled</c> rather than
/// <c>cancelled_streams</c>; the asymmetry with the property name is deliberate,
/// because the console parses the former.
/// </remarks>
public sealed record Snapshot
{
    [JsonPropertyName("active_streams")]
    public required long ActiveStreams { get; init; }

    [JsonPropertyName("streams_started")]
    public required ulong StreamsStarted { get; init; }

    [JsonPropertyName("streams_finished")]
    public required ulong StreamsFinished { get; init; }

    [JsonPropertyName("streams_cancelled")]
    public required ulong CancelledStreams { get; init; }

    [JsonPropertyName("streams_errored")]
    public required ulong ErroredStreams { get; init; }

    [JsonPropertyName("streams_rejected")]
    public required ulong RejectedStreams { get; init; }

    [JsonPropertyName("frames_sent")]
    public required ulong FramesSent { get; init; }

    [JsonPropertyName("bytes_sent")]
    public required ulong BytesSent { get; init; }
}

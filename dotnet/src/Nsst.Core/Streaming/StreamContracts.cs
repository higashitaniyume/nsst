using Nsst.Core.Metrics;

namespace Nsst.Core.Streaming;

/// <summary>
/// Transports an encoded frame over a concrete protocol.
/// </summary>
/// <remarks>
/// Implementations own their own framing (newline, SSE fields, WebSocket message)
/// and must unblock as soon as the token is cancelled.
/// <para>
/// <paramref name="sequence"/> is passed alongside the encoded frame because some
/// protocols need it outside the JSON body, e.g. the SSE <c>id:</c> field.
/// </para>
/// </remarks>
public interface IStreamSink
{
    ValueTask WriteFrameAsync(ulong sequence, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);
}

/// <summary>
/// The lifetime a stream runs under: a cancellation token plus the reason it was
/// cancelled.
/// </summary>
/// <remarks>
/// <see cref="CancellationTokenSource.Cancel()"/> carries no reason, so the reason
/// is modelled explicitly here — which is what lets a write timeout, a client
/// disconnect and a server shutdown be told apart without inspecting exception
/// types.
/// </remarks>
public interface IStreamLifetime
{
    /// <summary>Cancelled when the client disconnects or the server shuts down.</summary>
    CancellationToken Cancellation { get; }

    /// <summary>Why <see cref="Cancellation"/> fired. Only meaningful once it has.</summary>
    EndReason CancellationReason { get; }
}

/// <summary>
/// Thrown by a sink when its own per frame write deadline expires.
/// </summary>
/// <remarks>
/// This exists so the emit loop can tell "the peer is gone" apart from "the peer
/// is connected but has stopped reading". A dedicated type is less ambiguous than
/// matching on a cancellation exception that may have come from either the request
/// token or an internal timeout token.
/// <para>
/// It deliberately does <em>not</em> derive from
/// <see cref="OperationCanceledException"/>, so the two cases can never be
/// confused by catch ordering.
/// </para>
/// </remarks>
public sealed class StreamWriteTimeoutException : Exception
{
    public StreamWriteTimeoutException(string message)
        : base(message)
    {
    }

    public StreamWriteTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Per frame notifications. Every hook is optional.</summary>
public sealed record StreamHooks
{
    /// <summary>A no-op hook set.</summary>
    public static StreamHooks None { get; } = new();

    /// <summary>Called after a frame has been written successfully.</summary>
    public Action<ulong, int>? OnFrame { get; init; }
}

/// <summary>Summarises one completed emit loop.</summary>
public sealed record StreamResult
{
    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset EndedAt { get; init; }

    /// <summary>Frames written successfully. A frame that failed to write is not counted.</summary>
    public required ulong Frames { get; init; }

    public required ulong BytesSent { get; init; }

    public required EndReason Reason { get; init; }

    public Exception? Error { get; init; }

    /// <summary>Wall clock duration of the emit loop.</summary>
    public TimeSpan Elapsed => EndedAt - StartedAt;

    /// <summary>Whether the loop ran to its natural end.</summary>
    public bool Completed => Reason == EndReason.Completed;
}

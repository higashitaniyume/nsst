namespace Nsst.Core.Streaming;

/// <summary>
/// A granted slot to run one stream.
/// </summary>
/// <remarks>
/// The lease is the stream's
/// <see cref="IStreamLifetime"/>: it exposes the token the emit loop watches and
/// knows whether the cancellation came from the client or from the server.
/// <para>
/// Release is idempotent and safe to call from a <c>finally</c> block, which is
/// how the API handlers are expected to use it.
/// </para>
/// </remarks>
public sealed class StreamLease : IStreamLifetime, IDisposable
{
    private readonly StreamManager _manager;
    private readonly CancellationTokenSource _linked;
    private readonly CancellationToken _serverShutdownToken;

    private int _released;

    internal StreamLease(StreamManager manager, ulong id, CancellationTokenSource linked, DateTimeOffset startedAt)
    {
        _manager = manager;
        _linked = linked;
        _serverShutdownToken = manager.ServerShutdownToken;
        Id = id;
        StartedAt = startedAt;
    }

    /// <summary>Monotonically increasing lease id, useful for correlating log lines.</summary>
    public ulong Id { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>Cancelled when the client disconnects or the server shuts down.</summary>
    public CancellationToken Cancellation => _linked.Token;

    /// <summary>
    /// True when the server shut down rather than the client leaving.
    /// </summary>
    /// <remarks>
    /// Asking the server token directly is what makes the distinction exact: a
    /// linked source fires for either reason and does not record which.
    /// </remarks>
    public EndReason CancellationReason =>
        _serverShutdownToken.IsCancellationRequested ? EndReason.ServerShutdown : EndReason.ClientClosed;

    /// <summary>
    /// Returns the lease's slot to the manager.
    /// </summary>
    /// <remarks>
    /// Cancelling the linked source is what unblocks anything still awaiting the
    /// lease's token; no reason is attached because <see cref="CancellationReason"/>
    /// is derived from the server token rather than from this source. Disposing
    /// afterwards unregisters from the parent and server tokens so a later shutdown
    /// cannot reach a released lease.
    /// </remarks>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        _linked.Cancel();
        _manager.OnLeaseReleased();
        _linked.Dispose();
    }

    public void Dispose() => Release();
}

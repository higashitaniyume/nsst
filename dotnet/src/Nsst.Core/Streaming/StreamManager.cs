using Nsst.Core.Metrics;

namespace Nsst.Core.Streaming;

/// <summary>Why a stream request was refused before it could start.</summary>
public enum StreamRejectionReason
{
    /// <summary>The server is draining; the caller should retry against another instance or later.</summary>
    ServerShuttingDown,

    /// <summary>The concurrent stream budget is used up.</summary>
    TooManyStreams,

    /// <summary>The client was already gone by the time the stream was requested.</summary>
    ClientGone,
}

/// <summary>
/// Thrown when a stream lease cannot be granted, or when the caller was already
/// cancelled. The API layer maps this to a status code and an error body.
/// </summary>
/// <remarks>
/// One exception type rather than a sentinel per cause, widened to carry the "caller
/// already gone" case so that the API layer does not have to re-inspect the request
/// token.
/// </remarks>
public sealed class StreamRejectedException : Exception
{
    public StreamRejectedException(StreamRejectionReason reason)
        : base(Describe(reason))
    {
        Reason = reason;
    }

    public StreamRejectionReason Reason { get; }

    /// <summary>
    /// The message text. It travels verbatim in the JSON error body, so it is the wording an
    /// operator reads in the console rather than a log-only string.
    /// </summary>
    private static string Describe(StreamRejectionReason reason) => reason switch
    {
        StreamRejectionReason.ServerShuttingDown => "streamtest: server is shutting down",
        StreamRejectionReason.TooManyStreams => "streamtest: too many concurrent streams",
        StreamRejectionReason.ClientGone => "streamtest: client is gone",
        _ => "streamtest: stream rejected",
    };
}

/// <summary>
/// Hands out stream leases, enforcing the concurrent stream budget and tying every
/// stream lifetime to the process lifecycle.
/// </summary>
/// <remarks>
/// Graceful shutdown works by rejecting new
/// streams, waiting for the running ones, and then forcibly cancelling whatever is
/// left when the drain deadline expires.
/// </remarks>
public sealed class StreamManager : IDisposable
{
    /// <summary>How often the drain loop re-checks the active count.</summary>
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(20);

    private readonly Counters _counters;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _serverShutdown = new();

    private long _active;
    private volatile bool _draining;
    private ulong _nextId;

    public StreamManager(Limits limits, Counters? counters = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _counters = counters ?? new Counters();
        _timeProvider = timeProvider ?? TimeProvider.System;

        var slots = limits.MaxConcurrentStreams < 1 ? 1 : limits.MaxConcurrentStreams;

        // The semaphore is never awaited: Acquire tries it once and fails fast so the
        // caller can answer 503 immediately rather than queueing behind a slow test.
        _slots = new SemaphoreSlim(slots, slots);
    }

    /// <summary>The server wide counters.</summary>
    public Counters Counters => _counters;

    /// <summary>Number of streams currently running.</summary>
    public long Active => Interlocked.Read(ref _active);

    /// <summary>Whether the server has started shutting down.</summary>
    public bool Draining => _draining;

    /// <summary>Cancelled by <see cref="ForceClose"/>, which ends every live lease.</summary>
    internal CancellationToken ServerShutdownToken => _serverShutdown.Token;

    /// <summary>
    /// Rejects new streams. Already running streams keep going until
    /// <see cref="DrainAsync"/> returns or <see cref="ForceClose"/> is called.
    /// </summary>
    public void StartDraining() => _draining = true;

    /// <summary>Cancels every running stream, reported as a server shutdown.</summary>
    public void ForceClose() => _serverShutdown.Cancel();

    /// <summary>
    /// Waits until every active stream has released its lease.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> fired before the streams finished, which
    /// means the drain deadline expired and the caller should force close.
    /// </exception>
    public async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Read(ref _active) == 0)
        {
            return;
        }

        // Polled rather than signalled through a TaskCompletionSource: the emit loops
        // are already on the hot path, and 20ms of latency at shutdown is invisible
        // next to the 15s drain budget.
        using var timer = new PeriodicTimer(DrainPollInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Interlocked.Read(ref _active) == 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Reserves a stream slot.
    /// </summary>
    /// <param name="parent">
    /// The request's cancellation token. Cancelling it releases the lease's own
    /// token, which is how a client disconnect stops an emit loop.
    /// </param>
    /// <exception cref="StreamRejectedException">No slot is available, or the server is draining.</exception>
    public StreamLease Acquire(CancellationToken parent)
    {
        if (_draining)
        {
            _counters.IncrementRejectedStreams();
            throw new StreamRejectedException(StreamRejectionReason.ServerShuttingDown);
        }

        if (parent.IsCancellationRequested)
        {
            // No counter is bumped here: the peer never got a stream, so this is not
            // a rejection the server caused.
            throw new StreamRejectedException(StreamRejectionReason.ClientGone);
        }

        if (!_slots.Wait(0))
        {
            _counters.IncrementRejectedStreams();
            throw new StreamRejectedException(StreamRejectionReason.TooManyStreams);
        }

        // Linking the request token with the server shutdown token is what makes either
        // source end the stream, while the lease can still tell which one it was.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(parent, _serverShutdown.Token);
        var id = Interlocked.Increment(ref _nextId);

        Interlocked.Increment(ref _active);
        _counters.AddActiveStreams(1);
        _counters.IncrementStreamsStarted();

        return new StreamLease(this, id, linked, _timeProvider.GetUtcNow());
    }

    /// <summary>Increments the completion counters for a finished stream.</summary>
    public void Record(StreamResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Reason)
        {
            case EndReason.Completed:
                _counters.IncrementStreamsFinished();
                break;
            case EndReason.WriteError:
                _counters.IncrementErroredStreams();
                break;
            default:
                _counters.IncrementCancelledStreams();
                break;
        }
    }

    /// <summary>Returns a point in time copy of the server wide counters.</summary>
    public Snapshot TakeSnapshot() => _counters.TakeSnapshot();

    /// <summary>Returns a lease's slot to the manager. Called by <see cref="StreamLease.Release"/>.</summary>
    internal void OnLeaseReleased()
    {
        Interlocked.Decrement(ref _active);
        _counters.AddActiveStreams(-1);
        _slots.Release();
    }

    public void Dispose()
    {
        _slots.Dispose();
        _serverShutdown.Dispose();
    }
}

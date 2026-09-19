using Microsoft.Extensions.Time.Testing;
using Nsst.Core.Streaming;
using Xunit;

namespace Nsst.Core.Tests;

/// <summary>
/// The concurrency budget and the shutdown handshake.
/// </summary>
public sealed class StreamManagerTests
{
    private static Limits WithSlots(int slots) => Limits.Default with { MaxConcurrentStreams = slots };

    [Fact]
    public void RefusesWhenTheBudgetIsExhausted()
    {
        using var manager = new StreamManager(WithSlots(2));

        using var first = manager.Acquire(CancellationToken.None);
        using var second = manager.Acquire(CancellationToken.None);

        var rejected = Assert.Throws<StreamRejectedException>(() => manager.Acquire(CancellationToken.None));
        Assert.Equal(StreamRejectionReason.TooManyStreams, rejected.Reason);

        Assert.Equal(2, manager.Active);
        Assert.Equal(1UL, manager.Counters.RejectedStreams);
        Assert.Equal(2UL, manager.Counters.StreamsStarted);
        Assert.Equal(2, manager.Counters.ActiveStreams);
    }

    [Fact]
    public void FreesTheSlotOnRelease()
    {
        using var manager = new StreamManager(WithSlots(1));

        var lease = manager.Acquire(CancellationToken.None);
        Assert.Throws<StreamRejectedException>(() => manager.Acquire(CancellationToken.None));

        lease.Release();

        Assert.Equal(0, manager.Active);
        Assert.Equal(0, manager.Counters.ActiveStreams);
        using var second = manager.Acquire(CancellationToken.None);
        Assert.Equal(1, manager.Active);
    }

    /// <summary>
    /// Handlers release from a <c>finally</c>, which can also run on paths that
    /// already released. A double release must not free a slot twice.
    /// </summary>
    [Fact]
    public void ReleaseIsIdempotent()
    {
        using var manager = new StreamManager(WithSlots(1));

        var lease = manager.Acquire(CancellationToken.None);
        lease.Release();
        lease.Release();
        lease.Dispose();

        Assert.Equal(0, manager.Active);
        Assert.Equal(0, manager.Counters.ActiveStreams);

        // A leaked semaphore permit would show up as an extra stream being allowed.
        using var first = manager.Acquire(CancellationToken.None);
        Assert.Throws<StreamRejectedException>(() => manager.Acquire(CancellationToken.None));
    }

    [Fact]
    public void RefusesNewStreamsWhileDraining()
    {
        using var manager = new StreamManager(Limits.Default);
        manager.StartDraining();

        Assert.True(manager.Draining);

        var rejected = Assert.Throws<StreamRejectedException>(() => manager.Acquire(CancellationToken.None));
        Assert.Equal(StreamRejectionReason.ServerShuttingDown, rejected.Reason);
        Assert.Equal(1UL, manager.Counters.RejectedStreams);
    }

    /// <summary>
    /// A peer that vanished before the stream started is not a server rejection, so
    /// it must not inflate <c>streams_rejected</c>.
    /// </summary>
    [Fact]
    public void DoesNotCountAnAlreadyGoneClientAsARejection()
    {
        using var manager = new StreamManager(Limits.Default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var rejected = Assert.Throws<StreamRejectedException>(() => manager.Acquire(cancellation.Token));
        Assert.Equal(StreamRejectionReason.ClientGone, rejected.Reason);
        Assert.Equal(0UL, manager.Counters.RejectedStreams);
        Assert.Equal(0L, manager.Active);
    }

    [Fact]
    public void DistinguishesServerShutdownFromAClientDisconnect()
    {
        using var manager = new StreamManager(Limits.Default);

        using var clientGone = new CancellationTokenSource();
        using var fromClient = manager.Acquire(clientGone.Token);
        clientGone.Cancel();
        Assert.True(fromClient.Cancellation.IsCancellationRequested);
        Assert.Equal(EndReason.ClientClosed, fromClient.CancellationReason);

        using var fromServer = manager.Acquire(CancellationToken.None);
        Assert.False(fromServer.Cancellation.IsCancellationRequested);
        manager.ForceClose();
        Assert.True(fromServer.Cancellation.IsCancellationRequested);
        Assert.Equal(EndReason.ServerShutdown, fromServer.CancellationReason);
    }

    [Fact]
    public async Task DrainReturnsOnceEveryLeaseIsReleased()
    {
        var clock = new FakeTimeProvider();
        using var manager = new StreamManager(Limits.Default, null, clock);

        var lease = manager.Acquire(CancellationToken.None);
        var drain = manager.DrainAsync(CancellationToken.None).AsTask();
        Assert.False(drain.IsCompleted);

        lease.Release();
        clock.Advance(TimeSpan.FromMilliseconds(20));

        await drain.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Drain returning early is what tells the shutdown sequence to force close, so
    /// its timeout has to surface rather than being swallowed.
    /// </summary>
    [Fact]
    public async Task DrainThrowsWhenTheDeadlineExpiresWithStreamsStillRunning()
    {
        var clock = new FakeTimeProvider();
        using var manager = new StreamManager(Limits.Default, null, clock);

        using var lease = manager.Acquire(CancellationToken.None);
        using var deadline = new CancellationTokenSource();
        var drain = manager.DrainAsync(deadline.Token).AsTask();

        deadline.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await drain.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task DrainReturnsImmediatelyWhenNothingIsRunning()
    {
        using var manager = new StreamManager(Limits.Default);
        await manager.DrainAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RecordsEachEndReasonAgainstTheRightCounter()
    {
        using var manager = new StreamManager(Limits.Default);

        manager.Record(Result(EndReason.Completed));
        manager.Record(Result(EndReason.WriteError));
        manager.Record(Result(EndReason.ClientClosed));
        manager.Record(Result(EndReason.ServerShutdown));

        Assert.Equal(1UL, manager.Counters.StreamsFinished);
        Assert.Equal(1UL, manager.Counters.ErroredStreams);
        Assert.Equal(2UL, manager.Counters.CancelledStreams);

        var snapshot = manager.TakeSnapshot();
        Assert.Equal(1UL, snapshot.StreamsFinished);
        Assert.Equal(2UL, snapshot.CancelledStreams);
    }

    private static StreamResult Result(EndReason reason) => new()
    {
        StartedAt = DateTimeOffset.UnixEpoch,
        EndedAt = DateTimeOffset.UnixEpoch,
        Frames = 0,
        BytesSent = 0,
        Reason = reason,
    };
}

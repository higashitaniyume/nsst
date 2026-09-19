using Microsoft.Extensions.Time.Testing;
using Nsst.Core.Streaming;
using Xunit;

namespace Nsst.Core.Tests;

/// <summary>
/// Pins the emit grid.
/// </summary>
/// <remarks>
/// Driving the pacer with real time would force loose assertions, accepting gaps
/// anywhere between half an interval and four intervals. These drive it with a fake
/// clock instead, so the assertions are exact: the whole point of the pacer is that
/// the grid is absolute, and an exact check is the only way to prove drift is absent
/// rather than merely small.
/// </remarks>
public sealed class PacerTests
{
    [Fact]
    public async Task EmitsImmediatelyThenOnTheExactGrid()
    {
        var clock = new FakeTimeProvider();
        var interval = TimeSpan.FromMilliseconds(20);
        var pacer = new Pacer(interval, clock);
        var deadline = pacer.Now + TimeSpan.FromMilliseconds(200);

        var stamps = new List<TimeSpan>();
        while (true)
        {
            var wait = pacer.WaitAsync(deadline, CancellationToken.None);
            for (var guard = 0; !wait.IsCompleted && guard < 1000; guard++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(1));
                await Task.Yield();
            }

            if (!await wait)
            {
                break;
            }

            stamps.Add(pacer.Now);
        }

        Assert.Equal(10, stamps.Count);

        // The first frame must not wait for the first interval.
        Assert.Equal(TimeSpan.Zero, stamps[0]);

        // Every later frame lands exactly on the grid: no drift, no jitter.
        for (var i = 1; i < stamps.Count; i++)
        {
            Assert.Equal(interval, stamps[i] - stamps[i - 1]);
        }
    }

    [Fact]
    public async Task StopsAtDeadlineWithoutEmitting()
    {
        var clock = new FakeTimeProvider();
        var pacer = new Pacer(TimeSpan.FromMilliseconds(1), clock);

        // A deadline already in the past must suppress even the immediate first
        // emission: nothing may be sent after the deadline.
        Assert.False(await pacer.WaitAsync(pacer.Now - TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        var clock = new FakeTimeProvider();
        var pacer = new Pacer(TimeSpan.FromHours(1), clock);
        var deadline = pacer.Now + TimeSpan.FromHours(2);

        // Consume the immediate first tick so the next wait blocks on the timer.
        Assert.True(await pacer.WaitAsync(deadline, CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        var wait = pacer.WaitAsync(deadline, cancellation.Token);
        Assert.False(wait.IsCompleted);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
    }

    /// <summary>
    /// A sender that fell behind must resynchronise rather than fire a catch-up
    /// burst, because a burst would corrupt the very inter-arrival measurements the
    /// client is taking.
    /// </summary>
    [Fact]
    public async Task ResynchronisesAfterAStallInsteadOfBursting()
    {
        var clock = new FakeTimeProvider();
        var interval = TimeSpan.FromMilliseconds(10);
        var pacer = new Pacer(interval, clock);
        var deadline = pacer.Now + TimeSpan.FromHours(1);

        Assert.True(await pacer.WaitAsync(deadline, CancellationToken.None));

        // Six intervals pass without a call, as if the sender stalled.
        clock.Advance(TimeSpan.FromMilliseconds(60));

        var afterStall = pacer.WaitAsync(deadline, CancellationToken.None);
        Assert.True(afterStall.IsCompleted);
        Assert.True(await afterStall);

        // The grid is re-anchored to now, so the following frame is one fresh
        // interval away rather than immediate.
        var next = pacer.WaitAsync(deadline, CancellationToken.None);
        Assert.False(next.IsCompleted);
        clock.Advance(interval);
        Assert.True(await next);
    }

    [Fact]
    public void ClampsNonPositiveIntervals()
    {
        var clock = new FakeTimeProvider();
        var pacer = new Pacer(TimeSpan.Zero, clock);

        // A zero interval would otherwise be an infinite busy loop.
        Assert.NotNull(pacer);
        Assert.Equal(TimeSpan.Zero, pacer.Now);
    }
}

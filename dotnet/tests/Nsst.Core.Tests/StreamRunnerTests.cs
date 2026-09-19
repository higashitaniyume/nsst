using Microsoft.Extensions.Time.Testing;
using Nsst.Core.Metrics;
using Nsst.Core.Protocol;
using Nsst.Core.Streaming;
using Xunit;

namespace Nsst.Core.Tests;

/// <summary>
/// The emit loop, driven by a fake clock so frame counts are exact rather than
/// approximately right.
/// </summary>
public sealed class StreamRunnerTests
{
    [Fact]
    public async Task EmitsExactlyTheExpectedNumberOfFrames()
    {
        var clock = new FakeTimeProvider();
        var parameters = new StreamParams(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), 64);
        var sink = new RecordingSink();

        var result = await RunToCompletionAsync(clock, parameters, sink);

        Assert.True(result.Completed, $"reason={result.Reason} error={result.Error}");

        var (frames, bytes) = sink.Snapshot();
        Assert.Equal(parameters.ExpectedFrames, frames.Count);
        Assert.Equal(result.Frames, (ulong)frames.Count);
        Assert.Equal(result.BytesSent, (ulong)bytes);

        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal((ulong)(i + 1), frames[i].Sequence);
            Assert.Equal(parameters.PayloadSize, frames[i].PayloadSize);
            FrameAssertions.AssertWellFormed(frames[i], i);
        }

        for (var i = 1; i < frames.Count; i++)
        {
            Assert.True(
                frames[i].ServerTime >= frames[i - 1].ServerTime,
                $"server_time went backwards at frame {i}");
        }
    }

    /// <summary>
    /// The first frame must go out at t=0 so the client can measure first frame
    /// latency. With a fake clock this is exact: one frame exists while zero time has
    /// passed, which a real-clock test could only describe as "quickly".
    /// </summary>
    [Fact]
    public async Task WritesTheFirstFrameWithoutAdvancingTheClock()
    {
        var clock = new FakeTimeProvider();
        var parameters = new StreamParams(TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), 16);
        var sink = new RecordingSink();

        using var cancellation = new CancellationTokenSource();
        var startedAt = clock.GetUtcNow();

        var task = StreamRunner.RunAsync(
            new TestLifetime(cancellation.Token),
            parameters,
            FrameEncoder.For(parameters.PayloadSize),
            sink,
            null,
            StreamHooks.None,
            clock).AsTask();

        Assert.Equal(startedAt, clock.GetUtcNow());
        Assert.Equal(1, sink.FrameCount);

        cancellation.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EndReason.ClientClosed, result.Reason);
    }

    /// <summary>
    /// A frame that failed to write must not be reported as delivered, so the
    /// sequence counter and the frame counter diverge by exactly one.
    /// </summary>
    [Fact]
    public async Task ReportsWriteErrorsAndDoesNotCountTheFailedFrame()
    {
        var clock = new FakeTimeProvider();
        var parameters = new StreamParams(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), 0);
        var failure = new IOException("connection reset");
        var sink = new RecordingSink { FailOn = 3, FailError = failure };

        var result = await RunToCompletionAsync(clock, parameters, sink);

        Assert.Equal(EndReason.WriteError, result.Reason);
        Assert.Same(failure, result.Error);
        Assert.Equal(2, sink.FrameCount);
        Assert.Equal(2UL, result.Frames);
    }

    /// <summary>
    /// A sink that hits its own per frame deadline reports a write error, not a
    /// client disconnect, even though the peer is still connected.
    /// </summary>
    [Fact]
    public async Task ReportsAStalledReaderAsAWriteErrorNotADisconnect()
    {
        var clock = new FakeTimeProvider();
        var parameters = new StreamParams(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), 0);
        var sink = new StallingSink(failOn: 2);

        var result = await RunToCompletionAsync(clock, parameters, sink);

        Assert.Equal(EndReason.WriteError, result.Reason);
        Assert.IsType<StreamWriteTimeoutException>(result.Error);
        Assert.Equal(1UL, result.Frames);
    }

    [Fact]
    public async Task ReportsServerShutdownWhenTheManagerForceCloses()
    {
        var clock = new FakeTimeProvider();
        using var manager = new StreamManager(Limits.Default, null, clock);
        using var lease = manager.Acquire(CancellationToken.None);

        var parameters = new StreamParams(TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(10), 0);
        var sink = new RecordingSink();

        var task = StreamRunner.RunAsync(
            lease,
            parameters,
            FrameEncoder.For(0),
            sink,
            null,
            StreamHooks.None,
            clock).AsTask();

        await AdvanceUntilAsync(clock, () => sink.FrameCount >= 3, TimeSpan.FromMilliseconds(1));

        manager.ForceClose();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EndReason.ServerShutdown, result.Reason);
    }

    [Fact]
    public async Task InvokesHooksAndUpdatesCounters()
    {
        var clock = new FakeTimeProvider();
        var parameters = new StreamParams(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100), 8);
        var sink = new RecordingSink();
        var counters = new Counters();
        var hooked = 0;
        var hooks = new StreamHooks
        {
            OnFrame = (_, wireBytes) =>
            {
                hooked++;
                Assert.True(wireBytes > 0, "the hook must see the encoded frame length");
            },
        };

        var result = await RunToCompletionAsync(clock, parameters, sink, counters, hooks);

        Assert.Equal((int)result.Frames, hooked);
        Assert.Equal(result.Frames, counters.FramesSent);
        Assert.Equal(result.BytesSent, counters.BytesSent);
        Assert.Equal(0, counters.ActiveStreams);
    }

    /// <summary>
    /// Runs a stream to its natural end by stepping a fake clock.
    /// </summary>
    /// <remarks>
    /// The step is a tenth of the interval so the emit loop always gets several
    /// chances to reach its next timer between advances. If it ever fell more than a
    /// whole interval behind, the pacer would resynchronise and drop a slot — that
    /// behaviour is correct, but it would make this test measure the test harness
    /// rather than the runner.
    /// </remarks>
    private static async Task<StreamResult> RunToCompletionAsync(
        FakeTimeProvider clock,
        StreamParams parameters,
        IStreamSink sink,
        Counters? counters = null,
        StreamHooks? hooks = null)
    {
        var task = StreamRunner.RunAsync(
            TestLifetime.Never,
            parameters,
            FrameEncoder.For(parameters.PayloadSize),
            sink,
            counters,
            hooks ?? StreamHooks.None,
            clock).AsTask();

        var step = TimeSpan.FromTicks(Math.Max(1, parameters.Interval.Ticks / 10));
        var budget = (int)(parameters.Duration.Ticks / step.Ticks)
                     + (int)(parameters.Interval.Ticks / step.Ticks)
                     + 32;

        await AdvanceUntilAsync(clock, () => task.IsCompleted, step, budget);
        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task AdvanceUntilAsync(
        FakeTimeProvider clock,
        Func<bool> done,
        TimeSpan step,
        int maxSteps = 20000)
    {
        for (var i = 0; i < maxSteps && !done(); i++)
        {
            clock.Advance(step);
            await Task.Yield();
        }
    }
}

/// <summary>A sink whose write deadline expires at a chosen frame.</summary>
internal sealed class StallingSink : IStreamSink
{
    private readonly ulong _failOn;
    private int _written;

    public StallingSink(ulong failOn) => _failOn = failOn;

    public int Written => _written;

    public ValueTask WriteFrameAsync(ulong sequence, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (sequence == _failOn)
        {
            throw new StreamWriteTimeoutException("the client stopped reading");
        }

        _written++;
        return ValueTask.CompletedTask;
    }
}

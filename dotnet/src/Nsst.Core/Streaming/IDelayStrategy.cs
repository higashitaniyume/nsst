namespace Nsst.Core.Streaming;

/// <summary>
/// Waits for the gap between two frames.
/// </summary>
/// <remarks>
/// <para>
/// This is an injected abstraction rather than a direct <c>Task.Delay</c> call because on
/// Windows every .NET waiting primitive is quantised to the 15.625 ms system tick, which
/// makes the documented 10 ms minimum interval unreachable. Measured on this machine, with
/// a 10 ms target:
/// </para>
/// <list type="bullet">
/// <item><c>Task.Delay</c> returns after a median 15.6 ms, with a median deadline error of
/// 6.9 ms and a worst case of 13.9 ms;</item>
/// <item>a high-resolution waitable timer returns after a median 10.3 ms, with a median
/// deadline error of 0.4 ms and a worst case of 0.7 ms.</item>
/// </list>
/// <para>
/// Go is not subject to the tick because its runtime waits on the same high-resolution
/// primitive, which is why the Go server holds a 10.0 ms median. See
/// <c>dotnet/spike/TimerResolution</c> for the harness that produced these numbers.
/// </para>
/// <para>
/// The implementation deliberately lives outside this assembly: <c>Nsst.Core</c> stays free
/// of platform calls, and the host decides what its platform can offer. A test clock must be
/// given <see cref="TaskDelayStrategy"/>, since a kernel timer knows nothing about it.
/// </para>
/// </remarks>
public interface IDelayStrategy
{
    /// <summary>Completes once <paramref name="delay"/> has elapsed.</summary>
    /// <remarks>
    /// A non-positive <paramref name="delay"/> completes immediately rather than throwing,
    /// because the pacer may legitimately be exactly on its deadline.
    /// </remarks>
    ValueTask DelayAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken);
}

/// <summary>
/// The portable strategy: whatever the supplied <see cref="TimeProvider"/> offers.
/// </summary>
/// <remarks>
/// This is the correct choice whenever the clock is not the system clock — a
/// <c>FakeTimeProvider</c> only advances when the test says so, and a real kernel timer
/// would ignore it entirely.
/// </remarks>
public sealed class TaskDelayStrategy : IDelayStrategy
{
    /// <summary>The shared instance; the type is stateless.</summary>
    public static TaskDelayStrategy Instance { get; } = new();

    public ValueTask DelayAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero
            ? ValueTask.CompletedTask
            : new ValueTask(Task.Delay(delay, timeProvider, cancellationToken));
}

namespace Nsst.Core.Streaming;

/// <summary>
/// Schedules frame emissions on a fixed grid anchored at the first call.
/// </summary>
/// <remarks>
/// It exists instead of a bare timer for two
/// reasons:
/// <list type="bullet">
/// <item>the grid is absolute, so a frame delivered slightly late does not
/// permanently shift every later frame — there is no drift accumulation;</item>
/// <item>if the sender falls more than one interval behind (a slow client, a GC
/// pause, a saturated link) the grid is resynchronised rather than emitting a
/// catch-up burst, which would distort the client's inter-arrival
/// measurements.</item>
/// </list>
/// <para>
/// Timing is taken from <see cref="TimeProvider"/> monotonic timestamps rather
/// than wall clock, so an NTP step during a long test cannot make the grid jump.
/// </para>
/// </remarks>
public sealed class Pacer
{
    private readonly TimeSpan _interval;
    private readonly TimeProvider _timeProvider;
    private readonly IDelayStrategy _delay;
    private readonly long _origin;

    private TimeSpan _next;
    private bool _started;

    /// <summary>Creates a pacer that emits frames every <paramref name="interval"/>.</summary>
    /// <param name="delayStrategy">
    /// The wait primitive. Left null the portable <see cref="TaskDelayStrategy"/> is used,
    /// which is what a <see cref="TimeProvider"/>-driven test clock needs; the server passes
    /// a platform strategy able to reach the sub-tick precision this tool depends on.
    /// </param>
    public Pacer(TimeSpan interval, TimeProvider timeProvider, IDelayStrategy? delayStrategy = null)
    {
        _interval = interval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : interval;
        _timeProvider = timeProvider;
        _delay = delayStrategy ?? TaskDelayStrategy.Instance;
        _origin = timeProvider.GetTimestamp();
    }

    /// <summary>Monotonic time since this pacer was created.</summary>
    public TimeSpan Now => _timeProvider.GetElapsedTime(_origin, _timeProvider.GetTimestamp());

    /// <summary>
    /// Waits until the next frame should be emitted.
    /// </summary>
    /// <returns>
    /// False when <paramref name="deadline"/> has already passed and the loop
    /// should finish. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> fires, which the caller classifies.
    /// </returns>
    /// <remarks>Must be called sequentially; a pacer is not safe for concurrent use.</remarks>
    public async ValueTask<bool> WaitAsync(TimeSpan deadline, CancellationToken cancellationToken)
    {
        var now = Now;
        if (!_started)
        {
            _started = true;
            _next = now;
        }

        if (_next > now)
        {
            await _delay.DelayAsync(_next - now, _timeProvider, cancellationToken).ConfigureAwait(false);
            now = Now;
        }

        if (_next - now < -_interval)
        {
            // More than one interval behind: resynchronise rather than burst.
            _next = now;
        }

        if (now >= deadline)
        {
            return false;
        }

        _next += _interval;
        return true;
    }
}

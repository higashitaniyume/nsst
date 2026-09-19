using System.Globalization;
using System.Text;

namespace Nsst.Server.Streaming;

/// <summary>
/// Formats a <see cref="TimeSpan"/> as the compact duration text used in logs and in the
/// stream summary frame: <c>1h0m0s</c>, <c>1m30s</c>, <c>250ms</c>, <c>0</c>.
/// </summary>
/// <remarks>
/// <see cref="TimeSpan.ToString()"/> would emit <c>00:01:00</c>. That is fine for a machine
/// and poor for a human reading a log line at 3am, which is the whole audience for these
/// strings — none of them are parsed back. The format is deliberately the compact one:
/// units are emitted only when they carry information, so a 250 ms interval reads as
/// <c>250ms</c> rather than <c>0h0m0.25s</c>.
/// </remarks>
internal static class DurationText
{
    private const long NanosecondsPerTick = 100;
    private const long Microsecond = 1_000;
    private const long Millisecond = 1_000_000;
    private const long Second = 1_000_000_000;

    public static string Format(TimeSpan value)
    {
        var nanoseconds = value.Ticks * NanosecondsPerTick;
        var negative = nanoseconds < 0;
        var magnitude = (ulong)(negative ? -nanoseconds : nanoseconds);

        var text = magnitude < (ulong)Second ? FormatSubSecond(magnitude) : FormatSecondOrMore(magnitude);
        return negative ? "-" + text : text;
    }

    private static string FormatSubSecond(ulong magnitude)
    {
        if (magnitude == 0)
        {
            return "0";
        }

        if (magnitude < (ulong)Microsecond)
        {
            return $"{magnitude}ns";
        }

        if (magnitude < (ulong)Millisecond)
        {
            return Fraction(magnitude, 3) + "µs";
        }

        return Fraction(magnitude, 6) + "ms";
    }

    private static string FormatSecondOrMore(ulong magnitude)
    {
        var totalSeconds = magnitude / (ulong)Second;
        var subSecond = magnitude % (ulong)Second;

        var seconds = totalSeconds % 60;
        var totalMinutes = totalSeconds / 60;
        var minutes = totalMinutes % 60;
        var hours = totalMinutes / 60;

        var text = new StringBuilder(32);

        // An hour implies the minute component, so 3600s reads as "1h0m0s" and not "1h0s":
        // dropping the zero minute would make "1h30s" ambiguous at a glance.
        if (hours > 0)
        {
            text.Append(hours.ToString(CultureInfo.InvariantCulture)).Append('h');
        }

        if (totalMinutes > 0)
        {
            text.Append(minutes.ToString(CultureInfo.InvariantCulture)).Append('m');
        }

        text.Append(seconds.ToString(CultureInfo.InvariantCulture));
        if (subSecond > 0)
        {
            text.Append('.').Append(TrimmedFraction(subSecond, 9));
        }

        return text.Append('s').ToString();
    }

    /// <summary>
    /// Renders <paramref name="magnitude"/> divided by 10^<paramref name="precision"/>,
    /// trimming trailing zeros so <c>1.500s</c> prints as <c>1.5s</c>.
    /// </summary>
    private static string Fraction(ulong magnitude, int precision)
    {
        var scale = 1UL;
        for (var i = 0; i < precision; i++)
        {
            scale *= 10;
        }

        var whole = magnitude / scale;
        var remainder = magnitude % scale;
        if (remainder == 0)
        {
            return whole.ToString(CultureInfo.InvariantCulture);
        }

        return $"{whole.ToString(CultureInfo.InvariantCulture)}.{TrimmedFraction(remainder, precision)}";
    }

    private static string TrimmedFraction(ulong remainder, int precision) =>
        remainder.ToString(CultureInfo.InvariantCulture).PadLeft(precision, '0').TrimEnd('0');
}

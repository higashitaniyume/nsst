using System.Globalization;
using System.Text;

namespace Nsst.Core.Streaming;

/// <summary>
/// A validated client request.
/// </summary>
public readonly record struct StreamParams(TimeSpan Duration, TimeSpan Interval, int PayloadSize)
{
    /// <summary>
    /// How many frames the server will emit for these parameters.
    /// </summary>
    /// <remarks>
    /// Frames go out at t=0, interval, 2*interval, ... while elapsed time is
    /// strictly below <see cref="Duration"/>, which is <c>ceil(Duration/Interval)</c>
    /// frames. The console uses this to draw a progress bar, so it has to agree
    /// with what the emit loop actually does.
    /// </remarks>
    public long ExpectedFrames
    {
        get
        {
            if (Interval <= TimeSpan.Zero)
            {
                return 1;
            }

            return (Duration.Ticks + Interval.Ticks - 1) / Interval.Ticks;
        }
    }
}

/// <summary>
/// Thrown for a malformed or out of range query parameter. The API layer maps it
/// to HTTP 400.
/// </summary>
/// <remarks>
/// The message shape matters because those strings are surfaced to the
/// operator through the JSON error body.
/// </remarks>
public sealed class ParamException : Exception
{
    public ParamException(string param, string value, string reason)
        : base(Format(param, value, reason))
    {
        Param = param;
        Value = value;
        Reason = reason;
    }

    /// <summary>Name of the offending query parameter.</summary>
    public string Param { get; }

    /// <summary>Offending value; empty when the rejection was not about a value that parsed.</summary>
    public string Value { get; }

    /// <summary>Human readable explanation, without the parameter name.</summary>
    public string Reason { get; }

    private static string Format(string param, string value, string reason) =>
        value.Length == 0
            ? $"parameter \"{param}\" {reason}"
            : $"parameter \"{param}\": {reason} (got \"{value}\")";
}

/// <summary>
/// The part of a query string this layer needs, kept free of any web framework
/// type so <c>Nsst.Core</c> does not depend on ASP.NET Core.
/// </summary>
public interface IQuery
{
    /// <summary>
    /// True when the parameter is present at all. A present-but-blank parameter is
    /// still present, and is treated as absent by the parser so that
    /// <c>?duration=</c> falls back to the default rather than failing.
    /// </summary>
    bool Has(string name);

    /// <summary>The first value of the parameter, or null when it is absent.</summary>
    string? First(string name);
}

/// <summary>
/// Reads and validates <c>duration</c>, <c>interval</c> and <c>payload_size</c>.
/// </summary>
public static class ParamsParser
{
    public static StreamParams Parse(IQuery query, Limits limits)
    {
        var duration = limits.DefaultDuration;
        var interval = limits.DefaultInterval;
        var payloadSize = limits.DefaultPayloadSize;

        if (IntParam(query, "duration") is { } seconds)
        {
            if (seconds <= 0)
            {
                throw Fail("duration", seconds, "must be a positive number of seconds");
            }

            // The bounds are compared in whole seconds rather than by building a
            // TimeSpan first: an absurd value would overflow the conversion instead of
            // producing the clean 400 the caller needs, and comparing the number as the
            // operator typed it is the clearer check anyway.
            var maxSeconds = (long)limits.MaxDuration.TotalSeconds;
            if (seconds > maxSeconds)
            {
                throw Fail("duration", seconds, $"must not exceed {maxSeconds} seconds");
            }

            var minSeconds = (long)limits.MinDuration.TotalSeconds;
            if (seconds < minSeconds)
            {
                throw Fail("duration", seconds, $"must be at least {minSeconds} seconds");
            }

            duration = TimeSpan.FromSeconds(seconds);
        }

        if (IntParam(query, "interval") is { } millis)
        {
            if (millis <= 0)
            {
                throw Fail("interval", millis, "must be a positive number of milliseconds");
            }

            // Note the order: interval reports the lower bound first, while duration
            // reports the upper bound first. The asymmetry is an accident of the original
            // implementation rather than a contract, but the message text is observable —
            // it is shown verbatim in the console — so it is left as it is.
            var minMillis = (long)limits.MinInterval.TotalMilliseconds;
            if (millis < minMillis)
            {
                throw Fail("interval", millis, $"must be at least {minMillis} milliseconds");
            }

            var maxMillis = (long)limits.MaxInterval.TotalMilliseconds;
            if (millis > maxMillis)
            {
                throw Fail("interval", millis, $"must not exceed {maxMillis} milliseconds");
            }

            interval = TimeSpan.FromMilliseconds(millis);
        }

        if (IntParam(query, "payload_size") is { } payload)
        {
            if (payload < 0)
            {
                throw Fail("payload_size", payload, "must not be negative");
            }

            if (payload > limits.MaxPayloadSize)
            {
                throw Fail("payload_size", payload, $"must not exceed {limits.MaxPayloadSize} bytes");
            }

            payloadSize = (int)payload;
        }

        return new StreamParams(duration, interval, payloadSize);
    }

    /// <summary>
    /// Reads an optional integer parameter, returning null when it is absent or
    /// blank so the caller can apply its default.
    /// </summary>
    private static long? IntParam(IQuery query, string name)
    {
        if (!query.Has(name))
        {
            return null;
        }

        var raw = query.First(name)?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            return null;
        }

        if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            throw new ParamException(name, raw, "must be an integer");
        }

        return value;
    }

    /// <summary>
    /// Builds the error for a value that parsed but failed a bound. The reported
    /// value is the parsed integer rather than the raw text, so <c>duration=+5</c>
    /// reports <c>5</c>.
    /// </summary>
    private static ParamException Fail(string param, long value, string reason) =>
        new(param, value.ToString(CultureInfo.InvariantCulture), reason);
}

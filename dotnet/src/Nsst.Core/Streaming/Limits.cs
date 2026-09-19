namespace Nsst.Core.Streaming;

/// <summary>
/// The hard bounds a client request is validated against.
/// </summary>
/// <remarks>
/// Values are derived from environment
/// variables by the configuration layer, which clamps them, so by the time a
/// <see cref="Limits"/> exists every field is already sane.
/// </remarks>
public sealed record Limits(
    TimeSpan MinDuration,
    TimeSpan MaxDuration,
    TimeSpan DefaultDuration,
    TimeSpan MinInterval,
    TimeSpan MaxInterval,
    TimeSpan DefaultInterval,
    int MaxPayloadSize,
    int DefaultPayloadSize,
    int MaxConcurrentStreams,
    TimeSpan WriteTimeout)
{
    /// <summary>
    /// How much of the payload document one frame carries when the client does not
    /// ask for a size: about one line of a terminal.
    /// </summary>
    /// <remarks>
    /// A small default keeps a test looking like a real streaming endpoint — many
    /// small messages rather than a few huge ones — and gives the console a
    /// continuous trickle of text to display.
    /// </remarks>
    public const int DefaultPayloadBytes = 80;

    /// <summary>The documented defaults.</summary>
    public static Limits Default { get; } = new(
        MinDuration: TimeSpan.FromSeconds(1),
        MaxDuration: TimeSpan.FromHours(1),
        DefaultDuration: TimeSpan.FromMinutes(1),
        MinInterval: TimeSpan.FromMilliseconds(10),
        MaxInterval: TimeSpan.FromMinutes(1),
        DefaultInterval: TimeSpan.FromMilliseconds(100),
        MaxPayloadSize: 1 << 20,
        DefaultPayloadSize: DefaultPayloadBytes,
        MaxConcurrentStreams: 100,
        WriteTimeout: TimeSpan.FromSeconds(15));
}

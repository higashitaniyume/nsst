using System.Text.Json.Serialization;

namespace Nsst.Core.Protocol;

/// <summary>
/// The logical unit streamed by every endpoint.
/// </summary>
/// <remarks>
/// The field names are part of the public wire
/// contract shared with the browser console — do not rename them.
/// </remarks>
public sealed record Frame
{
    /// <summary>Monotonically increasing per stream, starting at 1.</summary>
    [JsonPropertyName("sequence")]
    public required ulong Sequence { get; init; }

    /// <summary>Unix timestamp in milliseconds when the server produced the frame.</summary>
    [JsonPropertyName("server_time")]
    public required long ServerTime { get; init; }

    /// <summary>Number of payload bytes carried by this frame.</summary>
    [JsonPropertyName("payload_size")]
    public required int PayloadSize { get; init; }

    /// <summary>Filler bytes taken from the payload document; omitted when <see cref="PayloadSize"/> is 0.</summary>
    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Payload { get; init; }
}

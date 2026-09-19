using System.Text.Encodings.Web;
using System.Text.Json;

namespace Nsst.Core.Protocol;

/// <summary>
/// Reads and writes <see cref="Frame"/> as JSON.
/// </summary>
/// <remarks>
/// This is the reference representation, used by tests and tooling. It is
/// deliberately NOT what the streaming hot path uses — <see cref="FrameEncoder"/>
/// writes the same field names but allocates nothing per frame.
/// <para>
/// The two are not interchangeable. This one uses the framework's default encoder,
/// which HTML-escapes <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c>;
/// <see cref="FrameEncoder"/> deliberately does not, because the console parses the
/// frame bytes the server actually sends. A test pins the difference so nobody
/// "simplifies" them into one.
/// </para>
/// </remarks>
public static class FrameJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.Default,
    };

    /// <summary>
    /// Decodes a frame from its JSON representation.
    /// </summary>
    /// <exception cref="JsonException">The payload is not a well formed frame.</exception>
    public static Frame Decode(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<Frame>(utf8Json, Options)
        ?? throw new JsonException("frame JSON decoded to null");

    /// <summary>
    /// Encodes a frame to JSON.
    /// </summary>
    /// <remarks>
    /// Tooling and test support only. See the type remarks for why this is not the
    /// streaming path.
    /// </remarks>
    public static byte[] Encode(Frame frame) => JsonSerializer.SerializeToUtf8Bytes(frame, Options);
}

using System.Text;
using Nsst.Core.Protocol;
using Nsst.Core.Streaming;
using Xunit;

namespace Nsst.Core.Tests;

/// <summary>
/// A query string parsed the way the server parses one, so the parser tests exercise the
/// same decoding step the real request path does.
/// </summary>
/// <remarks>
/// The semantics that matter here are the ones a caller can observe: a key with no value
/// (<c>?duration</c>) still counts as present, and <c>+</c> decodes to a space. A fake that
/// only handled <c>name=value</c> pairs would let the parser tests pass while the real
/// decoder rejected perfectly valid queries.
/// </remarks>
internal sealed class TestQuery : IQuery
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    public static TestQuery Parse(string query)
    {
        var parsed = new TestQuery();
        if (query.Length == 0)
        {
            return parsed;
        }

        foreach (var pair in query.Split('&'))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Decode(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : Decode(pair[(separator + 1)..]);

            if (!parsed._values.TryGetValue(name, out var list))
            {
                parsed._values[name] = list = [];
            }

            list.Add(value);
        }

        return parsed;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? First(string name) =>
        _values.TryGetValue(name, out var list) && list.Count > 0 ? list[0] : null;

    /// <summary>A key present with no value, e.g. <c>?duration</c>, still counts as present.</summary>
    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}

/// <summary>Captures the frames an emit loop hands to it.</summary>
internal sealed class RecordingSink : IStreamSink
{
    private readonly List<Frame> _frames = [];
    private readonly Lock _gate = new();

    /// <summary>When non-zero, the write for this sequence fails instead of being recorded.</summary>
    public ulong FailOn { get; init; }

    public Exception? FailError { get; init; }

    public int Bytes { get; private set; }

    public ValueTask WriteFrameAsync(ulong sequence, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (FailOn != 0 && sequence == FailOn)
        {
            throw FailError ?? new IOException("connection reset");
        }

        var decoded = FrameJson.Decode(frame.Span);
        lock (_gate)
        {
            _frames.Add(decoded);
            Bytes += frame.Length;
        }

        return ValueTask.CompletedTask;
    }

    public (IReadOnlyList<Frame> Frames, int Bytes) Snapshot()
    {
        lock (_gate)
        {
            return (_frames.ToArray(), Bytes);
        }
    }

    public int FrameCount
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }
}

/// <summary>A lifetime that never cancels, or a reason without a manager behind it.</summary>
internal sealed class TestLifetime : IStreamLifetime
{
    public TestLifetime(CancellationToken token, EndReason reason = EndReason.ClientClosed)
    {
        Cancellation = token;
        CancellationReason = reason;
    }

    public static IStreamLifetime Never { get; } = new TestLifetime(CancellationToken.None);

    public CancellationToken Cancellation { get; }

    public EndReason CancellationReason { get; }
}

internal static class FrameAssertions
{
    /// <summary>
    /// Checks the invariants every frame on the wire must satisfy.
    /// </summary>
    /// <remarks>
    /// <c>payload_size</c> counts UTF-8 bytes, not characters, which is why the
    /// check goes through <see cref="Encoding.UTF8"/> rather than
    /// <c>string.Length</c>. For the embedded ASCII document the two agree; for a
    /// custom document with multi-byte runes they do not, and a frame that cut a
    /// rune in half would fail here.
    /// </remarks>
    public static void AssertWellFormed(Frame frame, int index)
    {
        var payloadBytes = frame.Payload is null ? 0 : Encoding.UTF8.GetByteCount(frame.Payload);

        Assert.True(
            payloadBytes == frame.PayloadSize,
            $"frame {index}: payload_size is {frame.PayloadSize} but the payload is {payloadBytes} UTF-8 bytes");
        Assert.True(
            frame.PayloadSize > 0 || frame.Payload is null,
            $"frame {index}: payload must be omitted when payload_size is 0");
    }
}

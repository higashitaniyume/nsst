using System.Text;
using Nsst.Core.Protocol;
using Nsst.Core.Streaming;
using Xunit;

namespace Nsst.Core.Tests;

public sealed class ProtocolTests
{
    /// <summary>
    /// A metadata-only frame drops the <c>payload</c> key entirely rather than
    /// sending an empty string, and ignores the cursor: there is nothing to slice.
    /// </summary>
    [Fact]
    public void MetadataOnlyFrameHasNoPayloadField()
    {
        var encoder = new FrameEncoder(0);
        var buffer = new byte[encoder.RecommendedBufferSize];

        Assert.True(encoder.TryAppend(buffer, 7, 1730000000123, 12345, out var written, out var consumed));

        Assert.Equal(0, consumed);
        Assert.Equal(
            "{\"sequence\":7,\"server_time\":1730000000123,\"payload_size\":0}",
            Encoding.UTF8.GetString(buffer, 0, written));
    }

    [Fact]
    public void NegativePayloadSizeIsClampedToZero()
    {
        Assert.Equal(0, new FrameEncoder(-1).PayloadSize);
        Assert.Equal(0, FrameEncoder.For(-5).PayloadSize);
    }

    [Fact]
    public void EncodersAreCachedBySize()
    {
        Assert.Same(FrameEncoder.For(64), FrameEncoder.For(64));
        Assert.NotSame(FrameEncoder.For(64), FrameEncoder.For(65));
    }

    [Fact]
    public void FrameJsonRoundTrips()
    {
        var frame = new Frame { Sequence = 42, ServerTime = 1730000000123, PayloadSize = 3, Payload = "abc" };
        Assert.Equal(frame, FrameJson.Decode(FrameJson.Encode(frame)));
    }

    [Fact]
    public void FrameJsonOmitsThePayloadWhenItIsAbsent()
    {
        var json = Encoding.UTF8.GetString(FrameJson.Encode(
            new Frame { Sequence = 1, ServerTime = 1, PayloadSize = 0 }));

        Assert.DoesNotContain("\"payload\":", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Go's <c>json.Marshal</c> HTML-escapes <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c>;
    /// the server's own frame encoder does not. The golden fixture covers the latter
    /// (its custom document contains all three), so this pins the reference encoder as
    /// the one that differs — and stops the two being collapsed into one.
    /// </summary>
    [Fact]
    public void TheReferenceEncoderHtmlEscapesWhatTheStreamingEncoderDoesNot()
    {
        var json = Encoding.UTF8.GetString(FrameJson.Encode(new Frame
        {
            Sequence = 1,
            ServerTime = 1,
            PayloadSize = 5,
            Payload = "a&b<c",
        }));

        Assert.Contains("\\u0026", json, StringComparison.Ordinal);
        Assert.Contains("\\u003C", json, StringComparison.Ordinal);
        Assert.DoesNotContain("&b", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// An undersized buffer must fail cleanly with nothing reported as written, since
    /// the runner retries the whole frame into a larger one.
    /// </summary>
    [Fact]
    public void TryAppendReportsFailureRatherThanTruncating()
    {
        var encoder = new FrameEncoder(64);
        var tight = new byte[32];

        Assert.False(encoder.TryAppend(tight, 1, 1730000000123, 0, out var written, out var consumed));
        Assert.Equal(0, written);
        Assert.Equal(64, consumed);
    }

    [Fact]
    public void EndReasonWireValuesMatchTheDocumentedSpellings()
    {
        Assert.Equal("completed", EndReason.Completed.ToWireValue());
        Assert.Equal("client_closed", EndReason.ClientClosed.ToWireValue());
        Assert.Equal("write_error", EndReason.WriteError.ToWireValue());
        Assert.Equal("server_shutdown", EndReason.ServerShutdown.ToWireValue());
    }
}

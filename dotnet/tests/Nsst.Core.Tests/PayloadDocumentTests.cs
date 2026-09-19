using System.Text;
using Nsst.Core.Protocol;
using Xunit;

namespace Nsst.Core.Tests;

/// <summary>
/// Document loading and the invariants the streaming hot path relies on.
/// </summary>
/// <remarks>
/// The slicing behaviour itself is covered exhaustively by the golden fixture; what
/// is tested here is everything around it — how a document gets loaded, what happens
/// when it cannot be, and the property of the embedded document that lets the runner
/// size its buffer once and never grow it.
/// </remarks>
public sealed class PayloadDocumentTests
{
    /// <summary>
    /// The runner sizes its per frame buffer from the payload size alone, so escaping
    /// has to fit inside the reserved overhead or the first frame of every stream
    /// reallocates.
    /// </summary>
    /// <remarks>
    /// The embedded document escapes only its newlines — 35 of them, measured — so the
    /// expansion for a window of S bytes is about <c>35 * S / 8699</c>. The overhead
    /// covers that comfortably up to roughly 38 KiB, which is far beyond anything the
    /// console sends. <see cref="VeryLargePayloadSizesStillEncodeCorrectly"/> records
    /// what happens past that edge.
    /// </remarks>
    [Fact]
    public void EmbeddedDocumentFitsTheReservedOverhead()
    {
        var bytes = PayloadDocument.Bytes.ToArray();

        Assert.True(bytes.Length > 0);
        Assert.True(Ascii.IsValid(bytes), "the embedded document must stay ASCII");

        // No HTML-escapable characters, so the default document cannot expose the
        // difference between FrameEncoder and a general-purpose JSON writer. The golden
        // fixture uses a custom document specifically to cover that case.
        Assert.DoesNotContain((byte)'&', bytes);
        Assert.DoesNotContain((byte)'<', bytes);
        Assert.DoesNotContain((byte)'>', bytes);

        foreach (var size in new[] { 0, 1, 2, 3, 7, 13, 63, 64, 80, 81, 127, 255, 256, 1024, 4096, 16384 })
        {
            var encoder = new FrameEncoder(size);
            var buffer = new byte[encoder.RecommendedBufferSize];

            Assert.True(
                encoder.TryAppend(buffer, 1, 1730000000123, 0, out _, out _),
                $"payload_size={size} needed more than the reserved {FrameEncoder.Overhead} bytes of overhead");
        }
    }

    /// <summary>
    /// Past roughly 38 KiB the embedded document's escaping expansion exceeds the
    /// reserved overhead, so the runner grows the buffer once.
    /// </summary>
    /// <remarks>
    /// Correctness never depends on the buffer being big enough — the growth loop in
    /// the runner handles it — but "the default document never reallocates" is an
    /// assumption worth knowing the edge of, and this pins that the output is still
    /// correct on the other side of it.
    /// </remarks>
    [Fact]
    public void VeryLargePayloadSizesStillEncodeCorrectly()
    {
        const int size = 1 << 20;
        var encoder = new FrameEncoder(size);
        var buffer = new byte[encoder.RecommendedBufferSize];

        if (encoder.TryAppend(buffer, 1, 1730000000123, 0, out var written, out _))
        {
            FrameAssertions.AssertWellFormed(FrameJson.Decode(buffer.AsSpan(0, written)), size);
            return;
        }

        // Otherwise this is the path the runner's growth loop takes.
        var grown = new byte[buffer.Length * 2];
        Assert.True(encoder.TryAppend(grown, 1, 1730000000123, 0, out written, out _));
        FrameAssertions.AssertWellFormed(FrameJson.Decode(grown.AsSpan(0, written)), size);
    }

    [Fact]
    public void SeedsAMissingFileWithTheEmbeddedDocument()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nsst-payload-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "nested", "payload.txt");
        var embeddedSize = PayloadDocument.Size;

        try
        {
            Assert.False(File.Exists(path));

            PayloadDocument.LoadDocument(path);

            // Parent directories are created too, so a fresh checkout always finds a
            // working, editable file where the operator pointed PAYLOAD_FILE.
            Assert.True(File.Exists(path));
            Assert.Equal(embeddedSize, new FileInfo(path).Length);
            Assert.Equal(embeddedSize, PayloadDocument.Size);
        }
        finally
        {
            PayloadDocument.ResetToEmbedded();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ReadsADocumentFromDisk()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "custom payload text", new UTF8Encoding(false));

            PayloadDocument.LoadDocument(path);

            Assert.Equal(Encoding.UTF8.GetByteCount("custom payload text"), PayloadDocument.Size);
        }
        finally
        {
            File.Delete(path);
            PayloadDocument.ResetToEmbedded();
        }
    }

    [Fact]
    public void ABlankPathKeepsTheEmbeddedDocument()
    {
        var before = PayloadDocument.Size;

        PayloadDocument.LoadDocument(null);
        PayloadDocument.LoadDocument(string.Empty);
        PayloadDocument.LoadDocument("   ");

        Assert.Equal(before, PayloadDocument.Size);
    }

    /// <summary>
    /// Docker creates a directory when a bind mount's source file is missing, so this
    /// is a common and otherwise baffling deployment failure. The message says so.
    /// </summary>
    [Fact]
    public void RejectsADirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nsst-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var error = Assert.Throws<PayloadDocumentException>(() => PayloadDocument.LoadDocument(directory));
            Assert.Contains("is a directory", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            PayloadDocument.ResetToEmbedded();
        }
    }

    [Fact]
    public void RejectsInvalidUtf8()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [0x61, 0xFF, 0xFE, 0x62]);

            var error = Assert.Throws<PayloadDocumentException>(() => PayloadDocument.LoadDocument(path));
            Assert.Contains("not valid UTF-8", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            PayloadDocument.ResetToEmbedded();
        }
    }

    [Fact]
    public void RejectsABlankDocument()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "  \n\t  ", new UTF8Encoding(false));

            var error = Assert.Throws<PayloadDocumentException>(() => PayloadDocument.LoadDocument(path));
            Assert.Contains("is empty", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            PayloadDocument.ResetToEmbedded();
        }
    }

    /// <summary>
    /// A rejected document must leave the previous one in place: falling back to a
    /// half-applied state would mean the server streams something nobody chose.
    /// </summary>
    [Fact]
    public void KeepsTheCurrentDocumentWhenLoadingFails()
    {
        var before = PayloadDocument.Size;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [0xFF, 0xFE]);

            Assert.Throws<PayloadDocumentException>(() => PayloadDocument.LoadDocument(path));
            Assert.Equal(before, PayloadDocument.Size);
        }
        finally
        {
            File.Delete(path);
            PayloadDocument.ResetToEmbedded();
        }
    }

    /// <summary>
    /// No frame may end in the middle of a rune, at any requested width.
    /// </summary>
    /// <remarks>
    /// A torn rune would make <c>payload_size</c> (a byte count) disagree with the
    /// decoded string, and the JSON encoder would substitute U+FFFD.
    /// </remarks>
    [Fact]
    public void EveryFrameEndsOnARuneBoundaryForAMultiByteDocument()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "中😀é", new UTF8Encoding(false));
            PayloadDocument.LoadDocument(path);

            foreach (var size in Enumerable.Range(1, 14))
            {
                var encoder = new FrameEncoder(size);
                var buffer = new byte[encoder.RecommendedBufferSize * 4];

                Assert.True(encoder.TryAppend(buffer, 1, 1730000000123, 0, out var written, out var consumed));
                Assert.True(consumed > 0, $"payload_size={size} produced an empty payload");

                var frame = FrameJson.Decode(buffer.AsSpan(0, written));
                FrameAssertions.AssertWellFormed(frame, size);
            }
        }
        finally
        {
            File.Delete(path);
            PayloadDocument.ResetToEmbedded();
        }
    }

    /// <summary>
    /// When the requested payload is narrower than a single rune, one whole rune goes
    /// out anyway and <c>payload_size</c> reports what was actually written.
    /// </summary>
    [Fact]
    public void SendsOneWholeRuneWhenTheRequestIsNarrowerThanOne()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "中", new UTF8Encoding(false));
            PayloadDocument.LoadDocument(path);

            var encoder = new FrameEncoder(1);
            var buffer = new byte[encoder.RecommendedBufferSize];

            Assert.True(encoder.TryAppend(buffer, 1, 1730000000123, 0, out var written, out var consumed));

            Assert.Equal(3, consumed);
            var frame = FrameJson.Decode(buffer.AsSpan(0, written));
            Assert.Equal(3, frame.PayloadSize);
            Assert.Equal("中", frame.Payload);
        }
        finally
        {
            File.Delete(path);
            PayloadDocument.ResetToEmbedded();
        }
    }
}

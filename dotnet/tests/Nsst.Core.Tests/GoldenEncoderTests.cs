using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nsst.Core.Protocol;
using Xunit;

namespace Nsst.Core.Tests;

/// <summary>
/// Pins the bytes of the frame encoder against a recorded fixture.
/// </summary>
/// <remarks>
/// The frame format is a live contract rather than an internal detail: the browser console
/// parses these bytes, so a change here reaches users. The fixture therefore records the
/// format and this test refuses to let it drift silently.
///
/// The fixture happens to have been generated from the Go implementation, which is history
/// rather than the point — what it encodes is the format the console reads. Keeping it in the
/// repository as data is what makes this test meaningful: it cannot pass by agreeing with
/// itself, and it needs no other implementation running to be checked.
/// </remarks>
public sealed class GoldenEncoderTests
{
    private sealed record GoldenCase(
        string Document,
        int PayloadSize,
        int Offset,
        ulong Sequence,
        long ServerTime,
        int Written,
        int Consumed,
        byte[] Expected);

    private static string GoldenPath =>
        Path.Combine(AppContext.BaseDirectory, "golden", "frames.txt");

    [Fact]
    public void EmbeddedDocumentMatchesTheOneTheGoldenFileWasCutFrom()
    {
        var expected = ReadDocuments().EmbeddedSha256;
        var actual = Convert.ToHexStringLower(SHA256.HashData(PayloadDocument.Bytes));

        Assert.Equal(expected, actual);
        Assert.True(PayloadDocument.Size > 0);
    }

    [Fact]
    public void EncoderReproducesTheGoldenOutputByteForByte()
    {
        var golden = ReadGoldenFile();

        // Every case belonging to the embedded document must fit the recommended
        // buffer. The document is pure ASCII with nothing to escape, so a frame can
        // never be longer than its payload — if that ever stops holding, the hot
        // path has started reallocating per frame on the default document.
        var expandedEmbeddedFrames = 0;
        var compared = 0;

        try
        {
            foreach (var group in golden.Cases.GroupBy(c => c.Document))
            {
                if (group.Key == "custom")
                {
                    LoadDocumentFromHex(golden.CustomDocument);
                }

                foreach (var testCase in group)
                {
                    var grew = AssertMatchesGo(testCase);
                    if (grew && testCase.Document == "embedded")
                    {
                        expandedEmbeddedFrames++;
                    }

                    compared++;
                }
            }
        }
        finally
        {
            PayloadDocument.ResetToEmbedded();
        }

        Assert.Equal(975, compared);
        Assert.Equal(0, expandedEmbeddedFrames);
    }

    /// <summary>
    /// Renders one golden case and compares it. Returns true when the frame needed
    /// a larger buffer than the encoder recommends.
    /// </summary>
    private static bool AssertMatchesGo(GoldenCase testCase)
    {
        var encoder = new FrameEncoder(testCase.PayloadSize);
        var buffer = new byte[encoder.RecommendedBufferSize];

        var grew = false;
        var ok = encoder.TryAppend(
            buffer, testCase.Sequence, testCase.ServerTime, testCase.Offset, out var written, out var consumed);

        while (!ok)
        {
            grew = true;
            buffer = new byte[buffer.Length * 2];
            ok = encoder.TryAppend(
                buffer, testCase.Sequence, testCase.ServerTime, testCase.Offset, out written, out consumed);
        }

        var context = string.Create(
            CultureInfo.InvariantCulture,
            $"{testCase.Document} payload={testCase.PayloadSize} offset={testCase.Offset} seq={testCase.Sequence} time={testCase.ServerTime}");

        Assert.True(
            testCase.Consumed == consumed,
            $"{context}: consumed {consumed} but the fixture consumed {testCase.Consumed}");
        Assert.True(
            testCase.Written == written,
            $"{context}: wrote {written} bytes but the fixture wrote {testCase.Written}");
        AssertBytesEqual(testCase.Expected, buffer.AsSpan(0, written), context);

        return grew;
    }

    private static void AssertBytesEqual(byte[] expected, ReadOnlySpan<byte> actual, string context)
    {
        if (expected.AsSpan().SequenceEqual(actual))
        {
            return;
        }

        var shared = Math.Min(expected.Length, actual.Length);
        var at = 0;
        while (at < shared && expected[at] == actual[at])
        {
            at++;
        }

        var from = Math.Max(0, at - 24);
        var expectedWindow = Encoding.UTF8.GetString(expected, from, Math.Min(64, expected.Length - from));
        var actualWindow = Encoding.UTF8.GetString(actual[from..Math.Min(from + 64, actual.Length)]);

        Assert.Fail(
            $"{context}: first difference at byte {at} " +
            $"(fixture wrote {expected.Length} bytes, encoder wrote {actual.Length})\n" +
            $"  fixture: ...{expectedWindow}...\n" +
            $"  encoder: ...{actualWindow}...");
    }

    private static void LoadDocumentFromHex(byte[] document)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nsst-golden-{Guid.NewGuid():N}.txt");
        File.WriteAllBytes(path, document);
        try
        {
            PayloadDocument.LoadDocument(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed record GoldenFile(
        string EmbeddedSha256,
        byte[] CustomDocument,
        IReadOnlyList<GoldenCase> Cases);

    private static GoldenFile ReadDocuments() => ReadGoldenFile();

    private static GoldenFile ReadGoldenFile()
    {
        Assert.True(File.Exists(GoldenPath), $"golden fixture missing at {GoldenPath}");

        var embeddedSha = string.Empty;
        var customDocument = Array.Empty<byte>();
        var cases = new List<GoldenCase>();

        foreach (var line in File.ReadLines(GoldenPath))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split(' ');
            switch (parts[0])
            {
                case "doc":
                    {
                        var fields = ParseFields(parts, 2);
                        if (parts[1] == "embedded")
                        {
                            embeddedSha = fields["sha256"];
                        }
                        else if (parts[1] == "custom")
                        {
                            customDocument = Convert.FromHexString(fields["hex"]);
                        }

                        break;
                    }

                case "case":
                    {
                        var f = ParseFields(parts, 2);
                        cases.Add(new GoldenCase(
                            Document: parts[1],
                            PayloadSize: int.Parse(f["payload"], CultureInfo.InvariantCulture),
                            Offset: int.Parse(f["offset"], CultureInfo.InvariantCulture),
                            Sequence: ulong.Parse(f["seq"], CultureInfo.InvariantCulture),
                            ServerTime: long.Parse(f["time"], CultureInfo.InvariantCulture),
                            Written: int.Parse(f["written"], CultureInfo.InvariantCulture),
                            Consumed: int.Parse(f["consumed"], CultureInfo.InvariantCulture),
                            Expected: Convert.FromHexString(f["hex"])));
                        break;
                    }

                default:
                    Assert.Fail($"unrecognised golden line: {line[..Math.Min(80, line.Length)]}");
                    break;
            }
        }

        Assert.NotEmpty(embeddedSha);
        Assert.NotEmpty(customDocument);
        Assert.NotEmpty(cases);

        return new GoldenFile(embeddedSha, customDocument, cases);
    }

    private static Dictionary<string, string> ParseFields(string[] parts, int start)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = start; i < parts.Length; i++)
        {
            var separator = parts[i].IndexOf('=', StringComparison.Ordinal);
            fields[parts[i][..separator]] = parts[i][(separator + 1)..];
        }

        return fields;
    }
}

using System.Text;
using System.Text.Unicode;

namespace Nsst.Core.Protocol;

/// <summary>
/// Thrown when the configured payload document cannot be used.
/// </summary>
/// <remarks>
/// A broken document is deliberately a startup failure rather than a warning:
/// silently falling back to the embedded text would leave the operator believing
/// their own text was being streamed when it was not.
/// </remarks>
public sealed class PayloadDocumentException : Exception
{
    public PayloadDocumentException(string message)
        : base(message)
    {
    }

    public PayloadDocumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The document that every frame's payload is cut from.
/// </summary>
/// <remarks>
/// <para>
/// The document is process wide and is replaced only before the server starts
/// listening, so streams read it without a lock. It is kept as raw UTF-8 bytes
/// rather than a <see cref="string"/> on purpose: frames are cut on byte offsets
/// and a .NET string is UTF-16, so a round trip through <see cref="string"/>
/// would make byte counts and character counts disagree for non-ASCII text.
/// </para>
/// </remarks>
public static class PayloadDocument
{
    private const string ResourceName = "Nsst.Core.Protocol.payload.txt";

    private static readonly byte[] Embedded = ReadEmbedded();

    private static byte[] _document = Embedded;
    private static bool _ascii = Ascii.IsValid(Embedded);

    /// <summary>The document compiled into the assembly.</summary>
    public static string PayloadText => Encoding.UTF8.GetString(Embedded);

    /// <summary>The document currently in use, as raw UTF-8 bytes.</summary>
    public static ReadOnlySpan<byte> Bytes => _document;

    /// <summary>Length of the document currently in use, in bytes.</summary>
    public static int Size => _document.Length;

    /// <summary>
    /// Replaces the embedded document with the contents of <paramref name="path"/>.
    /// An empty or blank path keeps the embedded document and reports no error.
    /// </summary>
    /// <remarks>
    /// A path that does not exist yet is seeded with the embedded document,
    /// creating any missing parent directories, so a fresh checkout always finds
    /// a working, editable file where the operator pointed <c>PAYLOAD_FILE</c>.
    /// </remarks>
    /// <exception cref="PayloadDocumentException">The document is unusable.</exception>
    public static void LoadDocument(string? path)
    {
        path = path?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        // Seed only when neither a file nor a directory is there. File.Exists
        // reports false for a directory too, so both have to be checked before
        // deciding to write, or a bind-mounted directory would be clobbered.
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Seed(path);
        }

        if (Directory.Exists(path))
        {
            // Docker creates a directory when a bind mount's source file does not
            // exist, so this is a common and otherwise baffling failure.
            throw new PayloadDocumentException(
                $"payload document {path} is a directory; a bind mount whose source file is missing creates one");
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PayloadDocumentException($"payload document: {ex.Message}", ex);
        }

        if (!Utf8.IsValid(data))
        {
            throw new PayloadDocumentException($"payload document {path} is not valid UTF-8");
        }

        if (string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(data)))
        {
            throw new PayloadDocumentException($"payload document {path} is empty");
        }

        _document = data;
        _ascii = Ascii.IsValid(data);
    }

    /// <summary>
    /// Returns how much of the document one frame may carry when the caller asked
    /// for <paramref name="size"/> bytes: at most that, and always a whole number
    /// of runes.
    /// </summary>
    /// <remarks>
    /// Cutting a multi-byte rune in half would make the .NET byte count and
    /// JavaScript's UTF-16 length disagree, and the JSON encoder would replace the
    /// torn sequence with U+FFFD. Every frame therefore has to end on a rune
    /// boundary. For an ASCII document the answer is simply the requested size,
    /// which is the common case and keeps the hot path to a single branch.
    /// </remarks>
    internal static int ConsumedLength(int offset, int size)
    {
        var n = _document.Length;
        if (n == 0 || size <= 0)
        {
            return 0;
        }

        if (_ascii)
        {
            return size;
        }

        var start = offset % n;
        if (start < 0)
        {
            start += n;
        }

        var consumed = 0;
        while (consumed < size)
        {
            var width = RuneWidth(start + consumed);
            if (consumed + width > size)
            {
                break;
            }

            consumed += width;
        }

        if (consumed == 0)
        {
            // The frame is narrower than a single rune of this document. Send one
            // whole rune rather than an empty payload, and let payload_size report
            // what was actually written: the client checks the two against each
            // other, not against what it asked for.
            return RuneWidth(start);
        }

        return consumed;
    }

    /// <summary>
    /// Appends <paramref name="size"/> bytes of the document starting at
    /// <paramref name="offset"/>, wrapping around the end as many times as needed,
    /// with JSON string escaping applied.
    /// </summary>
    /// <remarks>
    /// Escaping happens here rather than on a precomputed copy of the document
    /// because a chunk boundary can fall in the middle of an escape sequence,
    /// which would produce JSON that no decoder could read.
    /// </remarks>
    internal static void AppendEscapedSlice(ref ByteWriter w, int offset, int size)
    {
        var n = _document.Length;
        if (n == 0 || size <= 0)
        {
            return;
        }

        var start = offset % n;
        if (start < 0)
        {
            start += n;
        }

        for (var remaining = size; remaining > 0;)
        {
            var chunk = n - start;
            if (chunk > remaining)
            {
                chunk = remaining;
            }

            AppendEscaped(ref w, _document.AsSpan(start, chunk));
            remaining -= chunk;
            start = 0;
        }
    }

    /// <summary>
    /// Appends <paramref name="src"/> with exactly the escaping the frame format
    /// uses: the two structural characters, the four short escapes, and
    /// <c>\u00XX</c> for the remaining control bytes.
    /// </summary>
    /// <remarks>
    /// Note what is <em>not</em> escaped: <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c>
    /// pass through raw. <see cref="FrameJson"/> uses the framework's encoder, which
    /// HTML-escapes them, but the streaming encoder does not, and the browser console
    /// is written against what the server actually sends on the wire. That difference
    /// shows up as soon as an operator supplies a payload document containing an
    /// ampersand.
    /// </remarks>
    private static void AppendEscaped(ref ByteWriter w, ReadOnlySpan<byte> src)
    {
        foreach (var c in src)
        {
            switch (c)
            {
                case (byte)'"':
                    w.WriteByte((byte)'\\');
                    w.WriteByte((byte)'"');
                    break;
                case (byte)'\\':
                    w.WriteByte((byte)'\\');
                    w.WriteByte((byte)'\\');
                    break;
                case (byte)'\n':
                    w.WriteByte((byte)'\\');
                    w.WriteByte((byte)'n');
                    break;
                case (byte)'\r':
                    w.WriteByte((byte)'\\');
                    w.WriteByte((byte)'r');
                    break;
                case (byte)'\t':
                    w.WriteByte((byte)'\\');
                    w.WriteByte((byte)'t');
                    break;
                default:
                    if (c < 0x20)
                    {
                        w.WriteByte((byte)'\\');
                        w.WriteByte((byte)'u');
                        w.WriteByte((byte)'0');
                        w.WriteByte((byte)'0');
                        w.WriteByte(HexDigits[c >> 4]);
                        w.WriteByte(HexDigits[c & 0x0F]);
                    }
                    else
                    {
                        w.WriteByte(c);
                    }

                    break;
            }
        }
    }

    private static ReadOnlySpan<byte> HexDigits => "0123456789abcdef"u8;

    /// <summary>
    /// Returns the encoded width of the rune that starts at <paramref name="index"/>,
    /// reading cyclically past the end of the document.
    /// </summary>
    private static int RuneWidth(int index)
    {
        var n = _document.Length;
        if (_document[index % n] < 0x80)
        {
            return 1;
        }

        // Copying at most four bytes into a small window is the cheapest way to
        // decode a rune that straddles the wrap point.
        Span<byte> window = stackalloc byte[4];
        for (var k = 0; k < window.Length; k++)
        {
            window[k] = _document[(index + k) % n];
        }

        var status = Rune.DecodeFromUtf8(window, out _, out var consumed);
        // Rune.DecodeFromUtf8 reports one consumed byte for invalid data and zero when
        // the window is truncated. The caller relies on a positive width, so both cases
        // are folded to 1.
        return status == System.Buffers.OperationStatus.Done && consumed > 0 ? consumed : 1;
    }

    private static void Seed(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PayloadDocumentException(
                    $"payload document: creating directory for {path}: {ex.Message}", ex);
            }
        }

        try
        {
            File.WriteAllBytes(path, Embedded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PayloadDocumentException(
                $"payload document: seeding {path}: {ex.Message}", ex);
        }
    }

    private static byte[] ReadEmbedded()
    {
        using var stream = typeof(PayloadDocument).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource {ResourceName} is missing");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Restores the embedded document. Test support only.</summary>
    internal static void ResetToEmbedded()
    {
        _document = Embedded;
        _ascii = Ascii.IsValid(Embedded);
    }
}

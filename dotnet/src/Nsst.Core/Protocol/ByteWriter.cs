using System.Buffers.Text;

namespace Nsst.Core.Protocol;

/// <summary>
/// Sequential, allocation free writer over a caller supplied buffer.
/// </summary>
/// <remarks>
/// A <see cref="Span{T}"/> cannot grow, so this writer latches an overflow flag when
/// the buffer runs out and lets the caller re-run the frame into a larger buffer.
/// Whether a frame overflows depends only on how escape-dense the payload document
/// is, so in practice it happens at most a couple of times per stream and then never
/// again.
/// <para>
/// Integers are rendered with <see cref="Utf8Formatter"/> rather than
/// <c>ToString</c>: it is culture independent (so the wire bytes cannot depend on
/// the host locale), cannot allocate, and produces the plain invariant digits the
/// frame format carries.
/// </para>
/// </remarks>
internal ref struct ByteWriter
{
    /// <summary>Longest decimal expansion of a 64 bit integer, sign included.</summary>
    private const int MaxIntegerBytes = 20;

    private readonly Span<byte> _dst;
    private int _pos;
    private bool _overflow;

    public ByteWriter(Span<byte> dst)
    {
        _dst = dst;
        _pos = 0;
        _overflow = false;
    }

    /// <summary>Bytes written so far; meaningless once <see cref="Overflowed"/> is set.</summary>
    public readonly int Length => _pos;

    /// <summary>True when the frame did not fit and the caller must retry with more room.</summary>
    public readonly bool Overflowed => _overflow;

    public void WriteByte(byte value)
    {
        if (!TryReserve(1, out var span))
        {
            return;
        }

        span[0] = value;
        _pos += 1;
    }

    public void WriteAscii(ReadOnlySpan<byte> value)
    {
        if (!TryReserve(value.Length, out var span))
        {
            return;
        }

        value.CopyTo(span);
        _pos += value.Length;
    }

    public void WriteUInt64(ulong value) => WriteNumber(value);

    public void WriteInt64(long value) => WriteNumber(value);

    public void WriteInt32(int value) => WriteNumber(value);

    private void WriteNumber(long value)
    {
        if (!TryReserve(MaxIntegerBytes, out var span))
        {
            return;
        }

        Utf8Formatter.TryFormat(value, span, out var written);
        _pos += written;
    }

    private void WriteNumber(ulong value)
    {
        if (!TryReserve(MaxIntegerBytes, out var span))
        {
            return;
        }

        Utf8Formatter.TryFormat(value, span, out var written);
        _pos += written;
    }

    private void WriteNumber(int value)
    {
        if (!TryReserve(MaxIntegerBytes, out var span))
        {
            return;
        }

        Utf8Formatter.TryFormat(value, span, out var written);
        _pos += written;
    }

    /// <summary>
    /// Reserves room for <paramref name="needed"/> more bytes. Returns false and
    /// latches <see cref="Overflowed"/> when the buffer is exhausted, after which
    /// every further write is a no-op.
    /// </summary>
    private bool TryReserve(int needed, out Span<byte> span)
    {
        span = default;
        if (_overflow)
        {
            return false;
        }

        if (_pos + needed > _dst.Length)
        {
            _overflow = true;
            return false;
        }

        span = _dst[_pos..];
        return true;
    }
}

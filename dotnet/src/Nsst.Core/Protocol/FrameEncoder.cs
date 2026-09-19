namespace Nsst.Core.Protocol;

/// <summary>
/// Renders frames as compact JSON into a caller supplied buffer so that the
/// streaming hot path performs no per-frame allocations.
/// </summary>
/// <remarks>
/// An instance is immutable after
/// construction and safe for concurrent use, which is why it does <em>not</em>
/// hold the cursor into the payload document: that belongs to a single stream and
/// is passed to <see cref="TryAppend"/> on every frame.
/// </remarks>
public sealed class FrameEncoder
{
    /// <summary>Room reserved for the JSON scaffolding around the payload.</summary>
    public const int Overhead = 256;

    /// <summary>
    /// How many distinct payload sizes are remembered. The cap exists so a caller
    /// iterating over arbitrary sizes cannot grow the map without bound; any value
    /// would do.
    /// </summary>
    private const int MaxCached = 32;

    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<int, FrameEncoder> Cache = [];

    private readonly int _size;

    /// <summary>Creates an encoder for a fixed payload size in bytes.</summary>
    public FrameEncoder(int payloadSize) => _size = payloadSize < 0 ? 0 : payloadSize;

    /// <summary>Number of payload bytes every frame from this encoder carries.</summary>
    public int PayloadSize => _size;

    /// <summary>
    /// Buffer size that fits every frame of this encoder when the document needs no
    /// escaping, which is the common case.
    /// </summary>
    public int RecommendedBufferSize => Overhead + _size;

    /// <summary>
    /// Returns a (possibly cached) encoder for the given payload size.
    /// </summary>
    /// <remarks>
    /// An encoder holds nothing but its payload size, so the cache exists to hand
    /// the same value to every stream that asks for the same size, not to save
    /// memory. The entry cap keeps a caller iterating over arbitrary payload sizes
    /// from growing the map without bound.
    /// </remarks>
    public static FrameEncoder For(int payloadSize)
    {
        if (payloadSize < 0)
        {
            payloadSize = 0;
        }

        lock (CacheLock)
        {
            if (Cache.TryGetValue(payloadSize, out var cached))
            {
                return cached;
            }

            var created = new FrameEncoder(payloadSize);
            if (Cache.Count < MaxCached)
            {
                Cache[payloadSize] = created;
            }

            return created;
        }
    }

    /// <summary>
    /// Renders the next frame onto <paramref name="dst"/>.
    /// </summary>
    /// <param name="dst">Destination buffer. Must be at least <see cref="RecommendedBufferSize"/> bytes to guarantee success.</param>
    /// <param name="sequence">1-based frame sequence.</param>
    /// <param name="serverTime">Unix milliseconds when the frame was built.</param>
    /// <param name="offset">Byte of the payload document this frame starts at; reduced modulo the document length.</param>
    /// <param name="written">Number of bytes produced in <paramref name="dst"/>.</param>
    /// <param name="consumed">
    /// Payload document bytes this frame consumed, which is what the caller has to
    /// advance its offset by. It can differ from <see cref="PayloadSize"/> when the
    /// document contains multi-byte runes: the frame then ends at the last whole
    /// rune that fits and <c>payload_size</c> reports what was actually written.
    /// </param>
    /// <returns>
    /// False when <paramref name="dst"/> was too small, which happens only when
    /// escaping expands the payload. The caller retries with a larger buffer; the
    /// frame is rebuilt from scratch, so no partial state is observable.
    /// </returns>
    public bool TryAppend(
        Span<byte> dst,
        ulong sequence,
        long serverTime,
        int offset,
        out int written,
        out int consumed)
    {
        consumed = PayloadDocument.ConsumedLength(offset, _size);

        var w = new ByteWriter(dst);
        w.WriteAscii("{\"sequence\":"u8);
        w.WriteUInt64(sequence);
        w.WriteAscii(",\"server_time\":"u8);
        w.WriteInt64(serverTime);
        w.WriteAscii(",\"payload_size\":"u8);
        w.WriteInt32(consumed);

        if (consumed > 0)
        {
            w.WriteAscii(",\"payload\":\""u8);
            PayloadDocument.AppendEscapedSlice(ref w, offset, consumed);
            w.WriteByte((byte)'"');
        }

        w.WriteByte((byte)'}');

        if (w.Overflowed)
        {
            written = 0;
            return false;
        }

        written = w.Length;
        return true;
    }
}

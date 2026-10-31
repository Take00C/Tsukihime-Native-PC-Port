using System.Buffers.Binary;
using Tsuki.Data.Containers;

namespace Tsuki.Data.Compression;

/// <summary>The optional <c>LV</c> chunk in front of a voice entry's audio stream (docs/formats/containers.md 6).</summary>
/// <param name="Present">Byte 0 is <c>'L'</c> (the only byte the engine tests).</param>
/// <param name="Length">The payload length n, the u16 at +2 (0 when absent).</param>
/// <param name="BodyOffset">Where the audio stream starts: <c>4 + n</c>, or 0 when absent.</param>
public readonly record struct LvHeader(bool Present, int Length, int BodyOffset)
{
    /// <summary>The engine clamps the copy of the payload to this many bytes (0x258).</summary>
    public const int EngineCopyMax = 600;

    /// <summary>Bytes the engine copies to its global LV buffer, header included (containers.md 6).</summary>
    public int EngineCopyLength => Present ? 4 + Math.Min(Length, EngineCopyMax) : 0;
}

/// <summary>A decoded voice entry (containers.md 6).</summary>
public sealed class LvDecoded
{
    /// <summary>Creates a result.</summary>
    public LvDecoded(LvHeader header, byte[] buffer, int length, bool compressed, int consumed, MzxStats? mzx)
    {
        Header = header;
        Buffer = buffer;
        Length = length;
        Compressed = compressed;
        Consumed = consumed;
        Mzx = mzx;
    }

    /// <summary>The LV chunk.</summary>
    public LvHeader Header { get; }

    /// <summary>Backing array of the decoded audio (a raw body is copied exactly).</summary>
    public byte[] Buffer { get; }

    /// <summary>Length of the decoded audio.</summary>
    public int Length { get; }

    /// <summary>The decoded audio: RIFF/WAVE ATRAC9 on retail data.</summary>
    public ReadOnlyMemory<byte> Data => Buffer.AsMemory(0, Length);

    /// <summary>True if the body started with <c>'M'</c> and was MZX0-decoded.</summary>
    public bool Compressed { get; }

    /// <summary>
    /// Entry bytes used: <c>BodyOffset + MZX bytes consumed</c> (the whole entry for a raw body). This
    /// defines the exact entry size that HED4 does not store (containers.md 3.1).
    /// </summary>
    public int Consumed { get; }

    /// <summary>The MZX0 statistics, null for a raw body.</summary>
    public MzxStats? Mzx { get; }
}

/// <summary>The <c>LV</c> voice wrapper of voice.mrg / voice2.mrg entries (docs/formats/containers.md 6).</summary>
public static class Lv
{
    /// <summary>Parses the LV chunk the way the engine does (VA 0x8BF8).</summary>
    /// <exception cref="ContainerFormatException">The chunk or its payload is cut off.</exception>
    public static LvHeader Parse(ReadOnlySpan<byte> entry)
    {
        if (entry.Length < 1 || entry[0] != (byte)'L')
        {
            return new LvHeader(false, 0, 0);
        }

        if (entry.Length < 4)
        {
            throw new ContainerFormatException("LV chunk shorter than its 4-byte header");
        }

        int n = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
        if (4 + n > entry.Length)
        {
            throw new ContainerFormatException($"LV payload length {n} runs past the entry ({entry.Length} bytes)");
        }

        return new LvHeader(true, n, 4 + n);
    }

    /// <summary>
    /// Decodes one voice entry: the optional LV chunk, then an MZX0 stream if the next byte is <c>'M'</c>,
    /// otherwise the remaining bytes unchanged (engine behaviour). Pass the entry's whole sector span.
    /// </summary>
    public static LvDecoded Decompress(ReadOnlySpan<byte> entry)
    {
        var header = Parse(entry);
        var body = entry[header.BodyOffset..];
        if (body.Length >= 1 && body[0] == (byte)'M')
        {
            var res = Mzx.Decompress(body);
            return new LvDecoded(header, res.Buffer, res.Length, true, header.BodyOffset + res.Stats.Consumed, res.Stats);
        }

        byte[] raw = body.ToArray();
        return new LvDecoded(header, raw, raw.Length, false, entry.Length, null);
    }
}

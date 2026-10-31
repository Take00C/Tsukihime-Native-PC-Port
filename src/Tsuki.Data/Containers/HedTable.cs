using System.Buffers.Binary;

namespace Tsuki.Data.Containers;

/// <summary>The two HED record layouts (docs/formats/containers.md 2 and 3).</summary>
public enum HedFormat
{
    /// <summary>8-byte records with an exact size (allpac*, parts, bgm, vse, vag).</summary>
    Hed8,

    /// <summary>4-byte records, sector-rounded size only (voice, voice2).</summary>
    Hed4,
}

/// <summary>One decoded HED record (docs/formats/containers.md 2.1-2.3, 3.1).</summary>
public readonly struct HedEntry
{
    /// <summary>Creates an entry; <see cref="HedTable.Parse"/> is the normal way to get one.</summary>
    public HedEntry(int index, ulong rawRecord, int storedOffset, long sectorOffset, int sizeSectors, int sizeLo)
    {
        Index = index;
        RawRecord = rawRecord;
        StoredOffset = storedOffset;
        SectorOffset = sectorOffset;
        SizeSectors = sizeSectors;
        SizeLo = sizeLo;
    }

    /// <summary>Entry number in the table.</summary>
    public int Index { get; }

    /// <summary>
    /// The record bytes as stored, little-endian: 8 bytes for HED8, 4 bytes (low half) for HED4.
    /// </summary>
    public ulong RawRecord { get; }

    /// <summary>The 20 stored bits of the sector offset.</summary>
    public int StoredOffset { get; }

    /// <summary>The full sector offset: the stored 20 bits plus the bank (containers.md 2.2).</summary>
    public long SectorOffset { get; }

    /// <summary>Sectors the entry occupies (HED8: ceil(size / 0x800); HED4: exact / 0x800 + 1).</summary>
    public int SizeSectors { get; }

    /// <summary>Low 16 bits of the exact size (HED8 only; -1 for HED4).</summary>
    public int SizeLo { get; }

    /// <summary>True when the exact byte size is stored (HED8).</summary>
    public bool SizeExact => SizeLo >= 0;

    /// <summary>Byte offset of the entry in the .mrg file.</summary>
    public long Offset => SectorOffset * Mzp.Sector;

    /// <summary>Bytes the entry occupies in the .mrg, padding included.</summary>
    public long Span => (long)SizeSectors * Mzp.Sector;

    /// <summary>Exact byte size when stored (HED8, containers.md 2.3), else the sector-rounded size.</summary>
    public long Size => SizeExact ? Mzp.DecodeSize(SizeSectors, SizeLo) : Span;

    /// <summary>The record bytes as lower-case hex (the format of the oracle's <c>hed_raw</c>).</summary>
    public string RawHex(HedFormat format)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, RawRecord);
        return Convert.ToHexStringLower(b[..(format == HedFormat.Hed8 ? 8 : 4)]);
    }
}

/// <summary>A parsed .hed file (docs/formats/containers.md 2 and 3).</summary>
public sealed class HedTable
{
    /// <summary>The engine stores at most this many bank switches (containers.md 2.2).</summary>
    public const int MaxBankSwitches = 11;

    private const int OffsetBits = 20;

    private HedTable(
        HedFormat format, HedEntry[] entries, byte[] terminator, byte[] trailing, bool contiguous, int[] bankStarts)
    {
        Format = format;
        Entries = entries;
        Terminator = terminator;
        Trailing = trailing;
        Contiguous = contiguous;
        BankStarts = bankStarts;
    }

    /// <summary>The record layout.</summary>
    public HedFormat Format { get; }

    /// <summary>All entries before the terminator.</summary>
    public IReadOnlyList<HedEntry> Entries { get; }

    /// <summary>The run of all-0xFF records after the last entry (empty for bgm.hed).</summary>
    public ReadOnlyMemory<byte> Terminator { get; }

    /// <summary>Anything after the terminator (none in the retail data).</summary>
    public ReadOnlyMemory<byte> Trailing { get; }

    /// <summary>True when every entry starts where the previous one ended.</summary>
    public bool Contiguous { get; }

    /// <summary>Entry index at which bank 1, 2, ... starts (the engine rule, containers.md 2.2).</summary>
    public IReadOnlyList<int> BankStarts { get; }

    /// <summary>Record size in bytes for a layout.</summary>
    public static int RecordSize(HedFormat format) => format == HedFormat.Hed8 ? 8 : 4;

    /// <summary>
    /// HED8 if the size is a multiple of 8 and bits 16..27 of the first u32 of every record before the
    /// first all-0xFF record are zero, else HED4 (containers.md 3.1 "Detection").
    /// </summary>
    public static HedFormat Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length % 8 == 0)
        {
            bool ok = true;
            for (int off = 0; off < data.Length; off += 8)
            {
                var rec = data.Slice(off, 8);
                if (IsAllFf(rec))
                {
                    break;
                }

                if ((BinaryPrimitives.ReadUInt32LittleEndian(rec) & 0x0FFF0000u) != 0)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return HedFormat.Hed8;
            }
        }

        if (data.Length % 4 == 0)
        {
            return HedFormat.Hed4;
        }

        throw new ContainerFormatException($"HED size {data.Length} matches no known layout");
    }

    /// <summary>Parses a .hed file. The layout is detected when <paramref name="format"/> is null.</summary>
    /// <exception cref="ContainerFormatException">The data breaks a rule of containers.md 2 or 3.</exception>
    public static HedTable Parse(ReadOnlySpan<byte> data, HedFormat? format = null)
    {
        HedFormat fmt = format ?? Detect(data);
        int rsize = RecordSize(fmt);
        if (data.Length % rsize != 0)
        {
            throw new ContainerFormatException($"HED size {data.Length} is not a multiple of {rsize}");
        }

        var entries = new List<HedEntry>(data.Length / rsize);
        var bankStarts = new List<int>();
        int termAt = data.Length;
        long prevEnd = 0;
        bool contiguous = true;
        int bank = 0;
        bool prevNibbleNonZero = false;
        int idx = 0;
        for (int off = 0; off < data.Length; off += rsize, idx++)
        {
            var rec = data.Slice(off, rsize);
            if (IsAllFf(rec))
            {
                termAt = off;
                break;
            }

            int stored;
            int sizeSectors;
            int sizeLo;
            ulong raw;
            if (fmt == HedFormat.Hed8)
            {
                uint field = BinaryPrimitives.ReadUInt32LittleEndian(rec);
                sizeSectors = BinaryPrimitives.ReadUInt16LittleEndian(rec[4..]);
                sizeLo = BinaryPrimitives.ReadUInt16LittleEndian(rec[6..]);
                raw = BinaryPrimitives.ReadUInt64LittleEndian(rec);
                if ((field & 0x0FFF0000u) != 0)
                {
                    throw new ContainerFormatException($"HED8 record {idx}: reserved offset bits set (0x{field:x8})");
                }

                stored = (int)((field & 0xFFFF) | ((field >> 28) << 16));
            }
            else
            {
                int lo16 = BinaryPrimitives.ReadUInt16LittleEndian(rec);
                int packed = BinaryPrimitives.ReadUInt16LittleEndian(rec[2..]);
                raw = BinaryPrimitives.ReadUInt32LittleEndian(rec);
                stored = lo16 | ((packed >> 12) << 16);
                sizeSectors = packed & 0x0FFF;
                sizeLo = -1;
            }

            // The engine's bank rule (containers.md 2.2): a nibble that returns to 0 after a non-zero
            // nibble starts a new bank.
            if ((stored >> 16) != 0)
            {
                prevNibbleNonZero = true;
            }
            else
            {
                if (prevNibbleNonZero)
                {
                    bank++;
                    bankStarts.Add(idx);
                }

                prevNibbleNonZero = false;
            }

            long full = (uint)stored | ((long)bank << OffsetBits);
            if (full != prevEnd)
            {
                contiguous = false;
            }

            entries.Add(new HedEntry(idx, raw, stored, full, sizeSectors, sizeLo));
            prevEnd = full + sizeSectors;
        }

        if (bank > MaxBankSwitches)
        {
            throw new ContainerFormatException(
                $"{bank} bank switches; the engine supports at most {MaxBankSwitches}");
        }

        var tail = data[termAt..];
        int tlen = 0;
        while (tlen + rsize <= tail.Length && IsAllFf(tail.Slice(tlen, rsize)))
        {
            tlen += rsize;
        }

        return new HedTable(
            fmt, entries.ToArray(), tail[..tlen].ToArray(), tail[tlen..].ToArray(), contiguous, bankStarts.ToArray());
    }

    private static bool IsAllFf(ReadOnlySpan<byte> rec) => !rec.ContainsAnyExcept((byte)0xFF);
}

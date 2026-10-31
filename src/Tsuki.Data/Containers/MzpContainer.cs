using System.Buffers.Binary;

namespace Tsuki.Data.Containers;

/// <summary>Constants and size rules shared by HED8 records and mrgd00 section records (containers.md 2.3, 4.2).</summary>
public static class Mzp
{
    /// <summary>The sector size of every archive: 0x800 bytes.</summary>
    public const int Sector = 0x800;

    /// <summary>The 6-byte container magic <c>mrgd00</c> (containers.md 4.1).</summary>
    public static ReadOnlySpan<byte> Magic => "mrgd00"u8;

    /// <summary>
    /// Exact byte size from the sector count and the low 16 bits (containers.md 2.3):
    /// <c>size = (cap &amp; ~0xFFFF) | lo</c>, minus 0x10000 when that exceeds <c>cap = sectors * 0x800</c>.
    /// </summary>
    public static long DecodeSize(int sizeSectors, int sizeLo)
    {
        long cap = (long)sizeSectors * Sector;
        long size = (cap & ~0xFFFFL) | (uint)sizeLo;
        if (size > cap)
        {
            size -= 0x10000;
        }

        return size;
    }

    /// <summary>The 1..8 bytes of 0xFF the packer puts after a section ending at <paramref name="end"/> (containers.md 4.3).</summary>
    public static int PadAfter(long end) => 8 - (int)(end % 8);
}

/// <summary>One section record of an mrgd00 container (containers.md 4.2).</summary>
/// <param name="Index">Section number.</param>
/// <param name="SectorOffset">The stored sector offset (units of 0x800, relative to the data area).</param>
/// <param name="ByteOffset">The stored byte-offset word: bits 0..11 offset inside the sector, bits 12..15 sector bits 16..19.</param>
/// <param name="SizeSectors">Sectors touched, counted from the sector offset.</param>
/// <param name="SizeLo">Low 16 bits of the exact size.</param>
/// <param name="Offset">Byte offset of the section data relative to the container start.</param>
/// <param name="Size">Exact byte size.</param>
public readonly record struct MzpSection(
    int Index, int SectorOffset, int ByteOffset, int SizeSectors, int SizeLo, long Offset, int Size)
{
    /// <summary>Byte offset inside the first sector (bits 0..11 of <see cref="ByteOffset"/>).</summary>
    public int InSectorOffset => ByteOffset & 0x0FFF;

    /// <summary>End of the data, relative to the container start.</summary>
    public long End => Offset + Size;
}

/// <summary>
/// An mrgd00 (MZP) container living in a memory window: a HED entry, a standalone file, or a section of
/// another container (docs/formats/containers.md 4). Sections are zero-copy slices of that window.
/// </summary>
public sealed class MzpContainer
{
    private readonly MzpSection[] _sections;

    private MzpContainer(ReadOnlyMemory<byte> data, int dataStart, MzpSection[] sections)
    {
        Data = data;
        DataStart = dataStart;
        _sections = sections;
    }

    /// <summary>The whole container window.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Section count N.</summary>
    public int Count => _sections.Length;

    /// <summary>Start of the data area: <c>8 + 8 * N</c>.</summary>
    public int DataStart { get; }

    /// <summary>The section records.</summary>
    public IReadOnlyList<MzpSection> Sections => _sections;

    /// <summary>End of the last section's data (relative to the container start).</summary>
    public long End
    {
        get
        {
            long end = DataStart;
            foreach (var s in _sections)
            {
                end = Math.Max(end, s.End);
            }

            return end;
        }
    }

    /// <summary>Size including the trailing 0xFF pad, which is what a HED8 size field holds (containers.md 4.3).</summary>
    public long PackedSize
    {
        get
        {
            long e = End;
            return e + Mzp.PadAfter(e);
        }
    }

    /// <summary>True if <paramref name="data"/> starts with an mrgd00 header (at least 8 bytes).</summary>
    public static bool IsMzp(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..6].SequenceEqual(Mzp.Magic);

    /// <summary>Parses the header and section table of a container that fills <paramref name="data"/>.</summary>
    /// <exception cref="ContainerFormatException">Bad magic, or a section outside the window.</exception>
    public static MzpContainer Parse(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        if (span.Length < 8)
        {
            throw new ContainerFormatException($"container window of {span.Length} bytes is shorter than its header");
        }

        if (!span[..6].SequenceEqual(Mzp.Magic))
        {
            throw new ContainerFormatException($"bad magic {Convert.ToHexStringLower(span[..6])} (expected mrgd00)");
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
        int dataStart = 8 + 8 * count;
        if (dataStart > span.Length)
        {
            throw new ContainerFormatException($"section table ({count} records) exceeds container size {span.Length}");
        }

        return new MzpContainer(data, dataStart, ReadTable(span, span.Length));
    }

    /// <summary>
    /// Reads the section table from the first <c>8 + 8 * N</c> bytes of a container whose total length is
    /// <paramref name="totalLength"/>. This is what a reader that has only the head of the container
    /// (positional reads) uses.
    /// </summary>
    /// <param name="head">At least the header and the section table.</param>
    /// <param name="totalLength">Length of the whole container.</param>
    public static MzpSection[] ReadTable(ReadOnlySpan<byte> head, long totalLength)
    {
        if (head.Length < 8 || !head[..6].SequenceEqual(Mzp.Magic))
        {
            throw new ContainerFormatException("bad magic (expected mrgd00)");
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(head[6..]);
        int dataStart = 8 + 8 * count;
        if (head.Length < dataStart)
        {
            throw new ContainerFormatException($"section table ({count} records) is cut off at {head.Length} bytes");
        }

        if (dataStart > totalLength)
        {
            throw new ContainerFormatException($"section table ({count} records) exceeds container size {totalLength}");
        }

        var sections = new MzpSection[count];
        for (int i = 0; i < count; i++)
        {
            var rec = head.Slice(8 + 8 * i, 8);
            int so = BinaryPrimitives.ReadUInt16LittleEndian(rec);
            int bo = BinaryPrimitives.ReadUInt16LittleEndian(rec[2..]);
            int ss = BinaryPrimitives.ReadUInt16LittleEndian(rec[4..]);
            int lo = BinaryPrimitives.ReadUInt16LittleEndian(rec[6..]);
            long off = dataStart + (long)so * Mzp.Sector + (bo & 0x0FFF) + ((long)(bo & 0xF000) << 15);
            long size = Mzp.DecodeSize(ss, lo);
            if (off + size > totalLength)
            {
                throw new ContainerFormatException(
                    $"section {i} [0x{off:x}+0x{size:x}] exceeds container size 0x{totalLength:x}");
            }

            sections[i] = new MzpSection(i, so, bo, ss, lo, off, (int)size);
        }

        return sections;
    }

    /// <summary>Section <paramref name="i"/> as a window onto the container (no copy).</summary>
    public ReadOnlyMemory<byte> Section(int i)
    {
        var s = _sections[i];
        return Data.Slice((int)s.Offset, s.Size);
    }

    /// <summary>True if section <paramref name="i"/> starts with the mrgd00 magic.</summary>
    public bool IsNested(int i)
    {
        var s = _sections[i];
        return s.Size >= 8 && Data.Span.Slice((int)s.Offset, 6).SequenceEqual(Mzp.Magic);
    }

    /// <summary>Parses section <paramref name="i"/> as a nested container (no copy).</summary>
    public MzpContainer Child(int i) => Parse(Section(i));

    /// <summary>The first <paramref name="n"/> bytes of section <paramref name="i"/> (fewer if it is shorter).</summary>
    public ReadOnlySpan<byte> Head(int i, int n = 16)
    {
        var s = _sections[i];
        return Data.Span.Slice((int)s.Offset, Math.Min(n, s.Size));
    }

    /// <summary>
    /// Deviations from the packing rule of containers.md 4.3 (empty for every retail container): the
    /// <c>size_sectors</c> rule, the contiguous layout, and 0xFF padding between and after the sections.
    /// </summary>
    public IReadOnlyList<string> Validate(bool checkPadding = true)
    {
        var issues = new List<string>();
        var span = Data.Span;
        long expected = DataStart;
        long prevEnd = DataStart;
        foreach (var s in _sections)
        {
            if (s.InSectorOffset >= Mzp.Sector)
            {
                issues.Add($"section {s.Index}: byte_offset 0x{s.ByteOffset:x} >= 0x800");
            }

            long touched = (s.InSectorOffset + (long)s.Size + Mzp.Sector - 1) / Mzp.Sector;
            if (s.SizeSectors != touched)
            {
                issues.Add($"section {s.Index}: size_sectors {s.SizeSectors} != ceil((byte_offset+size)/0x800)");
            }

            if (s.Offset != expected)
            {
                issues.Add($"section {s.Index}: offset 0x{s.Offset:x}, expected 0x{expected:x}");
            }
            else if (checkPadding && s.Index > 0)
            {
                var pad = span[(int)prevEnd..(int)s.Offset];
                if (pad.ContainsAnyExcept((byte)0xFF))
                {
                    issues.Add($"section {s.Index}: padding before it is not 0xFF: {Convert.ToHexStringLower(pad)}");
                }
            }

            prevEnd = s.End;
            expected = s.End + Mzp.PadAfter(s.End);
        }

        if (checkPadding && _sections.Length > 0)
        {
            long e = End;
            int tailLen = (int)Math.Min(Mzp.PadAfter(e), span.Length - e);
            var tail = span.Slice((int)e, tailLen);
            if (tail.ContainsAnyExcept((byte)0xFF))
            {
                issues.Add($"trailing pad not 0xFF: {Convert.ToHexStringLower(tail)}");
            }
        }

        return issues;
    }

    /// <summary>
    /// A digest of the section table (SHA-1 of <c>offset:size;</c> pairs) that the golden data uses to
    /// compare whole tables cheaply.
    /// </summary>
    public string SectionDigest()
    {
        var sb = new System.Text.StringBuilder(_sections.Length * 14);
        for (int i = 0; i < _sections.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(';');
            }

            sb.Append(_sections[i].Offset).Append(':').Append(_sections[i].Size);
        }

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.ASCII.GetBytes(sb.ToString())));
    }
}

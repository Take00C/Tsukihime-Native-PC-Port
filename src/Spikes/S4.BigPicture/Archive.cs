// Spike S4: minimal local readers for HED8 + NAM32 + MRG triplets and mrgd00 (MZP) section
// tables (docs/formats/containers.md 2, 4 and 7.1). Throwaway prototype: the data stream builds
// the real readers in Tsuki.Data. Positional reads only; the game folder is opened read-only.
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace S4.BigPicture;

/// <summary>One HED8 record resolved to an absolute byte range in the .mrg (containers.md 2.1-2.3).</summary>
internal readonly record struct HedEntry(int Index, long Offset, long Size, string? Name);

/// <summary>A HED8/NAM32/MRG triplet opened read-only (containers.md 2).</summary>
internal sealed class Triplet : IDisposable
{
    public const int Sector = 0x800;

    /// <summary>FILE_FLAG_NO_BUFFERING; .NET passes it through to CreateFile on Windows.</summary>
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public string Name { get; }
    public string MrgPath { get; }
    public IReadOnlyList<HedEntry> Entries { get; }
    private readonly SafeFileHandle _mrg;

    public Triplet(string imageDir, string name)
    {
        Name = name;
        string hed = Path.Combine(imageDir, name + ".hed");
        string nam = Path.Combine(imageDir, name + ".nam");
        MrgPath = Path.Combine(imageDir, name + ".mrg");
        string[]? names = File.Exists(nam) ? ReadNam32(File.ReadAllBytes(nam)) : null;
        Entries = ParseHed8(File.ReadAllBytes(hed), names);
        _mrg = File.OpenHandle(MrgPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
    }

    /// <summary>HED8 with the bank rule (containers.md 2.2) and the exact-size rule (2.3).</summary>
    public static List<HedEntry> ParseHed8(ReadOnlySpan<byte> hed, string[]? names)
    {
        var raw = new List<(ushort lo, ushort hi, ushort ss, ushort sl)>();
        for (int p = 0; p + 8 <= hed.Length; p += 8)
        {
            ulong rec = BinaryPrimitives.ReadUInt64LittleEndian(hed[p..]);
            if (rec == ulong.MaxValue)
                break;
            raw.Add((BinaryPrimitives.ReadUInt16LittleEndian(hed[p..]), BinaryPrimitives.ReadUInt16LittleEndian(hed[(p + 2)..]),
                     BinaryPrimitives.ReadUInt16LittleEndian(hed[(p + 4)..]), BinaryPrimitives.ReadUInt16LittleEndian(hed[(p + 6)..])));
        }
        var result = new List<HedEntry>(raw.Count);
        int bank = 0;
        bool prevNonZero = false;
        for (int i = 0; i < raw.Count; i++)
        {
            var (lo, hi, ss, sl) = raw[i];
            int nibble = hi >> 12;
            if (nibble != 0)
                prevNonZero = true;
            else
            {
                if (prevNonZero)
                    bank++;
                prevNonZero = false;
            }
            long sector = (lo | (long)nibble << 16) | (long)bank << 20;
            long cap = (long)ss * Sector;
            long size = (cap & ~0xFFFFL) | sl;
            if (size > cap)
                size -= 0x10000;
            result.Add(new HedEntry(i, sector * Sector, size, names is not null && i < names.Length ? names[i] : null));
        }
        return result;
    }

    /// <summary>NAM32: 32-byte records, NUL-padded name, bytes 30..31 CR LF (containers.md 7.1).</summary>
    public static string[] ReadNam32(byte[] nam)
    {
        var names = new string[nam.Length / 32];
        for (int i = 0; i < names.Length; i++)
        {
            ReadOnlySpan<byte> r = nam.AsSpan(i * 32, 30);
            int n = r.IndexOf((byte)0);
            names[i] = Encoding.ASCII.GetString(n < 0 ? r : r[..n]).Trim();
        }
        return names;
    }

    /// <summary>Positional read of one entry through the OS file cache.</summary>
    public byte[] Read(int index)
    {
        HedEntry e = Entries[index];
        var buf = GC.AllocateUninitializedArray<byte>(checked((int)e.Size));
        ReadExact(_mrg, buf, e.Offset);
        return buf;
    }

    /// <summary>Positional read into a caller buffer through the OS file cache.</summary>
    public void Read(int index, Span<byte> dst) => ReadExact(_mrg, dst[..checked((int)Entries[index].Size)], Entries[index].Offset);

    /// <summary>
    /// Uncached read (FILE_FLAG_NO_BUFFERING, 4 KiB aligned): measures the disk, not the file cache.
    /// Returns the elapsed milliseconds; the bytes are discarded.
    /// </summary>
    public unsafe double ReadUncached(int index)
    {
        HedEntry e = Entries[index];
        const long Align = 4096;
        long start = e.Offset & ~(Align - 1);
        long end = (e.Offset + e.Size + Align - 1) & ~(Align - 1);
        nuint len = (nuint)(end - start);
        void* p = NativeMemory.AlignedAlloc(len, (nuint)Align);
        try
        {
            using SafeFileHandle h = File.OpenHandle(MrgPath, FileMode.Open, FileAccess.Read, FileShare.Read, NoBuffering);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // NO_BUFFERING needs sector-multiple lengths: read in 8 MiB aligned chunks.
            long done = 0;
            const int Chunk = 8 << 20;
            while (done < (long)len)
            {
                int n = (int)Math.Min(Chunk, (long)len - done);
                int got = RandomAccess.Read(h, new Span<byte>((byte*)p + done, n), start + done);
                if (got <= 0)
                    break;
                done += got;
            }
            return System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
        finally
        {
            NativeMemory.AlignedFree(p);
        }
    }

    private static void ReadExact(SafeFileHandle h, Span<byte> dst, long offset)
    {
        int done = 0;
        while (done < dst.Length)
        {
            int n = RandomAccess.Read(h, dst[done..], offset + done);
            if (n <= 0)
                throw new EndOfStreamException($"short read at {offset + done}");
            done += n;
        }
    }

    public void Dispose() => _mrg.Dispose();
}

/// <summary>mrgd00 section table (containers.md 4.1-4.2); offsets are relative to the container start.</summary>
internal readonly struct MzpSections
{
    public readonly int Count;
    public readonly int[] Offsets;
    public readonly int[] Sizes;

    public MzpSections(int count, int[] offsets, int[] sizes)
    {
        Count = count;
        Offsets = offsets;
        Sizes = sizes;
    }

    public static MzpSections Parse(ReadOnlySpan<byte> mzp)
    {
        if (mzp.Length < 8 || !mzp[..6].SequenceEqual("mrgd00"u8))
            throw new InvalidDataException("not an mrgd00 container");
        int n = BinaryPrimitives.ReadUInt16LittleEndian(mzp[6..]);
        int dataStart = 8 + 8 * n;
        var off = new int[n];
        var size = new int[n];
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> r = mzp.Slice(8 + 8 * i, 8);
            int sectorOffset = BinaryPrimitives.ReadUInt16LittleEndian(r);
            int byteOffset = BinaryPrimitives.ReadUInt16LittleEndian(r[2..]);
            int sizeSectors = BinaryPrimitives.ReadUInt16LittleEndian(r[4..]);
            int sizeLo = BinaryPrimitives.ReadUInt16LittleEndian(r[6..]);
            long o = dataStart + (long)sectorOffset * Triplet.Sector + (byteOffset & 0xFFF) + ((long)(byteOffset & 0xF000) << 15);
            long cap = (long)sizeSectors * Triplet.Sector;
            long s = (cap & ~0xFFFFL) | (uint)sizeLo;
            if (s > cap)
                s -= 0x10000;
            if (o < 0 || o + s > mzp.Length)
                throw new InvalidDataException($"section {i} [{o}, {o + s}) outside the container ({mzp.Length} bytes)");
            off[i] = (int)o;
            size[i] = (int)s;
        }
        return new MzpSections(n, off, size);
    }
}

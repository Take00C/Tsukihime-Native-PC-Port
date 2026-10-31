// Spike S4: one image MZP read from an archive and decoded tile by tile (images.md 9).
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace S4.BigPicture;

/// <summary>Native, page-aligned buffer (kept out of the GC heap, like a pooled upload buffer).</summary>
internal sealed unsafe class NativeBuffer : IDisposable
{
    private static long s_live, s_peak;
    public static long LiveBytes => Interlocked.Read(ref s_live);
    public static long PeakBytes => Interlocked.Read(ref s_peak);
    public static void ResetPeak() => Interlocked.Exchange(ref s_peak, Interlocked.Read(ref s_live));

    public byte* Ptr { get; private set; }
    public long Length { get; }

    public NativeBuffer(long length)
    {
        Length = length;
        Ptr = (byte*)NativeMemory.AlignedAlloc((nuint)length, 4096);
        long live = Interlocked.Add(ref s_live, length);
        long peak;
        while (live > (peak = Interlocked.Read(ref s_peak)) && Interlocked.CompareExchange(ref s_peak, live, peak) != peak)
        {
        }
    }

    public void Dispose()
    {
        if (Ptr != null)
        {
            NativeMemory.AlignedFree(Ptr);
            Ptr = null;
            Interlocked.Add(ref s_live, -Length);
        }
    }
}

internal sealed unsafe class Picture
{
    public required string Archive { get; init; }
    public required int Index { get; init; }
    public required string? Name { get; init; }
    public required byte[] Entry { get; init; }
    public required MzpSections Sections { get; init; }
    public required ImageHeader Header { get; init; }

    /// <summary>Tiles with a non-zero flag, in row-major order (flag 0 is never decoded, images.md 2.3).</summary>
    public int[] LiveTiles => _live ??= Enumerable.Range(0, Header.Tiles).Where(t => Header.TileFlags[t] != 0).ToArray();
    private int[]? _live;

    public long TileRgbaBytes => 4L * Header.TilePixels;

    public static Picture Load(Triplet arc, int index)
    {
        byte[] entry = arc.Read(index);
        return FromEntry(arc.Name, index, arc.Entries[index].Name, entry);
    }

    public static Picture FromEntry(string archive, int index, string? name, byte[] entry)
    {
        var secs = MzpSections.Parse(entry);
        var hdr = ImageHeader.Parse(entry.AsSpan(secs.Offsets[0], secs.Sizes[0]));
        if (secs.Count != 1 + hdr.Tiles)
            throw new InvalidDataException($"{secs.Count} sections for {hdr.Tiles} tiles");
        return new Picture { Archive = archive, Index = index, Name = name, Entry = entry, Sections = secs, Header = hdr };
    }

    /// <summary>Sum of the declared MZX output sizes of the live tiles.</summary>
    public long MzxOutBytes()
    {
        long s = 0;
        foreach (int t in LiveTiles)
            s += Mzx.DeclaredSize(Entry.AsSpan(Sections.Offsets[1 + t], Sections.Sizes[1 + t]));
        return s;
    }

    /// <summary>MZX-decompresses tile <paramref name="t"/> into <paramref name="scratch"/> (TileBytes + Overrun bytes).</summary>
    public void Decompress(int t, byte* scratch)
    {
        int off = Sections.Offsets[1 + t], len = Sections.Sizes[1 + t];
        fixed (byte* p = &Entry[off])
        {
            int size = Mzx.DeclaredSize(new ReadOnlySpan<byte>(p, len));
            if (size != Header.TileBytes)
                throw new InvalidDataException($"tile {t}: declared {size} bytes, format needs {Header.TileBytes}");
            int used = Mzx.Decompress(p, len, scratch, size);
            if (used != len)
                throw new InvalidDataException($"tile {t}: consumed {used} of {len} bytes");
        }
    }

    /// <summary>Decompress + convert one tile into its stored RGBA tile buffer.</summary>
    public void DecodeTile(int t, byte* scratch, uint* tileRgba)
    {
        Decompress(t, scratch);
        TileDecoder.Convert(Header, scratch, tileRgba);
    }

    /// <summary>
    /// Decodes every live tile with <paramref name="workers"/> threads; <paramref name="tileOut"/>
    /// gives the destination of tile t (TilePixels u32), <paramref name="done"/> is called after each tile.
    /// </summary>
    public void DecodeAll(int workers, Func<int, nint> tileOut, Action<int>? done = null)
    {
        int[] live = LiveTiles;
        int scratchLen = Header.TileBytes + Mzx.Overrun;
        if (workers <= 1)
        {
            using var scratch = new NativeBuffer(scratchLen);
            foreach (int t in live)
            {
                DecodeTile(t, scratch.Ptr, (uint*)tileOut(t));
                done?.Invoke(t);
            }
            return;
        }
        Parallel.For(0, live.Length, new ParallelOptions { MaxDegreeOfParallelism = workers },
            () => new NativeBuffer(scratchLen),
            (i, _, scratch) =>
            {
                int t = live[i];
                DecodeTile(t, scratch.Ptr, (uint*)tileOut(t));
                done?.Invoke(t);
                return scratch;
            },
            scratch => scratch.Dispose());
    }

    /// <summary>Assembles stored tiles into the displayed image and returns its SHA-1 (oracle format).</summary>
    public string AssembleSha1(Func<int, nint> tileOf, out double assembleMs)
    {
        long bytes = 4L * Header.OutW * Header.OutH;
        using var img = new NativeBuffer(Math.Max(bytes, 1));
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        NativeMemory.Clear(img.Ptr, (nuint)bytes);
        foreach (int t in LiveTiles)
            TileDecoder.Place(Header, t, (uint*)tileOf(t), (uint*)img.Ptr);
        assembleMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return Sha1(img.Ptr, bytes);
    }

    public static string Sha1(byte* p, long len)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        const int Chunk = 1 << 30;
        for (long o = 0; o < len; o += Chunk)
            h.AppendData(new ReadOnlySpan<byte>(p + o, (int)Math.Min(Chunk, len - o)));
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }
}

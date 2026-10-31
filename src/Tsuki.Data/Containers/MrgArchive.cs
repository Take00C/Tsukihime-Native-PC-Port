using Tsuki.Data.IO;

namespace Tsuki.Data.Containers;

/// <summary>
/// One entry of a HED/NAM/MRG triplet: where it lives in the .mrg and how big it is
/// (docs/formats/containers.md 2.1-2.4, 3.1).
/// </summary>
/// <param name="Index">Entry number.</param>
/// <param name="Name">The NAM name, or null when the archive has no name table.</param>
/// <param name="Offset">Byte offset in the .mrg.</param>
/// <param name="Size">Exact size (HED8) or the sector-rounded size (HED4).</param>
/// <param name="SizeExact">True when <paramref name="Size"/> is exact (HED8).</param>
/// <param name="SectorOffset">Full sector offset, bank included.</param>
/// <param name="SizeSectors">Sectors the entry occupies.</param>
/// <param name="SizeLo">Low 16 bits of the exact size (HED8), else -1.</param>
/// <param name="HedRaw">The HED record as lower-case hex.</param>
public sealed record MrgEntry(
    int Index, string? Name, long Offset, long Size, bool SizeExact, long SectorOffset, int SizeSectors, int SizeLo, string HedRaw)
{
    /// <summary>End of the exact data in the .mrg.</summary>
    public long End => Offset + Size;

    /// <summary>Bytes the entry occupies in the .mrg, padding included.</summary>
    public long Span => (long)SizeSectors * Mzp.Sector;
}

/// <summary>
/// The index of a HED/NAM/MRG triplet: the entry table and the names, without the data file. It is all the
/// build check needs, and what <see cref="MrgArchive"/> adds a mapping to.
/// </summary>
public sealed class ArchiveIndex
{
    private readonly Lazy<Dictionary<string, int>> _byName;

    private ArchiveIndex(string name, HedTable hed, NamTable? nam, long mrgLength, MrgEntry[] entries)
    {
        Name = name;
        Hed = hed;
        Nam = nam;
        MrgLength = mrgLength;
        Entries = entries;
        _byName = new Lazy<Dictionary<string, int>>(BuildNameMap);
    }

    /// <summary>The archive's base name (<c>allpac0</c>).</summary>
    public string Name { get; }

    /// <summary>The parsed .hed.</summary>
    public HedTable Hed { get; }

    /// <summary>The parsed .nam, or null when the archive has none.</summary>
    public NamTable? Nam { get; }

    /// <summary>Length of the .mrg file.</summary>
    public long MrgLength { get; }

    /// <summary>The entries in index order.</summary>
    public IReadOnlyList<MrgEntry> Entries { get; }

    /// <summary>Entry count.</summary>
    public int Count => Entries.Count;

    /// <summary>The record layout of the .hed.</summary>
    public HedFormat Format => Hed.Format;

    /// <summary>True if the name table has exactly one name per entry.</summary>
    public bool NamesAligned => Nam is not null && Nam.Count == Entries.Count;

    /// <summary>
    /// Loads the index of <paramref name="relativeBase"/> (for example <c>image/allpac0</c>: reads
    /// <c>.hed</c>, the optional <c>.nam</c> and the length of <c>.mrg</c>). The .mrg is not opened.
    /// </summary>
    /// <exception cref="FileNotFoundException">The .hed or the .mrg is missing.</exception>
    /// <exception cref="ContainerFormatException">The .hed or the .nam breaks the documented layout.</exception>
    public static ArchiveIndex Load(IGameFiles files, string relativeBase, HedFormat? format = null)
    {
        string name = relativeBase[(relativeBase.LastIndexOfAny(['/', '\\']) + 1)..];
        byte[] hed = files.ReadAll(relativeBase + ".hed");
        NamTable? nam = files.Exists(relativeBase + ".nam") ? NamTable.Parse(files.ReadAll(relativeBase + ".nam")) : null;
        return Create(name, hed, nam, files.Length(relativeBase + ".mrg"), format);
    }

    /// <summary>Builds an index from already-read bytes (<paramref name="nam"/> is null for archives without names).</summary>
    public static ArchiveIndex Create(string name, ReadOnlySpan<byte> hed, NamTable? nam, long mrgLength, HedFormat? format = null)
    {
        var table = HedTable.Parse(hed, format);
        var entries = new MrgEntry[table.Entries.Count];
        for (int i = 0; i < entries.Length; i++)
        {
            var e = table.Entries[i];
            string? entryName = nam is not null && e.Index < nam.Count ? nam[e.Index] : null;
            entries[i] = new MrgEntry(
                e.Index, entryName, e.Offset, e.Size, e.SizeExact, e.SectorOffset, e.SizeSectors, e.SizeLo, e.RawHex(table.Format));
        }

        return new ArchiveIndex(name, table, nam, mrgLength, entries);
    }

    /// <summary>Index of the first entry called <paramref name="name"/> (case-insensitive), or -1.</summary>
    public int Find(string name) => _byName.Value.TryGetValue(name, out int i) ? i : -1;

    /// <summary>
    /// Structural checks (containers.md 2.2, 2.3, 2.4): every extent inside the .mrg, gapless layout, exact
    /// sizes consistent with the sector counts, one name per entry. Empty for every retail archive.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        long prevEnd = 0;
        foreach (var e in Entries)
        {
            if (e.Offset + e.Span > MrgLength)
            {
                issues.Add($"entry {e.Index}: extent 0x{e.Offset:x}+0x{e.Span:x} beyond file size 0x{MrgLength:x}");
            }

            if (e.SectorOffset != prevEnd)
            {
                issues.Add($"entry {e.Index}: sector {e.SectorOffset} != previous end {prevEnd}");
            }

            if (e.SizeExact && !(e.Span - Mzp.Sector < e.Size && e.Size <= e.Span))
            {
                issues.Add($"entry {e.Index}: exact size 0x{e.Size:x} inconsistent with {e.SizeSectors} sectors");
            }

            prevEnd = e.SectorOffset + e.SizeSectors;
        }

        if (prevEnd * Mzp.Sector != MrgLength)
        {
            issues.Add($"last entry ends at 0x{prevEnd * Mzp.Sector:x}, file size 0x{MrgLength:x}");
        }

        if (Nam is not null && Nam.Count != Entries.Count)
        {
            issues.Add($"NAM has {Nam.Count} names for {Entries.Count} entries");
        }

        return issues;
    }

    private Dictionary<string, int> BuildNameMap()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries)
        {
            if (e.Name is not null)
            {
                map.TryAdd(e.Name, e.Index);
            }
        }

        return map;
    }
}

/// <summary>
/// A HED/NAM/MRG triplet with its data file memory-mapped (docs/formats/containers.md 1, 2.4). Entries are
/// zero-copy windows onto the mapping, so reading one entry of the 24 GB allpac1.mrg touches only that entry's
/// pages. Dispose the archive when no window handed out by it is in use any more.
/// </summary>
public sealed class MrgArchive : IDisposable
{
    private readonly MappedFile _mrg;

    private MrgArchive(ArchiveIndex index, MappedFile mrg)
    {
        Index = index;
        _mrg = mrg;
    }

    /// <summary>The entry table and names.</summary>
    public ArchiveIndex Index { get; }

    /// <summary>Entry count.</summary>
    public int Count => Index.Count;

    /// <summary>The entries in index order.</summary>
    public IReadOnlyList<MrgEntry> Entries => Index.Entries;

    /// <summary>Opens a triplet. <paramref name="basePath"/> is the path without extension, or the .hed/.mrg/.nam path.</summary>
    /// <exception cref="FileNotFoundException">The .hed or the .mrg is missing.</exception>
    public static MrgArchive Open(string basePath, HedFormat? format = null)
    {
        string path = basePath;
        string ext = Path.GetExtension(path);
        if (ext.Equals(".hed", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mrg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".nam", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^ext.Length];
        }

        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string name = Path.GetFileName(path);
        var files = new DirectoryGameFiles(dir);
        var index = ArchiveIndex.Load(files, name, format);
        var mapped = MappedFile.Open(files.ResolveFile(name + ".mrg") ?? throw new FileNotFoundException($"{name}.mrg not found in {dir}"));
        return new MrgArchive(index, mapped);
    }

    /// <summary>Index of the first entry called <paramref name="name"/> (case-insensitive), or -1.</summary>
    public int Find(string name) => Index.Find(name);

    /// <summary>The exact bytes of entry <paramref name="i"/> (HED4: the whole sector span), as a window onto the mapping.</summary>
    public ReadOnlyMemory<byte> Memory(int i)
    {
        var e = Index.Entries[i];
        return _mrg.Memory(e.Offset, checked((int)e.Size));
    }

    /// <summary>The entry's whole sector span, packer padding included (what the HED4 voice decoder takes).</summary>
    public ReadOnlyMemory<byte> SectorMemory(int i)
    {
        var e = Index.Entries[i];
        return _mrg.Memory(e.Offset, checked((int)e.Span));
    }

    /// <summary>The exact bytes of entry <paramref name="i"/> as a span onto the mapping.</summary>
    public ReadOnlySpan<byte> Span(int i)
    {
        var e = Index.Entries[i];
        return _mrg.Span(e.Offset, checked((int)e.Size));
    }

    /// <summary>A copy of the exact bytes of entry <paramref name="i"/>.</summary>
    public byte[] Read(int i) => Span(i).ToArray();

    /// <summary>Parses entry <paramref name="i"/> as an mrgd00 container (no copy).</summary>
    public MzpContainer OpenContainer(int i) => MzpContainer.Parse(Memory(i));

    /// <summary>Bytes of the mapped .mrg.</summary>
    public long MappedLength => _mrg.Length;

    /// <summary>See <see cref="MappedFile.TrimResident"/>.</summary>
    public void TrimResident(Action<nint, nuint> trim) => _mrg.TrimResident(trim);

    /// <inheritdoc />
    public void Dispose() => _mrg.Dispose();
}

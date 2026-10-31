using Tsuki.Data.Containers;
using Tsuki.Data.IO;

namespace Tsuki.Data.Audio;

/// <summary>What a sound-effect name resolves to (engine VA 0xCC790).</summary>
/// <param name="Kind">0 for section 0 (<c>S00..S07</c>), 2 for sections 2 and 3.</param>
/// <param name="Index">Kind 0: the record in section 0. Kind 2: the vse.hed entry (below 10000), or 10000 + the record of section 3.</param>
public readonly record struct SeLookup(int Kind, int Index)
{
    /// <summary>Kind 2 with an index below 10000: the name of an entry of <c>vse.hed</c>.</summary>
    public bool IsVseEntry => Kind == 2 && Index < SeNames.SectionThreeBase;
}

/// <summary>
/// <c>se_nam.mrg</c>: four NAM32 name tables the engine loads at boot (docs/formats/audio.md 4.1) and the name lookup of engine VA 0xCC790
/// (4.2). Section 2 holds one name per <c>vse</c> entry, so <see cref="Lookup"/> turns the name in a <c>_SEPL</c> or <c>_SEFI</c> call into
/// the vse entry. A port of <c>tsuki.audio.load_se_names</c>, <c>_se_key</c> and <c>se_lookup</c>.
/// </summary>
public sealed class SeNames
{
    /// <summary>Kind-2 indices from this value on name records of section 3 (no data on the PS4).</summary>
    public const int SectionThreeBase = 10000;

    /// <summary>Lookup flag: skip section 0.</summary>
    public const int SkipSectionZero = 1;

    /// <summary>Lookup flag: skip section 3.</summary>
    public const int SkipSectionThree = 2;

    private const string DefaultPath = "image/se_nam.mrg";
    private const int KeyLength = 28;

    private readonly Dictionary<string, int>[] _first;

    private SeNames(IReadOnlyList<string>[] sections)
    {
        Sections = sections;
        _first = new Dictionary<string, int>[sections.Length];
        for (int s = 0; s < sections.Length; s++)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < sections[s].Count; i++)
            {
                map.TryAdd(Upper(sections[s][i]), i); // the engine scan stops at the first equal name
            }

            _first[s] = map;
        }
    }

    /// <summary>The four name tables (8, 2, 1,327 and 67 records in the retail data).</summary>
    public IReadOnlyList<IReadOnlyList<string>> Sections { get; }

    /// <summary>Number of names in section 2: the number of vse entries (1,327 in the retail data).</summary>
    public int VseCount => Sections[2].Count;

    /// <summary>Reads the four tables from <c>image/se_nam.mrg</c> (a few kilobytes).</summary>
    /// <exception cref="ContainerFormatException">The file is not an mrgd00 container of four NAM32 sections.</exception>
    public static SeNames Open(IGameFiles files, string path = DefaultPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        return FromMemory(files.ReadAll(path));
    }

    /// <summary>Reads the tables from a container that is already in memory.</summary>
    public static SeNames FromMemory(ReadOnlyMemory<byte> file)
    {
        MzpContainer container = MzpContainer.Parse(file);
        if (container.Count != 4)
        {
            throw new ContainerFormatException($"se_nam.mrg has {container.Count} sections, expected 4");
        }

        var sections = new IReadOnlyList<string>[4];
        for (int i = 0; i < 4; i++)
        {
            sections[i] = NamTable.ParseNam32(container.Section(i).ToArray()).Names;
        }

        return new SeNames(sections);
    }

    /// <summary>
    /// Resolves a name like the engine: unless <c>flags &amp; 1</c> section 0, unless <c>flags &amp; 2</c> section 3, then section 2. The
    /// name is cut after the last backslash, at the first <c>.</c> and after 28 characters, and upper-cased (a..z only).
    /// </summary>
    public SeLookup? Lookup(string name, int flags = 0)
    {
        ArgumentNullException.ThrowIfNull(name);
        string key = Key(name);
        if ((flags & SkipSectionZero) == 0 && _first[0].TryGetValue(key, out int i0))
        {
            return new SeLookup(0, i0);
        }

        if ((flags & SkipSectionThree) == 0 && _first[3].TryGetValue(key, out int i3))
        {
            return new SeLookup(2, SectionThreeBase + i3);
        }

        return _first[2].TryGetValue(key, out int i2) ? new SeLookup(2, i2) : null;
    }

    /// <summary>The vse.hed entry named <paramref name="name"/> (section 2), or -1; what <c>_SEPL</c> plays.</summary>
    public int VseIndex(string name) => _first[2].GetValueOrDefault(Key(name), -1);

    /// <summary>The lookup key of a script name (engine VA 0xCC7AD..0xCC80F).</summary>
    public static string Key(string name)
    {
        string baseName = name[(name.LastIndexOf('\\') + 1)..];
        int dot = baseName.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            baseName = baseName[..dot];
        }

        return Upper(baseName.Length > KeyLength ? baseName[..KeyLength] : baseName);
    }

    private static string Upper(string s)
    {
        return string.Create(s.Length, s, static (span, src) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                char c = src[i];
                span[i] = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
            }
        });
    }
}

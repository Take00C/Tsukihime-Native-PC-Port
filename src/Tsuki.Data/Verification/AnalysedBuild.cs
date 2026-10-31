using Tsuki.Data.Containers;

namespace Tsuki.Data.Verification;

/// <summary>One HED/NAM/MRG triplet of the analysed build (docs/formats/containers.md 1, 2.2, 7.3).</summary>
/// <param name="Name">Base name in <c>image/</c>.</param>
/// <param name="Format">HED8 or HED4.</param>
/// <param name="Entries">Entry count (FORMATS 2).</param>
/// <param name="HasNam">True when the archive has a <c>.nam</c> file with one name per entry.</param>
/// <param name="HasTerminator">False only for bgm.hed, which simply ends (containers.md 2.1).</param>
/// <param name="BankStarts">Entry indices at which bank 1, 2, ... start (containers.md 2.2).</param>
public sealed record TripletSpec(
    string Name, HedFormat Format, int Entries, bool HasNam, bool HasTerminator, IReadOnlyList<int> BankStarts);

/// <summary>One standalone mrgd00 file of the analysed build (containers.md 8).</summary>
/// <param name="FileName">File name in <c>image/</c>.</param>
/// <param name="Sections">Section count (FORMATS 2).</param>
public sealed record StandaloneSpec(string FileName, int Sections);

/// <summary>
/// What the analysed CUSA47999 build looks like: the numbers of FORMATS section 2 and RR 3.3, written down once
/// so that the build check, the tests and the CLI agree. A different data release breaks them (RR 3.3 item 3).
/// </summary>
public static class AnalysedBuild
{
    /// <summary>The 12 triplet archives, 49,620 entries in all.</summary>
    public static IReadOnlyList<TripletSpec> Triplets { get; } =
    [
        new("allpac0", HedFormat.Hed8, 2449, true, true, [474, 992]),
        new("allpac1", HedFormat.Hed8, 9968, true, true, [311, 799, 1403, 2212, 3449, 3725, 4197, 5033, 6199, 7280, 9115]),
        new("allpac1_2", HedFormat.Hed8, 4317, true, true, [2503, 3417, 3681, 4221]),
        new("allpac2", HedFormat.Hed8, 14745, true, true, []),
        new("allpac3", HedFormat.Hed8, 136, true, true, []),
        new("allpac4", HedFormat.Hed8, 37, true, true, []),
        new("parts", HedFormat.Hed8, 24, false, true, []),
        new("bgm", HedFormat.Hed8, 132, false, false, []),
        new("vse", HedFormat.Hed8, 1327, false, true, []),
        new("vag", HedFormat.Hed8, 19, false, true, []),
        new("voice", HedFormat.Hed4, 16464, true, true, []),
        new("voice2", HedFormat.Hed4, 2, true, true, []),
    ];

    /// <summary>The 8 standalone mrgd00 files, 891 sections in all.</summary>
    public static IReadOnlyList<StandaloneSpec> Standalone { get; } =
    [
        new("allscr.mrg", 552),
        new("script_text.mrg", 10),
        new("ft_en.mrg", 65),
        new("ft_ja.mrg", 65),
        new("ft_ko.mrg", 65),
        new("ft_zc.mrg", 65),
        new("ft_zt.mrg", 65),
        new("se_nam.mrg", 4),
    ];

    /// <summary>Triplet entries over all 12 archives.</summary>
    public const int TripletEntryTotal = 49_620;

    /// <summary>Sections over all standalone files.</summary>
    public const int StandaloneSectionTotal = 891;

    /// <summary>Scripts: allscr.mrg sections 3..551 (549 names in section 0).</summary>
    public const int Scripts = 549;

    /// <summary>Text lines per language in script_text.mrg.</summary>
    public const int TextLines = 43_969;

    /// <summary>Voice entries in voice.hed.</summary>
    public const int Voices = 16_464;

    /// <summary>Movie files in <c>image/movie</c>.</summary>
    public const int MovieFiles = 134;

    /// <summary>Names in se_nam.mrg section 2: one per vse entry (containers.md 7.3).</summary>
    public const int VseNames = 1327;

    /// <summary>The language order of script_text.mrg table pairs (text.md 2).</summary>
    public static IReadOnlyList<string> TextLanguages { get; } = ["ja", "en", "zc", "zt", "ko"];

    /// <summary>SELF magic at the start of eboot.bin (RR 3.3 item 2).</summary>
    public static ReadOnlySpan<byte> SelfMagic => [0x4F, 0x15, 0x3D, 0x1D];

    /// <summary>File offset of the string "Hunex Consumer Library": text VA 0x2DA510 + 0x2280 (RR 3.3 item 2).</summary>
    public const int HunexStringOffset = 0x2DA510 + 0x2280;

    /// <summary>The string the build check expects at <see cref="HunexStringOffset"/>.</summary>
    public static ReadOnlySpan<byte> HunexString => "Hunex Consumer Library"u8;
}

// Which build of the game a dump is (package K25): the title id, version and content id of sce_sys/param.sfo, the eboot's size and SHA-256 against the small table of
// known builds, and the languages the data actually carries (script text, glyph pages, UI sheets, movies). The verified build is the only one the port was played on;
// another eboot of the same release and an unknown build are told apart so that the launcher, the log and a bug report can say what the player has.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tsuki.Data.Containers;
using Tsuki.Data.Eboot;
using Tsuki.Data.IO;
using Tsuki.Data.Text;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Verification;

/// <summary>How well a dump matches what the port knows.</summary>
public enum BuildMatch
{
    /// <summary>The eboot is one the port was verified on: every address of the documents applies.</summary>
    Verified,

    /// <summary>The eboot is another one of a known release (same title id); its tables are located by content.</summary>
    KnownRelease,

    /// <summary>A title id the port has never seen; its tables are located by content.</summary>
    Unknown,
}

/// <summary>One eboot the port knows.</summary>
/// <param name="Name">A short name: "CUSA47999 01.01 (official update)".</param>
/// <param name="TitleId">The title id of its release.</param>
/// <param name="AppVersion">The version the update's param.sfo carries.</param>
/// <param name="Region">The release's region word.</param>
/// <param name="Languages">The data languages of the release.</param>
/// <param name="EbootSize">Size of eboot.bin.</param>
/// <param name="EbootSha256">SHA-256 of eboot.bin, lower-case hex.</param>
/// <param name="Status">How far it was checked: "played end to end" or "tables byte-identical to the verified eboot".</param>
public sealed record KnownBuild(string Name, string TitleId, string AppVersion, string Region, string Languages, long EbootSize, string EbootSha256, string Status);

/// <summary>The builds the port knows. Only the first is verified; the others are recorded because their tables were checked against it.</summary>
public static class KnownBuilds
{
    /// <summary>The verified build: the analysed eboot of docs/RUNTIME_REQUIREMENTS.md 3.3.</summary>
    public static KnownBuild Verified { get; } = new(
        "CUSA47999 01.01 (official 1.01 update eboot)", "CUSA47999", "01.01", "Asia", "ja en zc zt", BuildInfo.AnalysedEbootSize, BuildInfo.AnalysedEbootSha256,
        "played end to end; every address of the documents applies");

    /// <summary>Every known build, the verified one first.</summary>
    public static IReadOnlyList<KnownBuild> All { get; } =
    [
        Verified,
        new(
            "CUSA47999 01.01 (1.01 eboot of the 5.05 / 6.72 / 7.xx backport)", "CUSA47999", "01.01", "Asia", "ja en zc zt", 12_767_802,
            "9e74a735fb794c7aa42e798768eb89c52a81cadf9f2585dd287aff87116a02e4",
            "418 bytes differ from the verified eboot (the SDK version words and six code patches); every table is byte-identical and found at the same address"),
    ];

    /// <summary>The known build with this eboot, or null.</summary>
    public static KnownBuild? Find(long ebootSize, string ebootSha256) =>
        All.FirstOrDefault(b => b.EbootSize == ebootSize && string.Equals(b.EbootSha256, ebootSha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>The title ids of the known releases.</summary>
    public static IReadOnlyList<string> TitleIds { get; } = [.. All.Select(b => b.TitleId).Distinct(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>The identity of a dump: what param.sfo, the eboot and the data say.</summary>
public sealed class BuildIdentity
{
    private BuildIdentity(ParamSfo? sfo, long ebootSize, string ebootSha256, IReadOnlyList<Language> text, IReadOnlyList<Language> fonts, IReadOnlyList<Language> sheets, IReadOnlyList<Language> movies)
    {
        Sfo = sfo;
        EbootSize = ebootSize;
        EbootSha256 = ebootSha256;
        TextLanguages = text;
        FontLanguages = fonts;
        SheetLanguages = sheets;
        MovieLanguages = movies;
        Known = KnownBuilds.Find(ebootSize, ebootSha256);
        Match = Known is not null ? BuildMatch.Verified
            : TitleId is { } id && KnownBuilds.TitleIds.Contains(id, StringComparer.OrdinalIgnoreCase) ? BuildMatch.KnownRelease
            : BuildMatch.Unknown;
        AvailableLanguages = [.. TextRules.SelectableLanguages.Where(l => text.Contains(l) && fonts.Contains(l) && sheets.Contains(l))];
    }

    /// <summary>The folder's (or the update's) param.sfo; null when none could be read.</summary>
    public ParamSfo? Sfo { get; }

    /// <summary>TITLE_ID, or the title id inside CONTENT_ID (<c>HP3279-CUSA47999_00-...</c>), or null.</summary>
    public string? TitleId => Sfo?["TITLE_ID"] ?? TitleIdFromContent(Sfo?["CONTENT_ID"]);

    /// <summary>APP_VER (01.01), else VERSION, or null.</summary>
    public string? AppVersion => Sfo?["APP_VER"] ?? Sfo?["VERSION"];

    /// <summary>CONTENT_ID, or null.</summary>
    public string? ContentId => Sfo?["CONTENT_ID"];

    /// <summary>CATEGORY: <c>gd</c> a game, <c>gp</c> a patch.</summary>
    public string? Category => Sfo?["CATEGORY"];

    /// <summary>The plain TITLE of param.sfo.</summary>
    public string? Title => Sfo?["TITLE"];

    /// <summary>Size of eboot.bin.</summary>
    public long EbootSize { get; }

    /// <summary>SHA-256 of eboot.bin, lower-case hex.</summary>
    public string EbootSha256 { get; }

    /// <summary>The known build this eboot is, or null.</summary>
    public KnownBuild? Known { get; }

    /// <summary>Verified, another eboot of a known release, or unknown.</summary>
    public BuildMatch Match { get; }

    /// <summary>The languages whose script text is real (not the U+3000 placeholder of the Korean slot).</summary>
    public IReadOnlyList<Language> TextLanguages { get; }

    /// <summary>The languages that have glyph pages with glyphs (<c>ft_&lt;lang&gt;.mrg</c> with non-blank pages).</summary>
    public IReadOnlyList<Language> FontLanguages { get; }

    /// <summary>The languages that have localized UI sheets in allpac3 (<c>*_&lt;lang&gt;.mrg</c>).</summary>
    public IReadOnlyList<Language> SheetLanguages { get; }

    /// <summary>The languages that have their own movies (ja: the unsuffixed files; en, zc, zt: <c>*_&lt;lang&gt;.mp4</c>).</summary>
    public IReadOnlyList<Language> MovieLanguages { get; }

    /// <summary>The selectable languages the data supports: text, glyph pages and UI sheets all present (movies fall back to Japanese).</summary>
    public IReadOnlyList<Language> AvailableLanguages { get; }

    /// <summary>"CUSA47999 01.01" (the title id and version, "unknown" for a missing field).</summary>
    public string ShortName => $"{TitleId ?? "unknown title"} {AppVersion ?? "(version unknown)"}";

    /// <summary>One line: "verified build: CUSA47999 01.01 (official 1.01 update eboot)" or "other version of a known release: CUSA47999 01.00, eboot 12,345,678 bytes, SHA-256 ab12...".</summary>
    public string Describe() => Match switch
    {
        BuildMatch.Verified => $"known build: {Known!.Name}",
        BuildMatch.KnownRelease => $"other version of a known release: {ShortName}, eboot {EbootSize.ToString("N0", CultureInfo.InvariantCulture)} bytes, SHA-256 {EbootSha256}",
        _ => $"unknown build: {ShortName}, eboot {EbootSize.ToString("N0", CultureInfo.InvariantCulture)} bytes, SHA-256 {EbootSha256}",
    };

    /// <summary>"text ja en zc zt; fonts ja en zc zt; sheets ja en zc zt ko; movies ja en zc zt; selectable ja en zc zt".</summary>
    public string DescribeLanguages() =>
        $"text {Codes(TextLanguages)}; fonts {Codes(FontLanguages)}; sheets {Codes(SheetLanguages)}; movies {Codes(MovieLanguages)}; selectable {Codes(AvailableLanguages)}";

    private static string Codes(IReadOnlyList<Language> languages) => languages.Count == 0 ? "none" : string.Join(" ", languages.Select(TextRules.Code));

    /// <summary>
    /// Identifies the dump behind <paramref name="files"/>: param.sfo, the eboot (hashed; <paramref name="eboot"/> saves a second read when the caller has the bytes) and the
    /// languages present in the data. Reads small ranges only; never throws for a damaged file (what cannot be read counts as absent).
    /// </summary>
    public static BuildIdentity Identify(IGameFiles files, byte[]? eboot = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ParamSfo? sfo = null;
        try
        {
            if (files.Exists("sce_sys/param.sfo") && files.Length("sce_sys/param.sfo") <= ParamSfo.MaxFileSize)
            {
                sfo = ParamSfo.TryParse(files.ReadAll("sce_sys/param.sfo"));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            sfo = null;
        }

        long size = 0;
        string sha = string.Empty;
        try
        {
            if (eboot is not null)
            {
                size = eboot.Length;
                sha = Convert.ToHexStringLower(SHA256.HashData(eboot));
            }
            else if (files.Exists("eboot.bin"))
            {
                size = files.Length("eboot.bin");
                if (size <= 64 * 1024 * 1024)
                {
                    sha = Convert.ToHexStringLower(SHA256.HashData(files.ReadAll("eboot.bin")));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            sha = string.Empty;
        }

        return new BuildIdentity(sfo, size, sha, TextLanguagesOf(files), FontLanguagesOf(files), SheetLanguagesOf(files), MovieLanguagesOf(files));
    }

    /// <summary>The title id inside a content id (<c>HP3279-CUSA47999_00-GAME000TSUKIHIME</c>).</summary>
    public static string? TitleIdFromContent(string? contentId)
    {
        int dash = contentId?.IndexOf('-', StringComparison.Ordinal) ?? -1;
        int underscore = contentId?.IndexOf('_', StringComparison.Ordinal) ?? -1;
        return contentId is not null && dash >= 0 && underscore > dash ? contentId[(dash + 1)..underscore] : null;
    }

    /// <summary>
    /// The languages of <c>script_text.mrg</c> whose string blob is real text: the Korean slot of the retail data is 43,969 copies of U+3000 (text.md 3), so a blob counts
    /// when it holds at least three distinct lines (read from its first 64 KB).
    /// </summary>
    private static IReadOnlyList<Language> TextLanguagesOf(IGameFiles files)
    {
        const string path = "image/script_text.mrg";
        var list = new List<Language>();
        try
        {
            if (!files.Exists(path))
            {
                return list;
            }

            long length = files.Length(path);
            byte[] head = files.ReadRange(path, 0, (int)Math.Min(length, 8));
            if (head.Length < 8 || !head.AsSpan(0, 6).SequenceEqual(Mzp.Magic))
            {
                return list;
            }

            int count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6));
            MzpSection[] sections = MzpContainer.ReadTable(files.ReadRange(path, 0, 8 + (8 * count)), length);
            for (int k = 0; k < 5 && (2 * k) + 1 < sections.Length; k++)
            {
                MzpSection blob = sections[(2 * k) + 1];
                int n = (int)Math.Min(blob.Size, 64 * 1024);
                if (n < 16)
                {
                    continue;
                }

                byte[] sample = files.ReadRange(path, blob.Offset, n);
                var distinct = new HashSet<string>(StringComparer.Ordinal);
                int start = 0;
                for (int i = 0; i + 1 < sample.Length && distinct.Count < 3; i++)
                {
                    if (sample[i] == 0x0D && sample[i + 1] == 0x0A)
                    {
                        distinct.Add(Convert.ToHexString(sample, start, i - start));
                        start = i + 2;
                    }
                }

                if (distinct.Count >= 3)
                {
                    list.Add((Language)k);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ContainerFormatException)
        {
            // a damaged text file supports no language
        }

        return list;
    }

    /// <summary>The languages with a glyph file whose pages carry glyphs: <c>ft_&lt;lang&gt;.mrg</c> larger than 1 MB (the all-blank Korean file is 583 KB, the smallest real one, English, 1.2 MB).</summary>
    private static IReadOnlyList<Language> FontLanguagesOf(IGameFiles files)
    {
        var list = new List<Language>();
        foreach (Language lang in Enum.GetValues<Language>())
        {
            string path = $"image/ft_{TextRules.Code(lang)}.mrg";
            try
            {
                if (files.Exists(path) && files.Length(path) >= 1_000_000)
                {
                    list.Add(lang);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // absent
            }
        }

        return list;
    }

    /// <summary>The languages with localized UI sheets: <c>allpac3.nam</c> names ending in <c>_&lt;lang&gt;.mrg</c>.</summary>
    private static IReadOnlyList<Language> SheetLanguagesOf(IGameFiles files)
    {
        var list = new List<Language>();
        try
        {
            if (!files.Exists("image/allpac3.nam"))
            {
                return list;
            }

            NamTable nam = NamTable.ParseNam32(files.ReadAll("image/allpac3.nam"));
            var names = new List<string>();
            for (int i = 0; i < nam.Count; i++)
            {
                names.Add(nam[i].ToUpperInvariant());
            }

            foreach (Language lang in Enum.GetValues<Language>())
            {
                string suffix = "_" + TextRules.Code(lang).ToUpperInvariant() + ".MRG";
                if (names.Count(n => n.EndsWith(suffix, StringComparison.Ordinal)) >= 3)
                {
                    list.Add(lang);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ContainerFormatException)
        {
            // no sheets
        }

        return list;
    }

    /// <summary>The languages with movies: Japanese when any unsuffixed mp4 exists, en / zc / zt when at least three <c>*_&lt;lang&gt;.mp4</c> exist.</summary>
    private static IReadOnlyList<Language> MovieLanguagesOf(IGameFiles files)
    {
        var list = new List<Language>();
        try
        {
            IReadOnlyList<string> movies = files.ListFiles("image/movie");
            var mp4 = movies.Where(m => m.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)).Select(m => m[..^4].ToLowerInvariant()).ToList();
            string[] suffixes = ["_en", "_zc", "_zt"];
            if (mp4.Any(m => !suffixes.Any(s => m.EndsWith(s, StringComparison.Ordinal))))
            {
                list.Add(Language.Ja);
            }

            foreach (Language lang in new[] { Language.En, Language.Zc, Language.Zt })
            {
                string suffix = "_" + TextRules.Code(lang);
                if (mp4.Count(m => m.EndsWith(suffix, StringComparison.Ordinal)) >= 3)
                {
                    list.Add(lang);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // no movies
        }

        return list;
    }

    /// <summary>A multi-line summary for the build check output and the log.</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Describe());
        sb.Append("param.sfo: ").Append(Sfo is null ? "not read" : $"TITLE_ID {TitleId ?? "-"}, APP_VER {AppVersion ?? "-"}, CATEGORY {Category ?? "-"}, CONTENT_ID {ContentId ?? "-"}, TITLE {Title ?? "-"}").AppendLine();
        sb.Append("languages: ").AppendLine(DescribeLanguages());
        return sb.ToString();
    }
}

/// <summary>
/// The build cannot be used: its eboot is not one the port knows and one or more tables could not be found by content (package K25). The message names the build
/// and the tables, and asks the player to report it with the log.
/// </summary>
public sealed class UnsupportedBuildException : EbootFormatException
{
    /// <summary>Creates the exception.</summary>
    public UnsupportedBuildException(BuildIdentity identity, LocateResult result)
        : base(Compose(identity, result))
    {
        Identity = identity;
        Result = result;
    }

    /// <summary>What the dump is.</summary>
    public BuildIdentity Identity { get; }

    /// <summary>What the locator found and missed.</summary>
    public LocateResult Result { get; }

    /// <summary>The names of the missing tables.</summary>
    public IReadOnlyList<string> MissingTables => [.. Result.Missing.Select(m => m.Name)];

    private static string Compose(BuildIdentity identity, LocateResult result) =>
        $"This copy of the game ({identity.ShortName}, eboot.bin {identity.EbootSize.ToString("N0", CultureInfo.InvariantCulture)} bytes, SHA-256 {identity.EbootSha256}) is a build the port does not know, " +
        $"and {result.Missing.Count} of its {result.Found.Count + result.Missing.Count} tables could not be found: {string.Join(", ", result.Missing.Select(m => m.Name))}. " +
        "Please report it with the log file.";
}

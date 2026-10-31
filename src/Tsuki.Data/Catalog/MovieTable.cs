using System.Collections.Immutable;
using System.Globalization;
using Tsuki.Data.Eboot;

namespace Tsuki.Data.Catalog;

/// <summary>One record of the movie table (data VA 0xDC8C70 + 0x30 * k, docs/formats/archives_ui_movies.md 9).</summary>
/// <param name="MovieId">The id (+0x00); equals the record number.</param>
/// <param name="Va">VA of the record.</param>
/// <param name="Path">The path string at +0x08, for example <c>/app0/image/movie/titlebg1.mp4</c>.</param>
/// <param name="RawFields">The eight u32 at +0x10..+0x2F: runtime defaults of the player object.</param>
public sealed record MovieRecord(int MovieId, long Va, string Path, ImmutableArray<uint> RawFields)
{
    /// <summary>The file name after the last '/'.</summary>
    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>The movie table and the small tables around it (port of the movie parts of <c>tsuki.catalog_eboot.Eboot</c>).</summary>
public static class MovieTable
{
    /// <summary>
    /// Reads the records until the terminator (a negative id; at VA 0xDCA590 in v01.01). The first record must be
    /// <c>titlebg1.mp4</c>.
    /// </summary>
    /// <exception cref="EbootFormatException">The table is not where the analysed build has it.</exception>
    public static IReadOnlyList<MovieRecord> Read(EbootReader eb)
    {
        ArgumentNullException.ThrowIfNull(eb);
        var list = new List<MovieRecord>();
        long va = eb.Layout.MovieTable;
        while (true)
        {
            int id = eb.I32(va);
            if (id < 0)
            {
                break;
            }

            string path = eb.CString((long)eb.U64(va + 8), 96);
            var raw = eb.Read(va + 0x10, 0x20);
            var fields = ImmutableArray.CreateBuilder<uint>(8);
            for (int i = 0; i < 8; i++)
            {
                fields.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw[(4 * i)..]));
            }

            list.Add(new MovieRecord(id, va, path, fields.MoveToImmutable()));
            va += EbootAddresses.MovieRecordSize;
        }

        return list.Count == 0 || !list[0].Path.EndsWith("titlebg1.mp4", StringComparison.Ordinal)
            ? throw new EbootFormatException($"movie table not found at 0x{eb.Layout.MovieTable:x}")
            : list;
    }

    /// <summary>The five language suffixes <c>ja en zc zt ko</c> (RELRO VA 0x42C030).</summary>
    /// <exception cref="EbootFormatException">The table holds anything else (a different eboot version).</exception>
    public static IReadOnlyList<string> ReadLanguageSuffixes(EbootReader eb)
    {
        ArgumentNullException.ThrowIfNull(eb);
        string[] suffixes = new string[eb.Layout.LanguageCount];
        for (int i = 0; i < suffixes.Length; i++)
        {
            suffixes[i] = eb.CString((long)eb.U64(eb.Layout.LanguageSuffixes + (8 * i)), 8);
        }

        return suffixes.AsSpan().SequenceEqual(["ja", "en", "zc", "zt", "ko"])
            ? suffixes
            : throw new EbootFormatException($"unexpected language suffix table [{string.Join(", ", suffixes)}] (different eboot version?)");
    }

    /// <summary>The prologue movie id per language (text VA 0x200F70): 4, 46, 47, 106 in v01.01.</summary>
    public static IReadOnlyList<int> ReadPrologueIds(EbootReader eb)
    {
        int[] ids = new int[4];
        for (int i = 0; i < 4; i++)
        {
            ids[i] = (int)eb.U32(eb.Layout.PrologueTable + (4 * i));
        }

        return ids;
    }
}

/// <summary>
/// The movie selection rules of the engine (VA 0x5E280): the movie names in table order and the language remap of the
/// script command <c>_TOMP(id)</c> (docs/formats/archives_ui_movies.md 9). A port of the movie parts of
/// <c>tsuki.catalog</c>.
/// </summary>
public static class MovieRules
{
    /// <summary>The prologue movie ids per language (text VA 0x200F70).</summary>
    public static IReadOnlyList<int> PrologueIds { get; } = [4, 46, 47, 106];

    /// <summary>The language codes of the asset suffixes in engine order.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["ja", "en", "zc", "zt", "ko"];

    private static readonly string[] Phases =
    [
        "01_00", "02_00", "03_00", "04_00", "05_00", "05_01", "06_00", "06_01", "07_00", "07_01",
        "08_00", "08_01", "09_00", "09_01", "10_00", "10_01", "10_02", "11_00", "11_01", "12_00",
        "12_01", "13_00", "13_01", "14_00", "14_01", "15_01a", "15_01b",
    ];

    /// <summary>The 134 movie names (without <c>.mp4</c>) in table order; index = movie id.</summary>
    public static IReadOnlyList<string> Names { get; } = BuildNames();


    /// <summary>Port of <c>movie_index_for_language</c> (VA 0x5E280): the table index to play for a language-neutral id.</summary>
    public static int IndexForLanguage(int movieId, int lang)
    {
        if (movieId == 4)
        {
            return lang is >= 0 and < 4 ? PrologueIds[lang] : 46;
        }

        if (movieId is >= 8 and <= 34)
        {
            return lang switch { 0 => movieId, 3 => movieId + 99, 2 => movieId + 67, _ => movieId + 40 };
        }

        if (movieId is 5 or 6)
        {
            return lang is 2 or 3 ? movieId + 99 : (lang == 0 ? movieId : movieId + 97);
        }

        return movieId;
    }

    /// <summary>The file name (<c>prologue_en.mp4</c>) for a language-neutral id.</summary>
    public static string FileForLanguage(int movieId, int lang) => Names[IndexForLanguage(movieId, lang)] + ".mp4";

    /// <summary>Group, language and base of a movie-table index (port of <c>movie_info</c>).</summary>
    public static MovieInfo Info(int index)
    {
        string name = Names[index];
        string lang = string.Empty;
        string baseName = name;
        foreach (string l in new[] { "en", "zc", "zt" })
        {
            if (name.EndsWith("_" + l, StringComparison.Ordinal))
            {
                baseName = name[..^(l.Length + 1)];
                lang = l;
                break;
            }
        }

        string group = baseName.StartsWith("titlebg", StringComparison.Ordinal) ? "title background loop"
            : baseName == "prologue" ? "prologue"
            : baseName.StartsWith("op", StringComparison.Ordinal) ? "opening"
            : baseName.StartsWith("phasetitle", StringComparison.Ordinal) ? "chapter title card"
            : baseName == "phaseout" ? "chapter end"
            : baseName.StartsWith("ed", StringComparison.Ordinal) ? "ending"
            : baseName.StartsWith("credit", StringComparison.Ordinal) ? "staff credits"
            : baseName == "TM_CI" ? "TYPE-MOON logo"
            : "?";
        int baseId = Names.ToList().IndexOf(baseName);
        var variants = new string[4];
        for (int i = 0; i < 4; i++)
        {
            variants[i] = FileForLanguage(baseId, i);
        }

        return new MovieInfo(index, name, name + ".mp4", baseName, baseId, lang.Length == 0 ? "neutral" : lang, group, variants);
    }

    private static string[] BuildNames()
    {
        var names = new List<string> { "titlebg1", "titlebg2", "titlebg3", "titlebg4", "prologue", "op1_arc", "op2_ciel", "phaseout" };
        names.AddRange(Phases.Select(p => "phasetitle_" + p));
        names.AddRange(["ed01_arc", "ed02_ciel", "ed03_ciel", "TM_CI"]);
        names.AddRange(Enumerable.Range(0, 7).Select(i => "credit" + i.ToString("D2", CultureInfo.InvariantCulture)));
        names.AddRange(["prologue_en", "prologue_zc"]);
        names.AddRange(Phases.Select(p => $"phasetitle_{p}_en"));
        names.AddRange(Phases.Select(p => $"phasetitle_{p}_zc"));
        names.AddRange(["op1_arc_en", "op2_ciel_en", "op1_arc_zc", "op2_ciel_zc", "prologue_zt"]);
        names.AddRange(Phases.Select(p => $"phasetitle_{p}_zt"));
        return names.Count == 134 ? [.. names] : throw new InvalidOperationException($"{names.Count} movie names instead of 134");
    }
}

/// <summary>Group, language and base id of one movie-table index.</summary>
/// <param name="Index">The movie-table index.</param>
/// <param name="Name">The name without extension.</param>
/// <param name="File">The file name.</param>
/// <param name="Base">The name without the language suffix.</param>
/// <param name="BaseId">The index of <paramref name="Base"/> in the table.</param>
/// <param name="Lang">The suffix <c>en zc zt</c>, or <c>neutral</c>.</param>
/// <param name="Group">What the movie is for.</param>
/// <param name="Variants">The file played for languages ja, en, zc, zt of the base id.</param>
public sealed record MovieInfo(int Index, string Name, string File, string Base, int BaseId, string Lang, string Group, IReadOnlyList<string> Variants);

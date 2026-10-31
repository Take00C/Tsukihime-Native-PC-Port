#if TSUKI_DEV
using System.Security.Cryptography;
using System.Text.Json;
using Tsuki.Data.Golden;

namespace Tsuki.Tests.Data.Golden;

/// <summary>
/// The slice read the plain way, for the expectations of the golden tests: <see cref="System.Text.Json"/> over the files of
/// <c>samples/golden/slice</c>, never through <c>Tsuki.Data.Golden</c> (a test of <see cref="GoldenSource"/> must not take its
/// expected values from the code under test).
/// </summary>
internal static class SliceExpect
{
    public const string Regenerate =
        "$env:PYTHONPATH='D:\\TsukihimePort\\tools'; .venv\\Scripts\\python.exe -m oracle.run slice";

    private static readonly Lazy<GoldenSource> Shared = new(() => GoldenSource.Open(Dir, GameFolder.Path), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<JsonDocument> AreaDoc = new(() => Json("manifest.json"));

    /// <summary>The slice folder; fails with the command that creates it when it is missing.</summary>
    public static string Dir
    {
        get
        {
            string d = Path.Combine(GoldenData.RepoRoot, "samples", "golden", "slice");
            return File.Exists(Path.Combine(d, "manifest.json"))
                ? d
                : throw new DirectoryNotFoundException($"golden slice {d} is missing; create it with: {Regenerate}");
        }
    }

    /// <summary>One <see cref="GoldenSource"/> for all tests (it only reads and is thread safe).</summary>
    public static GoldenSource Source => Shared.Value;

    /// <summary>Root of the area manifest.</summary>
    public static JsonElement Area => AreaDoc.Value.RootElement;

    /// <summary>Parses a JSON file of the slice.</summary>
    public static JsonDocument Json(string relative) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, relative)));

    /// <summary>The manifest record of a file (path, part, size, sha1, format ...).</summary>
    public static JsonElement FileRecord(string relative)
    {
        foreach (JsonElement f in Area.GetProperty("files").EnumerateArray())
        {
            if (f.GetProperty("path").GetString() == relative)
            {
                return f;
            }
        }

        throw new KeyNotFoundException($"{relative} is not in the slice manifest");
    }

    /// <summary>A counter of the area manifest (<c>pictures.pictures</c>, <c>audio.voices</c>, <c>text.messages</c> ...), straight from the JSON.</summary>
    public static int Count(string name) => Area.GetProperty("counts").GetProperty(name).GetInt32();

    /// <summary>The array of a manifest section (<c>files</c>, <c>inputs</c>, <c>checks</c> ...), straight from the JSON.</summary>
    public static int RawLength(string section) => Area.GetProperty(section).GetArrayLength();

    /// <summary>The script numbers of the manifest's <c>scripts/script_NNNN.json</c> files, ascending.</summary>
    public static int[] ScriptIndices() =>
        [.. ManifestPaths("scripts/script_", ".json").Select(p => int.Parse(p["scripts/script_".Length..^".json".Length], System.Globalization.CultureInfo.InvariantCulture)).Order()];

    /// <summary>The languages of the manifest's <c>fonts/ft_XX.json</c> files, in enum order.</summary>
    public static Tsuki.Engine.Contracts.Language[] FontLanguages() =>
        [.. ManifestPaths("fonts/ft_", ".json").Select(p => LanguageOf(p["fonts/ft_".Length..^".json".Length])).Order()];

    /// <summary>The language of a two-letter code of the slice files.</summary>
    public static Tsuki.Engine.Contracts.Language LanguageOf(string code) => code switch
    {
        "ja" => Tsuki.Engine.Contracts.Language.Ja,
        "en" => Tsuki.Engine.Contracts.Language.En,
        "zc" => Tsuki.Engine.Contracts.Language.Zc,
        "zt" => Tsuki.Engine.Contracts.Language.Zt,
        "ko" => Tsuki.Engine.Contracts.Language.Ko,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "unknown language code"),
    };

    private static IEnumerable<string> ManifestPaths(string prefix, string suffix) =>
        Area.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!)
            .Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && p.EndsWith(suffix, StringComparison.Ordinal));

    public static string Sha1(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA1.HashData(data));

    public static string Sha1(ReadOnlySpan<float> samples) => Sha1(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples));

    /// <summary>The archive names of ids 0..5 (archives_ui_movies.md 2.1), written out here on purpose.</summary>
    public static readonly string[] ArchiveNames = ["allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4"];

    /// <summary>The (archive id, entry) of every picture file in the manifest.</summary>
    public static List<(int Archive, int Entry, string Json)> PictureFiles()
    {
        var list = new List<(int, int, string)>();
        foreach (JsonElement f in Area.GetProperty("files").EnumerateArray())
        {
            string p = f.GetProperty("path").GetString()!;
            if (p.StartsWith("pictures/allpac", StringComparison.Ordinal) && p.EndsWith(".json", StringComparison.Ordinal))
            {
                string name = p["pictures/".Length..^".json".Length];
                int entry = int.Parse(name[^5..], System.Globalization.CultureInfo.InvariantCulture);
                int archive = Array.IndexOf(ArchiveNames, name[..^6]);
                list.Add((archive, entry, p));
            }
        }

        list.Sort();
        return list;
    }

    /// <summary>The (bank, entry) of every audio file in the manifest.</summary>
    public static List<(AudioBankName Bank, int Entry, string Json)> AudioFiles()
    {
        var list = new List<(AudioBankName, int, string)>();
        foreach (JsonElement f in Area.GetProperty("files").EnumerateArray())
        {
            string p = f.GetProperty("path").GetString()!;
            if (p.StartsWith("audio/", StringComparison.Ordinal) && p.EndsWith(".json", StringComparison.Ordinal) && p != "audio/manifest.json")
            {
                string name = p["audio/".Length..^".json".Length];
                list.Add((new AudioBankName(name[..^6]), int.Parse(name[^5..], System.Globalization.CultureInfo.InvariantCulture), p));
            }
        }

        list.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Item1.Name, b.Item1.Name);
            return c != 0 ? c : a.Item2.CompareTo(b.Item2);
        });
        return list;
    }
}

/// <summary>An audio bank by its HED name.</summary>
internal readonly record struct AudioBankName(string Name)
{
    public Tsuki.Engine.Contracts.AudioBank Bank => Name switch
    {
        "bgm" => Tsuki.Engine.Contracts.AudioBank.Bgm,
        "vse" => Tsuki.Engine.Contracts.AudioBank.Se,
        "voice" => Tsuki.Engine.Contracts.AudioBank.Voice,
        "voice2" => Tsuki.Engine.Contracts.AudioBank.Voice2,
        "vag" => Tsuki.Engine.Contracts.AudioBank.Vag,
        _ => throw new ArgumentOutOfRangeException(nameof(Name), Name, "unknown bank"),
    };
}
#endif

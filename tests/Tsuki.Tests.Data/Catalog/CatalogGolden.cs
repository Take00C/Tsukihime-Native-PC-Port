using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Tsuki.Data.Catalog;
using Tsuki.Data.Eboot;
using Tsuki.Data.IO;

namespace Tsuki.Tests.Data.Catalog;

/// <summary>
/// Reads the golden files of <c>samples/golden/catalog/</c> (<c>python -m oracle.run catalog</c>, local and git-ignored) and checks
/// each against the area manifest (size and SHA-1) once per process, so a test never compares with a hand-edited or half-written file.
/// </summary>
public static class CatalogGolden
{
    private const string Regenerate =
        "$env:PYTHONPATH='D:\\TsukihimePort\\tools'; .venv\\Scripts\\python.exe -m oracle.catalog   (or: -m oracle.run catalog)";

    private static readonly ConcurrentDictionary<string, bool> Verified = new(StringComparer.Ordinal);

    /// <summary>The area folder.</summary>
    public static string Dir => Path.Combine(GoldenData.RepoRoot, "samples", "golden", "catalog");

    /// <summary>Full path of a file of the area (<c>eboot/self.json</c>), after the manifest check.</summary>
    public static string Locate(string relative)
    {
        string p = Path.Combine(Dir, relative);
        if (!File.Exists(p))
        {
            throw new FileNotFoundException($"golden file {p} is missing; generate it with: {Regenerate}");
        }

        Verified.GetOrAdd(relative, VerifyAgainstManifest);
        return p;
    }

    private static bool VerifyAgainstManifest(string relative)
    {
        string manifestPath = Path.Combine(Dir, "manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var record = manifest.RootElement.GetProperty("files").EnumerateArray()
            .FirstOrDefault(f => f.GetProperty("path").GetString() == relative);
        if (record.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"{relative} is not listed in the catalog manifest; regenerate with: {Regenerate}");
        }

        byte[] bytes = File.ReadAllBytes(Path.Combine(Dir, relative));
        Assert.Equal(record.GetProperty("size").GetInt64(), bytes.LongLength);
        Assert.Equal(record.GetProperty("sha1").GetString(), Convert.ToHexStringLower(SHA1.HashData(bytes)));
        return true;
    }

    /// <summary>A JSON file of the area.</summary>
    public static JsonDocument Json(string relative) => JsonDocument.Parse(File.ReadAllBytes(Locate(relative)));

    /// <summary>The lines of a TSV file without its header row, each split at the tabs; the header names are in <paramref name="header"/>.</summary>
    public static List<string[]> Tsv(string relative, out string[] header)
    {
        string[] lines = File.ReadAllLines(Locate(relative));
        header = lines[0].Split('\t');
        var rows = new List<string[]>(lines.Length - 1);
        for (int i = 1; i < lines.Length; i++)
        {
            string[] cells = lines[i].Split('\t');
            Assert.True(cells.Length == header.Length, $"{relative} line {i + 1}: {cells.Length} cells for {header.Length} columns");
            rows.Add(cells);
        }

        return rows;
    }

    /// <summary>A TSV cell: <c>-</c> is null.</summary>
    public static string? Cell(string c) => c == "-" ? null : c;

    /// <summary>Lower-case hex of a query to bytes (<c>-</c> is empty).</summary>
    public static byte[] FromHex(string hex) => hex == "-" ? [] : Convert.FromHexString(hex);

    /// <summary>The ulong of a JSON number.</summary>
    public static ulong U64(JsonElement e) => e.GetUInt64();
}

/// <summary>The parsed eboot.bin and the game catalog, loaded once per test process (the folder is only read).</summary>
public static class SharedEboot
{
    private static readonly Lazy<byte[]> Bytes = new(() => File.ReadAllBytes(Path.Combine(GameFolder.Require(), "eboot.bin")));
    private static readonly Lazy<EbootReader> ReaderLazy = new(() => EbootReader.Parse(Bytes.Value));
    private static readonly Lazy<GameCatalog> CatalogLazy = new(() => GameCatalog.Load(new DirectoryGameFiles(GameFolder.Require())));

    /// <summary>The 12.7 MB file. Tests must copy it before they change a byte.</summary>
    public static byte[] Data => Bytes.Value;

    /// <summary>The parsed file.</summary>
    public static EbootReader Reader => ReaderLazy.Value;

    /// <summary>The catalog loaded the way the runtime loads it.</summary>
    public static GameCatalog Catalog => CatalogLazy.Value;

    /// <summary>Data VA to file offset.</summary>
    public static long DataFile(long va) => va - EbootAddresses.DataVa + EbootAddresses.DataFileOffset;

    /// <summary>Text VA to file offset.</summary>
    public static long TextFile(long va) => va + EbootAddresses.TextFileOffset;

    /// <summary>RELRO VA to file offset.</summary>
    public static long RelroFile(long va) => va - EbootAddresses.RelroVa + EbootAddresses.RelroFileOffset;
}

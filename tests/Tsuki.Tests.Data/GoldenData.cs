using System.Globalization;
using System.Text.Json;

namespace Tsuki.Tests.Data;

/// <summary>
/// The expectations that <c>Oracle/gen_golden_data.py</c> wrote to <c>samples/golden_data/</c> from the Python
/// reference library (local only, git-ignored). It is not <c>samples/golden/</c>, which tools/oracle owns.
/// </summary>
public static class GoldenData
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    /// <summary>The repository root: the first ancestor of the test binary with src/Tsuki.slnx.</summary>
    public static string RepoRoot => RootPath.Value;

    /// <summary>The golden-data folder (<c>TSUKI_GOLDEN_DATA</c> overrides it).</summary>
    public static string Dir
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("TSUKI_GOLDEN_DATA");
            return string.IsNullOrWhiteSpace(env) ? Path.Combine(RepoRoot, "samples", "golden_data") : env;
        }
    }

    /// <summary>Full path of a golden file; fails with the command that creates it when the file is missing.</summary>
    public static string Locate(string relative)
    {
        string p = System.IO.Path.Combine(Dir, relative);
        if (!File.Exists(p))
        {
            throw new FileNotFoundException(
                $"golden data file {p} is missing; generate it with: .venv\\Scripts\\python.exe -I tests\\Tsuki.Tests.Data\\Oracle\\gen_golden_data.py");
        }

        return p;
    }

    /// <summary>Reads a TSV file with a header row; every row becomes a column-name to value map.</summary>
    public static List<TsvRow> Tsv(string relative)
    {
        string[] lines = File.ReadAllLines(Locate(relative));
        string[] header = lines[0].Split('\t');
        var rows = new List<TsvRow>(lines.Length - 1);
        for (int i = 1; i < lines.Length; i++)
        {
            rows.Add(new TsvRow(header, lines[i].Split('\t')));
        }

        return rows;
    }

    /// <summary>Reads a JSON file.</summary>
    public static JsonDocument Json(string relative) => JsonDocument.Parse(File.ReadAllBytes(Locate(relative)));

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "src", "Tsuki.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"src/Tsuki.slnx not found above {AppContext.BaseDirectory}");
    }
}

/// <summary>One TSV row with named columns.</summary>
public sealed class TsvRow(string[] header, string[] cells)
{
    /// <summary>The cell text.</summary>
    public string this[string column]
    {
        get
        {
            int i = Array.IndexOf(header, column);
            return i < 0 || i >= cells.Length ? throw new KeyNotFoundException(column) : cells[i];
        }
    }

    /// <summary>The cell as an int.</summary>
    public int Int(string column) => int.Parse(this[column], CultureInfo.InvariantCulture);

    /// <summary>The cell as a long.</summary>
    public long Long(string column) => long.Parse(this[column], CultureInfo.InvariantCulture);

    /// <summary>The cell as a flag (0 or 1).</summary>
    public bool Flag(string column) => this[column] == "1";
}

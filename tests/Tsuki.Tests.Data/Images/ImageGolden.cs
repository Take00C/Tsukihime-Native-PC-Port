using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Tsuki.Data.Containers;
using Tsuki.Data.Images;

namespace Tsuki.Tests.Data.Images;

/// <summary>One row of the oracle's <c>pictures.tsv</c> (<c>tools/oracle/image.py</c>).</summary>
public sealed record PictureRow(
    string Label, string Name, string Format, int Width, int Height, int ShownWidth, int ShownHeight, int TileW, int TileH,
    int TilesX, int TilesY, int Attr, int LiveTiles, string Sha1, string Stratum)
{
    /// <summary>The archive or standalone file the label starts with.</summary>
    public string Source => Label[..Label.IndexOf('#', StringComparison.Ordinal)];

    /// <summary>The HED entry or top-level section.</summary>
    public int Entry => int.Parse(Label[(Label.IndexOf('#', StringComparison.Ordinal) + 1)..].Split('/')[0], CultureInfo.InvariantCulture);

    /// <summary>The nested sections from the entry to the picture.</summary>
    public ImmutableArray<int> Path => [.. Label[(Label.IndexOf('#', StringComparison.Ordinal) + 1)..].Split('/').Skip(1).Select(s => int.Parse(s, CultureInfo.InvariantCulture))];
}

/// <summary>Reads the golden picture SHA-1s of <c>samples/golden/image/</c> and opens the pictures they name.</summary>
public static class ImageGolden
{
    private const string Regenerate =
        "$env:PYTHONPATH='D:\\TsukihimePort\\tools'; .venv\\Scripts\\python.exe -m oracle.image --parts sha1   (or: -m oracle.run image --parts sha1)";

    /// <summary>The python names of the pixel formats, as written in the TSV.</summary>
    public static string FormatName(PixelFormat f) => f switch
    {
        PixelFormat.Pal4 => "pal4",
        PixelFormat.Pal8 => "pal8",
        PixelFormat.Hep8 => "hep8",
        _ => "rgb565lo+a",
    };

    /// <summary>Full path of a file of the area.</summary>
    public static string Locate(string relative)
    {
        string p = System.IO.Path.Combine(GoldenData.RepoRoot, "samples", "golden", "image", relative);
        return File.Exists(p) ? p : throw new FileNotFoundException($"golden file {p} is missing; generate it with: {Regenerate}");
    }

    /// <summary>
    /// Fails if <c>&lt;part&gt;/pictures.tsv</c> is not the file that the area manifest lists (SHA-1 and size), so a test never compares
    /// against a hand-edited or half-written golden file.
    /// </summary>
    public static void VerifyAgainstManifest(string part)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Locate("manifest.json")));
        string path = part + "/pictures.tsv";
        var record = manifest.RootElement.GetProperty("files").EnumerateArray().FirstOrDefault(f => f.GetProperty("path").GetString() == path);
        if (record.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"{path} is not listed in the image manifest; regenerate with: {Regenerate}");
        }

        byte[] bytes = File.ReadAllBytes(Locate(path));
        Assert.Equal(record.GetProperty("size").GetInt64(), bytes.LongLength);
        Assert.Equal(record.GetProperty("sha1").GetString(), Convert.ToHexStringLower(SHA1.HashData(bytes)));
    }

    /// <summary>The rows of <c>&lt;part&gt;/pictures.tsv</c>.</summary>
    public static List<PictureRow> Rows(string part)
    {
        var rows = new List<PictureRow>();
        string[] lines = File.ReadAllLines(Locate(part + "/pictures.tsv"));
        Assert.StartsWith("label\tname\tformat\t", lines[0], StringComparison.Ordinal);
        for (int i = 1; i < lines.Length; i++)
        {
            string[] f = lines[i].Split('\t');
            int I(int k) => int.Parse(f[k], CultureInfo.InvariantCulture);
            rows.Add(new PictureRow(f[0], f[1], f[2], I(3), I(4), I(5), I(6), I(7), I(8), I(9), I(10), I(11), I(12), f[13], f[14]));
        }

        return rows;
    }
}

/// <summary>The archives a test has opened, closed together. Safe to use from several threads.</summary>
public sealed class ArchiveSet : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<object>> _open = new(StringComparer.Ordinal);

    /// <summary>The container of the HED entry or top-level section named by <paramref name="row"/>, descended to the picture.</summary>
    public MzpContainer Open(PictureRow row)
    {
        string source = row.Source;
        var archive = _open.GetOrAdd(source, s => new Lazy<object>(() =>
            s.EndsWith(".mrg", StringComparison.Ordinal) ? StandaloneMrg.Open(GameImage.Path(s)) : GameImage.Open(s))).Value;
        var container = archive is StandaloneMrg mrg ? mrg.Container.Child(row.Entry) : ((MrgArchive)archive).OpenContainer(row.Entry);
        foreach (int p in row.Path)
        {
            container = container.Child(p);
        }

        return container;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var lazy in _open.Values)
        {
            if (lazy.IsValueCreated && lazy.Value is IDisposable d)
            {
                d.Dispose();
            }
        }
    }
}

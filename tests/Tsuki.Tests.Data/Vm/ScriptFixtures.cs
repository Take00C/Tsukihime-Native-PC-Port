using System.Security.Cryptography;
using System.Text.Json;
using Tsuki.Data.Compression;
using Tsuki.Data.Containers;

namespace Tsuki.Tests.Data.Vm;

/// <summary>The 549 decoded scripts of the user's folder and the golden files of package C1.</summary>
public static class ScriptFixtures
{
    /// <summary>Number of scripts in allscr.mrg (script.md 2.1).</summary>
    public const int ScriptCount = 549;

    private static readonly Lazy<byte[][]> Decoded = new(DecodeAll);

    /// <summary>Script <paramref name="k"/> after MZX0 and XOR 0xFF (allscr.mrg section k + 3).</summary>
    public static byte[] Script(int k) => Decoded.Value[k];

    /// <summary>All 549 scripts.</summary>
    public static byte[][] AllScripts => Decoded.Value;

    /// <summary>The golden area written by <c>python -m oracle.script</c> (<c>samples/golden/script</c>).</summary>
    public static string ScriptArea => Path.Combine(GoldenData.RepoRoot, "samples", "golden", "script");

    /// <summary>The slice export of the oracle runner (<c>samples/golden/slice/scripts</c>).</summary>
    public static string SliceScripts => Path.Combine(GoldenData.RepoRoot, "samples", "golden", "slice", "scripts");

    /// <summary>Reads a JSON file of the golden area; fails with the command that creates it when it is missing.</summary>
    public static JsonDocument ReadJson(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"golden file {path} is missing; create it with: $env:PYTHONPATH='D:\\TsukihimePort\\tools'; .venv\\Scripts\\python.exe -m oracle.script (script area) or -m oracle.run slice (slice)");
        }

        return JsonDocument.Parse(File.ReadAllBytes(path));
    }

    /// <summary>Decodes a hex string.</summary>
    public static byte[] FromHex(string hex) => Convert.FromHexString(hex);

    /// <summary>SHA-256 of the bytes, lower-case hex.</summary>
    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[][] DecodeAll()
    {
        using var mrg = StandaloneMrg.Open(GameImage.Path("allscr.mrg"));
        MzpContainer c = mrg.Container;
        if (c.Count != ScriptCount + 3)
        {
            throw new InvalidDataException($"allscr.mrg has {c.Count} sections, expected {ScriptCount + 3}");
        }

        var scripts = new byte[ScriptCount][];
        Parallel.For(0, ScriptCount, k =>
        {
            MzxDecoded d = Mzx.Decompress(c.Section(k + 3).Span, invert: true);
            scripts[k] = d.ToArray();
        });
        return scripts;
    }
}

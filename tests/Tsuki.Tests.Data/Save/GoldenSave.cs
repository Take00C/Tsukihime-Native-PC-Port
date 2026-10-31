using System.Security.Cryptography;
using System.Text.Json;

namespace Tsuki.Tests.Data.Save;

/// <summary>Reads the save oracle of <c>samples/golden/save/</c> (<c>tools/oracle/save.py</c>).</summary>
internal static class GoldenSave
{
    private const string Regenerate = "$env:PYTHONPATH='D:\\TsukihimePort\\tools'; .venv\\Scripts\\python.exe -m oracle.save";

    /// <summary>A verified JSON file of the area (size and SHA-1 checked against the manifest).</summary>
    public static JsonDocument Json(string relative)
    {
        string dir = Path.Combine(GoldenData.RepoRoot, "samples", "golden", "save");
        string p = Path.Combine(dir, relative);
        if (!File.Exists(p))
        {
            throw new FileNotFoundException($"golden file {p} is missing; generate it with: {Regenerate}");
        }

        byte[] data = File.ReadAllBytes(p);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "manifest.json")));
        JsonElement record = manifest.RootElement.GetProperty("files").EnumerateArray()
            .FirstOrDefault(f => f.GetProperty("path").GetString() == relative);
        if (record.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"{relative} is not listed in the save manifest; regenerate with: {Regenerate}");
        }

        string sha1 = Convert.ToHexStringLower(SHA1.HashData(data));
        if (record.GetProperty("size").GetInt64() != data.Length || record.GetProperty("sha1").GetString() != sha1)
        {
            throw new InvalidOperationException($"{relative} differs from the save manifest; regenerate with: {Regenerate}");
        }

        return JsonDocument.Parse(data);
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using Tsuki.Data.Compression;
using Tsuki.Data.Containers;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Data.Media;

/// <summary>Reads the audio oracle of <c>samples/golden/audio/</c> (<c>tools/oracle/audio.py</c>).</summary>
public static class AudioGolden
{
    private const string Regenerate =
        "$env:PYTHONPATH='D:\\TsukihimePort\\tools'; .venv\\Scripts\\python.exe -m oracle.audio --parts hevag,atrac9,mixer";

    /// <summary>Full path of a file of the area; fails with the command that makes it.</summary>
    public static string Locate(string relative)
    {
        string p = Path.Combine(GoldenData.RepoRoot, "samples", "golden", "audio", relative);
        return File.Exists(p) ? p : throw new FileNotFoundException($"golden file {p} is missing; generate it with: {Regenerate}");
    }

    /// <summary>Reads a file after checking its size and SHA-1 against the area manifest (never a hand-edited file).</summary>
    public static byte[] ReadVerified(string relative)
    {
        byte[] data = File.ReadAllBytes(Locate(relative));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Locate("manifest.json")));
        JsonElement record = manifest.RootElement.GetProperty("files").EnumerateArray()
            .FirstOrDefault(f => f.GetProperty("path").GetString() == relative);
        if (record.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"{relative} is not listed in the audio manifest; regenerate with: {Regenerate}");
        }

        string sha1 = Convert.ToHexStringLower(SHA1.HashData(data));
        if (record.GetProperty("size").GetInt64() != data.Length || record.GetProperty("sha1").GetString() != sha1)
        {
            throw new InvalidOperationException($"{relative} differs from the audio manifest; regenerate with: {Regenerate}");
        }

        return data;
    }

    /// <summary>A verified TSV file as rows of named cells.</summary>
    public static List<TsvRow> Tsv(string relative)
    {
        string[] lines = System.Text.Encoding.UTF8.GetString(ReadVerified(relative)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] header = lines[0].Split('\t');
        return [.. lines.Skip(1).Select(l => new TsvRow(header, l.Split('\t')))];
    }

    /// <summary>A verified f32le file.</summary>
    public static float[] Floats(string relative)
    {
        byte[] b = ReadVerified(relative);
        float[] f = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, f, 0, f.Length * 4);
        return f;
    }

    /// <summary>The bank's triplet base name.</summary>
    public static string BankFile(AudioBank bank) => bank switch
    {
        AudioBank.Bgm => "bgm",
        AudioBank.Se => "vse",
        AudioBank.Voice => "voice",
        AudioBank.Voice2 => "voice2",
        _ => "vag",
    };

    /// <summary>The oracle's bank name back to the enum.</summary>
    public static AudioBank Bank(string name) => name switch
    {
        "bgm" => AudioBank.Bgm,
        "vse" => AudioBank.Se,
        "voice" => AudioBank.Voice,
        "voice2" => AudioBank.Voice2,
        "vag" => AudioBank.Vag,
        _ => throw new ArgumentException(name),
    };

    /// <summary>
    /// The decodable stream of an entry (tsuki.audio.AudioBank.stream): the RIFF or VAGp bytes; voice entries are LV-unwrapped
    /// and MZX0-decompressed from their whole sector span.
    /// </summary>
    public static ReadOnlyMemory<byte> Stream(MrgArchive archive, AudioBank bank, int index) =>
        bank is AudioBank.Voice or AudioBank.Voice2 ? Lv.Decompress(archive.SectorMemory(index).Span).Data : archive.Memory(index);

    /// <summary>Lower-case hex SHA-1 of float samples as little-endian bytes.</summary>
    public static string Sha1(ReadOnlySpan<float> samples) =>
        Convert.ToHexStringLower(SHA1.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples)));
}

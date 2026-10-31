// Spike S3: the real pictures exported by tools/export_s3.py (samples/spikes/s3/pictures).
using System.Diagnostics;
using System.Text.Json;

namespace Tsuki.Spikes.S3;

internal sealed record Picture(string Name, uint Texture, int Width, int Height, int AnchorX, int AnchorY, byte[]? Pixels);

internal sealed class PictureSet
{
    private readonly Dictionary<string, Picture> _pictures = new(StringComparer.Ordinal);

    public double ReadMs { get; private set; }
    public double UploadMs { get; private set; }
    public long Bytes { get; private set; }
    public IReadOnlyDictionary<string, Picture> All => _pictures;

    public Picture this[string name] => _pictures.TryGetValue(name, out var p) ? p
        : throw new KeyNotFoundException($"picture {name} not in the S3 manifest (run tools/export_s3.py)");

    /// <summary>Load every picture of the manifest and upload it as one RGBA8 texture; set
    /// <paramref name="keepPixels"/> to keep the CPU copy.</summary>
    public static PictureSet Load(Compositor c, string dataDir, bool keepPixels)
    {
        var set = new PictureSet();
        string dir = Path.Combine(dataDir, "pictures");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "manifest.json")));
        foreach (var p in doc.RootElement.GetProperty("pictures").EnumerateObject())
        {
            var e = p.Value;
            int w = e.GetProperty("width").GetInt32(), h = e.GetProperty("height").GetInt32();
            var anchor = e.GetProperty("anchor");
            long t0 = Stopwatch.GetTimestamp();
            byte[] px = File.ReadAllBytes(Path.Combine(dir, e.GetProperty("file").GetString()!));
            long t1 = Stopwatch.GetTimestamp();
            uint tex = c.CreateTexture(w, h, px);
            long t2 = Stopwatch.GetTimestamp();
            set.ReadMs += Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
            set.UploadMs += Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
            set.Bytes += px.Length;
            set._pictures[p.Name] = new Picture(p.Name, tex, w, h, anchor[0].GetInt32(), anchor[1].GetInt32(), keepPixels ? px : null);
        }
        return set;
    }
}

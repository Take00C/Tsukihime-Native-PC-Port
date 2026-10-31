// Spike S4: reads samples/spikes/s4/oracle.json written by oracle/s4_oracle.py (Python reference).
using System.Text.Json;

namespace S4.BigPicture;

internal sealed record OraclePicture(string Arc, int Index, string Name, string Format, int ShownW, int ShownH, string Sha1, double PyMs, string[] Roles);

internal sealed record SceneLayer(string Arc, int Index);

internal sealed record Scene(string Label, int BgCatalog, SceneLayer[] Layers, int Sprites);

internal sealed class Oracle
{
    public required Dictionary<(string, int), OraclePicture> Pictures { get; init; }
    public required List<Scene> Scenes { get; init; }
    public required Scene Heavy { get; init; }
    public required List<(string Arc, int Index, string Rank)> Largest { get; init; }
    public required JsonElement Usage { get; init; }

    public static Oracle Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var pics = new Dictionary<(string, int), OraclePicture>();
        foreach (var p in root.GetProperty("pictures").EnumerateArray())
        {
            var shown = p.GetProperty("shown");
            var op = new OraclePicture(p.GetProperty("arc").GetString()!, p.GetProperty("index").GetInt32(),
                p.GetProperty("name").GetString() ?? "", p.GetProperty("format").GetString()!,
                shown[0].GetInt32(), shown[1].GetInt32(), p.GetProperty("sha1").GetString()!,
                p.GetProperty("py_ms").GetDouble(),
                p.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray());
            pics[(op.Arc, op.Index)] = op;
        }
        var scenes = new List<Scene>();
        int k = 0;
        foreach (var s in root.GetProperty("scenes").EnumerateArray())
            scenes.Add(ParseScene($"scene{k++:00}", s));
        var largest = root.GetProperty("largest").EnumerateArray()
            .Select(x => (x.GetProperty("arc").GetString()!, x.GetProperty("index").GetInt32(), x.GetProperty("rank").GetString()!))
            .ToList();
        return new Oracle
        {
            Pictures = pics,
            Scenes = scenes,
            Heavy = ParseScene("heavy", root.GetProperty("heavy_scene")),
            Largest = largest,
            Usage = root.GetProperty("usage").Clone(),
        };
    }

    private static Scene ParseScene(string label, JsonElement s)
    {
        var layers = new List<SceneLayer>();
        void Add(JsonElement L)
        {
            foreach (var l in L.EnumerateArray())
            {
                var sl = new SceneLayer(l[0].GetString()!, l[1].GetInt32());
                if (!layers.Contains(sl))
                    layers.Add(sl); // a shared sprite base is decoded once (texture cache)
            }
        }
        var bg = s.GetProperty("bg");
        Add(bg.GetProperty("layers"));
        int n = 0;
        foreach (var sp in s.GetProperty("sprites").EnumerateArray())
        {
            Add(sp.GetProperty("layers"));
            n++;
        }
        return new Scene(label, bg.GetProperty("catalog").GetInt32(), layers.ToArray(), n);
    }
}

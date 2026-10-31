using System.Text.Json;
using Tsuki.Data.Catalog;
using Tsuki.Data.Eboot;
using Tsuki.Data.Verification;
using Tsuki.Data.IO;

namespace Tsuki.Tests.Data.Catalog;

/// <summary>
/// Cross-checks of the catalog against the two other places that hold the same facts: the golden slice that
/// <c>oracle.export_slice</c> wrote (<c>samples/golden/slice/eboot/</c>, written by another tool from the same Python readers) and the
/// build check.
/// </summary>
public sealed class SliceCrossCheckTests
{
    private static JsonDocument Slice(string file) =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(GoldenData.RepoRoot, "samples", "golden", "slice", "eboot", file)));

    private static int? PackedOf(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetProperty("packed").GetInt32();

    [GameDataFact]
    public void TheEighteenSlicePicturesResolveLikeTheGoldenSlice()
    {
        var cat = SharedEboot.Catalog;
        using var doc = Slice("catalog.json");
        var root = doc.RootElement;
        Assert.Equal(cat.Images.Count, root.GetProperty("table").GetProperty("used").GetInt32());
        Assert.Equal(cat.Images.EmptySlots, root.GetProperty("table").GetProperty("empty").GetInt32());
        Assert.Equal(cat.PictureNames.Count, root.GetProperty("names_table").GetProperty("entries").GetInt32());
        var records = root.GetProperty("records").EnumerateArray().ToArray();
        Assert.Equal(18, records.Length);
        foreach (var g in records)
        {
            string key = g.GetProperty("key").GetString()!;
            var r = cat.Resolver.Resolve(key);
            Assert.True(r.Found, key);
            Assert.Equal(g.GetProperty("name_index").GetInt32(), r.Index);
            Assert.Equal(g.GetProperty("name").GetString(), cat.PictureNames[r.Index]);
            Assert.Equal(g.GetProperty("file").GetString(), r.File);
            Assert.Equal(g.GetProperty("overlay").GetString(), r.Overlay);
            Assert.Equal(PackedOf(g.GetProperty("file_id")), r.FileId?.Packed);
            Assert.Equal(PackedOf(g.GetProperty("overlay_id")), r.OverlayId?.Packed);
            Assert.Equal(PackedOf(g.GetProperty("reduced_id")), r.ReducedId?.Packed);

            var rec = g.GetProperty("record");
            var e = cat.Images[r.Index];
            Assert.Equal(rec.GetProperty("index").GetInt32(), e.Index);
            Assert.Equal(rec.GetProperty("file_offset").GetInt64(), e.FileOffset);
            Assert.Equal(rec.GetProperty("name0").GetString(), e.Name0);
            Assert.Equal(rec.GetProperty("name1").GetString(), e.Name1);
            Assert.Equal(rec.GetProperty("w3").GetInt32(), e.W3);
            Assert.Equal(rec.GetProperty("h3").GetInt32(), e.H3);
            Assert.Equal(rec.GetProperty("width").GetInt32(), e.Width);
            Assert.Equal(rec.GetProperty("height").GetInt32(), e.Height);
            Assert.Equal(rec.GetProperty("anchor_x").GetInt32(), e.AnchorX);
            Assert.Equal(rec.GetProperty("anchor_y").GetInt32(), e.AnchorY);
            Assert.Equal(rec.GetProperty("pic_flags").GetInt32(), e.PicFlags);
            Assert.Equal(rec.GetProperty("band_stride").GetInt32(), e.BandStride);
            string[] langs = ["ja", "en", "zc", "zt"];
            for (int l = 0; l < 4; l++)
            {
                var rect = rec.GetProperty("source_rect").GetProperty(langs[l]);
                var got = e.SourceRect(l);
                Assert.Equal([rect[0].GetInt32(), rect[1].GetInt32(), rect[2].GetInt32(), rect[3].GetInt32()], [got.X, got.Y, got.X + got.Width, got.Y + got.Height]);
            }
        }
    }

    [GameDataFact]
    public void TheMovieRecordsAndTheRemapEqualTheGoldenSlice()
    {
        var cat = SharedEboot.Catalog;
        using var doc = Slice("movies.json");
        var root = doc.RootElement;
        var records = root.GetProperty("records").EnumerateArray().ToArray();
        Assert.Equal(cat.Movies.Count, records.Length);
        for (int i = 0; i < records.Length; i++)
        {
            Assert.Equal(records[i].GetProperty("id").GetInt32(), cat.Movies[i].MovieId);
            Assert.Equal(records[i].GetProperty("va").GetInt64(), cat.Movies[i].Va);
            Assert.Equal(records[i].GetProperty("path").GetString(), cat.Movies[i].Path);
            Assert.Equal(records[i].GetProperty("raw").EnumerateArray().Select(x => x.GetUInt32()), cat.Movies[i].RawFields);
        }

        Assert.Equal(root.GetProperty("language_suffixes").EnumerateArray().Select(x => x.GetString()), cat.LanguageSuffixes);
        Assert.Equal(root.GetProperty("prologue_table").GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()), cat.PrologueIds);
        Assert.Equal(EbootAddresses.PrologueTable, root.GetProperty("prologue_table").GetProperty("va").GetInt64());
        foreach (var kv in root.GetProperty("remap").GetProperty("ids").EnumerateObject())
        {
            int id = int.Parse(kv.Name, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(kv.Value.EnumerateArray().Select(x => x.GetInt32()), Enumerable.Range(0, 4).Select(l => MovieRules.IndexForLanguage(id, l)));
        }
    }

    [GameDataFact]
    public void TheBuildCheckAndTheStructuralProbesAcceptTheSameFolder()
    {
        var files = new DirectoryGameFiles(GameFolder.Require());
        var report = BuildCheck.Run(files);
        Assert.True(report.Accepted, BuildReportFormatter.Format(report, verbose: true));
        var probes = EbootProbes.Run(files.ReadAll("eboot.bin"));
        Assert.All(probes, p => Assert.True(p.Passed, $"{p.Id}: {p.Message}"));
        // the probes do not repeat what the build check has: its ids stay disjoint
        var ids = report.Checks.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(probes, p => ids.Contains(p.Id));
        Assert.Contains("eboot.probe.self", ids);
        Assert.Contains("eboot.probe.hunex", ids);
    }

    [GameDataFact]
    public void TheSceneIdTableStartsWhereTheProbeLooks()
    {
        using var doc = Slice("scenes.json");
        var ids = doc.RootElement.GetProperty("scene_ids").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Equal(549, ids.Length);
        Assert.Equal(EbootAddresses.SceneIds, doc.RootElement.GetProperty("va").GetInt64());
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(ids[i], SharedEboot.Reader.I16(EbootAddresses.SceneIds + (2 * i)));
        }
    }
}

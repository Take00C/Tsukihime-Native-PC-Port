#if TSUKI_DEV
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tsuki.Data;
using Tsuki.Data.Catalog;
using Tsuki.Data.Golden;
using Tsuki.Data.Images;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;
using Tsuki.Engine.Vm;
using Tsuki.Tests.Data.Golden;

namespace Tsuki.Tests.Data.Stage;

/// <summary>
/// The stage on the golden slice (<c>samples/golden/slice</c>) through <c>GoldenSource</c>: the motion tables and picture catalog it holds are the stage's
/// tables here, and the statements the traced paths execute (scripts 2 and 3 of the slice, and the retail path through scripts 0 and 1) are the scenario.
/// </summary>
/// <remarks>
/// Three checks. (1) The per-frame motion samples of <c>models/motion_samples.json</c> (the oracle's <c>motion_value</c> for the motions the slice starts)
/// against <see cref="MotionMath"/>. (2) The layer statements of the traced paths, with updates between them, through <see cref="StageRuntime"/> and through
/// the engine under emulation (<c>engine_stage.py --scenario</c>): scene state, drawn and committed records, draw order and the random generator after every
/// step. (3) In the same run, the frame matrix of every layer whose drawn record changed against the oracle's projection (<c>frame_matrix</c>) computed from
/// the engine's records and its own image catalog.
/// </remarks>
public sealed partial class StageGoldenTests
{
    /// <summary>Script commands the stage executes (the ones <see cref="StageRuntime.Execute"/> handles).</summary>
    private static readonly HashSet<string> StageNames =
    [
        "STCH", "SCH2", "STCC", "STCP", "STBR", "STEC", "SNX2", "SNX3", "STZ4", "STRT", "STRR", "STRA", "STPV", "SOXY", "STGS", "STMA", "SPMA", "STNS", "STTR",
        "SACL", "SBAS", "STCL", "SQK2", "SHK2", "SSW2", "SQS2", "SHS2", "SSS2", "SGMA", "STCF", "FADS", "SHOK", "SHAF", "MSQK",
    ];

    private static IEbootTables Tables => SliceExpect.Source.Tables;

    [GameDataFact]
    public void TheSliceTablesAreTheStageTables()
    {
        IPictureCatalog pictures = Tables.Pictures;
        Assert.Equal(14744, pictures.Count);
        int black = pictures.Lookup("BLACK");
        Assert.True(black >= 0);
        Assert.Equal("BLACK", pictures.Name(black));
        int img = pictures.Lookup("img0597.MZP");
        Assert.Equal(604, img);
        Assert.Equal(new PictureInfo(345, 206, 0, 0, false), pictures.Info(img));
        Assert.True(pictures.Lookup("IMG1696_11_11") >= 0, "the retail path's variant picture is in the catalog");
        Assert.Equal(-1, pictures.Lookup("NOT_A_PICTURE"));

        IMotionTables motions = Tables.Motions;
        Assert.Equal(24192, motions.MotionCount);
        Assert.Equal(71905, motions.KeyCount);
        Assert.Equal(1000, motions.Duration(22523));
        Assert.Equal(2800, motions.Duration(1));
        Assert.Equal(66625, motions.KeyEnd(22523));
        Assert.Equal(66623, motions.KeyEnd(22522));
        Assert.Throws<GoldenSliceMissingException>(() => motions.Duration(7));
    }

    [GameDataFact]
    public void TheMotionSamplesOfTheSliceEqualTheOracle()
    {
        var table = new MotionTable(Tables.Motions);
        using JsonDocument doc = SliceExpect.Json("models/motion_samples.json");
        int rows = 0;
        int values = 0;
        foreach (JsonElement mo in doc.RootElement.GetProperty("motions").EnumerateArray())
        {
            int id = mo.GetProperty("motion").GetInt32();
            int div = mo.GetProperty("divisor").GetInt32();
            int loop = mo.GetProperty("loop").GetInt32();
            MotionRef m = table.Get(id) ?? throw new InvalidOperationException($"motion {id}");
            ulong flags = table.Flags(id);
            Assert.Equal(mo.GetProperty("duration_ms").GetInt64(), m.Duration);
            Assert.Equal(mo.GetProperty("field_mask").GetUInt64(), flags);
            Assert.Equal(loop, table.Loop(id));
            foreach (JsonElement row in mo.GetProperty("samples").EnumerateArray())
            {
                int n = row.GetProperty("frame").GetInt32();
                string where = $"motion {id} frame {n}";
                double t0 = MotionMath.MotionTimeMs(n, div);
                Assert.True(row.GetProperty("t_ms").GetDouble() == t0, $"{where}: motion time {row.GetProperty("t_ms").GetDouble():R} / c# {t0:R}");
                double? t = MotionMath.KeyTime(table, m, t0, loop);
                JsonElement keyT = row.GetProperty("key_t_ms");
                if (keyT.ValueKind == JsonValueKind.Null)
                {
                    Assert.Null(t);
                    Assert.Empty(row.GetProperty("values").EnumerateObject());
                    rows++;
                    continue;
                }

                Assert.True(t is not null && keyT.GetDouble() == t.Value, $"{where}: key time {keyT.GetDouble():R} / c# {t:R}");
                int k = m.End - 1;
                for (int i = m.Start; i < m.End; i++)
                {
                    if ((double)table.Val(i, 0) >= t!.Value)
                    {
                        k = i;
                        break;
                    }
                }

                Assert.Equal(row.GetProperty("key").GetInt32(), k);
                var expect = new Dictionary<int, double>();
                foreach (JsonProperty v in row.GetProperty("values").EnumerateObject())
                {
                    expect[int.Parse(v.Name, CultureInfo.InvariantCulture)] = v.Value.GetDouble();
                }

                var got = new Dictionary<int, double>();
                for (int f = 3; f <= 27; f++)
                {
                    bool found = Array.IndexOf(FoundFields, f) < 0 || MotionMath.Found(table, m, k, f);
                    if ((flags & FieldMasks[f]) != 0 && found)
                    {
                        got[f] = MotionMath.MotionValue(table, f, m.Start, m.End - 1, k, t.Value, 0.0);
                    }
                }

                Assert.True(expect.Keys.Order().SequenceEqual(got.Keys.Order()), $"{where}: fields {string.Join(',', expect.Keys.Order())} / c# {string.Join(',', got.Keys.Order())}");
                foreach ((int f, double want) in expect)
                {
                    Assert.True(BitConverter.DoubleToUInt64Bits(want) == BitConverter.DoubleToUInt64Bits(got[f]), $"{where}: field {f} {want:R} / c# {got[f]:R}");
                    values++;
                }

                rows++;
            }
        }

        Assert.True(rows > 5000, $"only {rows} motion rows");
        Assert.True(values > 5000, $"only {values} motion values");
        TestContext.Current.SendDiagnosticMessage($"{rows} motion samples, {values} values compared with the oracle");
    }

    [GameDataFact]
    public void TheLayoutsOfTheGameFolderEqualTheTileRecordsOfTheOracle()
    {
        using GameFolderSource game = GameFolderSource.Open(GameFolder.Require());
        var real = new GamePictureLayouts(game);
        IPictureLayoutSource golden = SliceExpect.Source.Tables.StageLayouts;
        IPictureCatalog catalog = Tables.Pictures;
        var keys = new List<string>();
        using (JsonDocument doc = SliceExpect.Json("eboot/catalog.json"))
        {
            keys.AddRange(doc.RootElement.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("key").GetString()!));
        }

        using (JsonDocument doc = SliceExpect.Json("retail/path.json"))
        {
            keys.AddRange(doc.RootElement.GetProperty("catalog").EnumerateArray().Select(r => r.GetProperty("key").GetString()!));
        }

        int compared = 0;
        foreach (string key in keys.Distinct())
        {
            int pic = catalog.Lookup(key);
            Assert.True(pic >= 0, key);
            Assert.Equal(game.Catalog.Resolver.IndexOf(key), pic);
            foreach (int language in new[] { 0, 1 })
            {
                PictureLayout want = golden.Get(pic, language) ?? throw new InvalidOperationException($"{key}: the slice has no layout");
                PictureLayout got = real.Get(pic, language) ?? throw new InvalidOperationException($"{key}: the game folder has no layout");
                Assert.True(want.Width == got.Width && want.Height == got.Height, $"{key}: size {want.Width}x{want.Height} (oracle) / {got.Width}x{got.Height}");
                Assert.Equal(want.Files.Count, got.Files.Count);
                for (int f = 0; f < want.Files.Count; f++)
                {
                    Assert.Equal((want.Files[f].Archive, want.Files[f].Entry, want.Files[f].OffsetX, want.Files[f].OffsetY), (got.Files[f].Archive, got.Files[f].Entry, got.Files[f].OffsetX, got.Files[f].OffsetY));
                    Assert.True(want.Files[f].Tiles.SequenceEqual(got.Files[f].Tiles), $"{key}: the tile quads differ: {Difference(want.Files[f].Tiles, got.Files[f].Tiles)}");
                    Assert.NotEmpty(got.Files[f].Tiles);
                }

                compared++;
            }
        }

        Assert.True(compared >= 40, $"only {compared} layouts were compared");
        TestContext.Current.SendDiagnosticMessage($"{compared} layouts of {keys.Distinct().Count()} pictures compared with the oracle's tile records");
    }

    private static string Difference(IReadOnlyList<TileQuad> want, IReadOnlyList<TileQuad> got)
    {
        for (int i = 0; i < Math.Max(want.Count, got.Count); i++)
        {
            if (i >= want.Count || i >= got.Count || want[i] != got[i])
            {
                return $"quad {i} of {want.Count} / {got.Count}: oracle {(i < want.Count ? want[i].ToString() : "none")} / game {(i < got.Count ? got[i].ToString() : "none")}";
            }
        }

        return "equal";
    }

    [GameDataFact]
    public void EveryPictureOfTheCatalogHasALayoutOrIsMissingFromTheData()
    {
        using GameFolderSource game = GameFolderSource.Open(GameFolder.Require());
        var layouts = new GamePictureLayouts(game);
        int count = game.Catalog.Images.Count;
        var missing = new List<string>();
        var empty = new List<(string Name, PictureFileLayout File)>();
        int bands = 0;
        int laidOut = 0;
        for (int pic = 0; pic < count; pic++)
        {
            CatalogEntry entry = game.Catalog.Images[pic];
            if (string.IsNullOrEmpty(entry.Name0))
            {
                continue;
            }

            foreach (int language in new[] { 0, 1, 2, 3 })
            {
                PictureLayout? layout = layouts.Get(pic, language);
                if (layout is null)
                {
                    if (language == 0)
                    {
                        missing.Add(entry.Name);
                    }
                }
                else if (layout.Files.Any(f => f.Tiles.Count == 0))
                {
                    if (language == 0)
                    {
                        empty.AddRange(layout.Files.Where(f => f.Tiles.Count == 0).Select(f => (entry.Name, f)));
                    }
                }
                else
                {
                    laidOut++;
                    bands += entry.HasLanguageBands ? 1 : 0;
                    Assert.Equal(entry.SourceRect(language).Width, layout.Width);
                    Assert.Equal(entry.SourceRect(language).Height, layout.Height);
                }
            }
        }

        // pictures that are not drawn: the catalog names files that no archive holds (a few sprite variants), and files whose tiles all have flag 0 (placeholders and unused solid colours)
        Assert.True(missing.Count <= count / 100, $"{missing.Count} pictures name files that no archive holds: {string.Join(' ', missing.Take(12))}");
        foreach ((string name, PictureFileLayout file) in empty)
        {
            ImageHeader h = game.OpenImage(file.Archive, file.Entry).Header;
            Assert.True(Enumerable.Range(0, h.Tiles).All(t => !h.IsLive(t)), $"{name}: archive {file.Archive} entry {file.Entry} has live tiles but no quad");
        }

        Assert.True(empty.Count <= count / 100, $"{empty.Count} pictures have no live tile: {string.Join(' ', empty.Take(12).Select(e => e.Name))}");
        Assert.True(laidOut > 4 * (count - 100), $"only {laidOut} layouts");
        Assert.True(bands > 0, "no picture with language bands was laid out");
        TestContext.Current.SendDiagnosticMessage($"{count} catalog records: {laidOut} layouts (4 languages), {missing.Count} pictures with a file in no archive, {empty.Count} files without a live tile");
    }

    [GameDataFact]
    public void TheStageEqualsTheEngineOnTheRetailPath()
    {
        using JsonDocument path = SliceExpect.Json("retail/path.json");
        var statements = new List<string>();
        var scripts = new Dictionary<int, byte[]>();
        foreach (JsonElement step in path.RootElement.GetProperty("steps").EnumerateArray())
        {
            if (StageNames.Contains(step.GetProperty("name").GetString()!))
            {
                int script = step.GetProperty("script").GetInt32();
                if (!scripts.TryGetValue(script, out byte[]? bytes))
                {
                    bytes = SliceExpect.Source.Slice.ReadAll($"retail/script_{script:D4}.bin");
                    scripts[script] = bytes;
                }

                statements.Add(StatementText(bytes, step.GetProperty("offset").GetInt32()));
            }
        }

        Assert.True(statements.Count > 60, $"{statements.Count} stage statements on the retail path");
        RunAgainstTheEngine(statements, 12);
    }

    [GameDataFact]
    public void TheStageEqualsTheEngineOnTheSliceScripts()
    {
        var statements = new List<string>();
        foreach (int k in new[] { 2, 3 })
        {
            ScriptImage script = SliceExpect.Source.Script(k);
            using JsonDocument doc = SliceExpect.Json($"scripts/script_{k:D4}.json");
            foreach (JsonElement step in doc.RootElement.GetProperty("trace").GetProperty("steps").EnumerateArray())
            {
                if (StageNames.Contains(step.GetProperty("name").GetString()!))
                {
                    int offset = step.GetProperty("offset").GetInt32();
                    Assert.Equal(offset, script.SegmentStarts[step.GetProperty("index").GetInt32()]);
                    statements.Add(StatementText(script.Bytes.Span, offset));
                }
            }
        }

        Assert.True(statements.Count > 60, $"{statements.Count} stage statements in the slice scripts");
        RunAgainstTheEngine(statements, 20);
    }

    private static void RunAgainstTheEngine(List<string> statements, int layers)
    {
        IPictureCatalog catalog = Tables.Pictures;
        StageRuntime rt = StageEngineRun.BaseRuntime(catalog, Tables.Motions);
        rt.Layouts = SliceExpect.Source.Tables.StageLayouts;
        string file = Path.Combine(Path.GetTempPath(), $"tsuki_stage_{Environment.ProcessId}_{statements.Count}_{layers}.json");
        Assert.DoesNotContain(' ', file);
        File.WriteAllText(file, JsonSerializer.Serialize(new { steps = Scenario(statements) }), new UTF8Encoding(false));
        try
        {
            StageEngineResult r = StageEngineRun.Compare(rt, $"--scenario {file} --layers {layers}", layers, catalog, TimeSpan.FromMinutes(20), rt.Layouts);
            Assert.True(r.Failures.Count == 0, string.Join(Environment.NewLine, r.Failures));
            Assert.True(r.Steps > 500, $"only {r.Steps} steps were compared");
            Assert.True(r.Matrices > 100, $"only {r.Matrices} frame matrices were compared");
            Assert.True(r.Draws > 20, $"only {r.Draws} scene draws were compared");
            Assert.True(r.Quads > 500, $"only {r.Quads} picture quads were compared");
            TestContext.Current.SendDiagnosticMessage(
                $"{statements.Count} stage statements, {r.Steps} steps compared with the engine, {r.Matrices} frame matrices compared with the oracle, {r.Draws} scene draws, {r.Quads} picture quads");
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// The statements with updates between them: three frames after a statement, the frames of the transition after a <c>_FADS</c> (at most 90) and a long
    /// run at the end so that the motions and tweens the paths start get going; scene draws now and then (the engine's own, <c>d</c> steps).
    /// </summary>
    private static List<object[]> Scenario(List<string> statements)
    {
        var steps = new List<object[]>();
        foreach (string s in statements)
        {
            steps.Add(["s", s]);
            Match fads = FadsMs().Match(s);
            if (fads.Success)
            {
                int ms = int.Parse(fads.Groups[1].Value, CultureInfo.InvariantCulture);
                int frames = ms == 0 ? 2 : Math.Min(90, (ms * 60 / 1000) + 2);
                for (int i = 0; i < frames; i++)
                {
                    steps.Add(["u", 1]);
                    if (i % 7 == 0)
                    {
                        steps.Add(["d", 0]);
                    }
                }
            }
            else
            {
                steps.Add(["u", 1]);
                steps.Add(["u", 1]);
                steps.Add(["u", 2]);
                steps.Add(["d", 0]);
            }
        }

        for (int i = 0; i < 400; i++)
        {
            steps.Add(["u", 1]);
            if (i % 8 == 0)
            {
                steps.Add(["d", 0]);
            }
        }

        return steps;
    }

    private static string StatementText(ReadOnlySpan<byte> bytes, int offset)
    {
        ScriptSegment seg = ScriptSegmenter.Read(bytes, offset);
        Assert.True(seg.HasName);
        return Encoding.Latin1.GetString(bytes[seg.Underscore..seg.End]).Trim();
    }

    [GeneratedRegex(@"^_FADS\(\s*-?\d+\s*,\s*(\d+)")]
    private static partial Regex FadsMs();

    // The enable bit of each motion field and the fields with a found-scan (tsuki.misc_layer_state_and_motions FIELD_MASKS, FOUND_FIELDS).
    private static readonly int[] FoundFields = [3, 4, 5, 22, 23, 9, 10];

    private static readonly Dictionary<int, ulong> FieldMasks = new()
    {
        [3] = 0x40880, [4] = 0x5100000, [5] = 0x4000, [6] = 0x10002, [7] = 0x20004, [8] = 0x88000, [9] = 0x80200000, [10] = 0x100400000, [11] = 0x10000080,
        [12] = 0x4000400, [13] = 0x10000080, [14] = 0x4000400, [15] = 0x10, [16] = 0x20, [17] = 0x2000000, [18] = 0x200, [19] = 0x200000000, [20] = 0x200000000,
        [21] = 0x200000000, [22] = 0x20000000, [23] = 0x40000000, [24] = 0x40, [25] = 0x100, [26] = 0x1000, [27] = 0x1,
    };
}
#endif

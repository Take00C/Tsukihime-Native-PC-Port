using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tsuki.Data.Catalog;
using Tsuki.Data.Eboot;
using Tsuki.Data.Scripts;
using Tsuki.Engine.Vm;
using Tsuki.Tests.Data.Catalog;
using Tsuki.Tests.Data.Text;

namespace Tsuki.Tests.Data.Scripts;

/// <summary>
/// The eboot tables of <c>script_tables.py</c>: scene ids (549), the movie files (134), the layer motions (24,192 motions, 71,905 keyframes) and
/// the engine command table (112 entries), against the oracle (<c>script_tables_oracle.py eboot|engine</c>).
/// </summary>
public sealed class ScriptTableTests
{
    private const string Oracle = "tests/Tsuki.Tests.Data/Scripts/script_tables_oracle.py";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    [GameDataFact]
    public void The549SceneIdsEqualTheOraclesConstantsAndItsEbootReader()
    {
        SceneTable scenes = SceneTable.Read(SharedEboot.Reader);
        using JsonDocument doc = OracleRun.One(Oracle, "eboot", "scenes", Timeout);
        JsonElement e = doc.RootElement;
        int[] constants = e.GetProperty("constants").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        int[] fromEboot = e.GetProperty("eboot").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Equal(549, scenes.Count);
        Assert.Equal(constants, fromEboot);
        Assert.Equal(constants, scenes.Ids.ToArray().Select(i => (int)i).ToArray());
        Assert.Equal([110, 240, 260, 270], scenes.Ids[..4].ToArray().Select(i => (int)i).ToArray());   // RR 3.3 item 2
        Assert.True(scenes.IdsAreUnique());
        foreach (JsonElement row in e.GetProperty("index").EnumerateArray())
        {
            int? expected = row[1].ValueKind == JsonValueKind.Null ? null : row[1].GetInt32();
            Assert.Equal(expected, scenes.IndexOf(row[0].GetInt32()));
        }

        for (int i = 0; i < scenes.Count; i++)
        {
            Assert.Equal(i, scenes.IndexOf(scenes.SceneId(i)));
        }
    }

    [GameDataFact]
    public void TheSceneTableAgreesWithTheGoldenSlice()
    {
        // samples/golden/slice/eboot/scenes.json is produced by the exporter, independent of the readers
        string path = Path.Combine(GoldenData.RepoRoot, "samples", "golden", "slice", "eboot", "scenes.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
        SceneTable scenes = SceneTable.Read(SharedEboot.Reader);
        Assert.Equal(0x216130, doc.RootElement.GetProperty("va").GetInt64());
        Assert.Equal(scenes.Count, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(doc.RootElement.GetProperty("scene_ids").EnumerateArray().Select(x => x.GetInt32()).ToArray(), scenes.Ids.ToArray().Select(x => (int)x).ToArray());
    }

    [GameDataFact]
    public void TheMovieTableOfTheCatalogEqualsTheOraclesFileListInBothForms()
    {
        using JsonDocument doc = OracleRun.One(Oracle, "eboot", "movies", Timeout);
        string[] constants = doc.RootElement.GetProperty("constants").EnumerateArray().Select(x => x.GetString()!).ToArray();
        string[] fromEboot = doc.RootElement.GetProperty("eboot").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Equal(134, constants.Length);
        Assert.Equal(constants, fromEboot);
        IReadOnlyList<MovieRecord> movies = SharedEboot.Catalog.Movies;
        Assert.Equal(134, movies.Count);
        Assert.Equal(constants, movies.Select(m => m.FileName).ToArray());
        for (int i = 0; i < movies.Count; i++)
        {
            Assert.Equal(i, movies[i].MovieId);
        }
    }

    [GameDataFact]
    public void All24192MotionsAndTheir71905KeyframesEqualTheOracle()
    {
        MotionTables motions = MotionTables.Read(SharedEboot.Reader);
        using JsonDocument doc = OracleRun.One(Oracle, "eboot", "motions", Timeout);
        JsonElement e = doc.RootElement;
        Assert.Equal(24_192, e.GetProperty("count").GetInt32());
        Assert.Equal(71_905, e.GetProperty("keyframes").GetInt32());
        Assert.Equal(MotionTables.MotionCount, e.GetProperty("count").GetInt32());
        Assert.Equal(71_905, motions.KeyframeCount);
        long[] constants = e.GetProperty("constants").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        Assert.Equal([MotionTables.MotionCount, MotionTables.DurationVa, MotionTables.EndVa, MotionTables.KeysVa, MotionTables.KeySize, MotionTables.MaxKey, MotionTables.NotAnimated], constants);

        string oracle = e.GetProperty("h").GetString()!;
        int[] lengths = e.GetProperty("lengths").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        string[] ours = new string[MotionTables.MotionCount];
        Parallel.For(1, MotionTables.MotionCount + 1, id =>
        {
            var sb = new StringBuilder();
            sb.Append(id).Append('|').Append(motions.Duration(id)).Append('|');
            bool first = true;
            foreach (MotionKeyframe k in motions.Keyframes(id))
            {
                if (!first)
                {
                    sb.Append(';');
                }

                first = false;
                sb.Append(k.Time);
                foreach (long f in k.Fields.Span)
                {
                    sb.Append(',').Append(f);
                }
            }

            ours[id - 1] = OracleCanon.H16(sb.ToString());
        });
        var mismatches = new Mismatches();
        for (int i = 0; i < ours.Length; i++)
        {
            mismatches.Equal($"motion {i + 1} keyframe count", lengths[i], motions.KeyframeCountOf(i + 1));
            mismatches.Equal($"motion {i + 1} hash", oracle.Substring(16 * i, 16), ours[i]);
        }

        mismatches.AssertNone("motions");
        Assert.Equal(71_905, lengths.Sum());
    }

    [GameDataFact]
    public void TheFirstAndLastMotionAreReadValueForValue()
    {
        MotionTables motions = MotionTables.Read(SharedEboot.Reader);
        using JsonDocument doc = OracleRun.One(Oracle, "eboot", "first", Timeout);
        JsonElement e = doc.RootElement;
        Assert.Equal(e.GetProperty("duration").GetInt64(), motions.Duration(1));
        var keys = e.GetProperty("keys").EnumerateArray().ToList();
        MotionKeyframe[] ours = motions.Keyframes(1).ToArray();
        Assert.Equal(keys.Count, ours.Length);
        for (int i = 0; i < keys.Count; i++)
        {
            Assert.Equal(keys[i][0].GetInt64(), ours[i].Time);
            Assert.Equal(keys[i][1].EnumerateArray().Select(x => x.GetInt64()).ToArray(), ours[i].Fields.ToArray());
            Assert.Equal(MotionTables.FieldCount, ours[i].Fields.Length);
        }

        JsonElement last = e.GetProperty("last");
        Assert.Equal(last[1].GetInt64(), motions.Duration((int)last[0].GetInt64()));
        Assert.Equal(last[2].GetArrayLength(), motions.KeyframeCountOf(MotionTables.MotionCount));
        (int first, int end) = motions.KeyRange(MotionTables.MotionCount);
        Assert.Equal(MotionTables.MaxKey + 1, end);
        Assert.True(first <= end);
    }

    [GameDataFact]
    public void TheMotionTablesAgreeWithTheGoldenSlicesMotionExport()
    {
        // samples/golden/slice/eboot/motions.json: the motions the slice uses, with their keyframes, written by the exporter
        string path = Path.Combine(GoldenData.RepoRoot, "samples", "golden", "slice", "eboot", "motions.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
        MotionTables motions = MotionTables.Read(SharedEboot.Reader);
        JsonElement tables = doc.RootElement.GetProperty("tables");
        Assert.Equal(MotionTables.DurationVa, tables.GetProperty("duration_va").GetInt64());
        Assert.Equal(MotionTables.EndVa, tables.GetProperty("end_va").GetInt64());
        Assert.Equal(MotionTables.KeysVa, tables.GetProperty("keys_va").GetInt64());
        Assert.Equal(motions.KeyframeCount, tables.GetProperty("keyframes").GetInt32());
        int compared = 0;
        foreach (JsonElement motion in doc.RootElement.GetProperty("motions").EnumerateArray())
        {
            int id = motion.GetProperty("id").GetInt32();
            Assert.Equal(motion.GetProperty("duration_ms").GetInt64(), motions.Duration(id));
            (int first, int end) = motions.KeyRange(id);
            Assert.Equal(motion.GetProperty("first_key").GetInt32(), first);
            Assert.Equal(motion.GetProperty("end_key").GetInt32(), end);
            foreach (JsonElement key in motion.GetProperty("keys").EnumerateArray())
            {
                MotionKeyframe ours = motions.Keyframe(key.GetProperty("index").GetInt32());
                Assert.Equal(key.GetProperty("time").GetInt64(), ours.Time);
                Assert.Equal(key.GetProperty("fields").EnumerateArray().Select(x => x.GetInt64()).ToArray(), ours.Fields.ToArray());
            }

            compared++;
        }

        Assert.True(compared >= 4, $"only {compared} motions of the slice export were compared");
    }

    [Fact]
    public void TheMotionConstantsAreTheDocumentedOnes()
    {
        Assert.Equal(24_192, MotionTables.MotionCount);
        Assert.Equal(0x118E0, MotionTables.MaxKey);
        Assert.Equal(72, MotionTables.KeySize);
        Assert.Equal(27, MotionTables.FieldCount);
        Assert.Equal(-32767, MotionTables.NotAnimated);
        SceneTable synthetic = SceneTable.FromIds([5, 9, 5]);
        Assert.False(synthetic.IdsAreUnique());
        Assert.Equal(0, synthetic.IndexOf(5));          // the engine's linear search finds the first
        Assert.Null(synthetic.IndexOf(7));
    }

    // -- the engine command table ---------------------------------------------------------------------------------------

    [GameDataFact]
    public void TheEngineCommandTableEqualsTheOracleEntryForEntry()
    {
        using (JsonDocument doc = OracleRun.One(Oracle, "engine", "main", Timeout))
        {
            var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToList();
            Assert.Equal(112, entries.Count);
            Assert.Equal(EngineCommandTable.EntryCount, EngineCommandTable.Main.Count);
            foreach (JsonElement row in entries)
            {
                (long va, FrameBehaviour behaviour) = EngineCommandTable.Main[row[0].GetString()!];
                Assert.Equal(row[1].GetInt64(), va);
                Assert.Equal(row[2].GetString(), behaviour.ToString().ToLowerInvariant());
            }
        }

        using (JsonDocument doc = OracleRun.One(Oracle, "engine", "aliases", Timeout))
        {
            var aliases = doc.RootElement.GetProperty("entries").EnumerateArray().Select(r => (r[0].GetString()!, r[1].GetString()!)).ToList();
            Assert.Equal(aliases.OrderBy(a => a.Item1, StringComparer.Ordinal).ToArray(), EngineCommandTable.Aliases.Select(kv => (kv.Key, kv.Value)).OrderBy(a => a.Key, StringComparer.Ordinal).ToArray());
        }

        using (JsonDocument doc = OracleRun.One(Oracle, "engine", "keep", Timeout))
        {
            Assert.Equal(EngineCommandTable.KeepBgmFlagVa, doc.RootElement.GetProperty("va").GetInt64());
        }

        using (JsonDocument doc = OracleRun.One(Oracle, "engine", "obj", Timeout))
        {
            string[] oracle = doc.RootElement.GetProperty("names").EnumerateArray().Select(x => x.GetString()!).ToArray();
            Assert.Equal(oracle, ObjectCommands.Names.ToArray());         // C's list of the object-parser commands
            Assert.Equal(52, oracle.Length);
        }

        using (JsonDocument doc = OracleRun.One(Oracle, "engine", "handlers", Timeout))
        {
            int n = 0;
            foreach (JsonElement row in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                string name = row[0].GetString()!;
                var h = EngineCommandTable.Handler(name);
                if (row[1].ValueKind == JsonValueKind.Null)
                {
                    Assert.Null(h);
                    Assert.False(EngineCommandTable.IsEngineCommand(name));
                }
                else
                {
                    Assert.NotNull(h);
                    Assert.Equal(row[1].GetInt64(), h!.Value.HandlerVa);
                    Assert.Equal(row[2].GetString(), h.Value.Behaviour.ToString().ToLowerInvariant());
                    Assert.True(EngineCommandTable.IsEngineCommand(name));
                }

                n++;
            }

            Assert.True(n > 110);
        }
    }

    [Fact]
    public void PrefixRulesCoverEveryNameThatStartsWithJOrZAndNothingElseIsGuessed()
    {
        Assert.NotNull(EngineCommandTable.Handler("JUMP"));
        Assert.NotNull(EngineCommandTable.Handler("Z000"));
        Assert.Equal(EngineCommandTable.Main["J*"], EngineCommandTable.Handler("JAAA"));
        Assert.Null(EngineCommandTable.Handler("DMMY"));
        Assert.Null(EngineCommandTable.Handler("NOP_"));
        Assert.Null(EngineCommandTable.Handler(string.Empty));
        Assert.Equal(0x9ac1aL, EngineCommandTable.Handler("WTTM")!.Value.HandlerVa);
        Assert.Equal(FrameBehaviour.Yield, EngineCommandTable.Handler("WTTM")!.Value.Behaviour);
        Assert.Equal(EngineCommandTable.Handler("WTTM"), EngineCommandTable.Handler("WT__"));   // alias
        Assert.Equal("WTTM", EngineCommandTable.Aliases["WT__"]);
        Assert.Equal(0x174DE0C, EngineCommandTable.KeepBgmFlagVa);
    }
}

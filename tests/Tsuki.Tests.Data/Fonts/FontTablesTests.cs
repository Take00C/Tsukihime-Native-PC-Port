using System.Text.Json;
using Tsuki.Data.Eboot;
using Tsuki.Data.Fonts;
using Tsuki.Data.Verification;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;
using Tsuki.Tests.Data.Catalog;
using Tsuki.Tests.Data.Text;

namespace Tsuki.Tests.Data.Fonts;

/// <summary>
/// The glyph tables in eboot.bin (<c>tsuki.font_eboot</c>, run by <c>font_oracle.py tables</c>): 16 lookup tables with 17,423 entries and 17
/// record tables, the self-check of RR 3.3 item 2, and the exporter's slice copy (<c>samples/golden/slice/eboot/font_tables.json</c>).
/// </summary>
public sealed class FontTablesTests
{
    private const string Oracle = "tests/Tsuki.Tests.Data/Fonts/font_oracle.py";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private static readonly Lazy<EbootFontData> Shared = new(() => EbootFontData.Read(SharedEboot.Reader));

    [GameDataFact]
    public void TheFontTableSelfCheckPassesAndTheOracleAgrees()
    {
        EbootFontData data = Shared.Value;
        Assert.Empty(data.Verify());
        using JsonDocument doc = OracleRun.One(Oracle, "tables", "verify", Timeout);
        Assert.Empty(doc.RootElement.GetProperty("issues").EnumerateArray());
        Assert.Equal(doc.RootElement.GetProperty("wide_base").EnumerateArray().Select(e => e.GetInt32()).ToArray(), data.WideBase.ToArray());
        Assert.Equal(doc.RootElement.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).ToArray(), data.Tags.ToArray());
        Assert.Equal(39, data.Tags.Count);
        Assert.Equal(FontTables.StandardTagNames.ToArray(), data.Tags.ToArray());
    }

    [GameDataFact]
    public void All17423LookupEntriesEqualTheOracle()
    {
        EbootFontData data = Shared.Value;
        var mismatches = new Mismatches();
        int tables = 0;
        long entries = 0;
        foreach (JsonDocument doc in OracleRun.Records(Oracle, "tables", "lookup", Timeout))
        {
            using (doc)
            {
                JsonElement e = doc.RootElement;
                string name = e.GetProperty("name").GetString()!;
                FontLookupData ours = data.Lookups.Single(l => l.Spec.Name == name);
                mismatches.Equal($"{name} va", e.GetProperty("va").GetInt64(), ours.Spec.Va);
                mismatches.Equal($"{name} lea site", e.GetProperty("lea").GetInt64(), ours.Spec.LeaSite);
                mismatches.Equal($"{name} count", e.GetProperty("count").GetInt32(), ours.Keys.Length);
                var theirs = e.GetProperty("entries").EnumerateArray().ToList();
                mismatches.Equal($"{name} entries", theirs.Count, ours.Keys.Length);
                for (int i = 0; i < Math.Min(theirs.Count, ours.Keys.Length); i++)
                {
                    mismatches.Equal($"{name} key {i}", theirs[i][0].GetUInt64(), ours.Keys[i]);
                    mismatches.Equal($"{name} index {i}", theirs[i][1].GetInt32(), (int)ours.Indices[i]);
                }

                tables++;
                entries += theirs.Count;
            }
        }

        mismatches.AssertNone("font lookup tables");
        Assert.Equal(16, tables);
        Assert.Equal(17_423, entries);
        Assert.Equal(17_423, data.LookupEntryCount);
        using JsonDocument total = OracleRun.One(Oracle, "tables", "lookup_total", Timeout);
        Assert.Equal(17_423, total.RootElement.GetProperty("total").GetInt32());
    }

    [GameDataFact]
    public void EveryGlyphRecordIncludingTheEnginesReadsPastTheTablesEndEqualsTheOracle()
    {
        FontTables tables = Shared.Value.ToEngineTables();
        var mismatches = new Mismatches();
        int count = 0;
        long records = 0;
        foreach (JsonDocument doc in OracleRun.Records(Oracle, "tables", "records", Timeout))
        {
            using (doc)
            {
                JsonElement e = doc.RootElement;
                string name = e.GetProperty("name").GetString()!;
                GlyphRecordTable table = tables.Records(name);
                mismatches.Equal($"{name} count", e.GetProperty("count").GetInt32(), table.Count);
                var rows = e.GetProperty("entries").EnumerateArray().ToList();
                for (int i = 0; i < rows.Count; i++)
                {
                    GlyphRect r = table.Get(i);
                    mismatches.Equal(
                        $"{name} record {i}",
                        $"{rows[i][0].GetInt32()},{rows[i][1].GetInt32()},{rows[i][2].GetInt32()},{rows[i][3].GetInt32()},{rows[i][4].GetInt32()}",
                        $"{r.Tex},{r.X},{r.Y},{r.W},{r.H}");
                }

                count++;
                records += table.Count;
            }
        }

        mismatches.AssertNone("font record tables");
        Assert.Equal(17, count);
        Assert.True(records > 5000, $"only {records} records");
    }

    [GameDataFact]
    public void TheLookupTablesFeedingTheTextLayoutGiveTheOraclesGlyphIndices()
    {
        FontTables tables = Shared.Value.ToEngineTables();
        int checkedKeys = 0;
        foreach (JsonDocument doc in OracleRun.Records(Oracle, "tables", "lookup", Timeout))
        {
            using (doc)
            {
                GlyphLookupTable table = tables.Lookup(doc.RootElement.GetProperty("name").GetString()!);
                Assert.Equal(doc.RootElement.GetProperty("count").GetInt32(), table.Count);
                foreach (JsonElement row in doc.RootElement.GetProperty("entries").EnumerateArray())
                {
                    int index = row[1].GetInt32();
                    Assert.Equal(index >= 0 ? index : -1, table.Get(row[0].GetUInt64()));
                    checkedKeys++;
                }
            }
        }

        Assert.Equal(17_423, checkedKeys);
        Assert.Equal(Shared.Value.WideBase.ToArray(), tables.WideBase.ToArray());
        foreach (Language lang in Enum.GetValues<Language>())
        {
            Assert.NotNull(tables.For(lang));      // every table family the glyph resolver names exists
        }

        LanguageGlyphs ja = tables.For(Language.Ja);
        Glyph a = ja.CharGlyph("A"u8, 0, style: 0, kind: 0, scale: 1.0);
        Assert.Equal(1, a.Consumed);
        Assert.True(a.W > 0 && a.H > 0);
    }

    [GameDataFact]
    public void TheSliceExportOfTheTablesAgreesWithTheReader()
    {
        string path = Path.Combine(GoldenData.RepoRoot, "samples", "golden", "slice", "eboot", "font_tables.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
        EbootFontData data = Shared.Value;
        Assert.Equal(data.WideBase.ToArray(), doc.RootElement.GetProperty("wide_base").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal(data.Tags.ToArray(), doc.RootElement.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).ToArray());
        int lookups = 0;
        foreach (JsonElement t in doc.RootElement.GetProperty("lookups").EnumerateArray())
        {
            FontLookupData ours = data.Lookups.Single(l => l.Spec.Name == t.GetProperty("name").GetString());
            Assert.Equal(t.GetProperty("va").GetInt64(), ours.Spec.Va);
            var entries = t.GetProperty("entries").EnumerateArray().ToList();
            Assert.Equal(entries.Count, ours.Keys.Length);
            for (int i = 0; i < entries.Count; i++)
            {
                Assert.Equal(entries[i][0].GetUInt64(), ours.Keys[i]);
                Assert.Equal(entries[i][1].GetInt32(), (int)ours.Indices[i]);
            }

            lookups += entries.Count;
        }

        Assert.Equal(17_423, lookups);
        FontTables tables = data.ToEngineTables();
        foreach (JsonElement t in doc.RootElement.GetProperty("records").EnumerateArray())
        {
            GlyphRecordTable table = tables.Records(t.GetProperty("name").GetString()!);
            var entries = t.GetProperty("entries").EnumerateArray().ToList();
            Assert.Equal(entries.Count, table.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                GlyphRect r = table.Get(i);
                Assert.Equal([r.Tex, r.X, r.Y, r.W, r.H], entries[i].EnumerateArray().Select(x => x.GetInt32()).ToArray());
            }
        }
    }

    [GameDataFact]
    public void TheSelfCheckNamesEachKindOfDamageAndNothingElse()
    {
        byte[] original = SharedEboot.Data;

        EbootFontData Damaged(Action<byte[]> damage)
        {
            byte[] copy = (byte[])original.Clone();
            damage(copy);
            return EbootFontData.Read(EbootReader.Parse(copy));
        }

        // a lea displacement that no longer points at the lookup table
        IReadOnlyList<string> moved = Damaged(b => b[SharedEboot.TextFile(0x108C8 + 3)] ^= 0x10).Verify();
        Assert.Single(moved);
        Assert.StartsWith("lookup narrow: lea at 0x108c8 -> 0x", moved[0], StringComparison.Ordinal);
        Assert.EndsWith(", expected 0x626c10", moved[0], StringComparison.Ordinal);
        // a record table whose reference moved
        IReadOnlyList<string> record = Damaged(b => b[SharedEboot.TextFile(0x1146E + 3)] ^= 0x01).Verify();
        Assert.Single(record);
        Assert.StartsWith("records ja_s0: lea at 0x1146e", record[0], StringComparison.Ordinal);
        // two keys swapped: not sorted any more, indices still a permutation
        long keyVa = 0x626C10 + (16 * 5);
        IReadOnlyList<string> unsorted = Damaged(b =>
        {
            long o = SharedEboot.DataFile(keyVa);
            for (int i = 0; i < 8; i++)
            {
                (b[o + i], b[o + 16 + i]) = (b[o + 16 + i], b[o + i]);
            }
        }).Verify();
        Assert.Equal(["lookup narrow: keys not strictly sorted"], unsorted.ToArray());
        // a duplicated glyph index: no permutation any more
        IReadOnlyList<string> permutation = Damaged(b => b[SharedEboot.DataFile(0x626C10 + (16 * 7) + 8)] = 0).Verify();
        Assert.Equal(["lookup narrow: indices are not a permutation of 0..108"], permutation.ToArray());
        // a tag name and a wide base
        IReadOnlyList<string> tag = Damaged(b => b[SharedEboot.TextFile(0x187C80 + 1)] = (byte)'H').Verify();
        Assert.Single(tag);
        Assert.StartsWith("tag names differ", tag[0], StringComparison.Ordinal);
        IReadOnlyList<string> wide = Damaged(b => b[SharedEboot.TextFile(0x187E2E)] = 60).Verify();
        Assert.Equal(["unexpected wide texture bases (60, 59, 59, 57, 59)"], wide.ToArray());
        IReadOnlyList<string> site = Damaged(b => b[SharedEboot.TextFile(0x117B1 + 3)] ^= 0x01).Verify();
        Assert.Equal(["tag table reference moved"], site.ToArray());
    }

    [GameDataFact]
    public void ReadingTheTablesIsFast()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        EbootFontData data = EbootFontData.Read(SharedEboot.Reader);
        _ = data.ToEngineTables();
        _ = data.Verify();
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 1500, $"reading, converting and verifying the font tables took {watch.ElapsedMilliseconds} ms");
    }

    [GameDataFact]
    public void TheFontProbePassesOnTheUsersFileAndFailsAloneOnDamageOrGarbage()
    {
        CheckResult ok = FontTableProbe.Run(SharedEboot.Data);
        Assert.True(ok.Passed, ok.Message);
        Assert.Equal(FontTableProbe.Id, ok.Id);
        Assert.Contains("17,423", ok.Actual, StringComparison.Ordinal);

        byte[] damaged = (byte[])SharedEboot.Data.Clone();
        damaged[SharedEboot.TextFile(0x187C80 + 2)] ^= 0x20;
        CheckResult bad = FontTableProbe.Run(damaged);
        Assert.False(bad.Passed);
        Assert.Equal(BuildFailure.EbootProbeFailed, bad.Failure);
        Assert.Contains("tag names differ", bad.Message, StringComparison.Ordinal);

        Assert.False(FontTableProbe.Run(new byte[100]).Passed);
        Assert.False(FontTableProbe.Run(ReadOnlyMemory<byte>.Empty).Passed);
        Assert.False(FontTableProbe.Run(SharedEboot.Data.AsMemory(0, 5_000_000)).Passed);
        byte[] random = new byte[4096];
        new Random(7).NextBytes(random);
        Assert.False(FontTableProbe.Run(random).Passed);
    }

    [GameDataFact]
    public void TheFontProbeIsDisjointFromTheOtherEbootProbesAndTheBuildCheck()
    {
        var ids = Tsuki.Data.Eboot.EbootProbes.Run(SharedEboot.Data).Select(r => r.Id).ToList();
        Assert.DoesNotContain(FontTableProbe.Id, ids);
        Assert.Equal(8, ids.Count);
    }

    [Fact]
    public void TheSpecsAreTheDocumentedOnes()
    {
        Assert.Equal(16, EbootFontData.LookupSpecs.Count);
        Assert.Equal(17, EbootFontData.RecordSpecs.Count);
        Assert.Equal(17_423, EbootFontData.LookupSpecs.Sum(s => s.Count));
        Assert.Equal(39, EbootFontData.TagCount);
        Assert.Equal(11, EbootFontData.TagStride);
        Assert.Equal(EbootFontData.LookupSpecs.Count, EbootFontData.LookupSpecs.Select(s => s.Name).Distinct().Count());
        Assert.Equal(EbootFontData.RecordSpecs.Count, EbootFontData.RecordSpecs.Select(s => s.Name).Distinct().Count());
    }
}

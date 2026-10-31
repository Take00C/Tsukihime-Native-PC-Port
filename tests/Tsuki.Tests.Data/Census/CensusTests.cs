using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Tsuki.Data;
using Tsuki.Data.Catalog;
using Tsuki.Data.Census;
using Tsuki.Tests.Data.Source;
using Tsuki.Tests.Data.Text;

namespace Tsuki.Tests.Data.Census;

/// <summary>
/// Census T0 (PACE row 10; ROADMAP M1 acceptance 5 and 7): every reference the 549 scripts make, resolved through
/// <see cref="GameFolderSource"/>. Everything resolves except the allow-list of <see cref="CensusAllowList"/>: five script bugs and three
/// sprite bodies that are on no disk. The references are compared one by one with the Python oracle (<c>census_oracle.py</c>).
/// </summary>
public sealed class CensusTests(ITestOutputHelper output)
{
    private const string Oracle = "tests/Tsuki.Tests.Data/Census/census_oracle.py";
    private static readonly TimeSpan OracleTimeout = TimeSpan.FromSeconds(120);

    private static readonly Lazy<(CensusReport Report, TimeSpan Elapsed)> Shared = new(Run, LazyThreadSafetyMode.ExecutionAndPublication);

    private static (CensusReport, TimeSpan) Run()
    {
        var watch = Stopwatch.StartNew();
        CensusReport report = ReferenceCensus.Run(SharedSource.Source);
        return (report, watch.Elapsed);
    }

    private static CensusReport Report => Shared.Value.Report;

    [GameDataFact]
    public void EveryReferenceOfEveryScriptEqualsTheOraclesLine()
    {
        IReadOnlyList<string> expected = OracleRun.Lines(Oracle, "refs", OracleTimeout);
        IReadOnlyList<CensusReference> actual = Report.References;
        output.WriteLine($"{actual.Count} references; the oracle printed {expected.Count}");
        var bad = new Mismatches();
        for (int i = 0; i < Math.Min(expected.Count, actual.Count); i++)
        {
            if (expected[i] != actual[i].Line)
            {
                bad.Add($"reference {i}: oracle \"{expected[i]}\", C# \"{actual[i].Line}\"");
            }
        }

        bad.Equal("reference count", expected.Count, actual.Count);
        bad.AssertNone("the census against census_oracle.py refs");
    }

    [GameDataFact]
    public void TheCountsAreTheDocumentedOnes()
    {
        CensusReport r = Report;
        foreach (CensusKindTotals t in r.Totals)
        {
            output.WriteLine($"{t.Kind,-6} references {t.References,7}  distinct {t.Distinct,6}  resolved {t.Resolved,7}  allowed {t.Allowed,3}  unexpected {t.Unexpected,3}");
        }

        Assert.Equal(549, r.Scripts);
        CensusKindTotals Of(ReferenceKind k) => r.Totals.Single(t => t.Kind == k);

        // pictures: 10,967 effective names, 10,962 resolve, 5 script bugs (FORMATS 5.2); 3 bodies missing on disk, one of them reached
        Assert.Equal(10_967, Of(ReferenceKind.Image).Distinct);
        Assert.Equal(59_730, Of(ReferenceKind.Image).References);
        Assert.Equal(6, Of(ReferenceKind.Image).Allowed);
        Assert.Equal(0, Of(ReferenceKind.Image).Unexpected);

        // audio: 16,705 voice calls, 14,543 _SEPL calls naming 1,314 sounds, tracks 2..123 (audio.md 4.2; FORMATS 5.2)
        Assert.Equal(16_705, Of(ReferenceKind.Voice).References);
        Assert.Equal(16_705, Of(ReferenceKind.Voice).Resolved);
        Assert.Equal(14_543, Of(ReferenceKind.Se).References);
        Assert.Equal(1_314, Of(ReferenceKind.Se).Distinct);
        Assert.Equal(14_543, Of(ReferenceKind.Se).Resolved);
        Assert.Equal(Of(ReferenceKind.Bgm).References, Of(ReferenceKind.Bgm).Resolved);

        // movies: 40 distinct ids all name existing files; scenes: 1,741 edges, 629 of them with a label
        Assert.Equal(Of(ReferenceKind.Movie).References, Of(ReferenceKind.Movie).Resolved);
        Assert.Equal(1_741, Of(ReferenceKind.Scene).References);
        Assert.Equal(1_741, Of(ReferenceKind.Scene).Resolved);
        Assert.Equal(629, r.References.Count(x => x.Kind == ReferenceKind.Label && x.Command == "JUMP"));
        Assert.Equal(Of(ReferenceKind.Label).References, Of(ReferenceKind.Label).Resolved);

        // text: 43,930 references, 43,902 ids, none out of range
        Assert.Equal(43_930, Of(ReferenceKind.Text).References);
        Assert.Equal(43_902, Of(ReferenceKind.Text).Distinct);
        Assert.Equal(43_930, Of(ReferenceKind.Text).Resolved);

        // motions: 24,349 in range, 92 zero, 1 too large (script.md); wipe masks: 947, all RULE_nn
        Assert.Equal(24_349, Of(ReferenceKind.Motion).Resolved);
        Assert.Equal(93, Of(ReferenceKind.Motion).Allowed);
        Assert.Equal(0, Of(ReferenceKind.Motion).Unexpected);
        Assert.Equal(947, Of(ReferenceKind.Rule).References);
        Assert.Equal(947, Of(ReferenceKind.Rule).Resolved);
    }

    [GameDataFact]
    public void OnlyTheAllowListIsLeftUnresolved()
    {
        CensusReport r = Report;
        Assert.True(r.IsClean, "unexpected failures:" + Environment.NewLine + string.Join(Environment.NewLine, r.Failures.Take(20).Select(f => $"{f.Check}: {f.Detail}")));

        var allowed = r.Findings.Where(f => f.Allowed).ToList();
        foreach (CensusFinding f in allowed.Where(f => f.Check != "motion"))
        {
            output.WriteLine($"{f.Check}: {f.Detail} -> {f.AllowedBecause}");
        }

        // five picture script bugs, each once
        var bugs = allowed.Where(f => f.Check == "image" && f.Detail.EndsWith("\t-;m=0", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, bugs.Count);
        Assert.Equal(
            [.. CensusAllowList.PictureScriptBugs.Keys.Order(StringComparer.Ordinal)],
            [.. bugs.Select(f => f.Detail.Split('\t')[4]).Order(StringComparer.Ordinal)]);

        // one reference reaches a missing sprite body
        CensusFinding body = Assert.Single(allowed, f => f.Check == "image" && f.Detail.EndsWith(";m=1", StringComparison.Ordinal));
        Assert.Contains("NOE_L_60_03_N01", body.Detail, StringComparison.Ordinal);

        // motions: id 0 in 92 statements and one id past the table
        Assert.Equal(92, allowed.Count(f => f.Check == "motion" && f.Detail.EndsWith("\tzero", StringComparison.Ordinal)));
        Assert.Equal(1, allowed.Count(f => f.Check == "motion" && f.Detail.EndsWith("\tout", StringComparison.Ordinal)));

        // four characters of the Chinese scripts have no glyph (fonts.md 5.4, 9): zc 3 + 2 + 1 uses, zt 1 use
        Assert.Equal(4, allowed.Count(f => f.Check == "glyph"));
        Assert.Equal(5 + 1 + 92 + 1 + 4, allowed.Count);
    }

    [GameDataFact]
    public void ThreeSpriteBodiesAreOnNoDiskAndNoOtherCatalogFileIsMissing()
    {
        // archives_ui_movies.md 3.3: 3 paired records point at bodies in no archive. Audit the whole catalog, not only what scripts reach.
        GameCatalog catalog = SharedSource.Source.Catalog;
        var missing = new List<string>();
        foreach (CatalogEntry e in catalog.Images.Entries)
        {
            foreach (string? file in new[] { e.Name0, e.Name1 })
            {
                if (file is not null && catalog.NameIndex.Find(file) is null)
                {
                    missing.Add(file.ToUpperInvariant());
                }
            }
        }

        output.WriteLine("catalog layer files in no archive: " + string.Join(", ", missing));
        Assert.Equal(
            [.. CensusAllowList.MissingSpriteBodies.Select(b => b + ".MZP").Order(StringComparer.Ordinal)],
            [.. missing.Distinct().Order(StringComparer.Ordinal)]);
        Assert.Equal(3, missing.Count);
    }

    [GameDataFact]
    public void EveryTargetReadsThroughTheSource()
    {
        CensusReport r = Report;
        foreach (CensusCheck c in r.Checks)
        {
            output.WriteLine($"{c.Name}: {c.Checked} checked, {c.Failed} failed");
        }

        Assert.All(r.Checks, c => Assert.Equal(0, c.Failed));
        CensusCheck Check(string prefix) => r.Checks.Single(c => c.Name.StartsWith(prefix, StringComparison.Ordinal));
        Assert.True(Check("picture files opened").Checked > 20_000, "the distinct picture files and their allpac2 copies");
        Assert.Equal(16_459, Check("voice entries").Checked);
        Assert.Equal(1_314, Check("vse entries").Checked);
        Assert.Equal(40, r.References.Where(x => x.Kind == ReferenceKind.Movie).Select(x => x.Key).Distinct().Count());
        Assert.Equal(4 * 40, Check("movie ids").Checked);
        Assert.Equal(4 * 43_902, Check("text lines").Checked);
        Assert.Equal(48, Check("pictures decoded").Checked);
        Assert.Equal(48, Check("audio entries decoded").Checked);
    }

    [GameDataFact]
    public void EveryCharacterOfEveryReferencedLineHasAGlyphOnARealPage()
    {
        CensusReport r = Report;
        Assert.Equal(4, r.Glyphs.Count);
        var bad = new Mismatches();
        var oracle = OracleRun.Records(Oracle, "glyphs", "glyphs", OracleTimeout).ToList();
        try
        {
            Assert.Equal(4, oracle.Count);
            foreach (GlyphCensusLanguage g in r.Glyphs)
            {
                JsonElement o = oracle.Single(d => d.RootElement.GetProperty("lang").GetString() == g.Language).RootElement;
                output.WriteLine(
                    $"{g.Language}: {g.Lines} lines, {g.DistinctCharacters} distinct characters, {g.Occurrences} occurrences, {g.Missing.Count} without a glyph, {g.PagesUsed} pages used, {g.OutOfPage} outside their page");
                bad.Equal($"{g.Language} lines", o.GetProperty("lines").GetInt32(), g.Lines);
                bad.Equal($"{g.Language} distinct characters", o.GetProperty("distinct").GetInt32(), g.DistinctCharacters);
                bad.Equal($"{g.Language} occurrences", o.GetProperty("occurrences").GetInt64(), g.Occurrences);
                bad.Equal(
                    $"{g.Language} characters without a glyph",
                    string.Join(' ', o.GetProperty("missing").EnumerateArray().Select(m => $"{m[0].GetString()}:{m[1].GetInt32().ToString(CultureInfo.InvariantCulture)}")),
                    string.Join(' ', g.Missing.Select(m => $"{m.Key}:{m.Count.ToString(CultureInfo.InvariantCulture)}")));
            }
        }
        finally
        {
            foreach (JsonDocument d in oracle)
            {
                d.Dispose();
            }
        }

        bad.AssertNone("the glyph census against census_oracle.py glyphs");
        Assert.Equal(["zc", "zt"], r.Glyphs.Where(g => g.Missing.Count > 0).Select(g => g.Language).ToArray());
        Assert.Equal(3, r.Glyphs.Single(g => g.Language == "zc").Missing.Count);
        Assert.Single(r.Glyphs.Single(g => g.Language == "zt").Missing);
        Assert.All(r.Glyphs, g => Assert.Equal(0, g.OutOfPage));
    }

    [GameDataFact]
    public void TheWholeCensusRunsInUnderAMinute()
    {
        TimeSpan elapsed = Shared.Value.Elapsed;
        output.WriteLine($"census: {Report.References.Count} references, {Report.Statements} statements, {elapsed.TotalSeconds:F1} s (run inside the test process)");
        Assert.True(elapsed < TimeSpan.FromSeconds(60), $"the census took {elapsed.TotalSeconds:F1} s; move it to the CLI command");
    }
}

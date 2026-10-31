using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Tsuki.Data.IO;
using Tsuki.Data.Scripts;
using Tsuki.Data.Text;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Data.Text;

/// <summary>
/// How the scripts reference the text lines (<c>tsuki.text_refs</c>, run by <c>text_oracle.py refs</c>): the text commands of all 549 scripts,
/// the reference index, the message groups, the census and the classes of the unreferenced ids; the figures of text.md 5.
/// </summary>
public sealed class TextReferencesTests
{
    private const string Oracle = "tests/Tsuki.Tests.Data/Text/text_oracle.py";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(8);

    private sealed record Loaded(List<ScriptSource> Scripts, List<TextCommand> Commands);

    private static readonly Lazy<Loaded> Shared = new(() =>
    {
        AllscrArchive allscr = AllscrArchive.Open(new DirectoryGameFiles(GameFolder.Require()));
        List<ScriptSource> scripts = TextReferences.LoadScripts(allscr);
        return new Loaded(scripts, TextReferences.IterTextCommands(scripts));
    });

    private static string Canon(TextCommand c) => string.Join(
        "|",
        c.Script.ToString(System.Globalization.CultureInfo.InvariantCulture),
        c.Name,
        c.Pos.ToString(System.Globalization.CultureInfo.InvariantCulture),
        c.OpName,
        c.Label,
        Hex(c.Arg),
        c.Choice?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
        string.Join(",", c.Extra.Select(Hex)),
        Hex(c.Message),
        c.EndCode,
        c.EndMode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
        c.Displayed ? "1" : "0",
        c.Locked ? "1" : "0",
        string.Join(",", c.Refs));

    private static string Hex(string s) => Convert.ToHexStringLower(Encoding.UTF8.GetBytes(s));

    private static List<(string Key, int Value)> Entries(JsonElement e) =>
        [.. e.GetProperty("entries").EnumerateArray().Select(r => (Encoding.UTF8.GetString(Convert.FromHexString(r[0].GetString()!)), r[1].GetInt32()))];

    [GameDataFact]
    public void TheTextCommandsOfAll549ScriptsEqualTheOracleCommandForCommand()
    {
        Loaded data = Shared.Value;
        using (JsonDocument head = OracleRun.One(Oracle, "refs", "scripts", Timeout))
        {
            Assert.Equal(549, head.RootElement.GetProperty("n").GetInt32());
            Assert.Equal(head.RootElement.GetProperty("commands").GetInt32(), data.Commands.Count);
            Assert.Equal(OracleCanon.H16(string.Join("|", data.Scripts.Select(s => s.Name))), head.RootElement.GetProperty("names_sha").GetString());
        }

        var mismatches = new Mismatches();
        int scripts = 0;
        foreach (JsonDocument doc in OracleRun.Records(Oracle, "refs", "cmds", Timeout))
        {
            using (doc)
            {
                int k = doc.RootElement.GetProperty("script").GetInt32();
                string oracle = doc.RootElement.GetProperty("h").GetString()!;
                TextCommand[] ours = [.. data.Commands.Where(c => c.Script == k)];
                mismatches.Equal($"script {k} command count", doc.RootElement.GetProperty("n").GetInt32(), ours.Length);
                for (int i = 0; i < Math.Min(ours.Length, oracle.Length / 16); i++)
                {
                    mismatches.Equal($"script {k} command {i}", oracle.Substring(16 * i, 16), OracleCanon.H16(Canon(ours[i])));
                }

                scripts++;
            }
        }

        mismatches.AssertNone("text commands");
        Assert.Equal(549, scripts);
        Assert.Equal(44_109, data.Commands.Count);      // text.md 5.2: "44,109 commands"
    }

    [GameDataFact]
    public void TheCommandCountsAndReferenceTotalsAreTheDocumentedOnes()
    {
        Loaded data = Shared.Value;
        Dictionary<string, int> ops = data.Commands.GroupBy(c => c.OpName).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(42_590, ops["ZM"]);
        Assert.Equal(678, ops["MSAD"]);
        Assert.Equal(824, ops["SELR"]);
        Assert.Equal(17, ops["ZD"]);
        SortedDictionary<int, List<TextCommand>> index = TextReferences.RefIndex(data.Commands);
        Assert.Equal(43_930, index.Values.Sum(v => v.Count));    // text.md 5.1: 43,930 references
        Assert.Equal(43_902, index.Count);                         // to 43,902 distinct ids
        Assert.Equal(43_958, index.Keys.Max());
        Assert.Equal(
            [7695, 7927, 8662, 8663, 8736, 8737, 8749, 8761, 8912, 9036, 9308, 10483, 10484],
            TextReferences.ZdOnlyIds(data.Commands).ToArray());
        Assert.Equal(6, data.Commands.Count(c => c.Locked));    // the six three-argument story choices
    }

    [GameDataFact]
    public void TheReferenceIndexMessageGroupsAndZdOnlyIdsEqualTheOracle()
    {
        Loaded data = Shared.Value;
        SortedDictionary<int, List<TextCommand>> index = TextReferences.RefIndex(data.Commands);
        using (JsonDocument doc = OracleRun.One(Oracle, "refs", "ref_index", Timeout))
        {
            Assert.Equal(doc.RootElement.GetProperty("n").GetInt32(), index.Count);
            Assert.Equal(doc.RootElement.GetProperty("total").GetInt32(), index.Values.Sum(v => v.Count));
            string rows = string.Join("\n", index.Select(kv => $"{kv.Key}=" + string.Join(",", kv.Value.Select(c => $"{c.Script}.{c.Pos}"))));
            Assert.Equal(doc.RootElement.GetProperty("h").GetString(), OracleCanon.H16(rows));
        }

        using (JsonDocument doc = OracleRun.One(Oracle, "refs", "groups", Timeout))
        {
            List<List<TextCommand>> groups = TextReferences.MessageGroups(data.Commands);
            Assert.Equal(doc.RootElement.GetProperty("n").GetInt32(), groups.Count);
            string rows = string.Join("\n", groups.Select(g => string.Join(",", g.Select(c => $"{c.Script}.{c.Pos}"))));
            Assert.Equal(doc.RootElement.GetProperty("h").GetString(), OracleCanon.H16(rows));
        }

        using (JsonDocument doc = OracleRun.One(Oracle, "refs", "zd_only", Timeout))
        {
            Assert.Equal(doc.RootElement.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()).ToArray(), TextReferences.ZdOnlyIds(data.Commands).ToArray());
        }
    }

    [GameDataFact]
    public void TheScriptCensusAndTheVoiceCountsEqualTheOracle()
    {
        Loaded data = Shared.Value;
        (SortedDictionary<string, int> ops, SortedDictionary<string, int> shapes, SortedDictionary<string, int> codes) = TextReferences.ScriptCensus(data.Commands);
        foreach ((string kind, SortedDictionary<string, int> ours) in new[] { ("ops", ops), ("shapes", shapes), ("codes", codes) })
        {
            using JsonDocument doc = OracleRun.One(Oracle, "refs", "census_" + kind, Timeout);
            List<(string Key, int Value)> theirs = Entries(doc.RootElement);
            Assert.Equal(theirs.Count, ours.Count);
            foreach ((string key, int value) in theirs)
            {
                Assert.True(ours.TryGetValue(key, out int n), $"{kind}: key {key} missing");
                Assert.Equal(value, n);
            }
        }

        Assert.Equal(42_590, ops["ZM"]);
        // text.md 5.2: ZM($N) 40,734 and MSAD($N) 532, ZM($N^$N) 510
        Assert.Equal(40_734, shapes["ZM($N)"]);
        Assert.Equal(532, shapes["MSAD($N)"]);
        Assert.Equal(510, shapes["ZM($N^$N)"]);

        (SortedDictionary<int, int> vply, SortedDictionary<int, int> vpl2) = TextReferences.VoiceTextCounts(data.Scripts);
        using JsonDocument voice = OracleRun.One(Oracle, "refs", "voice", Timeout);
        Assert.Equal(voice.RootElement.GetProperty("vply").EnumerateArray().Select(r => (r[0].GetInt32(), r[1].GetInt32())).ToArray(), vply.Select(kv => (kv.Key, kv.Value)).ToArray());
        Assert.Equal(voice.RootElement.GetProperty("vpl2").EnumerateArray().Select(r => (r[0].GetInt32(), r[1].GetInt32())).ToArray(), vpl2.Select(kv => (kv.Key, kv.Value)).ToArray());
    }

    [GameDataFact]
    public void The67UnreferencedIdsAreClassifiedLikeTheOracleAndTheDocument()
    {
        Loaded data = Shared.Value;
        SortedDictionary<int, string> ours = TextReferences.ClassifyUnreferenced(SharedText.Value, data.Commands);
        using JsonDocument doc = OracleRun.One(Oracle, "refs", "unref", Timeout);
        var theirs = doc.RootElement.GetProperty("entries").EnumerateArray().Select(r => (r[0].GetInt32(), r[1].GetString()!)).ToArray();
        Assert.Equal(theirs, ours.Select(kv => (kv.Key, kv.Value)).ToArray());
        Assert.Equal(67, ours.Count);
        Dictionary<string, int> classes = ours.Values.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(10, classes["choice-number"]);
        Assert.Equal(11, classes["image-text"]);
        Assert.Equal(22, classes["merged-fragment"]);
        Assert.Equal(4, classes["blank"]);
        Assert.Equal(20, classes["not-shown"]);
    }

    [GameDataFact]
    public void EverySceneTranscriptInFourLanguagesEqualsTheOracle()
    {
        Loaded data = Shared.Value;
        ScriptText text = SharedText.Value;
        var mismatches = new Mismatches();
        int languages = 0;
        foreach (JsonDocument doc in OracleRun.Records(Oracle, "refs", "transcripts", Timeout))
        {
            using (doc)
            {
                Language lang = TextRules.Parse(doc.RootElement.GetProperty("lang").GetString()!);
                string oracle = doc.RootElement.GetProperty("h").GetString()!;
                Assert.Equal(549 * 16, oracle.Length);
                string[] ours = new string[549];
                Parallel.For(0, 549, k =>
                    ours[k] = OracleCanon.H16(string.Join("\n", TextReferences.SceneTranscript(text, data.Scripts, data.Scripts[k].Name, lang))));
                for (int k = 0; k < 549; k++)
                {
                    mismatches.Equal($"{lang} transcript of {data.Scripts[k].Name}", oracle.Substring(16 * k, 16), ours[k]);
                }

                languages++;
            }
        }

        mismatches.AssertNone("scene transcripts");
        Assert.Equal(4, languages);
    }

    [GameDataFact]
    public void ChoiceSlotsEqualTheOracleAndTheSixLockedChoicesAreStoryChoices()
    {
        Loaded data = Shared.Value;
        using JsonDocument doc = OracleRun.One(Oracle, "refs", "selr", Timeout);
        TextCommand[] selr = [.. data.Commands.Where(c => c.Op == TextCommandOp.Selr)];
        Assert.Equal(doc.RootElement.GetProperty("n").GetInt32(), selr.Length);
        Assert.Equal(doc.RootElement.GetProperty("locked").GetInt32(), selr.Count(c => c.Locked));
        foreach (JsonElement row in doc.RootElement.GetProperty("slots").EnumerateArray())
        {
            TextCommand c = selr.First(x => x.Script == row[0].GetInt32() && x.Pos == row[1].GetInt32());
            int slot = Enumerable.Range(0, 30).First(s => Convert.ToHexStringLower(c.ChoiceSlot(s)) == row[2].GetString());
            Assert.True(slot >= 0);
        }
    }

    // -- no game data ---------------------------------------------------------------------------------------------------

    private static ScriptSource S(string name, params string[] statements) => new(name, string.Join(";", statements));

    [Fact]
    public void TheFourCommandsAreRecognisedAndEverythingElseIsSkipped()
    {
        ScriptSource script = S("t", "_WKST(F823,eq,0)", "_ZM00a01($000003)", " _ZD00a02($000004@e)\r\n", "_MSAD(abc@nrest)", "_SELR(1,$000005,1)", "_ZM00a01(x", "_ZMxyz12(x)", "_SELX(1)", "_VPLY(1)");
        List<TextCommand> cmds = TextReferences.IterTextCommands([script]);
        Assert.Equal(["ZM", "ZD", "MSAD", "SELR"], cmds.Select(c => c.OpName).ToArray());
        Assert.Equal([1, 2, 3, 4], cmds.Select(c => c.Pos).ToArray());
        Assert.Equal("00a01", cmds[0].Label);
        Assert.Equal([3], cmds[0].Refs);
        Assert.False(cmds[1].Displayed);
        Assert.Equal("e", cmds[1].EndCode);
        Assert.Equal(2, cmds[1].EndMode);
        Assert.Equal("$000004", cmds[1].Message);
        Assert.Equal("abc", cmds[2].Message);
        Assert.Equal(1, cmds[2].EndMode);
        Assert.Equal(1, cmds[3].Choice);
        Assert.Equal("$000005", cmds[3].Arg);
        Assert.True(cmds[3].Locked);
        Assert.Null(cmds[3].EndMode);
        Assert.Equal(string.Empty, cmds[3].EndCode);
    }

    [Fact]
    public void AMessageIsCutAtTheFirstEndCodeAndAt0x300Bytes()
    {
        TextCommand cut = Assert.Single(TextReferences.IterTextCommands([S("t", "_ZM00a01(" + new string('a', 10) + "@n" + new string('b', 10) + ")")]));
        Assert.Equal(new string('a', 10), cut.Message);
        string longText = new string('a', 0x2FE) + "あ";            // 0x2FE + 3 bytes: the character does not fit
        TextCommand shortened = Assert.Single(TextReferences.IterTextCommands([S("t", "_ZM00a01(" + longText + ")")]));
        Assert.Equal(new string('a', 0x2FE), shortened.Message);
        Assert.Equal(0x300 - 2, Encoding.UTF8.GetByteCount(shortened.Message));
        TextCommand fits = Assert.Single(TextReferences.IterTextCommands([S("t", "_ZM00a01(" + new string('a', 0x2FD) + "あ)")]));
        Assert.Equal(0x2FD + 3, Encoding.UTF8.GetByteCount(fits.Message));
        // _SELR keeps its argument whole: @e has no meaning there
        TextCommand selr = Assert.Single(TextReferences.IterTextCommands([S("t", "_SELR(2,x@ey)")]));
        Assert.Equal("x@ey", selr.Message);
    }

    [Fact]
    public void ASelrArgumentSkipsAKeywordPrefixAndReadsItsChoiceNumber()
    {
        TextCommand c = Assert.Single(TextReferences.IterTextCommands([S("t", "_SELR( -3 ,`001:$000007,1,x)")]));
        Assert.Equal(-3, c.Choice);
        Assert.Equal("$000007", c.Arg);
        Assert.Equal(["1", "x"], c.Extra.ToArray());
        Assert.Equal([7], c.Refs);
        Assert.Equal(Encoding.UTF8.GetBytes("$043960$000007"), c.ChoiceSlot(0));
        Assert.Equal(Encoding.UTF8.GetBytes("$043959$000007"), c.ChoiceSlot(9));
        TextCommand noNumber = Assert.Single(TextReferences.IterTextCommands([S("t", "_SELR(x,abc)")]));
        Assert.Null(noNumber.Choice);
        Assert.Throws<InvalidOperationException>(() => TextReferences.IterTextCommands([S("t", "_ZM00a01(x)")])[0].ChoiceSlot(0));
    }

    [Fact]
    public void MessageGroupsAppendMsadToTheirZmAndSkipDisabledMessages()
    {
        List<TextCommand> cmds = TextReferences.IterTextCommands(
        [
            S("a", "_ZM00a01($000001@n)", "_MSAD($000002)", "_SELR(0,$000003)", "_MSAD($000004)", "_ZM00a02($000005)", "_ZD00a03($000006)", "_MSAD($000007)"),
            S("b", "_MSAD($000008)", "_ZM00b01($000009)"),
        ]);
        List<List<TextCommand>> groups = TextReferences.MessageGroups(cmds);
        Assert.Equal([[1, 2, 4], [5], [7], [8], [9]], groups.Select(g => g.SelectMany(c => c.Refs).ToArray()).ToArray());
        Assert.Equal([6], TextReferences.ZdOnlyIds(cmds).ToArray());
        SortedDictionary<int, List<TextCommand>> index = TextReferences.RefIndex(cmds);
        Assert.Equal(9, index.Count);
    }

    [Fact]
    public void PythonWhitespaceIsNotDotNetWhitespace()
    {
        Assert.Equal("a", TextReferences.PyStrip("\u001Ca\u001F"));
        Assert.Equal("a", TextReferences.PyStrip("　a "));
        Assert.Equal("​a", TextReferences.PyStrip("​a"));      // zero width space is not whitespace to Python either
        Assert.Equal(string.Empty, TextReferences.PyStrip(" \t\r\n"));
        Assert.True(TextReferences.IsPySpace('\u0085'));
        Assert.False(char.IsWhiteSpace('\u001C'));                         // the difference that matters
        Assert.True(TextReferences.IsPySpace('\u001C'));
    }

    [Fact]
    public void ReferencesNeedSixDecimalDigitsAndAreCountedWithoutOverlap()
    {
        Assert.Equal([123, 456], TextReferences.ScanRefs("$000123 and $000456"));
        Assert.Empty(TextReferences.ScanRefs("$12345"));
        Assert.Equal([123456], TextReferences.ScanRefs("$1234567"));
        Assert.Equal("a $N b $N", TextReferences.ReplaceRefs("a $000001 b $999999"));
        Assert.Equal("$12 $N5", TextReferences.ReplaceRefs("$12 $0000015"));
    }

    [Fact]
    public void Utf8TruncationKeepsWholeCharactersOnly()
    {
        Assert.Equal("ab", TextReferences.TruncateUtf8("ab", 5));
        Assert.Equal("あ", TextReferences.TruncateUtf8("あい", 5));
        Assert.Equal("あい", TextReferences.TruncateUtf8("あい", 6));
        Assert.Equal(string.Empty, TextReferences.TruncateUtf8("あ", 2));
        Assert.Equal("😀", TextReferences.TruncateUtf8("😀a", 4));
        Assert.Equal(string.Empty, TextReferences.TruncateUtf8("😀a", 3));
    }

    [Fact]
    public void TheUnreferencedClassesFollowTheDocumentedOrder()
    {
        Assert.Equal(["choice-number", "image-text", "blank", "merged-fragment", "not-shown"], TextReferences.UnreferencedClasses);
        Assert.Equal(11, TextReferences.ImageTextIds.Count);
        Assert.Equal("IMG2629", TextReferences.ImageTextIds[12]);
        Assert.Equal("IMG2461", TextReferences.ImageTextIds[93]);
        ImmutableArray<int> expectedChoiceIds = [43959, 43960, 43968];
        Assert.All(expectedChoiceIds, id => Assert.True(id >= TextRules.ChoiceNumberOverflow));
    }
}

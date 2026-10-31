using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Tsuki.Data.Text;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Data.Text;

/// <summary>
/// The <c>$NNNNNN</c> expansion and the markup tokenizer: hand-checked cases without game data, then the seeded random cases of
/// <c>text_oracle.py fuzz</c> (6,000 expansions, 9,000 token streams with their runs and plain text).
/// </summary>
public sealed class MarkupAndExpansionTests
{
    private const string Oracle = "tests/Tsuki.Tests.Data/Text/text_oracle.py";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(8);

    private static TextTable Lines(params string[] lines)
    {
        var offsets = new List<uint>();
        var blob = new List<byte>();
        foreach (string l in lines)
        {
            offsets.Add((uint)blob.Count);
            blob.AddRange(Encoding.UTF8.GetBytes(l + "\r\n"));
        }

        offsets.Add((uint)blob.Count);
        offsets.Add((uint)blob.Count);
        return new TextTable(Language.En, [.. offsets], blob.ToArray());
    }

    private static string Expand(TextTable t, string message, int? srcLen = null) =>
        Encoding.UTF8.GetString(TextExpansion.Expand(Encoding.UTF8.GetBytes(message), t, srcLen));

    // -- expansion, no game data -----------------------------------------------------------------------------------------

    [Fact]
    public void AReferenceIsReplacedByTheLineAndOtherBytesAreCopied()
    {
        TextTable t = Lines("zero", "one", "two");
        Assert.Equal("a zero b", Expand(t, "a $000000 b"));
        Assert.Equal("onetwo", Expand(t, "$000001$000002"));
        Assert.Equal("plain", Expand(t, "plain"));
        Assert.Equal(string.Empty, Expand(t, string.Empty));
    }

    [Fact]
    public void TheSixBytesAfterTheDollarAreNotCheckedAsDigits()
    {
        TextTable t = Lines("zero", "one", "two");
        // '1' = 0x31 gives digit value 1 at each position: "00000" + (0x31 - 0x30) = id 1 for "00000" + "1"
        Assert.Equal("one", Expand(t, "$000001"));
        // ':' (0x3A) is digit 10 in the last position: id 10 is beyond the table -> the engine would read garbage, the port refuses
        Assert.Throws<ArgumentOutOfRangeException>(() => Expand(t, "$00000:"));
        // '/' (0x2F) is -1 in the last position: the id is negative, nothing is inserted but the 7 bytes are consumed
        Assert.Equal("zero!", Expand(t, "$000000!"));
        Assert.Equal("!", Expand(t, "$00000/!"));
    }

    [Fact]
    public void TheGuardEntriesGiveNothingAndTheIdAfterThemIsRefused()
    {
        TextTable t = Lines("zero", "one");
        Assert.Equal("[]", Expand(t, "[$000002]"));   // id == count: blob_size, blob_size
        Assert.Equal("[]", Expand(t, "[$000003]"));   // id == count + 1: the next entry is the end mark
        Assert.Throws<ArgumentOutOfRangeException>(() => Expand(t, "[$000004]"));
    }

    [Fact]
    public void TheOutputStopsAtTheCapAndTheSourceAtItsLength()
    {
        TextTable t = Lines(new string('x', 400), new string('y', 400));
        string both = Expand(t, "$000000$000001");
        Assert.Equal(TextRules.EngineExpandMax, both.Length);
        Assert.Equal(new string('x', 400) + new string('y', 364), both);
        Assert.Equal("ab", Expand(t, "abcdef", srcLen: 2));
        Assert.Equal(string.Empty, Expand(t, "abcdef", srcLen: 0));
        Assert.Equal(string.Empty, Expand(t, "abcdef", srcLen: -3));
        // a source length beyond the bytes reads NUL, which stops the loop
        Assert.Equal("abc", Expand(t, "abc", srcLen: 50));
    }

    [Fact]
    public void ModeZeroCopiesTheSourceUpToTheFirstNul()
    {
        TextTable t = Lines("zero");
        Assert.Equal("$000000", Encoding.UTF8.GetString(TextExpansion.Expand("$000000"u8, t, expand: false)));
        Assert.Equal("ab", Encoding.UTF8.GetString(TextExpansion.Expand("ab\0cd"u8, t, expand: false)));
        Assert.Equal("a", Encoding.UTF8.GetString(TextExpansion.Expand("abc"u8, t, srcLen: 1, expand: false)));
        Assert.Empty(TextExpansion.Expand("abc"u8, t, srcLen: -4, expand: false));
        Assert.Equal("abc", Encoding.UTF8.GetString(TextExpansion.Expand("abc"u8, t, srcLen: 99, expand: false)));
    }

    // -- tokenizer, no game data -----------------------------------------------------------------------------------------

    private static string Canon(string text, Language lang = Language.Ja, bool script = false) =>
        OracleCanon.Tokens(Markup.Tokenize(text, lang, script));

    private static string T(string s) => "T:" + OracleCanon.Hex(Encoding.UTF8.GetBytes(s)) + ":" + OracleCanon.Hex(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void PlainTextIsOneToken()
    {
        Assert.Equal(T("こんにちは"), Canon("こんにちは"));
        Assert.Equal(string.Empty, Canon(string.Empty));
    }

    [Fact]
    public void RubyIsParsedUnlessTheLanguageIsEnglish()
    {
        ImmutableArray<MarkupToken> ja = Markup.Tokenize("a<漢字|かんじ>b", Language.Ja);
        Assert.Equal(3, ja.Length);
        RubyToken ruby = Assert.IsType<RubyToken>(ja[1]);
        Assert.Equal("漢字"u8.ToArray(), ruby.Base.ToArray());
        Assert.Equal("かんじ"u8.ToArray(), ruby.Reading.ToArray());
        Assert.Equal("<漢字|かんじ>"u8.ToArray(), ruby.Raw.ToArray());
        Assert.True(ruby.Closed);

        ImmutableArray<MarkupToken> en = Markup.Tokenize("a<漢字|かんじ>b", Language.En);
        TextToken only = Assert.IsType<TextToken>(Assert.Single(en));
        Assert.Equal("a<漢字|かんじ>b", only.TextString);

        RubyToken open = Assert.IsType<RubyToken>(Assert.Single(Markup.Tokenize("<ab|cd", Language.Ja)));
        Assert.False(open.Closed);
        Assert.Equal("ab"u8.ToArray(), open.Base.ToArray());
        Assert.Equal("cd"u8.ToArray(), open.Reading.ToArray());
    }

    [Fact]
    public void ControlCodesTakeTheirParametersByTheEnginesByteRules()
    {
        ImmutableArray<MarkupToken> t = Markup.Tokenize("@c1あ@s128,い@x12う@y@r^[heart]", Language.Ja);
        CodeToken c = Assert.IsType<CodeToken>(t[0]);
        Assert.Equal("c", c.Letter);
        Assert.Equal("1"u8.ToArray(), c.Arg!.Value.ToArray());
        Assert.Equal("@c1"u8.ToArray(), c.Raw.ToArray());
        CodeToken s = Assert.IsType<CodeToken>(t[2]);
        Assert.Equal("128"u8.ToArray(), s.Arg!.Value.ToArray());
        Assert.Equal("@s128,"u8.ToArray(), s.Raw.ToArray());          // the comma is consumed
        CodeToken x = Assert.IsType<CodeToken>(t[4]);
        Assert.Equal("12"u8.ToArray(), x.Arg!.Value.ToArray());
        CodeToken y = Assert.IsType<CodeToken>(t[6]);
        Assert.Null(y.Arg);                                              // a bare @y has no digits
        Assert.IsType<CodeToken>(t[7]);
        Assert.IsType<NewlineToken>(t[8]);
        IconToken icon = Assert.IsType<IconToken>(t[9]);
        Assert.Equal(("heart", 0), (icon.Name, icon.Index));
    }

    [Fact]
    public void EscapesTurnTheNextByteIntoAGlyph()
    {
        ImmutableArray<MarkupToken> t = Markup.Tokenize("@1^@2)", Language.Ja);
        Assert.Equal(4, t.Length);
        TextToken caret = Assert.IsType<TextToken>(t[1]);
        Assert.Equal("^"u8.ToArray(), caret.Text.ToArray());
        TextToken paren = Assert.IsType<TextToken>(t[3]);
        Assert.Equal(")"u8.ToArray(), paren.Text.ToArray());
    }

    [Fact]
    public void ScriptModeFindsReferencesAndStopsAtTheEndCodes()
    {
        ImmutableArray<MarkupToken> t = Markup.Tokenize("$000123@k@eignored", Language.Ja, script: true);
        Assert.Equal(123, Assert.IsType<RefToken>(t[0]).LineId);
        Assert.Equal("k", Assert.IsType<CodeToken>(t[1]).Letter);
        Assert.Equal("e", Assert.IsType<CodeToken>(t[2]).Letter);
        Assert.Equal("ignored", Assert.IsType<TextToken>(t[3]).TextString);
        // outside script mode the same bytes are plain text
        Assert.Equal(T("$000123"), Canon("$000123"));
        Assert.Single(Markup.Tokenize("$12", Language.Ja, script: true));   // too short: stays text
    }

    [Fact]
    public void ALoneAtSignAtTheEndIsAnEmptyCodeAndRunsTreatItLikeAPosition()
    {
        ImmutableArray<MarkupToken> t = Markup.Tokenize("a@", Language.Ja);
        CodeToken lone = Assert.IsType<CodeToken>(t[1]);
        Assert.Empty(lone.Code);
        List<MarkupRun> runs = Markup.ToRuns(t, Language.Ja);
        Assert.Equal(["text", "pos"], runs.Select(r => r.Kind).ToArray());
    }

    [Fact]
    public void RunsApplyTheStyleCodesAndTheSequenceCounter()
    {
        List<MarkupRun> runs = Markup.ToRuns(Markup.Tokenize("あ@c2い@gう@ra@t@uえ@k", Language.Ja), Language.Ja);
        Assert.Equal(0x345F, runs[1].Style.Color);
        Assert.Equal(1, runs[0].Seq);
        Assert.Equal(2, runs[1].Seq);
        Assert.Equal(1, runs[2].Style.Style);
        Assert.Equal("wait", runs[^1].Kind);
        Assert.Equal(["text", "text", "text", "text", "seq", "seq", "text", "wait"], runs.Select(r => r.Kind).ToArray());
    }

    [Fact]
    public void PlainTextRendersRubyAndIconsAsAsked()
    {
        byte[] line = Encoding.UTF8.GetBytes("a<漢|かん>[heart]^b");
        Assert.Equal("a漢[heart]\nb", Encoding.UTF8.GetString(Markup.PlainText(line, Language.Ja)));
        Assert.Equal("a漢(かん)[heart]\nb", Encoding.UTF8.GetString(Markup.PlainText(line, Language.Ja, RubyRendering.Paren)));
        Assert.Equal("a<漢|かん>[heart]\nb", Encoding.UTF8.GetString(Markup.PlainText(line, Language.Ja, RubyRendering.Markup)));
        Assert.Equal("a漢\nb", Encoding.UTF8.GetString(Markup.PlainText(line, Language.Ja, icons: false)));
    }

    [Fact]
    public void CodePointsAreCountedLikePythonWithSurrogateEscape()
    {
        Assert.Equal(3, Markup.CountCodePoints("あいa"u8));
        Assert.Equal(2, Markup.CountCodePoints([0xE3, 0x81]));            // a cut character: each byte is escaped
        Assert.Equal(1, Markup.CountCodePoints("😀"u8));
        Assert.Equal(2, Markup.CountCodePoints([0x80, (byte)'a']));
    }

    // -- the oracle's seeded cases ---------------------------------------------------------------------------------------

    [GameDataFact]
    public void SixThousandRandomExpansionsEqualTheOracleIncludingTheRefusedIds()
    {
        int n = 0;
        int refused = 0;
        var mismatches = new Mismatches();
        foreach (string line in OracleRun.Lines(Oracle, "fuzz", Timeout))
        {
            if (!line.StartsWith("{\"k\":\"x\"", StringComparison.Ordinal))
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement e = doc.RootElement;
            Language lang = TextRules.Parse(e.GetProperty("lang").GetString()!);
            byte[] src = Convert.FromHexString(e.GetProperty("src").GetString()!);
            int? srcLen = e.GetProperty("srclen").ValueKind == JsonValueKind.Null ? null : e.GetProperty("srclen").GetInt32();
            bool expand = e.GetProperty("expand").GetBoolean();
            string? oracleOut = e.TryGetProperty("out", out JsonElement o) ? o.GetString() : null;
            string? ours;
            try
            {
                ours = Convert.ToHexStringLower(TextExpansion.Expand(src, SharedText.Value.Table(lang), srcLen, expand));
            }
            catch (ArgumentOutOfRangeException)
            {
                ours = null;
                refused++;
            }

            mismatches.Equal($"case {n} ({lang}, src {e.GetProperty("src").GetString()})", oracleOut, ours);
            n++;
        }

        mismatches.AssertNone("random expansions");
        Assert.Equal(6000, n);
        Assert.True(refused > 100, $"only {refused} cases hit an id beyond the table");
    }

    [GameDataFact]
    public void NineThousandRandomTokenStreamsRunsAndPlainTextsEqualTheOracle()
    {
        int n = 0;
        int errors = 0;
        var mismatches = new Mismatches();
        foreach (string line in OracleRun.Lines(Oracle, "fuzz", Timeout))
        {
            if (!line.StartsWith("{\"k\":\"t\"", StringComparison.Ordinal))
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement e = doc.RootElement;
            Language lang = TextRules.Parse(e.GetProperty("lang").GetString()!);
            bool script = e.GetProperty("script").GetBoolean();
            byte[] src = Convert.FromHexString(e.GetProperty("src").GetString()!);
            string label = $"case {n} ({lang}, script {script}, src {e.GetProperty("src").GetString()})";
            if (e.TryGetProperty("err", out JsonElement err))
            {
                errors++;
                mismatches.Add($"{label}: the oracle raised {err.GetString()}");
                n++;
                continue;
            }

            ImmutableArray<MarkupToken> tokens = Markup.Tokenize(src, lang, script);
            mismatches.Equal(label + " tokens", e.GetProperty("tok").GetString(), OracleCanon.Tokens(tokens));
            mismatches.Equal(label + " runs", e.GetProperty("runs").GetString(), OracleCanon.Runs(Markup.ToRuns(tokens, lang)));
            mismatches.Equal(label + " plain", e.GetProperty("plain").GetString(), OracleCanon.Plain(tokens));
            n++;
        }

        mismatches.AssertNone("random token streams");
        Assert.Equal(9000, n);
        Assert.Equal(0, errors);
    }

    [GameDataFact]
    public void TheSeededRunEndsWithItsCounts()
    {
        using JsonDocument end = OracleRun.One(Oracle, "fuzz", "end", Timeout);
        Assert.Equal(6000, end.RootElement.GetProperty("expand").GetInt32());
        Assert.Equal(9000, end.RootElement.GetProperty("tok").GetInt32());
    }
}

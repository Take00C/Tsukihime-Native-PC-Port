using System.Globalization;
using System.Text;
using System.Text.Json;
using Tsuki.Engine.Vm;

namespace Tsuki.Tests.Data.Vm;

/// <summary>
/// The raw readers and the segmenter on random inputs against the oracle's answers (<c>samples/golden/script/fuzz/fuzz.json</c>,
/// written by <c>python -m oracle.script</c>; the inputs are synthetic, not game data, and come from a fixed seed): the inline
/// readers, the expression evaluator, the strtol getters, the object-parser split and whole synthetic scripts including
/// segments around the 0x300-byte limit.
/// </summary>
public sealed class ReaderFuzzTests
{
    private static JsonDocument Fuzz() => ScriptFixtures.ReadJson(Path.Combine(ScriptFixtures.ScriptArea, "fuzz", "fuzz.json"));

    [GameDataFact]
    public void InlineReadersEqualTheOracleOnRandomBytes()
    {
        using JsonDocument doc = Fuzz();
        JsonElement cases = doc.RootElement.GetProperty("inline");
        Assert.True(cases.GetArrayLength() >= 10_000);
        var m = new Mismatches();
        foreach (JsonElement c in cases.EnumerateArray())
        {
            byte[] buf = ScriptFixtures.FromHex(c[0].GetString()!);
            int p0 = c[1].GetInt32();
            string want = c[2].GetString()!;
            int n = want.Split(' ').Length;
            var got = new StringBuilder();
            for (int k = 0; k < n; k++)
            {
                got.Append(k > 0 ? " " : string.Empty).Append(InlineReader.ReadInt(buf, p0, k, 16)).Append('/').Append(InlineReader.ReadInt(buf, p0, k, 32))
                    .Append('/').Append(InlineReader.IsPresent(buf, p0, k) ? '1' : '0');
            }

            if (want != got.ToString())
            {
                m.Add($"inline buf={c[0].GetString()} p0={p0}: oracle {want}, C# {got}");
            }
        }

        m.AssertNone($"inline readers ({cases.GetArrayLength()} cases)");
    }

    [GameDataFact]
    public void ExpressionsEqualTheOracle()
    {
        using JsonDocument doc = Fuzz();
        JsonElement cases = doc.RootElement.GetProperty("expr");
        Assert.True(cases.GetArrayLength() >= 15_000);
        var m = new Mismatches();
        int undefined = 0;
        foreach (JsonElement c in cases.EnumerateArray())
        {
            byte[] arg = ScriptFixtures.FromHex(c[0].GetString()!);
            string want = c[1].GetString()!;
            string got = ObjectExpression.TryEvaluate(arg, out int v, out ExpressionError e)
                ? v.ToString(CultureInfo.InvariantCulture)
                : "u" + CanonicalRecords.Code(e);
            undefined += got[0] == 'u' ? 1 : 0;
            if (want != got)
            {
                m.Add($"expr '{Encoding.Latin1.GetString(arg)}' ({c[0].GetString()}): oracle {want}, C# {got}");
            }
        }

        m.AssertNone($"expressions ({cases.GetArrayLength()} cases)");
        Assert.True(undefined > 1000, "the cases must include undefined results");
    }

    [GameDataFact]
    public void StrtolGettersEqualTheOracle()
    {
        using JsonDocument doc = Fuzz();
        JsonElement cases = doc.RootElement.GetProperty("int");
        var m = new Mismatches();
        foreach (JsonElement c in cases.EnumerateArray())
        {
            byte[] arg = ScriptFixtures.FromHex(c[0].GetString()!);
            string strict = ObjectParser.StrictInt(arg) is { } s ? s.ToString(CultureInfo.InvariantCulture) : "n";
            string partial = ObjectParser.PartialInt(arg) is { } p ? p.ToString(CultureInfo.InvariantCulture) : "n";
            if (strict != c[1].GetString() || partial != c[2].GetString())
            {
                m.Add($"int {c[0].GetString()}: oracle {c[1].GetString()}/{c[2].GetString()}, C# {strict}/{partial}");
            }
        }

        m.AssertNone($"strtol getters ({cases.GetArrayLength()} cases)");
    }

    [GameDataFact]
    public void ObjectSplitEqualsTheOracle()
    {
        using JsonDocument doc = Fuzz();
        JsonElement cases = doc.RootElement.GetProperty("split");
        var m = new Mismatches();
        var ranges = new ArgRange[ObjectParser.MaxArguments];
        int errors = 0;
        foreach (JsonElement c in cases.EnumerateArray())
        {
            byte[] buf = ScriptFixtures.FromHex(c[0].GetString()!);
            int sep = c[1].GetInt32();
            SplitStatus status = ObjectParser.Split(buf, sep, ranges, out int count);
            var got = new StringBuilder(status switch
            {
                SplitStatus.Ok => "ok",
                SplitStatus.UnterminatedQuote => "quote",
                _ => "noterm",
            });
            got.Append(' ').Append(status == SplitStatus.Ok ? count : 0);
            if (status == SplitStatus.Ok)
            {
                for (int i = 0; i < count; i++)
                {
                    got.Append(' ').Append(ranges[i].Start).Append('-').Append(ranges[i].End);
                }
            }
            else
            {
                errors++;
            }

            if (got.ToString() != c[2].GetString())
            {
                m.Add($"split buf={c[0].GetString()} sep={sep}: oracle {c[2].GetString()}, C# {got}");
            }
        }

        m.AssertNone($"object split ({cases.GetArrayLength()} cases)");
        Assert.True(errors > 500, "the cases must include unsplittable lists");
    }

    [GameDataFact]
    public void SyntheticScriptsEqualTheOracleRecordByRecord()
    {
        using JsonDocument doc = Fuzz();
        JsonElement cases = doc.RootElement.GetProperty("scripts");
        var m = new Mismatches();
        int raising = 0;
        int index = 0;
        foreach (JsonElement c in cases.EnumerateArray())
        {
            byte[] script = ScriptFixtures.FromHex(c[0].GetString()!);
            CanonicalRecords.Records r = CanonicalRecords.Build(script, out int truncated);
            if (c[1].ValueKind == JsonValueKind.Null)
            {
                // tsuki.script.parse_script raises ScriptError for a segment of 0x300 bytes without ';' before the script end
                raising++;
                if (truncated == 0)
                {
                    m.Add($"script case {index}: the oracle raises, C# reports no truncated segment ({c[0].GetString()})");
                }
            }
            else
            {
                if (truncated != 0)
                {
                    m.Add($"script case {index}: C# reports a truncated segment, the oracle does not ({c[0].GetString()})");
                }

                string wantSeg = string.Concat(c[1].EnumerateArray().Select(e => e.GetString() + "\n"));
                string wantArg = string.Concat(c[2].EnumerateArray().Select(e => e.GetString() + "\n"));
                if (wantSeg != r.Segments.ToString())
                {
                    m.Add($"script case {index}: segment records differ ({c[0].GetString()})\noracle:\n{wantSeg}C#:\n{r.Segments}");
                }

                if (wantArg != r.Arguments.ToString())
                {
                    m.Add($"script case {index}: argument records differ ({c[0].GetString()})\noracle:\n{wantArg}C#:\n{r.Arguments}");
                }
            }

            index++;
        }

        m.AssertNone($"synthetic scripts ({cases.GetArrayLength()} cases)");
        Assert.True(raising >= 20, "the cases must include overlong segments");
    }
}

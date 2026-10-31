using System.Globalization;
using System.Text;
using Tsuki.Engine.Vm;

namespace Tsuki.Tests.Synthetic.Vm;

/// <summary>
/// The C# readers and the segmenter against the answers of the Python oracle (<c>tsuki.script</c>) on random synthetic inputs,
/// embedded in <c>OracleReaderCases.g.cs</c> (written by <c>python -m oracle.script --emit-synthetic</c>). No game data is
/// involved. The same generator, with its own seed and 30 times as many cases, feeds the data test <c>ReaderFuzzTests</c>.
/// </summary>
public sealed class OracleCasesTests
{
    private static string Join(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);

    [Fact]
    public void InlineReadersEqualTheOracle()
    {
        Assert.True(OracleReaderCases.Inline.Length >= 300);
        var bad = new List<string>();
        foreach ((string hex, int p0, string want) in OracleReaderCases.Inline)
        {
            byte[] buf = Convert.FromHexString(hex);
            int n = want.Split(' ').Length;
            var got = new StringBuilder();
            for (int k = 0; k < n; k++)
            {
                got.Append(k > 0 ? " " : string.Empty).Append(InlineReader.ReadInt(buf, p0, k, 16)).Append('/')
                    .Append(InlineReader.ReadInt(buf, p0, k, 32)).Append('/').Append(InlineReader.IsPresent(buf, p0, k) ? '1' : '0');
            }

            if (got.ToString() != want)
            {
                bad.Add($"{hex} p0={p0}: oracle {want}, C# {got}");
            }
        }

        Assert.True(bad.Count == 0, Join(bad.Take(8)));
    }

    [Fact]
    public void ExpressionsEqualTheOracleIncludingUndefinedOnes()
    {
        Assert.True(OracleReaderCases.Expr.Length >= 500);
        var bad = new List<string>();
        int undefined = 0;
        foreach ((string hex, string want) in OracleReaderCases.Expr)
        {
            string got = ObjectExpression.TryEvaluate(Convert.FromHexString(hex), out int v, out ExpressionError e)
                ? v.ToString(CultureInfo.InvariantCulture)
                : "u" + Code(e);
            undefined += got[0] == 'u' ? 1 : 0;
            if (got != want)
            {
                bad.Add($"{hex} ({Encoding.Latin1.GetString(Convert.FromHexString(hex))}): oracle {want}, C# {got}");
            }
        }

        Assert.True(bad.Count == 0, Join(bad.Take(8)));
        Assert.True(undefined > 20, "the sample must contain undefined results");
    }

    [Fact]
    public void StrtolGettersEqualTheOracle()
    {
        var bad = new List<string>();
        foreach ((string hex, string strict, string partial) in OracleReaderCases.Int)
        {
            byte[] arg = Convert.FromHexString(hex);
            string s = ObjectParser.StrictInt(arg) is { } a ? a.ToString(CultureInfo.InvariantCulture) : "n";
            string p = ObjectParser.PartialInt(arg) is { } b ? b.ToString(CultureInfo.InvariantCulture) : "n";
            if (s != strict || p != partial)
            {
                bad.Add($"{hex}: oracle {strict}/{partial}, C# {s}/{p}");
            }
        }

        Assert.True(bad.Count == 0, Join(bad.Take(8)));
    }

    [Fact]
    public void ObjectSplitEqualsTheOracle()
    {
        var bad = new List<string>();
        var ranges = new ArgRange[ObjectParser.MaxArguments];
        foreach ((string hex, int sep, string want) in OracleReaderCases.Split)
        {
            SplitStatus st = ObjectParser.Split(Convert.FromHexString(hex), sep, ranges, out int count);
            var got = new StringBuilder(st switch { SplitStatus.Ok => "ok", SplitStatus.UnterminatedQuote => "quote", _ => "noterm" });
            got.Append(' ').Append(st == SplitStatus.Ok ? count : 0);
            for (int i = 0; st == SplitStatus.Ok && i < count; i++)
            {
                got.Append(' ').Append(ranges[i].Start).Append('-').Append(ranges[i].End);
            }

            if (got.ToString() != want)
            {
                bad.Add($"{hex} sep={sep}: oracle {want}, C# {got}");
            }
        }

        Assert.True(bad.Count == 0, Join(bad.Take(8)));
    }

    [Fact]
    public void SegmentationEqualsTheOracleOnSyntheticScripts()
    {
        var bad = new List<string>();
        int errors = 0;
        foreach ((string hex, string want) in OracleReaderCases.Segments)
        {
            byte[] script = Convert.FromHexString(hex);
            var lines = new List<string>();
            int truncated = 0;
            int index = 0;
            for (int pos = 0; pos < script.Length; index++)
            {
                ScriptSegment s = ScriptSegmenter.Read(script, pos);
                truncated += s.Truncated ? 1 : 0;
                string key = s.HasName ? Convert.ToHexStringLower(script.AsSpan(s.NameStart, 4)) : "-";
                string ids = ScriptSegmenter.TryReadMarkerId(script, s, out int block, out int sub)
                    ? block.ToString(CultureInfo.InvariantCulture) + " " + sub.ToString(CultureInfo.InvariantCulture)
                    : "- -";
                lines.Add($"S {index} {s.Start} {s.End} {(s.IsCommand ? s.Underscore : -1)} {key} {ids}");
                pos = s.End;
            }

            if (want == "error")
            {
                errors++;
                if (truncated == 0)
                {
                    bad.Add($"{hex}: the oracle raises ScriptError, C# found no truncated segment");
                }
            }
            else if (truncated != 0 || string.Join("|", lines) != want)
            {
                bad.Add($"{hex}: oracle {want}, C# {string.Join("|", lines)} (truncated {truncated})");
            }
        }

        Assert.True(bad.Count == 0, Join(bad.Take(6)));
        Assert.True(errors >= 1, "the sample must contain an overlong segment");
    }

    private static string Code(ExpressionError e) => e switch
    {
        ExpressionError.DivisionFault => "div",
        ExpressionError.UnaryWithoutOperand => "un",
        ExpressionError.BinaryWithoutOperands => "bin",
        ExpressionError.EmptyStack => "empty",
        ExpressionError.InsideBraces => "brace",
        _ => "?",
    };
}

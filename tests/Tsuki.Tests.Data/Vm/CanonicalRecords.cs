using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Vm;

namespace Tsuki.Tests.Data.Vm;

/// <summary>
/// The canonical record text of a script, written from the C# API. It must equal, byte for byte, what
/// <c>tools/oracle/script.py</c> (<c>segment_line</c>, <c>argument_lines</c>) writes from <c>tsuki.script</c>; the record format
/// is documented there. One <c>S</c> record per segment, then for command segments the <c>A</c> / <c>I</c> / <c>O</c> /
/// <c>V</c> records.
/// </summary>
public static class CanonicalRecords
{
    /// <summary>The records of one script: segment records, argument records, and the number of command segments.</summary>
    public sealed record Records(StringBuilder Segments, StringBuilder Arguments, int SegmentCount, int CommandCount);

    /// <summary>Per-script digests as the golden file stores them.</summary>
    public sealed record Digests(int Segments, int Commands, string SegSha256, string ArgsSha256, string RawSha256);

    /// <summary>
    /// Writes the records of <paramref name="script"/>. Returns the number of truncated segments in
    /// <paramref name="truncated"/> (the Python reference raises on those).
    /// </summary>
    public static Records Build(byte[] script, out int truncated)
    {
        var seg = new StringBuilder(script.Length / 2);
        var arg = new StringBuilder(script.Length * 2);
        var ranges = new List<ArgRange>();
        truncated = 0;
        int index = 0;
        int commands = 0;
        for (int pos = 0; pos < script.Length; index++)
        {
            ScriptSegment s = ScriptSegmenter.Read(script, pos);
            if (s.Truncated)
            {
                truncated++;
            }

            AppendSegment(seg, script, s, index);
            if (ScriptSegmenter.TryGetStatement(0, script, s, out Statement st))
            {
                commands++;
                AppendArguments(arg, st, s, index, ranges);
            }

            pos = s.End;
        }

        return new Records(seg, arg, index, commands);
    }

    /// <summary>The digests the golden file lists for a script.</summary>
    public static Digests Digest(byte[] script)
    {
        Records r = Build(script, out _);
        return new Digests(r.SegmentCount, r.CommandCount, Sha256(r.Segments), Sha256(r.Arguments), Convert.ToHexStringLower(SHA256.HashData(script)));
    }

    /// <summary>SHA-256 of the ASCII text.</summary>
    public static string Sha256(StringBuilder text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(text.ToString())));

    /// <summary>The record text of one script for failure messages: statements [first, first + count).</summary>
    public static string Dump(byte[] script, int first, int count)
    {
        Records r = Build(script, out _);
        string[] seg = r.Segments.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] arg = r.Arguments.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        for (int i = first; i < Math.Min(seg.Length, first + count); i++)
        {
            sb.AppendLine(seg[i]);
            foreach (string a in arg.Where(a => a.Split(' ', 3)[1] == i.ToString(CultureInfo.InvariantCulture)))
            {
                sb.AppendLine(a);
            }
        }

        return sb.ToString();
    }

    private static void AppendSegment(StringBuilder sb, byte[] script, ScriptSegment s, int index)
    {
        string key = s.HasName ? Convert.ToHexStringLower(script.AsSpan(s.NameStart, 4)) : "-";
        string block = "-";
        string sub = "-";
        if (ScriptSegmenter.TryReadMarkerId(script, s, out int b, out int ss))
        {
            block = b.ToString(CultureInfo.InvariantCulture);
            sub = ss.ToString(CultureInfo.InvariantCulture);
        }

        sb.Append("S ").Append(index).Append(' ').Append(s.Start).Append(' ').Append(s.End).Append(' ')
            .Append(s.IsCommand ? s.Underscore : -1).Append(' ').Append(key).Append(' ').Append(block).Append(' ').Append(sub).Append('\n');
    }

    private static void AppendArguments(StringBuilder sb, Statement st, ScriptSegment seg, int index, List<ArgRange> ranges)
    {
        ReadOnlySpan<byte> args = st.Args.Span;

        // A: textual arguments
        ranges.Clear();
        int close = StatementArguments.SplitText(st, ranges);
        ReadOnlySpan<byte> trailer = ArgumentList.Trailer(args, close);
        int n = ranges.Count;
        sb.Append("A ").Append(index).Append(' ').Append(trailer.IsEmpty ? "-" : Convert.ToHexStringLower(trailer)).Append(' ').Append(n).Append(' ');
        if (n == 0)
        {
            sb.Append("- -");
        }
        else
        {
            for (int k = 0; k < n; k++)
            {
                sb.Append(k > 0 ? "," : string.Empty).Append(Convert.ToHexStringLower(ranges[k].In(args)));
            }

            sb.Append(' ');
            for (int k = 0; k < n; k++)
            {
                int? tag = ArgumentList.Tag(ranges[k].In(args));
                sb.Append(k > 0 ? "," : string.Empty).Append(tag is { } t ? t.ToString(CultureInfo.InvariantCulture) : "-");
            }
        }

        sb.Append('\n');

        // I: inline readers
        sb.Append("I ").Append(index);
        for (int k = 0; k <= Math.Max(n, 1); k++)
        {
            sb.Append(' ').Append(InlineReader.ReadInt16(st, k)).Append('/').Append(InlineReader.ReadInt32(st, k)).Append('/')
                .Append(InlineReader.IsPresent(st, k) ? '1' : '0');
        }

        sb.Append('\n');

        // O: object parser
        Span<ArgRange> r = stackalloc ArgRange[ObjectParser.MaxArguments];
        SplitStatus status = ObjectParser.Split(st, r, out int count);
        sb.Append("O ").Append(index).Append(' ').Append(status switch
        {
            SplitStatus.Ok => "ok",
            SplitStatus.UnterminatedQuote => "quote",
            _ => "noterm",
        }).Append(' ').Append(status == SplitStatus.Ok ? count : 0);
        if (status == SplitStatus.Ok)
        {
            for (int k = 0; k < count; k++)
            {
                ReadOnlySpan<byte> data = r[k].In(args);
                bool has = data.Length > 0;
                string expr = "n";
                if (has)
                {
                    expr = ObjectExpression.TryEvaluate(data, out int v, out ExpressionError e)
                        ? v.ToString(CultureInfo.InvariantCulture)
                        : "u" + Code(e);
                }

                int? strict = ObjectParser.StrictInt(data);
                int? partial = ObjectParser.PartialInt(data);
                sb.Append(' ').Append(seg.ArgsStart + r[k].Start).Append('-').Append(seg.ArgsStart + r[k].End).Append(':').Append(has ? '1' : '0')
                    .Append(':').Append(expr).Append(':').Append(strict is { } s ? s.ToString(CultureInfo.InvariantCulture) : "n")
                    .Append(':').Append(partial is { } p ? p.ToString(CultureInfo.InvariantCulture) : "n");
            }
        }

        sb.Append('\n');

        // V: the reader the command's handler uses
        sb.Append("V ").Append(index).Append(' ');
        if (n == 0)
        {
            sb.Append('-');
        }

        for (int k = 0; k < n; k++)
        {
            ArgValue value = StatementArguments.Value(st, k);
            sb.Append(k > 0 ? " " : string.Empty).Append(value.Kind switch
            {
                ArgValueKind.Value => value.Value.ToString(CultureInfo.InvariantCulture),
                ArgValueKind.Absent => "n",
                ArgValueKind.Undefined => "u",
                _ => "e",
            });
        }

        sb.Append('\n');
    }

    /// <summary>The oracle's short code of an expression error.</summary>
    public static string Code(ExpressionError e) => e switch
    {
        ExpressionError.DivisionFault => "div",
        ExpressionError.UnaryWithoutOperand => "un",
        ExpressionError.BinaryWithoutOperands => "bin",
        ExpressionError.EmptyStack => "empty",
        ExpressionError.InsideBraces => "brace",
        _ => throw new ArgumentOutOfRangeException(nameof(e), e, null),
    };
}

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tsuki.Data.Text;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Data.Text;

/// <summary>
/// The canonical text forms of tokens, runs and plain text that <c>text_oracle.py</c> prints (see its docstring); the C# side writes exactly
/// the same text, so a hash of it equals the oracle's hash when and only when the token streams are equal.
/// </summary>
internal static class OracleCanon
{
    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    public static string Hex(ImmutableArray<byte> bytes) => Convert.ToHexStringLower(bytes.AsSpan());

    /// <summary>The first 8 bytes of SHA-1 over the UTF-8 text, as 16 lower-case hex digits.</summary>
    public static string H16(string s) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(s)).AsSpan(0, 8));

    public static string Token(MarkupToken t) => t switch
    {
        TextToken x => $"T:{Hex(x.Text)}:{Hex(x.Raw)}",
        RubyToken r =>
            $"R:{Hex(r.Base)}:{Hex(r.Reading)}:{Hex(r.Raw)}:{(r.Closed ? 1 : 0)}:[{string.Join(",", r.BaseTokens.Select(Token))}]:[{string.Join(",", r.ReadingTokens.Select(Token))}]",
        NewlineToken => $"N:{Hex("^"u8)}",
        IconToken i => $"I:{i.Name}:{i.Index}:{Hex(i.Raw)}",
        CodeToken c => $"C:{Hex(c.Code)}:{(c.Arg is { } a ? Hex(a) : "-")}:{Hex(c.Raw)}",
        RefToken f => $"F:{f.LineId}:{Hex(f.Raw)}",
        _ => throw new InvalidOperationException(t.GetType().Name),
    };

    public static string Tokens(IEnumerable<MarkupToken> tokens) => string.Join("|", tokens.Select(Token));

    private static string F(double? x) => x is { } v ? BitConverter.DoubleToInt64Bits(v).ToString("x16", CultureInfo.InvariantCulture) : "-";

    private static string N<T>(T? x)
        where T : struct, IFormattable => x is { } v ? v.ToString(null, CultureInfo.InvariantCulture) : "-";

    public static string Run(MarkupRun r)
    {
        MarkupStyle s = r.Style;
        return $"U:{r.Kind}:{HexOrEmpty(r.Text)}:{HexOrEmpty(r.Ruby)}:{N(s.Color)}:{s.ColorExt}:{s.Style}:{(s.FlagB ? 1 : 0)}:{s.Anim}:" +
               $"{(s.NoWrap ? 1 : 0)}:{F(s.ScaleW)}:{F(s.ScaleH)}:{N(s.FontMetric)}:{N(r.Value)}:{r.Seq}";
    }

    public static string Runs(IEnumerable<MarkupRun> runs) => string.Join("|", runs.Select(Run));

    public static string Plain(ImmutableArray<MarkupToken> tokens) => string.Join(
        "|",
        Hex(Markup.PlainText(tokens, RubyRendering.Drop)),
        Hex(Markup.PlainText(tokens, RubyRendering.Paren)),
        Hex(Markup.PlainText(tokens, RubyRendering.Markup)),
        Hex(Markup.PlainText(tokens, RubyRendering.Drop, icons: false)));

    private static string HexOrEmpty(ImmutableArray<byte> a) => a.IsDefaultOrEmpty ? string.Empty : Hex(a);

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    public static Language Lang(string code) => TextRules.Parse(code);
}

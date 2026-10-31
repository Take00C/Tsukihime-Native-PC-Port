using System.Collections.Immutable;
using System.Text;

namespace Tsuki.Data.Text;

/// <summary>
/// A markup token of a text line (docs/formats/text.md 6; <c>tsuki.text</c> <c>Token</c>). All text is kept as the UTF-8 bytes the
/// tokenizer saw, because the engine counts bytes and a code's parameter can split a multi-byte character (never the case in the
/// retail data).
/// </summary>
public abstract record MarkupToken;

/// <summary>Plain text. <see cref="Raw"/> differs from <see cref="Text"/> only for the escapes <c>@1X</c> and <c>@2X</c>.</summary>
/// <param name="Text">The glyph text.</param>
/// <param name="Raw">The source bytes.</param>
public sealed record TextToken(ImmutableArray<byte> Text, ImmutableArray<byte> Raw) : MarkupToken
{
    /// <summary>A token whose source bytes are its text.</summary>
    public TextToken(ImmutableArray<byte> text)
        : this(text, text)
    {
    }

    /// <summary>The text decoded as UTF-8; invalid bytes become U+FFFD.</summary>
    public string TextString => Encoding.UTF8.GetString(Text.AsSpan());
}

/// <summary>A ruby group <c>&lt;base|reading&gt;</c> (not parsed for English).</summary>
/// <param name="Base">Glyph text of the base part (the text tokens inside it, joined).</param>
/// <param name="Reading">Glyph text of the reading.</param>
/// <param name="Raw">The group's source bytes.</param>
/// <param name="BaseTokens">Every token inside the base part (codes, newlines and icons are processed inside groups by the engine).</param>
/// <param name="ReadingTokens">Every token inside the reading.</param>
/// <param name="Closed">False when the string ended inside the group.</param>
public sealed record RubyToken(
    ImmutableArray<byte> Base,
    ImmutableArray<byte> Reading,
    ImmutableArray<byte> Raw,
    ImmutableArray<MarkupToken> BaseTokens,
    ImmutableArray<MarkupToken> ReadingTokens,
    bool Closed) : MarkupToken;

/// <summary>A <c>^</c> line break.</summary>
public sealed record NewlineToken : MarkupToken;

/// <summary>An inline icon <c>[name]</c>.</summary>
/// <param name="Name">The icon name without brackets.</param>
/// <param name="Index">The icon number (index into <see cref="TextRules.IconNames"/>).</param>
/// <param name="Raw">The source bytes <c>[name]</c>.</param>
public sealed record IconToken(string Name, int Index, ImmutableArray<byte> Raw) : MarkupToken;

/// <summary>An <c>@</c> control code. <see cref="Arg"/> is the parameter text (digits, a number, or a parenthesised group) or null.</summary>
/// <param name="Code">The code letter as bytes (empty for a lone <c>@</c> at the end, <c>&gt;</c> for a swallowed stray close).</param>
/// <param name="Arg">The parameter bytes, null when the code has none.</param>
/// <param name="Raw">The source bytes.</param>
public sealed record CodeToken(ImmutableArray<byte> Code, ImmutableArray<byte>? Arg, ImmutableArray<byte> Raw) : MarkupToken
{
    /// <summary>The code letter when it is one ASCII character, else the empty string.</summary>
    public string Letter => Code.Length == 1 && Code[0] < 0x80 ? ((char)Code[0]).ToString() : string.Empty;
}

/// <summary>A script-level <c>$NNNNNN</c> reference (only in script mode).</summary>
/// <param name="LineId">The six-digit id.</param>
/// <param name="Raw">The source bytes.</param>
public sealed record RefToken(int LineId, ImmutableArray<byte> Raw) : MarkupToken;

/// <summary>Style state of a run after the control codes before it (docs/formats/text.md 6.2).</summary>
/// <param name="Color">The u16 colour word, null for the caller's default.</param>
/// <param name="ColorExt"><c>@c6..@c9</c> value (glyph field +0x0A).</param>
/// <param name="Style">0 normal, 1 g, 2 z, 3 italic, 4 g and italic, 5 z and italic.</param>
/// <param name="FlagB"><c>@b</c>.</param>
/// <param name="Anim"><c>@aN</c>.</param>
/// <param name="NoWrap"><c>@o</c> until <c>@p</c>.</param>
/// <param name="ScaleW">Width multiplier, null for the default.</param>
/// <param name="ScaleH">Height multiplier, null for the default.</param>
/// <param name="FontMetric"><c>@fN</c>.</param>
public readonly record struct MarkupStyle(
    int? Color = null,
    int ColorExt = 0,
    int Style = 0,
    bool FlagB = false,
    int Anim = 0,
    bool NoWrap = false,
    double? ScaleW = null,
    double? ScaleH = null,
    int? FontMetric = null);

/// <summary>A styled run (<c>tsuki.text.Run</c>).</summary>
/// <param name="Kind">text, ruby, icon, newline, wait, seq, pos or ref.</param>
/// <param name="Text">Run text (a ruby run's base, an icon's name, a pos run's code letter, a ref's source).</param>
/// <param name="Ruby">The reading of a ruby run.</param>
/// <param name="Style">The style in force.</param>
/// <param name="Value">Icon index, ref id, sequence value or x/y value.</param>
/// <param name="Seq">Typewriter sequence index of the first glyph.</param>
public readonly record struct MarkupRun(
    string Kind, ImmutableArray<byte> Text, ImmutableArray<byte> Ruby, MarkupStyle Style, int? Value, int Seq);

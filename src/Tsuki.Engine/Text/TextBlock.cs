namespace Tsuki.Engine.Text;

/// <summary>
/// One 0x30-byte glyph record of a laid-out text block (record i = element i), as the engine writes it
/// (docs/formats/textbox.md 4; tools/tsuki/textbox.py <c>GlyphRec</c>).
/// </summary>
/// <param name="Index">Element index.</param>
/// <param name="Flags">Bit 0 used, bit 1 ruby reading, bit 2 style 1, bit 3 <c>@a</c>, bit 16 <c>@b</c>, bits 20..22 style.</param>
/// <param name="Color">RGB565 colour word.</param>
/// <param name="Ext">The <c>@c6..@c9</c> value (outline colour selector).</param>
/// <param name="Line">Line index (a ruby reading carries the line of its base).</param>
/// <param name="Tex">Texture slot (font page = slot - 0x21).</param>
/// <param name="X">Atlas rectangle left.</param>
/// <param name="Y">Atlas rectangle top.</param>
/// <param name="X2">Atlas rectangle right, inclusive (<c>x + w - 1</c>, 16 bit).</param>
/// <param name="Y2">Atlas rectangle bottom, inclusive.</param>
/// <param name="Ctr">Reveal counter (typewriter position).</param>
/// <param name="Dx">x in the block, canvas units.</param>
/// <param name="Dy">y offset from the line top (a ruby reading: -7.04).</param>
/// <param name="Sx">Horizontal scale, canvas units per texel.</param>
/// <param name="Sy">Vertical scale, canvas units per texel.</param>
public readonly record struct GlyphRecord(
    int Index, int Flags, int Color, int Ext, int Line, int Tex, int X, int Y, int X2, int Y2, int Ctr,
    float Dx, float Dy, float Sx, float Sy)
{
    /// <summary>Width of the atlas rectangle.</summary>
    public int W => X2 - X + 1;

    /// <summary>Height of the atlas rectangle.</summary>
    public int H => Y2 - Y + 1;

    /// <summary>True for a ruby (furigana) reading glyph.</summary>
    public bool IsRuby => (Flags & 2) != 0;
}

/// <summary>
/// Result of one <c>Text_Layout</c> call: the 0x190-byte block the engine keeps per text block
/// (textbox.md 4; tools/tsuki/textbox.py <c>TextBlock</c>).
/// </summary>
public sealed class TextBlock
{
    /// <summary>Lines the engine tracks per block (+0x22 / +0x80 / +0x100 arrays).</summary>
    public const int LineSlots = 32;

    /// <summary>Elements (glyphs and controls) found by pass 1, at most 1200.</summary>
    public int N { get; init; }

    /// <summary>Width argument (+0x02).</summary>
    public int WidthArg { get; init; }

    /// <summary>Height limit (+0x04): drawing stops below y + height.</summary>
    public int Height { get; init; }

    /// <summary>Caller flags (+0x08).</summary>
    public int Flags { get; init; }

    /// <summary>Extra advance per glyph (+0x16).</summary>
    public int Spacing { get; init; }

    /// <summary>Extra space between lines (+0x18).</summary>
    public int Leading { get; init; }

    /// <summary>Effective box width W (VA 0x538F0 or <c>@f</c>).</summary>
    public int BoxWidth { get; init; }

    /// <summary>Base glyph scale x (argument / 3, +0x70).</summary>
    public float Sx { get; init; }

    /// <summary>Base glyph scale y (+0x74).</summary>
    public float Sy { get; init; }

    /// <summary>Number of lines (+0x14).</summary>
    public int NLines { get; init; }

    /// <summary>Line of the last base glyph (+0x20).</summary>
    public int LastGlyphLine { get; init; }

    /// <summary>Glyph count per line (+0x22, u16 x 32).</summary>
    public required ushort[] LineGlyphs { get; init; }

    /// <summary>Pen position after the last glyph of each line (+0x80, f32 x 32).</summary>
    public required float[] LineWidth { get; init; }

    /// <summary>Height of each line (+0x100, f32 x 32).</summary>
    public required float[] LineHeight { get; init; }

    /// <summary>The glyph records (base glyphs and ruby readings).</summary>
    public required GlyphRecord[] Records { get; init; }

    /// <summary>Break class of every pass-1 element (1200 bytes, VA 0x178C100).</summary>
    public required byte[] Classes { get; init; }

    /// <summary>English word-length table (1200 entries, VA 0x178C5B0).</summary>
    public required ushort[] WordLen { get; init; }

    /// <summary>Typewriter counter after the text (VA 0x178BB50[block]); -1 for an empty text.</summary>
    public int EndCtr { get; init; }

    /// <summary>The <c>@y</c> value (+0x1E).</summary>
    public int YField { get; init; }

    /// <summary>True when pass 1 found no element: the engine returns 0 and leaves the block untouched.</summary>
    public bool Empty { get; init; }
}

/// <summary>Arguments of <c>Text_Layout</c> (VA 0xC6C20); the defaults are those of the ordinary ADV message.</summary>
/// <param name="Width">Width argument (474; VA 0x538F0 may widen it).</param>
/// <param name="Height">Height limit (220).</param>
/// <param name="Spacing">Extra advance per glyph.</param>
/// <param name="Leading">Extra space between lines (9).</param>
/// <param name="Color">Initial RGB565 colour word.</param>
/// <param name="Flags">Caller flags: bit 20 starts in style 2 (speech bubbles); 0x40, 0x2000, 0x4000, 0x8000, 0x20000 change the markup handling.</param>
/// <param name="Sx">Glyph scale argument x (0.74, divided by 3 by the engine).</param>
/// <param name="Sy">Glyph scale argument y.</param>
/// <param name="Block">Text block slot (4..8 and 79..89 are the message slots).</param>
/// <param name="Window">F1105 / F1109 window index: 42 selects width 600, below 3 is the "normal window".</param>
/// <param name="SavedX">The <c>@x</c> continue position (global VA 0x178BE20).</param>
/// <param name="NormalWindow">Overrides the normal-window predicate (VA 0x53860); null = window &lt; 3.</param>
public sealed record LayoutParams(
    int Width = 474, int Height = 220, int Spacing = 0, int Leading = 9, int Color = 0xFFFF, int Flags = 0,
    double Sx = 0.74, double Sy = 0.74, int Block = 4, int Window = 0, double SavedX = 0.0, bool? NormalWindow = null);

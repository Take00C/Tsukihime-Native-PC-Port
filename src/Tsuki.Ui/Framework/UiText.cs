using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Ui;

/// <summary>
/// How a block of UI text is laid out and where its coordinates live (docs/research/text_vertical_scale.md 1): the canvas that maps text units to
/// pixels, the layout arguments of <c>Text_Layout</c>, and the draw flags of the block draw.
/// </summary>
/// <param name="CanvasW">Width of the text space in units (640 for the 640 x 448 screens).</param>
/// <param name="CanvasH">Height of the text space in units (448; 344.7 for the backlog and the choice screen).</param>
/// <param name="Sx">Glyph scale argument x (0.74 = the message size; the layout divides it by 3).</param>
/// <param name="Sy">Glyph scale argument y.</param>
/// <param name="Width">Layout width in units (lines break at it).</param>
/// <param name="Height">Block height limit in units.</param>
/// <param name="Leading">Extra space between lines.</param>
/// <param name="Spacing">Extra advance per glyph.</param>
/// <param name="Colour">Initial RGB565 colour word (0xFFFF white).</param>
/// <param name="DrawFlags">Block draw flags (<see cref="BlockDraw"/>): centre, right, shadow, outline.</param>
public readonly record struct TextStyle(
    double CanvasW = 640, double CanvasH = 448, double Sx = 0.74, double Sy = 0.74, int Width = 474, int Height = 220, int Leading = 9,
    int Spacing = 0, int Colour = 0xFFFF, int DrawFlags = BlockDraw.Always | BlockDraw.Outline)
{
    /// <summary>RGB565 white.</summary>
    public const int White = 0xFFFF;

    /// <summary>RGB565 of the choice cursor line (112, 248, 248).</summary>
    public const int Cyan = 0x77FF;

    /// <summary>RGB565 black.</summary>
    public const int Black = 0x0000;

    /// <summary>The RGB565 word of an 8-bit colour.</summary>
    public static int Rgb565(int r, int g, int b) => ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);

    /// <summary>
    /// A style for text on a 640 x 448 canvas from knife's slot scale (<paramref name="a"/>, <paramref name="b"/>) (the factors of its <c>sub_A7790</c> calls:
    /// 0.36 for the scene title, 0.6 x 0.7 for dialog messages): the PS4 layout takes <c>sy = 1.24444 b</c>, because one unit is 3 px across and 2.4107 px down
    /// (docs/research/text_vertical_scale.md 1: "their layout sy arguments already carry a x1.24 compensation").
    /// </summary>
    public static TextStyle Slot(double a, double b) => new(640, 448, a, b * (448.0 / 360.0), 474, 220, 9, 0, White, BlockDraw.Always | BlockDraw.Outline);

    /// <summary>A square-glyph style (<see cref="Slot"/> with equal factors).</summary>
    public static TextStyle Menu(double a) => Slot(a, a);
}

/// <summary>
/// Draws <c>ui_text</c> strings with the game's glyph pages: the bit-exact <see cref="TextLayout"/> and <see cref="BlockDraw"/> of the message
/// window, converted to quads of the display list. Layouts are cached per string and style.
/// </summary>
public sealed class UiText
{
    private readonly UiAssets _assets;
    private readonly Dictionary<(string, Language, TextStyle), TextBlock> _cache = [];

    /// <summary>Creates a text drawer over <paramref name="assets"/> (the fonts come from its tables).</summary>
    public UiText(UiAssets assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _assets = assets;
    }

    /// <summary>Lays out <paramref name="text"/> (<c>^</c> breaks lines).</summary>
    public TextBlock Layout(string text, in TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(text);
        var key = (text, _assets.Language, style);
        if (_cache.TryGetValue(key, out TextBlock? block))
        {
            return block;
        }

        if (_cache.Count > 512)
        {
            _cache.Clear();
        }

        var p = new LayoutParams(style.Width, style.Height, style.Spacing, style.Leading, style.Colour, 0, style.Sx, style.Sy, Block: 100);
        block = TextLayout.Layout(Encoding.UTF8.GetBytes(text), _assets.Language, _assets.Tables.Fonts, p);
        _cache[key] = block;
        return block;
    }

    /// <summary>Lays out raw message bytes (with their markup) in text block slot <paramref name="block"/> (the message slots are 4..8 and 79..89).</summary>
    public TextBlock Layout(ReadOnlyMemory<byte> text, in TextStyle style, int block)
    {
        var p = new LayoutParams(style.Width, style.Height, style.Spacing, style.Leading, style.Colour, 0, style.Sx, style.Sy, Block: block);
        return TextLayout.Layout(text.Span, _assets.Language, _assets.Tables.Fonts, p);
    }

    /// <summary>The pen width in units of the widest line of <paramref name="text"/>.</summary>
    public float Width(string text, in TextStyle style)
    {
        TextBlock b = Layout(text, style);
        float w = 0f;
        for (int i = 0; i < b.NLines && i < TextBlock.LineSlots; i++)
        {
            w = MathF.Max(w, b.LineWidth[i]);
        }

        return w;
    }

    /// <summary>
    /// The horizontal extent of the glyph quads that drawing <paramref name="text"/> with <paramref name="style"/> at x = 0 emits, in canvas pixels (the ink: its right
    /// edge is what has to stay inside a box, not the pen width). (0, 0) for an empty string.
    /// </summary>
    public (float Left, float Right) InkSpan(string text, in TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(text);
        TextBlock block = Layout(text, style);
        if (block.Empty)
        {
            return (0f, 0f);
        }

        List<Quad> quads = BlockDraw.BlockQuads(block, 0f, 0f, style.DrawFlags, 1f, BlockDraw.RevealAll, BlockDraw.RevealAll, 1.0, true, false, _assets.Language);
        float kx = (float)(UiMetrics.Width / style.CanvasW);
        float left = float.MaxValue;
        float right = float.MinValue;
        foreach (Quad q in quads)
        {
            if (q.Tex - 0x21 < 0)
            {
                continue;
            }

            float px = (float)q.X * kx;
            left = MathF.Min(left, px);
            right = MathF.Max(right, px + ((q.U1 - q.U0 + 1) * (float)q.Sx * kx));
        }

        return left > right ? (0f, 0f) : (left, right);
    }

    /// <summary>The height in units of the laid-out lines (line heights plus leading).</summary>
    public float Height(string text, in TextStyle style)
    {
        TextBlock b = Layout(text, style);
        float h = 0f;
        for (int i = 0; i < b.NLines && i < TextBlock.LineSlots; i++)
        {
            h += b.LineHeight[i] + b.Leading;
        }

        return h;
    }

    /// <summary>
    /// Draws <paramref name="text"/> with its first line's top-left at (<paramref name="x"/>, <paramref name="y"/>) in units of the style's canvas.
    /// With <see cref="BlockDraw.Center"/> or <see cref="BlockDraw.Right"/> the lines align in the style's width starting at x.
    /// </summary>
    /// <param name="p">The painter.</param>
    /// <param name="text">The string.</param>
    /// <param name="style">Layout and canvas.</param>
    /// <param name="x">Left edge in canvas units.</param>
    /// <param name="y">Top edge in canvas units.</param>
    /// <param name="alpha">Alpha 0..1.</param>
    /// <param name="head">Reveal head for typed text (768 shows everything).</param>
    /// <param name="tail">Reveal tail (768 shows everything).</param>
    /// <returns>Quads emitted.</returns>
    public int Draw(UiPainter p, string text, in TextStyle style, float x, float y, float alpha = 1f, double head = BlockDraw.RevealAll, double tail = BlockDraw.RevealAll)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (string.IsNullOrEmpty(text) || alpha <= 0f)
        {
            return 0;
        }

        return DrawBlock(p, Layout(text, style), style, x, y, alpha, head, tail);
    }

    /// <summary>
    /// Draws a one-line label of the game's glyph font centred on a point, with a soft glow (package K18: the title's EXIT, whose art the game does not have: the five
    /// items of TITLE_PARTS are pictures). The glow is the glyph itself in the glow colour, drawn at a low alpha on three rings of eight offsets around its place; the glyph
    /// goes on top in the fill colour. (The pages' dilated outline copy is not used: its quads end in a faint box, which shows as soon as it is light on a dark sky.)
    /// </summary>
    /// <param name="p">The painter.</param>
    /// <param name="text">The string (one line).</param>
    /// <param name="style">Layout and canvas (<see cref="TextStyle.Spacing"/> is the extra advance between the letters).</param>
    /// <param name="centreX">Canvas pixel x of the middle of the ink.</param>
    /// <param name="topY">Canvas pixel y of the top of the layout box.</param>
    /// <param name="fill">The glyph colour.</param>
    /// <param name="glow">The glow colour.</param>
    /// <param name="alpha">Overall alpha.</param>
    /// <param name="glowAlpha">The alpha of one glow copy (twenty-five are stacked).</param>
    /// <returns>Quads emitted.</returns>
    public int DrawGlowLabel(UiPainter p, string text, in TextStyle style, float centreX, float topY, (int R, int G, int B) fill, (int R, int G, int B) glow, float alpha, float glowAlpha)
    {
        ArgumentNullException.ThrowIfNull(p);
        TextBlock block = Layout(text, style);
        if (block.Empty || alpha <= 0f)
        {
            return 0;
        }

        float kx = (float)(UiMetrics.Width / style.CanvasW);
        float ky = (float)(UiMetrics.Height / style.CanvasH);
        (float left, float right) = InkSpan(text, style);
        float shift = centreX - ((left + right) / 2f);
        List<Quad> quads = BlockDraw.BlockQuads(block, 0f, 0f, BlockDraw.Always, 1.0, BlockDraw.RevealAll, BlockDraw.RevealAll, 1.0, true, false, _assets.Language);
        var around = new List<(float Dx, float Dy)> { (0f, 0f) };
        foreach (float radius in new[] { 2f, 4f, 6f })
        {
            for (int k = 0; k < 8; k++)
            {
                float angle = k * MathF.PI / 4f;
                around.Add((radius * MathF.Cos(angle), radius * MathF.Sin(angle)));
            }
        }

        uint glowColour = (uint)(glow.R | (glow.G << 8) | (glow.B << 16));
        uint fillColour = (uint)(fill.R | (fill.G << 8) | (fill.B << 16));
        int n = 0;
        foreach (bool glowPass in new[] { true, false })
        {
            // every glow first, then every glyph: a glow never lies over the letter before it
            foreach (Quad q in quads)
            {
                int page = q.Tex - 0x21;
                if (page < 0 || q.Kind != QuadKind.Fill)
                {
                    continue;
                }

                float w = (q.U1 - q.U0 + 1) * (float)q.Sx * kx;
                float h = (q.V1 - q.V0 + 1) * (float)q.Sy * ky;
                float px = ((float)q.X * kx) + shift;
                float py = ((float)q.Y * ky) + topY;
                TextureRef tex = TextureRef.ForGlyphPage(_assets.Language, page);
                float u0 = q.U0 / 1024f;
                float v0 = q.V0 / 1024f;
                float u1 = (q.U1 + 1) / 1024f;
                float v1 = (q.V1 + 1) / 1024f;
                if (glowPass)
                {
                    uint rgba = UiPainter.Pack(glowAlpha * alpha, glowColour);
                    foreach ((float dx, float dy) in around)
                    {
                        p.Quad(tex, px + dx, py + dy, px + dx + w, py + dy + h, u0, v0, u1, v1, rgba);
                        n++;
                    }
                }
                else
                {
                    p.Quad(tex, px, py, px + w, py + h, u0, v0, u1, v1, UiPainter.Pack(alpha, fillColour));
                    n++;
                }
            }
        }

        return n;
    }

    /// <summary>Draws a laid-out block with its first line's top-left at (<paramref name="x"/>, <paramref name="y"/>) in units of the style's canvas.</summary>
    public int DrawBlock(UiPainter p, TextBlock block, in TextStyle style, float x, float y, float alpha = 1f, double head = BlockDraw.RevealAll, double tail = BlockDraw.RevealAll)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(block);
        if (block.Empty || alpha <= 0f)
        {
            return 0;
        }

        List<Quad> quads = BlockDraw.BlockQuads(block, x, y, style.DrawFlags, alpha, head, tail, 1.0, true, false, _assets.Language);
        float kx = (float)(UiMetrics.Width / style.CanvasW);
        float ky = (float)(UiMetrics.Height / style.CanvasH);
        int n = 0;
        foreach (Quad q in quads)
        {
            int page = q.Tex - 0x21;
            if (page < 0)
            {
                continue;
            }

            float w = (q.U1 - q.U0 + 1) * (float)q.Sx * kx;
            float h = (q.V1 - q.V0 + 1) * (float)q.Sy * ky;
            float px = (float)q.X * kx;
            float py = (float)q.Y * ky;
            uint rgba = UiPainter.Pack((float)Math.Clamp(q.Alpha, 0.0, 1.0), (uint)(q.R | (q.G << 8) | (q.B << 16)));
            p.Quad(
                TextureRef.ForGlyphPage(_assets.Language, page), px, py, px + w, py + h,
                q.U0 / 1024f, q.V0 / 1024f, (q.U1 + 1) / 1024f, (q.V1 + 1) / 1024f, rgba);
            n++;
        }

        return n;
    }
}

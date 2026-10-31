using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;
using Tsuki.Engine.Text;

namespace Tsuki.Engine.Game.Effects;

/// <summary>What the choice draw needs from the choice machine for one frame (system_flow.md 5.2, 5.3).</summary>
/// <param name="Phase">0 fade-in, 1 reveal, 2 select, 3 decided.</param>
/// <param name="Cursor">The cursor line (u16 0x17506A2), -1 none.</param>
/// <param name="Previous">The previous cursor line (0x17506A4), -1 none.</param>
/// <param name="Alpha">The choice alpha <c>a</c> (HUD alpha times 0x17506A8).</param>
/// <param name="Highlight">The highlight <c>h</c> (0x17506B4), 0..1.</param>
/// <param name="Flash">The decide flash (0x17506AC), 1 to 0.</param>
/// <param name="X0">Left edge of the lines in canvas units (87, or 30 in English).</param>
/// <param name="Y0">Top of the first line in canvas units.</param>
/// <param name="UnitX">Pixels per canvas unit in x (3).</param>
/// <param name="UnitY">Pixels per canvas unit in y (the host's text scale; 1080 / 344.7 on the console).</param>
public readonly record struct ChoiceView(int Phase, int Cursor, int Previous, float Alpha, float Highlight, float Flash, double X0, double Y0, double UnitX, double UnitY)
{
    /// <summary>Line pitch in canvas units.</summary>
    public const double LinePitch = 26.02;

    /// <summary>The top of line <paramref name="k"/> (<c>y_k = y0 + 26.02 k</c>).</summary>
    public double LineY(int k) => Y0 + (LinePitch * k);
}

/// <summary>
/// One draw call of the choice screen as the PS4 function 0xA6E10 makes it (system_flow.md 5.3), in the units of its canvas (640 x 344.7): what
/// <c>tools/tsuki/misc_boot_title_choice_flow.choice_draw_list</c> lists. Recorded only when <see cref="GameHost.ChoiceTrace"/> is set (the tests).
/// </summary>
/// <param name="Kind"><c>"text"</c> (a block draw) or <c>"tex"</c> (a SELECT_PARTS picture).</param>
/// <param name="Slot">Text block id (0x14 + k, glow copies 0x64 + j / 0x6C + j) or texture slot (91 + SELECT_PARTS child).</param>
/// <param name="X">Position x in canvas units (the centre for the ring, whose draw flag is 0x34).</param>
/// <param name="Y">Position y in canvas units.</param>
/// <param name="Alpha">The alpha of the draw.</param>
/// <param name="ScaleX">Texture draws: picture scale x in canvas units per texel (the engine's <c>SetScale</c>); 0 for text.</param>
/// <param name="ScaleY">Texture draws: picture scale y; 0 for text.</param>
/// <param name="Flags">Text draws: the draw flags (0x1020 | shadow bit); 0 for the glow copies and for pictures.</param>
/// <param name="Colour">Text draws: the colour word the block was laid out with (0xFFFF, 0x77FF, 0xC7FF); 0 for pictures.</param>
public readonly record struct ChoiceDraw(string Kind, int Slot, double X, double Y, double Alpha, double ScaleX, double ScaleY, int Flags, int Colour);

/// <summary>
/// The decoration of the choice screen that the PS4 draws around the option texts (system_flow.md 5.3, VA 0xA6E10): the vignette that dims the scene and
/// the page text, the eight offset copies of the cursor line's text in cyan (the glow, cross-fading from the previous line), the underline and the lens
/// flare of the cursor line, and, once a line is decided, the ring, the blue bands and the lens flare of the flash. The sheet is SELECT_PARTS (children
/// 0..5 = textures 91..96), drawn 1 texel per pixel with the additive-alpha effect word 0xB (ps_add) except the vignette (word 0).
/// </summary>
/// <param name="sheetSize">Texel size of a child of SELECT_PARTS.</param>
public sealed class ChoiceGlow(Func<int, (int Width, int Height)> sheetSize)
{
    // the eight glow copies' offsets in canvas units (VA 0xA7209)
    private static readonly (double Dx, double Dy)[] Offsets = [(-0.9, 0), (0.9, 0), (0, -0.7), (0, 0.7), (-0.9, -0.7), (0.9, -0.7), (-0.9, 0.7), (0.9, 0.7)];

    private const int AddWord = 0xB;
    private const double SelectedScaleY = 0.31917;          // the TexDraw scale of the SELECT_PARTS pieces: one texel per pixel in y
    private float _pulse = 1f;
    private float _pulseStep = -0.0111f;

    /// <summary>Receives every decoration draw in the engine's own units (the tests compare it with the oracle's draw list); null in play.</summary>
    public Action<ChoiceDraw>? Trace { get; set; }

    /// <summary>The flare pulse (0x17506B8), bouncing between 0.333 and 1.</summary>
    public float Pulse => _pulse;

    /// <summary>The glow colour word of the cursor line: cyan while selecting, pale after the decision (system_flow.md 5.3).</summary>
    public static int GlowColour(int phase) => phase == 3 ? 0xC7FF : 0x77FF;

    /// <summary>A new choice: the pulse restarts.</summary>
    public void Reset()
    {
        _pulse = 1f;
        _pulseStep = -0.0111f;
    }

    /// <summary>A cursor change sets the pulse to 1 and its step to -0.0111 (VA 0xA6885).</summary>
    public void OnCursorChange() => Reset();

    /// <summary>Every frame of the choice mode: the pulse bounces between 0.333 and 1 at 0.0111 per frame (VA 0xA61BB).</summary>
    public void Tick(int frames)
    {
        for (int i = 0; i < Math.Max(1, frames); i++)
        {
            _pulse += _pulseStep;
            if (_pulse <= 0.333f)
            {
                _pulse = 0.333f;
                _pulseStep = 0.0111f;
            }
            else if (_pulse >= 1f)
            {
                _pulse = 1f;
                _pulseStep = -0.0111f;
            }
        }
    }

    /// <summary>The vignette (texture 96, child 5) over the whole frame, before the lines: it dims the scene and the page text.</summary>
    public void DrawVignette(DisplayList list, in ChoiceView v)
    {
        (int w, int h) = sheetSize(5);
        Add(list, v, 5, 0, 0, w, h, v.Alpha, 0, 1080.0 / 448.0);         // the vignette is drawn on the 640 x 448 canvas, before SetCanvas(640, 344.7)
    }

    /// <summary>
    /// What belongs to line <paramref name="k"/> after its text: the eight glow copies when it is the cursor or the previous line, and the underline and
    /// the flare when it is the cursor.
    /// </summary>
    /// <param name="list">The display list.</param>
    /// <param name="v">The choice view.</param>
    /// <param name="k">The line.</param>
    /// <param name="glowText">The line's text laid out in a colour word (cached by the host); null when the line has none.</param>
    /// <param name="drawText">Draws a laid-out block at canvas units (x, y) with an alpha, fully revealed.</param>
    public void DrawLine(DisplayList list, in ChoiceView v, int k, Func<int, int, TextBlock?> glowText, Action<TextBlock, double, double, double> drawText)
    {
        double y = v.LineY(k);
        if (k == v.Cursor || k == v.Previous)
        {
            float g = (k == v.Previous ? 1f - v.Highlight : v.Highlight) * v.Alpha * 192f / 255f;
            if (g > 0f && glowText(k, GlowColour(v.Phase)) is { } block)
            {
                int baseBlock = k == v.Previous ? 0x6C : 0x64;
                for (int j = 0; j < Offsets.Length; j++)
                {
                    (double dx, double dy) = Offsets[j];
                    Trace?.Invoke(new ChoiceDraw("text", baseBlock + j, v.X0 + dx, y + dy, g / 8f, 0, 0, 0, GlowColour(v.Phase)));
                    drawText(block, v.X0 + dx, y + dy, g / 8f);
                }
            }
        }

        if (k != v.Cursor)
        {
            return;
        }

        // the underline grows with the highlight; the flare pulses (both drawn even when h = 0)
        (int uw, int uh) = sheetSize(2);
        double scaleX = ((0.9 * v.Highlight) + 0.1) / 3.0;
        double ux = (v.X0 - 16.3333) * v.UnitX;
        double uy = (y + 8.93667) * v.UnitY;
        Add(list, v, 2, ux, uy, ux + (uw * scaleX * v.UnitX), uy + (uh * SelectedScaleY * v.UnitY), v.Alpha, AddWord);
        (int fw, int fh) = sheetSize(4);
        double fx = (v.X0 - 153.333) * v.UnitX;
        double fy = (y - 23.6183) * v.UnitY;
        Add(list, v, 4, fx, fy, fx + (fw * v.UnitX / 3.0), fy + (fh * SelectedScaleY * v.UnitY), v.Alpha * _pulse, AddWord);
    }

    /// <summary>After the lines of a decided choice: the ring that grows and turns, the blue bands and the lens flare.</summary>
    public void DrawDecided(DisplayList list, in ChoiceView v)
    {
        if (v.Phase != 3 || v.Cursor < 0)
        {
            return;
        }

        double s = Math.Sin(v.Flash * (-Math.PI / 2.0)) + 1.0;
        double scale = (1.4 * s) + 0.1;
        double cx = 86.0 * v.UnitX;
        double cy = (v.Y0 + 18.8308 + (ChoiceView.LinePitch * v.Cursor)) * v.UnitY;
        (int rw, int rh) = sheetSize(3);
        double hw = rw * scale / 3.0 * v.UnitX / 2.0;
        double hh = rh * scale * (344.7 / 1080.0) * v.UnitY / 2.0;
        double angle = (int)(s * 65535.0) * (2.0 * Math.PI / 65536.0);
        double c = Math.Cos(angle), sn = Math.Sin(angle);
        uint rgba = Pack(v.Alpha);
        Trace?.Invoke(new ChoiceDraw("tex", 91 + 3, cx / v.UnitX, cy / v.UnitY, v.Alpha, scale / 3.0, scale * (344.7 / 1080.0), 0, 0));
        Vertex Corner(double dx, double dy, float u, float t) => new((float)(cx + (dx * c) - (dy * sn)), (float)(cy + (dx * sn) + (dy * c)), u, t, rgba);
        list.Add(new DrawCmd
        {
            Kind = DrawKind.TexturedQuad,
            Texture = Sheet(3),
            V0 = Corner(-hw, -hh, 0, 0),
            V1 = Corner(hw, -hh, 1, 0),
            V2 = Corner(-hw, hh, 0, 1),
            V3 = Corner(hw, hh, 1, 1),
            EffectWord = AddWord,
            BlendState = 1,
        });
        (int bw, int bh) = sheetSize(0);
        Add(list, v, 0, 0, 0, bw * v.UnitX / 3.0, bh * SelectedScaleY * v.UnitY, v.Alpha, AddWord);
        (int lw, int lh) = sheetSize(1);
        double lx = 5.0 * v.UnitX;
        double ly = (v.Y0 - 290.761 + (ChoiceView.LinePitch * v.Cursor)) * v.UnitY;
        Add(list, v, 1, lx, ly, lx + (lw * v.UnitX / 3.0), ly + (lh * SelectedScaleY * v.UnitY), v.Alpha, AddWord);
    }

    private static TextureRef Sheet(int child) => TextureRef.ForUiTile(UiSet.SelectParts, Language.Ja, child, -1);

    private static uint Pack(float alpha) => 0x00FFFFFFu | ((uint)(byte)Math.Clamp((int)Math.Round(alpha * 255.0), 0, 255) << 24);

    private void Add(DisplayList list, in ChoiceView v, int child, double x0, double y0, double x1, double y1, float alpha, int word, double? canvasUnitY = null)
    {
        if (Trace is { } trace)
        {
            (int tw, int th) = sheetSize(child);
            double unitY = canvasUnitY ?? v.UnitY;
            trace(new ChoiceDraw("tex", 91 + child, x0 / v.UnitX, y0 / unitY, alpha, (x1 - x0) / (Math.Max(tw, 1) * v.UnitX), (y1 - y0) / (Math.Max(th, 1) * unitY), 0, 0));
        }

        uint rgba = Pack(alpha);
        list.Add(new DrawCmd
        {
            Kind = DrawKind.TexturedQuad,
            Texture = Sheet(child),
            V0 = new Vertex((float)x0, (float)y0, 0, 0, rgba),
            V1 = new Vertex((float)x1, (float)y0, 1, 0, rgba),
            V2 = new Vertex((float)x0, (float)y1, 0, 1, rgba),
            V3 = new Vertex((float)x1, (float)y1, 1, 1, rgba),
            EffectWord = unchecked((short)word),
            BlendState = (byte)EffectTables.BlendStateOf(word),
        });
    }
}

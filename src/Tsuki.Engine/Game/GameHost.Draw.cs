using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game.Effects;
using Tsuki.Engine.Movie;
using Tsuki.Engine.Stage;
using Tsuki.Engine.Text;
using Tsuki.Engine.Vm.Variables;

namespace Tsuki.Engine.Game;

// The frame's display list (wait_model.md 13 step 4; textbox.md 5.8, 6, 7; system_flow.md 5.3): the stage with its transition, the movie, the message
// window, the page text, the click icon, the choice lines. The quads are in the 1920 x 1080 frame; text quads are the block draw's quads times 3 in x
// and times 3.1339 (page text, 1.3 * 1080 / 448) or 3.1332 (the choice canvas, 1080 / 344.7) in y (docs/research/text_vertical_scale.md). Textures: glyph pages by (language, page); UI sheets as one whole-sheet quad (tile -1, uv 0..1) because the sheet's
// own size is not known here.
public sealed partial class GameHost
{
    private const double TextScale = 3.0;

    /// <summary>Pixels per text unit in y for the page text, the name plate and everything else the HUD puts into list 0x1E: the list is drawn nested with the scale (1, 1.3) under the 640 x 448 canvas (text_vertical_scale.md 1).</summary>
    public const double PageScaleY = 1.3 * 1080.0 / 448.0;

    /// <summary>Pixels per canvas unit in y of the choice screen (<c>SetCanvas(640, 344.7)</c>, VA 0xA6F3C; float32 1080 / 344.7).</summary>
    public static readonly double ChoiceScaleY = 1080f / 344.7f;

    /// <summary>Pixels per unit of the 640 x 448 HUD canvas in y (the unit of the message quake offsets).</summary>
    private const double HudUnitY = 1080.0 / 448.0;

    private const int WholeSheet = -1;
    private readonly Dictionary<int, (int W, int H)> _sheetSizes = [];

    private static uint Pack(int r, int g, int b, double alpha)
    {
        int a = (int)Math.Clamp(Math.Round(alpha * 255.0), 0, 255);
        return (uint)(r & 0xFF) | ((uint)(g & 0xFF) << 8) | ((uint)(b & 0xFF) << 16) | ((uint)a << 24);
    }

    /// <summary>
    /// How many commands of the list the last <see cref="Step"/> drew were the scene (layers, transition, movie); the message window, the page text and the
    /// choice lines follow. The menus that cover the story keep only the scene (the message text is hidden under them; package K6).
    /// </summary>
    public int SceneCommandCount { get; private set; }

    private void Draw(DisplayList list)
    {
        Stage.Draw(list);
        if (MovieActive && _movie!.Alpha > 0f)
        {
            DrawMovie(list, _movie.Alpha);
        }

        SceneCommandCount = list.Count;
        if (Phase == GamePhase.Running)
        {
            if (!_windowHidden)
            {
                DrawWindow(list);
                DrawPage(list);
            }

            if (ChoiceOpen)
            {
                DrawChoice(list);
            }
        }
    }

    private static void AddQuad(DisplayList list, TextureRef tex, double x0, double y0, double x1, double y1, float u0, float v0, float u1, float v1, uint rgba, int effectWord = 0)
    {
        list.Add(new DrawCmd
        {
            Kind = DrawKind.TexturedQuad,
            Texture = tex,
            V0 = new Vertex((float)x0, (float)y0, u0, v0, rgba),
            V1 = new Vertex((float)x1, (float)y0, u1, v0, rgba),
            V2 = new Vertex((float)x0, (float)y1, u0, v1, rgba),
            V3 = new Vertex((float)x1, (float)y1, u1, v1, rgba),
            EffectWord = (short)effectWord,
            BlendState = (byte)EffectTables.BlendStateOf(effectWord),
        });
    }

    private void DrawMovie(DisplayList list, float alpha)
    {
        AddQuad(list, TextureRef.ForMovieFrame(MovieSlots.FullScreen), 0, 0, 1920, 1080, 0, 0, 1, 1, Pack(255, 255, 255, alpha));
    }

    private (int W, int H) SheetSize(int child)
    {
        if (!_sheetSizes.TryGetValue(child, out (int W, int H) size))
        {
            size = _data.UiSheetSize(UiSet.DispParts, _language, child);
            _sheetSizes[child] = size;
        }

        return size;
    }

    /// <summary>The window picture (HUD VA 0x53F33..0x54492): drawn when F822 == 1 and F1104 == 0.</summary>
    private void DrawWindow(DisplayList list)
    {
        if (F(822) != 1 || F(1104) != 0 || Wait.WindowAlpha <= 0f)
        {
            return;
        }

        int w = WindowIndex();
        WindowDraw? d = MessageWindow.WindowFor(w, Config.Hud10, Wait.WindowAlpha, F(1106), F(1107));
        if (d is not { } win)
        {
            return;
        }

        (int sw, int sh) = SheetSize(win.DispChild);
        double x0 = win.XPx;
        double y0 = win.YPx;
        double x1 = x0 + (sw * win.ScaleX);
        double y1 = y0 + (sh * win.ScaleY);
        TextureRef tex = TextureRef.ForUiTile(UiSet.DispParts, _language, win.DispChild, WholeSheet);
        AddQuad(list, tex, x0, y0, x1, y1, 0, 0, 1, 1, Pack(255, 255, 255, win.Alpha));
    }

    private static int PageOf(int tex) => tex - 0x21;

    /// <summary>
    /// Appends the glyph quads of a laid-out block drawn at text units (<paramref name="x"/>, <paramref name="y"/>). The text space maps to the frame
    /// with 3 px per unit in x and <paramref name="scaleY"/> px per unit in y (glyph heights included); the pixel offsets are added after the scale
    /// (the HUD adds its quake offsets to the nested list after the 1.3 scale).
    /// </summary>
    private void AddBlock(DisplayList list, TextBlock block, double x, double y, double alpha, double head, double tail, double scaleY = PageScaleY, double offsetXPx = 0.0, double offsetYPx = 0.0, int? drawFlags = null)
    {
        int flags = drawFlags ?? (BlockDraw.Always | (Config.Text28 != 0 ? BlockDraw.Shadow : 0) | (Config.TextDraw26 != 0 ? BlockDraw.Outline : 0));
        List<Quad> quads = BlockDraw.BlockQuads(block, (float)x, (float)y, flags, alpha, head, tail, _textFade, Config.TextDraw26 != 0, Config.TextAnimations == 1, _language);
        foreach (Quad q in quads)
        {
            int page = PageOf(q.Tex);
            double pw = page == 0 ? 256.0 : 1024.0;
            double w = (q.U1 - q.U0 + 1) * q.Sx * TextScale;
            double h = (q.V1 - q.V0 + 1) * q.Sy * scaleY;
            double x0 = (q.X * TextScale) + offsetXPx;
            double y0 = (q.Y * scaleY) + offsetYPx;
            AddQuad(
                list,
                TextureRef.ForGlyphPage(_language, page),
                x0,
                y0,
                x0 + w,
                y0 + h,
                (float)(q.U0 / pw),
                (float)(q.V0 / pw),
                (float)((q.U1 + 1) / pw),
                (float)((q.V1 + 1) / pw),
                Pack(q.R, q.G, q.B, q.Alpha));
        }
    }

    /// <summary>The origins of the page's messages in text units (textbox.md 5.8; the name plate above the first message moves them down).</summary>
    private (double X, double Y)[] PageOrigins()
    {
        var blocks = new List<TextBlock>(_page.Count);
        var blank = new List<int>(_page.Count);
        var scale = new List<double>(_page.Count);
        foreach (PageMessage m in _page)
        {
            blocks.Add(m.Block);
            blank.Add(m.Blank);
            scale.Add(m.ScaleY);
        }

        // a speech-bubble window (F1105 / F1109 3..55) has its own text origin (VA 0x546CF..0x558B8: the bubble's table position plus the text offset and the F1106 / F1107 shifts); the
        // full-screen page starts at (30, 35) / (87, 49). The origin used to be the full-screen one for every window, so the text of the QA bubbles stood at the top left (package K15)
        int window = WindowIndex();
        (double X, double Y)? origin = window >= 3 && window < MessageWindow.WindowCount ? MessageWindow.BubbleTextOrigin(window, F(1106), F(1107)) : null;
        (double X, double Y)[] pos = MessageWindow.PagePositions(blocks, _language, blank, scale, origin);
        pos[^1].Y += NamePlateShift();
        return pos;
    }

    /// <summary>The messages of the page (textbox.md 5.8): the earlier ones dimmed, the current one revealed up to head and tail.</summary>
    private void DrawPage(DisplayList list)
    {
        // F1103 ("the text is hidden until the next message": _CLO2 / _CLO3, a story movie, a layer change that hides the text, the two-button prompts) hides the whole
        // page: the HUD draws the current message (VA 0x54BD8: `cmp F1103, 0; jne` over the three block draws at 0x54C2F / 0x54C71 / 0x54CAC) and the earlier ones
        // (VA 0x550F7 / 0x551D9: the history loop at 0x55930 runs only with F1103 == 0) only then (package K15: the last message used to stay over a story movie)
        if (_page.Count == 0 || Wait.WindowAlpha <= 0f || F(1103) != 0)
        {
            return;
        }

        (double X, double Y)[] pos = PageOrigins();
        double t = WindowIndex() < 3 ? 100.0 : 255.0;
        (double qx, double qy) = Stage.MessageQuake.Active ? (Stage.MessageQuake.X, Stage.MessageQuake.Y) : (0.0, 0.0);
        // the quake offsets are 640 x 448 canvas units added after the (1, 1.3) scale; the converting instruction truncates them (text_vertical_scale.md 3)
        double offX = Math.Truncate(qx) * TextScale;
        double offY = Math.Truncate(qy) * HudUnitY;
        DrawNamePlate(list, offX, offY);
        for (int j = 0; j < _page.Count; j++)
        {
            PageMessage m = _page[j];
            bool current = j == _page.Count - 1;
            double alpha = Wait.WindowAlpha;
            if (!current)
            {
                double dim = m.DimTimer < 0 ? t / 255.0 : 1.0 - ((255.0 - t) / 255.0 * Math.Min(1.0, m.DimTimer / Math.Max(1.0, m.DimLimit)));
                alpha *= dim;
            }

            double head = current ? Wait.Head : BlockDraw.RevealAll;
            double tail = current ? Wait.Tail : BlockDraw.RevealAll;
            AddBlock(list, m.Block, pos[j].X, pos[j].Y, alpha, head, tail, PageScaleY, offX, offY);
        }
    }

    /// <summary>
    /// The click-icon y the HUD stores at 0xDF0890 every frame (VA 0x54CC7): the y of the page's last message plus the offset of the last revealed
    /// line of its block (<c>0xCA690</c>, only while the window fade is positive). With no message the HUD stores the origin of the first one.
    /// </summary>
    private double ClickIconY()
    {
        (double _, double y) = _language == Language.En ? (30.0, 35.0) : (87.0, 49.0);
        if (_page.Count == 0)
        {
            return y;
        }

        (double X, double Y)[] pos = PageOrigins();
        PageMessage last = _page[^1];
        double lastLine = Wait.WindowAlpha > 0f ? MessageWindow.RevealedLastLineOffset(last.Block, Wait.Head) : 0;
        return (float)(pos[^1].Y + lastLine);
    }

    /// <summary>
    /// The top of the first choice line in canvas units (VA 0xA6F41..0xA6FAB): the click-icon y (the default constants only when it is not
    /// positive), plus two line pitches when the page has messages.
    /// </summary>
    public double ChoiceFirstY()
    {
        float y = (float)ClickIconY();
        if (!(y > 0f))
        {
            y = _language == Language.En ? 34.934246f : 49.151669f;
        }

        if (F(823) > 0)
        {
            y += 52.04f;
        }

        return y;
    }

    private void DrawChoice(DisplayList list)
    {
        if (VarState.ChoiceMode != ChoiceMode.List || _choiceAlpha <= 0f)
        {
            return;
        }

        double x0 = _language == Language.En ? 30.0 : 87.0;
        double y0 = ChoiceFirstY();
        var view = new ChoiceView(_choicePhase, _cursor, _previousCursor, _choiceAlpha, _choiceHighlight, _choiceFlash, x0, y0, TextScale, ChoiceScaleY);
        _glow.Trace = ChoiceTrace is { } trace ? trace.Add : null;
        _glow.DrawVignette(list, view);
        int flags = 0x1020 | (Config.Text28 != 0 ? 0x10 : 0);
        for (int k = 0; k < _lines.Count; k++)
        {
            ChoiceLine line = _lines[k];
            if (line.Block is null)
            {
                continue;
            }

            double y = y0 + (ChoiceView.LinePitch * k);
            if (_choicePhase != 3 || k == _cursor)
            {
                // the line is drawn at alpha 1.0 whatever the fade (VA 0xA7111); only its reveal runs
                double head = _choicePhase >= 2 ? WaitState.Instant : _choiceHead;
                ChoiceTrace?.Add(new ChoiceDraw("text", 0x14 + k, x0, y, 1.0, 0, 0, flags, 0xFFFF));
                AddBlock(list, line.Block, x0, y, 1.0, head, head, ChoiceScaleY);
            }

            // the glow of the cursor line and of the line it came from, the underline and the flare (system_flow.md 5.3)
            _glow.DrawLine(list, view, k, GlowText, (block, bx, by, alpha) => AddBlock(list, block, bx, by, alpha, BlockDraw.RevealAll, BlockDraw.RevealAll, ChoiceScaleY, drawFlags: 0));
        }

        _glow.DrawDecided(list, view);
    }

    /// <summary>When set, every draw of the choice screen is appended here in the engine's own units (the tests compare it with the oracle).</summary>
    public List<ChoiceDraw>? ChoiceTrace { get; set; }

    // a choice line laid out in a colour word (the glow copies are the same text in cyan / pale cyan)
    private TextBlock? GlowText(int line, int colour)
    {
        if (line < 0 || line >= _lines.Count)
        {
            return null;
        }

        if (!_glowText.TryGetValue((line, colour), out TextBlock? block))
        {
            // the glow copies are laid out with flags 0 (VA 0xA7270), the same width as the lines
            block = TextLayout.Layout(_lines[line].Text, _language, _fonts, new LayoutParams(Width: ChoiceWidth(), Block: 100, Window: WindowIndex(), Color: colour));
            _glowText[(line, colour)] = block;
        }

        return block;
    }
}

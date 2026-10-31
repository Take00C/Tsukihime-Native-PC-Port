namespace Tsuki.Engine.Text;

/// <summary>
/// Arguments the <c>_ZM</c> handler (VA 0x92380) and the HUD (VA 0x53CF0) pass for the ordinary ADV
/// message, and the text origin of its first message (docs/formats/textbox.md 5; tools/tsuki/textbox.py
/// <c>BoxParams</c>).
/// </summary>
/// <param name="Width">Width argument (474; VA 0x538F0 turns it into 574 in English).</param>
/// <param name="Height">Height limit (220).</param>
/// <param name="Spacing">Extra advance per glyph.</param>
/// <param name="Leading">Extra space between lines (9).</param>
/// <param name="Sx">Glyph scale x argument (0.74, times F31 / 24 when F31 &gt; 0).</param>
/// <param name="Sy">Glyph scale y argument.</param>
/// <param name="Color">First stack argument: the RGB565 colour word.</param>
/// <param name="Flags">Second stack argument: bit 20 = start in style 2 (bubble windows).</param>
/// <param name="X">Text origin x in the 640 x 360 text space.</param>
/// <param name="Y">Text origin y.</param>
/// <param name="Window">F1105 (F1109 in English when &gt; 0).</param>
public sealed record BoxParams(
    int Width = 474, int Height = 220, int Spacing = 0, int Leading = 9, double Sx = 0.74, double Sy = 0.74,
    int Color = 0xFFFF, int Flags = 0, double X = 87.0, double Y = 49.0, int Window = 0)
{
    /// <summary>The <see cref="TextLayout"/> arguments for a message block; <paramref name="savedX"/> is the <c>@x</c> continue position (<see cref="MessageWindow.ContinueX"/> of the message before it on the page).</summary>
    public LayoutParams ToLayoutParams(int block = 4, double savedX = 0.0) =>
        new(Width, Height, Spacing, Leading, Color, Flags, Sx, Sy, block, Window, savedX);
}

/// <summary>Where a window picture is drawn (VA 0x540A5..0x54492).</summary>
/// <param name="Index">F1105 (or F1109) value.</param>
/// <param name="Slot">Texture slot.</param>
/// <param name="DispChild">DISP_PARTS child that registration table VA 0x780C90 loads into the slot.</param>
/// <param name="XPx">Top-left x on the 1920 x 1080 screen.</param>
/// <param name="YPx">Top-left y.</param>
/// <param name="ScaleX">Screen pixels per texel, x.</param>
/// <param name="ScaleY">Screen pixels per texel, y.</param>
/// <param name="Alpha">Picture alpha.</param>
/// <param name="Bubble">True for a speech-bubble picture (index 3..55).</param>
public readonly record struct WindowDraw(
    int Index, int Slot, int DispChild, double XPx, double YPx, double ScaleX, double ScaleY, double Alpha, bool Bubble);

/// <summary>
/// The outputs of the HUD's call of VA 0xCA690 (at VA 0x53FC4, docs/formats/textbox.md 7.1): the position after the last revealed glyph of
/// the current message in block units with the function's own x origin of 88, truncated to whole units, and whether the text is complete.
/// </summary>
/// <param name="X"><c>trunc(88 + dx + (x2 - x0) * sx + 3)</c> of the last revealed non-ruby glyph (the mirrored <c>@b</c> rule for those glyphs); 88 with no glyph.</param>
/// <param name="Y"><c>trunc(y_line + dy)</c> of that glyph; 0 with no glyph.</param>
/// <param name="Complete">The last glyph record (ruby included) is revealed at alpha 1: <c>tail &gt; ctr</c>, or <c>head - tail &lt;= 0</c>.</param>
public readonly record struct ClickAnchor(int X, int Y, bool Complete);

/// <summary>
/// The ADV message window: parameters of the main text box, the window and bubble tables and how the
/// messages of one page are stacked (docs/formats/textbox.md 5, 8; tools/tsuki/textbox.py
/// <c>main_box_params</c>, <c>page_positions</c>, <c>window_for</c>, <c>bubble_text_origin</c>).
/// </summary>
public static class MessageWindow
{
    /// <summary>The x origin VA 0xCA690 lays its lines out from (float32 at VA 0x2D3048 and the eight copies at 0x2D3460).</summary>
    public const float AnchorOriginX = 88f;

    /// <summary>
    /// VA 0xCA690 as the HUD calls it (<c>block, 0x30, &amp;x, &amp;y, head, tail</c> at VA 0x53FC4): the line origins of <see cref="BlockDraw.LineOffsets"/>
    /// with x = 88 and y = 0 (the draw flags OR-ed with the block's), then one pass over the glyph records in order. A zeroed control record is
    /// skipped; a record whose counter is above <paramref name="head"/> leaves alpha 0 (VA 0xCABDB..0xCABE7); otherwise its alpha is 1 when
    /// <paramref name="tail"/> is above the counter or <c>head - tail &lt;= 0</c>, else <c>(head - ctr) / (head - tail)</c>; a ruby record (flag 2) counts for
    /// the alpha but not for the position. The position of a plain glyph is <c>x = (x2 - x0) * sx + (dx + x_line) + 3</c> (VA 0xCAB6E..0xCABA0), of an
    /// <c>@b</c> glyph <c>x = MirrorX - dx - w' * sx + x_line - 16</c> (VA 0xCAC14..0xCACAA, w' = x2 - x0 minus 5 for the 67-wide page-61 pieces), and
    /// <c>y = dy + y_line</c>; both outputs are truncated toward zero (<c>vcvttss2si</c>, VA 0xCACCB / 0xCACDB). The result is the alpha of the last
    /// record compared with 1 (VA 0xCACCF).
    /// </summary>
    public static ClickAnchor ClickIconAnchor(TextBlock block, double head, double tail, Contracts.Language lang, int drawFlags = 0x30)
    {
        ArgumentNullException.ThrowIfNull(block);
        float x = AnchorOriginX;
        float y = 0f;
        float alpha = 0f;
        if (block.N > 0 && block.Records.Length > 0)
        {
            (float[] xs, float[] ys, _) = BlockDraw.LineOffsets(block, AnchorOriginX, 0f, drawFlags);
            float h = (float)head;
            float t = (float)tail;
            float span = h - t;
            foreach (GlyphRecord r in block.Records)
            {
                if (r.Index == 0 && r.Flags == 0)
                {
                    continue;
                }

                float ctr = r.Ctr;
                if (ctr > h)
                {
                    alpha = 0f;
                    continue;
                }

                alpha = t > ctr || !(0f < span) ? 1f : (h - ctr) / span;
                if ((r.Flags & 2) != 0)
                {
                    continue;
                }

                int line = Math.Clamp(r.Line, 0, Math.Max(0, xs.Length - 1));
                if (xs.Length == 0)
                {
                    continue;
                }

                float w = r.X2 - r.X;
                if ((r.Flags & 0x10000) != 0)
                {
                    float wq = w == 0x43 && r.Tex == 0x5E ? w + -5f : w;
                    float gx = BlockDraw.MirrorOrigin(lang) - r.Dx;
                    gx -= wq * r.Sx;
                    gx += xs[line];
                    x = gx + -16f;
                }
                else
                {
                    float after = r.Dx + xs[line];
                    float wsx = w * r.Sx;
                    x = (wsx + after) + 3f;
                }

                y = r.Dy + ys[line];
            }
        }

        return new ClickAnchor((int)x, (int)y, alpha == 1f);
    }

    // Window table (VA 0x2006C0, 56 x {i32 slot, f32 x, f32 y} in a 1280 x 720 space).
    private static readonly (int Slot, int X, int Y)[] WindowTable =
    [
        (22, 0, 0), (23, 0, 0), (24, 0, 0), (25, 525, 414), (26, 525, 414), (27, 328, 393),
        (28, 328, 393), (29, 90, 388), (30, 90, 388), (31, 525, 44), (32, 525, 44), (33, 328, 34),
        (34, 328, 34), (35, 90, 13), (36, 90, 13), (37, 94, 414), (38, 94, 414), (39, 79, 393),
        (40, 79, 393), (41, 63, 388), (42, 63, 388), (43, 94, 44), (44, 94, 44), (45, 79, 34),
        (46, 79, 34), (47, 63, 13), (48, 63, 13), (49, 338, 229), (50, 338, 229), (51, 196, 210),
        (52, 196, 210), (53, 71, 198), (54, 71, 198), (27, 318, 393), (27, 318, 393), (29, 70, 388),
        (29, 90, 378), (29, 250, 378), (30, 300, 388), (33, 268, 34), (33, 98, 34), (34, 198, 34),
        (35, -5, 13), (35, 90, 13), (35, 105, 23), (36, 90, 13), (39, -101, 393), (41, 43, 388),
        (41, 63, 388), (45, 29, 34), (47, -47, 13), (47, -17, 13), (48, 18, 13), (53, 21, 198),
        (53, 61, 198), (54, 11, 198),
    ];

    // Text anchor offsets (VA 0x200960, 56 x {f32 dx, f32 dy}).
    private static readonly (double Dx, double Dy)[] TextOffsets =
    [
        (0, 0), (0, 0), (0, 0), (97, 62.67), (97, 62.67), (99, 96), (99, 96), (99, 119.34),
        (99, 119.34), (97, 66.33), (97, 66.33), (99, 82.66), (99, 82.66), (99, 89), (99, 89),
        (78, 62.67), (78, 62.67), (89, 96), (89, 96), (112, 119.34), (112, 119.34), (78, 66.33),
        (78, 66.33), (89, 82.66), (89, 82.66), (112, 89), (112, 89), (97, 58.33), (97, 58.33),
        (99, 56), (99, 56), (99, 80.33), (99, 80.33), (69, 96), (79, 88), (80, 119.34), (99, 109.34),
        (90, 119.34), (99, 110), (64, 82.66), (60, 82.66), (85, 82.66), (45, 89), (65, 89),
        (95, 65), (99, 69), (85, 96), (98, 119.34), (103, 104), (89, 82.66), (103, 65), (93, 89),
        (103, 89), (70, 80.33), (82, 55), (70, 80.33),
    ];

    // Image size in the 640 x 448 window canvas for indices 33..55 (VA 0x200BB0 / 0x200C0C). Entries 2, 4 and 21
    // carry the exact float32 values of the eboot (the oracle's constants were corrected in this package).
    private static readonly (double W, double H)[] WindowSizes =
    [
        (716.8, 448.0), (716.8, 488.32), (684.800048828125, 448.0), (640.0, 488.32), (684.800048828125, 448.0),
        (640.0, 448.0), (710.4, 448.0), (896.0, 448.0), (729.6, 448.0), (729.6, 448.0),
        (646.4, 448.0), (640.0, 448.0), (640.0, 448.0), (646.4, 448.0), (704.0, 448.0),
        (640.0, 448.0), (832.0, 448.0), (716.8, 448.0), (716.8, 448.0), (710.4, 448.0),
        (710.4, 448.0), (678.3999633789062, 448.0), (710.4, 448.0),
    ];

    // F1106 -> VA 0x2005B0 and F1107 -> VA 0x200620.
    private static readonly double[] XOffsets =
    [
        0.0, 100.0, 120.0, 150.0, 20.0, 200.0, 250.0, 30.0, 40.0, 50.0, 500.0, 60.0, 70.0, 80.0,
        -100.0, -120.0, -150.0, -200.0, -250.0, -30.0, -300.0, -40.0, -50.0, -60.0, -70.0, -80.0, 400.0,
    ];

    private static readonly double[] YOffsets =
    [
        0.0, 10.0, 100.0, 15.0, 150.0, 20.0, 200.0, 220.0, 230.0, 240.0, 25.0, 250.0, 270.0,
        30.0, 300.0, 35.0, 50.0, 65.0, -10.0, -100.0, -15.0, -150.0, -170.0, -180.0, -190.0,
        -20.0, -200.0, -210.0, -220.0, -230.0, -30.0, -300.0, -40.0, -45.0, -50.0, -55.0, -60.0,
    ];

    /// <summary>Number of window table entries (F1105 values 0..55).</summary>
    public static int WindowCount => WindowTable.Length;

    /// <summary>Texture slot and 1280 x 720 position of a window index (VA 0x2006C0).</summary>
    public static (int Slot, int X, int Y) WindowEntry(int index) => WindowTable[index];

    /// <summary>
    /// Arguments of the <c>_ZM</c> layout call (VA 0x9280B) and the HUD origin of the first message.
    /// <paramref name="window"/> = F1105 (F1109 overrides it in English when &gt; 0); 0..2 = the ordinary
    /// full-screen page, 3..55 = a speech bubble (style 2 via flag bit 20).
    /// </summary>
    /// <param name="lang">Text language: English starts at (30, 35), the others at (87, 49).</param>
    /// <param name="window">F1105 / F1109 value.</param>
    /// <param name="read">The message was read before.</param>
    /// <param name="readColorMode">Config +0x2A: 1 colours already-read messages 0xDC6D in the full-screen window.</param>
    /// <param name="f31">Script variable F31 (text width scale in 24ths when &gt; 0).</param>
    /// <param name="f32">Script variable F32 (text height scale).</param>
    public static BoxParams MainBoxParams(Contracts.Language lang, int window = 0, bool read = false, int readColorMode = 0, int f31 = 0, int f32 = 0)
    {
        bool normal = window < 3;
        double sx = f31 <= 0 ? 0.74 : (double)((float)f31 / 24f * (float)0.74);
        double sy = f32 <= 0 ? 0.74 : (double)((float)f32 / 24f * (float)0.74);
        int color = 0xFFFF;
        if (readColorMode == 1 && read)
        {
            color = normal ? 0xDC6D : 0xFFFF;
        }

        int flags = normal ? 0 : 1 << 20;
        (double x, double y) = lang == Contracts.Language.En ? (30.0, 35.0) : (87.0, 49.0);
        if (!normal)
        {
            (x, y) = BubbleTextOrigin(window);
        }

        return new BoxParams(Sx: sx, Sy: sy, Color: color, Flags: flags, X: x, Y: y, Window: window);
    }

    /// <summary>DISP_PARTS child that registration table VA 0x780C90 loads into texture <paramref name="slot"/>.</summary>
    public static int DispChildForSlot(int slot)
    {
        if (slot >= 22 && slot <= 24)
        {
            return 11 + (slot - 22);
        }

        if (slot >= 25 && slot <= 55)
        {
            return slot - 3;
        }

        throw new ArgumentOutOfRangeException(nameof(slot), $"slot {slot} is not a message-window slot");
    }

    /// <summary>
    /// The window picture drawn by the HUD (VA 0x540A5..0x54492; F822 == 1, F1104 == 0): index 0..2 is the
    /// full-screen 1920 x 1080 overlay with alpha opacity / 255 * fade, index 3..55 a speech bubble with alpha
    /// 0.784314 * fade. Null for an index past 55.
    /// </summary>
    public static WindowDraw? WindowFor(int index, int opacity = 255, double fade = 1.0, int xoffIdx = 0, int yoffIdx = 0)
    {
        if (index < 0 || index > 0x37)
        {
            return null;
        }

        (int slot, int tx, int ty) = WindowTable[index];
        double xo = xoffIdx >= 0 && xoffIdx <= 0x1A ? XOffsets[xoffIdx] : 0.0;
        double yo = yoffIdx >= 0 && yoffIdx <= 0x24 ? YOffsets[yoffIdx] : 0.0;
        bool bubble = index >= 3;
        (double cw, double ch) = index >= 0x21 && index <= 0x37 ? WindowSizes[index - 0x21] : (640.0, 448.0);
        double xCanvas = (tx + xo) * 640.0 / 1280.0;
        double yCanvas = (ty + yo) * 448.0 / 720.0;
        double a = (bubble ? 0.784314 : opacity / 255.0) * fade;
        return new WindowDraw(index, slot, DispChildForSlot(slot), xCanvas * 3.0, yCanvas * 1080.0 / 448.0, cw / 640.0, ch / 448.0, a, bubble);
    }

    /// <summary>Text origin of speech bubble <paramref name="index"/> in the 640 x 360 text space (VA 0x546CF..0x558B8).</summary>
    public static (double X, double Y) BubbleTextOrigin(int index, int xoffIdx = 0, int yoffIdx = 0)
    {
        (_, int tx, int ty) = WindowTable[index];
        (double dx, double dy) = TextOffsets[index];
        double xo = xoffIdx >= 0 && xoffIdx <= 0x1A ? XOffsets[xoffIdx] : 0.0;
        double yo = yoffIdx >= 0 && yoffIdx <= 0x24 ? YOffsets[yoffIdx] * 0.9578 : 0.0;
        return ((float)((tx + dx + xo) * 640.0 / 1280.0), (float)((ty + dy + yo) * 360.0 / 720.0));
    }

    /// <summary>The width the HUD adds to the end x of a message to get the position an <c>@x</c> message continues at (float32 at VA 0x1A713C and 0x20B25C: one full-width glyph advance).</summary>
    public const float ContinueGap = 16.52666664123535f;

    /// <summary>
    /// The position the next message of the page continues at after <paramref name="block"/>, for its <c>@x</c> (global VA 0x178BE20): the HUD's history loop (VA 0x55A02..0x55A3F) and
    /// the <c>_ZM</c> handler through 0x7C340 (VA 0x7C66C..0x7C683) call 0xCAD10, whose first output is the x of the last used glyph record (left-aligned text: the record's x in
    /// the block), and set the global to <c>16.5267 + x</c>. The first message of a page leaves it at 0 (the HUD clears it at the start of every frame, VA 0x53D32).
    /// </summary>
    public static float ContinueX(TextBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        float x = 0f;
        foreach (GlyphRecord r in block.Records)
        {
            // the loop skips a record whose first 8 bytes (element index and flags) are zero
            if (r.Index == 0 && r.Flags == 0)
            {
                continue;
            }

            x = r.Dx;
        }

        return ContinueGap + x;
    }

    /// <summary>Second output of VA 0xCAD10 as the HUD uses it: <c>int(y of the last line in the block)</c>.</summary>
    public static int LastLineOffset(TextBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.NLines <= 1)
        {
            return 0;
        }

        float acc = 0f;
        for (int i = 0; i < block.NLines - 1; i++)
        {
            acc += (float)block.Leading + block.LineHeight[i];
        }

        return (int)acc;
    }

    /// <summary>
    /// The y offset of the last line the block draw has revealed at <paramref name="head"/> (the second output of the HUD's 0xCA690 call at VA
    /// 0x53FC4): like <see cref="LastLineOffset"/>, but only up to the line of the last glyph whose counter is not above the head.
    /// </summary>
    public static int RevealedLastLineOffset(TextBlock block, double head)
    {
        ArgumentNullException.ThrowIfNull(block);
        int line = 0;
        float h = (float)head;
        foreach (GlyphRecord r in block.Records)
        {
            if ((r.Flags & 1) != 0 && r.Ctr <= h)
            {
                line = Math.Max(line, r.Line);
            }
        }

        line = Math.Min(line, block.NLines - 1);
        float acc = 0f;
        for (int i = 0; i < line; i++)
        {
            acc += (float)block.Leading + block.LineHeight[i];
        }

        return (int)acc;
    }

    /// <summary>
    /// Origins of the messages of one full-screen page (HUD VA 0x54733..0x54C2F, 0x558BD..0x55A6B). Message j
    /// (0-based) is drawn at <c>y_j = y0 + sum_{i&lt;j}(last_line_offset_i + 12 * s_i + 14) + sum_{i&lt;=j} C_i * (12 * s_i + 14)</c>,
    /// where C_i = <paramref name="blankBefore"/>[i] (script variable C847 as seen by message i; values at or above
    /// 0x21 mean C - 64, clamped to y &gt;= 49.82 / 35.41) and s_i = <paramref name="scaleY"/>[i] (C836 / 24, default 1).
    /// </summary>
    public static (double X, double Y)[] PagePositions(
        IReadOnlyList<TextBlock> blocks, Contracts.Language lang, IReadOnlyList<int>? blankBefore = null,
        IReadOnlyList<double>? scaleY = null, (double X, double Y)? origin = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        (double x0, double y0) = origin ?? (lang == Contracts.Language.En ? (30.0, 35.0) : (87.0, 49.0));
        var result = new (double X, double Y)[blocks.Count];
        float y = (float)y0;
        for (int j = 0; j < blocks.Count; j++)
        {
            float s = (float)(scaleY is not null && j < scaleY.Count ? scaleY[j] : 1.0);
            float step = s * 12f + 14f;
            int c = blankBefore is not null && j < blankBefore.Count ? blankBefore[j] : 0;
            if (c > 0)
            {
                if (c >= 0x21)
                {
                    y = (float)(c - 64) * step + y;
                    float floor = lang == Contracts.Language.En ? (float)35.411766 : (float)49.823528;
                    y = y >= floor ? y : floor;
                }
                else
                {
                    y = (float)c * step + y;
                }
            }

            result[j] = (x0, y);
            y = (float)LastLineOffset(blocks[j]) + s * 12f + 14f + y;
        }

        return result;
    }
}

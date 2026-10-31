using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Engine.Game;

/// <summary>Which text-break mark the HUD draws (docs/formats/textbox.md 7.1).</summary>
public enum ClickIconKind : byte
{
    /// <summary>Nothing drawn this frame.</summary>
    None,

    /// <summary>The line-end mark (a wait inside a page, F799 == 0): DISP_PARTS child 4, the waxing and waning crescent.</summary>
    Line,

    /// <summary>The page-end mark (the last message of a page, F799 == 1, or any wait outside page mode): DISP_PARTS child 9, the stacked lines, at half alpha.</summary>
    Page,

    /// <summary>The speech-bubble mark (window index 3..55): DISP_PARTS child 53.</summary>
    Bubble,
}

/// <summary>The click-wait icon as the HUD drew it in the last frame (package K34).</summary>
/// <param name="Kind">Which mark.</param>
/// <param name="DispChild">The DISP_PARTS child (4, 9 or 53).</param>
/// <param name="Frame">The animation cell drawn.</param>
/// <param name="X">Top-left x in text units (the 640 x 344.6 space of list 0x1E).</param>
/// <param name="Y">Top-left y in text units.</param>
/// <param name="Alpha">The colour alpha of the quad before the list alpha (icon alpha x window fade x 1 or 0.5).</param>
public readonly record struct ClickIconDraw(ClickIconKind Kind, int DispChild, int Frame, double X, double Y, double Alpha);

// The click-wait icon (docs/formats/textbox.md 7.1; GitHub issue #1): the alpha ramp of HUD VA 0x53F8B..0x5400C, the anchor after the last glyph
// (VA 0xCA690 at 0x53FC4, adjusted at 0x54B6A..0x54B80), the draw at VA 0x54CB1..0x54F94 with the story-page helper VA 0x53630. Every number here
// was read back from the eboot under emulation (scratch/k34/icon_emu.py, docs/status/knife/K34.md).
public sealed partial class GameHost
{
    /// <summary>Units per texel of the icon quads (float32 0.333 at VA 0x1A7094 / 0x1A7170): 1 px per texel in x, 1.0446 in y under list 0x1E.</summary>
    public const float ClickIconScale = 0.333f;

    /// <summary>DISP_PARTS child of UI texture slot 2 (registration table VA 0x780C90, record 0x780FC0): the line-end mark, 105 cells of 39 x 39 in a 4095 x 39 sheet.</summary>
    public const int LineIconChild = 4;

    /// <summary>DISP_PARTS child of UI slot 3 (record 0x780FD8): the page-end mark, 105 cells of 39 x 39; the last cell is blank and the table repeats cell 103.</summary>
    public const int PageIconChild = 9;

    /// <summary>DISP_PARTS child of UI slot 4 (record 0x780FF0): the speech-bubble mark, 59 cells of 43 x 46 in a 2537 x 46 sheet.</summary>
    public const int BubbleIconChild = 53;

    private float _iconAlpha;                           // VA 0xDF08E4
    private int _iconTick = -1;                         // VA 0x78122C (-1 in the data segment)
    private ClickAnchor _iconAnchor;                    // the outputs of 0xCA690 at VA 0x53FC4
    private float _iconStoredX;                         // VA 0xDF088C
    private float _iconStoredY;                         // VA 0xDF0890

    /// <summary>The click-wait icon alpha (VA 0xDF08E4): +0.1 per frame to 1 while the text is complete and the message waits for a click, else 0.</summary>
    public float ClickIconAlpha => _iconAlpha;

    /// <summary>The animation tick counter (VA 0x78122C): advanced by the frame delta only while an icon is drawn, modulo twice the icon's cell count; the cell is the half.</summary>
    public int ClickIconTick => _iconTick;

    /// <summary>The anchor after the last revealed glyph of the current message, as 0xCA690 returned it in the last frame.</summary>
    public ClickAnchor ClickIconAnchor => _iconAnchor;

    /// <summary>The click-icon position the HUD stores every frame (VA 0xDF088C / 0xDF0890): the anchor x after the window adjustment, and the anchor y plus the message's y.</summary>
    public (float X, float Y) ClickIconStored => (_iconStoredX, _iconStoredY);

    /// <summary>What the last frame drew for the icon (<see cref="ClickIconKind.None"/> when nothing).</summary>
    public ClickIconDraw LastClickIcon { get; private set; }

    /// <summary>The cell grid of a mark's sheet: the frame tables VA 0x781760 (slot 2), 0x781410 (slot 3) and 0x781230 (slot 4) step one cell per row.</summary>
    public static (int Cells, int CellW, int CellH) ClickIconCells(int dispChild) => dispChild switch
    {
        BubbleIconChild => (59, 43, 46),
        _ => (105, 39, 39),
    };

    /// <summary>
    /// The cell a frame table row names: row k of the three tables is the k-th cell, except row 104 of the page icon's table (VA 0x781410 + 8 * 104:
    /// <c>4017, 0, 4056, 39</c>), which repeats cell 103 because the 105th cell of DISP_PARTS child 9 is blank.
    /// </summary>
    public static int ClickIconCell(int dispChild, int row) => dispChild == PageIconChild && row == 104 ? 103 : row;

    private void ResetClickIcon()
    {
        // 0xDF08E4 has no writer but the HUD; it is set to 0 or raised every frame the window is visible, so a reset only removes a stale value
        _iconAlpha = 0f;
        _iconAnchor = default;
        LastClickIcon = default;
    }

    /// <summary>
    /// HUD VA 0x53F8B..0x5400C, once a frame while the window fade is positive: the anchor and the complete flag of the current message's block
    /// (0xCA690 with the head and the tail); when the text is complete and wait bit 0x400 is set the alpha rises by 0.1 to at most 1, otherwise it
    /// is 0. With the fade at 0 nothing changes.
    /// </summary>
    private void UpdateClickIcon()
    {
        if (!(Wait.WindowAlpha > 0f))
        {
            return;
        }

        bool complete = false;
        if (_current is { } m && m.Block is { Empty: false })
        {
            _iconAnchor = MessageWindow.ClickIconAnchor(m.Block, Wait.Head, Wait.Tail, _language);
            complete = _iconAnchor.Complete;
        }
        else
        {
            _iconAnchor = default;
        }

        if (complete && (Wait.Bits & WaitBits.Click) != 0)
        {
            float a = _iconAlpha + 0.1f;
            _iconAlpha = a > 1f ? 1f : a;
        }
        else
        {
            _iconAlpha = 0f;
        }
    }

    /// <summary>
    /// HUD VA 0x54B6A..0x54F94 after the current message's block draws (reached even when F1103 hides them, <c>jne 0x54CB1</c> at VA 0x54BE2).
    /// The anchor x moves with the window's text origin (<c>trunc(x + origin_x - 87)</c> for a speech bubble, <c>origin_x - origin_x</c> for the page), the
    /// position is stored, and while the alpha is positive and the anchor is valid (<c>x &gt;= 0 or y &gt; 0</c>) one quad is drawn into the text list:
    /// window 3..55 the bubble mark at <c>(x - 2, y + 2)</c>; F822 == 1 the story-page helper 0x53630 at <c>(x - 57 (English) - 3.7, y + 4.3)</c>, which
    /// picks the page mark at half alpha when F799 == 1 and the line mark otherwise; F822 != 1 the page mark at <c>(x + 115, y + 2)</c>, half alpha.
    /// Auto mode (mode word bit 0) draws nothing on any path. The tick counter advances by the frame delta modulo twice the cell count and the
    /// cell is its half; the quad is the cell at 0.333 units per texel with the colour alpha <c>icon alpha x fade x factor</c>, then the list alpha.
    /// </summary>
    private void DrawClickIcon(DisplayList list, (double X, double Y) origin, double offX, double offY)
    {
        int window = WindowIndex();
        float baseX = _language == Language.En ? 30f : 87f;
        float subtract = window >= 3 || F(1111) >= 3 ? 87f : baseX;
        float shift = (float)origin.X - subtract;
        // F822 != 1 jumps over the origin code (VA 0x54A37 -> 0x54BD8: the message at the bottom-window origin 169.333, 43.1407), so the anchor x stays as 0xCA690 gave it
        int xInt = F(822) == 1 ? (int)(shift + (float)_iconAnchor.X) : _iconAnchor.X;
        int yInt = _iconAnchor.Y;
        float msgY = (float)origin.Y;
        _iconStoredX = xInt;
        _iconStoredY = yInt + msgY;
        LastClickIcon = default;
        if (!(_iconAlpha > 0f) || !(xInt >= 0 || yInt > 0))
        {
            return;
        }

        float fade = Wait.WindowAlpha;
        ClickIconKind kind;
        int child;
        float x;
        float y;
        float factor;
        if (window > 2)
        {
            if (AutoMode)
            {
                return;
            }

            kind = ClickIconKind.Bubble;
            child = BubbleIconChild;
            x = xInt + -2f;
            y = (yInt + msgY) + 2f;
            factor = 1f;
        }
        else if (F(822) == 1)
        {
            x = ((_language == Language.En ? -57f : 0f) + xInt) + -3.7f;
            y = (yInt + msgY) + 4.3f;
            if (AutoMode)
            {
                return;
            }

            if (F(799) == 1)
            {
                kind = ClickIconKind.Page;
                child = PageIconChild;
                factor = 0.5f;
            }
            else
            {
                kind = ClickIconKind.Line;
                child = LineIconChild;
                factor = 1f;
            }
        }
        else
        {
            if (AutoMode)
            {
                return;
            }

            kind = ClickIconKind.Page;
            child = PageIconChild;
            x = xInt + 115f;
            y = (yInt + msgY) + 2f;
            factor = 0.5f;
        }

        (int cells, int cellW, int cellH) = ClickIconCells(child);
        _iconTick = (_iconTick + 1) % (2 * cells);
        int cell = ClickIconCell(child, _iconTick / 2);
        float alpha = _iconAlpha * fade * factor;
        // list 0x1E is drawn with the alpha 2 max(0, T - 0.5) x window fade x HUD alpha (text_vertical_scale.md 2.1), so the fade enters twice for the icon
        double listAlpha = fade * Math.Clamp(2.0 * (_textFade - 0.5), 0.0, 1.0);
        (int sw, int sh) = SheetSize(child);
        int cx = cell * cellW;
        double x0 = (x * TextScale) + offX;
        double y0 = (y * PageScaleY) + offY;
        double w = cellW * ClickIconScale * TextScale;
        double h = cellH * ClickIconScale * PageScaleY;
        AddQuad(
            list,
            TextureRef.ForUiTile(UiSet.DispParts, _language, child, WholeSheet),
            x0,
            y0,
            x0 + w,
            y0 + h,
            (float)cx / sw,
            0f,
            (float)(cx + cellW) / sw,
            (float)cellH / sh,
            Pack(255, 255, 255, alpha * listAlpha));
        LastClickIcon = new ClickIconDraw(kind, child, cell, x, y, alpha);
    }
}

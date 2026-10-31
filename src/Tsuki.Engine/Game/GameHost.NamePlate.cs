using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Engine.Game;

// The name plate (docs/formats/textbox.md 3.3 and 5.8, adv_state.md 3.2): text block 14 is laid out from the 0x1C bytes at VA 0x164FD40 by every _ZM
// (VA 0x926B1: width 150, height 20, spacing 2, leading 0, colour 0xF6FD, scale 0.5, bit 20 in a bubble window) and the HUD draws it above the current
// message of a page (VA 0x54813..0x5488C), moving that message down by the plate's last-line offset plus 14. The two writers of 0x164FD40 (VA 0x930C0
// and 0x937C0) have no callers in v01.01, so the plate is empty in every retail run and nothing is drawn; the plate is there for a record that carries
// plate bytes (a restore) and for the day a script command fills it. The bytes are mirrored into the history record (FillLive).
public sealed partial class GameHost
{
    private const int NamePlateSize = 0x1C;
    private const int NamePlateBlock = 14;

    private readonly byte[] _namePlate = new byte[NamePlateSize];
    private TextBlock? _namePlateBlock;

    /// <summary>The text of the name plate (the bytes at 0x164FD40 up to the first NUL); empty in retail.</summary>
    public ReadOnlySpan<byte> NamePlateText
    {
        get
        {
            int n = Array.IndexOf(_namePlate, (byte)0);
            return _namePlate.AsSpan(0, n < 0 ? NamePlateSize : n);
        }
    }

    /// <summary>True when a laid-out plate is drawn above the current message (the engine's 0x16541EC).</summary>
    public bool NamePlateShown => _namePlateBlock is not null;

    /// <summary>
    /// Sets the name plate (at most 0x1B bytes, NUL terminated) and lays it out as <c>_ZM</c> does; an empty text removes the plate. The next
    /// <c>_ZM</c> lays the stored text out again.
    /// </summary>
    public void SetNamePlate(ReadOnlySpan<byte> text)
    {
        Array.Clear(_namePlate);
        text[..Math.Min(text.Length, NamePlateSize - 1)].CopyTo(_namePlate);
        LayoutNamePlate();
    }

    /// <summary>The <c>Text_Layout(14, ...)</c> call of VA 0x926B1 over the stored bytes; the plate is shown when the first byte is not NUL.</summary>
    private void LayoutNamePlate()
    {
        ReadOnlySpan<byte> text = NamePlateText;
        if (text.IsEmpty)
        {
            _namePlateBlock = null;
            return;
        }

        int window = WindowIndex();
        var p = new LayoutParams(150, 20, 2, 0, 0xF6FD, window < 3 ? 0 : 1 << 20, 0.5, 0.5, NamePlateBlock, window);
        TextBlock block = TextLayout.Layout(text, _language, _fonts, p);
        _namePlateBlock = block.Empty ? null : block;
    }

    /// <summary>The plate is drawn only in a page (F822 != 0) while the window is not closed (F1103 == 0) (VA 0x547F7..0x54823).</summary>
    private TextBlock? ActiveNamePlate => _namePlateBlock is { } b && F(1103) == 0 && F(822) != 0 ? b : null;

    /// <summary>How far the current message moves down for the plate: the plate's last-line offset plus 14 (VA 0x54880).</summary>
    private double NamePlateShift() => ActiveNamePlate is { } b ? MessageWindow.LastLineOffset(b) + 14 : 0.0;

    private void DrawNamePlate(DisplayList list, double offsetXPx, double offsetYPx)
    {
        if (ActiveNamePlate is not { } block || _page.Count == 0)
        {
            return;
        }

        (double X, double Y)[] pos = PageOrigins();
        AddBlock(list, block, pos[^1].X, pos[^1].Y - NamePlateShift(), Wait.WindowAlpha, BlockDraw.RevealAll, BlockDraw.RevealAll, PageScaleY, offsetXPx, offsetYPx);
    }
}

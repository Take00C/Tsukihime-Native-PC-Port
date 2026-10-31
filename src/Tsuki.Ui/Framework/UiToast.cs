// A short notice of the PC port (package K21): "Screenshot saved", "Text copied to the clipboard". The PS4 has no such thing, so it is the port's own look (the pill
// palette of the Button Prompts row). Package K24 made it small and brief, in the bottom left corner: one short line in a low box above the menus' guide bar, where
// neither the mode panel (top left) nor the guide line's words (bottom right) sit, up for a second and a half with a quick fade in and out. A notice that carries
// something to read (an address, an error) may ask for the longer hold.
using Tsuki.Engine.Contracts;

namespace Tsuki.Ui;

/// <summary>A toast: one short line of text in a small rounded box at the bottom left, shown briefly.</summary>
public sealed class UiToast
{
    /// <summary>Frames a brief toast stays fully visible (1.2 s; with the fades the toast is on show for 1.5 s).</summary>
    public const int HoldFrames = 72;

    /// <summary>Frames a long toast stays fully visible (2.5 s): for a notice with an address or an error in it.</summary>
    public const int LongHoldFrames = 150;

    /// <summary>Frames of the fade-in.</summary>
    public const int FadeInFrames = 6;

    /// <summary>Frames of the fade-out.</summary>
    public const int FadeOutFrames = 12;

    private const float Margin = 40f;
    private const float Bottom = UiMetrics.Height - 72f;      // the box ends above the menus' guide bar (which starts at y 1023) and the message window's lowest line
    private const float PaddingX = 18f;
    private const float Height = 40f;
    private const float Border = 2f;
    private const double Scale = 0.32;                        // the key caps' scale: smaller than the pills' labels (0.36) and the headings (0.4)

    private readonly int _hold;
    private int _frame;

    internal UiToast(string text, bool brief = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        _hold = brief ? HoldFrames : LongHoldFrames;
    }

    /// <summary>The words.</summary>
    public string Text { get; }

    /// <summary>Frames this toast stays fully visible (<see cref="HoldFrames"/> or <see cref="LongHoldFrames"/>).</summary>
    public int Hold => _hold;

    /// <summary>The alpha 0..1 of this frame.</summary>
    public float Alpha => _frame < FadeInFrames
        ? _frame / (float)FadeInFrames
        : _frame < FadeInFrames + _hold
            ? 1f
            : MathF.Max(0f, 1f - ((_frame - FadeInFrames - _hold) / (float)FadeOutFrames));

    /// <summary>True once the toast has gone.</summary>
    public bool Done => _frame >= FadeInFrames + _hold + FadeOutFrames;

    internal void Step() => _frame++;

    /// <summary>The box the toast draws (the text's width plus the padding), for the tests and the layout of anything that must avoid it.</summary>
    public RectF Box(UiPainter p)
    {
        ArgumentNullException.ThrowIfNull(p);
        float w = UiPrompts.TextWidth(p, Text, Scale) + (2f * PaddingX);
        return new RectF(Margin, Bottom - Height, w, Height);
    }

    internal void Draw(UiPainter p)
    {
        float a = Alpha;
        if (a <= 0f)
        {
            return;
        }

        RectF r = Box(p);
        p.FillRounded(r, 10f, 0.62f, 0.86f, 1f, 0.8f * a);
        p.FillRounded(new RectF(r.X + Border, r.Y + Border, r.W - (2f * Border), r.H - (2f * Border)), 8f, 0.03f, 0.07f, 0.16f, 0.9f * a, smooth: false);
        UiPrompts.DrawText(p, Text, r.X + PaddingX, r.Y + (r.H / 2f), a, Scale);
    }
}

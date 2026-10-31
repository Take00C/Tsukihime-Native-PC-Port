namespace Tsuki.Ui;

/// <summary>What the pointer did to a <see cref="UiScrollBar"/> in one step.</summary>
/// <param name="Consumed">The bar used the pointer this step (the screen must not also hover or click what lies under it).</param>
/// <param name="Fraction">The thumb position 0..1 that a drag asks for, or null when no drag moved it.</param>
/// <param name="Page">-1 to scroll a page towards the start, +1 towards the end, 0 for none (a click on the track, repeating while the button is held).</param>
public readonly record struct ScrollBarUse(bool Consumed, float? Fraction, int Page);

/// <summary>
/// The mouse side of the lists' scroll bar (package K12): the PS4 draws a bar at x 1847 and a thumb that runs <c>travel</c> pixels from y 99 (<see cref="UiWidgets.ScrollBar"/>);
/// on the PS4 a touch on the thumb grabs it (knife <c>bar_grab</c>, sub_27974). On the PC the thumb is dragged with the left button, a click on the track above or below it
/// scrolls a page that way (and repeats while the button is held, until the thumb reaches the pointer), the thumb lights while the pointer is over it (the sheet holds the
/// grey and the lit thumb side by side), and the wheel keeps scrolling through the screens' own wheel code.
/// One instance per screen; the screen calls <see cref="Step"/> every frame with the thumb's current fraction and applies what it returns.
/// </summary>
public sealed class UiScrollBar
{
    /// <summary>Left edge of the area that grabs the pointer: wider than the 16 px track so the bar is easy to hit.</summary>
    public const float ZoneX = 1831f;

    /// <summary>Width of the grab area.</summary>
    public const float ZoneW = 48f;

    /// <summary>Top of the track area (the thumb's rest position).</summary>
    public const float ZoneTop = 99f;

    /// <summary>Height of the track area (the 888 px track plus the thumb's overhang).</summary>
    public const float ZoneH = 896f;

    /// <summary>The thumb sprite is 288 px tall; its lit part starts 8 px below the sprite's top and is 276 px tall.</summary>
    public const float ThumbHitTop = 8f;

    /// <summary>Height of the part of the thumb that grabs.</summary>
    public const float ThumbHitH = 276f;

    /// <summary>Frames a held track click waits before it repeats (the d-pad's repeat delay).</summary>
    public const int RepeatDelay = 24;

    /// <summary>Frames between repeats of a held track click.</summary>
    public const int RepeatPeriod = 6;

    private bool _drag;
    private float _grab;
    private int _page;
    private int _held;

    /// <summary>True while the thumb is being dragged or a track click is held.</summary>
    public bool Pressed => _drag || _page != 0;

    /// <summary>True while the thumb is being dragged.</summary>
    public bool Dragging => _drag;

    /// <summary>The pointer is over the thumb (or has it grabbed).</summary>
    public bool Hover { get; private set; }

    /// <summary>The top of the thumb sprite at <paramref name="fraction"/> of <paramref name="travel"/>.</summary>
    public static float ThumbTop(float fraction, float travel) => (Math.Clamp(fraction, 0f, 1f) * travel) + ZoneTop;

    /// <summary>The area in which the bar takes the pointer.</summary>
    public static RectF Zone => new(ZoneX, ZoneTop, ZoneW, ZoneH);

    /// <summary>The part of the thumb that grabs, at <paramref name="fraction"/> of <paramref name="travel"/>.</summary>
    public static RectF ThumbRect(float fraction, float travel) => new(ZoneX, ThumbTop(fraction, travel) + ThumbHitTop, ZoneW, ThumbHitH);

    /// <summary>Lets go of the thumb and any held track click (a screen that goes away or has nothing to scroll calls it).</summary>
    public void Release()
    {
        _drag = false;
        _page = 0;
        _held = 0;
        Hover = false;
    }

    /// <summary>
    /// One 60 Hz step of the bar.
    /// </summary>
    /// <param name="input">The step's input.</param>
    /// <param name="fraction">Where the thumb is now, 0..1.</param>
    /// <param name="travel">The pixels the thumb runs (608 or 610, as the screen draws it).</param>
    /// <param name="enabled">False when there is nothing to scroll (the bar is not drawn and takes nothing).</param>
    public ScrollBarUse Step(in UiInput input, float fraction, float travel, bool enabled)
    {
        if (!enabled || travel <= 0f)
        {
            Release();
            return default;
        }

        float top = ThumbTop(fraction, travel);
        RectF thumb = ThumbRect(fraction, travel);
        if (_drag)
        {
            if (input.PointerHeld)
            {
                Hover = true;
                return new ScrollBarUse(true, Math.Clamp((input.PointerY - _grab - ZoneTop) / travel, 0f, 1f), 0);
            }

            _drag = false;
        }

        if (_page != 0)
        {
            // the click on the track repeats while the button is down and the pointer still lies beyond the thumb on that side
            bool beyond = _page < 0 ? input.PointerY < thumb.Y : input.PointerY >= thumb.Bottom;
            if (!input.PointerHeld || !input.PointerActive || !beyond || !Zone.Contains(input.PointerX, input.PointerY))
            {
                _page = 0;
                _held = 0;
            }
            else
            {
                _held++;
                bool again = _held >= RepeatDelay && (_held - RepeatDelay) % RepeatPeriod == 0;
                Hover = false;
                return new ScrollBarUse(true, null, again ? _page : 0);
            }
        }

        Hover = input.Over(thumb);
        if (input.Click && input.PointerActive && Zone.Contains(input.PointerX, input.PointerY))
        {
            if (thumb.Contains(input.PointerX, input.PointerY))
            {
                _drag = true;
                _grab = input.PointerY - top;
                Hover = true;
                return new ScrollBarUse(true, null, 0);
            }

            _page = input.PointerY < thumb.Y ? -1 : 1;
            _held = 0;
            return new ScrollBarUse(true, null, _page);
        }

        return default;
    }
}

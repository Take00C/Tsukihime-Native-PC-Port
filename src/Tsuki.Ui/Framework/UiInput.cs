using Tsuki.Engine.Contracts;

namespace Tsuki.Ui;

/// <summary>
/// The input of one 60 Hz UI step in the terms the menus use (docs/formats/adv_state.md 10; knife <c>ui/pad.rs</c>): PS4 pad actions, the pointer in canvas pixels,
/// and the wheel. Keyboard and mouse reach the UI as <see cref="InputFrame"/> bits (Tsuki.Platform maps them), so there is one path.
/// </summary>
public readonly record struct UiInput
{
    /// <summary>The 60 Hz frame counter.</summary>
    public long Frame { get; init; }

    /// <summary>Decide was newly pressed, or the left button clicked: confirm the item under the cursor.</summary>
    public bool Decide { get; init; }

    /// <summary>Cancel was newly pressed, or the right button clicked: go back.</summary>
    public bool Cancel { get; init; }

    /// <summary>The left button was newly pressed (a click on whatever is under the pointer).</summary>
    public bool Click { get; init; }

    /// <summary>The right button was newly pressed.</summary>
    public bool RightClick { get; init; }

    /// <summary>The left button was held in the previous step and is up now (package K12: a click that was not a drag acts on release in the screens that also drag).</summary>
    public bool PointerReleased { get; init; }

    /// <summary>Up was pressed or auto-repeated (d-pad or left stick).</summary>
    public bool Up { get; init; }

    /// <summary>Down was pressed or auto-repeated.</summary>
    public bool Down { get; init; }

    /// <summary>Left was pressed or auto-repeated.</summary>
    public bool Left { get; init; }

    /// <summary>Right was pressed or auto-repeated.</summary>
    public bool Right { get; init; }

    /// <summary>Up was newly pressed (not a repeat).</summary>
    public bool UpPressed { get; init; }

    /// <summary>Down was newly pressed (not a repeat).</summary>
    public bool DownPressed { get; init; }

    /// <summary>Left was newly pressed (not a repeat).</summary>
    public bool LeftPressed { get; init; }

    /// <summary>Right was newly pressed (not a repeat).</summary>
    public bool RightPressed { get; init; }

    /// <summary>Decide is held (a held decide with left / right drags a config slider).</summary>
    public bool DecideHeld { get; init; }

    /// <summary>The left button is held over the screen.</summary>
    public bool PointerHeld { get; init; }

    /// <summary>L1 was newly pressed (previous page / tab).</summary>
    public bool L1 { get; init; }

    /// <summary>R1 was newly pressed (next page / tab).</summary>
    public bool R1 { get; init; }

    /// <summary>L2 was newly pressed.</summary>
    public bool L2 { get; init; }

    /// <summary>R2 was newly pressed.</summary>
    public bool R2 { get; init; }

    /// <summary>TRIANGLE (the north button) was newly pressed: the backlog button (knife X; the config controls picture says Log = TRIANGLE) and the save list's delete.</summary>
    public bool Triangle { get; init; }

    /// <summary>SQUARE (the west button) was newly pressed: the system menu button (knife Y; the config controls picture says System Menu = SQUARE).</summary>
    public bool Square { get; init; }

    /// <summary>OPTIONS was newly pressed.</summary>
    public bool Options { get; init; }

    /// <summary>Any pad button or a click was newly pressed (PRESS ANY BUTTON, notices).</summary>
    public bool Any { get; init; }

    /// <summary>The pointer is inside the game area.</summary>
    public bool PointerActive { get; init; }

    /// <summary>The pointer x in canvas pixels (to the pixel when the source reports it, else the centre of its 640 x 448 unit).</summary>
    public float PointerX { get; init; }

    /// <summary>The pointer y in canvas pixels.</summary>
    public float PointerY { get; init; }

    /// <summary>The pointer moved since the previous step (hover takes the cursor only then).</summary>
    public bool PointerMoved { get; init; }

    /// <summary>The middle button was newly pressed.</summary>
    public bool MiddleClick { get; init; }

    /// <summary>
    /// The wheel steps this frame, positive = up (away from the user). Page Up and Page Down add <see cref="PageSteps"/> steps up or down, so every list that
    /// scrolls with the wheel scrolls a page with them (package K9).
    /// </summary>
    public int Wheel { get; init; }

    /// <summary>The wheel steps a Page Up or Page Down press stands for.</summary>
    public const int PageSteps = 5;

    /// <summary>Delete was newly pressed: delete the data under the cursor (the pad's TRIANGLE does the same in the save list, as its guide says).</summary>
    public bool DeleteData { get; init; }

    /// <summary>The raw held buttons.</summary>
    public PadButtons Held { get; init; }

    /// <summary>The raw newly pressed buttons.</summary>
    public PadButtons Pressed { get; init; }

    /// <summary>The shell actions newly pressed (backlog, quick save, ...).</summary>
    public ShellActions Shell { get; init; }

    /// <summary>True when the pointer is over <paramref name="r"/> (canvas pixels).</summary>
    public bool Over(RectF r) => PointerActive && r.Contains(PointerX, PointerY);

    /// <summary>
    /// Builds the UI input from an engine input frame. <paramref name="previous"/> is the pointer of the previous step (for <see cref="PointerMoved"/>).
    /// Decide and cancel are as given; the face buttons follow the game's own controls picture (CONF_PARTS 16: System Menu = SQUARE, Log = TRIANGLE; it
    /// disagrees with adv_state.md 10.2 [H] on which of the two opens which); a stick press moves like the d-pad.
    /// </summary>
    public static UiInput From(in InputFrame f, PointerState previous)
    {
        const PadButtons upBits = PadButtons.Up | PadButtons.StickUp;
        const PadButtons downBits = PadButtons.Down | PadButtons.StickDown;
        const PadButtons leftBits = PadButtons.Left | PadButtons.StickLeft;
        const PadButtons rightBits = PadButtons.Right | PadButtons.StickRight;
        // the letter keys W, S, A and D are arrow keys in the menus (package K14): the same bits, from the frame's menu-only fields
        PadButtons rep = f.Repeated | f.Pressed | f.MenuRepeated | f.MenuPressed;
        PadButtons newly = f.Pressed | f.MenuPressed;
        PointerState p = f.Pointer;
        bool click = (p.Pressed & PointerButtons.Left) != 0;
        bool rclick = (p.Pressed & PointerButtons.Right) != 0;
        int wheel = p.Wheel;
        bool released = (previous.Held & PointerButtons.Left) != 0 && (p.Held & PointerButtons.Left) == 0;
        float px = float.IsNaN(p.CanvasX) ? (p.X + 0.5f) * UiMetrics.UnitX : p.CanvasX;
        float py = float.IsNaN(p.CanvasY) ? (p.Y + 0.5f) * UiMetrics.UnitY : p.CanvasY;
        bool moved = p.Active && (p.X != previous.X || p.Y != previous.Y
            || (!float.IsNaN(p.CanvasX) && !float.IsNaN(previous.CanvasX) && (MathF.Abs(p.CanvasX - previous.CanvasX) >= 1f || MathF.Abs(p.CanvasY - previous.CanvasY) >= 1f)));
        if ((f.ShellPressed & ShellActions.PageUp) != 0)
        {
            wheel += PageSteps;
        }

        if ((f.ShellPressed & ShellActions.PageDown) != 0)
        {
            wheel -= PageSteps;
        }

        const PadButtons anyPad = PadButtons.Decide | PadButtons.Cancel | PadButtons.Triangle | PadButtons.Square | PadButtons.L1 | PadButtons.R1 | PadButtons.L2 | PadButtons.R2
            | PadButtons.Options | upBits | downBits | leftBits | rightBits;
        return new UiInput
        {
            Frame = f.Frame,
            Decide = (f.Pressed & PadButtons.Decide) != 0 || click,
            Cancel = (f.Pressed & PadButtons.Cancel) != 0 || rclick,
            Click = click,
            RightClick = rclick,
            PointerReleased = released,
            Up = (rep & upBits) != 0,
            Down = (rep & downBits) != 0,
            Left = (rep & leftBits) != 0,
            Right = (rep & rightBits) != 0,
            UpPressed = (newly & upBits) != 0,
            DownPressed = (newly & downBits) != 0,
            LeftPressed = (newly & leftBits) != 0,
            RightPressed = (newly & rightBits) != 0,
            DecideHeld = (f.Held & PadButtons.Decide) != 0,
            PointerHeld = (p.Held & PointerButtons.Left) != 0,
            L1 = (f.Pressed & PadButtons.L1) != 0,
            R1 = (f.Pressed & PadButtons.R1) != 0,
            L2 = (f.Pressed & PadButtons.L2) != 0,
            R2 = (f.Pressed & PadButtons.R2) != 0,
            Triangle = (f.Pressed & PadButtons.Triangle) != 0,
            Square = (f.Pressed & PadButtons.Square) != 0,
            Options = (f.Pressed & PadButtons.Options) != 0,
            Any = (newly & anyPad) != 0 || click || rclick || f.ShellPressed != ShellActions.None,
            PointerActive = p.Active,
            PointerX = px,
            PointerY = py,
            PointerMoved = moved,
            MiddleClick = (p.Pressed & PointerButtons.Middle) != 0,
            Wheel = wheel,
            DeleteData = (f.ShellPressed & ShellActions.DeleteData) != 0,
            Held = f.Held,
            Pressed = f.Pressed,
            Shell = f.ShellPressed,
        };
    }

    /// <summary>An input frame with the pointer at canvas pixel (<paramref name="x"/>, <paramref name="y"/>), for tests and the preview tool.</summary>
    public static InputFrame PointerAt(float x, float y, bool click = false, long frame = 0) =>
        PointerFrame(x, y, click ? PointerButtons.Left : PointerButtons.None, click ? PointerButtons.Left : PointerButtons.None, 0, frame);

    /// <summary>
    /// An input frame with the pointer at canvas pixel (<paramref name="x"/>, <paramref name="y"/>), kept to the pixel, the given buttons held and newly pressed, and
    /// <paramref name="wheel"/> wheel steps (tests and the preview tool).
    /// </summary>
    public static InputFrame PointerFrame(float x, float y, PointerButtons held, PointerButtons pressed, int wheel = 0, long frame = 0) => new()
    {
        Frame = frame,
        Pointer = new PointerState(
            (short)Math.Clamp((int)(x / UiMetrics.UnitX), 0, 639), (short)Math.Clamp((int)(y / UiMetrics.UnitY), 0, 447), true, held, pressed, wheel, x, y),
    };
}

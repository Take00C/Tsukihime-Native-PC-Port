namespace Tsuki.Engine.Contracts;

/// <summary>
/// The PS4 game input word (docs/formats/adv_state.md 10.1, built by VA 0xE3A40). The 27 actions of
/// adv_state.md 10.2 test these bits; Tsuki.Platform maps keyboard, mouse and gamepad onto them
/// (ARCHITECTURE 9.4).
/// </summary>
[Flags]
public enum PadButtons : uint
{
    /// <summary>No button.</summary>
    None = 0,

    /// <summary>L2.</summary>
    L2 = 0x1,

    /// <summary>R2.</summary>
    R2 = 0x2,

    /// <summary>L1.</summary>
    L1 = 0x4,

    /// <summary>R1.</summary>
    R1 = 0x8,

    /// <summary>TRIANGLE.</summary>
    Triangle = 0x10,

    /// <summary>Decide (CIRCLE, or CROSS with the enter-button setting).</summary>
    Decide = 0x20,

    /// <summary>Cancel (the other of CIRCLE and CROSS).</summary>
    Cancel = 0x40,

    /// <summary>SQUARE.</summary>
    Square = 0x80,

    /// <summary>Never set by the pad; actions bound to it are unbound (adv_state.md 10.2).</summary>
    Unbound = 0x100,

    /// <summary>L3.</summary>
    L3 = 0x200,

    /// <summary>R3.</summary>
    R3 = 0x400,

    /// <summary>OPTIONS.</summary>
    Options = 0x800,

    /// <summary>D-pad up.</summary>
    Up = 0x1000,

    /// <summary>D-pad right.</summary>
    Right = 0x2000,

    /// <summary>D-pad down.</summary>
    Down = 0x4000,

    /// <summary>D-pad left.</summary>
    Left = 0x8000,

    /// <summary>Stick A up (axis beyond +-80).</summary>
    StickUp = 0x10000,

    /// <summary>Stick A right.</summary>
    StickRight = 0x20000,

    /// <summary>Stick A down.</summary>
    StickDown = 0x40000,

    /// <summary>Stick A left.</summary>
    StickLeft = 0x80000,

    /// <summary>Touch pad click.</summary>
    TouchPadClick = 0x100000,
}

/// <summary>Mouse buttons of the pointer (engine.md 8.5).</summary>
[Flags]
public enum PointerButtons : byte
{
    /// <summary>No button.</summary>
    None = 0,

    /// <summary>Left button.</summary>
    Left = 0x1,

    /// <summary>Right button.</summary>
    Right = 0x2,

    /// <summary>Middle button.</summary>
    Middle = 0x4,
}

/// <summary>
/// Our own player-shell actions that have no PS4 button in preset 0 or are keyboard conventions
/// (ARCHITECTURE 9.4). Window actions (fullscreen) stay in Tsuki.Platform.
/// </summary>
[Flags]
public enum ShellActions : uint
{
    /// <summary>No action.</summary>
    None = 0,

    /// <summary>Open the backlog (wheel up, Page Up).</summary>
    Backlog = 0x1,

    /// <summary>Rewind while held (Backspace).</summary>
    Rewind = 0x2,

    /// <summary>Fast-forward while held (Ctrl).</summary>
    FastForward = 0x4,

    /// <summary>Toggle latched skip (Tab).</summary>
    SkipToggle = 0x8,

    /// <summary>Toggle auto mode (A).</summary>
    AutoToggle = 0x10,

    /// <summary>Hide or show the message window (H).</summary>
    HideWindow = 0x20,

    /// <summary>Quick save (F5).</summary>
    QuickSave = 0x40,

    /// <summary>Quick load (F9).</summary>
    QuickLoad = 0x80,

    /// <summary>Next scene (N).</summary>
    NextScene = 0x100,

    /// <summary>Previous scene (B).</summary>
    PreviousScene = 0x200,

    /// <summary>Replay the voice (V).</summary>
    ReplayVoice = 0x400,

    /// <summary>Open the system menu from the story (Esc; the right mouse button sets the pointer's right button instead). Package K6.</summary>
    SystemMenu = 0x800,

    /// <summary>Delete the save slot under the cursor (Delete). Package K9: the pad's TRIANGLE does the same in the save list, the guide's "Delete Data".</summary>
    DeleteData = 0x1000,

    /// <summary>Page up in a list (Page Up): the menus scroll a page like five wheel steps. Package K9.</summary>
    PageUp = 0x2000,

    /// <summary>Page down in a list (Page Down): the menus scroll a page like five wheel steps. Package K9.</summary>
    PageDown = 0x4000,

    /// <summary>
    /// Some key of the keyboard went down, whatever it is (package K14). Not an action: it is what "PRESS ANY KEY", the boot notice, the caution screens and the title
    /// (which shows its logo and menu again on any input) read as "a button was pressed", so that a key the game binds to nothing counts too.
    /// </summary>
    AnyKey = 0x8000,
}

/// <summary>
/// The pointer in the PS4's 640 x 448 logical space (engine.md 8.5: x 0..639, y 0..447).
/// </summary>
/// <param name="X">Pointer x, 0..639.</param>
/// <param name="Y">Pointer y, 0..447.</param>
/// <param name="Active">True when the pointer is inside the game area and the current screen uses it (ARCHITECTURE 9.4).</param>
/// <param name="Held">Buttons held.</param>
/// <param name="Pressed">Buttons newly pressed this step.</param>
/// <param name="Wheel">Wheel steps this step, positive away from the user.</param>
/// <param name="CanvasX">The pointer x in canvas pixels (0..1920) when the source knows it more finely than <paramref name="X"/>; NaN when it does not (package K12: scroll bar and slider drags need more than the 640 x 448 units).</param>
/// <param name="CanvasY">The pointer y in canvas pixels (0..1080), or NaN.</param>
public readonly record struct PointerState(
    short X, short Y, bool Active, PointerButtons Held, PointerButtons Pressed, int Wheel, float CanvasX = float.NaN, float CanvasY = float.NaN);

/// <summary>
/// The input of one 60 Hz logic step (ARCHITECTURE 3.2, 3.4): the PS4 input words of
/// adv_state.md 10.1, the pointer and our shell actions. Logic reads input only from here, never
/// from the wall clock or a device (ARCHITECTURE 1.5).
/// </summary>
public readonly record struct InputFrame
{
    /// <summary>The 60 Hz frame counter of the step.</summary>
    public long Frame { get; init; }

    /// <summary>Buttons held (input word +0x10).</summary>
    public PadButtons Held { get; init; }

    /// <summary>Buttons newly pressed: held and not held in the previous step (+0x20).</summary>
    public PadButtons Pressed { get; init; }

    /// <summary>Newly pressed or d-pad auto-repeat (+0x18; repeat delay and period of engine.md 8.5).</summary>
    public PadButtons Repeated { get; init; }

    /// <summary>Analog R2 while held (+0x30).</summary>
    public short AnalogR2 { get; init; }

    /// <summary>Analog L2 while held (+0x32).</summary>
    public short AnalogL2 { get; init; }

    /// <summary>Stick A x (+0x34).</summary>
    public short StickAX { get; init; }

    /// <summary>Stick A y (+0x36).</summary>
    public short StickAY { get; init; }

    /// <summary>Stick B x (+0x38).</summary>
    public short StickBX { get; init; }

    /// <summary>Stick B y (+0x3A).</summary>
    public short StickBY { get; init; }

    /// <summary>The pointer.</summary>
    public PointerState Pointer { get; init; }

    /// <summary>Shell actions held.</summary>
    public ShellActions ShellHeld { get; init; }

    /// <summary>Shell actions newly pressed this step.</summary>
    public ShellActions ShellPressed { get; init; }

    /// <summary>
    /// The d-pad bits (<see cref="PadButtons.Up"/>, <see cref="PadButtons.Down"/>, <see cref="PadButtons.Left"/>, <see cref="PadButtons.Right"/> only) held through the letter keys
    /// W, S, A and D (package K14). They are aliases of the arrow keys <b>in the menus only</b>: the story never reads them, because there Up opens the mode panel and A toggles
    /// auto mode. <c>Tsuki.Ui</c> reads them with the d-pad bits.
    /// </summary>
    public PadButtons MenuHeld { get; init; }

    /// <summary>The letter-key direction bits newly pressed this step (see <see cref="MenuHeld"/>).</summary>
    public PadButtons MenuPressed { get; init; }

    /// <summary>The letter-key direction bits newly pressed or auto-repeated this step (see <see cref="MenuHeld"/>; the repeat delay and period are the d-pad's).</summary>
    public PadButtons MenuRepeated { get; init; }

    /// <summary>The newly pressed bits: <c>held &amp; ~previous</c> (adv_state.md 10.1).</summary>
    public static PadButtons Edges(PadButtons previous, PadButtons held) => held & ~previous;
}

// Keyboard, mouse and gamepad onto the PS4 input word (ARCHITECTURE 9.4; docs/formats/adv_state.md 10.1; K5a.md "input mapping"). The mapper is pure: it takes the
// keys held, the pad snapshot and the pointer of one step and returns the InputFrame, keeping only the previous step's bits (for newly pressed) and the d-pad
// repeat counters. PcInput feeds it from SDL; the verification mode feeds it from a script.
using Tsuki.Engine.Contracts;

namespace Tsuki.Platform.Input;

/// <summary>The keyboard keys the game binds (our own list, so the mapper does not depend on SDL scancodes).</summary>
public enum PcKey
{
    /// <summary>Enter.</summary>
    Enter,

    /// <summary>Keypad enter.</summary>
    KeypadEnter,

    /// <summary>Space.</summary>
    Space,

    /// <summary>Z.</summary>
    Z,

    /// <summary>X.</summary>
    X,

    /// <summary>Escape.</summary>
    Escape,

    /// <summary>Arrow up.</summary>
    Up,

    /// <summary>Arrow down.</summary>
    Down,

    /// <summary>Arrow left.</summary>
    Left,

    /// <summary>Arrow right.</summary>
    Right,

    /// <summary>Q.</summary>
    Q,

    /// <summary>E.</summary>
    E,

    /// <summary>Page up.</summary>
    PageUp,

    /// <summary>Page down.</summary>
    PageDown,

    /// <summary>Delete.</summary>
    Delete,

    /// <summary>Backspace.</summary>
    Backspace,

    /// <summary>Left control.</summary>
    LeftCtrl,

    /// <summary>Right control.</summary>
    RightCtrl,

    /// <summary>Tab.</summary>
    Tab,

    /// <summary>A.</summary>
    A,

    /// <summary>H.</summary>
    H,

    /// <summary>M.</summary>
    M,

    /// <summary>N.</summary>
    N,

    /// <summary>B.</summary>
    B,

    /// <summary>V.</summary>
    V,

    /// <summary>I.</summary>
    I,

    /// <summary>F5.</summary>
    F5,

    /// <summary>F9.</summary>
    F9,

    /// <summary>W (package K14: up in the menus).</summary>
    W,

    /// <summary>S (package K14: down in the menus).</summary>
    S,

    /// <summary>D (package K14: right in the menus).</summary>
    D,

    /// <summary>Some key is down, whatever it is, bound or not (package K14: <see cref="ShellActions.AnyKey"/>, for "PRESS ANY KEY").</summary>
    Other,
}

/// <summary>A set of held keys.</summary>
/// <param name="Bits">One bit per <see cref="PcKey"/>.</param>
public readonly record struct KeySet(ulong Bits)
{
    /// <summary>The empty set.</summary>
    public static KeySet None => default;

    /// <summary>The set holding <paramref name="keys"/>.</summary>
    public static KeySet Of(params PcKey[] keys)
    {
        ulong b = 0;
        foreach (PcKey k in keys)
        {
            b |= 1UL << (int)k;
        }

        return new KeySet(b);
    }

    /// <summary>True when <paramref name="key"/> is in the set.</summary>
    public bool Has(PcKey key) => (Bits & (1UL << (int)key)) != 0;

    /// <summary>The set with <paramref name="key"/> added.</summary>
    public KeySet With(PcKey key) => new(Bits | (1UL << (int)key));
}

/// <summary>The pointer of one step in the PS4 pointer space of 640 x 448 units.</summary>
/// <param name="X">0..639.</param>
/// <param name="Y">0..447.</param>
/// <param name="Inside">The pointer is inside the game area of a focused window.</param>
/// <param name="Held">Buttons held.</param>
/// <param name="Wheel">Wheel steps since the previous sample, positive away from the user.</param>
/// <param name="CanvasX">The pointer x in canvas pixels (0..1920), or NaN when the source only knows <paramref name="X"/> (package K12).</param>
/// <param name="CanvasY">The pointer y in canvas pixels (0..1080), or NaN.</param>
public readonly record struct PointerSample(short X, short Y, bool Inside, PointerButtons Held, int Wheel, float CanvasX = float.NaN, float CanvasY = float.NaN);

/// <summary>One documented binding for the README and the status file.</summary>
/// <param name="Input">The key, button or gesture.</param>
/// <param name="Action">What it does.</param>
public readonly record struct Binding(string Input, string Action);

/// <summary>The input mapping.</summary>
public sealed class InputMapper
{
    /// <summary>Frames a direction is held before it repeats.</summary>
    public const int RepeatDelay = 24;

    /// <summary>Frames between repeats.</summary>
    public const int RepeatPeriod = 6;

    private const int StickThreshold = 80;
    private const short TriggerOn = 8192;
    private readonly int[] _hold = new int[4];
    private readonly int[] _menuHold = new int[4];
    private PadButtons _previous;
    private PadButtons _previousMenu;
    private ShellActions _previousShell;
    private PointerButtons _previousPointer;

    /// <summary>The bindings, in the order the documents list them.</summary>
    public static IReadOnlyList<Binding> Bindings { get; } =
    [
        new("Enter / Space / Z / left click", "advance the text, decide in menus"),
        new("Esc / right click", "system menu in the story; back in menus"),
        new("X", "back in menus; hide or show the message window in the story"),
        new("Arrow keys", "move the cursor; Up opens the mode panel in the story"),
        new("W / A / S / D", "move the cursor in the menus (the same as the arrow keys; in the story they do not move anything: A toggles auto mode there)"),
        new("Q / E", "previous / next tab or page in menus (auto / skip toggles in the story)"),
        new("Ctrl (held)", "fast-forward"),
        new("Tab / middle click (wheel button)", "toggle latched skip"),
        new("A", "toggle auto mode"),
        new("H", "hide or show the message window"),
        new("Mouse wheel up / Page Up / I", "backlog (in the story)"),
        new("Mouse wheel / Page Up / Page Down", "scroll lists and pages in menus (a page is five wheel steps)"),
        new("Mouse: scroll bar, sliders, guide line words", "drag the thumb or a knob, click the track for a page, click Back / Confirm / Delete Data / the shoulder pair"),
        new("Mouse: pointer at the top / bottom edge", "opens the mode panel / hides the message window (story)"),
        new("Mouse: drag from the middle right / left", "fast-forward / rewind while held (story); pan the flowchart and the CG viewer"),
        new("Backspace (held)", "rewind (back in menus)"),
        new("F5 / F9", "quick save / quick load"),
        new("N / B", "next / previous scene"),
        new("V", "replay the voice"),
        new("M", "system menu (copy data in the save list)"),
        new("Delete", "delete the slot under the cursor in the save list"),
        new("Alt+Enter / F11", "fullscreen"),
        new("F12", "screenshot (a PNG at the window's size into Pictures\\Tsukihime; a short notice follows unless Config > System > Screenshot Notice is Off). Print Screen and its combinations are left to Windows"),
        new("Gamepad", "south = decide, east = cancel, west = system menu, north = backlog, shoulders = auto / skip, left trigger (held) = rewind, right trigger (held) = fast-forward, start = quick save, d-pad / left stick = cursor"),
    ];

    /// <summary>Builds the input of one step.</summary>
    /// <param name="frame">The 60 Hz frame counter.</param>
    /// <param name="keys">Keys held now.</param>
    /// <param name="pad">The gamepads now.</param>
    /// <param name="pointer">The pointer now.</param>
    public InputFrame Map(long frame, KeySet keys, in PadSnapshot pad, in PointerSample pointer)
    {
        PadButtons held = PadButtons.None;
        held |= Bit(keys.Has(PcKey.Enter) || keys.Has(PcKey.KeypadEnter) || keys.Has(PcKey.Space) || keys.Has(PcKey.Z) || pad.South, PadButtons.Decide);
        held |= Bit(keys.Has(PcKey.Escape) || keys.Has(PcKey.X) || pad.East, PadButtons.Cancel);
        held |= Bit(keys.Has(PcKey.Up) || pad.Up, PadButtons.Up);
        held |= Bit(keys.Has(PcKey.Down) || pad.Down, PadButtons.Down);
        held |= Bit(keys.Has(PcKey.Left) || pad.Left, PadButtons.Left);
        held |= Bit(keys.Has(PcKey.Right) || pad.Right, PadButtons.Right);
        held |= Bit(keys.Has(PcKey.Q) || pad.LeftShoulder, PadButtons.L1);
        held |= Bit(keys.Has(PcKey.E) || pad.RightShoulder, PadButtons.R1);
        held |= Bit(keys.Has(PcKey.Backspace) || pad.LeftTrigger >= TriggerOn, PadButtons.L2);
        held |= Bit(pad.RightTrigger >= TriggerOn, PadButtons.R2);
        // TRIANGLE is a pad button only: Page Up, I and Delete have shell actions of their own, so that a menu that reads TRIANGLE (the save list's delete
        // data, the backlog's close) is not triggered by the scroll keys
        held |= Bit(pad.North, PadButtons.Triangle);
        held |= Bit(keys.Has(PcKey.M) || pad.West, PadButtons.Square);
        held |= Bit(pad.Start, PadButtons.Options);
        held |= Bit(pad.LeftStick, PadButtons.L3);
        held |= Bit(pad.RightStick, PadButtons.R3);
        held |= Bit(pad.Back, PadButtons.TouchPadClick);
        short ax = (short)(pad.LeftX >> 8);
        short ay = (short)(pad.LeftY >> 8);
        held |= Bit(ay < -StickThreshold, PadButtons.StickUp);
        held |= Bit(ay > StickThreshold, PadButtons.StickDown);
        held |= Bit(ax < -StickThreshold, PadButtons.StickLeft);
        held |= Bit(ax > StickThreshold, PadButtons.StickRight);

        PadButtons pressed = held & ~_previous;
        PadButtons repeated = pressed;
        PadButtons[] dirs =
        [
            PadButtons.Up | PadButtons.StickUp, PadButtons.Down | PadButtons.StickDown,
            PadButtons.Left | PadButtons.StickLeft, PadButtons.Right | PadButtons.StickRight,
        ];
        for (int i = 0; i < dirs.Length; i++)
        {
            if ((held & dirs[i]) == 0)
            {
                _hold[i] = 0;
                continue;
            }

            _hold[i]++;
            if (_hold[i] > RepeatDelay && (_hold[i] - RepeatDelay) % RepeatPeriod == 0)
            {
                repeated |= held & dirs[i];
            }
        }

        // the letter keys are arrow keys in the menus only (package K14): the story never reads these bits, since there Up opens the mode panel and A toggles auto mode
        PadButtons menuHeld = PadButtons.None;
        menuHeld |= Bit(keys.Has(PcKey.W), PadButtons.Up);
        menuHeld |= Bit(keys.Has(PcKey.S), PadButtons.Down);
        menuHeld |= Bit(keys.Has(PcKey.A), PadButtons.Left);
        menuHeld |= Bit(keys.Has(PcKey.D), PadButtons.Right);
        PadButtons menuPressed = menuHeld & ~_previousMenu;
        PadButtons menuRepeated = menuPressed;
        for (int i = 0; i < dirs.Length; i++)
        {
            PadButtons bit = dirs[i] & (PadButtons.Up | PadButtons.Down | PadButtons.Left | PadButtons.Right);
            if ((menuHeld & bit) == 0)
            {
                _menuHold[i] = 0;
                continue;
            }

            _menuHold[i]++;
            if (_menuHold[i] > RepeatDelay && (_menuHold[i] - RepeatDelay) % RepeatPeriod == 0)
            {
                menuRepeated |= bit;
            }
        }

        _previousMenu = menuHeld;

        ShellActions shellHeld = ShellActions.None;
        shellHeld |= Shell(keys.Has(PcKey.PageUp) || keys.Has(PcKey.I) || pointer.Wheel > 0, ShellActions.Backlog);
        shellHeld |= Shell(keys.Has(PcKey.Backspace), ShellActions.Rewind);
        shellHeld |= Shell(keys.Has(PcKey.LeftCtrl) || keys.Has(PcKey.RightCtrl) || pad.RightTrigger >= TriggerOn, ShellActions.FastForward);
        shellHeld |= Shell(keys.Has(PcKey.Tab) || (pointer.Held & PointerButtons.Middle) != 0, ShellActions.SkipToggle);
        shellHeld |= Shell(keys.Has(PcKey.A), ShellActions.AutoToggle);
        shellHeld |= Shell(keys.Has(PcKey.H), ShellActions.HideWindow);
        shellHeld |= Shell(keys.Has(PcKey.F5) || pad.Start, ShellActions.QuickSave);
        shellHeld |= Shell(keys.Has(PcKey.F9), ShellActions.QuickLoad);
        shellHeld |= Shell(keys.Has(PcKey.N), ShellActions.NextScene);
        shellHeld |= Shell(keys.Has(PcKey.B), ShellActions.PreviousScene);
        shellHeld |= Shell(keys.Has(PcKey.V), ShellActions.ReplayVoice);
        shellHeld |= Shell(keys.Has(PcKey.Escape) || keys.Has(PcKey.M) || pad.West, ShellActions.SystemMenu);
        shellHeld |= Shell(keys.Has(PcKey.Delete), ShellActions.DeleteData);
        shellHeld |= Shell(keys.Has(PcKey.PageUp), ShellActions.PageUp);
        shellHeld |= Shell(keys.Has(PcKey.PageDown), ShellActions.PageDown);
        shellHeld |= Shell(keys.Has(PcKey.Other), ShellActions.AnyKey);
        ShellActions shellPressed = shellHeld & ~_previousShell;
        PointerButtons pointerPressed = pointer.Held & ~_previousPointer;
        _previous = held;
        _previousShell = shellHeld;
        _previousPointer = pointer.Held;
        return new InputFrame
        {
            Frame = frame,
            Held = held,
            Pressed = pressed,
            Repeated = repeated,
            AnalogR2 = (short)(pad.RightTrigger >= TriggerOn ? pad.RightTrigger >> 7 : 0),
            AnalogL2 = (short)(pad.LeftTrigger >= TriggerOn ? pad.LeftTrigger >> 7 : 0),
            StickAX = ax,
            StickAY = ay,
            StickBX = (short)(pad.RightX >> 8),
            StickBY = (short)(pad.RightY >> 8),
            Pointer = new PointerState(pointer.X, pointer.Y, pointer.Inside, pointer.Held, pointerPressed, pointer.Wheel, pointer.CanvasX, pointer.CanvasY),
            ShellHeld = shellHeld,
            ShellPressed = shellPressed,
            MenuHeld = menuHeld,
            MenuPressed = menuPressed,
            MenuRepeated = menuRepeated,
        };
    }

    private static PadButtons Bit(bool on, PadButtons b) => on ? b : PadButtons.None;

    private static ShellActions Shell(bool on, ShellActions a) => on ? a : ShellActions.None;
}

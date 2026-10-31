// The live input source (ARCHITECTURE 3.4 "SDL events -> InputFrame", 9.4): polls the keyboard, the mouse and the gamepads once per logic step and hands the
// state to the InputMapper. Wheel steps come from the event pump (AddWheel). Everything runs on the main thread.
using SDL;
using static SDL.SDL3;

namespace Tsuki.Platform.Input;

/// <summary>Produces the <see cref="Tsuki.Engine.Contracts.InputFrame"/> of each logic step.</summary>
public interface IInputSource
{
    /// <summary>The input of step <paramref name="frame"/>.</summary>
    Tsuki.Engine.Contracts.InputFrame Sample(long frame);

    /// <summary>The device the player used last (the button prompts follow it). Sources that are not a real device report the keyboard.</summary>
    InputDevice LastDevice => InputDevice.Keyboard;
}

/// <summary>Keyboard, mouse and gamepad input of a window.</summary>
public sealed unsafe class PcInput(GameWindow window, GamepadSet? pads) : IInputSource
{
    private readonly InputMapper _mapper = new();
    private readonly InputDeviceTracker _tracker = new();
    private int _wheel;

    /// <summary>The gamepads polled each step; null until the app has created the set (behind the first frame, package R1): the keyboard and the mouse work meanwhile.</summary>
    public GamepadSet? Pads { get; set; } = pads;

    /// <summary>The device the player used last: the keyboard (and the mouse) or the kind of the pad that was touched last.</summary>
    public InputDevice LastDevice => _tracker.Last;

    /// <summary>Adds wheel steps from a <see cref="PlatformEventKind.MouseWheel"/> event.</summary>
    public void AddWheel(int steps) => _wheel += steps;

    /// <inheritdoc/>
    public Tsuki.Engine.Contracts.InputFrame Sample(long frame)
    {
        KeySet keys = ReadKeys();
        GamepadSet? pads = Pads;
        PadSnapshot pad = pads?.Poll() ?? default;
        PointerSample pointer = ReadPointer();
        _wheel = 0;
        _tracker.Update(keys, pad, pointer, pads?.ActiveKind ?? InputDevice.XboxPad);
        return _mapper.Map(frame, keys, pad, pointer);
    }

    private static KeySet ReadKeys()
    {
        int count;
        byte* state = (byte*)SDL_GetKeyboardState(&count);
        return state == null ? KeySet.None : Classify(new ReadOnlySpan<byte>(state, count));
    }

    /// <summary>
    /// The keys the game sees in SDL's keyboard state (one byte per scancode, non-zero = held). A key held together with the Windows key is Windows' own (Win+Shift+S,
    /// Win+Print Screen, Win+G, Win+D ...; package K24): nothing counts then, so a Windows screenshot taken on PRESS ANY KEY or in a menu never presses or moves anything.
    /// Print Screen and F12 never count as "some key" either (K21 / K24: a screenshot of PRESS ANY KEY must not also press it).
    /// </summary>
    internal static KeySet Classify(ReadOnlySpan<byte> state)
    {
        if (Held(state, SDL_Scancode.SDL_SCANCODE_LGUI) || Held(state, SDL_Scancode.SDL_SCANCODE_RGUI))
        {
            return KeySet.None;
        }

        KeySet set = KeySet.None;
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_RETURN, PcKey.Enter);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_KP_ENTER, PcKey.KeypadEnter);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_SPACE, PcKey.Space);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_Z, PcKey.Z);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_X, PcKey.X);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_ESCAPE, PcKey.Escape);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_UP, PcKey.Up);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_DOWN, PcKey.Down);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_LEFT, PcKey.Left);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_RIGHT, PcKey.Right);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_Q, PcKey.Q);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_E, PcKey.E);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_PAGEUP, PcKey.PageUp);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_PAGEDOWN, PcKey.PageDown);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_DELETE, PcKey.Delete);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_BACKSPACE, PcKey.Backspace);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_LCTRL, PcKey.LeftCtrl);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_RCTRL, PcKey.RightCtrl);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_TAB, PcKey.Tab);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_A, PcKey.A);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_W, PcKey.W);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_S, PcKey.S);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_D, PcKey.D);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_H, PcKey.H);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_M, PcKey.M);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_N, PcKey.N);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_B, PcKey.B);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_V, PcKey.V);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_I, PcKey.I);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_F5, PcKey.F5);
        set = Add(set, state, SDL_Scancode.SDL_SCANCODE_F9, PcKey.F9);

        // any other key counts as "a key was pressed" (PRESS ANY KEY, the notices): the whole state array, bound or not, except the game's screenshot key and Windows'
        // (package K21: a screenshot of PRESS ANY KEY must not also press it; K24: Print Screen is Windows' key alone now, and still never "some key")
        for (int i = 0; i < state.Length; i++)
        {
            if (state[i] != 0 && i != (int)SDL_Scancode.SDL_SCANCODE_F12 && i != (int)SDL_Scancode.SDL_SCANCODE_PRINTSCREEN)
            {
                set = set.With(PcKey.Other);
                break;
            }
        }

        return set;
    }

    private static bool Held(ReadOnlySpan<byte> state, SDL_Scancode code) => (int)code < state.Length && state[(int)code] != 0;

    private static KeySet Add(KeySet set, ReadOnlySpan<byte> state, SDL_Scancode code, PcKey key) => Held(state, code) ? set.With(key) : set;

    private PointerSample ReadPointer()
    {
        float mx, my;
        SDL_MouseButtonFlags buttons = SDL_GetMouseState(&mx, &my);
        int ww, wh;
        SDL_GetWindowSize(window.Handle, &ww, &wh);
        (int pw, int ph) = window.PixelSize;
        float sx = ww > 0 ? pw / (float)ww : 1f;
        float sy = wh > 0 ? ph / (float)wh : 1f;
        Viewport view = Viewport.Fit(pw, ph);
        (bool inside, short x, short y) = view.ToPointer(mx * sx, my * sy);
        (float cx, float cy) = view.ToCanvas(mx * sx, my * sy);
        cx = Math.Clamp(cx, 0f, Viewport.CanvasWidth - 0.01f);
        cy = Math.Clamp(cy, 0f, Viewport.CanvasHeight - 0.01f);
        bool focused = (SDL_GetWindowFlags(window.Handle) & SDL_WindowFlags.SDL_WINDOW_MOUSE_FOCUS) != 0;
        var held = Tsuki.Engine.Contracts.PointerButtons.None;
        if ((buttons & SDL_MouseButtonFlags.SDL_BUTTON_LMASK) != 0)
        {
            held |= Tsuki.Engine.Contracts.PointerButtons.Left;
        }

        if ((buttons & SDL_MouseButtonFlags.SDL_BUTTON_RMASK) != 0)
        {
            held |= Tsuki.Engine.Contracts.PointerButtons.Right;
        }

        if ((buttons & SDL_MouseButtonFlags.SDL_BUTTON_MMASK) != 0)
        {
            held |= Tsuki.Engine.Contracts.PointerButtons.Middle;
        }

        return new PointerSample(x, y, inside && focused, held, _wheel, cx, cy);
    }
}

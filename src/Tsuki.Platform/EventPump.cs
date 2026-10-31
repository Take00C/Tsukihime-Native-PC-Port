// SDL event pump of the main thread (ARCHITECTURE 3.4 "SDL events -> InputFrame", 9.4). It handles
// the shell's own window actions itself (Alt+Enter / F11 fullscreen) and gamepad hot-plug, and
// hands the app a small list of platform events. Mapping keys and buttons onto the PS4 input word
// (InputFrame) is a later package.
using SDL;
using static SDL.SDL3;

namespace Tsuki.Platform;

/// <summary>What happened.</summary>
public enum PlatformEventKind
{
    /// <summary>The user closed the window or the OS asked the app to quit.</summary>
    QuitRequested,

    /// <summary>The drawable size changed (<see cref="PlatformEvent.Width"/> x <see cref="PlatformEvent.Height"/>).</summary>
    PixelSizeChanged,

    /// <summary>Alt+Enter or F11 asked for <see cref="PlatformEvent.Fullscreen"/>; <see cref="PlatformEvent.Accepted"/> is SDL's answer.</summary>
    FullscreenToggled,

    /// <summary>The window entered fullscreen.</summary>
    EnteredFullscreen,

    /// <summary>The window left fullscreen.</summary>
    LeftFullscreen,

    /// <summary>A gamepad was plugged in and opened (<see cref="PlatformEvent.Gamepad"/>).</summary>
    GamepadAdded,

    /// <summary>A gamepad was unplugged and closed (<see cref="PlatformEvent.Gamepad"/>).</summary>
    GamepadRemoved,

    /// <summary>A key went down (<see cref="PlatformEvent.Key"/> is SDL's key name), other than the fullscreen hotkeys.</summary>
    KeyDown,

    /// <summary>The mouse wheel turned (<see cref="PlatformEvent.Wheel"/> steps, positive away from the user).</summary>
    MouseWheel,

    /// <summary>The window lost the keyboard focus.</summary>
    FocusLost,

    /// <summary>The window gained the keyboard focus.</summary>
    FocusGained,

    /// <summary>F12 went down (packages K21 and K24): the app saves the frame it presents next as a picture.</summary>
    ScreenshotRequested,
}

/// <summary>One platform event.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Width">New drawable width (PixelSizeChanged).</param>
/// <param name="Height">New drawable height (PixelSizeChanged).</param>
/// <param name="Fullscreen">The requested state (FullscreenToggled).</param>
/// <param name="Accepted">SDL accepted the request (FullscreenToggled).</param>
/// <param name="Gamepad">The pad (GamepadAdded / GamepadRemoved).</param>
/// <param name="Key">SDL key name (KeyDown).</param>
/// <param name="Wheel">Wheel steps (MouseWheel).</param>
public readonly record struct PlatformEvent(
    PlatformEventKind Kind,
    int Width = 0,
    int Height = 0,
    bool Fullscreen = false,
    bool Accepted = false,
    GamepadInfo? Gamepad = null,
    string? Key = null,
    int Wheel = 0);

/// <summary>Polls SDL events on the main thread.</summary>
/// <param name="window">The game window (fullscreen hotkeys act on it), or null.</param>
/// <param name="gamepads">The gamepad set that hot-plug events open and close pads in, or null.</param>
public sealed unsafe class EventPump(GameWindow? window, GamepadSet? gamepads)
{
    /// <summary>The gamepad set hot-plug events open and close pads in; null until the app has created one (it does so behind the first frame, package R1).</summary>
    public GamepadSet? Gamepads { get; set; } = gamepads;

    /// <summary>Drains SDL's queue into <paramref name="sink"/>; returns the number of SDL events read.</summary>
    public int Pump(List<PlatformEvent> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        int n = 0;
        SDL_Event ev;
        while (SDL_PollEvent(&ev))
        {
            n++;
            Handle(ref ev, sink);
        }

        return n;
    }

    /// <summary>
    /// Queues a synthetic Alt+Enter key-down for <paramref name="target"/>, so the smoke check and the
    /// tests exercise the same path as the keyboard.
    /// </summary>
    public static void PushFullscreenHotkey(GameWindow target)
    {
        ArgumentNullException.ThrowIfNull(target);
        SDL_Event ev = default;
        ev.key.type = SDL_EventType.SDL_EVENT_KEY_DOWN;
        ev.key.windowID = (SDL_WindowID)target.WindowId;
        ev.key.scancode = SDL_Scancode.SDL_SCANCODE_RETURN;
        ev.key.key = SDL_Keycode.SDLK_RETURN;
        ev.key.mod = SDL_Keymod.SDL_KMOD_LALT;
        ev.key.down = true;
        ev.key.repeat = false;
        if (!SDL_PushEvent(&ev))
        {
            throw new PlatformException("SDL_PushEvent: " + SDL_GetError());
        }
    }

    /// <summary>
    /// Queues a window close request for <paramref name="target"/> (what the title bar's close button and Alt+F4 produce), so the verification scripts and the tests
    /// exercise the same path as the window manager (package K21).
    /// </summary>
    public static void PushCloseRequest(GameWindow target)
    {
        ArgumentNullException.ThrowIfNull(target);
        SDL_Event ev = default;
        ev.window.type = SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED;
        ev.window.windowID = (SDL_WindowID)target.WindowId;
        if (!SDL_PushEvent(&ev))
        {
            throw new PlatformException("SDL_PushEvent: " + SDL_GetError());
        }
    }

    /// <summary>Queues a synthetic F12 key-down for <paramref name="target"/> (the screenshot key; package K21).</summary>
    public static void PushScreenshotHotkey(GameWindow target)
    {
        ArgumentNullException.ThrowIfNull(target);
        SDL_Event ev = default;
        ev.key.type = SDL_EventType.SDL_EVENT_KEY_DOWN;
        ev.key.windowID = (SDL_WindowID)target.WindowId;
        ev.key.scancode = SDL_Scancode.SDL_SCANCODE_F12;
        ev.key.key = SDL_Keycode.SDLK_F12;
        ev.key.down = true;
        ev.key.repeat = false;
        if (!SDL_PushEvent(&ev))
        {
            throw new PlatformException("SDL_PushEvent: " + SDL_GetError());
        }
    }

    /// <summary>
    /// True for F12 alone, ignoring key repeat (package K21; K24 took Print Screen out). Print Screen and its combinations (Win+Print Screen, Alt+Print Screen, Win+Shift+S)
    /// belong to Windows: the player's own screenshot must not also take the game's and show its notice.
    /// </summary>
    internal static bool IsScreenshotHotkey(SDL_Keycode key, bool repeat) => !repeat && key == SDL_Keycode.SDLK_F12;

    /// <summary>True for Alt+Enter (either Enter key) and F11, ignoring key repeat (ARCHITECTURE 9.4).</summary>
    internal static bool IsFullscreenHotkey(SDL_Keycode key, SDL_Keymod mod, bool repeat)
    {
        if (repeat)
        {
            return false;
        }

        if (key == SDL_Keycode.SDLK_F11)
        {
            return true;
        }

        return (key == SDL_Keycode.SDLK_RETURN || key == SDL_Keycode.SDLK_KP_ENTER) && (mod & SDL_Keymod.SDL_KMOD_ALT) != 0;
    }

    private void Handle(ref SDL_Event ev, List<PlatformEvent> sink)
    {
        switch ((SDL_EventType)ev.type)
        {
            case SDL_EventType.SDL_EVENT_QUIT:
            case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                sink.Add(new PlatformEvent(PlatformEventKind.QuitRequested));
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                sink.Add(new PlatformEvent(PlatformEventKind.PixelSizeChanged, ev.window.data1, ev.window.data2));
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_ENTER_FULLSCREEN:
                sink.Add(new PlatformEvent(PlatformEventKind.EnteredFullscreen, Fullscreen: true));
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_LEAVE_FULLSCREEN:
                sink.Add(new PlatformEvent(PlatformEventKind.LeftFullscreen, Fullscreen: false));
                break;
            case SDL_EventType.SDL_EVENT_KEY_DOWN:
                if (window != null && IsFullscreenHotkey(ev.key.key, ev.key.mod, ev.key.repeat))
                {
                    bool accepted = window.ToggleFullscreen();
                    sink.Add(new PlatformEvent(PlatformEventKind.FullscreenToggled, Fullscreen: window.FullscreenRequested, Accepted: accepted));
                }
                else if (window != null && IsScreenshotHotkey(ev.key.key, ev.key.repeat))
                {
                    sink.Add(new PlatformEvent(PlatformEventKind.ScreenshotRequested, Key: SDL_GetKeyName(ev.key.key) ?? ""));
                }
                else
                {
                    sink.Add(new PlatformEvent(PlatformEventKind.KeyDown, Key: SDL_GetKeyName(ev.key.key) ?? ""));
                }

                break;
            case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                sink.Add(new PlatformEvent(PlatformEventKind.MouseWheel, Wheel: (int)Math.Round(ev.wheel.y)));
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
                sink.Add(new PlatformEvent(PlatformEventKind.FocusLost));
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED:
                sink.Add(new PlatformEvent(PlatformEventKind.FocusGained));
                break;
            case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED:
                if (Gamepads is { } addTo && addTo.Open((uint)ev.gdevice.which, out GamepadInfo? added))
                {
                    sink.Add(new PlatformEvent(PlatformEventKind.GamepadAdded, Gamepad: added));
                }

                break;
            case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED:
                if (Gamepads is { } removeFrom && removeFrom.Close((uint)ev.gdevice.which, out GamepadInfo? removed))
                {
                    sink.Add(new PlatformEvent(PlatformEventKind.GamepadRemoved, Gamepad: removed));
                }

                break;
        }
    }
}

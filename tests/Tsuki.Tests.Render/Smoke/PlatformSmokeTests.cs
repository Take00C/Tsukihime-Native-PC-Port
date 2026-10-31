using SDL;
using Tsuki.Platform;
using Tsuki.Platform.Input;

namespace Tsuki.Tests.Render.Smoke;

/// <summary>
/// The app skeleton's platform paths (PACE 7.2 #1, H1; ARCHITECTURE 9.4): gamepad hot-plug through
/// SDL's own device events (a virtual DualShock 4) and the Alt+Enter / F11 fullscreen hotkeys through
/// the event pump.
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class PlatformSmokeTests
{
    [Fact]
    public void VirtualGamepadHotPlugGoesThroughTheEventPump()
    {
        using GamepadSet pads = GamepadSet.Create();
        var pump = new EventPump(null, pads);
        var events = new List<PlatformEvent>();
        int before = pads.Connected.Count;

        uint id = VirtualGamepad.Attach();
        PlatformEvent added = PumpUntil(pump, events, PlatformEventKind.GamepadAdded, id);
        Assert.Equal(before + 1, pads.Connected.Count);
        GamepadInfo g = added.Gamepad!;
        Assert.Equal("Tsuki virtual DualShock 4", g.Name);
        Assert.Equal(VirtualGamepad.SonyVendorId, g.VendorId);
        Assert.Equal(VirtualGamepad.DualShock4ProductId, g.ProductId);
        Assert.Equal("ps4", g.Type);
        Assert.Equal("Cross", g.SouthLabel);
        Assert.Equal("Circle", g.EastLabel);

        VirtualGamepad.Detach(id);
        PlatformEvent removed = PumpUntil(pump, events, PlatformEventKind.GamepadRemoved, id);
        Assert.Equal(id, removed.Gamepad!.InstanceId);
        Assert.Equal(before, pads.Connected.Count);
        Assert.DoesNotContain(pads.Connected, p => p.InstanceId == id);
    }

    [Fact]
    public void AltEnterTogglesFullscreenThroughTheEventPump()
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("Tsuki.Tests.Render hotkeys", 320, 180, Hidden: true, Resizable: false));
        var pump = new EventPump(window, null);
        var events = new List<PlatformEvent>();
        pump.Pump(events); // drop window-creation events
        Assert.False(window.FullscreenRequested);

        EventPump.PushFullscreenHotkey(window);
        events.Clear();
        pump.Pump(events);
        PlatformEvent on = Assert.Single(events, e => e.Kind == PlatformEventKind.FullscreenToggled);
        Assert.True(on.Fullscreen);
        Assert.True(on.Accepted);
        Assert.True(window.FullscreenRequested);
        Assert.DoesNotContain(events, e => e.Kind == PlatformEventKind.KeyDown);

        EventPump.PushFullscreenHotkey(window);
        events.Clear();
        pump.Pump(events);
        PlatformEvent off = Assert.Single(events, e => e.Kind == PlatformEventKind.FullscreenToggled);
        Assert.False(off.Fullscreen);
        Assert.True(off.Accepted);
        Assert.False(window.FullscreenRequested);
        window.Sync();
        Assert.False(window.IsFullscreen);
    }

    [Fact]
    public void F12AndTheCloseRequestGoThroughTheEventPump()
    {
        // package K21: the screenshot key is its own platform event (not a KeyDown), and a pushed close request is what the title bar's button produces
        using GameWindow window = GameWindow.Create(new WindowOptions("Tsuki.Tests.Render screenshot key", 320, 180, Hidden: true, Resizable: false));
        var pump = new EventPump(window, null);
        var events = new List<PlatformEvent>();
        pump.Pump(events);

        EventPump.PushScreenshotHotkey(window);
        events.Clear();
        pump.Pump(events);
        PlatformEvent shot = Assert.Single(events, e => e.Kind == PlatformEventKind.ScreenshotRequested);
        Assert.Equal("F12", shot.Key);
        Assert.DoesNotContain(events, e => e.Kind == PlatformEventKind.KeyDown);
        Assert.DoesNotContain(events, e => e.Kind == PlatformEventKind.FullscreenToggled);

        EventPump.PushCloseRequest(window);
        events.Clear();
        pump.Pump(events);
        Assert.Single(events, e => e.Kind == PlatformEventKind.QuitRequested);
    }

    [Theory]
    [InlineData(SDL_Keycode.SDLK_F12, false, true)]
    [InlineData(SDL_Keycode.SDLK_PRINTSCREEN, false, false)]
    [InlineData(SDL_Keycode.SDLK_F12, true, false)]
    [InlineData(SDL_Keycode.SDLK_F11, false, false)]
    [InlineData(SDL_Keycode.SDLK_RETURN, false, false)]
    [InlineData(SDL_Keycode.SDLK_S, false, false)]
    public void TheScreenshotHotkeyIsF12AloneWithoutRepeat(SDL_Keycode key, bool repeat, bool expected)
    {
        // package K24: Print Screen (alone, with Alt, with Win) and Win+Shift+S are Windows' own screenshot keys; the game's is F12 only
        Assert.Equal(expected, EventPump.IsScreenshotHotkey(key, repeat));
    }

    [Fact]
    public void PrintScreenAndF12AreNeverSomeKeyAndAKeyHeldWithTheWindowsKeyIsWindowsOwn()
    {
        // package K24: the keyboard state as PcInput reads it (one byte per scancode)
        static byte[] State(params SDL_Scancode[] held)
        {
            var s = new byte[(int)SDL_Scancode.SDL_SCANCODE_COUNT];
            foreach (SDL_Scancode code in held)
            {
                s[(int)code] = 1;
            }

            return s;
        }

        Assert.Equal(KeySet.None, PcInput.Classify(State()));
        Assert.Equal(KeySet.None, PcInput.Classify(State(SDL_Scancode.SDL_SCANCODE_PRINTSCREEN)));                                                   // Print Screen: Windows' own
        Assert.Equal(KeySet.None, PcInput.Classify(State(SDL_Scancode.SDL_SCANCODE_F12)));                                                           // F12: the pump's event, not "some key"
        Assert.Equal(KeySet.Of(PcKey.Other), PcInput.Classify(State(SDL_Scancode.SDL_SCANCODE_LALT, SDL_Scancode.SDL_SCANCODE_PRINTSCREEN)));          // Alt+Print Screen: the Alt is a key like any other, Print Screen still is not
        Assert.Equal(KeySet.Of(PcKey.Enter, PcKey.Other), PcInput.Classify(State(SDL_Scancode.SDL_SCANCODE_RETURN)));
        Assert.Equal(KeySet.Of(PcKey.S, PcKey.Other), PcInput.Classify(State(SDL_Scancode.SDL_SCANCODE_S, SDL_Scancode.SDL_SCANCODE_LSHIFT)));          // Shift+S alone: S moves the cursor in menus
        // with the Windows key held nothing counts (Win+Shift+S, Win+Print Screen, Win+G, Win+D ...): a Windows screenshot never presses PRESS ANY KEY or moves a menu cursor
        Assert.Equal(KeySet.None, PcInput.Classify(State(SDL_Scancode.SDL_SCANCODE_LGUI, SDL_Scancode.SDL_SCANCODE_LSHIFT, SDL_Scancode.SDL_SCANCODE_S)));
        Assert.Equal(KeySet.None, PcInput.Classify(State(SDL_Scancode.SDL_SCANCODE_RGUI, SDL_Scancode.SDL_SCANCODE_PRINTSCREEN)));
        Assert.Equal(KeySet.None, PcInput.Classify(State(SDL_Scancode.SDL_SCANCODE_LGUI, SDL_Scancode.SDL_SCANCODE_RETURN)));
        // a short state array (fewer scancodes than the game asks about) is read without running off its end
        Assert.Equal(KeySet.Of(PcKey.A, PcKey.Other), PcInput.Classify(State(SDL_Scancode.SDL_SCANCODE_A).AsSpan(0, 8)));
    }

    [Theory]
    [InlineData(SDL_Keycode.SDLK_RETURN, SDL_Keymod.SDL_KMOD_LALT, false, true)]
    [InlineData(SDL_Keycode.SDLK_RETURN, SDL_Keymod.SDL_KMOD_RALT, false, true)]
    [InlineData(SDL_Keycode.SDLK_KP_ENTER, SDL_Keymod.SDL_KMOD_LALT, false, true)]
    [InlineData(SDL_Keycode.SDLK_F11, SDL_Keymod.SDL_KMOD_NONE, false, true)]
    [InlineData(SDL_Keycode.SDLK_RETURN, SDL_Keymod.SDL_KMOD_NONE, false, false)]
    [InlineData(SDL_Keycode.SDLK_RETURN, SDL_Keymod.SDL_KMOD_LCTRL, false, false)]
    [InlineData(SDL_Keycode.SDLK_RETURN, SDL_Keymod.SDL_KMOD_LALT, true, false)]
    [InlineData(SDL_Keycode.SDLK_F11, SDL_Keymod.SDL_KMOD_NONE, true, false)]
    [InlineData(SDL_Keycode.SDLK_ESCAPE, SDL_Keymod.SDL_KMOD_LALT, false, false)]
    public void FullscreenHotkeysAreAltEnterAndF11WithoutRepeat(SDL_Keycode key, SDL_Keymod mod, bool repeat, bool expected)
    {
        Assert.Equal(expected, EventPump.IsFullscreenHotkey(key, mod, repeat));
    }

    private static PlatformEvent PumpUntil(EventPump pump, List<PlatformEvent> events, PlatformEventKind kind, uint id)
    {
        long deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            events.Clear();
            pump.Pump(events);
            foreach (PlatformEvent e in events)
            {
                if (e.Kind == kind && e.Gamepad?.InstanceId == id)
                {
                    return e;
                }
            }

            Thread.Sleep(5);
        }

        Assert.Fail($"no {kind} event for gamepad {id} within 3 s");
        return default;
    }
}

// Gamepad hot-plug (ARCHITECTURE 9.4: gamepads hot-plug, PlayStation labels through
// SDL_GetGamepadButtonLabel). The set opens a pad on SDL_EVENT_GAMEPAD_ADDED and closes it on
// SDL_EVENT_GAMEPAD_REMOVED; EventPump routes the events here. Button mapping onto the PS4 input
// word is a later package.
using System.Runtime.InteropServices;
using SDL;
using static SDL.SDL3;
using Tsuki.Platform.Input;

namespace Tsuki.Platform;

/// <summary>One open gamepad.</summary>
/// <param name="InstanceId">SDL joystick instance id (unique for the session).</param>
/// <param name="Name">Device name.</param>
/// <param name="Type">SDL gamepad type string ("ps4", "xboxone", ...).</param>
/// <param name="SouthLabel">Label of the bottom face button ("Cross" on PlayStation pads).</param>
/// <param name="EastLabel">Label of the right face button ("Circle" on PlayStation pads).</param>
/// <param name="VendorId">USB vendor id.</param>
/// <param name="ProductId">USB product id.</param>
public sealed record GamepadInfo(uint InstanceId, string Name, string Type, string SouthLabel, string EastLabel, ushort VendorId, ushort ProductId);

/// <summary>The gamepads currently connected, in connection order. Use it on the main thread.</summary>
public sealed unsafe class GamepadSet : IDisposable
{
    private readonly Dictionary<uint, nint> _handles = [];
    private readonly Dictionary<uint, InputDevice> _kinds = [];
    private readonly List<GamepadInfo> _connected = [];
    private bool _disposed;

    private GamepadSet()
    {
    }

    /// <summary>The connected gamepads, oldest first.</summary>
    public IReadOnlyList<GamepadInfo> Connected => _connected;

    /// <summary>
    /// The kind of the pad that was plugged in last or had button, stick or trigger activity last (the button prompts follow it): a PlayStation pad shows
    /// PlayStation prompts, every other pad Xbox prompts (package K9).
    /// </summary>
    public InputDevice ActiveKind { get; private set; } = InputDevice.XboxPad;

    /// <summary>Initialises the gamepad subsystem and opens every pad already connected.</summary>
    public static GamepadSet Create()
    {
        Sdl.Acquire(PlatformSubsystems.Gamepad);
        var set = new GamepadSet();
        using var ids = SDL_GetGamepads();
        if (ids != null)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                set.Open((uint)ids[i], out _);
            }
        }

        return set;
    }

    /// <summary>
    /// Opens the pad (on SDL_EVENT_GAMEPAD_ADDED). Returns true and the new entry when it was not open
    /// yet; false when it already was or SDL could not open it (<paramref name="info"/> is then null).
    /// </summary>
    public bool Open(uint instanceId, out GamepadInfo? info)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        info = null;
        if (_handles.ContainsKey(instanceId))
        {
            return false;
        }

        SDL_Gamepad* pad = SDL_OpenGamepad((SDL_JoystickID)instanceId);
        if (pad == null)
        {
            return false;
        }

        _handles[instanceId] = (nint)pad;
        info = new GamepadInfo(
            instanceId,
            SDL_GetGamepadName(pad) ?? "",
            SDL_GetGamepadStringForType(SDL_GetGamepadType(pad)) ?? "unknown",
            Label(SDL_GetGamepadButtonLabel(pad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH)),
            Label(SDL_GetGamepadButtonLabel(pad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST)),
            SDL_GetGamepadVendor(pad),
            SDL_GetGamepadProduct(pad));
        _connected.Add(info);
        InputDevice kind = PadKinds.FromType(info.Type, info.VendorId);
        _kinds[instanceId] = kind;
        ActiveKind = kind;
        return true;
    }

    /// <summary>Closes the pad (on SDL_EVENT_GAMEPAD_REMOVED). False when it was not open.</summary>
    public bool Close(uint instanceId, out GamepadInfo? info)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        info = _connected.FirstOrDefault(g => g.InstanceId == instanceId);
        if (!_handles.Remove(instanceId, out nint pad))
        {
            return false;
        }

        SDL_CloseGamepad((SDL_Gamepad*)pad);
        _connected.RemoveAll(g => g.InstanceId == instanceId);
        _kinds.Remove(instanceId);
        if (_connected.Count > 0)
        {
            ActiveKind = _kinds[_connected[^1].InstanceId];
        }

        return true;
    }

    /// <summary>
    /// Reads every open pad now and merges them (buttons OR-ed, triggers and sticks of the largest magnitude): the state the input mapper turns into the
    /// PS4 input word. Face buttons are positional (south = decide), whatever the pad's labels say.
    /// </summary>
    public PadSnapshot Poll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PadSnapshot s = default;
        foreach ((uint id, nint h) in _handles)
        {
            SDL_Gamepad* p = (SDL_Gamepad*)h;
            var one = new PadSnapshot(
                South: SDL_GetGamepadButton(p, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH),
                East: SDL_GetGamepadButton(p, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST),
                West: SDL_GetGamepadButton(p, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST),
                North: SDL_GetGamepadButton(p, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH),
                LeftShoulder: SDL_GetGamepadButton(p, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER),
                RightShoulder: SDL_GetGamepadButton(p, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER),
                Start: SDL_GetGamepadButton(p, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START),
                Back: SDL_GetGamepadButton(p, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK),
                LeftStick: SDL_GetGamepadButton(p, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK),
                RightStick: SDL_GetGamepadButton(p, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK),
                Up: SDL_GetGamepadButton(p, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP),
                Down: SDL_GetGamepadButton(p, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN),
                Left: SDL_GetGamepadButton(p, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT),
                Right: SDL_GetGamepadButton(p, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT),
                LeftTrigger: SDL_GetGamepadAxis(p, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER),
                RightTrigger: SDL_GetGamepadAxis(p, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER),
                LeftX: SDL_GetGamepadAxis(p, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX),
                LeftY: SDL_GetGamepadAxis(p, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY),
                RightX: SDL_GetGamepadAxis(p, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX),
                RightY: SDL_GetGamepadAxis(p, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY));
            if (InputDeviceTracker.HasActivity(one))
            {
                ActiveKind = _kinds[id];
            }

            s = s.Merge(one);
        }

        return s;
    }

    /// <summary>Closes every pad and releases the subsystem.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (nint pad in _handles.Values)
        {
            SDL_CloseGamepad((SDL_Gamepad*)pad);
        }

        _handles.Clear();
        _connected.Clear();
        _disposed = true;
        Sdl.Release(PlatformSubsystems.Gamepad);
    }

    private static string Label(SDL_GamepadButtonLabel label) => label switch
    {
        SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_A => "A",
        SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_B => "B",
        SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_X => "X",
        SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_Y => "Y",
        SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_CROSS => "Cross",
        SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_CIRCLE => "Circle",
        SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_SQUARE => "Square",
        SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_TRIANGLE => "Triangle",
        _ => "Unknown",
    };
}

/// <summary>
/// A virtual SDL gamepad, so the smoke check and the tests can drive a real hot-plug through SDL's
/// own device-added / device-removed path without hardware. It reports a DualShock 4's USB ids.
/// </summary>
public static unsafe class VirtualGamepad
{
    /// <summary>Sony's USB vendor id.</summary>
    public const ushort SonyVendorId = 0x054C;

    /// <summary>DualShock 4 (CUH-ZCT2) product id.</summary>
    public const ushort DualShock4ProductId = 0x09CC;

    /// <summary>
    /// Attaches the virtual pad and returns its instance id. SDL posts SDL_EVENT_GAMEPAD_ADDED for it;
    /// the gamepad subsystem must be initialised (a <see cref="GamepadSet"/> exists).
    /// </summary>
    public static uint Attach(string name = "Tsuki virtual DualShock 4")
    {
        ArgumentNullException.ThrowIfNull(name);
        nint utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            SDL_VirtualJoystickDesc desc;
            SDL_INIT_INTERFACE(out desc);
            desc.type = (ushort)SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD;
            desc.vendor_id = SonyVendorId;
            desc.product_id = DualShock4ProductId;
            desc.naxes = (ushort)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_COUNT;
            desc.nbuttons = (ushort)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_COUNT;
            desc.name = (byte*)utf8;
            SDL_JoystickID id = SDL_AttachVirtualJoystick(&desc);
            if (id == 0)
            {
                throw new PlatformException("SDL_AttachVirtualJoystick: " + SDL_GetError());
            }

            return (uint)id;
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>Detaches the virtual pad; SDL posts SDL_EVENT_GAMEPAD_REMOVED.</summary>
    public static void Detach(uint instanceId)
    {
        if (!SDL_DetachVirtualJoystick((SDL_JoystickID)instanceId))
        {
            throw new PlatformException("SDL_DetachVirtualJoystick: " + SDL_GetError());
        }
    }
}

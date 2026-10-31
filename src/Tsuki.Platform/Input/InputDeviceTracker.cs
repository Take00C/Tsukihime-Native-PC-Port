// Which device the player used last (package K9): the button prompts follow it. The tracker is pure (the keys held, the pad snapshot and the pointer of one
// step in, the device out), so the rule is testable without SDL; PcInput feeds it every step.
using Tsuki.Engine.Contracts;

namespace Tsuki.Platform.Input;

/// <summary>The kind of device the player touched last. The mouse counts as the keyboard: both get keyboard prompts.</summary>
public enum InputDevice
{
    /// <summary>The keyboard or the mouse.</summary>
    Keyboard,

    /// <summary>A PlayStation pad (SDL gamepad type ps3, ps4 or ps5, or a Sony vendor id).</summary>
    PlayStationPad,

    /// <summary>Any other pad: Xbox pads and every pad SDL does not know as a PlayStation pad.</summary>
    XboxPad,
}

/// <summary>Maps an SDL gamepad to the prompt family it should show.</summary>
public static class PadKinds
{
    /// <summary>Sony's USB vendor id.</summary>
    public const ushort SonyVendor = 0x054C;

    /// <summary>
    /// The device kind of a pad from SDL's gamepad type string (<c>SDL_GetGamepadStringForType</c>: "ps3", "ps4", "ps5", "xbox360", "xboxone", "switchpro",
    /// "standard", "unknown", ...) and its USB vendor id. PlayStation pads show PlayStation prompts; every other pad shows Xbox prompts.
    /// </summary>
    public static InputDevice FromType(string? sdlType, ushort vendorId = 0)
    {
        string t = (sdlType ?? string.Empty).Trim().ToLowerInvariant();
        if (t is "ps3" or "ps4" or "ps5")
        {
            return InputDevice.PlayStationPad;
        }

        // a pad SDL could not classify but whose maker is Sony is still a PlayStation pad
        if ((t is "" or "unknown" or "standard") && vendorId == SonyVendor)
        {
            return InputDevice.PlayStationPad;
        }

        return InputDevice.XboxPad;
    }
}

/// <summary>Follows the last input device: a newly pressed key, a mouse move, click or wheel turn means the keyboard; a pad button or a stick or trigger pushed out of its dead zone means that pad.</summary>
public sealed class InputDeviceTracker
{
    /// <summary>A stick must leave this distance from the centre (of 32767) to count as use; slighter drift is ignored.</summary>
    public const short StickDeadZone = 12000;

    /// <summary>A trigger must pass this value (of 32767) to count as use.</summary>
    public const short TriggerDeadZone = 8192;

    /// <summary>The pointer must move this many units (of 640 x 448) in one step to count as use.</summary>
    public const int PointerMoveUnits = 2;

    private KeySet _keys;
    private ulong _padBits;
    private PointerButtons _buttons;
    private short _x = -1;
    private short _y = -1;

    /// <summary>The device used last; the keyboard until something else is touched.</summary>
    public InputDevice Last { get; private set; } = InputDevice.Keyboard;

    /// <summary>
    /// Takes one step's raw state and returns the device used last. A pad takes the lead when it was touched in the same step as the keyboard.
    /// </summary>
    /// <param name="keys">The keys held now.</param>
    /// <param name="pad">The pads merged.</param>
    /// <param name="pointer">The pointer now.</param>
    /// <param name="padKind">The kind of the pad that was touched last (the set that polled it knows).</param>
    public InputDevice Update(KeySet keys, in PadSnapshot pad, in PointerSample pointer, InputDevice padKind)
    {
        bool keyboard = (keys.Bits & ~_keys.Bits) != 0;
        _keys = keys;
        if (pointer.Inside)
        {
            if (pointer.Wheel != 0 || (pointer.Held & ~_buttons) != 0)
            {
                keyboard = true;
            }

            if (_x >= 0 && Math.Abs(pointer.X - _x) + Math.Abs(pointer.Y - _y) >= PointerMoveUnits)
            {
                keyboard = true;
            }

            _x = pointer.X;
            _y = pointer.Y;
        }
        else
        {
            // a pointer outside the game area (or an unfocused window) is not input, and its next entry is not a move
            _x = -1;
        }

        _buttons = pointer.Held;
        ulong bits = ActiveBits(pad);
        bool padUsed = (bits & ~_padBits) != 0;
        _padBits = bits;
        if (padUsed)
        {
            Last = padKind == InputDevice.Keyboard ? InputDevice.XboxPad : padKind;
        }
        else if (keyboard)
        {
            Last = InputDevice.Keyboard;
        }

        return Last;
    }

    /// <summary>True when the snapshot holds a button or a stick or trigger past its dead zone.</summary>
    public static bool HasActivity(in PadSnapshot pad) => ActiveBits(pad) != 0;

    private static ulong ActiveBits(in PadSnapshot s)
    {
        ulong b = 0;
        void Set(bool on, int bit)
        {
            if (on)
            {
                b |= 1UL << bit;
            }
        }

        Set(s.South, 0);
        Set(s.East, 1);
        Set(s.West, 2);
        Set(s.North, 3);
        Set(s.LeftShoulder, 4);
        Set(s.RightShoulder, 5);
        Set(s.Start, 6);
        Set(s.Back, 7);
        Set(s.LeftStick, 8);
        Set(s.RightStick, 9);
        Set(s.Up, 10);
        Set(s.Down, 11);
        Set(s.Left, 12);
        Set(s.Right, 13);
        Set(s.LeftTrigger >= TriggerDeadZone, 14);
        Set(s.RightTrigger >= TriggerDeadZone, 15);
        Set(s.LeftX < -StickDeadZone, 16);
        Set(s.LeftX > StickDeadZone, 17);
        Set(s.LeftY < -StickDeadZone, 18);
        Set(s.LeftY > StickDeadZone, 19);
        Set(s.RightX < -StickDeadZone, 20);
        Set(s.RightX > StickDeadZone, 21);
        Set(s.RightY < -StickDeadZone, 22);
        Set(s.RightY > StickDeadZone, 23);
        return b;
    }
}

using Tsuki.Platform;
using Tsuki.Platform.Input;

namespace Tsuki.Tests.Render.Smoke;

/// <summary>
/// Package K9: the kind of a pad that SDL reports decides the button prompts. A virtual DualShock 4 goes through SDL's own device-added path; no window is
/// opened and nothing is played.
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class PadKindSmokeTests
{
    [Fact]
    public void AVirtualDualShock4IsAPlayStationPadAndTheSetFollowsThePlugAndUnplug()
    {
        using GamepadSet pads = GamepadSet.Create();
        var pump = new EventPump(null, pads);
        var events = new List<PlatformEvent>();
        uint id = VirtualGamepad.Attach("Tsuki virtual DualShock 4 (prompt kind)");
        try
        {
            GamepadInfo info = PumpUntilAdded(pump, events, id);
            Assert.Equal("ps4", info.Type);
            Assert.Equal(InputDevice.PlayStationPad, PadKinds.FromType(info.Type, info.VendorId));
            Assert.Equal(InputDevice.PlayStationPad, pads.ActiveKind);      // the pad just plugged in is the one the prompts follow
        }
        finally
        {
            VirtualGamepad.Detach(id);
        }

        long deadline = Environment.TickCount64 + 3000;
        while (pads.Connected.Any(p => p.InstanceId == id) && Environment.TickCount64 < deadline)
        {
            events.Clear();
            pump.Pump(events);
            Thread.Sleep(5);
        }

        Assert.DoesNotContain(pads.Connected, p => p.InstanceId == id);
    }

    private static GamepadInfo PumpUntilAdded(EventPump pump, List<PlatformEvent> events, uint id)
    {
        long deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            events.Clear();
            pump.Pump(events);
            foreach (PlatformEvent e in events)
            {
                if (e.Kind == PlatformEventKind.GamepadAdded && e.Gamepad?.InstanceId == id)
                {
                    return e.Gamepad;
                }
            }

            Thread.Sleep(5);
        }

        Assert.Fail($"no GamepadAdded event for gamepad {id} within 3 s");
        return null!;
    }
}

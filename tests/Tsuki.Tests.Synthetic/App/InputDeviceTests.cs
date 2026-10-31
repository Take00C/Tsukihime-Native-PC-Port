using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Platform.Input;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>Package K9: which device the player used last, the prompt style that follows it, and the keys the keyboard prompts show.</summary>
public sealed class InputDeviceTests
{
    private static readonly PointerSample Away = new(0, 0, false, PointerButtons.None, 0);

    private static PointerSample At(int x, int y, PointerButtons held = PointerButtons.None, int wheel = 0) => new((short)x, (short)y, true, held, wheel);

    [Fact]
    public void TheKeyboardIsTheDeviceUntilAnotherOneIsTouched()
    {
        var t = new InputDeviceTracker();
        Assert.Equal(InputDevice.Keyboard, t.Last);
        Assert.Equal(InputDevice.Keyboard, t.Update(KeySet.None, default, Away, InputDevice.XboxPad));
        Assert.Equal(InputDevice.Keyboard, t.Update(KeySet.None, default, At(100, 100), InputDevice.XboxPad));
    }

    [Fact]
    public void AGamepadButtonOrAStickOrTriggerPushedOutOfItsDeadZoneMeansThatPad()
    {
        var t = new InputDeviceTracker();
        Assert.Equal(InputDevice.PlayStationPad, t.Update(KeySet.None, new PadSnapshot(South: true), Away, InputDevice.PlayStationPad));
        Assert.Equal(InputDevice.XboxPad, new InputDeviceTracker().Update(KeySet.None, new PadSnapshot(LeftX: 20000), Away, InputDevice.XboxPad));
        Assert.Equal(InputDevice.XboxPad, new InputDeviceTracker().Update(KeySet.None, new PadSnapshot(RightTrigger: 20000), Away, InputDevice.XboxPad));
        Assert.Equal(InputDevice.XboxPad, new InputDeviceTracker().Update(KeySet.None, new PadSnapshot(Up: true), Away, InputDevice.XboxPad));
    }

    [Fact]
    public void StickDriftAndALightTriggerTouchDoNotCount()
    {
        var t = new InputDeviceTracker();
        Assert.Equal(InputDevice.Keyboard, t.Update(KeySet.None, new PadSnapshot(LeftX: 3000, LeftY: -4000, RightX: 2000, LeftTrigger: 1000), Away, InputDevice.XboxPad));
        Assert.Equal(InputDevice.Keyboard, t.Update(KeySet.None, new PadSnapshot(LeftX: InputDeviceTracker.StickDeadZone), Away, InputDevice.XboxPad));
        Assert.Equal(InputDevice.XboxPad, t.Update(KeySet.None, new PadSnapshot(LeftX: InputDeviceTracker.StickDeadZone + 1), Away, InputDevice.XboxPad));
    }

    [Fact]
    public void ANewKeyAMouseMoveAClickOrAWheelTurnMeansTheKeyboardAgain()
    {
        var pad = new PadSnapshot(South: true);
        var t = new InputDeviceTracker();
        t.Update(KeySet.None, pad, Away, InputDevice.PlayStationPad);
        Assert.Equal(InputDevice.Keyboard, t.Update(KeySet.Of(PcKey.Down), default, Away, InputDevice.PlayStationPad));

        t.Update(KeySet.Of(PcKey.Down), pad, Away, InputDevice.PlayStationPad);              // the pad again
        Assert.Equal(InputDevice.PlayStationPad, t.Last);
        t.Update(KeySet.None, default, At(100, 100), InputDevice.PlayStationPad);               // the pointer enters: a baseline, not a move
        Assert.Equal(InputDevice.PlayStationPad, t.Last);
        t.Update(KeySet.None, default, At(101, 100), InputDevice.PlayStationPad);               // one unit of jitter: not a move
        Assert.Equal(InputDevice.PlayStationPad, t.Last);
        Assert.Equal(InputDevice.Keyboard, t.Update(KeySet.None, default, At(104, 100), InputDevice.PlayStationPad));

        t.Update(KeySet.None, pad, At(104, 100), InputDevice.XboxPad);
        Assert.Equal(InputDevice.XboxPad, t.Last);
        Assert.Equal(InputDevice.Keyboard, t.Update(KeySet.None, default, At(104, 100, PointerButtons.Left), InputDevice.XboxPad));
        t.Update(KeySet.None, new PadSnapshot(North: true), At(104, 100), InputDevice.XboxPad);
        Assert.Equal(InputDevice.XboxPad, t.Last);
        Assert.Equal(InputDevice.Keyboard, t.Update(KeySet.None, default, At(104, 100, wheel: 1), InputDevice.XboxPad));
    }

    [Fact]
    public void AHeldKeyOrButtonIsNotNewUseAndAPadTouchedWithAKeyInOneStepTakesTheLead()
    {
        var t = new InputDeviceTracker();
        t.Update(KeySet.Of(PcKey.Enter), default, Away, InputDevice.XboxPad);
        // the pad is touched while the key stays down: the pad is the newest
        Assert.Equal(InputDevice.XboxPad, t.Update(KeySet.Of(PcKey.Enter), new PadSnapshot(East: true), Away, InputDevice.XboxPad));
        // both stay down, nothing new: the pad is still the last
        Assert.Equal(InputDevice.XboxPad, t.Update(KeySet.Of(PcKey.Enter), new PadSnapshot(East: true), Away, InputDevice.XboxPad));
        // a second key: the keyboard is the newest
        Assert.Equal(InputDevice.Keyboard, t.Update(KeySet.Of(PcKey.Enter, PcKey.Space), new PadSnapshot(East: true), Away, InputDevice.XboxPad));
        // a new pad button and a new key in the same step: the pad leads
        Assert.Equal(InputDevice.PlayStationPad, t.Update(KeySet.Of(PcKey.Enter, PcKey.Space, PcKey.Z), new PadSnapshot(East: true, West: true), Away, InputDevice.PlayStationPad));
    }

    [Fact]
    public void APointerOutsideTheGameAreaIsNotInputAndComingBackIsNotAMove()
    {
        var t = new InputDeviceTracker();
        t.Update(KeySet.None, new PadSnapshot(South: true), Away, InputDevice.XboxPad);
        t.Update(KeySet.None, default, At(10, 10), InputDevice.XboxPad);
        Assert.Equal(InputDevice.XboxPad, t.Last);
        t.Update(KeySet.None, default, Away, InputDevice.XboxPad);
        Assert.Equal(InputDevice.XboxPad, t.Update(KeySet.None, default, At(400, 300), InputDevice.XboxPad));       // re-entry far away: a baseline
        Assert.Equal(InputDevice.Keyboard, t.Update(KeySet.None, default, At(410, 300), InputDevice.XboxPad));
    }

    [Theory]
    [InlineData("ps3", 0, InputDevice.PlayStationPad)]
    [InlineData("ps4", 0, InputDevice.PlayStationPad)]
    [InlineData("ps5", 0, InputDevice.PlayStationPad)]
    [InlineData("PS5", 0, InputDevice.PlayStationPad)]
    [InlineData("xbox360", 0, InputDevice.XboxPad)]
    [InlineData("xboxone", 0, InputDevice.XboxPad)]
    [InlineData("switchpro", 0, InputDevice.XboxPad)]
    [InlineData("joyconpair", 0, InputDevice.XboxPad)]
    [InlineData("standard", 0, InputDevice.XboxPad)]
    [InlineData("unknown", 0, InputDevice.XboxPad)]
    [InlineData("", 0, InputDevice.XboxPad)]
    [InlineData(null, 0, InputDevice.XboxPad)]
    [InlineData("unknown", PadKinds.SonyVendor, InputDevice.PlayStationPad)]
    [InlineData("standard", PadKinds.SonyVendor, InputDevice.PlayStationPad)]
    [InlineData("xboxone", PadKinds.SonyVendor, InputDevice.XboxPad)]
    public void SdlGamepadTypesPickThePromptFamily(string? type, int vendor, InputDevice expected)
    {
        Assert.Equal(expected, PadKinds.FromType(type, (ushort)vendor));
    }

    [Fact]
    public void TheDeviceMapsToThePromptStyleAndAutoFollowsItWhileAFixedSettingDoesNot()
    {
        var state = new PromptState(null);
        Assert.Equal(PromptSetting.Auto, state.Setting);
        Assert.Equal(PromptStyle.Keyboard, state.Style);
        state.Observe(InputDevice.PlayStationPad);
        Assert.Equal(PromptStyle.PlayStation, state.Style);
        state.Observe(InputDevice.XboxPad);
        Assert.Equal(PromptStyle.Xbox, state.Style);
        state.Observe(InputDevice.Keyboard);
        Assert.Equal(PromptStyle.Keyboard, state.Style);

        state.Setting = PromptSetting.PlayStation;
        state.Observe(InputDevice.XboxPad);
        Assert.Equal(PromptStyle.PlayStation, state.Style);
        Assert.Equal(PromptStyle.Xbox, state.LastDevice);
        state.Setting = PromptSetting.Keyboard;
        Assert.Equal(PromptStyle.Keyboard, state.Style);
        state.Setting = PromptSetting.Xbox;
        Assert.Equal(PromptStyle.Xbox, state.Style);
    }

    // ---- the keys the prompts show are the keys the mapper acts on --------------------------------------------------------------------------------

    private static PcKey KeyOf(string name) => name switch
    {
        "Esc" => PcKey.Escape,
        "PgUp" => PcKey.PageUp,
        "PgDn" => PcKey.PageDown,
        "Del" => PcKey.Delete,
        "Ctrl" => PcKey.LeftCtrl,
        var other => Enum.Parse<PcKey>(other),
    };

    [Fact]
    public void EveryKeyOfTheKeyboardPromptsDoesWhatItsActionSays()
    {
        foreach ((PromptAction action, string[] keys) in PromptBindings.Keys)
        {
            Assert.NotEmpty(keys);
            foreach (string name in keys)
            {
                InputFrame f = new InputMapper().Map(1, KeySet.Of(KeyOf(name)), default, new PointerSample(0, 0, false, PointerButtons.None, 0));
                // W, S, A and D are the arrow keys of the menus only (package K14): they set the frame's menu-only direction bits and none of the pad's
                bool letter = name is "W" or "S" or "A" or "D";
                bool Pad(PadButtons b) => letter ? (f.MenuPressed & b) == b && (f.Pressed & b) == 0 : (f.Pressed & b) == b;
                bool Shell(ShellActions a) => (f.ShellPressed & a) == a;
                bool ok = action switch
                {
                    PromptAction.Confirm => Pad(PadButtons.Decide),
                    PromptAction.Back => Pad(PadButtons.Cancel),
                    PromptAction.Up => Pad(PadButtons.Up),
                    PromptAction.Down => Pad(PadButtons.Down),
                    PromptAction.Left => Pad(PadButtons.Left),
                    PromptAction.Right => Pad(PadButtons.Right),
                    PromptAction.PreviousPage => Pad(PadButtons.L1),
                    PromptAction.NextPage => Pad(PadButtons.R1),
                    PromptAction.Scroll => name == "PgUp" ? Shell(ShellActions.PageUp) : Shell(ShellActions.PageDown),
                    PromptAction.DeleteData => Shell(ShellActions.DeleteData),
                    PromptAction.SystemMenu => Shell(ShellActions.SystemMenu),
                    PromptAction.Backlog => Shell(ShellActions.Backlog),
                    PromptAction.SkipToggle => Shell(ShellActions.SkipToggle),
                    PromptAction.AutoToggle => Shell(ShellActions.AutoToggle),
                    PromptAction.FastForward => Shell(ShellActions.FastForward),
                    PromptAction.Rewind => Shell(ShellActions.Rewind),
                    PromptAction.QuickSave => Shell(ShellActions.QuickSave),
                    _ => false,
                };
                Assert.True(ok, $"{name} should do {action}");
            }
        }
    }

    [Fact]
    public void DeleteAndThePageKeysHaveShellActionsOfTheirOwnAndTriangleBelongsToThePadAlone()
    {
        InputFrame del = new InputMapper().Map(1, KeySet.Of(PcKey.Delete), default, default);
        Assert.Equal(ShellActions.DeleteData, del.ShellPressed & ShellActions.DeleteData);
        Assert.Equal(PadButtons.None, del.Held & (PadButtons.Triangle | PadButtons.Square));

        InputFrame up = new InputMapper().Map(1, KeySet.Of(PcKey.PageUp), default, default);
        Assert.Equal(ShellActions.PageUp | ShellActions.Backlog, up.ShellPressed & (ShellActions.PageUp | ShellActions.Backlog));      // the story still opens the backlog with it
        Assert.Equal(PadButtons.None, up.Held & PadButtons.Triangle);
        InputFrame down = new InputMapper().Map(1, KeySet.Of(PcKey.PageDown), default, default);
        Assert.Equal(ShellActions.PageDown, down.ShellPressed & ShellActions.PageDown);
        Assert.Equal(PadButtons.None, new InputMapper().Map(1, KeySet.Of(PcKey.I), default, default).Held & PadButtons.Triangle);

        InputFrame pad = new InputMapper().Map(1, KeySet.None, new PadSnapshot(North: true, West: true), default);
        Assert.Equal(PadButtons.Triangle | PadButtons.Square, pad.Held & (PadButtons.Triangle | PadButtons.Square));
    }
}

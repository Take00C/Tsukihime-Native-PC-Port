using Tsuki.App.Play;
using Tsuki.Engine.Contracts;
using Tsuki.Platform.Input;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>The keyboard, mouse and gamepad mapping onto the PS4 input word, the letterbox and the verification script.</summary>
public sealed class InputTests
{
    private static InputFrame Map(InputMapper m, long frame, KeySet keys, PadSnapshot pad = default, PointerSample? pointer = null) =>
        m.Map(frame, keys, pad, pointer ?? new PointerSample(0, 0, false, PointerButtons.None, 0));

    [Fact]
    public void EnterSpaceAndZDecideAndOnlyTheFirstStepIsAnEdge()
    {
        var m = new InputMapper();
        foreach (PcKey key in new[] { PcKey.Enter, PcKey.Space, PcKey.Z, PcKey.KeypadEnter })
        {
            var fresh = new InputMapper();
            InputFrame a = Map(fresh, 1, KeySet.Of(key));
            Assert.Equal(PadButtons.Decide, a.Held & PadButtons.Decide);
            Assert.Equal(PadButtons.Decide, a.Pressed & PadButtons.Decide);
            InputFrame b = Map(fresh, 2, KeySet.Of(key));
            Assert.Equal(PadButtons.None, b.Pressed & PadButtons.Decide);
            Assert.Equal(PadButtons.Decide, b.Held & PadButtons.Decide);
        }

        Assert.Equal(PadButtons.None, Map(m, 1, KeySet.None).Held);
    }

    [Fact]
    public void EscapeIsCancelAndTheSystemMenuShellAction()
    {
        InputFrame f = Map(new InputMapper(), 1, KeySet.Of(PcKey.Escape));
        Assert.NotEqual(PadButtons.None, f.Pressed & PadButtons.Cancel);
        Assert.NotEqual(ShellActions.None, f.ShellPressed & ShellActions.SystemMenu);
        InputFrame x = Map(new InputMapper(), 1, KeySet.Of(PcKey.X));
        Assert.NotEqual(PadButtons.None, x.Pressed & PadButtons.Cancel);
        Assert.Equal(ShellActions.None, x.ShellPressed & ShellActions.SystemMenu);       // X hides the window, it does not open the menu
    }

    [Fact]
    public void TheShellKeysMapToTheirActions()
    {
        (PcKey Key, ShellActions Action)[] table =
        [
            (PcKey.LeftCtrl, ShellActions.FastForward), (PcKey.RightCtrl, ShellActions.FastForward), (PcKey.Tab, ShellActions.SkipToggle), (PcKey.A, ShellActions.AutoToggle),
            (PcKey.H, ShellActions.HideWindow), (PcKey.F5, ShellActions.QuickSave), (PcKey.F9, ShellActions.QuickLoad), (PcKey.N, ShellActions.NextScene),
            (PcKey.B, ShellActions.PreviousScene), (PcKey.V, ShellActions.ReplayVoice), (PcKey.Backspace, ShellActions.Rewind), (PcKey.PageUp, ShellActions.Backlog),
        ];
        foreach ((PcKey key, ShellActions action) in table)
        {
            InputFrame f = Map(new InputMapper(), 1, KeySet.Of(key));
            Assert.True((f.ShellHeld & action) == action, $"{key} should hold {action}");
        }
    }

    [Fact]
    public void WheelUpOpensTheBacklogAndWheelDownDoesNot()
    {
        var up = new PointerSample(10, 10, true, PointerButtons.None, 1);
        var down = new PointerSample(10, 10, true, PointerButtons.None, -1);
        Assert.NotEqual(ShellActions.None, Map(new InputMapper(), 1, KeySet.None, default, up).ShellPressed & ShellActions.Backlog);
        Assert.Equal(ShellActions.None, Map(new InputMapper(), 1, KeySet.None, default, down).ShellPressed & ShellActions.Backlog);
    }

    [Fact]
    public void ADirectionRepeatsAfterTheDelayAtTheGivenPeriod()
    {
        var m = new InputMapper();
        var repeats = new List<int>();
        for (int f = 1; f <= InputMapper.RepeatDelay + (3 * InputMapper.RepeatPeriod) + 2; f++)
        {
            InputFrame i = Map(m, f, KeySet.Of(PcKey.Down));
            if ((i.Repeated & PadButtons.Down) != 0)
            {
                repeats.Add(f);
            }
        }

        Assert.Equal(1, repeats[0]);                                                      // the press itself
        Assert.Equal(InputMapper.RepeatDelay + InputMapper.RepeatPeriod, repeats[1]);       // the hold counter starts at the press
        Assert.Equal(InputMapper.RepeatPeriod, repeats[2] - repeats[1]);
    }

    [Fact]
    public void TheGamepadMapsSouthToDecideWestToTheMenuAndNorthToTheBacklog()
    {
        InputFrame f = Map(new InputMapper(), 1, KeySet.None, new PadSnapshot(South: true, West: true, North: true, LeftShoulder: true, RightTrigger: 30000, LeftX: -30000));
        Assert.NotEqual(PadButtons.None, f.Held & PadButtons.Decide);
        Assert.NotEqual(PadButtons.None, f.Held & PadButtons.Square);
        Assert.NotEqual(PadButtons.None, f.Held & PadButtons.Triangle);
        Assert.NotEqual(PadButtons.None, f.Held & PadButtons.L1);
        Assert.NotEqual(PadButtons.None, f.Held & PadButtons.R2);
        Assert.NotEqual(PadButtons.None, f.Held & PadButtons.StickLeft);
        Assert.True(f.AnalogR2 > 200);
        Assert.NotEqual(ShellActions.None, f.ShellHeld & ShellActions.FastForward);
    }

    [Theory]
    [InlineData(1920, 1080, 0, 0, 1920, 1080)]
    [InlineData(1280, 720, 0, 0, 1280, 720)]
    [InlineData(1920, 1200, 0, 60, 1920, 1080)]
    [InlineData(1000, 1000, 0, 219, 999, 562)]
    [InlineData(3440, 1440, 440, 0, 2560, 1440)]
    public void TheCanvasIsLetterboxedLikeThePresentBlit(int ww, int wh, int x, int y, int w, int h)
    {
        Assert.Equal(new Viewport(x, y, w, h), Viewport.Fit(ww, wh));
    }

    [Fact]
    public void AWindowPointMapsToThePointerSpaceInsideTheLetterbox()
    {
        Viewport v = Viewport.Fit(1920, 1200);
        (bool inside, short px, short py) = v.ToPointer(960, 600);
        Assert.True(inside);
        Assert.Equal(320, px);
        Assert.Equal(224, py);
        Assert.False(v.ToPointer(960, 10).Inside);                                  // in the black bar
        (_, short cx, short cy) = v.ToPointer(5000, 5000);
        Assert.Equal((639, 447), (cx, cy));                                         // clamped
    }

    [Fact]
    public void AScriptPressesKeysClicksAndNamesItsShots()
    {
        InputScript s = InputScript.Parse("10:Enter;20:Esc*5+Down;30:click=960,540 40:shot=menu;50:wheel=1;60:log;70:quit;80:autopause");
        Assert.True(s.KeysAt(10).Has(PcKey.Enter));
        Assert.True(s.KeysAt(11).Has(PcKey.Enter));
        Assert.False(s.KeysAt(12).Has(PcKey.Enter));
        Assert.True(s.KeysAt(24).Has(PcKey.Escape));
        Assert.False(s.KeysAt(25).Has(PcKey.Escape));
        Assert.True(s.KeysAt(20).Has(PcKey.Down));
        PointerSample click = s.PointerAt(30, default);
        Assert.Equal(PointerButtons.Left, click.Held);
        Assert.Equal((320, 224), ((int)click.X, (int)click.Y));
        Assert.Equal(PointerButtons.None, s.PointerAt(33, click).Held);
        Assert.Equal(["menu"], s.VerbsAt(40, "shot").Select(e => e.Argument).ToArray());
        Assert.Equal(1, s.PointerAt(50, default).Wheel);
        Assert.True(s.LogAt(60));
        Assert.True(s.QuitAt(70));
        Assert.Single(s.VerbsAt(80, "autopause"));
    }

    [Fact]
    public void ABadScriptEntryIsAnErrorNotASilentSkip()
    {
        Assert.Throws<FormatException>(() => InputScript.Parse("Enter"));
        Assert.Throws<FormatException>(() => InputScript.Parse("10:Frobnicate"));
    }

    [Fact]
    public void ChoicePoliciesParseAndRejectUnknownNames()
    {
        Assert.NotNull(ChoicePolicy.Parse("first"));
        Assert.NotNull(ChoicePolicy.Parse("random:7"));
        Assert.NotNull(ChoicePolicy.Parse("list:1,0,2"));
        Assert.Throws<ArgumentException>(() => ChoicePolicy.Parse("middle"));
    }

    [Fact]
    public void TheWheelButtonTogglesSkipOnceAndThePointerKeepsItsPixelPosition()
    {
        var m = new InputMapper();
        var middle = new PointerSample(100, 100, true, PointerButtons.Middle, 0, 301.5f, 241.25f);
        InputFrame first = Map(m, 1, KeySet.None, pointer: middle);
        Assert.NotEqual(ShellActions.None, first.ShellPressed & ShellActions.SkipToggle);
        InputFrame held = Map(m, 2, KeySet.None, pointer: middle);
        Assert.Equal(ShellActions.None, held.ShellPressed & ShellActions.SkipToggle);          // a held button is not a second press
        Assert.Equal(301.5f, held.Pointer.CanvasX);
        Assert.Equal(241.25f, held.Pointer.CanvasY);

        // a source that only knows the 640 x 448 unit leaves the pixel position unset
        InputFrame units = Map(m, 3, KeySet.None, pointer: new PointerSample(5, 5, true, PointerButtons.None, 0));
        Assert.True(float.IsNaN(units.Pointer.CanvasX));
    }

    [Fact]
    public void AScriptDragHoldsTheLeftButtonWhileThePointerRunsAndLeavesItAtTheEnd()
    {
        InputScript s = InputScript.Parse("10:drag=1855,200,1855,500,30;100:mclick=960,540");
        PointerSample at9 = s.PointerAt(9, default);
        Assert.Equal(PointerButtons.None, at9.Held);
        PointerSample at10 = s.PointerAt(10, at9);
        Assert.Equal(PointerButtons.Left, at10.Held);
        Assert.Equal((1855f, 200f), (at10.CanvasX, at10.CanvasY));
        PointerSample at25 = s.PointerAt(25, at10);
        Assert.Equal(PointerButtons.Left, at25.Held);
        Assert.Equal(350f, at25.CanvasY, 3);
        PointerSample at40 = s.PointerAt(40, at25);
        Assert.Equal(PointerButtons.Left, at40.Held);
        Assert.Equal(500f, at40.CanvasY, 3);
        PointerSample at41 = s.PointerAt(41, at40);
        Assert.Equal(PointerButtons.None, at41.Held);
        Assert.Equal(500f, at41.CanvasY, 3);                                    // the pointer stays where it ended
        Assert.Equal(PointerButtons.Middle, s.PointerAt(100, at41).Held);
        Assert.Equal((320, 224), ((int)s.PointerAt(100, at41).X, (int)s.PointerAt(100, at41).Y));
    }
    [Fact]
    public void AScriptPadVerbHoldsAGamepadButtonThroughTheMapperAndTheChoiceVerbsParse()
    {
        // package K16: a scripted gamepad (the choice by pad), and the autoplay switches that leave an open choice to the script
        InputScript s = InputScript.Parse("10:pad=South*3;20:pad=Down;30:pad=L2*4;40:holdchoice;50:releasechoice");
        Assert.True(s.PadAt(9) == default);
        Assert.True(s.PadAt(10).South && s.PadAt(12).South);
        Assert.False(s.PadAt(13).South);
        Assert.True(s.PadAt(20).Down && s.PadAt(21).Down);                      // two steps by default
        Assert.False(s.PadAt(22).Down);
        Assert.True(s.PadAt(30).LeftTrigger > 20000 && s.PadAt(33).LeftTrigger > 20000);
        Assert.Equal(0, s.PadAt(34).LeftTrigger);
        Assert.Single(s.VerbsAt(40, "holdchoice"));
        Assert.Single(s.VerbsAt(50, "releasechoice"));
        InputFrame pressed = new InputMapper().Map(10, KeySet.None, s.PadAt(10), default);
        Assert.NotEqual(PadButtons.None, pressed.Pressed & PadButtons.Decide);          // south is decide on every pad
        Assert.Throws<FormatException>(() => InputScript.Parse("10:pad=Banana").PadAt(10));
    }
}

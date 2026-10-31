using Tsuki.App.Play;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Platform.Input;
using Tsuki.Tests.Synthetic.Ui;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package K14: W, S, A and D are the arrow keys <b>in the menus only</b>. In the story Up opens the mode panel and A toggles auto mode, so the mapper keeps the
/// letters out of the pad bits the story reads and puts them in the frame's menu-only fields, which <see cref="UiInput"/> adds to the d-pad.
/// </summary>
public sealed class LetterKeysTests
{
    private static InputFrame Map(InputMapper m, long frame, KeySet keys) => m.Map(frame, keys, default, new PointerSample(0, 0, false, PointerButtons.None, 0));

    private const PadButtons Dirs = PadButtons.Up | PadButtons.Down | PadButtons.Left | PadButtons.Right;

    [Theory]
    [InlineData(PcKey.W, PadButtons.Up)]
    [InlineData(PcKey.S, PadButtons.Down)]
    [InlineData(PcKey.A, PadButtons.Left)]
    [InlineData(PcKey.D, PadButtons.Right)]
    public void ALetterKeySetsTheMenuDirectionAndNeverThePadsOwn(PcKey key, PadButtons direction)
    {
        InputFrame f = Map(new InputMapper(), 1, KeySet.Of(key));
        Assert.Equal(direction, f.MenuHeld);
        Assert.Equal(direction, f.MenuPressed);
        Assert.Equal(direction, f.MenuRepeated);
        Assert.Equal(PadButtons.None, (f.Held | f.Pressed | f.Repeated) & (Dirs | PadButtons.StickUp | PadButtons.StickDown | PadButtons.StickLeft | PadButtons.StickRight));
    }

    [Fact]
    public void TheArrowKeysAreUnchangedAndSetNoMenuBits()
    {
        foreach ((PcKey key, PadButtons direction) in new[] { (PcKey.Up, PadButtons.Up), (PcKey.Down, PadButtons.Down), (PcKey.Left, PadButtons.Left), (PcKey.Right, PadButtons.Right) })
        {
            InputFrame f = Map(new InputMapper(), 1, KeySet.Of(key));
            Assert.Equal(direction, f.Pressed & Dirs);
            Assert.Equal(PadButtons.None, f.MenuHeld | f.MenuPressed | f.MenuRepeated);
        }
    }

    [Fact]
    public void AKeepsTheAutoModeShellActionForTheStory()
    {
        InputFrame f = Map(new InputMapper(), 1, KeySet.Of(PcKey.A));
        Assert.Equal(ShellActions.AutoToggle, f.ShellPressed & ShellActions.AutoToggle);
        Assert.Equal(PadButtons.None, f.Pressed & PadButtons.Left);
        foreach (PcKey key in new[] { PcKey.W, PcKey.S, PcKey.D })
        {
            Assert.Equal(ShellActions.None, Map(new InputMapper(), 1, KeySet.Of(key)).ShellPressed);      // the other three have no story action at all
        }
    }

    [Fact]
    public void AKeyTheGameBindsToNothingStillCountsAsAnyKey()
    {
        var m = new InputMapper();
        InputFrame f = Map(m, 1, KeySet.Of(PcKey.Other));                                       // what PcInput reports for G, Shift, F1, ...
        Assert.Equal(ShellActions.AnyKey, f.ShellPressed);
        Assert.Equal(PadButtons.None, f.Held | f.Pressed | f.MenuHeld);
        Assert.True(UiInput.From(f, default).Any);                                              // PRESS ANY KEY, the notices and the title's wake read it
        Assert.Equal(ShellActions.None, Map(m, 2, KeySet.Of(PcKey.Other)).ShellPressed);        // an edge once
        Assert.False(UiInput.From(Map(new InputMapper(), 1, KeySet.None), default).Any);
    }

    [Fact]
    public void ALetterDirectionIsAnEdgeOnceAndRepeatsLikeTheDpad()
    {
        var m = new InputMapper();
        var repeats = new List<int>();
        for (int f = 1; f <= InputMapper.RepeatDelay + (3 * InputMapper.RepeatPeriod) + 2; f++)
        {
            InputFrame i = Map(m, f, KeySet.Of(PcKey.S));
            Assert.Equal(f == 1 ? PadButtons.Down : PadButtons.None, i.MenuPressed);
            if ((i.MenuRepeated & PadButtons.Down) != 0)
            {
                repeats.Add(f);
            }
        }

        Assert.Equal(1, repeats[0]);
        Assert.Equal(InputMapper.RepeatDelay + InputMapper.RepeatPeriod, repeats[1]);
        Assert.Equal(InputMapper.RepeatPeriod, repeats[2] - repeats[1]);
        Assert.Equal(PadButtons.None, Map(m, 99, KeySet.None).MenuHeld);
    }

    [Fact]
    public void TheMenuInputReadsTheLetterKeysAsTheArrowsAndAnyKeyCounts()
    {
        foreach ((PcKey key, Func<UiInput, bool> read, Func<UiInput, bool> readPressed) in new (PcKey, Func<UiInput, bool>, Func<UiInput, bool>)[]
        {
            (PcKey.W, u => u.Up, u => u.UpPressed), (PcKey.S, u => u.Down, u => u.DownPressed),
            (PcKey.A, u => u.Left, u => u.LeftPressed), (PcKey.D, u => u.Right, u => u.RightPressed),
        })
        {
            UiInput u = UiInput.From(Map(new InputMapper(), 1, KeySet.Of(key)), default);
            Assert.True(read(u), $"{key}");
            Assert.True(readPressed(u), $"{key} pressed");
            Assert.True(u.Any, $"{key} is a key press");
            Assert.False(u.Decide || u.Cancel);
        }

        UiInput none = UiInput.From(Map(new InputMapper(), 1, KeySet.None), default);
        Assert.False(none.Up || none.Down || none.Left || none.Right || none.Any);
    }

    // ---- through the app: the menus take them, the story does not ---------------------------------------------------------------------------------------

    private long _n;

    private sealed class Rt(AppRig rig, AppRuntime runtime, UiRoot ui, InputMapper mapper)
    {
        public AppRig Rig { get; } = rig;

        public AppRuntime Runtime { get; } = runtime;

        public UiRoot Ui { get; } = ui;

        public InputMapper Mapper { get; } = mapper;
    }

    private Rt Make(string script = AppRig.ThreePages)
    {
        var rig = new AppRig(script);
        var ui = new UiRoot(UiTestKit.Data(), rig.Services);
        var runtime = new AppRuntime(rig.Services, ui, null, null);
        rig.Services.StartScene(100);
        var rt = new Rt(rig, runtime, ui, new InputMapper());
        Step(rt, KeySet.None, 200);
        return rt;
    }

    private void Step(Rt rt, KeySet keys, int frames = 1)
    {
        for (int i = 0; i < frames; i++)
        {
            rt.Runtime.Step(rt.Mapper.Map(++_n, keys, default, default), _n * 16_666_667L, new DisplayList(2048));
        }
    }

    private void Tap(Rt rt, PcKey key)
    {
        Step(rt, KeySet.Of(key));
        Step(rt, KeySet.None, 2);
    }

    [Fact]
    public void InTheStoryWSDAndTheArrowsStayAsTheyWereAndAStillTogglesAutoMode()
    {
        Rt rt = Make();
        Assert.False(rt.Ui.Active);
        Tap(rt, PcKey.W);
        Tap(rt, PcKey.S);
        Tap(rt, PcKey.D);
        Assert.False(rt.Ui.Active);                                       // W is not Up: it does not open the mode panel
        Assert.False(rt.Rig.Host.AutoMode);
        Tap(rt, PcKey.A);
        Assert.True(rt.Rig.Host.AutoMode);                                // A is still auto mode
        Tap(rt, PcKey.A);
        Assert.False(rt.Rig.Host.AutoMode);
        Tap(rt, PcKey.Up);
        Assert.IsType<ModePanelScreen>(rt.Ui.Top);                        // the arrow Up still opens the panel
    }

    [Fact]
    public void InTheSystemMenuWAndSMoveTheCursorLikeTheArrows()
    {
        Rt rt = Make();
        Tap(rt, PcKey.Escape);
        Assert.IsType<SystemMenuScreen>(rt.Ui.Top);
        Step(rt, KeySet.None, 90);
        int tab = rt.Ui.Context.MenuTab;
        Tap(rt, PcKey.S);
        Assert.Equal(tab + 1, rt.Ui.Context.MenuTab);
        Tap(rt, PcKey.Down);
        Assert.Equal(tab + 2, rt.Ui.Context.MenuTab);
        Tap(rt, PcKey.W);
        Assert.Equal(tab + 1, rt.Ui.Context.MenuTab);
        Tap(rt, PcKey.Up);
        Assert.Equal(tab, rt.Ui.Context.MenuTab);
        Tap(rt, PcKey.A);                                                 // A is a menu key here: it is Left, it does not toggle auto mode under the menu
        Assert.False(rt.Rig.Host.AutoMode);
        Assert.IsType<SystemMenuScreen>(rt.Ui.Top);
    }
}

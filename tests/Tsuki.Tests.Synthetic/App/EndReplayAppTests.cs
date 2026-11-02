using Tsuki.App.Play;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Tests.Synthetic.Ui;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Issue #7 (package K38): a scene entered from the gallery's flowchart, then Esc and FlowChart again, "Do you want to return to the flowchart?", Yes. The
/// story must hand over to the gallery through the runtime once the menu has gone, and no step may ever end with no story and no screen (a black screen that
/// takes no key, which is what the player got). The hand-made game stands in for the replayed scene (F024 set: the archive flag the replay runs under).
/// </summary>
public sealed class EndReplayAppTests
{
    // many pages, so that the story does not end within a test
    private static readonly string Pages = "_WKST(F822,eq,1);_ATSV();" + string.Concat(Enumerable.Repeat("_WKST(F823,eq,0);_WNTY(1);_ZM00101($000002);", 3000)) + "_END_();";

    private long _n;

    private sealed class Rt(AppRig rig, AppRuntime runtime, UiRoot ui)
    {
        public AppRig Rig { get; } = rig;

        public AppRuntime Runtime { get; } = runtime;

        public UiRoot Ui { get; } = ui;

        public List<string> Log { get; } = [];
    }

    private Rt Make(IGameData data)
    {
        var rig = new AppRig(Pages);
        var ui = new UiRoot(data, rig.Services);
        var runtime = new AppRuntime(rig.Services, ui, null, null);
        var rt = new Rt(rig, runtime, ui);
        runtime.Logged += rt.Log.Add;
        rig.Services.StartScene(100);
        Run(rt, 60);
        Assert.True(rig.Services.StoryActive);
        Assert.Equal(GamePhase.Running, rig.Host.Phase);
        return rt;
    }

    private void Step(Rt rt, in InputFrame input)
    {
        rt.Runtime.Step(input with { Frame = ++_n }, _n * 16_666_667L, new DisplayList(2048));
        Assert.True(rt.Rig.Services.StoryActive || rt.Ui.Active, $"step {_n}: no story and no screen (a dead black screen)");
    }

    private void Run(Rt rt, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            Step(rt, new InputFrame());
        }
    }

    private void Press(Rt rt, PadButtons b)
    {
        Step(rt, new InputFrame { Held = b, Pressed = b, Repeated = b });
        Step(rt, new InputFrame());
    }

    /// <summary>The story runs as a replay picked in the gallery's chart; Esc opens the system menu and FlowChart asks to return to the flowchart.</summary>
    private Rt OpenEndReplayDialog(IGameData data)
    {
        Rt rt = Make(data);
        rt.Rig.Host.Vars.SetF(24, 1);                                          // F024: the archive scene's replay flag (gallery.md 3.2)
        Assert.True(rt.Rig.Services.ReplayActive);
        rt.Ui.Context.Gallery.Mode = (int)GalleryMode.Flowchart;              // the replay was picked in the chart (GalleryScreen.ReplayFromChart)
        rt.Ui.Context.Gallery.Node = 3;
        Step(rt, new InputFrame { ShellPressed = ShellActions.SystemMenu });
        Assert.IsType<SystemMenuScreen>(rt.Ui.Top);
        Run(rt, 30);
        rt.Ui.Context.MenuTab = (int)MenuTab.FlowChart;
        Press(rt, PadButtons.Decide);
        var dialog = Assert.IsType<ConfirmDialog>(rt.Ui.Top);
        Run(rt, 20);
        Assert.True(dialog.CursorOnYes);
        return rt;
    }

    [Fact]
    public void YesEndsTheReplayIntoTheGalleryChartWithTheMenuGoneAndNoDeadFrame()
    {
        Rt rt = OpenEndReplayDialog(GalleryKit.Data());
        rt.Rig.Services.SetGlobal(110, 2);                                     // scene 1 seen: the gallery's flowchart is available
        Press(rt, PadButtons.Decide);

        // the menu fades out by itself (no Esc), then the title restarts hidden behind the gallery, which reopens on the chart
        int frames = 0;
        for (; frames < 150 && rt.Ui.Find<GalleryScreen>() is null; frames++)
        {
            Run(rt, 1);
        }

        Assert.NotNull(rt.Ui.Find<GalleryScreen>());
        Assert.InRange(frames, 20, 80);
        Assert.False(rt.Rig.Services.StoryActive);
        Assert.Null(rt.Ui.Find<SystemMenuScreen>());
        Assert.IsType<TitleScreen>(rt.Ui.Stack[0]);
        var chart = Assert.IsType<FlowchartScreen>(rt.Ui.Top);
        Assert.True(chart.Gallery);
        Run(rt, 60);
        Assert.Equal(1f, chart.Fade);
        Assert.DoesNotContain(rt.Log, l => l.Contains("nothing on screen", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutGalleryTablesTheEndOfAReplayFallsBackToTheTitle()
    {
        Rt rt = OpenEndReplayDialog(UiTestKit.Data());
        Press(rt, PadButtons.Decide);
        Assert.True(RunUntil(rt, () => rt.Ui.Find<TitleScreen>() is not null, 150));
        Assert.False(rt.Rig.Services.StoryActive);
        Assert.Null(rt.Ui.Find<SystemMenuScreen>());
    }

    [Fact]
    public void NoLeavesTheReplayRunningUnderTheMenu()
    {
        Rt rt = OpenEndReplayDialog(GalleryKit.Data());
        Press(rt, PadButtons.Right);
        Press(rt, PadButtons.Decide);
        Run(rt, 60);
        Assert.IsType<SystemMenuScreen>(rt.Ui.Top);
        Assert.True(rt.Rig.Services.StoryActive);
        Assert.True(rt.Rig.Services.ReplayActive);
    }

    [Fact]
    public void AStepLeftWithNoStoryAndNoScreenFallsBackToTheTitleAndSaysSo()
    {
        // the last resort behind every hand-off: whatever empties the screens while no story runs gets the title and a log line, never a black screen
        var rig = new AppRig(Pages);
        var ui = new UiRoot(UiTestKit.Data(), rig.Services);
        var runtime = new AppRuntime(rig.Services, ui, null, null);
        var log = new List<string>();
        runtime.Logged += log.Add;
        runtime.Start();
        Assert.IsType<BootScreen>(ui.Top);
        ui.Clear();
        Assert.False(rig.Services.StoryActive);
        runtime.Step(new InputFrame { Frame = ++_n }, _n * 16_666_667L, new DisplayList(2048));
        Assert.Contains(log, l => l.Contains("nothing on screen", StringComparison.Ordinal));
        Assert.IsType<TitleScreen>(ui.Top);
        runtime.Step(new InputFrame { Frame = ++_n }, _n * 16_666_667L, new DisplayList(2048));
        Assert.Single(ui.Stack);                                               // once, not every frame
    }

    [Fact]
    public void AnExceptionInAMenuLeavesTheStepForTheCrashHandler()
    {
        // the loop has no catch of its own: an exception thrown by a screen reaches GameApp.Run, which writes the log line and crash.log and shows the error box over
        // a hidden game window (package K38); nothing on the way swallows it and leaves the player on a screen that takes no key. The script verb fault opens such a
        // screen in the real app
        Assert.Single(InputScript.Parse("10:fault").VerbsAt(10, "fault"));
        Rt rt = Make(UiTestKit.Data());
        Step(rt, new InputFrame { ShellPressed = ShellActions.SystemMenu });
        Assert.IsType<SystemMenuScreen>(rt.Ui.Top);
        rt.Ui.Push(new FaultScreen());
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => rt.Runtime.Step(new InputFrame { Frame = ++_n }, _n * 16_666_667L, new DisplayList(2048)));
        Assert.Contains("fault", e.Message, StringComparison.Ordinal);
    }

    private bool RunUntil(Rt rt, Func<bool> condition, int limit)
    {
        for (int i = 0; i < limit && !condition(); i++)
        {
            Run(rt, 1);
        }

        return condition();
    }
}

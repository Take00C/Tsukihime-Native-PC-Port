using Tsuki.App.Play;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Tests.Synthetic.Ui;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package K39 (review of K38, issue #7): the host's replay flag (<see cref="GameHost.ReplayRequested"/>, the gallery started the story as a replay) decides
/// <see cref="Tsuki.App.Services.AppServices.ReplayActive"/> (the system menu's FlowChart then asks "Do you want to return to the flowchart?") and where a story
/// ends (the gallery or the title). A rewind keeps it, so it also survived a load: a replay left by the menu's Title tab and then CONTINUE gave a normal story
/// that asked to return to the flowchart and, on Yes, threw the player's story away into the gallery (in v0.1.2 onto the black screen of issue #7). And a
/// language change during a replay rebuilt the host without it, so the replay ended on the title instead of the gallery. The rig replays scene 110 (it has no
/// ARCHIVE scene: 3000 falls back to START_SCRIPT).
/// </summary>
public sealed class ReplayFlagTests
{
    private static AppRig NewRig() => new(AppRig.ThreePages, copies: 11);

    [Fact]
    public void AStoryLoadedAfterAReplayIsNotAReplay()
    {
        AppRig rig = NewRig();
        rig.Begin();
        Assert.True(rig.Services.Save(0));
        Assert.False(rig.Host.ReplayRequested);

        rig.Services.ReplayScene(100, 3);
        Assert.True(rig.Services.ReplayActive);
        Assert.Equal(110, rig.Host.SceneId);
        rig.WaitFor("alpha line");

        // the menu's Title tab leaves the replay's host as it was; the title's CONTINUE / LOAD GAME then loads a slot into it
        rig.Services.ReturnToTitle();
        Assert.True(rig.Services.TakePendingTitle());
        Assert.True(rig.Host.ReplayRequested);
        Assert.True(rig.Services.Load(0));
        Assert.True(rig.Services.StoryActive);
        Assert.Equal(100, rig.Host.SceneId);
        Assert.False(rig.Host.ReplayRequested);
        Assert.False(rig.Services.ReplayActive);
    }

    [Fact]
    public void AQuickLoadDuringAReplayStartsAStoryThatIsNotAReplay()
    {
        // F9 is not refused during a replay (the PS4's quick load is a mode panel button, and the panel is off in a replay; the port's key reaches it anyway)
        AppRig rig = NewRig();
        rig.Begin();
        Assert.True(rig.Services.QuickSave());
        rig.Services.ReplayScene(100, 3);
        rig.WaitFor("alpha line");
        Assert.True(rig.Services.ReplayActive);
        Assert.True(rig.Services.QuickLoad());
        Assert.Equal(100, rig.Host.SceneId);
        Assert.False(rig.Services.ReplayActive);
    }

    [Fact]
    public void ARewindInAReplayKeepsItAReplay()
    {
        AppRig rig = NewRig();
        rig.Services.ReplayScene(100, 3);
        rig.RunTo("beta line");
        Assert.True(rig.Host.RewindStep());
        Assert.True(rig.Host.ReplayRequested);
        Assert.True(rig.Services.ReplayActive);
    }

    [Fact]
    public void ALanguageChangeDuringAReplayKeepsItAReplay()
    {
        AppRig rig = NewRig();
        rig.Services.ReplayScene(100, 3);
        rig.RunTo("beta line");
        GameHost before = rig.Host;
        rig.Services.Config.Language = (short)Language.Ja;
        rig.Services.ConfigChanged(ConfigChange.Language);
        Assert.NotSame(before, rig.Host);
        Assert.Equal(GamePhase.Running, rig.Host.Phase);
        Assert.True(rig.Host.ReplayRequested);
        Assert.True(rig.Services.ReplayActive);

        // a story's page carried by a language change stays a story
        rig = NewRig();
        rig.Begin();
        rig.RunTo("beta line");
        rig.Services.Config.Language = (short)Language.Ja;
        rig.Services.ConfigChanged(ConfigChange.Language);
        Assert.False(rig.Host.ReplayRequested);
        Assert.False(rig.Services.ReplayActive);
    }

    // ---- through the runtime and the menus ----------------------------------------------------------------------------------------------------------------------

    private long _n;

    private void Step(AppRig rig, AppRuntime runtime, UiRoot ui, in InputFrame input)
    {
        runtime.Step(input with { Frame = ++_n }, _n * 16_666_667L, new DisplayList(2048));
        Assert.True(rig.Services.StoryActive || ui.Active, $"step {_n}: no story and no screen (a dead black screen)");
    }

    private void Run(AppRig rig, AppRuntime runtime, UiRoot ui, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            Step(rig, runtime, ui, new InputFrame());
        }
    }

    private void Press(AppRig rig, AppRuntime runtime, UiRoot ui, PadButtons b)
    {
        Step(rig, runtime, ui, new InputFrame { Held = b, Pressed = b, Repeated = b });
        Step(rig, runtime, ui, new InputFrame());
    }

    [Fact]
    public void AReplayThroughALanguageChangeStillEndsInTheGallery()
    {
        AppRig rig = NewRig();
        var ui = new UiRoot(GalleryKit.Data(), rig.Services);
        var runtime = new AppRuntime(rig.Services, ui, null, null);
        rig.Services.SetGlobal(110 + (485 >> 4), (ushort)(1 << (485 & 15)));   // the prologue was read: a title that takes over does not start it
        ui.Context.Gallery.Mode = (int)GalleryMode.Flowchart;                 // as GalleryScreen.ReplayFromChart leaves it
        ui.Context.Gallery.Node = 3;
        rig.Services.ReplayScene(100, 3);
        rig.Host.Vars.SetF(24, 1);                                             // F024: what the ARCHIVE scene sets (the rig has none); the carried page keeps it
        Run(rig, runtime, ui, 60);
        Assert.True(rig.Services.ReplayActive);
        Assert.NotEqual(0, rig.Host.History!.Staging.Used);

        rig.Services.Config.Language = (short)Language.Ja;
        rig.Services.ConfigChanged(ConfigChange.Language);
        Assert.True(rig.Services.ReplayActive);
        Assert.True(rig.Host.ReplayRequested);

        // the three pages, then the end of the scene: back in the gallery, the title (restarted, hidden) under it
        for (int i = 0; i < 200 && rig.Services.StoryActive; i++)
        {
            Press(rig, runtime, ui, PadButtons.Decide);
            Run(rig, runtime, ui, 8);
        }

        Assert.False(rig.Services.StoryActive);
        Run(rig, runtime, ui, 30);
        Assert.NotNull(ui.Find<GalleryScreen>());
        Assert.IsType<TitleScreen>(ui.Stack[0]);
    }

    [Fact]
    public void AfterAReplayLeftByTheTitleTabALoadedStoryOpensTheChartNotTheEndOfAReplay()
    {
        AppRig rig = NewRig();
        var ui = new UiRoot(GalleryKit.Data(), rig.Services);
        var runtime = new AppRuntime(rig.Services, ui, null, null);
        rig.Services.SetGlobal(110 + (485 >> 4), (ushort)(1 << (485 & 15)));
        rig.Begin();
        Assert.True(rig.Services.Save(0));

        // a replay picked in the gallery's chart, left by the system menu's Title tab
        ui.Context.Gallery.Mode = (int)GalleryMode.Flowchart;
        ui.Context.Gallery.Node = 3;
        rig.Services.ReplayScene(100, 3);
        Run(rig, runtime, ui, 60);
        Step(rig, runtime, ui, new InputFrame { ShellPressed = ShellActions.SystemMenu });
        Assert.IsType<SystemMenuScreen>(ui.Top);
        Run(rig, runtime, ui, 30);
        ui.Context.MenuTab = (int)MenuTab.Title;
        Press(rig, runtime, ui, PadButtons.Decide);
        for (int i = 0; i < 120 && ui.Find<SystemMenuScreen>() is not null; i++)
        {
            Run(rig, runtime, ui, 1);
        }

        Assert.False(rig.Services.StoryActive);
        Assert.IsType<TitleScreen>(ui.Stack[0]);

        // the title's LOAD GAME / CONTINUE: the saved story runs, and it is not a replay
        Assert.True(rig.Services.Load(0));
        Run(rig, runtime, ui, 30);
        Assert.False(ui.Active);
        Assert.False(rig.Services.ReplayActive);

        // Esc, FlowChart: the in-game chart's way (here "You cannot use the flowchart yet.": the rig's playthrough has no scene 1), not "return to the flowchart?"
        Step(rig, runtime, ui, new InputFrame { ShellPressed = ShellActions.SystemMenu });
        Assert.IsType<SystemMenuScreen>(ui.Top);
        Run(rig, runtime, ui, 30);
        ui.Context.MenuTab = (int)MenuTab.FlowChart;
        Press(rig, runtime, ui, PadButtons.Decide);
        Assert.IsNotType<ConfirmDialog>(ui.Top);
        Assert.True(ui.Top is FlowchartScreen or MessagePopup, ui.Top?.GetType().Name);
        Run(rig, runtime, ui, 60);
        Assert.True(rig.Services.StoryActive);
    }
}

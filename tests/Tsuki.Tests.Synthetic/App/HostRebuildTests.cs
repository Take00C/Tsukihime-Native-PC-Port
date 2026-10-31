using Tsuki.App.Play;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Tests.Synthetic.Ui;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package R1: a language change rebuilds the host (K3 open issue 12); when the story has not recorded a page yet (the prologue movie scene records nothing before its
/// movie) there was nothing to carry and the new host stayed NotStarted under a story that counted as active: a black screen no key could leave. The new host now
/// starts the scene over, and the runtime sends a story whose host has nothing to run back to the title.
/// </summary>
public sealed class HostRebuildTests
{
    // a scene that records no page before a long wait (a minute, no click skips it), as the prologue movie scene does before its _TOMP: its staging stays empty meanwhile
    private const string Waiting = "_WKST(F822,eq,1);_WTTM(60000,1);_WKST(F823,eq,0);_WNTY(1);_ZM00101($000002);_END_();";

    [Fact]
    public void ALanguageChangeBeforeTheFirstPageRestartsTheSceneOnTheNewHost()
    {
        var rig = new AppRig(Waiting);
        rig.Services.StartScene(100);
        rig.Idle(10);
        Assert.Equal(GamePhase.Running, rig.Host.Phase);
        Assert.Equal(0, rig.Host.History!.Staging.Used);
        GameHost before = rig.Host;

        rig.Services.Config.Language = (short)Language.Ja;
        rig.Services.ConfigChanged(ConfigChange.Language);

        Assert.NotSame(before, rig.Host);
        Assert.Equal(Language.Ja, rig.Services.Language);
        Assert.True(rig.Services.StoryActive);
        Assert.Equal(GamePhase.Running, rig.Host.Phase);
        Assert.Equal(100, rig.Host.SceneId);
        rig.Idle(5);
        Assert.Equal(GamePhase.Running, rig.Host.Phase);
    }

    [Fact]
    public void ALanguageChangeWithAPageOnShowCarriesThePage()
    {
        var rig = new AppRig();
        rig.Begin();
        rig.RunTo("beta line");
        rig.Services.Config.Language = (short)Language.Ja;
        rig.Services.ConfigChanged(ConfigChange.Language);
        Assert.Equal(GamePhase.Running, rig.Host.Phase);
        Assert.True(rig.Services.StoryActive);
        rig.Idle(30);
        Assert.Equal("beta line", rig.Text);
    }

    [Fact]
    public void TheRuntimeSendsAStoryWhoseHostHasNothingToRunBackToTheTitleOnceTheMenusClose()
    {
        // the safety net behind the restart: a host that could not restart (here: a replay whose ARCHIVE scene the rig does not have) must not leave a dead screen
        var rig = new AppRig(Waiting);
        var ui = new UiRoot(UiTestKit.Data(), rig.Services);
        var runtime = new AppRuntime(rig.Services, ui, null, null);
        rig.Services.StartScene(100);
        long n = 0;
        for (int i = 0; i < 10; i++)
        {
            runtime.Step(new InputFrame { Frame = ++n }, n * 16_666_667L, new DisplayList(2048));
        }

        Assert.True(rig.Services.StoryActive);
        Assert.Equal(GamePhase.Running, rig.Host.Phase);

        // the replay path: BeginReplay fails for a scene the flow table has not (scene 3000), so the rebuilt host stays NotStarted
        rig.Services.ReplayScene(100, 0);
        Assert.True(rig.Services.StoryActive);                              // the first host's StartReplay failed too, but the story stayed active; the host is NotStarted
        Assert.Equal(GamePhase.NotStarted, rig.Host.Phase);

        var log = new List<string>();
        runtime.Logged += log.Add;
        runtime.Step(new InputFrame { Frame = ++n }, n * 16_666_667L, new DisplayList(2048));
        Assert.Contains(log, l => l.Contains("story host not started", StringComparison.Ordinal));
        Assert.False(rig.Services.StoryActive);
        runtime.Step(new InputFrame { Frame = ++n }, n * 16_666_667L, new DisplayList(2048));
        Assert.Contains(ui.Stack, s => s is TitleScreen);
    }
}

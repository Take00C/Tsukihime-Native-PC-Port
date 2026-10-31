using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;
using Tsuki.Engine.Sound;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// What loading, rewinding, going back a scene and jumping in the backlog do to the interpreter (package K6, GameHost.Restore.cs): the position, the page, the
/// stage, the music and the pending waits come back from the history record.
/// </summary>
public sealed class RestoreGlueTests
{
    private static int LayerPicture(GameHost host, int layer) => host.Stage.Drawn[layer].Used != 0 ? host.Stage.Drawn[layer].Pic : -1;

    [Fact]
    public void ANewStoryHasAnEmptyRingAndShowsItsFirstPage()
    {
        var rig = new AppRig();
        rig.Begin();
        Assert.Equal(GamePhase.Running, rig.Host.Phase);
        Assert.Equal("alpha line", rig.Text);
        Assert.True(rig.Host.History!.Count >= 1);
    }

    [Fact]
    public void LoadingASlotBringsBackThePageItsLayerAndItsMusic()
    {
        var rig = new AppRig();
        rig.Begin();
        rig.RunTo("beta line");
        Assert.Equal(1, LayerPicture(rig.Host, 1));                           // IMG0001 is catalog entry 1
        Assert.Equal(5, rig.Host.Sound.ScriptTrack);
        Assert.True(rig.Services.Save(1));
        rig.RunTo("gamma line");
        rig.Host.TakeAudioCommands();

        Assert.True(rig.Services.Load(1));

        Assert.Equal(RestoreKind.Load, rig.Host.LastRestore!.Kind);
        Assert.Equal(5, rig.Host.Sound.ScriptTrack);
        AudioCommand[] commands = rig.Host.TakeAudioCommands();
        Assert.Contains(commands, c => c.Op == AudioOp.Play && c.Bank == AudioBank.Bgm && c.Entry == 5);
        Assert.Equal(AudioStreams.Count, commands.Count(c => c.Op == AudioOp.Stop));           // every stream stopped first
        rig.WaitFor("beta line");                                             // the saved statement runs again
        Assert.Equal(1, LayerPicture(rig.Host, 1));
        Assert.True(rig.Services.StoryActive);
    }

    [Fact]
    public void LoadingAnEarlierSlotUndoesTheLayerAndTheMusicOfTheLaterPages()
    {
        var rig = new AppRig();
        rig.Begin();
        Assert.True(rig.Services.Save(0));                                      // page 1: no layer, no music yet
        rig.RunTo("gamma line");
        Assert.Equal(1, LayerPicture(rig.Host, 1));
        Assert.Equal(5, rig.Host.Sound.ScriptTrack);

        rig.Host.TakeAudioCommands();
        Assert.True(rig.Services.Load(0));
        rig.WaitFor("alpha line");
        Assert.Equal(-1, LayerPicture(rig.Host, 1));
        Assert.Equal(-1, rig.Host.Sound.ScriptTrack);
        Assert.DoesNotContain(rig.Host.TakeAudioCommands(), c => c.Op == AudioOp.Play && c.Bank == AudioBank.Bgm);
    }

    [Fact]
    public void ALoadResumesTheScriptSoThePageAfterItFollows()
    {
        var rig = new AppRig();
        rig.Begin();
        rig.RunTo("beta line");
        rig.Services.Save(0);
        rig.RunTo("gamma line");
        rig.Services.Load(0);
        rig.RunTo("gamma line");                                               // clicking through the restored page reaches the next one
        Assert.Equal(1, rig.Host.RestoreCount);
    }

    [Fact]
    public void ARewindStepGoesBackOnePageAndShowsItsLineWaitingForTheClick()
    {
        var rig = new AppRig();
        rig.Begin();
        rig.RunTo("beta line");
        rig.RunTo("gamma line");
        Assert.True(rig.Host.CanRewind);

        Assert.True(rig.Host.RewindStep());
        Assert.Equal(RestoreKind.Rewind, rig.Host.LastRestore!.Kind);
        Assert.True(rig.Host.LastRestore.Redisplayed);
        Assert.Equal("beta line", rig.Text);
        Assert.NotEqual(0UL, rig.Host.Wait.Bits & WaitBits.Click);
        Assert.Equal(1, LayerPicture(rig.Host, 1));
        Assert.Equal(5, rig.Host.Sound.ScriptTrack);

        rig.Idle(60);
        Assert.Equal("beta line", rig.Text);                                    // it waits for the click, the script does not run on
    }

    [Fact]
    public void RewindingToThePageBeforeTheChangeUndoesTheLayerAndTheMusic()
    {
        var rig = new AppRig();
        rig.Begin();
        rig.RunTo("beta line");
        rig.RunTo("gamma line");
        Assert.True(rig.Host.RewindStep());
        Assert.True(rig.Host.RewindStep());
        Assert.Equal("alpha line", rig.Text);
        Assert.Equal(-1, LayerPicture(rig.Host, 1));
        Assert.Equal(-1, rig.Host.Sound.ScriptTrack);

        // the story goes on from the rewound page: the commands of the next page run again
        rig.RunTo("beta line");
        Assert.Equal(1, LayerPicture(rig.Host, 1));
        Assert.Equal(5, rig.Host.Sound.ScriptTrack);
    }

    [Fact]
    public void TheBacklogHoldsTheEarlierPagesNewestFirstAndAJumpRunsThePageAgain()
    {
        var rig = new AppRig();
        rig.Begin();
        rig.RunTo("beta line");
        rig.RunTo("gamma line");

        IReadOnlyList<Tsuki.Ui.BacklogPage> pages = rig.Services.Backlog;
        Assert.Equal(2, pages.Count);
        Assert.Equal("beta line", System.Text.Encoding.UTF8.GetString(pages[0].Messages[0].Text.Span));
        Assert.Equal("alpha line", System.Text.Encoding.UTF8.GetString(pages[1].Messages[0].Text.Span));

        rig.Services.BacklogJump(1);
        Assert.Equal(RestoreKind.BacklogJump, rig.Host.LastRestore!.Kind);
        rig.WaitFor("alpha line");
        Assert.Equal(-1, LayerPicture(rig.Host, 1));
    }

    [Fact]
    public void QuickSaveThenQuickLoadReturnsToTheSavedPageAndTheCooldownHolds()
    {
        var rig = new AppRig();
        rig.Begin();
        rig.RunTo("beta line");
        Assert.False(rig.Services.HasQuickSave);
        Assert.True(rig.Services.QuickSave());
        Assert.True(rig.Services.HasQuickSave);
        Assert.False(rig.Services.QuickSave());                                 // 300 frames of cooldown
        for (int i = 0; i < 300; i++)
        {
            rig.Services.Tick();
        }

        Assert.True(rig.Services.QuickSave());
        rig.RunTo("gamma line");
        Assert.True(rig.Services.QuickLoad());
        rig.WaitFor("beta line");
    }

    [Fact]
    public void AnEmptyOrDamagedSlotDoesNotLoadAndTheStoryIsUntouched()
    {
        var rig = new AppRig();
        rig.Begin();
        Assert.False(rig.Services.Load(5));                                     // empty
        Assert.False(rig.Services.Load(500));                                   // not a slot
        Assert.Equal("alpha line", rig.Text);
        Assert.Equal(0, rig.Host.RestoreCount);
    }

    [Fact]
    public void ChangingTheLanguageBuildsANewHostAndShowsTheSamePageAgain()
    {
        var rig = new AppRig();
        rig.Begin();
        rig.RunTo("beta line");
        GameHost before = rig.Host;
        rig.Services.Config.Language = (short)Language.Ja;
        rig.Services.ConfigChanged(Tsuki.Ui.ConfigChange.Language);

        Assert.NotSame(before, rig.Host);
        Assert.Equal(Language.Ja, rig.Services.Language);
        Assert.True(rig.Services.StoryActive);
        rig.WaitFor("beta line");
        Assert.Equal(1, LayerPicture(rig.Host, 1));
        Assert.Equal(5, rig.Host.Sound.ScriptTrack);
    }

    [Fact]
    public void ALanguageChangeAtTheTitleJustReplacesTheHost()
    {
        var rig = new AppRig();
        GameHost before = rig.Host;
        rig.Services.Config.Language = (short)Language.Zc;
        rig.Services.ConfigChanged(Tsuki.Ui.ConfigChange.Language);
        Assert.NotSame(before, rig.Host);
        Assert.Equal(GamePhase.NotStarted, rig.Host.Phase);
        Assert.False(rig.Services.StoryActive);
    }

    [Fact]
    public void PreviousSceneRestartsTheCurrentSceneAndNothingIsLeftBeforeTheFirst()
    {
        const string twoScenes =
            "_WKST(F822,eq,1);_ATSV();_WKST(F823,eq,0);_ZM00101($000002);_WKST(F823,eq,0);_ZM00102($000003);" +
            "_ATSV();_WKST(F823,eq,0);_ZM00103($000004);_WKST(F823,eq,0);_ZM00104($000005);_WKST(F823,eq,0);_ZM00105($000006);_END_();";
        var rig = new AppRig(twoScenes);
        rig.Begin();
        rig.RunTo("epsilon line");
        Assert.True(rig.Host.CanGoToPreviousScene);

        Assert.True(rig.Host.PreviousScene());
        Assert.Equal(RestoreKind.PreviousScene, rig.Host.LastRestore!.Kind);
        rig.WaitFor("gamma line");                                              // the start of the second scene runs again
        Assert.False(rig.Host.PreviousScene());                                 // the first scene is the bottom of the stack
    }

    [Fact]
    public void ANextSceneSkipRunsTheScriptToTheNextSceneStart()
    {
        const string twoScenes =
            "_WKST(F822,eq,1);_ATSV();_WKST(F823,eq,0);_ZM00101($000002);_WKST(F823,eq,0);_ZM00102($000003);" +
            "_ATSV();_WKST(F823,eq,0);_ZM00103($000004);_END_();";
        var rig = new AppRig(twoScenes);
        rig.Begin();
        bool ended = false;
        rig.Host.SceneSkipEnded += () => ended = true;
        Assert.True(rig.Host.RequestNextScene());
        rig.Idle(30);
        Assert.True(ended);
        Assert.True(rig.Host.Machine.Cursor > 0);
    }
}

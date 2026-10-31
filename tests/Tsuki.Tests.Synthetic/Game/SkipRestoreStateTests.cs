using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;

namespace Tsuki.Tests.Synthetic.Game;

/// <summary>
/// What the PS4 keeps, restores or resyncs at a next-scene skip's end, a rewind and a story movie, and the port had dropped (docs/status/knife/R1-engine.md):
/// the BGM and loop effects asked for during a skip, the BGM across a same-track rewind, the skip latch across a movie, the read flag and the screen effects
/// of a re-displayed line, and the auto save's commit hook.
/// </summary>
public sealed class SkipRestoreStateTests
{
    private static string Page(int sub, int line) => $"_WKST(F823,eq,0);_WNTY(1);_ZM001{sub:D2}($0000{line:D2});";

    private static (GameRig Rig, GameHost Host, SaveStore Store) Start(string script, bool readOnlyRule = false)
    {
        var rig = new GameRig().Line(2, "alpha line").Line(3, "beta line").Line(4, "gamma line").Line(5, "delta line").Script(script);
        GameHost host = rig.Build();
        var store = new SaveStore();
        host.AttachHistory(store);
        host.Config.FastForwardReadOnly = (ushort)(readOnlyRule ? 1 : 0);
        host.NewGame(100);
        return (rig, host, store);
    }

    private static string Text(GameHost host) => host.CurrentMessage is { } m ? Encoding.UTF8.GetString(m.Text) : string.Empty;

    /// <summary>Clicks through the story until <paramref name="text"/> is on show, then lets it finish revealing (as the app rig does).</summary>
    private static void RunTo(GameRig rig, GameHost host, string text)
    {
        for (int i = 0; i < 4000; i++)
        {
            if (Text(host) == text)
            {
                rig.Run(_ => false, 120);
                return;
            }

            if (i % 3 == 0)
            {
                rig.Click();
            }
            else
            {
                host.Step(default);
            }
        }

        throw new InvalidOperationException($"message '{text}' never came (on show: '{Text(host)}')");
    }

    // -- the next-scene skip ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ANextSceneSkipStartsTheMusicAndTheLoopEffectTheScriptAskedForDuringIt()
    {
        // adv_state.md 2.5 / 5.2: the _ATSV that ends the skip calls 0x7E620 (restart BGM 0x16545D0 and the loop SEs); during the skip the handlers only
        // updated the script-side state
        (GameRig rig, GameHost host, _) = Start("_WKST(F822,eq,1);_ATSV();" + Page(1, 2) + "_MPLY(5);_SEPL(3,1,SE0001);" + Page(2, 3) + "_ATSV();" + Page(3, 4) + "_END_();");
        RunTo(rig, host, "alpha line");
        host.TakeAudioCommands();
        Assert.Equal(-1, host.Sound.CurrentTrack);
        bool ended = false;
        host.SceneSkipEnded += () => ended = host.Sound.CurrentTrack == 5;      // the resync comes before the event

        Assert.True(host.RequestNextScene());
        host.Step(default);

        Assert.True(ended);
        Assert.False(host.SkipScene);
        Assert.Equal(5, host.Sound.ScriptTrack);
        Assert.Equal(5, host.Sound.CurrentTrack);
        AudioCommand[] commands = host.TakeAudioCommands();
        Assert.Contains(commands, c => c.Op == AudioOp.Play && c.Bank == AudioBank.Bgm && c.Entry == 5);
        Assert.Contains(commands, c => c.Op == AudioOp.Play && c.Stream == AudioStreams.Se(3) && c.Arg0 != 0);    // the loop SE, looping
        Assert.Equal(1u, host.Sound.Record(AudioStreams.Se(3)).State);
    }

    [Fact]
    public void ANextSceneSkipOverTheMusicsStopLeavesItSilentAndATrackThatPlaysOnIsNotRestarted()
    {
        (GameRig rig, GameHost host, _) = Start("_WKST(F822,eq,1);_ATSV();_MPLY(5);" + Page(1, 2) + "_MFAD(,,`001:1000);" + Page(2, 3) + "_ATSV();" + Page(3, 4) + "_ATSV();" + Page(4, 5) + "_END_();");
        RunTo(rig, host, "alpha line");
        Assert.Equal(5, host.Sound.CurrentTrack);
        host.TakeAudioCommands();
        Assert.True(host.RequestNextScene());                                 // over the _MFAD: the stream was left playing, the script says none
        host.Step(default);
        Assert.Equal(-1, host.Sound.ScriptTrack);
        Assert.Equal(-1, host.Sound.CurrentTrack);
        Assert.Contains(host.TakeAudioCommands(), c => c.Op == AudioOp.Stop && c.Stream < AudioStreams.FirstVoice);

        RunTo(rig, host, "gamma line");
        host.Sound.Run("_MPLY(7);");
        host.Step(default);                                                   // the frame's sound update performs the play
        host.TakeAudioCommands();
        Assert.True(host.RequestNextScene());                                 // nothing about the music in this scene: the stream goes on untouched
        host.Step(default);
        Assert.Equal(7, host.Sound.CurrentTrack);
        Assert.DoesNotContain(host.TakeAudioCommands(), c => c.Stream < AudioStreams.FirstVoice && c.Op is AudioOp.Stop or AudioOp.Play);
    }

    // -- the rewind -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ARewindOntoARecordWithTheSameTrackKeepsTheMusicPlayingAndADifferentTrackIsRestarted()
    {
        // knife game.rs restore_with: a rewind keeps the BGM when it is the same track (only the SEs and the voice stop); the PS4's BgmStart 0x98C0 early-out
        (GameRig rig, GameHost host, _) = Start("_WKST(F822,eq,1);_ATSV();_MPLY(5);_SEPL(2,1,SE0001);" + Page(1, 2) + Page(2, 3) + "_MPLY(6);" + Page(3, 4) + "_END_();");
        RunTo(rig, host, "beta line");
        Assert.Equal(5, host.Sound.CurrentTrack);
        int slot = host.Sound.CurrentSlot;
        host.TakeAudioCommands();

        Assert.True(host.RewindStep());
        AudioCommand[] commands = host.TakeAudioCommands();
        Assert.Equal("alpha line", Text(host));
        Assert.Equal(RestoreKind.Rewind, host.LastRestore!.Kind);
        Assert.Equal(5, host.LastRestore.BgmTrack);
        Assert.Equal(5, host.Sound.CurrentTrack);
        Assert.Equal(slot, host.Sound.CurrentSlot);
        Assert.Equal(1u, host.Sound.Record(slot).State);
        Assert.DoesNotContain(commands, c => c.Stream < AudioStreams.FirstVoice && c.Op is AudioOp.Stop or AudioOp.Play or AudioOp.Close);
        Assert.Contains(commands, c => c.Stream >= AudioStreams.FirstVoice && c.Op == AudioOp.Stop);                 // voices and effects do stop
        Assert.Contains(commands, c => c.Op == AudioOp.Play && c.Stream == AudioStreams.Se(2));                      // and the loop SE of the record restarts

        RunTo(rig, host, "gamma line");
        Assert.Equal(6, host.Sound.CurrentTrack);
        host.TakeAudioCommands();
        Assert.True(host.RewindStep());                                      // the record of "beta line" has track 5: cut and restart
        commands = host.TakeAudioCommands();
        Assert.Equal(5, host.Sound.CurrentTrack);
        Assert.Contains(commands, c => c.Op == AudioOp.Play && c.Bank == AudioBank.Bgm && c.Entry == 5);
        Assert.Contains(commands, c => c.Op == AudioOp.Stop && c.Stream < AudioStreams.FirstVoice);
    }

    [Fact]
    public void ALineReDisplayedByARewindIsReadWhenReadmaxSaysSoAndKeepsTheSkipLatch()
    {
        // adv_state.md 6: "Read" (0x75C90) is readmax[block] against the current marker ids, computed each time; the read-only rule reads it
        (GameRig rig, GameHost host, _) = Start("_WKST(F822,eq,1);_ATSV();" + Page(1, 2) + Page(2, 3) + Page(3, 4) + "_END_();", readOnlyRule: true);
        RunTo(rig, host, "gamma line");
        Assert.Equal(2, host.SystemData.GetReadmax(1));                       // markers (1,1) (1,2) (1,3) reached: the first two are recorded
        Assert.False(host.LineRead);                                          // the newest line is unread

        Assert.True(host.RewindStep());
        Assert.Equal("beta line", Text(host));
        Assert.True(host.Markers.Seen);
        Assert.True(host.Markers.IsRead);
        Assert.True(host.LineRead);
        Assert.True(host.CurrentMessage!.Read);

        host.Step(new InputFrame { ShellPressed = ShellActions.SkipToggle });
        Assert.True(host.SkipLatch);                                          // a read line may be skipped: the latch stays
        Assert.False(host.SkipStoppedByReadRule);
        Assert.Equal(1, host.Mode.SkipActive);
    }

    [Fact]
    public void ARewindBringsBackTheGhostAndFocusEffectsOfItsRecord()
    {
        // adv_state.md 3.1: History_Restore's common part calls 0x42DB0(F1100) and 0xA88B0(scene byte +0x19A2, 0)
        (GameRig rig, GameHost host, _) = Start("_WKST(F822,eq,1);_ATSV();" + Page(1, 2) + "_FADE(40,0,8);_FCLN(0,0,0,0,0,10);" + Page(2, 3) + "_FADE(41);_FCCL();" + Page(3, 4) + "_END_();");
        RunTo(rig, host, "beta line");
        Assert.Equal(4, host.GhostMode);
        Assert.Equal(1f, host.GhostAlpha);
        Assert.Equal(10, host.FocusEffect);
        Assert.Equal(10, host.Stage.Scene.Effect19A2);
        RunTo(rig, host, "gamma line");
        Assert.Equal(0, host.GhostMode);
        Assert.Equal(FocusMode.Off, host.FocusState);
        Assert.Equal(0, host.Stage.Scene.Effect19A2);

        Assert.True(host.RewindStep());                                       // the record of "beta line": F1100 = 4, the scene byte 10
        Assert.Equal("beta line", Text(host));
        Assert.Equal(4, host.F(1100));
        Assert.Equal(4, host.GhostMode);
        Assert.Equal(1f, host.GhostAlpha);
        Assert.True(host.Ghost.Wrapped);
        Assert.Equal(10, host.FocusEffect);
        Assert.Equal(FocusMode.FadingIn, host.FocusState);

        Assert.True(host.RewindStep());                                       // "alpha line" was pushed before the effects started: they stay off
        Assert.Equal(0, host.F(1100));
        Assert.Equal(0, host.GhostMode);
        Assert.Equal(FocusMode.Off, host.FocusState);
    }

    // -- the story movie --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSkipLatchComesBackWhenAStoryMovieEnds()
    {
        // wait_model.md 9: the movie state's enter callback saves 0x17DB230 and clears the latch, its exit restores it: skip resumes after the movie
        var rig = new GameRig().Script("_TOMP(4);_TOMW();_WKST(F1,eq,1);_END_();");
        GameHost host = rig.Build(new GameOptions { VirtualMovies = true });
        host.Config.FastForwardReadOnly = 0;
        host.NewGame(100);
        host.SetSkipLatch(true);
        host.Step(default);
        Assert.True(host.MovieActive);
        Assert.False(host.SkipLatch);
        host.Step(default);
        Assert.Equal(0, host.Mode.SkipActive);
        Assert.False(host.SkipLatch);

        int frames = rig.Run(h => !h.MovieActive, 3000);
        Assert.True(frames > 0);
        Assert.True(host.SkipLatch);
        host.Step(default);
        Assert.Equal(1, host.Mode.SkipActive);                                // skipping again from the first frame after the movie
        Assert.False(host.SkipStoppedByReadRule);
        rig.Run(h => h.F(1) == 1, 5);                                         // _TOMW lets the script go on in the frame after F36 fell
        Assert.Equal(1, host.F(1));
    }

    [Fact]
    public void AMovieStartedWithoutTheLatchEndsWithoutIt()
    {
        var rig = new GameRig().Script("_TOMP(4);_TOMW();_WKST(F1,eq,1);_END_();");
        GameHost host = rig.Build(new GameOptions { VirtualMovies = true });
        host.NewGame(100);
        rig.Run(h => h.MovieActive, 5);
        host.Step(new InputFrame { Pressed = PadButtons.R1 });                // a toggle during the movie does not outlive it
        rig.Run(h => !h.MovieActive, 3000);
        host.Step(default);
        Assert.False(host.SkipLatch);
        Assert.Equal(0, host.Mode.SkipActive);
    }

    // -- the auto save ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheAutoSaveOfAtsv8ReachesTheStoreAndTellsTheAppSoDoesEverySceneStart()
    {
        // adv_state.md 7.2: AutoSave 0x73B70 -> 0xA9450(100) -> SaveImage_Commit(2): the app commits the file on the sink's events
        (GameRig rig, GameHost host, SaveStore store) = Start("_WKST(F822,eq,1);_ATSV(8);" + Page(1, 2) + "_ATSV();" + Page(2, 3) + "_END_();");
        var events = new List<string>();
        AdvHistorySink sink = host.HistorySink!;
        sink.AutoSaved += () => events.Add(store.HasSave(SaveStore.AutoSaveFirst) ? "auto" : "auto?");
        sink.SceneStackPushed += () => events.Add("scene");
        RunTo(rig, host, "alpha line");
        Assert.Equal(["scene", "auto"], events);
        RunTo(rig, host, "beta line");
        Assert.Equal(["scene", "auto", "scene"], events);
    }
}

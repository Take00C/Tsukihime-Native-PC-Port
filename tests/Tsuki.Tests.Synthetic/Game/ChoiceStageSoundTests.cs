using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Movie;

namespace Tsuki.Tests.Synthetic.Game;

/// <summary>Choices, the stage waits, sound, movies and the progress marks on hand-made scripts.</summary>
public sealed class ChoiceStageSoundTests
{
    private const string ChoiceScript =
        "_WKST(F822,eq,1);_WKAD(F823,1);_SELR(0,$000002);_SELR(1,$000003);_SELR(2,$000004,1);_SELD(_D000,5);_GOTS(_D000,A,B,C);"
        + "_ZZ00101(A);_WKST(F10,eq,10);_END_();_ZZ00201(B);_WKST(F10,eq,20);_END_();_ZZ00301(C);_WKST(F10,eq,30);_END_();";

    private static GameRig Rig(string script) => new GameRig()
        .Line(2, "left").Line(3, "right").Line(4, "locked")
        .Line(43960, "1. ").Line(43961, "2. ").Line(43962, "3. ")
        .Script(script);

    // -- choices ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ASelDOpensTheListWithNumberedLinesAndLocksTheDisabledOne()
    {
        GameRig rig = Rig(ChoiceScript);
        GameHost host = rig.Build();
        host.NewGame(100);
        rig.Run(h => h.ChoiceOpen, 50);
        Assert.Equal(ChoiceKind.List, host.ChoiceKind);
        Assert.Equal(3, host.ChoiceLines.Count);
        Assert.Equal("1. left", Encoding.UTF8.GetString(host.ChoiceLines[0].Text));
        Assert.Equal("3. locked", Encoding.UTF8.GetString(host.ChoiceLines[2].Text));
        Assert.True(host.ChoiceLines[2].Locked);
        Assert.Equal(5, host.ChoicePoint);
        Assert.Equal(-1, host.ChoiceCursor);
        Assert.False(host.Choose(2));             // a locked line cannot be decided
    }

    [Fact]
    public void DecidingALineStoresItsValueTakesTheBranchAndEndsAfterFiftyOneFrames()
    {
        GameRig rig = Rig(ChoiceScript);
        GameHost host = rig.Build();
        host.NewGame(100);
        rig.Run(h => h.ChoiceOpen, 50);
        var decided = new List<(ChoiceKind Kind, int Point, int Value, int Cursor)>();
        host.ChoiceDecided += (k, p, v, c) => decided.Add((k, p, v, c));
        Assert.True(host.Choose(1));
        Assert.Equal([(ChoiceKind.List, 5, 1, 1)], decided);
        Assert.Equal(1, host.F(0));                                    // _D000 = F0 receives the line's value
        Assert.Equal(2, host.SystemData.GetChoiceTaken(5));            // the taken bit 1 of choice point 5
        int steps = 0;
        while (host.ChoiceOpen)
        {
            host.Step(default);
            steps++;
        }

        Assert.Equal(51, steps);                                       // system_flow.md 5.2: the decided phase ends on frame 51
        rig.Run(h => h.Phase == GamePhase.Ended, 20);
        Assert.Equal(20, host.F(10));                                  // GOTS took the second label
    }

    [Fact]
    public void TheCursorMovesWithTheDpadSkippingLockedLinesAndDecideTakesIt()
    {
        GameRig rig = Rig(ChoiceScript);
        GameHost host = rig.Build();
        host.NewGame(100);
        rig.Run(h => h.ChoiceSelectable, 80);
        host.Step(new InputFrame { Repeated = PadButtons.Down });
        Assert.Equal(0, host.ChoiceCursor);
        host.Step(new InputFrame { Repeated = PadButtons.Down });
        Assert.Equal(1, host.ChoiceCursor);
        host.Step(new InputFrame { Repeated = PadButtons.Down });
        Assert.Equal(0, host.ChoiceCursor);                            // wraps over the locked line 2
        host.Step(new InputFrame { Pressed = PadButtons.Decide });
        Assert.Equal(3, host.ChoicePhase);
        rig.Run(h => h.Phase == GamePhase.Ended, 100);
        Assert.Equal(10, host.F(10));
    }

    [Fact]
    public void ASelaWhileTheSavedMarkerIsSetOpensNothingAndClearsTheMarker()
    {
        GameRig rig = Rig("_WKST(F1118,eq,1);_SELA(_D001);_WKST(F10,eq,1);_END_();");
        GameHost host = rig.Build();
        host.NewGame(100);
        rig.Run(h => h.Phase == GamePhase.Ended, 50);
        Assert.Equal(1, host.F(10));
        Assert.Equal(0, host.F(1118));

        GameRig rig2 = Rig("_SELA(_D001);_WKST(F10,eq,1);_END_();");
        GameHost host2 = rig2.Build();
        host2.NewGame(100);
        rig2.Run(h => h.ChoiceOpen, 20);
        Assert.Equal(ChoiceKind.SavePrompt, host2.ChoiceKind);
        bool saved = false;
        host2.SavePromptAccepted += () => saved = true;
        host2.Choose(0);                                              // "no" (button 0; button 1 is "yes" and opens the save screen)
        rig2.Run(h => h.Phase == GamePhase.Ended, 100);
        Assert.False(saved);
        Assert.Equal(1, host2.F(10));
    }

    [Fact]
    public void ASelsStoresTheButtonInItsVariable()
    {
        GameRig rig = Rig("_SELS(_D002);_END_();");
        GameHost host = rig.Build();
        host.NewGame(100);
        rig.Run(h => h.ChoiceOpen, 20);
        Assert.Equal(ChoiceKind.TwoButtons, host.ChoiceKind);
        host.Choose(1);
        rig.Run(h => h.Phase == GamePhase.Ended, 100);
        Assert.Equal(1, host.F(2));
    }

    // -- the stage waits ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void FadsYieldsAndReleasesTheScriptTwoFramesAfterAnInstantTransition()
    {
        // wait_model.md 10.1: _FADS(1,0,0) holds the script for 2 frames
        var rig = new GameRig().Script("_STCH(1,IMG0001,240,136);_FADS(1,0,0);_WKST(F1,eq,1);_END_();");
        GameHost host = rig.Build();
        host.NewGame(100);
        host.Step(default);
        Assert.True((host.Wait.Bits & WaitBits.Fads) != 0);
        Assert.Equal(0, host.F(1));
        host.Step(default);
        Assert.Equal(0, host.F(1));
        host.Step(default);
        Assert.Equal(1, host.F(1));
        Assert.Equal(2, host.Stage.Scene.GetPic(1) >= 0 ? 2 : -1);
    }

    [Fact]
    public void StcfWaitsForTheAlphaTweenOfItsLayer()
    {
        var rig = new GameRig().Script("_STCH(1,IMG0001,240,136,1000,0);_FADS(1,0,0);_STGS(1,0,255,500,0);_STCF(1,8);_WKST(F1,eq,1);_END_();");
        GameHost host = rig.Build();
        host.NewGame(100);
        int frames = rig.Run(h => h.F(1) == 1, 200);
        Assert.InRange(frames, 30, 40);               // 500 ms = 30 frames
        Assert.Equal(0, host.Stage.Drawn[1].TwAlpha.Counter);
    }

    [Fact]
    public void StcfWaitsOnlyForItsOwnLayerNotForALayerAnEarlierStcfNamed()
    {
        // wait_model.md 10.2: _STCF(l) sets bit l of the one word 0x1653E30 and Adv_Update clears it when the layer's condition ends. The later _STCF(0,8)
        // must not wait for layer 5's new 10 s fade only because an earlier _STCF(5) named that layer (R1-engine 1: minute-long black screens)
        var rig = new GameRig().Script(
            "_STCH(0,IMG0001,240,136,1000,0);_STCH(5,IMG0002,240,136,1000,0);_FADS(1,0,0);"
            + "_STGS(5,0,255,100,0);_STCF(5);_WKST(F2,eq,1);"
            + "_STGS(5,255,0,10000,0);"
            + "_STGS(0,0,255,800,0);_STCF(0,8);_WKST(F1,eq,1);_END_();");
        GameHost host = rig.Build();
        host.NewGame(100);
        int first = rig.Run(h => h.F(2) == 1, 200);
        Assert.InRange(first, 5, 20);                 // the 100 ms fade of layer 5: its own wait
        Assert.NotEqual(0UL, host.Wait.Bits & WaitBits.Stcf);
        Assert.Equal(1UL, host.Wait.StcfLayers);      // only layer 0 is registered now
        Assert.Equal(8, host.Wait.StcfMask[0]);
        int second = rig.Run(h => h.F(1) == 1, 2000);
        Assert.InRange(second, 40, 60);               // 800 ms = 48 frames: layer 0's fade, not layer 5's 10 s one
        Assert.NotEqual(0, host.Stage.Drawn[5].TwAlpha.Counter);
    }

    [Fact]
    public void StcfWithoutAMaskWaitsForEveryTweenOfItsLayerAndAnOutOfRangeLayerYieldsOnce()
    {
        var rig = new GameRig().Script("_STCH(1,IMG0001,240,136,1000,0);_FADS(1,0,0);_STGS(1,0,255,500,0);_STCF(1);_WKST(F1,eq,1);_STCF(40,8);_WKST(F2,eq,1);_END_();");
        GameHost host = rig.Build();
        host.NewGame(100);
        int frames = rig.Run(h => h.F(1) == 1, 200);
        Assert.InRange(frames, 30, 40);
        Assert.Equal(0x3F, host.Wait.StcfMask[1]);
        rig.Run(h => h.F(2) == 1, 5);
        Assert.Equal(1, host.F(2));                   // a layer outside 0..39 registers nothing; the yield still happens
        Assert.Equal(0UL, host.Wait.StcfLayers);
    }

    [Fact]
    public void LayerChangesOpenOneBatchThatTheNextCommitPointClosesAndFadsClosesToo()
    {
        var sink = new RecordingSink();
        var rig = new GameRig().Script("_STCH(1,IMG0001,240,136);_STCP(1,5);_WTTM(1,1);_STCH(2,IMG0002,240,136);_FADS(1,0,0);_END_();");
        GameHost host = rig.Build();
        host.Sink = sink;
        host.NewGame(100);
        rig.Run(h => h.Phase == GamePhase.Ended, 20);
        Assert.Equal([HistoryKind.LayerBatchStart, HistoryKind.LayerCommit, HistoryKind.LayerBatchStart, HistoryKind.LayerBatchStart], sink.Kinds);
        Assert.Equal(0, host.F(991));
    }

    [Fact]
    public void FadsFourthArgumentHidesTheText()
    {
        var rig = new GameRig().Script("_FADS(1,0,0,1);_END_();");
        GameHost host = rig.Build();
        host.NewGame(100);
        host.Step(default);
        Assert.Equal(1, host.F(1103));
    }

    // -- sound and movies ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void MusicAndEffectsGoThroughTheSoundModelAndMarkTheTrackAsHeard()
    {
        var rig = new GameRig().Script("_MPLY(5);_SEPL(3,0,SE0001);_END_();");
        GameHost host = rig.Build();
        host.NewGame(100);
        rig.Run(h => h.Phase == GamePhase.Ended, 10);
        AudioCommand[] commands = host.TakeAudioCommands();
        Assert.Contains(commands, c => c.Op == AudioOp.Play && c.Bank == AudioBank.Bgm && c.Entry == 5);
        Assert.Contains(commands, c => c.Op == AudioOp.Play && c.Stream == AudioStreams.Se(3) && c.Bank == AudioBank.Se);
        Assert.True(host.SystemData.IsBgmHeard(5));
    }

    [Fact]
    public void AVoiceStartsWithItsMessageAndVcwtWaitsForItToEnd()
    {
        var rig = new GameRig().Line(1, "hello").Script("_WKST(F822,eq,1);_WKAD(F823,1);_VPLY(A00_00_0001,00001);_ZM00101($000001@e);_VCWT();_WKST(F11,eq,1);_END_();");
        GameHost host = rig.Build();
        host.NewGame(100);
        host.Step(default);
        AudioCommand[] commands = host.TakeAudioCommands();
        Assert.Contains(commands, c => c.Op == AudioOp.Play && c.Bank == AudioBank.Voice && c.Entry == 1 && c.Stream is >= 2 and < 6);
        Assert.NotEqual(0, host.Sound.VoiceState);
        int frames = rig.Run(h => h.F(11) == 1, 400);
        Assert.InRange(frames, 100, 160);              // the virtual clip lasts 120 frames
        Assert.Equal(0, host.Sound.VoiceState);
    }

    [Fact]
    public void MfadWaitsItsFadeInTheTimer()
    {
        var rig = new GameRig().Script("_MPLY(5);_MFAD(,,`001:1600);_WKST(F1,eq,1);_END_();");
        GameHost host = rig.Build();
        host.NewGame(100);
        int frames = rig.Run(h => h.F(1) == 1, 400);
        Assert.InRange(frames, 98, 102);               // 1600 ms / 16
    }

    [Fact]
    public void TompStartsTheMovieAndTomwWaitsOnF36UntilTheStateEnds()
    {
        var rig = new GameRig().Script("_TOMP(4);_TOMW();_WKST(F1,eq,1);_END_();");
        GameHost host = rig.Build();
        host.NewGame(100);
        host.Step(default);
        Assert.Equal(1, host.F(36));
        Assert.Equal(1, host.F(1103));
        MovieCommand[] commands = host.TakeMovieCommands();
        Assert.Equal(MovieCommand.Open(MovieSlots.FullScreen, MovieTable.Remap(4, Language.En), false, 0.4f).Op, commands[0].Op);
        Assert.Equal(MovieTable.Remap(4, Language.En), commands[0].MovieIndex);
        Assert.True(host.MovieActive);
        int frames = rig.Run(h => h.F(1) == 1, 3000);
        Assert.Equal(0, host.F(36));
        Assert.False(host.MovieActive);
        Assert.True((host.Vars.GetG(5) & 4) != 0);     // movie_seen bit 2 (the prologue)
        Assert.True(frames > 100);
        Assert.Contains(host.TakeMovieCommands(), c => c.Op == MovieOp.Stop);
    }

    [Fact]
    public void TheCancelButtonSkipsAMovieOnlyOnceItWasWatched()
    {
        static int Frames(bool seen)
        {
            var rig = new GameRig().Script("_TOMP(4);_TOMW();_WKST(F1,eq,1);_END_();");
            GameHost host = rig.Build(new GameOptions { VirtualMovies = true });
            if (seen)
            {
                host.Vars.SetG(5, 4);
            }

            host.NewGame(100);
            int n = 0;
            while (host.F(1) != 1 && n < 5000)
            {
                host.Step(new InputFrame { Pressed = PadButtons.Cancel });
                n++;
            }

            return n;
        }

        Assert.True(Frames(true) <= Frames(false));
    }

    // -- the progress marks ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void RdstRtstAndRdedMarkTheFlowchartNodes()
    {
        var rig = new GameRig().Script("_RDST(485);_RTST(40);_RDED(40);_RDED(41);_END_();");
        GameHost host = rig.Build();
        host.NewGame(100);
        rig.Run(h => h.Phase == GamePhase.Ended, 20);
        Assert.True(host.IsSceneReached(485));
        Assert.False(host.IsSceneReached(486));
        Assert.Equal((1 << (40 & 7)) & 0xFF, host.Vars.GetF(201 + (40 >> 3)) & (1 << (40 & 7)));
        Assert.False(host.IsSceneDone(40));            // RDED needs n == F110, and RTST sets F110 only for the QA nodes 533..557 or a replay
    }

    [Fact]
    public void RtstOfAQaNodeSetsTheCurrentNodeAndRdedCompletesIt()
    {
        var rig = new GameRig().Script("_RTST(540);_RDED(540);_END_();");
        GameHost host = rig.Build();
        host.NewGame(100);
        rig.Run(h => h.Phase == GamePhase.Ended, 20);
        Assert.Equal(540, host.F(110));
        Assert.True(host.IsSceneDone(540));
    }

    [Fact]
    public void ASceneJumpMarksTheEdgeBetweenTheNodesAndMakesTheTargetTheCurrentNode()
    {
        var rig = new GameRig().Script("_JUMP(101);").Script("_END_();");
        GameHost host = rig.Build();
        host.NewGame(100);
        // nodes are looked up through the flow tables of the rig; add them and the edge before the jump runs
        rig.Flow.NodeMap[0] = 10;
        rig.Flow.NodeMap[1] = 11;
        rig.Flow.EdgeRows.Add(new FlowEdgeRow(10, 11, [600], [601]));
        rig.Run(h => h.Phase == GamePhase.Ended, 20);
        Assert.True(host.IsEdgeSeen(600));
        Assert.Equal(11, host.F(110));
        Assert.True((host.Vars.GetF(600 + ((601 - 564) >> 3)) & (1 << ((601 - 564) & 7))) != 0);
    }

    [Fact]
    public void AFlowchartJumpKeepsTheListedWordsClearsTheRestAndStartsTheScene()
    {
        var rig = new GameRig().Script("_END_();").Script("_WKST(F70,eq,4);_END_();");
        rig.Build();
        rig.Flow.NodeList.Add(new FlowNodeRow(3, 20, 101, -1, 1));
        rig.Flow.KeepWords.AddRange([110, 26, 4]);
        GameHost host = rig.Host;
        host.NewGame(100);
        host.Vars.SetF(9, 99);
        host.Vars.SetF(4, 44);
        Assert.True(host.JumpToNode(20));
        Assert.Equal(1, host.Script);
        Assert.Equal(20, host.F(110));
        Assert.Equal(101, host.F(26));
        Assert.Equal(0, host.F(9));                    // not on the keep list
        Assert.Equal(44, host.F(4));                   // on it
        Assert.False(host.JumpToNode(999));
    }

    private sealed class RecordingSink : IHistorySink
    {
        public List<HistoryKind> Kinds { get; } = [];

        public void Push(HistoryKind kind, ScriptPosition position, ScriptPosition resume) => Kinds.Add(kind);

        public void SceneStackPush()
        {
        }

        public void AutoSave()
        {
        }

        public void SetMessageText(ReadOnlySpan<byte> text)
        {
        }

        public void OnSceneJump(int fromScript, int toSceneId)
        {
        }

        public void OnScriptEnd(EndReason reason)
        {
        }

        public void OnChoiceTaken(int choiceId, int option)
        {
        }

        public void OnMessageShown()
        {
        }

        public void OnVoiceStarted(int voiceId, short character, sbyte speakerRow)
        {
        }

        public void UpdateStaging()
        {
        }
    }
}

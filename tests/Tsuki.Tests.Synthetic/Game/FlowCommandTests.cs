using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;

namespace Tsuki.Tests.Synthetic.Game;

/// <summary>The flow commands of the interpreter on hand-made scripts: jumps, scene ends, the platform and system branches, unknown names.</summary>
public sealed class FlowCommandTests
{
    private static GameHost Start(GameRig rig, int script = 0)
    {
        GameHost host = rig.Build();
        host.NewGame(100 + script);
        return host;
    }

    [Fact]
    public void IfJumpsWhenItsConditionIsFalse()
    {
        // script.md 4.1 lists "then goto"; the VM (verified under emulation) and knife jump when the condition is FALSE
        var rig = new GameRig().Script("_IF__(G003,ne,1,LV1);_WKST(F10,eq,1);_END_();_ZYf0001(LV1);_WKST(F10,eq,2);_END_();");
        GameHost host = Start(rig);
        host.Vars.SetG(3, 1);
        rig.Run(h => h.Phase == GamePhase.Ended);
        Assert.Equal(2, host.F(10));

        var rig2 = new GameRig().Script("_IF__(G003,ne,1,LV1);_WKST(F10,eq,1);_END_();_ZYf0001(LV1);_WKST(F10,eq,2);_END_();");
        GameHost host2 = Start(rig2);
        rig2.Run(h => h.Phase == GamePhase.Ended);
        Assert.Equal(1, host2.F(10));
    }

    [Fact]
    public void GWordsSetBeforeANewGameSurviveIt()
    {
        var rig = new GameRig().Script("_IF__(G003,ne,1,LV1);_WKST(F10,eq,1);_END_();_ZYf0001(LV1);_WKST(F10,eq,2);_END_();");
        GameHost host = rig.Build();
        host.Vars.SetG(3, 1);
        host.NewGame(100);
        Assert.Equal(1, host.Vars.GetG(3));
        rig.Run(h => h.Phase == GamePhase.Ended);
        Assert.Equal(2, host.F(10));
    }

    [Fact]
    public void JumpEntersTheSceneAtItsLabelAndEndStops()
    {
        var rig = new GameRig()
            .Script("_WKST(F1,eq,1);_JUMP(101,LBL_A);_WKST(F1,eq,9);_END_();")
            .Script("_WKST(F1,eq,5);_END_();_ZZ00101(LBL_A);_WKST(F2,eq,7);_END_();");
        GameHost host = Start(rig);
        rig.Run(h => h.Phase == GamePhase.Ended);
        Assert.Equal(1, host.Script);
        Assert.Equal(1, host.F(1));
        Assert.Equal(7, host.F(2));
        Assert.Equal(EndReason.End, host.EndReason);
        Assert.Equal([0, 1], host.ScriptsReached.ToArray());
    }

    [Fact]
    public void JumpYieldsSoTheTargetStartsOnTheNextFrame()
    {
        var rig = new GameRig().Script("_JUMP(101);_END_();").Script("_WKST(F2,eq,7);_END_();");
        GameHost host = Start(rig);
        host.Step(default);
        Assert.Equal(0, host.F(2));
        Assert.Equal(GamePhase.Running, host.Phase);
        host.Step(default);
        Assert.Equal(7, host.F(2));
        Assert.Equal(GamePhase.Ended, host.Phase);
    }

    [Fact]
    public void JumpToAnUnknownSceneWithoutStartScriptEndsTheStory()
    {
        var rig = new GameRig().Script("_JUMP(9999);_END_();");
        GameHost host = Start(rig);
        rig.Run(h => h.Phase == GamePhase.Ended);
        Assert.Equal(EndReason.MissingScene, host.EndReason);
    }

    [Theory]
    [InlineData("_END_();", EndReason.End)]
    [InlineData("_RTM_();", EndReason.ReturnToTitle)]
    [InlineData("_RTMN();", EndReason.ReturnKeepBgm)]
    public void TheScriptEndsHaveTheirReasons(string end, EndReason reason)
    {
        var rig = new GameRig().Script(end + "_WKST(F5,eq,1);");
        GameHost host = Start(rig);
        rig.Run(h => h.Phase == GamePhase.Ended);
        Assert.Equal(reason, host.EndReason);
        Assert.Equal(0, host.F(5));
    }

    [Fact]
    public void TheScriptEndCleanupStopsTheBgmUnlessTheKeepFlagIsSet()
    {
        static bool Stopped(string script)
        {
            var rig = new GameRig().Script(script);
            GameHost host = rig.Build();
            host.NewGame(100);
            rig.Run(h => h.Phase == GamePhase.Ended);
            return host.TakeAudioCommands().Any(c => c.Op == AudioOp.Stop && c.Stream < AudioStreams.FirstVoice);
        }

        Assert.True(Stopped("_MPLY(5);_WTTM(100,1);_RTM_();"));
        Assert.False(Stopped("_MPLY(5);_WTTM(100,1);_RTMN();"));
        Assert.False(Stopped("_MPLY(5);_WTTM(100,1);_RTM2();_RTM_();"));
    }

    [Fact]
    public void LanguageAndSystemBranchesFollowTheirConditions()
    {
        // IFLA(language, label): on in that language, else to the label; IFSC jumps unless a gallery replay runs; IFUR jumps when every ending is done
        var rig = new GameRig().Script(
            "_IFLA(1,L1);_WKST(F1,eq,1);_ZZ00101(L1);_IFLA(0,L2);_WKST(F2,eq,1);_ZZ00201(L2);_IFSC(,L3);_WKST(F3,eq,1);_ZZ00301(L3);_IFUR(1,L4);_WKST(F4,eq,1);_ZZ00401(L4);_END_();");
        GameHost host = Start(rig);
        rig.Run(h => h.Phase == GamePhase.Ended);
        // English is language 1: IFLA(1) goes on, IFLA(0) jumps; IFSC jumps outside the gallery; IFUR goes on while an ending is open
        Assert.Equal(1, host.F(1));
        Assert.Equal(0, host.F(2));
        Assert.Equal(0, host.F(3));
        Assert.Equal(1, host.F(4));
    }

    [Fact]
    public void IfurJumpsOnceEveryEndingNodeIsDone()
    {
        var rig = new GameRig().Script("_IFUR(1,L4);_WKST(F4,eq,1);_ZZ00401(L4);_END_();");
        GameHost host = Start(rig);
        for (int n = 533; n <= 556; n++)
        {
            host.Vars.SetG(300 + (n >> 4), (ushort)(host.Vars.GetG(300 + (n >> 4)) | (1 << (n & 15))));
        }

        rig.Run(h => h.Phase == GamePhase.Ended);
        Assert.Equal(0, host.F(4));
    }

    [Fact]
    public void PlatformBranchesTakeThePs4Path()
    {
        // IFP4(,A) is a no-op on the PS4, IFNX(,B) always jumps (script.md 4.1): the PS4 variant runs, the Switch variant is skipped
        var rig = new GameRig().Script("_IFP4(,A);_WKST(F1,eq,1);_ZZ00101(A);_IFNX(,B);_WKST(F2,eq,1);_ZZ00201(B);_END_();");
        GameHost host = Start(rig);
        rig.Run(h => h.Phase == GamePhase.Ended);
        Assert.Equal(1, host.F(1));
        Assert.Equal(0, host.F(2));
    }

    [Fact]
    public void DocumentedNoOpsAndEngineOnlyNamesDoNothing()
    {
        var rig = new GameRig().Script(
            "_SNX4(0,0,0);_FANX(1,0,0);_STQK(1);_DMMY(x);_NOP_();_WTSV();_TRPY(5);_XSTP();_XRSM();_WKST(F1,eq,1);_END_();");
        GameHost host = Start(rig);
        var trophies = new List<int>();
        host.TrophyUnlocked += trophies.Add;
        rig.Run(h => h.Phase == GamePhase.Ended);
        Assert.Equal(1, host.F(1));
        Assert.Equal([5], trophies);
        Assert.Empty(host.UnknownCommands);
    }

    [Fact]
    public void AnUnknownNameIsCountedWhenRecordedAndThrowsWhenStrict()
    {
        var rig = new GameRig().Script("_ABCD(1);_WKST(F1,eq,1);_END_();");
        GameHost host = Start(rig);
        rig.Run(h => h.Phase == GamePhase.Ended);
        Assert.Equal(1, host.UnknownCommands["ABCD"]);
        Assert.Equal(1, host.F(1));

        var strict = new GameRig().Script("_ABCD(1);_END_();");
        GameHost h2 = strict.Build(new GameOptions { StrictCommands = true });
        h2.NewGame(100);
        Assert.Throws<UnknownCommandException>(() => h2.Step(default));
    }

    [Fact]
    public void TheNextSceneSkipRunsTheScriptWithoutWaitsUntilTheNextSceneStart()
    {
        var rig = new GameRig().Script("_WTTM(60000,1);_WKST(F1,eq,1);_WTTM(60000,1);_ATSV();_WKST(F2,eq,1);_WTTM(60000,1);_END_();");
        GameHost host = Start(rig);
        host.Step(default);
        Assert.Equal(0, host.F(1));
        Assert.True(host.RequestNextScene());
        var ended = 0;
        host.SceneSkipEnded += () => ended++;
        host.Step(default);
        Assert.False(host.SkipScene);
        Assert.Equal(1, ended);
        Assert.Equal(1, host.F(1));
        Assert.Equal(1, host.F(2));                    // the statements after _ATSV run in the same call; the next wait then holds again
        Assert.Equal(GamePhase.Running, host.Phase);
    }

    [Fact]
    public void ANewGameClearsTheFAndZWordsAndKeepsTheGWords()
    {
        var rig = new GameRig().Script("_WKST(F7,eq,3);_WKST(Z4,eq,5);_WKST(G9,eq,6);_END_();");
        GameHost host = Start(rig);
        rig.Run(h => h.Phase == GamePhase.Ended);
        Assert.Equal(3, host.F(7));
        host.NewGame(100);
        Assert.Equal(0, host.F(7));
        Assert.Equal(0, host.Vars.GetZ(4));
        Assert.Equal(6, host.Vars.GetG(9));
    }

    [Fact]
    public void NewGameStartsThePrologueFirstAndStartScriptOnceItWasRead()
    {
        var rig = new GameRig().Script("_END_();");
        GameHost host = Start(rig);
        Assert.Equal(260, host.NewGameSceneId());
        host.Vars.SetG(110 + (485 >> 4), (ushort)(1 << (485 & 15)));
        Assert.Equal(110, host.NewGameSceneId());
    }

    [Fact]
    public void EveryNameOfTheMainInterpreterTableIsClassified()
    {
        string[] engineMain =
        [
            "ATSV", "CLO2", "CLO3", "CLO4", "CLO5", "EIF_", "ELS_", "END_", "FADE", "FADS", "FCCL", "FCLN", "FCSP", "FLNT", "FLOP", "FLSP",
            "GOTO", "GOTS", "IFF_", "IFJP", "IFLA", "IFNX", "IFSC", "IFUR", "IF__", "MFAD", "MPLY", "MSAD", "MSQK", "MSTP", "MVOL", "PGST",
            "RDAS", "RDED", "RDST", "RTM2", "RTMN", "RTM_", "RTST", "SACL", "SBAS", "SCH2", "SEAC", "SEFD", "SEFI", "SELA", "SELD", "SELR",
            "SELS", "SEP3", "SEPL", "SEPN", "SESP", "SEVL", "SGMA", "SHAF", "SHAK", "SHAS", "SHK2", "SHOK", "SHS2", "SNOW", "SNX2", "SNX3",
            "SOXY", "SPFC", "SPMA", "SPRC", "SPRD", "SQK2", "SQS2", "SSS2", "SSW2", "STBR", "STCC", "STCF", "STCG", "STCH", "STCL", "STCP",
            "STEC", "STGS", "STMA", "STNS", "STPV", "STRA", "STRC", "STRR", "STRT", "STTR", "STZ4", "TOMP", "TOMW", "TRPY", "VCLR", "VCWT",
            "VPL2", "VPLY", "WKAD", "WKBO", "WKOF", "WKOR", "WKST", "WNTY", "WTSV", "WTTM", "WTTR", "WT__", "XRSM", "XSTP",
        ];
        Assert.Equal(110, engineMain.Length);
        foreach (string n in engineMain)
        {
            Assert.NotEqual(Cmd.Unknown, GameCommands.Classify(CommandName.FromAscii(n)));
        }

        foreach (string n in new[] { "SNX4", "FANX", "IFP4", "DMMY", "STQK", "NOP_" })
        {
            Assert.NotEqual(Cmd.Unknown, GameCommands.Classify(CommandName.FromAscii(n)));
        }

        Assert.Equal(Cmd.Jump, GameCommands.Classify(CommandName.FromAscii("JUMP")));
        Assert.Equal(Cmd.Jump, GameCommands.Classify(CommandName.FromAscii("JXYZ")));
        Assert.Equal(Cmd.Unknown, GameCommands.Classify(CommandName.FromAscii("ABCD")));
    }
}

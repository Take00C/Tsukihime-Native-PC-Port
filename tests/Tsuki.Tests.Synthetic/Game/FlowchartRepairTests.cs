using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;

namespace Tsuki.Tests.Synthetic.Game;

/// <summary>Package K44: <see cref="FlowRepair"/>, the marks a profile of v0.1.0..v0.1.4 lacks, rebuilt from its system data on the rig's prologue lunch scenes.</summary>
public sealed class FlowchartRepairTests
{
    // 01_00_ARC01_2 (51) -> 01_00_ARC01_2_2 (68), whose end jumps into the cafeteria's (67) tail at LBL_004; the tail's last marker is _ZZ04001 (block 0x040, line 1).
    // With ciel, 311 jumps into the same tail earlier (LBL_CIEL, the shape of LBL_CIEL01_2_4) and both end at that one marker.
    private static (GameRig Rig, SystemData System, Vars Vars) OldProfile(bool ciel = false)
    {
        var rig = new GameRig()
            .Script("_RDST(51);_RTST(51);_JUMP(101,LBL_005);")
            .Script("_ZZ04101(LBL_005);_RDST(68);_RTST(68);_JUMP(102,LBL_004);")
            .Script("_RDST(67);_RTST(67);_ZZ03901(LBL_CIEL);_ZZ04001(LBL_004);_RDED(67);_RDED(68);_RDED(311);_JUMP(103);")
            .Script("_RDST(66);_RTST(66);_END_();")
            .Script(ciel ? "_RDST(311);_RTST(311);_JUMP(102,LBL_CIEL);" : "_END_();");
        rig.Build();
        rig.Flow.NodeMap[0] = 51;
        rig.Flow.NodeMap[1] = 68;
        rig.Flow.NodeMap[2] = 67;
        rig.Flow.NodeMap[3] = 66;
        rig.Flow.NodeMap[4] = 311;
        rig.Flow.ChoiceEdgeRows.AddRange([(-1, -1, -1), (605, 606, 607), (630, 631, 632)]);
        rig.Flow.EdgeRows.Add(new FlowEdgeRow(67, 66, [633], [633]));
        rig.Flow.EdgeRows.Add(new FlowEdgeRow(68, 66, [633, 634], [633, 634]));

        // what v0.1.4 left after "I'll stay in the classroom." played to its end: the scenes started, the edge of the jump 51 -> 68, the choice taken, the text read
        var system = new SystemData();
        var vars = new Vars();
        foreach (int n in new[] { 1, 51, 68, 66 })
        {
            SystemData.SetSceneSeen(vars, n);
        }

        SystemData.SetEdgeSeen(vars, 630);
        SystemData.SetEdgeSeen(vars, 631);
        system.MarkChoiceTaken(2, 1);
        system.Block.SetReadmax(0x040, 1);
        return (rig, system, vars);
    }

    // a saved state whose playthrough reached the given nodes (their F201.. bits)
    private static HistoryRecord Playthrough(params int[] nodes)
    {
        var r = new HistoryRecord();
        foreach (int n in nodes)
        {
            int k = HistoryRecord.FS8Indices.IndexOf(201 + (n >> 3));
            r.SetFS8(k, unchecked((sbyte)((byte)r.GetFS8(k) | (1 << (n & 7)))));
        }

        return r;
    }

    [Fact]
    public void AnOldProfileGetsTheSceneOfASharedEndingTheChoiceEdgesAndThePrologue()
    {
        (GameRig rig, SystemData system, Vars vars) = OldProfile();
        Assert.True(FlowRepair.Needed(vars));
        FlowRepairResult r = FlowRepair.Repair(rig.Data, system, vars);
        Assert.Equal(1, r.Scenes);
        Assert.True(SystemData.SceneDone(vars, 68));
        Assert.False(SystemData.SceneDone(vars, 67));
        Assert.True(SystemData.EdgeSeen(vars, 634));        // the tail's exit 67 -> 66 with node 68 reached marks 68 -> 66 (0xB9B7C)
        Assert.True(SystemData.EdgeSeen(vars, 632));        // choice point 2's third option (0xB8620 marks all three when the list opens)
        Assert.Equal(1, r.Choices);
        Assert.True(r.Prologue);
        Assert.True(SystemData.SceneDone(vars, 485) && SystemData.SceneDone(vars, 486) && SystemData.SceneDone(vars, 528) && SystemData.SceneDone(vars, 529));
        Assert.False(FlowRepair.Needed(vars));               // once is enough
    }

    [Fact]
    public void TheSceneStaysDarkWhenTheTailMayHaveBeenReadTheOtherWay()
    {
        (GameRig rig, SystemData system, Vars vars) = OldProfile();
        SystemData.SetSceneSeen(vars, 67);                   // the cafeteria was played too: the save cannot tell which way the tail was finished
        Assert.Equal(0, FlowRepair.Repair(rig.Data, system, vars).Scenes);
        Assert.False(SystemData.SceneDone(vars, 68));
    }

    [Fact]
    public void TheSceneStaysDarkWhenTheTailWasNotReadToItsEnd()
    {
        (GameRig rig, SystemData system, Vars vars) = OldProfile();
        system.Block.SetReadmax(0x040, 0);
        Assert.Equal(0, FlowRepair.Repair(rig.Data, system, vars).Scenes);
        Assert.False(SystemData.SceneDone(vars, 68));
    }

    [Theory]
    [InlineData(68)]
    [InlineData(311)]
    public void TwoScenesThatEndAtTheSameMarkerAreLeftWhenBothWereStarted(int finished)
    {
        // review of K44: 68 and 311 both end at the tail's last marker; one started and left, the other played to the end, and the old repair marked both
        (GameRig rig, SystemData system, Vars vars) = OldProfile(ciel: true);
        SystemData.SetSceneSeen(vars, 311);
        Assert.Equal(0, FlowRepair.Repair(rig.Data, system, vars).Scenes);
        Assert.False(SystemData.SceneDone(vars, 68) || SystemData.SceneDone(vars, 311));

        // with only one of them started, that one is done
        (rig, system, vars) = OldProfile(ciel: true);
        if (finished == 311)
        {
            vars.SetG(SystemData.SceneSeenWord + (68 >> 4), 0);
            SystemData.SetSceneSeen(vars, 1);
            SystemData.SetSceneSeen(vars, 51);
            SystemData.SetSceneSeen(vars, 66);
            SystemData.SetSceneSeen(vars, 311);
        }

        Assert.Equal(1, FlowRepair.Repair(rig.Data, system, vars).Scenes);
        Assert.True(SystemData.SceneDone(vars, finished));
        Assert.False(SystemData.SceneDone(vars, finished == 68 ? 311 : 68));
    }

    [Fact]
    public void ASavesPlaythroughProvesTheWayThroughATailReadBothWays()
    {
        // the reporter's likely state: both lunch options played. A slot of the playthrough that reached 68 and 66 but not 67 proves 68 went through the tail
        (GameRig rig, SystemData system, Vars vars) = OldProfile();
        SystemData.SetSceneSeen(vars, 67);
        Assert.Equal(0, FlowRepair.Repair(rig.Data, system, vars, [Playthrough(51, 67, 66), Playthrough(51, 68)]).Scenes);   // neither proves it

        FlowRepairResult r = FlowRepair.Repair(rig.Data, system, vars, [Playthrough(51, 68, 66)]);
        Assert.Equal(1, r.Scenes);
        Assert.True(SystemData.SceneDone(vars, 68));
        Assert.False(SystemData.SceneDone(vars, 67));
        Assert.True(SystemData.EdgeSeen(vars, 634));
    }

    [Fact]
    public void AJumpTheOldMarksProveGetsTheRestOfThePs4Marks()
    {
        // 355 -> 20 sets the plain record and one more edge bit (0xB9460: G byte 0x33D bit 3 = edge 799); 67 -> 66 depends on the playthrough, so its 633 adds nothing
        (GameRig rig, SystemData system, Vars vars) = OldProfile();
        rig.Flow.EdgeRows.Add(new FlowEdgeRow(355, 20, [570], [570]));
        SystemData.SetEdgeSeen(vars, 570);
        SystemData.SetEdgeSeen(vars, 633);
        system.Block.SetReadmax(0x040, 0);
        FlowRepair.Repair(rig.Data, system, vars);
        Assert.True(SystemData.EdgeSeen(vars, 799));
        Assert.False(SystemData.EdgeSeen(vars, 634));
    }

    [Fact]
    public void AnOldSlotSavedInsideTheTailGetsThePs4CurrentNodeBackOnLoad()
    {
        // review of K44: v0.1.4 wrote F110 = 67 at the jump into the tail; a slot saved there kept it, and the tail's _RDED(68) failed after every load
        var rig = new GameRig()
            .Line(2, "alpha line").Line(3, "beta line")
            .Script("_RDST(51);_RTST(51);_JUMP(101,LBL_005);")
            .Script("_ZZ04101(LBL_005);_RDST(68);_RTST(68);_JUMP(102,LBL_004);")
            .Script("_RDST(67);_RTST(67);_ZZ04001(LBL_004);_WKST(F822,eq,1);_ATSV();_WKST(F823,eq,0);_WNTY(1);_ZM04002($000002);"
                + "_WKST(F823,eq,0);_WNTY(1);_ZM04003($000003);_RDED(67);_RDED(68);_JUMP(103);")
            .Script("_RDST(66);_RTST(66);_END_();");
        GameHost host = rig.Build();
        rig.Flow.NodeMap[0] = 51;
        rig.Flow.NodeMap[1] = 68;
        rig.Flow.NodeMap[2] = 67;
        rig.Flow.NodeMap[3] = 66;
        var store = new SaveStore();
        host.AttachHistory(store);
        host.NewGame(100);
        RunTo(rig, host, "alpha line");
        Assert.Equal(68, host.F(110));
        Assert.True(store.SaveManual(3, host.HistorySink!.History.Staging, new SlotDate(2026, 11, 2, 12, 0, 0)));
        int f110 = HistoryRecord.FU16Indices.IndexOf(110);
        store.GetSlot(3)!.StateA.SetFU16(f110, 67);          // as v0.1.4 saved it

        host.LoadSlot(store, 3);
        Assert.Equal(68, host.F(110));
        for (int i = 0; i < 4000 && host.Phase != GamePhase.Ended; i++)
        {
            if (i % 3 == 0)
            {
                rig.Click();
            }
            else
            {
                host.Step(default);
            }
        }

        Assert.Equal(GamePhase.Ended, host.Phase);
        Assert.True(host.IsSceneDone(68));
        Assert.False(host.IsSceneDone(67));
    }

    [Fact]
    public void AProfileOfThisVersionOrOneThatNeverStartedNode1NeedsNothing()
    {
        var vars = new Vars();
        Assert.False(FlowRepair.Needed(vars));
        SystemData.SetSceneSeen(vars, 1);
        SystemData.SetSceneDone(vars, 485);
        Assert.False(FlowRepair.Needed(vars));
    }

    private static string Text(GameHost host) => host.CurrentMessage is { } m ? Encoding.UTF8.GetString(m.Text) : string.Empty;

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
}

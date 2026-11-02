using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;

namespace Tsuki.Tests.Synthetic.Game;

/// <summary>
/// Package K44 (issue #9): the flowchart marks of the PS4 (<see cref="FlowMarks"/>, VA 0xBA690 / 0xB9460 / 0xB8620) in the interpreter. The whole table is checked
/// against the PS4 functions under emulation by the data test <c>FlowMarksOracleTests</c>; these tests pin the cases the issue is about.
/// </summary>
public sealed class FlowchartMarksTests
{
    // the shape of the prologue's lunch choice: 01_00_ARC01_2 (node 51) -> choice 2 -> 01_00_ARC01_2_2 (node 68, "Stay in the Classroom"), whose end jumps into
    // the cafeteria scene 01_00_ARC01_2_1 (node 67) at the label of its shared tail; that tail ends with _RDED(67) and _RDED(68)
    private static (GameRig Rig, GameHost Host) Lunch(string head = "_RDST(51);_RTST(51);_JUMP(101,LBL_005);")
    {
        var rig = new GameRig()
            .Script(head)
            .Script("_ZZ04101(LBL_005);_RDST(68);_RTST(68);_RDAS(68);_JUMP(102,LBL_004);")
            .Script("_RDST(67);_RTST(67);_ZZ04001(LBL_004);_RDED(67);_RDED(68);_RDED(311);_JUMP(103,LBL_006);")
            .Script("_ZZ04001(LBL_006);_RDST(66);_RTST(66);_END_();");
        GameHost host = rig.Build();
        rig.Flow.NodeMap[0] = 51;
        rig.Flow.NodeMap[1] = 68;
        rig.Flow.NodeMap[2] = 67;
        rig.Flow.NodeMap[3] = 66;
        rig.Flow.EdgeRows.Add(new FlowEdgeRow(51, 67, [630], [630]));
        rig.Flow.EdgeRows.Add(new FlowEdgeRow(51, 68, [630, 631], [630, 631]));
        rig.Flow.EdgeRows.Add(new FlowEdgeRow(67, 66, [633], [633]));
        rig.Flow.EdgeRows.Add(new FlowEdgeRow(68, 66, [633, 634], [633, 634]));
        return (rig, host);
    }

    [Fact]
    public void StayInTheClassroomIsDoneWhenTheCafeteriasSharedTailEnds()
    {
        (GameRig rig, GameHost host) = Lunch();
        host.NewGame(100);
        rig.Run(h => h.Script == 1, 50);
        Assert.Equal(68, host.F(110));                 // the jump 51 -> 68 makes 68 the current node
        Assert.True(host.IsEdgeSeen(631));             // the node shows (black) as soon as it is entered
        Assert.False(host.IsSceneDone(68));

        rig.Run(h => h.Phase == GamePhase.Ended, 50);
        // 68 -> 67 is a special pair of 0xB9460: no edge, F110 stays 68, so the tail's _RDED(68) completes node 68 and _RDED(67) does not
        Assert.True(host.IsSceneDone(68));
        Assert.False(host.IsSceneDone(67));
        // 67 -> 66 with node 68 reached in this playthrough (F byte 0x1A2 bit 4) marks the edge 68 -> 66
        Assert.True(host.IsEdgeSeen(634));
        Assert.Equal(66, host.F(110));
    }

    [Fact]
    public void TheCafeteriaItselfStillCompletesNode67()
    {
        (GameRig rig, GameHost host) = Lunch("_RDST(51);_RTST(51);_JUMP(102);");
        host.NewGame(100);
        rig.Run(h => h.Phase == GamePhase.Ended, 50);
        Assert.True(host.IsSceneDone(67));
        Assert.False(host.IsSceneDone(68));
        Assert.True(host.IsEdgeSeen(633));
        Assert.False(host.IsEdgeSeen(634));            // node 68 was not reached: the plain edge 67 -> 66
    }

    [Fact]
    public void AChoiceListMarksTheEdgesOfAllItsOptionsWhenItOpens()
    {
        var rig = new GameRig().Script("_SELR(0,$000001);_SELR(1,$000002);_SELR(2,$000003);_SELD(_D000,2);_GOTS(_D000,A,B,C);_ZZ00101(A);_ZZ00102(B);_ZZ00103(C);_END_();");
        GameHost host = rig.Build();
        rig.Flow.ChoiceEdgeRows.AddRange([(-1, -1, -1), (605, 606, 607), (630, 631, 632)]);
        host.NewGame(100);
        rig.Run(h => h.ChoiceOpen, 50);
        Assert.True(host.IsEdgeSeen(630) && host.IsEdgeSeen(631) && host.IsEdgeSeen(632));
        Assert.False(host.IsEdgeSeen(605));
    }

    [Fact]
    public void AChoiceIdWithoutASecondEdgeSkipsTheThird()
    {
        var v = new Vars();
        FlowMarks.Choice(v, [(-1, -1, -1), (700, -1, 701)], 1);
        Assert.Equal(1, v.GetG(400 + ((700 - 564) >> 4)) >> ((700 - 564) & 15) & 1);
        Assert.Equal(0, v.GetG(400 + ((701 - 564) >> 4)) >> ((701 - 564) & 15) & 1);
    }

    [Theory]
    [InlineData(0, 501)]
    [InlineData(1, 502)]
    [InlineData(2, 503)]
    public void TheLastPickChoosesTheHiddenNodeOfAChoiceJump(int pick, int hidden)
    {
        var v = new Vars();
        FlowMarks.JumpNodes(v, [], 101, 102, pick);
        Assert.Equal(1, v.GetG(300 + (hidden >> 4)) >> (hidden & 15) & 1);   // done
        Assert.Equal(1, v.GetG(110 + (hidden >> 4)) >> (hidden & 15) & 1);   // seen
        Assert.Equal(1, v.GetF(201 + (hidden >> 3)) >> (hidden & 7) & 1);    // reached in this playthrough
        Assert.Equal(102, v.GetF(110));
    }

    [Fact]
    public void TheProloguesSecondHalfIsEnteredThroughItsHiddenNodes()
    {
        var v = new Vars();
        FlowMarks.JumpNodes(v, [new FlowEdgeRow(485, 528, [1170], [1170]), new FlowEdgeRow(528, 486, [1179], [1179])], -1, 486, 0);
        Assert.Equal(486, v.GetF(110));
        foreach (int n in new[] { 485, 528 })
        {
            Assert.Equal(1, v.GetG(300 + (n >> 4)) >> (n & 15) & 1);
        }

        foreach (int id in new[] { 1170, 1179 })
        {
            Assert.Equal(1, v.GetG(400 + ((id - 564) >> 4)) >> ((id - 564) & 15) & 1);
        }
    }

    [Fact]
    public void APlainPairMarksItsEdgeAndAnUnknownPairStillMovesTheCurrentNode()
    {
        var v = new Vars();
        FlowMarks.JumpNodes(v, [new FlowEdgeRow(5, 6, [600], [601])], 5, 6, 0);
        Assert.Equal(6, v.GetF(110));
        Assert.Equal(1, v.GetG(400 + ((600 - 564) >> 4)) >> ((600 - 564) & 15) & 1);
        Assert.Equal(1, v.GetF(600 + ((601 - 564) >> 3)) >> ((601 - 564) & 7) & 1);
        FlowMarks.JumpNodes(v, [], 6, 9, 0);
        Assert.Equal(9, v.GetF(110));
        FlowMarks.JumpNodes(v, [], 9, -1, 0);             // no target node: nothing
        FlowMarks.JumpNodes(v, [], 531, 12, 0);           // a source past node 530: nothing
        Assert.Equal(9, v.GetF(110));
    }
}

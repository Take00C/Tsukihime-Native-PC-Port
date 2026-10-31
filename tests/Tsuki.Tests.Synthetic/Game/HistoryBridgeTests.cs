using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;

namespace Tsuki.Tests.Synthetic.Game;

/// <summary>The interpreter's push-site hooks mapped onto the history ring of package K4.</summary>
public sealed class HistoryBridgeTests
{
    private static (GameRig Rig, GameHost Host, AdvHistorySink Sink) Start(string script)
    {
        var rig = new GameRig().Line(2, "left").Line(3, "right").Script(script);
        GameHost host = rig.Build();
        AdvHistorySink sink = host.AttachHistory();
        host.NewGame(100);
        host.Wait.WindowAlpha = 1f;
        host.Wait.WindowTarget = 1;
        return (rig, host, sink);
    }

    private static List<ushort> Kinds(AdvHistorySink sink)
    {
        var kinds = new List<ushort>();
        int i = sink.History.Oldest;
        while (true)
        {
            kinds.Add(sink.History.Record(i).Kind);
            if (i == sink.History.Cur)
            {
                break;
            }

            i = (i + 1) % AdvHistory.RingCount;
        }

        return kinds;
    }

    [Fact]
    public void SceneStartsMessagesAndChoicePointsAreRecordedInOrder()
    {
        (GameRig rig, GameHost host, AdvHistorySink sink) = Start("_WKST(F822,eq,1);_WKAD(F823,1);_ATSV();_ZM00101($000002@e);_ATSV(1);_END_();");
        rig.Run(h => h.Phase == GamePhase.Ended, 200);
        Assert.Equal([AdvHistory.Kinds.SceneStart, AdvHistory.Kinds.Message, AdvHistory.Kinds.ChoiceSavePoint], Kinds(sink));
        Assert.Equal(1, sink.History.StackTop);                                 // both ATSV pushed the scene stack
        Assert.Equal("left", Encoding.UTF8.GetString(sink.History.Newest.MessageText[..4]));
        Assert.Equal(100, host.SceneId);
    }

    [Fact]
    public void ARecordKeepsTheScriptPositionAndTheVariablesOfItsMoment()
    {
        (GameRig rig, GameHost host, AdvHistorySink sink) = Start("_WKST(F3,eq,5);_ATSV();_WKST(F3,eq,6);_END_();");
        rig.Run(h => h.Phase == GamePhase.Ended, 50);
        HistoryRecord r = sink.History.Newest;
                Assert.True(r.CmdOffset > 0);
        Assert.Equal(6, host.F(3));
        Assert.Equal(5, r.GetFU16(HistoryRecord.FU16Indices.IndexOf(3)));
    }

    [Fact]
    public void NothingIsPushedWhileF929IsSet()
    {
        (GameRig rig, _, AdvHistorySink sink) = Start("_WKST(F929,eq,1);_ATSV();_WKAD(F823,1);_ZM00101($000002@e);_END_();");
        rig.Run(h => h.Phase == GamePhase.Ended, 200);
        Assert.Equal(0, sink.Pushes);
    }

    [Fact]
    public void ChoiceStartsAndLinesAreRecordedAndLayerBatchesBracketTheirCommit()
    {
        (GameRig rig, GameHost host, AdvHistorySink sink) = Start("_STCH(1,IMG0001,240,136);_WTTM(1,1);_SELR(0,$000002);_SELR(1,$000003);_SELD(_D000,5);_END_();");
        rig.Run(h => h.ChoiceOpen, 50);
        Assert.Equal(
            [AdvHistory.Kinds.LayerChange, AdvHistory.Kinds.Commit, AdvHistory.Kinds.ChoiceLine, AdvHistory.Kinds.ChoiceStart],
            Kinds(sink));
        Assert.True(host.Choose(0));
    }
}

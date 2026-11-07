using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;
using Tsuki.Engine.Vm;
using Tsuki.Tests.Data.Source;

namespace Tsuki.Tests.Data.Game;

/// <summary>
/// Package K44, review: <see cref="FlowRepair"/> on the user's scripts. 01_00_ARC01_2_1 (the cafeteria, node 67) holds one tail that "Stay in the Classroom"
/// (68, at LBL_004) and Ciel's 311 (at LBL_CIEL01_2_4, earlier) both run to the same last marker (block 0x03F line 54) and the same <c>_RDED(67); _RDED(68);
/// _RDED(311)</c>; the exit then jumps to node 66.
/// </summary>
public sealed class FlowRepairRealDataTests
{
    private const int Cafeteria = 67;
    private const int Stay = 68;
    private const int Ciel = 311;

    private static IGameData Data => SharedSource.Source;

    private static (SystemData System, Vars Vars) OldProfile(params int[] started)
    {
        var system = new SystemData();
        var vars = new Vars();
        foreach (int n in started.Append(1))
        {
            SystemData.SetSceneSeen(vars, n);
        }

        system.Block.SetReadmax(0x03F, 54);
        return (system, vars);
    }

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

    [GameDataFact]
    public void OnlyTheSceneThatWasStartedIsDoneThroughTheSharedTail()
    {
        (SystemData system, Vars vars) = OldProfile(51, Stay, 66);
        FlowRepair.Repair(Data, system, vars);
        Assert.True(SystemData.SceneDone(vars, Stay));
        Assert.False(SystemData.SceneDone(vars, Ciel) || SystemData.SceneDone(vars, Cafeteria));
        Assert.True(SystemData.EdgeSeen(vars, 634));                 // the exit 67 -> 66 after 68 (0xB9B7C)

        // 68 started and left, 311 played to the end (review finding): the marker could have been read either way, so neither is marked
        (system, vars) = OldProfile(51, Stay, 69, Ciel, 66);
        FlowRepair.Repair(Data, system, vars);
        Assert.False(SystemData.SceneDone(vars, Stay) || SystemData.SceneDone(vars, Ciel));
    }

    [GameDataFact]
    public void ASlotOfThePlaythroughThroughTheTailProvesItWhenBothLunchesWerePlayed()
    {
        (SystemData system, Vars vars) = OldProfile(51, Cafeteria, Stay, 66);
        FlowRepair.Repair(Data, system, vars, [Playthrough(51, Cafeteria, 66)]);
        Assert.False(SystemData.SceneDone(vars, Stay));

        FlowRepair.Repair(Data, system, vars, [Playthrough(51, Stay, 66)]);
        Assert.True(SystemData.SceneDone(vars, Stay));
        Assert.False(SystemData.SceneDone(vars, Cafeteria));
    }

    [GameDataFact]
    public void AnOldSlotInsideTheTailGetsTheSceneItCameFromAsF110()
    {
        IGameFlowTables flow = Data.Eboot.GameFlow;
        int script = Enumerable.Range(0, Data.ScriptCount).First(k => flow.NodeOfScript(k) == Cafeteria);
        int f110 = HistoryRecord.FU16Indices.IndexOf(110);

        // inside 68's part of the tail (after LBL_004), as v0.1.4 saved it: F110 = 67
        HistoryRecord r = Playthrough(51, Stay);
        r.Script = (ushort)script;
        r.CmdOffset = (ulong)(MessageAfter(Data.Script(script), "LBL_004") - 1);
        r.SetFU16(f110, Cafeteria);
        Assert.True(FlowRepair.RepairLoadedRecord(Data, r));
        Assert.Equal(Stay, r.GetFU16(f110));
        Assert.False(FlowRepair.RepairLoadedRecord(Data, r));        // this build's state is left alone

        // the same place on 311's way
        r = Playthrough(51, 69, Ciel);
        r.Script = (ushort)script;
        r.CmdOffset = (ulong)(MessageAfter(Data.Script(script), "LBL_004") - 1);
        r.SetFU16(f110, Cafeteria);
        Assert.True(FlowRepair.RepairLoadedRecord(Data, r));
        Assert.Equal(Ciel, r.GetFU16(f110));

        // the cafeteria's own playthrough (its _RTST ran) and a playthrough that reached both are left
        r = Playthrough(51, Cafeteria);
        r.Script = (ushort)script;
        r.CmdOffset = (ulong)(MessageAfter(Data.Script(script), "LBL_004") - 1);
        r.SetFU16(f110, Cafeteria);
        Assert.False(FlowRepair.RepairLoadedRecord(Data, r));
        r = Playthrough(51, Stay, Ciel);
        r.Script = (ushort)script;
        r.CmdOffset = (ulong)(MessageAfter(Data.Script(script), "LBL_004") - 1);
        r.SetFU16(f110, Cafeteria);
        Assert.False(FlowRepair.RepairLoadedRecord(Data, r));
    }

    // the start of the first _ZM statement after the label
    private static int MessageAfter(ScriptImage image, string label)
    {
        ReadOnlySpan<byte> bytes = image.Bytes.Span;
        bool past = false;
        foreach (int start in image.SegmentStarts)
        {
            ScriptSegment seg = ScriptSegmenter.Read(bytes, start);
            if (!seg.IsCommand)
            {
                continue;
            }

            string text = Encoding.Latin1.GetString(bytes[seg.NameStart..seg.End]);
            if (past && text.StartsWith("ZM", StringComparison.Ordinal))
            {
                return seg.Start;
            }

            past |= text.StartsWith('Z') && text.Contains("(" + label + ")", StringComparison.Ordinal);
        }

        throw new InvalidOperationException($"no message after {label}");
    }
}

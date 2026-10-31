using Tsuki.Engine.Vm.Variables;

namespace Tsuki.Tests.Synthetic.Flow;

/// <summary>
/// The synthetic cases of <c>tools/tests/test_vm_vars.py</c> (operand roles, C targets, value dispatch, IF__ jumps
/// when false, GOTS / SEL* / SELR, the choice flags and the history storage classes) against the C# port. The 61,000
/// fuzz cases are in <c>Tsuki.Tests.Data/Flow/VarsFuzzTests</c>.
/// </summary>
public sealed class VarCommandsTests
{
    private static long F(int i) => VarAddresses.OfF(i);

    private static long G(int i) => VarAddresses.OfG(i);

    private static VarState State(params (string Var, int Value)[] words)
    {
        var st = new VarState(new VarMemory(new Engine.Contracts.Vars(), OutOfBlockPolicy.Flat)) { };
        st.Memory.Log = [];
        foreach ((string v, int value) in words)
        {
            int n = int.Parse(v[1..], System.Globalization.CultureInfo.InvariantCulture);
            st.Memory.Poke(v[0] == 'F' ? F(n) : G(n), unchecked((ushort)value));
        }

        return st;
    }

    private static List<(long, ushort)> Writes(VarState st) =>
        st.Memory.Log!.Where(a => a.Kind == VarAccessKind.Write).Select(a => (a.Va, a.Value)).ToList();

    private static List<long> Reads(VarState st) =>
        st.Memory.Log!.Where(a => a.Kind == VarAccessKind.Read).Select(a => a.Va).ToList();

    [Fact]
    public void OperandRoles()
    {
        Assert.Equal("F[F823+847]", VarOperand.Parse("C847", OperandRole.WkTarget).ToString());
        Assert.Equal(new VarOperand(OperandKind.Literal, 847), VarOperand.Parse("C847", OperandRole.Value));
        Assert.Equal(OperandKind.None, VarOperand.Parse("C847", OperandRole.OrTarget).Kind);
        Assert.Equal(new VarOperand(OperandKind.F, 12), VarOperand.Parse("F0012", OperandRole.Value));
        Assert.Equal(VarAddresses.Z, VarOperand.Parse("F1200", OperandRole.WkTarget).Address());
        Assert.Equal(OperandKind.None, VarOperand.Parse("Z12", OperandRole.WkTarget).Kind);
        Assert.Equal(OperandKind.Literal, VarOperand.Parse("Z12", OperandRole.Value).Kind);
        Assert.Equal(OperandKind.None, VarOperand.Parse("f12", OperandRole.WkTarget).Kind);
        Assert.Equal(new VarOperand(OperandKind.F, 0), VarOperand.Parse("_D000", OperandRole.Value));
        Assert.Equal(new VarOperand(OperandKind.G, 50), VarOperand.Parse("_D650", OperandRole.Value));
        Assert.Equal(new VarOperand(OperandKind.F, 650), VarOperand.Parse("_D650", OperandRole.Choice));
        Assert.Equal(OperandKind.None, VarOperand.Parse("_X012", OperandRole.Choice).Kind);
        Assert.Equal(1000, VarOperand.Parse("_D1000", OperandRole.Value).Index);
        Assert.Equal(100, VarOperand.Parse("_D1000", OperandRole.Gots).Index);
        Assert.Equal(OperandKind.None, VarOperand.Parse("F12", OperandRole.Gots).Kind);
        Assert.Equal(OperandKind.None, VarOperand.Parse("_D000", OperandRole.BoTarget).Kind);
        Assert.Equal(new VarOperand(OperandKind.F, 12), VarOperand.Parse("`001:F12", OperandRole.Value));
        Assert.Equal(4464, VarOperand.Parse("70000", OperandRole.Value).Index);
    }

    [Fact]
    public void COperandsAreMessageRelative()
    {
        VarState st = State(("F823", 5));
        VarCommands.ExecuteText("_WKST(C847,eq,63);", st);
        Assert.Equal([(F(852), (ushort)63)], Writes(st));
        Assert.Equal([F(823)], Reads(st));
        st.Memory.Log!.Clear();
        VarCommands.ExecuteText("_WKAD(C847,1);", st);
        Assert.Equal([(F(852), (ushort)64)], Writes(st));
        st.Memory.Poke(F(823), 0);
        st.Memory.Log!.Clear();
        VarCommands.ExecuteText("_WKST(C847,eq,0);", st);
        Assert.Equal([(F(847), (ushort)0)], Writes(st));
        st.Memory.Log!.Clear();
        VarCommands.ExecuteText("_WKST(C847,eq,0);", st, prefetch: true);
        Assert.Empty(Writes(st));
    }

    [Fact]
    public void ValueDispatchAndOperatorSlot()
    {
        VarState st = State(("F3", 0x1234), ("G3", 0x42));
        foreach (string s in new[] { "_WKST(F12,eq,G3);", "_WKST(F13,eq,C3);", "_WKST(F14,7);", "_WKST(F15,ne,7);",
                     "_WKST(F16,eq);", "_WKOR(G21,2);", "_WKOF(F3,0x4);", "_WKBO(F17,3);" })
        {
            Assert.True(VarCommands.ExecuteText(s, st).Handled, s);
        }

        Dictionary<long, ushort> w = Writes(st).GroupBy(x => x.Item1).ToDictionary(x => x.Key, x => x.Last().Item2);
        Assert.Equal(0x42, w[F(12)]);
        Assert.Equal(3, w[F(13)]);
        Assert.Equal(7, w[F(14)]);
        Assert.Equal(0, w[F(15)]);
        Assert.Equal(0xFFFF, w[F(16)]);
        Assert.Equal(2, w[G(21)]);
        Assert.Equal(0x1230, w[F(3)]);
        Assert.Equal(8, w[F(17)]);
    }

    [Theory]
    [InlineData(0, "eq", 0, false)]
    [InlineData(1, "eq", 0, true)]
    [InlineData(0, "ne", 0, true)]
    [InlineData(5, "gt", 4, false)]
    [InlineData(5, "gt", 5, true)]
    [InlineData(5, "ge", 5, false)]
    [InlineData(-2, "lt", 3, false)]
    [InlineData(0xFFFF, "eq", 65535, false)]
    [InlineData(3, "le", -2, true)]
    [InlineData(0, "xx", 0, true)]
    [InlineData(0, "", 0, true)]
    [InlineData(0, "e", 0, false)]
    public void IfJumpsWhenTheConditionIsFalse(int f26, string op, int value, bool jumps)
    {
        VarEffect e = VarCommands.ExecuteText($"_IF__(F26,{op},{value},LV00000);", State(("F26", f26)));
        Assert.Equal(jumps, e.Goto is not null && e.Goto.SequenceEqual("LV00000"u8.ToArray()));
        Assert.Null(VarCommands.ExecuteText("_IF__(F26,eq,1);", State(("F26", 0))).Goto);   // no label: falls through
        Assert.Null(VarCommands.ExecuteText("_IF__(F26,eq,1,LBL);", new VarState(new VarMemory(new Engine.Contracts.Vars(), OutOfBlockPolicy.Flat)) { SkipCounter = 1 }).Goto);
    }

    [Fact]
    public void GotsChoicesAndSelr()
    {
        VarState st = State(("F823", 2), ("F929", 1));
        VarCommands.ExecuteText("_SELR(7,$000000);", st);
        VarCommands.ExecuteText("_SELR(8,$000001);", st);
        Assert.Equal(2, st.ChoiceCount);
        Assert.Equal([(ushort)7, 8], st.ChoiceValues);
        Assert.Contains((F(849), (ushort)1), Writes(st));                                  // first line only: C847 += 1
        VarEffect e = VarCommands.ExecuteText("_SELD(_D000,12);", st);
        Assert.Equal((0x46, 0, (short)12, ChoiceMode.List), ((int)st.ChoiceKind, (int)st.ChoiceIndex, st.ChoiceId, st.ChoiceMode));
        Assert.True(e.Yielded && e.ChoiceUi);
        Assert.Equal(12, e.ChoicePoint);
        Assert.Equal(F(0), VarCommands.ChoiceStore(st, 1));
        Assert.Equal(8, st.Memory.Peek(F(0)));
        st.Memory.Poke(F(0), 1);
        Assert.Equal("SEL0_1"u8.ToArray(), VarCommands.ExecuteText("_GOTS(_D000,SEL0_0,SEL0_1);", st).Goto);
        st.Memory.Poke(F(0), 0xFFFF);
        Assert.Equal("_D000"u8.ToArray(), VarCommands.ExecuteText("_GOTS(_D000,A,B);", st).Goto);
        st.Memory.Poke(F(0), 5);
        Assert.Equal([], VarCommands.ExecuteText("_GOTS(_D000,A,B);", st).Goto!);
        Assert.Null(VarCommands.ExecuteText("_GOTS(F000,A,B);", st).Goto);
        var st2 = new VarState { ChoiceKind = 0x46, ChoiceIndex = 0 };
        VarCommands.ExecuteText("_SELS(_G005,);", st2);
        Assert.Equal((0x47, ChoiceMode.TwoButtons), ((int)st2.ChoiceKind, st2.ChoiceMode));
        Assert.Equal([(ushort)0, 1], st2.ChoiceValues);
        Assert.Null(VarCommands.ChoiceStore(st2, 1));
        var st3 = new VarState();
        foreach (string line in new[] { "_SELR(0,$042569);", "_SELR(1,$042570,1);", "_SELR(2,$042571,0);" })
        {
            VarCommands.ExecuteText(line, st3);
        }

        Assert.Equal([(ushort)0, 1, 2], st3.ChoiceValues);
        Assert.Equal([false, true, false], st3.ChoiceLocked);
    }

    [Fact]
    public void ChoiceCommandFlags()
    {
        VarState st = State(("F1118", 1));
        VarEffect e = VarCommands.ExecuteText("_SELA(_D000,);", st);
        Assert.Equal([new VarAccess(VarAccessKind.Read, F(1118), 1), new VarAccess(VarAccessKind.Write, F(1118), 0)], st.Memory.Log!);
        Assert.True(e.Yielded && !e.ChoiceUi);
        Assert.Equal(ChoiceMode.None, st.ChoiceMode);
        e = VarCommands.ExecuteText("_SELA(_D000,);", st);
        Assert.True(e.ChoiceUi);
        Assert.Equal((ChoiceMode.SavePrompt, 0x46, 2), (st.ChoiceMode, (int)st.ChoiceKind, st.ChoiceCount));
        Assert.Null(VarCommands.ChoiceStore(st, 1));

        var sc = new VarState { ScanMode = true, ChoiceKind = 0x11, ChoiceIndex = 0x2222, ChoiceId = 0x3333 };
        foreach (string text in new[] { "_SELA(_D000,);", "_SELS(_D000,);", "_SELD(_D005,7);" })
        {
            e = VarCommands.ExecuteText(text, sc);
            Assert.True(e.Yielded && !e.ChoiceUi, text);
        }

        Assert.Equal((0x11, 0x2222, (short)0x3333, ChoiceMode.None), ((int)sc.ChoiceKind, (int)sc.ChoiceIndex, sc.ChoiceId, sc.ChoiceMode));
        var two = new VarState { ChoiceKind = 0x46, ChoiceIndex = 4, ChoiceMode = ChoiceMode.TwoButtons };
        Assert.Equal(F(4), VarCommands.ChoiceStore(two, 1));
        Assert.Equal(1, two.Memory.Peek(F(4)));
        Assert.Null(VarCommands.ChoiceStore(two, 2));
        Assert.Null(VarCommands.ChoiceStore(two, 0xFFFF));

        foreach ((int f929, int f1102, byte block, bool scan, bool snap, int[] reads) in new[]
                 {
                     (0, 0, (byte)0, false, true, new[] { 929, 1102, 823 }),
                     (0, 1, (byte)0, false, false, new[] { 929, 1102, 823 }),
                     (0, 0, (byte)1, false, false, new[] { 929, 1102, 823 }),
                     (1, 0, (byte)0, false, false, new[] { 929, 823 }),
                     (0, 0, (byte)0, true, false, new[] { 823 }),
                 })
        {
            VarState s = State(("F929", f929), ("F1102", f1102));
            s.SnapshotBlock = block;
            s.ScanMode = scan;
            e = VarCommands.ExecuteText("_SELR(1,$000001);", s);
            Assert.Equal(snap, e.Snapshot);
            Assert.False(e.Yielded);
            Assert.Equal(reads.Select(F).ToList(), Reads(s));
        }
    }

    [Fact]
    public void HistoryStorageClasses()
    {
        Assert.Equal(HistoryStorage.U16, HistoryVars.Storage(0));
        Assert.Equal(0xBEEF, HistoryVars.RoundTrip(0, 0xBEEF));
        Assert.Equal(HistoryStorage.S8, HistoryVars.Storage(848));
        Assert.Equal(0xFF80, HistoryVars.RoundTrip(848, 0x80));
        Assert.Equal(HistoryStorage.Bool, HistoryVars.Storage(929));
        Assert.Equal(1, HistoryVars.RoundTrip(929, 0x100));
        Assert.Equal(HistoryStorage.None, HistoryVars.Storage(1102));
        Assert.Equal(0, HistoryVars.RoundTrip(1102, 9));
        Assert.Equal(4, HistoryVars.AfterRewind(1102, 9, 4));
        Assert.Equal(0xFFFF, HistoryVars.AfterRewind(848, 0x1FF, 4));
        Assert.Equal(HistoryStorage.S8, HistoryVars.Storage(967));     // in the u16 and s8 lists: s8 wins
        Assert.Equal(HistoryStorage.Bool, HistoryVars.Storage(991));   // in the u16 and bool lists: bool wins
        Assert.Equal(356, Enumerable.Range(0, 1200).Count(i => HistoryVars.Storage(i) != HistoryStorage.None));
    }

    [Fact]
    public void OutOfBlockAddressesAreRejectedByDefault()
    {
        var st = new VarState();
        Assert.Throws<VarAccessException>(() => VarCommands.ExecuteText("_WKST(F-1,eq,1);", st));
        Assert.Throws<VarAccessException>(() => VarCommands.ExecuteText("_WKST(G2000,eq,1);", st));
        VarCommands.ExecuteText("_WKST(F1200,eq,9);", st);              // F1200 is Z[0]: inside the block
        Assert.Equal(9, st.Memory.Block.GetZ(0));
        Assert.Equal("F[1199]", VarAddresses.Name(F(1199)));
        Assert.Equal("Z[0]", VarAddresses.Name(F(1200)));
        Assert.Equal("G[1999]", VarAddresses.Name(G(1999)));
        Assert.Equal("0x164d5c0", VarAddresses.Name(G(2000)));
    }
}

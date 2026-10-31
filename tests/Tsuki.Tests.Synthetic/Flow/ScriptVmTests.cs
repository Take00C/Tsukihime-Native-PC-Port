using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Flow;
using Tsuki.Engine.Vm;
using Tsuki.Engine.Vm.Variables;

namespace Tsuki.Tests.Synthetic.Flow;

/// <summary>
/// The interpreter loop on hand-made scripts: the 1000-segment budget, the frame behaviour of the jumps (measured under
/// unicorn, see docs/status/wave2/C2.md), the label lookup, markers and read tracking.
/// </summary>
public sealed class ScriptVmTests
{
    private sealed class Scripts(string script, params (string Name, int Offset)[] labels) : IFlowScripts
    {
        public byte[] Bytes { get; } = Encoding.Latin1.GetBytes(script);

        public ScriptImage Script(int k) => ScriptSegmenter.CreateImage(k, Bytes);

        public LabelTable Labels(int k) => LabelTable.FromLabels(labels.Select(l => (l.Name, 1, l.Offset)));

        public int OffsetOf(string text) => script.IndexOf(text, StringComparison.Ordinal);
    }

    /// <summary>Records dispatched names; yields on <c>WTTM</c> / <c>ZM</c>, halts on <c>END_</c>.</summary>
    private sealed class Host : IHost
    {
        public List<string> Names { get; } = [];

        public StepResult Dispatch(in Statement statement)
        {
            string n = statement.Name.ToString();
            Names.Add(n);
            return n switch
            {
                "END_" => StepResult.Halt,
                "WTTM" => StepResult.Yield,
                _ when n.StartsWith("ZM", StringComparison.Ordinal) => StepResult.Yield,
                _ => StepResult.Continue,
            };
        }
    }

    private static (ScriptVm Vm, List<FlowStep> Steps) Vm(Scripts s, VarState? vars = null, SystemBlock? system = null)
    {
        var vm = new ScriptVm(s, vars ?? new VarState(), new MarkerState(system ?? new SystemBlock()), 7);
        var steps = new List<FlowStep>();
        vm.Observer = steps.Add;
        return (vm, steps);
    }

    [Fact]
    public void BudgetIsThousandSegmentsPerCall()
    {
        var s = new Scripts(string.Concat(Enumerable.Repeat("_WKAD(F1,1);", 1500)) + "x;" + "_END_();");
        (ScriptVm vm, _) = Vm(s);
        var host = new Host();
        Assert.Equal(StepEnd.Budget, vm.Step(host));
        Assert.Equal(1000, vm.Vars.Memory.Peek(VarAddresses.OfF(1)));
        Assert.Equal(12 * 1000, vm.Cursor);
        Assert.Equal(StepEnd.Halted, vm.Step(host));                 // 500 more, the non-command "x;", then END_
        Assert.Equal(1500, vm.Vars.Memory.Peek(VarAddresses.OfF(1)));
        Assert.Equal(["END_"], host.Names);
    }

    [Fact]
    public void IfYieldsAtTheLabelWhenFalseAndContinuesWhenTrue()
    {
        const string text = "_IF__(F26,eq,0,L1);_WKST(F5,eq,1);_ZZ00101(L1);_WKST(F6,eq,2);_END_();";
        var s = new Scripts(text, ("L1", text.IndexOf("_ZZ", StringComparison.Ordinal)));
        (ScriptVm vm, List<FlowStep> steps) = Vm(s);
        var host = new Host();
        Assert.Equal(StepEnd.Halted, vm.Step(host));                 // F26 == 0: no jump, everything in one call
        Assert.Equal(1, vm.Vars.Memory.Peek(VarAddresses.OfF(5)));
        Assert.Equal(FlowAction.NoJump, steps[0].Action);

        var vars = new VarState();
        vars.Memory.Poke(VarAddresses.OfF(26), 1);
        (vm, steps) = Vm(s, vars);
        Assert.Equal(StepEnd.Yielded, vm.Step(host));                // false: the jump yields at the label
        Assert.Equal(s.OffsetOf("_ZZ"), vm.Cursor);
        Assert.Equal(FlowAction.Jump, steps[0].Action);
        Assert.Equal("L1"u8.ToArray(), steps[0].Goto);
        Assert.Equal(StepEnd.Halted, vm.Step(host));
        Assert.Equal(0, vars.Memory.Peek(VarAddresses.OfF(5)));          // skipped
        Assert.Equal(2, vars.Memory.Peek(VarAddresses.OfF(6)));
        Assert.Equal([0, 2, 3, 4], steps.Select(x => x.Index));
        Assert.Equal("ZZ", steps[1].DisplayName);
    }

    [Fact]
    public void GotoYieldsGotsAndIfnxContinue()
    {
        const string text = "_GOTO(A);_WKST(F1,eq,1);_ZZ00101(A);_GOTS(_D000,B,C);_WKST(F2,eq,1);" +
                            "_ZZ00102(B);_WKST(F3,eq,1);_ZZ00103(C);_IFNX(,D);_WKST(F4,eq,1);_ZZ00104(D);_IFNX(,);_END_();";
        int At(string m) => text.IndexOf(m, StringComparison.Ordinal);
        var s = new Scripts(text, ("A", At("_ZZ00101")), ("B", At("_ZZ00102")), ("C", At("_ZZ00103")), ("D", At("_ZZ00104")));
        var vars = new VarState();
        vars.Memory.Poke(VarAddresses.OfF(0), 1);                       // GOTS selects label 1 = C
        (ScriptVm vm, List<FlowStep> steps) = Vm(s, vars);
        var host = new Host();
        Assert.Equal(StepEnd.Yielded, vm.Step(host));                // GOTO yields at A
        Assert.Equal(At("_ZZ00101"), vm.Cursor);
        Assert.Equal(StepEnd.Halted, vm.Step(host));                 // GOTS and IFNX jump within the same call
        for (int f = 1; f <= 4; f++)
        {
            Assert.Equal(0, vars.Memory.Peek(VarAddresses.OfF(f)));
        }

        Assert.Equal(["GOTO", "ZZ", "GOTS", "ZZ", "IFNX", "ZZ", "IFNX", "END_"], steps.Select(x => x.DisplayName));
        Assert.Equal(
            [FlowAction.Jump, FlowAction.Marker, FlowAction.Jump, FlowAction.Marker, FlowAction.Jump, FlowAction.Marker, FlowAction.NoJump, FlowAction.Host],
            steps.Select(x => x.Action));
    }

    [Fact]
    public void SkipCounterStopsIfGotoAndGots()
    {
        const string text = "_GOTO(A);_IF__(F1,eq,1,A);_GOTS(_D000,A);_ZZ00101(A);_END_();";
        var s = new Scripts(text, ("A", text.IndexOf("_ZZ", StringComparison.Ordinal)));
        (ScriptVm vm, List<FlowStep> steps) = Vm(s, new VarState { SkipCounter = 1 });
        Assert.Equal(StepEnd.Halted, vm.Step(new Host()));
        Assert.All(steps.Take(3), x => Assert.Equal(FlowAction.NoJump, x.Action));
    }

    [Fact]
    public void MissingLabelIsAnError()
    {
        var s = new Scripts("_GOTO(NOPE);_END_();");
        (ScriptVm vm, _) = Vm(s);
        Assert.Throws<ScriptFlowException>(() => vm.Step(new Host()));
    }

    [Fact]
    public void LabelLookupFollowsTheEngine()
    {
        LabelTable t = LabelTable.FromLabels([("START", 0x19, 0xB), ("LBL_000", 0x1a, 0x2e), ("LV00000", 0x1b, 0x2cb)]);
        Assert.Equal(3, t.Count);
        Assert.Equal("START                019,0000b\r\n", Encoding.ASCII.GetString(t.Records[..32]));
        Assert.Equal(0xB, t.Find("START"));
        Assert.Equal(0x2cb, t.Find("LV00000"));
        Assert.Equal(0x2e, t.Find("LBL_000.x"));                     // the wanted name stops at '.'
        Assert.Equal(0x2e, t.Find("LBL_000\r\n"u8));                  // ... and at CR / LF / TAB / NUL
        Assert.Equal(-1, t.Find("LBL_00"));
        Assert.Equal(-1, t.Find("LBL_0000"));
        Assert.Equal(-1, t.Find(string.Empty));
    }

    [Fact]
    public void MarkersRecordTheLineWhenTheNextMarkerIsReached()
    {
        var system = new SystemBlock();
        const string text = "_ZM00a01($000000);_ZM00a02($000001);_ZY00b01(LV1);_ZZ00c01();_END_();";
        var s = new Scripts(text);
        (ScriptVm vm, _) = Vm(s, null, system);
        var host = new Host();
        Assert.Equal(StepEnd.Yielded, vm.Step(host));                // ZM yields (host)
        Assert.Equal((0xA, 1), (vm.Markers.Block, vm.Markers.Sub));
        Assert.Equal(0, system.GetReadmax(0xA));                      // not yet: recorded at the next marker
        Assert.Equal(StepEnd.Yielded, vm.Step(host));
        Assert.Equal(1, system.GetReadmax(0xA));
        Assert.Equal(StepEnd.Halted, vm.Step(host));
        Assert.Equal(2, system.GetReadmax(0xA));
        Assert.Equal(1, system.GetReadmax(0xB));
        Assert.Equal(1, vm.LocalLabelFlag);
        Assert.Equal("LV1"u8.ToArray(), vm.Vars.ArgText.ToArray());

        var m = new MarkerState(system);
        Assert.True(m.Enter(0xA, 2));                                  // already read up to line 2
        Assert.False(m.Enter(0xA, 3));
        Assert.False(m.Enter(0xFB4, 1));                               // never recorded
        Assert.Equal(3, system.GetReadmax(0xA));
        Assert.False(m.Enter(0xFB4, 2));
        Assert.Equal(0, m.Enter(-5, 1) ? 1 : 0);
    }

    [Fact]
    public void ChoiceCommandsYieldAndKeepTheirVariableSide()
    {
        const string text = "_SELR(0,$000001);_SELR(1,$000002);_SELD(_D000,3);_GOTS(_D000,A,B);_ZZ00101(A);_ZZ00102(B);_END_();";
        int At(string m) => text.IndexOf(m, StringComparison.Ordinal);
        var s = new Scripts(text, ("A", At("_ZZ00101")), ("B", At("_ZZ00102")));
        (ScriptVm vm, List<FlowStep> steps) = Vm(s);
        var host = new Host();
        Assert.Equal(StepEnd.Yielded, vm.Step(host));                // SELD yields
        Assert.Equal([(ushort)0, 1], vm.Vars.ChoiceValues);
        Assert.Equal(VarAddresses.OfF(0), VarCommands.ChoiceStore(vm.Vars, 1));   // the UI stores values[1] = 1 into F0
        Assert.Equal(StepEnd.Halted, vm.Step(host));                 // GOTS -> label 1 = B, then END_
        Assert.Equal(["SELR", "SELR", "SELD", "GOTS", "ZZ", "END_"], steps.Select(x => x.DisplayName));
        Assert.Equal(At("_ZZ00102"), steps[^2].Offset);
        Assert.Equal(["SELR", "SELR", "SELD", "END_"], host.Names);   // the host sees the SEL* commands for their UI
    }
}

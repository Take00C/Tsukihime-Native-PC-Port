using System.Globalization;
using System.Text;
using System.Text.Json;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Flow;
using Tsuki.Engine.Vm;
using Tsuki.Engine.Vm.Variables;
using Tsuki.Tests.Data.Vm;

namespace Tsuki.Tests.Data.Flow;

/// <summary>
/// The executed traces of the golden slice (<c>samples/golden/slice/scripts/script_000k.json</c>, <c>trace</c>; written by
/// <c>oracle.export_slice.trace_script</c> over <c>tsuki.vm_vars</c>): 00_01 (script 2) from its start to <c>_RTMN</c> and
/// 00_02 (script 3) to <c>_PGST(2);</c>, run by the C# <see cref="ScriptVm"/> on the game's own decoded scripts. Every step
/// is compared: statement index, offset, name, the variable writes, the jump label and, at each <c>ZM</c>, the message
/// variables; so is the skipped range 40..68 (the <c>_IF__(F026,...)</c> block jumped over because F026 is 0).
/// </summary>
public sealed class SliceTraceTests
{
    private static readonly string[] MessageVars = ["F823", "F31", "F32", "F1104", "F1105", "F1109"];
    private static readonly (string Name, int N)[] MessageCVars = [("C846", 846), ("C835", 835), ("C836", 836)];

    /// <summary>Commands that always yield on the PS4 (script.md 3.6) plus ZM; the test host yields on them to exercise resuming.</summary>
    private static readonly HashSet<string> YieldNames =
        ["CLO2", "CLO3", "CLO4", "CLO5", "END_", "FADS", "MSAD", "STCF", "TOMW", "WTSV", "WTTM", "WT__", "WTTR", "ZM"];

    private static readonly HashSet<string> EndNames = ["END_", "RTM_", "RTMN"];

    [GameDataTheory]
    [InlineData(2)]
    [InlineData(3)]
    public void TraceEqualsTheSlice(int k)
    {
        using JsonDocument doc = ScriptFixtures.ReadJson(Path.Combine(ScriptFixtures.SliceScripts, $"script_{k:0000}.json"));
        JsonElement root = doc.RootElement;
        byte[] bytes = File.ReadAllBytes(Path.Combine(ScriptFixtures.SliceScripts, root.GetProperty("decoded").GetProperty("file").GetString()!));
        Assert.Equal(ScriptFixtures.Script(k), bytes);                      // the slice holds the game's own script

        var scripts = new SliceScripts();
        scripts.Add(k, bytes, root.GetProperty("labels"));
        JsonElement trace = root.GetProperty("trace");
        string? stop = trace.GetProperty("stop").ValueKind == JsonValueKind.Null ? null : trace.GetProperty("stop").GetString();

        var vars = new VarState();                                           // all variables 0, scan mode off; out-of-range rejected
        var vm = new ScriptVm(scripts, vars, new MarkerState(new SystemBlock()), k);
        var steps = new List<string>();
        var host = new TraceHost(bytes, stop);
        vm.Observer = s =>
        {
            Assert.Equal(k, s.Script);
            steps.Add(Canonical(s, vars));
        };

        int calls = 0;
        StepEnd end;
        do
        {
            end = vm.Step(host);
            calls++;
            Assert.True(calls < 10_000, "the VM does not reach the end of the trace");
        }
        while (end is StepEnd.Yielded or StepEnd.Budget && !host.Done);

        Assert.Equal(StepEnd.Halted, end);
        List<string> expected = trace.GetProperty("steps").EnumerateArray().Select(Canonical).ToList();
        int first = Enumerable.Range(0, Math.Min(expected.Count, steps.Count)).FirstOrDefault(i => expected[i] != steps[i], -1);
        Assert.True(first < 0 && expected.Count == steps.Count,
            first < 0 ? $"{steps.Count} steps, the oracle has {expected.Count}" : $"step {first}: oracle {expected[first]} / c# {steps[first]}");

        Assert.Equal(trace.GetProperty("executed").GetInt32(), steps.Count);
        Assert.Equal(trace.GetProperty("end").GetString(), host.End);
        Assert.Equal(trace.GetProperty("last_offset").GetInt32(), host.LastOffset);
        Assert.Equal("[[40,68]]", JsonSerializer.Serialize(SkippedRanges(steps)));
        Assert.Equal(JsonSerializer.Serialize(trace.GetProperty("skipped").EnumerateArray().Select(r => r.EnumerateArray().Select(x => x.GetInt32()).ToArray())),
            JsonSerializer.Serialize(SkippedRanges(steps)));
        Assert.Equal(0, vars.Memory.OutOfBlockAccesses);
    }

    private static List<int[]> SkippedRanges(List<string> steps)
    {
        List<int> idx = steps.Select(s => int.Parse(s[..s.IndexOf(' ', StringComparison.Ordinal)], CultureInfo.InvariantCulture)).ToList();
        var r = new List<int[]>();
        for (int i = 0; i + 1 < idx.Count; i++)
        {
            if (idx[i + 1] > idx[i] + 1)
            {
                r.Add([idx[i] + 1, idx[i + 1] - 1]);
            }
        }

        return r;
    }

    /// <summary>One step as text: <c>index offset name writes=[F[23]=2] goto=LBL vars=F823=1,...</c>.</summary>
    private static string Canonical(JsonElement s)
    {
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"{s.GetProperty("index").GetInt32()} {s.GetProperty("offset").GetInt32()} {s.GetProperty("name").GetString()}");
        if (s.TryGetProperty("writes", out JsonElement w))
        {
            b.Append(" writes=").Append(string.Join(",", w.EnumerateArray().Select(x => $"{x[0].GetString()}={x[1].GetInt32()}")));
        }

        if (s.TryGetProperty("goto", out JsonElement g))
        {
            b.Append(" goto=").Append(g.GetString());
        }

        if (s.TryGetProperty("vars", out JsonElement v))
        {
            b.Append(" vars=").Append(string.Join(",", v.EnumerateObject().Select(p => $"{p.Name}={p.Value.GetInt32()}")));
        }

        return b.ToString();
    }

    private static string Canonical(FlowStep s, VarState vars)
    {
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"{s.Index} {s.Offset} {s.DisplayName}");
        if (s.Writes.Count > 0)
        {
            b.Append(" writes=").Append(string.Join(",", s.Writes.Select(x => $"{VarAddresses.Name(x.Va)}={x.Value}")));
        }

        if (s.Goto is { Length: > 0 } g)
        {
            b.Append(" goto=").Append(Encoding.Latin1.GetString(g));
        }

        if (s.DisplayName == "ZM")
        {
            VarMemory m = vars.Memory;
            ushort f823 = m.Peek(VarAddresses.OfF(823));
            IEnumerable<string> plain = MessageVars.Select(n => $"{n}={m.Peek(VarAddresses.OfF(int.Parse(n[1..], CultureInfo.InvariantCulture)))}");
            IEnumerable<string> c = MessageCVars.Select(x => $"{x.Name}={m.Peek(VarAddresses.OfF((x.N + f823) & 0xFFFF))}");
            b.Append(" vars=").Append(string.Join(",", plain.Concat(c)));
        }

        return b.ToString();
    }

    private sealed class SliceScripts : IFlowScripts
    {
        private readonly Dictionary<int, (ScriptImage Image, LabelTable Labels)> map = [];

        public void Add(int k, byte[] bytes, JsonElement labels)
        {
            var table = LabelTable.FromLabels(labels.EnumerateArray().Select(l =>
                (l.GetProperty("name").GetString()!, l.GetProperty("block").GetInt32(), l.GetProperty("offset").GetInt32())));
            map[k] = (ScriptSegmenter.CreateImage(k, bytes), table);
        }

        public ScriptImage Script(int k) => map[k].Image;

        public LabelTable Labels(int k) => map[k].Labels;
    }

    /// <summary>
    /// Steps over every host command like the oracle's tracer, yields on the PS4's yielding commands, and halts after the
    /// stop statement or at END_ / RTM_ / RTMN.
    /// </summary>
    private sealed class TraceHost(byte[] script, string? stop) : IHost
    {
        public bool Done { get; private set; }

        public string? End { get; private set; }

        public int LastOffset { get; private set; } = -1;

        public StepResult Dispatch(in Statement statement)
        {
            Assert.False(Done, "a statement ran after the end of the trace");
            string name = statement.Name.ToString();
            string raw = Encoding.Latin1.GetString(script, statement.Offset, statement.End - statement.Offset);
            LastOffset = statement.Offset;
            if (stop is not null && raw == stop)
            {
                (Done, End) = (true, "stop statement");
                return StepResult.Halt;
            }

            if (EndNames.Contains(name))
            {
                (Done, End) = (true, name);
                return StepResult.Halt;
            }

            return YieldNames.Contains(name) || YieldNames.Contains(name[..2]) ? StepResult.Yield : StepResult.Continue;
        }
    }
}

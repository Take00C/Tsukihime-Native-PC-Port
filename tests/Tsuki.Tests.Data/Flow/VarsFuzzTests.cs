using System.Globalization;
using System.Text;
using System.Text.Json;
using Tsuki.Engine.Vm.Variables;
using VarsBlock = Tsuki.Engine.Contracts.Vars;

namespace Tsuki.Tests.Data.Flow;

/// <summary>
/// The 61,000 fuzz cases of vm_variables.md 10 (<c>misc_vm_variables_errata fuzz 1000 20261025</c> and
/// <c>fuzz 20000 1|2|3</c>): the C# <see cref="VarCommands"/> against <c>tsuki.vm_vars</c>, which equals the engine
/// under unicorn on exactly these cases (re-run by <c>vars_fuzz_oracle.py --engine</c>: 0 mismatches). Every field the
/// engine comparison checks is compared, plus the whole argument buffer and the SELR / SELS locked flags.
/// </summary>
public sealed class VarsFuzzTests
{
    private const int ExpectedCases = 61_000;

    private static string Script => Path.Combine(GoldenData.RepoRoot, "tests", "Tsuki.Tests.Data", "Flow", "vars_fuzz_oracle.py");

    [GameDataFact]
    public void AllFuzzCasesEqualTheOracle()
    {
        Layout? layout = null;
        byte[]? basePattern = null;
        int cases = 0;
        int equal = 0;
        var perCommand = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var mismatches = new List<string>();
        JsonElement? summary = null;
        OracleProcess.Run(Script, string.Empty, TimeSpan.FromMinutes(5), line =>
        {
            using var doc = JsonDocument.Parse(line);
            JsonElement e = doc.RootElement;
            if (e.TryGetProperty("header", out _))
            {
                layout = Layout.From(e);
                basePattern = layout.BlockImage();
                return;
            }

            if (e.TryGetProperty("summary", out _))
            {
                summary = e.Clone();
                return;
            }

            Assert.NotNull(layout);
            cases++;
            string text = e.GetProperty("text").GetString()!;
            string key = text.Substring(1, 4) + (e.GetProperty("pre").GetBoolean() ? ".pre" : string.Empty);
            perCommand[key] = perCommand.GetValueOrDefault(key) + 1;
            List<string> diff = RunCase(e, layout!, basePattern!);
            if (diff.Count == 0)
            {
                equal++;
            }
            else if (mismatches.Count < 10)
            {
                mismatches.Add($"seed {e.GetProperty("seed").GetInt32()} case {e.GetProperty("i").GetInt32()} {text}: {string.Join("; ", diff)}");
            }
        });

        Assert.True(summary.HasValue, "the oracle printed no summary line");
        Assert.True(mismatches.Count == 0, $"{cases - equal} of {cases} cases differ:\n{string.Join("\n", mismatches)}");
        Assert.Equal(ExpectedCases, cases);
        Assert.Equal(ExpectedCases, summary.Value.GetProperty("cases").GetInt32());
        Assert.Equal(ExpectedCases, equal);

        // every handled name (and the prefetch variants) is in the set
        foreach (string name in VarCommands.HandledMain)
        {
            Assert.True(perCommand.GetValueOrDefault(name) > 100, $"{name}: {perCommand.GetValueOrDefault(name)} cases");
        }

        foreach (string name in VarCommands.HandledPrefetch)
        {
            Assert.True(perCommand.GetValueOrDefault(name + ".pre") > 100, $"{name}.pre: {perCommand.GetValueOrDefault(name + ".pre")} cases");
        }
    }

    private static List<string> RunCase(JsonElement e, Layout layout, byte[] basePattern)
    {
        bool prefetch = e.GetProperty("pre").GetBoolean();
        var init = new Dictionary<long, ushort>();
        foreach (JsonElement kv in e.GetProperty("init").EnumerateArray())
        {
            init[kv[0].GetInt64()] = (ushort)kv[1].GetInt32();
        }

        ushort Initial(long va) => layout.Initial(va, prefetch, init);

        var block = new VarsBlock((byte[])basePattern.Clone());
        var mem = new VarMemory(block, OutOfBlockPolicy.Flat, Initial);
        foreach ((long va, ushort v) in init)
        {
            if (VarMemory.TryWord(va, out _))
            {
                mem.Poke(va, v);
            }
        }

        mem.Log = [];
        var st = new VarState(mem)
        {
            ChoiceId = 0,
            ScanMode = e.GetProperty("scan").GetBoolean(),
            SkipCounter = (ushort)e.GetProperty("skip").GetInt32(),
            SnapshotBlock = (byte)e.GetProperty("block").GetInt32(),
        };
        Convert.FromHexString(e.GetProperty("argbuf").GetString()!).CopyTo(st.ArgBuffer, 0);
        byte[] stmt = Encoding.Latin1.GetBytes(e.GetProperty("text").GetString()! + "_DMMY();");
        VarEffect eff = VarCommands.Execute(stmt, Array.IndexOf(stmt, (byte)'_') + 1, st, prefetch);

        var d = new List<string>();
        void Check<T>(string what, T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                d.Add($"{what} oracle={expected} c#={actual}");
            }
        }

        string expLog = string.Join(" ", e.GetProperty("log").EnumerateArray().Select(a =>
            $"{a[0].GetString()}:{VarAddresses.Name(a[1].GetInt64())}={a[2].GetInt32()}"));
        string gotLog = string.Join(" ", mem.Log.Select(a =>
            $"{(a.Kind == VarAccessKind.Read ? "r" : "w")}:{VarAddresses.Name(a.Va)}={a.Value}"));
        Check("log", expLog, gotLog);
        JsonElement g = e.GetProperty("goto");
        Check("goto", g.ValueKind == JsonValueKind.Null ? "null" : g.GetString()!, eff.Goto is null ? "null" : Convert.ToHexStringLower(eff.Goto));
        Check("handled", e.GetProperty("handled").GetBoolean(), eff.Handled);
        Check("yielded", e.GetProperty("yielded").GetBoolean(), eff.Yielded);
        Check("snapshot", e.GetProperty("snapshot").GetBoolean(), eff.Snapshot);
        Check("choice_ui", e.GetProperty("choice_ui").GetBoolean(), eff.ChoiceUi);
        JsonElement cp = e.GetProperty("choice_point");
        Check("choice_point", cp.ValueKind == JsonValueKind.Null ? "null" : cp.GetInt32().ToString(CultureInfo.InvariantCulture),
            eff.ChoicePoint?.ToString(CultureInfo.InvariantCulture) ?? "null");
        Check("argbuf", e.GetProperty("argbuf_out").GetString()!, Convert.ToHexStringLower(st.ArgBuffer));
        Check("kind", e.GetProperty("kind").GetInt32(), (int)st.ChoiceKind);
        Check("index", e.GetProperty("index").GetInt32(), (int)st.ChoiceIndex);
        Check("id", e.GetProperty("id").GetInt32(), st.ChoiceId & 0xFFFF);
        Check("mode", e.GetProperty("mode").GetInt64(), (long)st.ChoiceMode);
        Check("count", e.GetProperty("count").GetInt32(), st.ChoiceCount);
        Check("values", string.Join(",", e.GetProperty("values").EnumerateArray().Select(v => v.GetInt32())), string.Join(",", st.ChoiceValues));
        Check("locked", string.Join(",", e.GetProperty("locked").EnumerateArray().Select(v => v.GetBoolean())), string.Join(",", st.ChoiceLocked));
        return d;
    }

    /// <summary>The initial memory of a case (VarEmu.initial_word), from the oracle's header line.</summary>
    private sealed record Layout(long LogLo, long FillLo, long FillHi, (long Save, long Base, long Size)[] SaveAreas)
    {
        public static Layout From(JsonElement h)
        {
            var areas = h.GetProperty("save_areas").EnumerateArray()
                .Select(a => (a[0].GetInt64(), a[1].GetInt64(), a[2].GetInt64())).ToArray();
            var l = new Layout(h.GetProperty("log_lo").GetInt64(), h.GetProperty("fill_lo").GetInt64(), h.GetProperty("fill_hi").GetInt64(), areas);
            foreach (JsonElement c in h.GetProperty("pattern_check").EnumerateArray())
            {
                Assert.Equal(c[1].GetInt32(), l.Pattern(c[0].GetInt64()));
            }

            return l;
        }

        public int Pattern(long va) => (int)((0x2000 + ((va - LogLo) / 2 % 0xC000)) & 0xFFFF);

        public ushort Initial(long va, bool prefetch, Dictionary<long, ushort> init)
        {
            if (init.TryGetValue(va, out ushort v))
            {
                return v;
            }

            if (prefetch)
            {
                foreach ((long save, long b, long n) in SaveAreas)
                {
                    if (va >= save && va < save + n)
                    {
                        return Initial(b + (va - save), false, init);
                    }
                }
            }

            return va >= FillLo && va < FillHi ? (ushort)Pattern(va) : (ushort)0;
        }

        /// <summary>The block's bytes under the fill pattern (the overrides are applied per case).</summary>
        public byte[] BlockImage()
        {
            var b = new byte[VarsBlock.Size];
            for (int w = 0; w < VarsBlock.WordCount; w++)
            {
                long va = VarsBlock.VaOfWord(w);
                ushort v = va >= FillLo && va < FillHi ? (ushort)Pattern(va) : (ushort)0;
                b[2 * w] = (byte)v;
                b[(2 * w) + 1] = (byte)(v >> 8);
            }

            return b;
        }
    }
}

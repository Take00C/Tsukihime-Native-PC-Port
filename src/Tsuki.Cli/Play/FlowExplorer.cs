using System.Globalization;
using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Flow;
using Tsuki.Engine.Vm;
using Tsuki.Engine.Vm.Variables;

namespace Tsuki.Cli.Play;

/// <summary>A choice taken on the way to a state: the option's line index and the script it was taken in.</summary>
internal sealed record PathNode(PathNode? Parent, int Script, int Option);

/// <summary>One playthrough of a profile chain: the choices of the run and the run that produced the profile it started with.</summary>
internal sealed record ProfileRun(ProfileRun? Previous, PathNode? Path);

/// <summary>What <see cref="FlowExplorer.Explore"/> found.</summary>
internal sealed class ExploreResult
{
    /// <summary>For each script reached: the chain of playthroughs that reaches it (each with its choices).</summary>
    public Dictionary<int, ProfileRun> First { get; } = [];

    public long Expansions { get; set; }

    public long States { get; set; }

    public int Profiles { get; set; }

    public bool BudgetHit { get; set; }

    /// <summary>For each script a playthrough ended in (<c>_END_ _RTM_ _RTMN</c>): the first run found that ends there (package K15, route plans).</summary>
    public Dictionary<int, ProfileRun> Ends { get; } = [];

    /// <summary>The line indices of every playthrough of the chain to script <paramref name="k"/>, first playthrough first.</summary>
    public List<int[]> ChainOf(int k) => ChainOf(First[k]);

    /// <summary>The line indices of every playthrough of <paramref name="run"/>'s chain, first playthrough first.</summary>
    public static List<int[]> ChainOf(ProfileRun run)
    {
        var runs = new List<int[]>();
        for (ProfileRun? r = run; r is not null; r = r.Previous)
        {
            var picks = new List<int>();
            for (PathNode? n = r.Path; n is not null; n = n.Parent)
            {
                picks.Add(n.Option);
            }

            picks.Reverse();
            runs.Add([.. picks]);
        }

        runs.Reverse();
        return runs;
    }
}

/// <summary>
/// A logic-only explorer of the story: the script VM with a host that does nothing but follow jumps and stop at choices. It explores every choice of every
/// choice point depth-first, prunes a state it has seen (script position and the variables that decide a branch), and carries the global words from one
/// playthrough to the next like the game does (a finished route unlocks the next). A reached scene comes with the list of choices of the playthroughs that
/// lead there, which the full engine can replay.
/// </summary>
internal sealed class FlowExplorer
{
    private const int VarWords = Vars.WordCount;
    private const int GBase = Vars.GWord;

    private readonly IFlowScripts _scripts;
    private readonly IGameFlowTables _flow;
    private readonly HashSet<int> _relevant;
    private readonly int[] _relevantList;

    public FlowExplorer(IFlowScripts scripts, IGameFlowTables flow)
    {
        _scripts = scripts;
        _flow = flow;
        _relevant = Analyze(scripts, flow.SceneCount);

        _relevantList = [.. _relevant.Order()];
    }

    /// <summary>Variable words (F: index, G: 2600 + index) that decide a branch, with the copies feeding them.</summary>
    public IReadOnlyCollection<int> RelevantWords => _relevant;

    private static string[] Args(in Statement st)
    {
        ReadOnlySpan<byte> a = st.Args.Span;
        var ranges = new List<ArgRange>(8);
        ArgumentList.Split(a, ArgumentList.StartIn(st), ranges);
        var result = new string[ranges.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Encoding.Latin1.GetString(a[ranges[i].Start..ranges[i].End]);
        }

        return result;
    }

    private static int WordOf(VarOperand op) => op.Kind switch
    {
        OperandKind.F => (ushort)op.Index,
        OperandKind.G => GBase + (ushort)op.Index,
        _ => -1,
    };

    private static HashSet<int> Analyze(IFlowScripts scripts, int count)
    {
        var relevant = new HashSet<int>();
        var copies = new List<(int From, int To)>();
        for (int k = 0; k < count; k++)
        {
            ScriptImage image = scripts.Script(k);
            ReadOnlyMemory<byte> bytes = image.Bytes;
            int pos = 0;
            while (pos < bytes.Length)
            {
                ScriptSegment seg = ScriptSegmenter.Read(bytes.Span, pos);
                pos = seg.End;
                if (!ScriptSegmenter.TryGetStatement(k, bytes, seg, out Statement st))
                {
                    continue;
                }

                string name = st.Name.ToString();
                switch (name)
                {
                    case "IF__":
                    {
                        string[] a = Args(st);
                        foreach (int i in new[] { 0, 2 })
                        {
                            if (i < a.Length)
                            {
                                int w = WordOf(VarOperand.Parse(a[i], OperandRole.Value));
                                if (w >= 0)
                                {
                                    relevant.Add(w);
                                }
                            }
                        }

                        break;
                    }

                    case "GOTS":
                    {
                        string[] a = Args(st);
                        if (a.Length > 0)
                        {
                            int w = WordOf(VarOperand.Parse(a[0], OperandRole.Gots));
                            if (w >= 0)
                            {
                                relevant.Add(w);
                            }
                        }

                        break;
                    }

                    case "WKST" or "WKAD" or "WKOR" or "WKOF" or "WKBO":
                    {
                        string[] a = Args(st);
                        int srcIndex = name == "WKST" ? 2 : 1;
                        if (a.Length > srcIndex)
                        {
                            int dst = WordOf(VarOperand.Parse(a[0], OperandRole.WkTarget));
                            int src = WordOf(VarOperand.Parse(a[srcIndex], OperandRole.Value));
                            if (dst >= 0 && src >= 0)
                            {
                                copies.Add((src, dst));
                            }
                        }

                        break;
                    }
                }
            }
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach ((int from, int to) in copies)
            {
                if (relevant.Contains(to) && relevant.Add(from))
                {
                    changed = true;
                }
            }
        }

        return relevant;
    }

    // -- the host -------------------------------------------------------------------------------------------------------------------

    private sealed class FlowHost(FlowExplorer owner, VarState vars) : IHost
    {
        public bool ChoiceOpen;
        public bool Ended;
        public bool Missing;
        public readonly HashSet<int> Entered = [];

        public int Script { get; set; }

        public StepResult Dispatch(in Statement st)
        {
            string name = st.Name.ToString();
            switch (name)
            {
                case "SELD":
                    ChoiceOpen = true;
                    return StepResult.Yield;
                case "SELA":
                    Reset();
                    return StepResult.Continue;
                case "SELS":
                    VarCommands.ChoiceStore(vars, 1);
                    Reset();
                    return StepResult.Continue;
                case "END_" or "RTM_" or "RTMN":
                    Ended = true;
                    return StepResult.Halt;
                case "IFSC":
                    return JumpToLabel(st, 1, true);
                case "IFLA":
                    return JumpToLabel(st, 1, InlineReader.ReadInt16(st, 0) != (int)Language.En);
                case "RDST":
                {
                    int n = new Tsuki.Engine.Stage.StageArgs(st).Expr(0);
                    if (n is >= 0 and <= 0x23F)
                    {
                        int w = Vars.GWord + 110 + (n >> 4);
                        vars.Memory.Block.SetWord(w, (ushort)(vars.Memory.Block.GetWord(w) | (1 << (n & 15))));
                    }

                    return StepResult.Continue;
                }
            }

            if (st.Name.First == (byte)'J')
            {
                return Jump(st);
            }

            return StepResult.Continue;
        }

        public void Reset()
        {
            vars.ChoiceMode = ChoiceMode.None;
            vars.ChoiceCount = 0;
            vars.ChoiceValues.Clear();
            vars.ChoiceLocked.Clear();
            ChoiceOpen = false;
        }

        private StepResult JumpToLabel(in Statement st, int argIndex, bool condition)
        {
            if (!condition)
            {
                return StepResult.Continue;
            }

            string[] a = Args(st);
            if (argIndex >= a.Length || a[argIndex].Length == 0)
            {
                return StepResult.Continue;
            }

            int off = owner._scripts.Labels(Script).Find(a[argIndex]);
            return off < 0 ? StepResult.Continue : StepResult.ContinueAt(new ScriptPosition(Script, off));
        }

        private StepResult Jump(in Statement st)
        {
            string[] a = Args(st);
            int id = a.Length > 0 && int.TryParse(a[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;
            int k = owner._flow.ScriptOfSceneId(id);
            if (k < 0)
            {
                k = owner._flow.ScriptOfSceneId(110);
            }

            int offset = 0;
            if (a.Length > 1 && a[1].Length > 0)
            {
                offset = owner._scripts.Labels(k).Find(a[1]);
                if (offset < 0)
                {
                    Missing = true;
                    return StepResult.Halt;
                }
            }

            Script = k;
            Entered.Add(k);
            return StepResult.ContinueAt(new ScriptPosition(k, offset));
        }
    }


    // -- the search -----------------------------------------------------------------------------------------------------------------

    /// <summary>A choice point: where the VM stands, the deciding variable words, the choice registers and how the state was reached.</summary>
    private sealed class Snapshot
    {
        public ushort[] Words = [];
        public int Script;
        public int Cursor;
        public int Count;
        public ushort[] Values = [];
        public bool[] Locked = [];
        public byte Kind;
        public ushort Index;
        public short Id;
        public uint Mode;
        public PathNode? Path;
        public ProfileRun? Chain;
        public int Depth;
    }

    private readonly Queue<(ushort[] Words, ProfileRun? Chain)> _pending = new();
    private List<int>[] _predecessors = [];

    private static ulong Key(Snapshot s)
    {
        ulong h = 14695981039346656037UL;
        h = (h ^ (uint)s.Script) * 1099511628211UL;
        h = (h ^ (uint)s.Cursor) * 1099511628211UL;
        foreach (ushort w in s.Words)
        {
            h = (h ^ w) * 1099511628211UL;
        }

        return h;
    }

    private ulong ProfileKey(ushort[] words)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < _relevantList.Length; i++)
        {
            if (_relevantList[i] >= GBase)
            {
                h = (h ^ words[i]) * 1099511628211UL;
            }
        }

        return h;
    }

    private int[] DistancesTo(HashSet<int> reached)
    {
        // hops from every script to the nearest script that no state has entered yet (reverse breadth-first search)
        int n = _flow.SceneCount;
        var dist = new int[n];
        Array.Fill(dist, int.MaxValue);
        var q = new Queue<int>();
        for (int k = 0; k < n; k++)
        {
            if (!reached.Contains(k))
            {
                dist[k] = 0;
                q.Enqueue(k);
            }
        }

        while (q.Count > 0)
        {
            int t = q.Dequeue();
            foreach (int p in _predecessors[t])
            {
                if (dist[p] == int.MaxValue)
                {
                    dist[p] = dist[t] + 1;
                    q.Enqueue(p);
                }
            }
        }

        return dist;
    }

    private ushort[] Words(Vars vars)
    {
        var w = new ushort[_relevantList.Length];
        for (int i = 0; i < w.Length; i++)
        {
            w[i] = vars.GetWord(_relevantList[i]);
        }

        return w;
    }

    private Snapshot Capture(ScriptVm vm, VarState state, Vars vars, PathNode? path, ProfileRun? chain, int depth) => new()
    {
        Words = Words(vars),
        Script = vm.Image.Index,
        Cursor = vm.Cursor,
        Count = state.ChoiceCount,
        Values = [.. state.ChoiceValues],
        Locked = [.. state.ChoiceLocked],
        Kind = state.ChoiceKind,
        Index = state.ChoiceIndex,
        Id = state.ChoiceId,
        Mode = state.ChoiceMode,
        Path = path,
        Chain = chain,
        Depth = depth,
    };

    private void Restore(Snapshot s, ScriptVm vm, VarState state, Vars vars, FlowHost host)
    {
        vars.Bytes.Clear();
        for (int i = 0; i < _relevantList.Length; i++)
        {
            vars.SetWord(_relevantList[i], s.Words[i]);
        }

        vm.Seek(new ScriptPosition(s.Script, s.Cursor));
        state.ChoiceCount = s.Count;
        state.ChoiceValues.Clear();
        state.ChoiceValues.AddRange(s.Values);
        state.ChoiceLocked.Clear();
        state.ChoiceLocked.AddRange(s.Locked);
        state.ChoiceKind = s.Kind;
        state.ChoiceIndex = s.Index;
        state.ChoiceId = s.Id;
        state.ChoiceMode = s.Mode;
        host.Script = s.Script;
        host.ChoiceOpen = true;
    }

    /// <summary>
    /// Explores from START_SCRIPT with the prologue read, best-first: the next state to expand is the one whose script is the fewest static jumps away from a
    /// scene nobody has reached (ties: the fewest choices taken). <paramref name="maxExpansions"/> bounds the work.
    /// </summary>
    public ExploreResult Explore(long maxExpansions = 5_000_000)
    {
        var graph = SceneGraph.Build(_scripts, _flow);
        _predecessors = [.. Enumerable.Range(0, _flow.SceneCount).Select(_ => new List<int>())];
        for (int k = 0; k < _flow.SceneCount; k++)
        {
            foreach (int t in graph.Successors(k))
            {
                _predecessors[t].Add(k);
            }
        }

        var result = new ExploreResult();
        var reached = new HashSet<int>();
        int[] dist = DistancesTo(reached);
        var frontier = new PriorityQueue<Snapshot, (int Dist, int Depth)>();
        var visited = new HashSet<ulong>();
        var profileSeen = new HashSet<ulong>();
        var vars = new Vars();
        var state = new VarState(new VarMemory(vars, OutOfBlockPolicy.Flat));
        var host = new FlowHost(this, state);
        var vm = new ScriptVm(_scripts, state, new MarkerState(new SystemBlock()), _flow.ScriptOfSceneId(110));

        void Enter(int script, ProfileRun? chain, PathNode? path)
        {
            if (reached.Add(script))
            {
                result.First[script] = new ProfileRun(chain, path);
                dist = DistancesTo(reached);
            }
        }

        // runs the VM to its next choice (pushing the state), or to the end of the playthrough (queueing the next profile)
        void Advance(PathNode? path, ProfileRun? chain, int depth)
        {
            int guard = 0;
            while (guard++ < 100_000)
            {
                host.Entered.Clear();
                StepEnd end = vm.Step(host);
                foreach (int k in host.Entered)
                {
                    Enter(k, chain, path);
                }

                host.Script = vm.Image.Index;
                if (host.ChoiceOpen)
                {
                    Snapshot snap = Capture(vm, state, vars, path, chain, depth);
                    frontier.Enqueue(snap, (dist[snap.Script], depth));
                    return;
                }

                if (host.Ended || host.Missing || end is StepEnd.EndOfScript or StepEnd.Halted)
                {
                    if (host.Ended)
                    {
                        result.Ends.TryAdd(vm.Image.Index, new ProfileRun(chain, path));
                        ushort[] next = Words(vars);
                        if (profileSeen.Add(ProfileKey(next)))
                        {
                            _pending.Enqueue((next, new ProfileRun(chain, path)));
                        }
                    }

                    host.Ended = false;
                    host.Missing = false;
                    return;
                }
            }
        }

        void StartPlaythrough(ushort[] words, ProfileRun? chain)
        {
            // F and Z cleared, the global words as the last playthrough left them
            vars.Bytes.Clear();
            for (int i = 0; i < _relevantList.Length; i++)
            {
                if (_relevantList[i] >= GBase)
                {
                    vars.SetWord(_relevantList[i], words[i]);
                }
            }

            int start = _flow.ScriptOfSceneId(110);
            vm.Seek(new ScriptPosition(start, 0));
            host.Script = start;
            host.Reset();
            host.Ended = false;
            host.Missing = false;
            Enter(start, chain, null);
            Advance(null, chain, 0);
        }

        // the first playthrough starts with a fresh profile (the prologue has been read; no deciding word is set by it)
        ushort[] initial = new ushort[_relevantList.Length];
        profileSeen.Add(ProfileKey(initial));
        _pending.Enqueue((initial, null));
        while ((_pending.Count > 0 || frontier.Count > 0) && result.Expansions < maxExpansions)
        {
            if (_pending.Count > 0)
            {
                // start the next playthrough as soon as its profile is known: a new profile can open whole routes
                (ushort[] words, ProfileRun? chain) = _pending.Dequeue();
                result.Profiles++;
                StartPlaythrough(words, chain);
                continue;
            }

            Snapshot s = frontier.Dequeue();
            if (!visited.Add(Key(s)))
            {
                continue;
            }

            result.States++;
            for (int option = 0; option < s.Count; option++)
            {
                if (s.Mode == ChoiceMode.List && s.Locked.Length > option && s.Locked[option])
                {
                    continue;
                }

                result.Expansions++;
                Restore(s, vm, state, vars, host);
                VarCommands.ChoiceStore(state, option);
                host.Reset();
                var path = new PathNode(s.Path, s.Script, option);
                Advance(path, s.Chain, s.Depth + 1);
            }
        }

        result.BudgetHit = result.Expansions >= maxExpansions;
        return result;
    }
}

using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;
using Tsuki.Engine.Vm;

namespace Tsuki.Engine.Game;

/// <summary>What <see cref="FlowRepair.Repair"/> added.</summary>
/// <param name="Choices">Choice points whose edges were marked (taken in an old playthrough).</param>
/// <param name="Prologue">True when the prologue's marks were added (node 1 had been started).</param>
/// <param name="Scenes">Scenes completed through a shared ending, now done.</param>
/// <param name="Bits">The persistent bits that were not set before.</param>
public readonly record struct FlowRepairResult(int Choices, bool Prologue, int Scenes, int Bits);

/// <summary>
/// Package K44: the flowchart marks that v0.1.0..v0.1.4 never wrote (they lacked <see cref="FlowMarks"/>' special pairs and choice edges), rebuilt once on start
/// from what the system data and the save slots prove, like package K36 rebuilt the voice rows. Only marks the PS4 is sure to have written are added:
/// <list type="bullet">
/// <item>every choice point taken (<c>choice_taken</c>) had its list open, so its edges are marked (0xB8620; the parts that depend on the playthrough's
/// state are left out);</item>
/// <item>node 1 started means the prologue ran: START_SCRIPT -> 00_00 -> 01_00_ARC01_1 with its hidden nodes 485, 528, 529, its edges and 00_00 done;</item>
/// <item>a jump the old marks prove (an edge id set, held by the first records of the pairs that could have set it) gets the rest of what the PS4 marks for
/// it in every state: the extra edges of 355 -> 20, 136 -> 137, 213 -> 215, ...;</item>
/// <item>a scene that ends in another scene's shared tail (the tail's <c>_RDED</c> completes it because the jumps into the tail keep it the current node) is
/// done when the tail's last marker was read and it is the only scene started of all that can read that marker (the tail's own scene, every scene whose
/// jumps keep it current on the way there), or when a save's playthrough reached it and the scene after the tail but none of the others. The tail's exit
/// jump then gets its marks too (68 -> 66 through 67 -> 66).</item>
/// </list>
/// A scene whose tail was read more than one way, and the jumps whose hidden node depends on route bits or the last pick, are left: nothing in the save says
/// which way the playthrough went. They light the next time that part is played. A slot saved inside such a tail by an old build holds the tail's own node
/// in F110; <see cref="RepairLoadedRecord"/> puts the PS4's back when the slot is loaded.
/// </summary>
public static class FlowRepair
{
    private const int Node1 = 1;
    private const int GlassMoon = 485;
    private static readonly ConditionalWeakTable<IGameData, ScriptGraph> Graphs = new();

    /// <summary>
    /// True for a profile written before package K44 that played the prologue: node 1 started, node 485 (A Glass Moon) not done. With K44 the jump into 00_00
    /// marks 485 done before node 1 can start (0xB94F3), so a profile repaired or played once never qualifies again.
    /// </summary>
    public static bool Needed(Vars vars) => SystemData.SceneSeen(vars, Node1) && !SystemData.SceneDone(vars, GlassMoon);

    /// <summary>Adds the marks (see the class summary) to <paramref name="vars"/>' G words.</summary>
    /// <param name="data">The game data (the scripts and the flowchart tables).</param>
    /// <param name="system">The system data (readmax, <c>choice_taken</c>).</param>
    /// <param name="vars">The variables whose G words are repaired.</param>
    /// <param name="playthroughs">The saved states of the stored slots (state A), whose playthrough bits can prove a way through a shared tail.</param>
    public static FlowRepairResult Repair(IGameData data, SystemData system, Vars vars, IEnumerable<HistoryRecord>? playthroughs = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(vars);
        IGameFlowTables flow = data.Eboot.GameFlow;
        ushort[] before = Globals(vars);

        int choices = 0;
        for (int c = 1; c < Math.Min(flow.ChoiceEdges.Count, SystemBlock.ChoiceTakenCount); c++)
        {
            if (system.Block.GetChoiceTaken(c) != 0)
            {
                Or(vars, Unconditional(v => FlowMarks.Choice(v, flow.ChoiceEdges, c)));
                choices++;
            }
        }

        bool prologue = SystemData.SceneSeen(vars, Node1);
        if (prologue)
        {
            Or(vars, Unconditional(v =>
            {
                FlowMarks.JumpNodes(v, flow.Edges, -1, 486, 0);
                SystemData.SetSceneDone(v, 486);                // 00_00's _RDED(486): F110 = 486 since the jump
                FlowMarks.JumpNodes(v, flow.Edges, 486, Node1, 0);
            }));
        }

        ProvenJumps(flow, before, vars);

        ScriptGraph graph = Graphs.GetValue(data, d => new ScriptGraph(d));
        List<HistoryRecord> saved = playthroughs?.ToList() ?? [];
        int scenes = 0;
        foreach (TailEnd end in graph.Analysis.Ends)
        {
            if (end.Unknown || SystemData.SceneDone(vars, end.Reader))
            {
                continue;
            }

            bool read = system.IsRead(end.Block, end.Line) && end.Readers.Where(r => SystemData.SceneSeen(vars, r)).SequenceEqual([end.Reader]);
            if (read || saved.Any(r => end.PassedIn(r)))
            {
                SystemData.SetSceneDone(vars, end.Reader);
                if (end.Exit is { } exit && Invariant(v => FlowMarks.JumpNodes(v, flow.Edges, exit.From, exit.To, v.GetF(1)), end.Reader) is { } marks)
                {
                    Or(vars, marks);
                }

                scenes++;
            }
        }

        ushort[] after = Globals(vars);
        int bits = 0;
        for (int w = 0; w < after.Length; w++)
        {
            bits += System.Numerics.BitOperations.PopCount((uint)(after[w] & ~before[w]));
        }

        return new FlowRepairResult(choices, prologue, scenes, bits);
    }

    /// <summary>
    /// A slot saved by v0.1.0..v0.1.4 inside a shared tail holds F110 = the tail's own node, which the PS4 never writes there (the jump in kept the scene being
    /// left): set it back to that scene when the record's playthrough reached exactly one of the scenes that keep F110 into this statement and not the tail's own
    /// one past its <c>_RTST</c>. Without that the tail's <c>_RDED</c> would never complete the scene after the load. True when <paramref name="record"/> changed.
    /// </summary>
    public static bool RepairLoadedRecord(IGameData data, HistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(record);
        int f110 = HistoryRecord.FU16Indices.IndexOf(110);
        int node = record.Script < data.ScriptCount ? data.Eboot.GameFlow.NodeOfScript(record.Script) : -1;
        if (node < 1 || f110 < 0 || record.GetFU16(f110) != node || PlayBit(record, node))
        {
            return false;                                   // this build's state, or a state before the scene's _RTST
        }

        ScriptGraph graph = Graphs.GetValue(data, d => new ScriptGraph(d));
        ParsedScript s = graph.Get(record.Script);
        int i = s.IndexAt((long)record.CmdOffset + 1);      // CmdOffset is the statement's start - 1
        if (s.FirstRtst < 0 || i <= s.FirstRtst)
        {
            return false;
        }

        Analysis a = graph.Analysis;
        if (a.Unknown[record.Script] is { } unknown && unknown[i])
        {
            return false;
        }

        List<int> from = [.. a.Foreign[record.Script].Where(f => f.Visits[i] && PlayBit(record, f.Reader)).Select(f => f.Reader).Distinct()];
        if (from.Count != 1)
        {
            return false;
        }

        record.SetFU16(f110, (ushort)from[0]);
        return true;
    }

    // a jump of the old marks set the edge ids of its pair's first record (from 1..530, to > 0, GameHost.MarkSceneJump of v0.1.4): an id set proves one of the
    // pairs whose first record holds it ran, and what the PS4 marks for all of them in every state is added
    private static void ProvenJumps(IGameFlowTables flow, ushort[] before, Vars vars)
    {
        var pairs = new Dictionary<int, List<(int From, int To)>>();
        var first = new HashSet<(int, int)>();
        foreach (FlowEdgeRow e in flow.Edges)
        {
            if (e.From is < 1 or > 0x212 || e.To < 1 || !first.Add((e.From, e.To)))
            {
                continue;
            }

            foreach (int id in e.GlobalIds.Distinct())
            {
                (pairs.TryGetValue(id, out var list) ? list : pairs[id] = []).Add((e.From, e.To));
            }
        }

        var marks = new Dictionary<(int, int), ushort[]?>();
        foreach ((int id, List<(int From, int To)> list) in pairs)
        {
            if (id is < 564 or > 1267 || (before[400 + ((id - 564) >> 4)] >> ((id - 564) & 15) & 1) == 0)
            {
                continue;
            }

            ushort[]? all = null;
            foreach ((int from, int to) in list)
            {
                if (!marks.TryGetValue((from, to), out ushort[]? m))
                {
                    marks[(from, to)] = m = Invariant(v => FlowMarks.JumpNodes(v, flow.Edges, from, to, v.GetF(1)));
                }

                if (m is null)
                {
                    all = null;
                    break;
                }

                all = all is null ? m : [.. all.Zip(m, (x, y) => (ushort)(x & y))];
            }

            if (all is not null)
            {
                Or(vars, all);
            }
        }
    }

    /// <summary>The playthrough bit of node <paramref name="n"/> (F201.., 8 bits per word) in a saved state.</summary>
    private static bool PlayBit(HistoryRecord r, int n)
    {
        int k = n is >= 0 and <= 0x23F ? HistoryRecord.FS8Indices.IndexOf(201 + (n >> 3)) : -1;
        return k >= 0 && (r.GetFS8(k) >> (n & 7) & 1) != 0;
    }

    /// <summary>One statement of a script: its name, arguments, marker id (markers only) and start offset.</summary>
    private readonly record struct Stmt(string Name, string Args, int Block, int Line, int Start)
    {
        public bool IsMarker => Name[0] == 'Z';

        public bool IsLabel => Name[0] == 'Z' && Name[1] is 'Z' or 'Y';

        public bool EndsScript => Name is "END_" or "RTM_" or "RTMN";
    }

    /// <summary>A script's statements, its labels and where its own node's <c>_RDST</c> / <c>_RTST</c> first stand (-1 none).</summary>
    private sealed class ParsedScript(int node, Stmt[] statements)
    {
        public int Node { get; } = node;

        public Stmt[] Statements { get; } = statements;

        public Dictionary<string, int> Labels { get; } = Index(statements);

        public int FirstRdst { get; } = Array.FindIndex(statements, s => s.Name == "RDST" && s.Args == node.ToString(CultureInfo.InvariantCulture));

        public int FirstRtst { get; } = Array.FindIndex(statements, s => s.Name == "RTST" && s.Args == node.ToString(CultureInfo.InvariantCulture));

        /// <summary>The statement that starts at or before <paramref name="offset"/>.</summary>
        public int IndexAt(long offset)
        {
            int i = Array.FindLastIndex(Statements, s => s.Start <= offset);
            return Math.Max(i, 0);
        }

        private static Dictionary<string, int> Index(Stmt[] statements)
        {
            var labels = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < statements.Length; i++)
            {
                if (statements[i].IsLabel && statements[i].Args.Length > 0)
                {
                    labels.TryAdd(statements[i].Args, i);
                }
            }

            return labels;
        }
    }

    /// <summary>
    /// A <c>_RDED(Reader)</c> that a scene reaches in another scene's script with F110 still its own: the marker before it, every scene that can read that
    /// marker (<see cref="Readers"/>), whether a path of unknown F110 can (<see cref="Unknown"/>), the exit jump after the <c>_RDED</c>s, and the scenes that can
    /// run each other jump into the exit's target (<see cref="OtherEntries"/>; null when one of them cannot be told).
    /// </summary>
    private sealed record TailEnd(int Reader, int Script, int Block, int Line, int[] Readers, bool Unknown, (int From, int To)? Exit, int[][]? OtherEntries)
    {
        /// <summary>
        /// True when the playthrough of <paramref name="r"/> went through this <c>_RDED</c> with F110 = <see cref="Reader"/>: it reached the exit's target, so
        /// one of the jumps into it ran; none of the others' scenes was reached, so the exit did; and of the scenes that can run the exit only the reader was.
        /// </summary>
        public bool PassedIn(HistoryRecord r) =>
            Exit is { } exit && OtherEntries is { } others && PlayBit(r, Reader) && PlayBit(r, exit.To)
            && !Readers.Any(o => o != Reader && PlayBit(r, o)) && !others.Any(e => e.Any(o => PlayBit(r, o)));
    }

    /// <summary>The scripts' paths: per script, the statements its own node reaches, those other nodes reach with F110 kept, and those of unknown F110.</summary>
    private sealed class Analysis(BitArray?[] own, List<(int Reader, BitArray Visits)>[] foreign, BitArray?[] unknown, List<TailEnd> ends)
    {
        public BitArray?[] Own { get; } = own;

        public List<(int Reader, BitArray Visits)>[] Foreign { get; } = foreign;

        public BitArray?[] Unknown { get; } = unknown;

        public List<TailEnd> Ends { get; } = ends;
    }

    /// <summary>
    /// The scripts as a graph of statements (cached per game data; reading every script takes about a second). A path follows the next statement, the label of
    /// an <c>_IF*</c> (both ways) or a <c>_GOTO</c>, and a <c>_JUMP</c> whose pair keeps F110 (<see cref="Keeps"/>); it stops at any other <c>_JUMP</c> and at
    /// <c>_END_ _RTM_ _RTMN</c>. A node's own paths start at its scripts' tops and at the labels jumps enter before its <c>_RDST</c> (F110 = the node from the jump
    /// or the flowchart); a jump that does not keep F110 and enters past the <c>_RDST</c> starts a path of unknown F110.
    /// </summary>
    private sealed class ScriptGraph(IGameData data)
    {
        private readonly IGameFlowTables _flow = data.Eboot.GameFlow;
        private readonly Dictionary<int, ParsedScript> _scripts = [];
        private readonly Dictionary<(int, int), bool> _keeps = [];
        private readonly Lock _lock = new();
        private Analysis? _analysis;

        public ParsedScript Get(int k)
        {
            lock (_lock)
            {
                return GetLocked(k);
            }
        }

        public Analysis Analysis
        {
            get
            {
                lock (_lock)
                {
                    return _analysis ??= Analyse();
                }
            }
        }

        private ParsedScript GetLocked(int k)
        {
            if (!_scripts.TryGetValue(k, out ParsedScript? s))
            {
                _scripts[k] = s = new ParsedScript(_flow.NodeOfScript(k), Parse(data.Script(k)));
            }

            return s;
        }

        private bool Keeps(int from, int to)
        {
            if (!_keeps.TryGetValue((from, to), out bool keep))
            {
                _keeps[(from, to)] = keep = FlowRepair.Keeps(_flow, from, to);
            }

            return keep;
        }

        // the _JUMP's target script and the statement its label names (the top without one); null when the scene or the label does not exist
        private (int Script, int Index)? Target(Stmt st)
        {
            string[] a = st.Args.Split(',');
            if (!int.TryParse(a[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || _flow.ScriptOfSceneId(id) is not (>= 0 and var t) || t >= data.ScriptCount)
            {
                return null;
            }

            if (a.Length < 2 || a[1].Length == 0)
            {
                return (t, 0);
            }

            return GetLocked(t).Labels.TryGetValue(a[1], out int i) ? (t, i) : null;
        }

        private Dictionary<int, BitArray> Walk(IEnumerable<(int Script, int Index)> seeds)
        {
            var visits = new Dictionary<int, BitArray>();
            var todo = new Stack<(int Script, int Index)>(seeds);
            while (todo.Count > 0)
            {
                (int k, int i) = todo.Pop();
                ParsedScript s = GetLocked(k);
                if (i < 0 || i >= s.Statements.Length)
                {
                    continue;
                }

                if (!visits.TryGetValue(k, out BitArray? seen))
                {
                    visits[k] = seen = new BitArray(s.Statements.Length);
                }

                if (seen[i])
                {
                    continue;
                }

                seen[i] = true;
                Stmt st = s.Statements[i];
                if (st.EndsScript)
                {
                    continue;
                }

                if (st.Name == "JUMP")
                {
                    if (Target(st) is { } t && Keeps(s.Node, GetLocked(t.Script).Node))
                    {
                        todo.Push(t);
                    }

                    continue;
                }

                if (st.Name == "GOTO" || st.Name.StartsWith("IF", StringComparison.Ordinal))
                {
                    if (s.Labels.TryGetValue(st.Args[(st.Args.LastIndexOf(',') + 1)..], out int to))
                    {
                        todo.Push((k, to));
                    }

                    if (st.Name == "GOTO")
                    {
                        continue;
                    }
                }

                todo.Push((k, i + 1));
            }

            return visits;
        }

        private Analysis Analyse()
        {
            int count = data.ScriptCount;
            var jumps = new List<(int Script, int Index, int Target, int Label)>();
            for (int k = 0; k < count; k++)
            {
                Stmt[] st = GetLocked(k).Statements;
                for (int i = 0; i < st.Length; i++)
                {
                    if (st[i].Name == "JUMP" && Target(st[i]) is { } t)
                    {
                        jumps.Add((k, i, t.Script, t.Index));
                    }
                }
            }

            // the entries that do not keep F110: before the target's _RDST they start the target's own path, past it a path of unknown F110
            var ownSeeds = new Dictionary<int, List<(int, int)>>();
            var unknownSeeds = new List<(int, int)>();
            for (int k = 0; k < count; k++)
            {
                ParsedScript s = GetLocked(k);
                if (s.Node >= 1 && s.FirstRdst >= 0)
                {
                    (ownSeeds.TryGetValue(s.Node, out var l) ? l : ownSeeds[s.Node] = []).Add((k, 0));
                }
                else if (s.Node >= 1)
                {
                    unknownSeeds.Add((k, 0));                    // a script of the node without its _RDST: the node may run it unseen
                }
            }

            foreach ((int k, _, int t, int label) in jumps)
            {
                ParsedScript target = GetLocked(t);
                if (Keeps(GetLocked(k).Node, target.Node))
                {
                    continue;
                }

                if (target.Node >= 1 && target.FirstRdst >= 0 && label <= target.FirstRdst)
                {
                    (ownSeeds.TryGetValue(target.Node, out var l) ? l : ownSeeds[target.Node] = []).Add((t, label));
                }
                else if (target.Node >= 1)
                {
                    unknownSeeds.Add((t, label));
                }
            }

            var own = new BitArray?[count];
            var unknown = new BitArray?[count];
            var foreign = new List<(int Reader, BitArray Visits)>[count];
            for (int k = 0; k < count; k++)
            {
                foreign[k] = [];
            }

            foreach ((int k, BitArray v) in Walk(unknownSeeds))
            {
                unknown[k] = v;
            }

            // the scripts only a gallery replay or the developers' menu runs: ARCHIVE (scene 3000, F025 on, so no slot holds its playthrough; gallery.md 3.2),
            // DEBUG_TOP (8000: no jump enters it and the title starts 110, 260 or 3000), and the scripts without a node that only those enter
            var replay = new HashSet<int>(new[] { _flow.ScriptOfSceneId(3000), _flow.ScriptOfSceneId(8000) }.Where(k => k >= 0));
            for (bool grew = true; grew;)
            {
                grew = false;
                for (int k = 0; k < count; k++)
                {
                    if (GetLocked(k).Node < 1 && !replay.Contains(k) && jumps.Any(x => x.Target == k) && jumps.All(x => x.Target != k || replay.Contains(x.Script)))
                    {
                        grew |= replay.Add(k);
                    }
                }
            }

            foreach ((int node, List<(int, int)> seeds) in ownSeeds)
            {
                foreach ((int k, BitArray v) in Walk(seeds))
                {
                    if (GetLocked(k).Node == node)
                    {
                        own[k] = own[k] is { } o ? o.Or(v) : v;
                    }
                    else
                    {
                        foreign[k].Add((node, v));
                    }
                }
            }

            var ends = new List<TailEnd>();
            for (int k = 0; k < count; k++)
            {
                Stmt[] st = GetLocked(k).Statements;
                foreach ((int reader, BitArray v) in foreign[k])
                {
                    string want = reader.ToString(CultureInfo.InvariantCulture);
                    for (int i = 0; i < st.Length; i++)
                    {
                        if (v[i] && st[i].Name == "RDED" && st[i].Args == want && End(k, i, reader, own, foreign, unknown, jumps, replay) is { } end)
                        {
                            ends.Add(end);
                        }
                    }
                }
            }

            return new Analysis(own, foreign, unknown, ends);
        }

        private static bool Plain(Stmt s) => !s.IsMarker && !s.EndsScript && s.Name is not ("JUMP" or "GOTO") && !s.Name.StartsWith("IF", StringComparison.Ordinal);

        // the scenes whose paths reach statement i of script k (its own node, the nodes that keep F110 on the way there); null when a path of unknown F110 does
        private int[]? ReadersAt(int k, int i, BitArray?[] own, List<(int Reader, BitArray Visits)>[] foreign, BitArray?[] unknown)
        {
            if (unknown[k] is { } u && u[i])
            {
                return null;
            }

            var readers = new List<int>();
            if (own[k] is { } o && o[i])
            {
                readers.Add(GetLocked(k).Node);
            }

            readers.AddRange(foreign[k].Where(f => f.Visits[i]).Select(f => f.Reader));
            return [.. readers.Distinct()];
        }

        // the marker before _RDED(reader) at i: readmax records a marker when the next one is reached (MarkerState.Enter), so it reads as read only once the
        // story went past the _RDED; nothing but plain statements may stand between them. The exit is the _JUMP after the run of _RDEDs and plain statements.
        private TailEnd? End(int k, int i, int reader, BitArray?[] own, List<(int Reader, BitArray Visits)>[] foreign, BitArray?[] unknown, List<(int Script, int Index, int Target, int Label)> jumps, HashSet<int> replay)
        {
            ParsedScript s = GetLocked(k);
            Stmt[] st = s.Statements;
            int m = i - 1;
            while (m >= 0 && Plain(st[m]))
            {
                m--;
            }

            if (s.Node < 1 || m < 0 || !st[m].IsMarker)
            {
                return null;                                    // a script without a node: who enters it is not all walked
            }

            int[]? readers = ReadersAt(k, m, own, foreign, unknown);
            int j = i + 1;
            while (j < st.Length && Plain(st[j]))
            {
                j++;
            }

            (int From, int To)? exit = null;
            int[][]? others = null;
            if (j < st.Length && st[j].Name == "JUMP" && Target(st[j]) is { } t && GetLocked(t.Script) is { Node: >= 1 and var to } target && to != reader)
            {
                exit = (s.Node, to);
                if (target.FirstRtst >= 0 && t.Index <= target.FirstRtst)
                {
                    // every other jump into the target's scripts, as the scenes that can run it (a replay-only script runs none in a story)
                    List<int[]>? list = [];
                    foreach ((int xs, int xi, int xt, _) in jumps)
                    {
                        if (GetLocked(xt).Node != to || (xs == k && xi == j) || replay.Contains(xs))
                        {
                            continue;
                        }

                        if (GetLocked(xs).Node < 1 || ReadersAt(xs, xi, own, foreign, unknown) is not { } r)
                        {
                            list = null;
                            break;
                        }

                        list.Add(r);
                    }

                    others = list is null ? null : [.. list];
                }
            }

            return new TailEnd(reader, k, st[m].Block, st[m].Line, readers ?? [], readers is null, exit, others);
        }
    }

    private static Stmt[] Parse(ScriptImage image)
    {
        ReadOnlySpan<byte> bytes = image.Bytes.Span;
        var list = new List<Stmt>();
        foreach (int start in image.SegmentStarts)
        {
            ScriptSegment seg = ScriptSegmenter.Read(bytes, start);
            if (!seg.IsCommand)
            {
                continue;
            }

            // a marker's id digits run past the four name bytes (_ZZ04101(LBL_005);): the arguments are what the first '(' opens
            string name = Encoding.Latin1.GetString(bytes.Slice(seg.NameStart, 4));
            ReadOnlySpan<byte> rest = bytes[seg.ArgsStart..seg.End];
            int open = rest.IndexOf((byte)'(');
            int close = rest.LastIndexOf((byte)')');
            string args = open >= 0 && close > open ? Encoding.Latin1.GetString(rest[(open + 1)..close]) : string.Empty;
            int block = 0, sub = 0;
            if (name[0] == 'Z')
            {
                ScriptSegmenter.TryReadMarkerId(bytes, seg, out block, out sub);
            }

            list.Add(new Stmt(name, args, block, sub, seg.Start));
        }

        return [.. list];
    }

    /// <summary>True when the jump <paramref name="from"/> -> <paramref name="to"/> leaves F110 and every persistent bit alone in every state (a pair of 0xB9460 that returns at once).</summary>
    private static bool Keeps(IGameFlowTables flow, int from, int to)
    {
        foreach (Vars v in States())
        {
            v.SetF(110, (ushort)from);
            FlowMarks.JumpNodes(v, flow.Edges, from, to, v.GetF(1));
            if (v.GetF(110) != from || Globals(v).Any(w => w != 0))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The G words <paramref name="act"/> sets in every state: the part of a mark that does not depend on the playthrough.</summary>
    private static ushort[] Unconditional(Action<Vars> act)
    {
        ushort[]? and = null;
        foreach (Vars v in States())
        {
            act(v);
            ushort[] g = Globals(v);
            and = and is null ? g : [.. and.Zip(g, (x, y) => (ushort)(x & y))];
        }

        return and!;
    }

    /// <summary>
    /// The G words <paramref name="act"/> sets when they are the same in every state (with node <paramref name="reached"/>'s playthrough bit set in all of
    /// them); null when they differ, so the marks depend on the playthrough. 0xB9460's conditions test one bit or word each, or a chain of them whose arms the
    /// two extremes (none set, all set) already tell apart.
    /// </summary>
    private static ushort[]? Invariant(Action<Vars> act, int reached = -1)
    {
        ushort[]? first = null;
        foreach (Vars v in States())
        {
            if (reached is >= 0 and <= 0x23F)
            {
                v.SetF(201 + (reached >> 3), (ushort)(v.GetF(201 + (reached >> 3)) | (1 << (reached & 7))));
            }

            act(v);
            ushort[] g = Globals(v);
            if (first is null)
            {
                first = g;
            }
            else if (!first.AsSpan().SequenceEqual(g))
            {
                return null;
            }
        }

        return first;
    }

    // no state, every per-playthrough bit and flag set with F024 off and on; the last pick 0, 1 or 2 (kept in F1 of the scratch block, read by Keeps)
    private static IEnumerable<Vars> States()
    {
        for (int full = 0; full < 3; full++)
        {
            for (int pick = 0; pick < 3; pick++)
            {
                var v = new Vars();
                if (full > 0)
                {
                    for (int w = 201; w <= 272; w++)
                    {
                        v.SetF(w, 0xFF);
                    }

                    for (int w = 600; w <= 687; w++)
                    {
                        v.SetF(w, 0xFF);
                    }

                    v.SetF(50, 1);
                    v.SetF(54, 1);
                    v.SetF(24, (ushort)(full - 1));
                }

                v.SetF(1, (ushort)pick);
                yield return v;
            }
        }
    }

    private static ushort[] Globals(Vars v)
    {
        ushort[] g = new ushort[SystemBlock.GCount];
        for (int w = 0; w < g.Length; w++)
        {
            g[w] = v.GetG(w);
        }

        return g;
    }

    private static void Or(Vars vars, ushort[] g)
    {
        for (int w = 0; w < g.Length; w++)
        {
            vars.SetG(w, (ushort)(vars.GetG(w) | g[w]));
        }
    }
}

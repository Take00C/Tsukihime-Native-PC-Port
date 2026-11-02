using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tsuki.Cli.Play;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Cli.Commands;

// Package K44 (issue #9): the flowchart cross-check. Every scene is entered through its real path (the explorer's choice lists, replayed from the title on fresh
// profiles in the full engine); then every option of every choice list those playthroughs met that none of them took is played (the same playthrough up to that
// choice, the other option, then the first options), round after round until no option is left. The flowchart state of all these playthroughs, ORed as the
// persistent bits are, is checked node by node: a scene node is done when its own _RDED completed it (scene_done: the gallery's thumbnail and replay), every
// node link is an edge some jump or choice set (edge_seen), and the column filter keeps all 277 columns.
public sealed partial class PlayCommand
{
    private const int SceneNodes = 495;

    private const int FlowchartChartColumns = 277;

    private static int RunFlowchartSweep(HeadlessRig rig, Options o, TextWriter output)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        o.Mode = o.Mode == PlayMode.Click ? PlayMode.Skip : o.Mode;
        Dictionary<int, List<int[]>> chains;
        if (o.Chains is { } path)
        {
            chains = ReadChains(path);
            output.WriteLine($"chains: {chains.Count} scenes from {path}");
        }
        else
        {
            var explorer = new FlowExplorer(rig.Services.Scripts, rig.Source.Eboot.GameFlow);
            ExploreResult found = explorer.Explore(o.ExploreBudget);
            chains = found.First.Keys.ToDictionary(k => k, found.ChainOf);
            output.WriteLine($"explorer: {chains.Count} scenes reachable from the title, {clock.Elapsed.TotalSeconds:F0} s");
        }

        // G0..G499 is what the system data keeps (scene_seen G110.., scene_done G300.., edge_seen G400..): OR over every session
        ushort[] g = new ushort[SystemBlock.GCount];
        var reached = new SortedSet<int>();
        var picks = new List<SweepPick>();
        var check = new RepairCheck(rig);
        int current = -1;
        bool main = true;
        void Merge(GameHost host)
        {
            for (int w = 0; w < g.Length; w++)
            {
                g[w] |= host.Vars.GetG(w);
            }

            // --sessions DIR: the system state each session of the first pass leaves (a build's), or with --repair-check that state of another build repaired
            if (main && current >= 0 && o.Sessions is { } dir)
            {
                string file = Path.Combine(dir, current.ToString(CultureInfo.InvariantCulture) + ".bin");
                if (o.RepairCheck)
                {
                    check.Add(file, host);
                }
                else
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(file, SessionState(host));
                }
            }

            current = -1;
        }

        ChoicePolicy Record(int chain, int playthrough, ChoicePolicy policy)
        {
            current = chain;
            return new RecordingPolicy(policy, (host, index, line) => picks.Add(new SweepPick(
                chain, playthrough, index, host.Script, host.Machine.Cursor, [.. host.ChoiceLines.Select(l => (int)l.Value)], [.. host.ChoiceLines.Select(l => l.Locked)], line)));
        }

        List<int> failed = ReplayChains(rig, o, output, chains, reached, Merge, out int sessions, Record);
        main = false;

        // the options no replay took; a variant's key is not a script, so ReplayChains plays every variant (its "missed" list means nothing here)
        var all = new Dictionary<int, List<int[]>>(chains);
        var planned = new HashSet<(int Script, int Cursor, int Value)>();
        int variants = 0;
        int next = 1_000_000;
        for (int round = 0; round < 12; round++)
        {
            var taken = picks.Select(s => (s.Script, s.Cursor, s.Values[s.Line])).ToHashSet();
            planned.UnionWith(taken);
            var todo = new Dictionary<int, List<int[]>>();
            foreach (SweepPick s in picks)
            {
                for (int line = 0; line < s.Values.Length; line++)
                {
                    if (s.Locked[line] || !planned.Add((s.Script, s.Cursor, s.Values[line])))
                    {
                        continue;
                    }

                    int[] before = [.. picks.Where(x => x.Chain == s.Chain && x.Playthrough == s.Playthrough && x.Index < s.Index).OrderBy(x => x.Index).Select(x => x.Line)];
                    List<int[]> variant = [.. all[s.Chain].Take(s.Playthrough), [.. before, line]];
                    all[next] = variant;
                    todo[next++] = variant;
                }
            }

            if (todo.Count == 0)
            {
                break;
            }

            ReplayChains(rig, o, output, todo, reached, Merge, out int played, Record);
            variants += played;
        }

        var state = new SweepProgress(g);
        IGameFlowTables flow = rig.Source.Eboot.GameFlow;
        Dictionary<int, List<string>> rdedIn = RdedScripts(rig);
        var notDone = new List<string>();
        var notLit = new List<string>();
        int done = 0;
        foreach (FlowNodeRow n in flow.Nodes.Where(n => n.Scene > 0).OrderBy(n => n.Scene))
        {
            bool d = state.SceneDone(n.Scene);
            done += d ? 1 : 0;
            if (!d)
            {
                string where = rdedIn.TryGetValue(n.Scene, out List<string>? s) ? string.Join(",", s) : "no _RDED";
                notDone.Add($"{n.Scene} (column {n.Column}, seen {(state.SceneSeen(n.Scene) ? "yes" : "no")}, link {n.Link} {(state.EdgeSeen(n.Link) ? "seen" : "not seen")}, _RDED in {where})");
            }

            if (!d && !state.EdgeSeen(n.Link))
            {
                notLit.Add(n.Scene.ToString(CultureInfo.InvariantCulture));
            }
        }

        var links = flow.Nodes.Where(n => n.Link > 0).Select(n => n.Link).Distinct().OrderBy(x => x).ToList();
        var unseen = links.Where(id => !state.EdgeSeen(id)).ToList();
        var hidden = Enumerable.Range(498, 33).Where(state.SceneDone).ToList();
        int columns = rig.Source.Eboot.Gallery is { } tables ? FlowchartChart.KeptColumns(tables.ChartColumns, state).Count : -1;

        output.WriteLine(
            $"flowchart sweep: {sessions} sessions, then {variants} more for the {next - 1_000_000} options none had taken; {picks.Select(s => (s.Script, s.Cursor)).Distinct().Count()} choice lists, "
            + $"{planned.Count} options; {reached.Count} scenes reached from the title ({failed.Count} lists missed their scene), {clock.Elapsed.TotalSeconds:F0} s");
        output.WriteLine($"  scene nodes done (scene_done, thumbnail and replay in the gallery): {done} of {SceneNodes}");
        output.WriteLine($"  scene nodes not done ({notDone.Count}): {string.Join("; ", notDone)}");
        output.WriteLine($"  scene nodes not shown at all (neither done nor their link seen) ({notLit.Count}): {string.Join(" ", notLit)}");
        output.WriteLine($"  node links seen (edge_seen): {links.Count - unseen.Count} of {links.Count}; not seen: {string.Join(" ", unseen)}");
        output.WriteLine($"  hidden nodes 498..530 done: {hidden.Count} of 33 (missing: {string.Join(" ", Enumerable.Range(498, 33).Except(hidden))})");
        output.WriteLine($"  columns the gallery's chart keeps: {columns} of {FlowchartChartColumns}");
        if (o.RepairCheck)
        {
            check.Print(output);
        }
        return notDone.Count == 0 && unseen.Count == 0 && columns == FlowchartChartColumns ? ExitCode.Ok : ExitCode.Failed;
    }

    /// <summary>Reads the <c>--explore-dump</c> format: index, name, the choice lists of the chain separated by <c> | </c>.</summary>
    private static Dictionary<int, List<int[]>> ReadChains(string path)
    {
        var chains = new Dictionary<int, List<int[]>>();
        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 3 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int k))
            {
                continue;
            }

            chains[k] = [.. parts[2].Split(" | ").Select(r => r.Length == 0 ? [] : r.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray())];
        }

        return chains;
    }

    /// <summary>The scripts that hold <c>_RDED(n)</c>, by node.</summary>
    private static Dictionary<int, List<string>> RdedScripts(HeadlessRig rig)
    {
        var map = new Dictionary<int, List<string>>();
        for (int k = 0; k < rig.Source.ScriptCount; k++)
        {
            string text = Encoding.Latin1.GetString(rig.Services.Scripts.Script(k).Bytes.Span);
            foreach (Match m in Regex.Matches(text, @"_RDED\((\d+)\)"))
            {
                int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (!map.TryGetValue(n, out List<string>? list))
                {
                    map[n] = list = [];
                }

                list.Add(rig.Source.Scripts.Names[k]);
            }
        }

        return map;
    }

    /// <summary>A session's persistent state: G0..G499, readmax, choice_taken.</summary>
    private static byte[] SessionState(GameHost host)
    {
        SystemBlock sys = host.Profile.System;
        byte[] b = new byte[(2 * SystemBlock.GCount) + SystemBlock.ReadmaxCount + SystemBlock.ChoiceTakenCount];
        for (int w = 0; w < SystemBlock.GCount; w++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2 * w), host.Vars.GetG(w));
        }

        for (int i = 0; i < SystemBlock.ReadmaxCount; i++)
        {
            b[(2 * SystemBlock.GCount) + i] = sys.GetReadmax(i);
        }

        for (int i = 0; i < SystemBlock.ChoiceTakenCount; i++)
        {
            b[(2 * SystemBlock.GCount) + SystemBlock.ReadmaxCount + i] = sys.GetChoiceTaken(i);
        }

        return b;
    }

    /// <summary>
    /// <c>--repair-check</c>: each session's state as another build (v0.1.4) left it, repaired by <see cref="FlowRepair"/>, against the state this build's same
    /// session leaves. A repaired bit this build does not set would be a mark the PS4 never made.
    /// </summary>
    private sealed class RepairCheck(HeadlessRig rig)
    {
        private readonly List<string> _wrong = [];
        private int _sessions;
        private int _missingFile;
        private int _needed;
        private int _added;
        private int _right;
        private int _scenesRepaired;
        private int _scenesWanted;

        public void Add(string file, GameHost host)
        {
            if (!File.Exists(file))
            {
                _missingFile++;
                return;
            }

            _sessions++;
            byte[] b = File.ReadAllBytes(file);
            var vars = new Vars();
            for (int w = 0; w < SystemBlock.GCount; w++)
            {
                vars.SetG(w, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2 * w)));
            }

            var block = new SystemBlock();
            for (int i = 0; i < SystemBlock.ReadmaxCount; i++)
            {
                block.SetReadmax(i, b[(2 * SystemBlock.GCount) + i]);
            }

            for (int i = 0; i < SystemBlock.ChoiceTakenCount; i++)
            {
                block.SetChoiceTaken(i, b[(2 * SystemBlock.GCount) + SystemBlock.ReadmaxCount + i]);
            }

            ushort[] old = [.. Enumerable.Range(0, SystemBlock.GCount).Select(vars.GetG)];
            if (FlowRepair.Needed(vars))
            {
                _needed++;
                FlowRepair.Repair(rig.Source, new Engine.Save.SystemData(block), vars);
            }

            for (int w = 0; w < SystemBlock.GCount; w++)
            {
                int now = host.Vars.GetG(w);
                int added = vars.GetG(w) & ~old[w];
                _added += System.Numerics.BitOperations.PopCount((uint)added);
                _right += System.Numerics.BitOperations.PopCount((uint)(added & now));
                for (int bit = 0; bit < 16; bit++)
                {
                    if ((added >> bit & 1) != 0 && (now >> bit & 1) == 0 && _wrong.Count < 40)
                    {
                        _wrong.Add($"{Path.GetFileNameWithoutExtension(file)}:G{w}.{bit}");
                    }
                }
            }

            for (int n = 1; n <= SceneNodes; n++)
            {
                bool want = Engine.Save.SystemData.SceneDone(host.Vars, n);
                bool had = (old[300 + (n >> 4)] >> (n & 15) & 1) != 0;
                _scenesWanted += want && !had ? 1 : 0;
                _scenesRepaired += want && !had && Engine.Save.SystemData.SceneDone(vars, n) ? 1 : 0;
            }
        }

        public void Print(TextWriter output)
        {
            output.WriteLine(
                $"  repair check: {_sessions} sessions of the other build ({_missingFile} not dumped), {_needed} needed the repair; {_added} bits added, {_right} of them set by this build too, "
                + $"{_added - _right} not ({string.Join(" ", _wrong)}); scene nodes the other build left undone that this build completes: {_scenesWanted}, repaired {_scenesRepaired}");
        }
    }

    /// <summary>One decided choice of a replay: the chain, its playthrough and the index among that playthrough's choices, the list (script, cursor), its lines and the pick.</summary>
    private sealed record SweepPick(int Chain, int Playthrough, int Index, int Script, int Cursor, int[] Values, bool[] Locked, int Line);

    /// <summary>A policy that reports every pick of the one it wraps.</summary>
    private sealed class RecordingPolicy(ChoicePolicy inner, Action<GameHost, int, int> log) : ChoicePolicy
    {
        private int _index;

        public override int Pick(GameHost host, int point)
        {
            int pick = inner.Pick(host, point);
            log(host, _index++, pick);
            return pick;
        }
    }

    /// <summary>The persistent flowchart bits of the sweep (G0..G499); the per-playthrough bits are not part of the gallery's chart.</summary>
    private sealed class SweepProgress(ushort[] g) : IUiProgress
    {
        private bool Bit(int word, int bit) => (g[word + (bit >> 4)] >> (bit & 15) & 1) != 0;

        public bool SceneSeen(int n) => n is >= 0 and <= 0x23F && Bit(110, n);

        public bool SceneDone(int n) => n is >= 0 and <= 0x23F && Bit(300, n);

        public bool EdgeSeen(int id) => id is >= 564 and <= 1267 && Bit(400, id - 564);

        public bool MovieSeen(int bit) => false;

        public bool PictureSeen(int seenIndex) => false;

        public bool BgmHeard(int track) => false;

        public bool RouteScene(int n) => false;

        public bool RouteEdge(int id) => false;
    }
}

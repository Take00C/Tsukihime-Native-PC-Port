using System.Diagnostics;
using System.Globalization;
using System.Text;
using Tsuki.Cli.Play;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Flow;
using Tsuki.Engine.Game;

namespace Tsuki.Cli.Commands;

/// <summary>
/// <c>Tsuki.Cli play</c>: the headless game runner (package K3). It boots a fresh profile, starts New Game as the PS4 title does (scene 260, the
/// prologue movie, then START_SCRIPT once bit 485 is set), advances the text like a player (clicks, skip or auto), picks the choices by a policy
/// and writes a trace. <c>--coverage</c> plays again and again with the same profile, as the game does (routes unlock others): random-ish playthroughs,
/// then the replay of the choice lists a logic-only explorer found for every scene a playthrough can reach, then flowchart jumps, then the special entries;
/// it reports the scenes reached (and how), the unknown commands and the exceptions. <c>--explore</c> runs the explorer and its replays alone.
/// </summary>
public sealed class PlayCommand : ICliCommand
{
    private sealed class Options
    {
        public string? Game;
        public Language Language = Language.En;
        public string Policy = "first";
        public PlayMode Mode = PlayMode.Click;
        public long MaxFrames = 20_000_000;
        public int MaxGames = 1;
        public bool UntilFirstChoice;
        public string? TracePath;
        public bool TraceVars;
        public int? TextSpeed;
        public int Seed = 1;
        public int? Scene;
        public bool Coverage;
        public int MaxRuns = 200;
        public int Patience = 6;
        public bool Verbose;
        public bool Strict;
        public bool NoAim;
        public bool NoJump;
        public bool Explore;
        public string? ExploreDump;
        public bool ListScenes;
        public bool Aim;
        public long ExploreBudget = 1_000_000;
        public string? Target;
        public List<(int Index, int Value)> Globals = [];
        public int MaxRounds = 12;
        public int MaxAimRuns = 3000;
    }

    /// <inheritdoc />
    public string Name => "play";

    /// <inheritdoc />
    public string Summary =>
        "play the game headless: --policy first|last|random[:seed]|list:i,j  --mode click|skip|auto  --until-first-choice  --games N  --trace FILE  --coverage [--no-aim] [--no-jump] [--explore-budget N]  --explore  (see docs/status/knife/K3.md)";

    /// <inheritdoc />
    public int Run(string[] args, TextWriter output, TextWriter error)
    {
        Options? o = Parse(args, error);
        if (o is null)
        {
            return ExitCode.Usage;
        }

        string? folder = GameFolderLocator.Resolve(o.Game);
        if (folder is null || !File.Exists(Path.Combine(folder, "eboot.bin")))
        {
            error.WriteLine("play: no game folder; pass --game <dir> or set TSUKI_GAME_DIR");
            return ExitCode.Usage;
        }

        using HeadlessRig rig = HeadlessRig.Open(folder);
        if (!rig.Source.Build.IsAnalysedBuild)
        {
            error.WriteLine($"play: {folder} is not the analysed build (eboot.bin SHA-256 {rig.Source.Build.EbootSha256}); run `verify` first");
            return ExitCode.Failed;
        }

        if (o.ListScenes)
        {
            IGameFlowTables flowTables = rig.Source.Eboot.GameFlow;
            SceneGraph jumps = SceneGraph.Build(rig.Services.Scripts, flowTables);
            for (int k = 0; k < rig.Source.ScriptCount; k++)
            {
                output.WriteLine($"{k}	{rig.Source.Scripts.Names[k]}	scene {flowTables.SceneId(k)}	node {flowTables.NodeOfScript(k)}	jumps {string.Join(",", jumps.Successors(k))}");
            }

            return ExitCode.Ok;
        }

        using StreamWriter? trace = o.TracePath is null ? null : new StreamWriter(o.TracePath, append: false, new UTF8Encoding(false)) { AutoFlush = false };
        if (o.Explore)
        {
            var reached = new SortedSet<int>();
            ExploreAndReplay(rig, o, output, reached);
            return ExitCode.Ok;
        }

        return o.Coverage ? RunCoverage(rig, o, output, trace) : RunSession(rig, o, output, trace);
    }

    private static Options? Parse(string[] args, TextWriter error)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--game":
                    o.Game = Next();
                    break;
                case "--lang":
                    o.Language = (Next() ?? string.Empty).ToLowerInvariant() switch { "ja" => Language.Ja, "en" => Language.En, "zc" => Language.Zc, "zt" => Language.Zt, _ => Language.En };
                    break;
                case "--policy":
                    o.Policy = Next() ?? "first";
                    break;
                case "--mode":
                    o.Mode = (Next() ?? string.Empty).ToLowerInvariant() switch { "skip" => PlayMode.Skip, "auto" => PlayMode.Auto, _ => PlayMode.Click };
                    break;
                case "--max-frames":
                    o.MaxFrames = long.Parse(Next() ?? "0", CultureInfo.InvariantCulture);
                    break;
                case "--games":
                    o.MaxGames = int.Parse(Next() ?? "1", CultureInfo.InvariantCulture);
                    break;
                case "--until-first-choice":
                    o.UntilFirstChoice = true;
                    break;
                case "--trace":
                    o.TracePath = Next();
                    break;
                case "--trace-vars":
                    o.TraceVars = true;
                    break;
                case "--text-speed":
                    o.TextSpeed = int.Parse(Next() ?? "35", CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    o.Seed = int.Parse(Next() ?? "1", CultureInfo.InvariantCulture);
                    break;
                case "--scene":
                    o.Scene = int.Parse(Next() ?? "0", CultureInfo.InvariantCulture);
                    break;
                case "--coverage":
                    o.Coverage = true;
                    break;
                case "--max-runs":
                    o.MaxRuns = int.Parse(Next() ?? "200", CultureInfo.InvariantCulture);
                    break;
                case "--patience":
                    o.Patience = int.Parse(Next() ?? "6", CultureInfo.InvariantCulture);
                    break;
                case "-v" or "--verbose":
                    o.Verbose = true;
                    break;
                case "--target":
                    o.Target = Next();
                    break;
                case "--g":
                {
                    string[] kv = (Next() ?? "0=0").Split('=');
                    o.Globals.Add((int.Parse(kv[0], CultureInfo.InvariantCulture), int.Parse(kv[1], CultureInfo.InvariantCulture)));
                    break;
                }

                case "--max-rounds":
                    o.MaxRounds = int.Parse(Next() ?? "12", CultureInfo.InvariantCulture);
                    break;
                case "--max-aim-runs":
                    o.MaxAimRuns = int.Parse(Next() ?? "3000", CultureInfo.InvariantCulture);
                    break;
                case "--explore":
                    o.Explore = true;
                    break;
                case "--explore-dump":
                    o.ExploreDump = Next();
                    break;
                case "--list-scenes":
                    o.ListScenes = true;
                    break;
                case "--aim":
                    o.Aim = true;
                    break;
                case "--explore-budget":
                    o.ExploreBudget = long.Parse(Next() ?? "3000000", CultureInfo.InvariantCulture);
                    break;
                case "--no-jump":
                    o.NoJump = true;
                    break;
                case "--no-aim":
                    o.NoAim = true;
                    break;
                case "--strict":
                    o.Strict = true;
                    break;
                default:
                    error.WriteLine($"play: unexpected argument '{a}'");
                    return null;
            }
        }

        return o;
    }

    private static GameHost CreateHost(HeadlessRig rig, Options o, GameProfile? profile)
    {
        var options = new GameOptions
        {
            Language = o.Language,
            VirtualMovies = true,
            RecordUnknownCommands = !o.Strict,
            StrictCommands = o.Strict,
            RandomSeed = (uint)o.Seed,
        };
        GameHost host = new(rig.Services, options, profile);
        if (o.TextSpeed is { } speed)
        {
            host.Config.UnseenTextSpeed = (ushort)speed;
            host.Config.SeenTextSpeed = (ushort)speed;
        }

        if (o.Mode == PlayMode.Skip)
        {
            host.Config.FastForwardReadOnly = 0;
        }

        foreach ((int index, int value) in o.Globals)
        {
            host.Vars.SetG(index, (ushort)value);
        }

        return host;
    }

    // -- one session ------------------------------------------------------------------------------------------------------------

    private static int RunSession(HeadlessRig rig, Options o, TextWriter output, StreamWriter? trace)
    {
        GameHost host = CreateHost(rig, o, null);
        ChoicePolicy policy = ChoicePolicy.Parse(o.Policy);
        if (o.Target is { } targetName)
        {
            int k = Enumerable.Range(0, rig.Source.ScriptCount).First(i => rig.Source.Scripts.Names[i] == targetName);
            SceneGraph graph = SceneGraph.Build(rig.Services.Scripts, rig.Source.Eboot.GameFlow);
            List<int>? path = graph.Path(0, k);
            output.WriteLine($"target {targetName}: path {(path is null ? "none" : string.Join(" > ", path.Select(i => rig.Source.Scripts.Names[i])))}");
            policy = new TargetPolicy(graph, path ?? [], new RandomPolicy(o.Seed));
        }

        var player = new AutoPlayer(policy, o.Mode);
        var report = new Report();
        Attach(host, rig, o, trace, player, report);
        trace?.WriteLine($"# boot: fresh profile, language {o.Language}, system data created, title screen (G002 = {host.Vars.GetG(2)})");
        var clock = Stopwatch.StartNew();
        bool stop = false;
        for (int game = 0; game < o.MaxGames && !stop; game++)
        {
            int sceneId = o.Scene ?? host.NewGameSceneId();
            trace?.WriteLine($"# title: NEW GAME -> scene {sceneId}");
            output.WriteLine($"game {game + 1}: New Game -> scene {sceneId} ({SceneName(rig, host, sceneId)})");
            host.NewGame(sceneId);
            long startFrames = host.Frame;
            try
            {
                stop = Play(host, player, o, report, trace, ref startFrames);
            }
            catch (Exception e)
            {
                report.Exceptions.Add($"{e.GetType().Name}: {e.Message} [script {host.Script}, offset 0x{host.Machine.Cursor:x}]");
                output.WriteLine($"EXCEPTION {e}");
                stop = true;
            }

            output.WriteLine($"  ended: {host.EndReason}, frames {host.Frame}, statements {host.DispatchCount}, choices {player.ChoicesTaken}, scenes reached {host.ScriptsReached.Count}");
            if (o.UntilFirstChoice && report.FirstChoice is not null)
            {
                stop = true;
            }
        }

        trace?.Flush();
        PrintSummary(rig, host, report, output, clock.Elapsed);
        return report.Exceptions.Count == 0 && host.UnknownCommands.Count == 0 ? ExitCode.Ok : ExitCode.Failed;
    }

    private sealed class Report
    {
        public string? FirstChoice;
        public readonly List<string> Exceptions = [];
        public long Messages;
        public long Movies;
    }

    private static string SceneName(HeadlessRig rig, GameHost host, int sceneId)
    {
        int k = host.Profile is null ? -1 : rig.Source.Eboot.GameFlow.ScriptOfSceneId(sceneId);
        return k >= 0 ? rig.Source.Scripts.Names[k] : "?";
    }

    private static void Attach(GameHost host, HeadlessRig rig, Options o, StreamWriter? trace, AutoPlayer player, Report report)
    {
        host.SceneEntered += (k, id) => trace?.WriteLine($"F{host.Frame:D8} SCENE {id} script {k} {rig.Source.Scripts.Names[k]}");
        host.MessageStarted += (script, offset, read) =>
        {
            report.Messages++;
            if (trace is not null)
            {
                string text = host.CurrentMessage is { } m ? Encoding.UTF8.GetString(m.Text).Replace("\n", "\\n", StringComparison.Ordinal) : string.Empty;
                trace.WriteLine($"F{host.Frame:D8} MSG script {script} @0x{offset:x} {(read ? "read" : "new")} F823={host.F(823)} F799={host.F(799)} end={host.CurrentMessage?.End} {Trunc(text)}");
            }
        };
        host.WaitStarted += (kind, ms) => trace?.WriteLine($"F{host.Frame:D8} WAIT {kind} {ms}");
        host.MovieStarted += id =>
        {
            report.Movies++;
            trace?.WriteLine($"F{host.Frame:D8} MOVIE start {id}");
        };
        host.MovieEnded += id => trace?.WriteLine($"F{host.Frame:D8} MOVIE end {id}");
        host.ChoiceDecided += (kind, point, value, cursor) => trace?.WriteLine($"F{host.Frame:D8} CHOICE {kind} point {point} -> line {cursor} value {value}");
        player.Chose += (point, pick, value, count) =>
        {
            trace?.WriteLine($"F{host.Frame:D8} PICK script {host.Script} {rig.Source.Scripts.Names[host.Script]} point {point}: line {pick} of [{string.Join(", ", host.ChoiceLines.Select(l => $"{l.Value}{(l.Locked ? "L" : string.Empty)}"))}] G003={host.Vars.GetG(3)}");
            report.FirstChoice ??= $"script {host.Script} ({rig.Source.Scripts.Names[host.Script]}), choice point {point}, {count} lines, picked {pick}";
        };
        if (trace is not null)
        {
            host.SetObserver(step =>
            {
                bool interesting = step.Action is FlowAction.Host or FlowAction.Marker or FlowAction.Jump || o.TraceVars;
                if (!interesting || (step.Action == FlowAction.Marker && step.DisplayName != "ZM"))
                {
                    return;
                }

                string raw = o.TraceVars && step.DisplayName == "IF__" ? " [" + Encoding.Latin1.GetString(host.Machine.Image.Bytes.Span.Slice(step.Offset, Math.Min(48, host.Machine.Image.Bytes.Length - step.Offset))) + $" G3={host.Vars.GetG(3)}]" : string.Empty;
                trace.WriteLine($"F{host.Frame:D8} k{step.Script} @0x{step.Offset:x} {step.DisplayName} {step.Action} -> {step.Result}{(step.Goto is { } g ? " " + Encoding.Latin1.GetString(g) : string.Empty)}{raw}");
            });
        }
    }

    private static string Trunc(string s) => s.Length <= 60 ? s : s[..60] + "...";

    /// <summary>Plays the running story to its end (or the limits). Returns true when the run should stop altogether.</summary>
    private static bool Play(GameHost host, AutoPlayer player, Options o, Report report, StreamWriter? trace, ref long startFrames)
    {
        long limit = host.Frame + o.MaxFrames;
        while (host.Phase == GamePhase.Running)
        {
            InputFrame input = player.Next(host);
            host.Step(input);
            if (o.UntilFirstChoice && report.FirstChoice is not null)
            {
                return true;
            }

            if (host.Frame >= limit)
            {
                trace?.WriteLine($"# frame limit {o.MaxFrames} reached in script {host.Script}");
                return true;
            }
        }

        trace?.WriteLine($"F{host.Frame:D8} END {host.EndReason}");
        return false;
    }

    private static void PrintSummary(HeadlessRig rig, GameHost host, Report report, TextWriter output, TimeSpan elapsed)
    {
        output.WriteLine($"summary: {host.Frame} frames, {host.DispatchCount} statements dispatched to the host, {report.Messages} messages, {report.Movies} movies, {elapsed.TotalSeconds:F1} s");
        output.WriteLine($"  scenes reached: {host.ScriptsReached.Count} of {rig.Source.ScriptCount}");
        if (report.FirstChoice is not null)
        {
            output.WriteLine($"  first choice: {report.FirstChoice}");
        }

        output.WriteLine($"  unknown commands: {(host.UnknownCommands.Count == 0 ? "none" : string.Join(", ", host.UnknownCommands.Select(kv => $"{kv.Key} x{kv.Value}")))}");
        output.WriteLine($"  exceptions: {(report.Exceptions.Count == 0 ? "none" : string.Empty)}");
        foreach (string e in report.Exceptions)
        {
            output.WriteLine($"    {e}");
        }
    }

    // -- coverage ---------------------------------------------------------------------------------------------------------------

    private sealed class Cov(HeadlessRig rig, Options o, GameHost host, AutoPlayer player, Report report, TextWriter output)
    {
        public int Run;
        public readonly List<string> Lines = [];

        /// <summary>Plays one playthrough with the current policy and returns the number of new scenes.</summary>
        public int Once(string what, int? node = null) => Once(what, () => StartFrom(node), null);

        private bool StartFrom(int? node)
        {
            if (node is { } n)
            {
                return host.JumpToNode(n);
            }

            host.NewGame(host.NewGameSceneId());
            return true;
        }

        /// <summary>Plays one playthrough started by <paramref name="start"/> and returns the number of new scenes.</summary>
        public int Once(string what, Func<bool> start, int? sceneIdForLog)
        {
            Run++;
            int before = host.ScriptsReached.Count;
            if (!start())
            {
                output.WriteLine($"run {Run,3} {what,-22}: nothing to start");
                return 0;
            }

            int sceneId = sceneIdForLog ?? host.SceneId;
            long startFrames = host.Frame;
            long statements = host.DispatchCount;
            int choices = player.ChoicesTaken;
            string failure = string.Empty;
            try
            {
                Play(host, player, o, report, null, ref startFrames);
            }
            catch (Exception e)
            {
                failure = $"{e.GetType().Name}: {e.Message} [script {host.Script} {rig.Source.Scripts.Names[host.Script]}, offset 0x{host.Machine.Cursor:x}]";
                report.Exceptions.Add(failure);
            }

            int gained = host.ScriptsReached.Count - before;
            output.WriteLine(
                $"run {Run,3} {what,-22}: scene {sceneId,4} -> {host.EndReason,-14} {host.Frame - startFrames,9} frames {host.DispatchCount - statements,9} stmts {player.ChoicesTaken - choices,3} choices  +{gained,3} scenes = {host.ScriptsReached.Count,3}/{rig.Source.ScriptCount} {failure}");
            if (host.Phase == GamePhase.Running)
            {
                output.WriteLine("  (frame limit)");
            }

            return gained;
        }
    }

    /// <summary>A policy that can be replaced between playthroughs.</summary>
    private sealed class ChoicePolicyHolder : ChoicePolicy
    {
        public ChoicePolicy Current { get; set; } = new FirstPolicy();

        public override int Pick(GameHost host, int point) => Current.Pick(host, point);
    }

    private static int RunCoverage(HeadlessRig rig, Options o, TextWriter output, StreamWriter? trace)
    {
        o.Mode = o.Mode == PlayMode.Click ? PlayMode.Skip : o.Mode;
        GameHost host = CreateHost(rig, o, null);
        var coverage = new CoveragePolicy(o.Seed);
        var holder = new ChoicePolicyHolder { Current = coverage };
        var player = new AutoPlayer(holder, o.Mode);
        var report = new Report();
        Attach(host, rig, o, trace, player, report);
        var clock = Stopwatch.StartNew();
        var cov = new Cov(rig, o, host, player, report, output);
        SceneGraph graph = SceneGraph.Build(rig.Services.Scripts, rig.Source.Eboot.GameFlow);
        HashSet<int> staticReach = graph.Reachable(0);
        staticReach.Add(2);
        output.WriteLine($"static graph: {staticReach.Count} of {rig.Source.ScriptCount} scripts reachable from START_SCRIPT (plus the prologue 00_01 by New Game)");

        // phase 1: the prologue, then random-ish coverage until nothing new turns up
        int stale = 0;
        while (cov.Run < o.MaxRuns && stale < o.Patience)
        {
            holder.Current = coverage;
            stale = cov.Once("coverage") == 0 ? stale + 1 : 0;
        }

        // phase 2a: the logic-only explorer finds a choice list for every scene a playthrough from the title can reach; each is replayed in the full engine
        if (!o.NoAim)
        {
            ExploreAndReplay(rig, o, output, host.ScriptsReached);
        }

        // phase 2b (only with --aim): aim at every scene the jumps can reach and the runs have not, shallowest first. A round tries each of them; the ones that fail
        // are tried again in the next round when the round gained something (a finished route unlocks the next: G003 after Arcueid's route)
        int round = 0;
        int aimRuns = 0;
        var failed = new HashSet<int>();
        while (o.Aim && !o.NoAim && round < o.MaxRounds && aimRuns < o.MaxAimRuns)
        {
            round++;
            var targets = Enumerable.Range(0, rig.Source.ScriptCount)
                .Where(k => !host.ScriptsReached.Contains(k) && staticReach.Contains(k) && !failed.Contains(k))
                .Select(k => (K: k, Path: graph.Path(0, k)))
                .Where(t => t.Path is not null)
                .OrderBy(t => t.Path!.Count)
                .ToList();
            output.WriteLine($"-- aim round {round}: {targets.Count} scenes to aim at");
            if (targets.Count == 0)
            {
                break;
            }

            bool gained = false;
            foreach ((int k, List<int>? path) in targets)
            {
                if (host.ScriptsReached.Contains(k) || aimRuns >= o.MaxAimRuns)
                {
                    continue;
                }

                aimRuns++;
                holder.Current = new TargetPolicy(graph, path!, coverage);
                if (cov.Once($"aim {rig.Source.Scripts.Names[k]}") > 0)
                {
                    gained = true;
                }
                else
                {
                    failed.Add(k);
                }
            }

            if (!gained)
            {
                break;
            }

            failed.Clear();
        }

        // phase 3: the flowchart jump (gallery flowchart, system menu) starts a scene with the story state its jump-entry block sets up. It reaches the
        // scenes behind flags that a playthrough from the title only sets deep inside another route; they are reported apart.
        HashSet<int> fromTitle = [.. host.ScriptsReached];
        IGameFlowTables flow = rig.Source.Eboot.GameFlow;
        int jumpRounds = 0;
        var jumpFailed = new HashSet<int>();
        while (!o.NoJump && jumpRounds < o.MaxRounds)
        {
            jumpRounds++;
            var targets = Enumerable.Range(0, rig.Source.ScriptCount)
                .Where(k => !host.ScriptsReached.Contains(k) && flow.NodeOfScript(k) >= 1 && !jumpFailed.Contains(k))
                .ToList();
            output.WriteLine($"-- flowchart jump round {jumpRounds}: {targets.Count} scenes with a node to start from");
            if (targets.Count == 0)
            {
                break;
            }

            bool gained = false;
            foreach (int k in targets)
            {
                if (host.ScriptsReached.Contains(k))
                {
                    continue;
                }

                holder.Current = coverage;
                if (cov.Once($"jump {rig.Source.Scripts.Names[k]}", flow.NodeOfScript(k)) > 0)
                {
                    gained = true;
                }
                else
                {
                    jumpFailed.Add(k);
                }
            }

            if (!gained)
            {
                break;
            }

            jumpFailed.Clear();
        }

        // phase 3b: the Q&A endings (nodes 533..556) each end at their own _RDED; _IFUR in QA_25 passes once all 24 are done. Replay the missing ones.
        for (int attempt = 0; attempt < 12 && !o.NoJump; attempt++)
        {
            var open = Enumerable.Range(533, 24).Where(n => !host.IsSceneDone(n)).ToList();
            if (open.Count == 0)
            {
                break;
            }

            foreach (int n in open)
            {
                // the gallery replays QA_n through ARCHIVE (scene id 1000 + n - 533); the replayed scene ends at its _RDED
                holder.Current = coverage;
                cov.Once($"ending {n}", () => host.StartReplay(1000 + n - 533), 3000);
            }
        }

        // phase 4: scenes that only a special entry reaches: the scripted prologue 00_02, the staff roll, the debug menus, the gallery replay (ARCHIVE)
        HashSet<int> beforeSpecial = [.. host.ScriptsReached];
        if (!o.NoJump)
        {
            for (int k = 0; k < rig.Source.ScriptCount; k++)
            {
                string name = rig.Source.Scripts.Names[k];
                if (!host.ScriptsReached.Contains(k) && (k == 3 || k == 45 || k == 519 || name is "STAFFROLL" or "TEST_SCRIPT" || name.StartsWith("JUMP", StringComparison.Ordinal)))
                {
                    int id = flow.SceneId(k);
                    holder.Current = coverage;
                    cov.Once($"entry {name}", () =>
                    {
                        host.NewGame(id);
                        return true;
                    }, id);
                }
            }

            foreach (int replay in new[] { 1000, 1018, 2000, 750 })
            {
                holder.Current = coverage;
                cov.Once($"replay {replay}", () => host.StartReplay(replay), 3000);
            }
        }

        trace?.Flush();
        PrintSummary(rig, host, report, output, clock.Elapsed);
        output.WriteLine($"  reached through a special entry (prologue 00_02, staff roll, debug menus, gallery replay): {host.ScriptsReached.Count - beforeSpecial.Count}");
        output.WriteLine($"  Q&A ending nodes completed: {Enumerable.Range(533, 24).Count(host.IsSceneDone)} of 24");
        output.WriteLine($"  reached by playing from the title: {fromTitle.Count}; reached only through the Q&A replays, flowchart jumps and special entries: {host.ScriptsReached.Count - fromTitle.Count}");
        PrintMissing(rig, host, output, staticReach, graph);
        return report.Exceptions.Count == 0 && host.UnknownCommands.Count == 0 ? ExitCode.Ok : ExitCode.Failed;
    }

    /// <summary>
    /// Explores the story with <see cref="FlowExplorer"/>, then replays, on fresh profiles and in the full engine, the choice lists that reach each scene
    /// the explorer found (fewest choices first, skipping scenes an earlier replay passed through). <paramref name="reached"/> receives the scenes the
    /// engine really reached.
    /// </summary>
    private static void ExploreAndReplay(HeadlessRig rig, Options o, TextWriter output, ISet<int> reached)
    {
        var clock = Stopwatch.StartNew();
        var explorer = new FlowExplorer(rig.Services.Scripts, rig.Source.Eboot.GameFlow);
        ExploreResult found = explorer.Explore(o.ExploreBudget);
        output.WriteLine(
            $"explorer: {explorer.RelevantWords.Count} deciding variable words, {found.States} choice states, {found.Expansions} expansions, {found.Profiles} profiles, "
            + $"{found.First.Count} of {rig.Source.ScriptCount} scripts reachable{(found.BudgetHit ? " (budget hit)" : string.Empty)}, {clock.Elapsed.TotalSeconds:F1} s");
        if (o.ExploreDump is { } dumpPath)
        {
            // package K15: the choice lists that reach every scene (one playthrough per line, the lists of the chain separated by ' | '), to plan route sweeps
            using var dump = new StreamWriter(dumpPath, append: false, new UTF8Encoding(false));
            foreach (int k in found.First.Keys.OrderBy(x => x))
            {
                dump.WriteLine($"{k}	{rig.Source.Scripts.Names[k]}	{string.Join(" | ", found.ChainOf(k).Select(r => string.Join(",", r)))}");
            }

            using var ends = new StreamWriter(dumpPath + ".ends", append: false, new UTF8Encoding(false));
            foreach (KeyValuePair<int, ProfileRun> end in found.Ends.OrderBy(e => e.Key))
            {
                ends.WriteLine($"{end.Key}	{rig.Source.Scripts.Names[end.Key]}	{string.Join(" | ", ExploreResult.ChainOf(end.Value).Select(r => string.Join(",", r)))}");
            }

            return;
        }

        var order = found.First.Keys.OrderBy(k => found.ChainOf(k).Sum(r => r.Length)).ThenBy(k => k).ToList();
        var done = new HashSet<int>();
        var mismatches = new List<int>();
        int sessions = 0;
        foreach (int k in order)
        {
            if (done.Contains(k))
            {
                continue;
            }

            List<int[]> chain = found.ChainOf(k);
            GameHost host = CreateHost(rig, o, null);
            var holder = new ChoicePolicyHolder();
            var player = new AutoPlayer(holder, o.Mode == PlayMode.Click ? PlayMode.Skip : o.Mode);
            var report = new Report();
            host.NewGame(host.NewGameSceneId());                           // the prologue movie, 00_01
            holder.Current = new FirstPolicy();
            long fs = host.Frame;
            Play(host, player, o, report, null, ref fs);
            foreach (int[] picks in chain)
            {
                holder.Current = new ListPolicy(picks);
                host.NewGame(host.NewGameSceneId());
                try
                {
                    Play(host, player, o, report, null, ref fs);
                }
                catch (Exception e)
                {
                    report.Exceptions.Add(e.Message);
                    output.WriteLine($"  replay exception: {e.GetType().Name}: {e.Message} [script {host.Script}]");
                }
            }

            sessions++;
            done.UnionWith(host.ScriptsReached);
            foreach (int r in host.ScriptsReached)
            {
                reached.Add(r);
            }

            if (!host.ScriptsReached.Contains(k))
            {
                mismatches.Add(k);
            }
        }

        output.WriteLine($"replayed {sessions} choice lists in the full engine: {reached.Count} scenes reached from the title; the explorer's list failed for {mismatches.Count}: {string.Join(" ", mismatches.Select(k => rig.Source.Scripts.Names[k]))}");
        var unreachable = Enumerable.Range(0, rig.Source.ScriptCount).Where(k => !found.First.ContainsKey(k) && k != 2).Select(k => $"{k}:{rig.Source.Scripts.Names[k]}").ToList();
        output.WriteLine($"  not reachable from the title by any playthrough the explorer could build ({unreachable.Count}): {string.Join(" ", unreachable)}");
    }

    private static void PrintMissing(HeadlessRig rig, GameHost host, TextWriter output, HashSet<int> staticReach, SceneGraph graph)
    {
        var notReachable = new List<string>();
        var gated = new List<string>();
        for (int k = 0; k < rig.Source.ScriptCount; k++)
        {
            if (host.ScriptsReached.Contains(k))
            {
                continue;
            }

            string item = $"{k}:{rig.Source.Scripts.Names[k]}";
            (staticReach.Contains(k) ? gated : notReachable).Add(item);
        }

        output.WriteLine($"  not reached and not reachable from the title by any jump ({notReachable.Count}): {string.Join(" ", notReachable)}");
        output.WriteLine($"  not reached although a jump leads there ({gated.Count}): {string.Join(" ", gated)}");
    }
}

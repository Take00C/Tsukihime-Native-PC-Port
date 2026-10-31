// Spike S4 giant picture (ROADMAP M0 scope 8; docs/status/spikes/S4.md): the largest picture
// through a C# MZX0 + MZP prototype, parallel tiles, sliced upload. Throwaway: deleted after M2.
//
//   S4.BigPicture decode <arc> <index> [--reps N]      read / MZX0 / convert / workers / memory / SHA-1
//   S4.BigPicture verify                               SHA-1 of every oracle picture vs tsuki.image
//   S4.BigPicture scenes [--reps N]                    backdrop + 3 sprites scene-load sets
//   S4.BigPicture upload <label> (--layers a#i,b#j | --scene sceneNN|heavy) [--tiles-per-frame N]
//                 [--budget-ms X] [--budget-mb X] [--workers N] [--normal-priority] [--prealloc] [--texpool] [--staging-mb N]
//                 [--pbo] [--no-verify]
//                 [--swap-interval N] [--time-limit S] [--warm-frames N]   (--tiles-per-frame 0 = decode-only control)
// Common: --game <CUSA47999 dir> (default: the environment variable TSUKI_GAME_DIR; no built-in path),
//         --oracle <oracle.json>, --out <dir> (default samples/spikes/s4).
// Inputs: samples/spikes/s4/oracle.json from oracle/s4_oracle.py. Windows close themselves.
using System.Diagnostics;
using System.Text.Json;
using S4.BigPicture;

return Cli.Run(args);

internal static class Cli
{
    private static readonly JsonSerializerOptions Jso = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(d.FullName, "docs")))
                return d.FullName;
        return Directory.GetCurrentDirectory();
    }

    private static string? Opt(List<string> a, string name)
    {
        int i = a.IndexOf(name);
        if (i < 0 || i + 1 >= a.Count)
            return null;
        string v = a[i + 1];
        a.RemoveRange(i, 2);
        return v;
    }

    private static bool Flag(List<string> a, string name) => a.Remove(name);

    public static int Run(string[] argv)
    {
        var a = argv.ToList();
        if (a.Count == 0)
        {
            Console.Error.WriteLine("usage: S4.BigPicture decode|verify|scenes|upload ... (see Program.cs)");
            return 2;
        }
        string root = RepoRoot();
        string outDir = Opt(a, "--out") ?? Path.Combine(root, "samples", "spikes", "s4");
        string oraclePath = Opt(a, "--oracle") ?? Path.Combine(outDir, "oracle.json");
        string? gameDir = Opt(a, "--game");
        int reps = int.Parse(Opt(a, "--reps") ?? "5");
        Directory.CreateDirectory(outDir);
        ThreadPool.SetMinThreads(32, 32);
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

        using var game = new Game(gameDir);
        Oracle? oracle = File.Exists(oraclePath) ? Oracle.Load(oraclePath) : null;
        if (oracle is null)
            Console.Error.WriteLine($"note: no oracle at {oraclePath}; SHA-1 comparisons are skipped");

        string cmd = a[0];
        a.RemoveAt(0);
        var doc = new Dictionary<string, object?>
        {
            ["schema"] = "tsuki-s4-result/1",
            ["command"] = string.Join(' ', argv),
            ["utc"] = DateTime.UtcNow.ToString("O"),
            ["runtime"] = Environment.Version.ToString(),
            ["configuration"] =
#if DEBUG
                "Debug",
#else
                "Release",
#endif
            ["processors"] = Environment.ProcessorCount,
            ["server_gc"] = System.Runtime.GCSettings.IsServerGC,
            ["game_dir"] = game.Dir,
        };
        string file;
        switch (cmd)
        {
            case "decode":
            {
                string arc = a[0];
                int index = int.Parse(a[1]);
                var r = Bench.Picture(game, oracle, arc, index, reps);
                doc["result"] = r;
                file = $"decode_{arc}_{index}.json";
                var w = (Dictionary<string, object?>)r["decode_ms_by_workers"]!;
                Console.WriteLine($"{arc}#{index} {r["name"]}: {r["header"]}");
                Console.WriteLine($"  read first {r["read_ms_first"]} ms, cached {r["read_ms_cached_median"]} ms, uncached {string.Join("/", (double[])r["read_ms_uncached"]!)} ms");
                Console.WriteLine($"  decode cold 16w {r["decode_ms_cold_16w"]} ms; median by workers: " +
                                  string.Join(", ", w.Select(kv => $"{kv.Key}w {((Dictionary<string, object?>)kv.Value!)["median"]}")));
                Console.WriteLine($"  ST MZX0 {((Dictionary<string, object?>)r["st_mzx_ms"]!)["min"]} ms = {r["st_mzx_mb_s_out"]} MB/s out; convert {((Dictionary<string, object?>)r["st_convert_ms"]!)["min"]} ms");
                Console.WriteLine($"  sha1 {r["sha1"]} equal={r.GetValueOrDefault("sha1_equal")}");
                var mem = (Dictionary<string, object?>)r["memory_after_cold_decode"]!;
                Console.WriteLine($"  peak WS after cold decode {mem["peak_working_set_mb"]} MB, native peak {mem["native_peak_mb"]} MB");
                break;
            }
            case "verify":
            {
                if (oracle is null)
                    return 3;
                var r = Bench.VerifyAll(game, oracle);
                doc["result"] = r;
                file = "verify.json";
                Console.WriteLine($"verify: {r["sha1_equal"]}/{r["pictures"]} equal, by format {JsonSerializer.Serialize(r["by_format"], Jso)}");
                break;
            }
            case "scenes":
            {
                if (oracle is null)
                    return 3;
                var rows = new List<Dictionary<string, object?>>();
                foreach (var s in oracle.Scenes.Append(oracle.Heavy))
                {
                    var r = Bench.Scene(game, s, reps);
                    rows.Add(r);
                    Console.WriteLine($"{s.Label}: {r["tiles"]} tiles, {(long)r["upload_bytes"]! / 1048576.0:F1} MB, decode 1w {r["decode_ms_1w_median"]} ms, 16w {r["decode_ms_16w_median"]} ms (cold {r["decode_ms_cold_16w"]})");
                }
                var typical = rows.Where(r => (string)r["label"]! != "heavy").ToList();
                double Q(IEnumerable<double> xs, double q)
                {
                    var s = xs.OrderBy(x => x).ToArray();
                    double pos = q * (s.Length - 1);
                    int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
                    return Math.Round(s[lo] + (s[hi] - s[lo]) * (pos - lo), 2);
                }
                Dictionary<string, object?> Agg(string key) => new()
                {
                    ["p50"] = Q(typical.Select(r => Convert.ToDouble(r[key])), 0.5),
                    ["p90"] = Q(typical.Select(r => Convert.ToDouble(r[key])), 0.9),
                    ["max"] = Q(typical.Select(r => Convert.ToDouble(r[key])), 1.0),
                };
                doc["result"] = new Dictionary<string, object?>
                {
                    ["scenes"] = rows,
                    ["typical_aggregate"] = new Dictionary<string, object?>
                    {
                        ["n"] = typical.Count,
                        ["upload_bytes"] = Agg("upload_bytes"),
                        ["tiles"] = Agg("tiles"),
                        ["entry_bytes"] = Agg("entry_bytes"),
                        ["read_ms"] = Agg("read_ms"),
                        ["read_ms_uncached"] = Agg("read_ms_uncached"),
                        ["decode_ms_cold_16w"] = Agg("decode_ms_cold_16w"),
                        ["decode_ms_1w_median"] = Agg("decode_ms_1w_median"),
                        ["decode_ms_16w_median"] = Agg("decode_ms_16w_median"),
                    },
                    ["usage"] = oracle.Usage,
                };
                file = "scenes.json";
                break;
            }
            case "upload":
            {
                string label = a.Count > 0 && !a[0].StartsWith("--", StringComparison.Ordinal) ? a[0] : "upload";
                var layers = new List<(string, int)>();
                if (Opt(a, "--layers") is { } spec)
                    foreach (string part in spec.Split(','))
                    {
                        var p = part.Split('#');
                        layers.Add((p[0], int.Parse(p[1])));
                    }
                if (Opt(a, "--scene") is { } sceneName)
                {
                    if (oracle is null)
                        return 3;
                    if (sceneName is "median" or "p90" or "max")
                    {
                        // pick from the scenes command output by upload bytes (typical scenes only)
                        using var sj = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(outDir, "scenes.json")));
                        var ranked = sj.RootElement.GetProperty("result").GetProperty("scenes").EnumerateArray()
                            .Where(x => x.GetProperty("label").GetString() != "heavy")
                            .OrderBy(x => x.GetProperty("upload_bytes").GetInt64())
                            .Select(x => x.GetProperty("label").GetString()!).ToList();
                        int at = sceneName switch { "median" => (ranked.Count - 1) / 2, "p90" => (int)Math.Round(0.9 * (ranked.Count - 1)), _ => ranked.Count - 1 };
                        sceneName = ranked[at];
                        Console.WriteLine($"scene by upload bytes: {sceneName}");
                    }
                    Scene sc = sceneName == "heavy" ? oracle.Heavy : oracle.Scenes.First(s => s.Label == sceneName);
                    layers.AddRange(sc.Layers.Select(l => (l.Arc, l.Index)));
                }
                var opt = new UploadOptions
                {
                    Label = label,
                    WarmFrames = int.Parse(Opt(a, "--warm-frames") ?? "120"),
                    TilesPerFrame = int.Parse(Opt(a, "--tiles-per-frame") ?? "4"),
                    BudgetMs = double.Parse(Opt(a, "--budget-ms") ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                    BudgetMb = double.Parse(Opt(a, "--budget-mb") ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                    Workers = int.Parse(Opt(a, "--workers") ?? "14"),
                    LowPriorityWorkers = !Flag(a, "--normal-priority"),
                    Prealloc = Flag(a, "--prealloc"),
                    TexPool = Flag(a, "--texpool"),
                    StagingMb = int.Parse(Opt(a, "--staging-mb") ?? "0"),
                    Pbo = Flag(a, "--pbo"),
                    Verify = !Flag(a, "--no-verify"),
                    SwapInterval = int.Parse(Opt(a, "--swap-interval") ?? "1"),
                    TimeLimitS = double.Parse(Opt(a, "--time-limit") ?? "40", System.Globalization.CultureInfo.InvariantCulture),
                };
                var sw = Stopwatch.StartNew();
                var r = GlUpload.Run(game, oracle, layers, opt);
                r["process_wall_s"] = Math.Round(sw.Elapsed.TotalSeconds, 2);
                doc["result"] = r;
                file = $"upload_{label}.json";
                Console.WriteLine($"upload {label}: {r["tiles"]} tiles, {r["load_frames"]} load frames, decode {r["decode_wall_ms"]} ms, load {r["load_wall_ms"]} ms");
                Console.WriteLine($"  vsync {JsonSerializer.Serialize(r["refresh_ms_warm"], Jso)}");
                Console.WriteLine($"  frame during load {JsonSerializer.Serialize(r["frame_ms_load"], Jso)}, over 16.7 ms: {r["frames_over_16_7ms_load"]}");
                Console.WriteLine($"  upload cpu/frame {JsonSerializer.Serialize(r["upload_cpu_ms_per_frame"], Jso)}, gpu/frame {JsonSerializer.Serialize(r["upload_gpu_ms_per_frame"], Jso)}");
                Console.WriteLine($"  per tile {JsonSerializer.Serialize(r["upload_cpu_ms_per_tile"], Jso)}");
                if (r.TryGetValue("readback_verify", out var v))
                    Console.WriteLine($"  readback {JsonSerializer.Serialize(v, Jso)}");
                break;
            }
            default:
                Console.Error.WriteLine($"unknown command {cmd}");
                return 2;
        }
        string path = Path.Combine(outDir, file);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, Jso));
        Console.WriteLine($"wrote {path}");
        return 0;
    }
}

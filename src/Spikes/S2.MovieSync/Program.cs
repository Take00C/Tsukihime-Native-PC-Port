// Spike S2 movie: prologue_en.mp4 decoded on a thread, synced to the audio clock (ROADMAP M0 scope 8; thresholds
// and fallback in the M0 spike table; results in docs/status/spikes/S2.md). Windows it opens close themselves:
// playback stops after --seconds (default 45, at most 52) and a watchdog ends a play process after --hard-limit (default 57 s).
//
//   S2.MovieSync play  [--hw none|d3d11va|dxva2|d3d12va] [--threads 0] [--seconds 45] [--start 0] [--upload direct|pbo]
//                      [--readback 25] [--tick-clock nominal|now] [--cadence-lock 4] [--tick-phase <ms>] [--swap 1]
//   S2.MovieSync bench [--hw ...] [--threads 0] [--limit 0] [--extra-hw-frames 0]   headless decode-only benchmark
//   S2.MovieSync audio                                          headless AAC decode of the whole track
//   S2.MovieSync caps                                           FFmpeg version, hw device types, h264 hw configs
// Common: --movie <path> (default $TSUKI_GAME_DIR/image/movie/prologue_en.mp4), --out <dir>, --name <run name>.
using System.Globalization;

namespace S2.MovieSync;

/// <summary>Command-line options.</summary>
internal sealed class Options
{
    /// <summary>What the parser says when --movie is not given and TSUKI_GAME_DIR is unset (no built-in default path).</summary>
    public const string GameDirMissing =
        "--movie not given and the environment variable TSUKI_GAME_DIR is not set: set it to your extracted CUSA47999 folder (the one holding eboot.bin and image\\)";

    public string Command { get; set; } = "play";

    public string Movie { get; set; } = "";

    public string Hw { get; set; } = "none";

    public int Threads { get; set; }

    public double Seconds { get; set; } = 45;

    public double Start { get; set; }

    public double Limit { get; set; }

    public string Upload { get; set; } = "direct";

    public double Readback { get; set; } = -1;

    public int WindowWidth { get; set; } = 1920;

    public int WindowHeight { get; set; } = 1080;

    public int SwapInterval { get; set; } = 1;

    public int FrameBuffers { get; set; } = 8;

    /// <summary>When set: start the 60 Hz tick grid when the audio clock reaches base + this many ms (0 = ticks on frame boundaries, the worst case).</summary>
    public double TickPhaseMs { get; set; } = double.NaN;

    /// <summary>Cadence lock window (ms): near a frame boundary keep the 2-ticks-per-frame cadence instead of following clock jitter. 0 = off.</summary>
    public double CadenceLockMs { get; set; } = 4;

    /// <summary>When a 60 Hz tick reads the audio clock: "nominal" = at the tick's grid time, "now" = when the 144 Hz loop gets to it.</summary>
    public string TickClock { get; set; } = "nominal";

    public double HardLimit { get; set; } = 57;

    public string OutDir { get; set; } = "";

    public string Name { get; set; } = "";

    public static Options Parse(string[] args)
    {
        var o = new Options();
        int i = 0;
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            o.Command = args[0];
            i = 1;
        }

        for (; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value for " + a);
            double D() => double.Parse(Next(), CultureInfo.InvariantCulture);
            switch (a)
            {
                case "--movie": o.Movie = Next(); break;
                case "--hw": o.Hw = Next(); break;
                case "--threads": o.Threads = (int)D(); break;
                case "--seconds": o.Seconds = D(); break;
                case "--start": o.Start = D(); break;
                case "--limit": o.Limit = D(); break;
                case "--upload": o.Upload = Next(); break;
                case "--readback": o.Readback = D(); break;
                case "--window":
                    string[] wh = Next().Split('x');
                    o.WindowWidth = int.Parse(wh[0], CultureInfo.InvariantCulture);
                    o.WindowHeight = int.Parse(wh[1], CultureInfo.InvariantCulture);
                    break;
                case "--swap": o.SwapInterval = (int)D(); break;
                case "--buffers": o.FrameBuffers = (int)D(); break;
                case "--tick-phase": o.TickPhaseMs = D(); break;
                case "--cadence-lock": o.CadenceLockMs = D(); break;
                case "--tick-clock": o.TickClock = Next(); break;
                case "--hard-limit": o.HardLimit = D(); break;
                case "--extra-hw-frames": VideoDecoder.ExtraHwFrames = (int)D(); break;
                case "--out": o.OutDir = Next(); break;
                case "--name": o.Name = Next(); break;
                default: throw new ArgumentException("unknown option " + a);
            }
        }

        if (o.Movie.Length == 0)
        {
            string game = Environment.GetEnvironmentVariable("TSUKI_GAME_DIR") is { Length: > 0 } g ? g : throw new ArgumentException(GameDirMissing);
            o.Movie = Path.Combine(game, "image", "movie", "prologue_en.mp4");
        }

        if (o.OutDir.Length == 0)
        {
            o.OutDir = Path.Combine(RepoRoot(), "samples", "spikes", "s2");
        }

        if (o.Name.Length == 0)
        {
            o.Name = $"{o.Command}_{o.Hw}" + (o.Start > 0 ? $"_from{o.Start.ToString("F0", CultureInfo.InvariantCulture)}" : "");
        }

        // a window must close itself within 60 s (spike rule)
        o.Seconds = Math.Min(o.Seconds, 52);
        o.HardLimit = Math.Min(o.HardLimit, 58);
        return o;
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(d.FullName, "src")))
            {
                return d.FullName;
            }
        }

        return Directory.GetCurrentDirectory();
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        Options o;
        try
        {
            o = Options.Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }

        // watchdog: whatever happens, a process with a window is gone before 60 s (headless modes have no window)
        if (o.Command == "play")
        {
            var watchdog = new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(o.HardLimit));
                Console.Error.WriteLine($"watchdog: {o.HardLimit} s elapsed, exiting");
                Environment.Exit(3);
            })
            { IsBackground = true, Name = "s2-watchdog" };
            watchdog.Start();
        }

        if (!File.Exists(o.Movie))
        {
            Console.Error.WriteLine($"movie not found: {o.Movie} (set TSUKI_GAME_DIR)");
            return 4;
        }

        string avcodec = FfmpegLib.Init();
        var head = new Dictionary<string, object?>
        {
            ["run"] = o.Name,
            ["utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["args"] = string.Join(' ', args),
            ["movie"] = Path.GetFileName(o.Movie),
            ["movie_bytes"] = new FileInfo(o.Movie).Length,
            ["avcodec_version"] = avcodec,
            ["ffmpeg_dir"] = FfmpegLib.RootPath,
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
#if DEBUG
            ["configuration"] = "Debug",
#else
            ["configuration"] = "Release",
#endif
        };

        Dictionary<string, object?> result;
        switch (o.Command)
        {
            case "caps":
                result = new Dictionary<string, object?>
                {
                    ["mode"] = "caps",
                    ["hw_device_types"] = FfmpegLib.HwDeviceTypes(),
                    ["h264_hw_configs"] = FfmpegLib.H264HwConfigs(),
                };
                Console.WriteLine("hw device types: " + string.Join(", ", (List<string>)result["hw_device_types"]!));
                Console.WriteLine("h264 hw configs: " + string.Join(", ", (List<string>)result["h264_hw_configs"]!));
                break;
            case "bench":
                result = Bench.Video(o.Movie, o.Hw, o.Threads, o.Limit);
                break;
            case "audio":
                result = Bench.Audio(o.Movie);
                break;
            case "play":
                result = Player.Run(o);
                PrintPlaySummary(result);
                break;
            default:
                Console.Error.WriteLine("unknown command " + o.Command);
                return 2;
        }

        foreach (var kv in result)
        {
            head[kv.Key] = kv.Value;
        }

        string json = Path.Combine(o.OutDir, o.Name + ".json");
        Report.WriteJson(json, head);
        Console.WriteLine("report: " + json);
        return 0;
    }

    private static void PrintPlaySummary(Dictionary<string, object?> r)
    {
        foreach (string key in new[]
        {
            "stop_reason", "play_wall_s", "hw", "upload_path", "dropped_frames", "selected_but_never_presented", "late_frames", "cadence_lock_holds", "cadence_lock_early", "ticks_per_frame_histogram", "presents_per_frame_histogram",
            "frame_index_error_histogram (shown - ideal)", "av_offset_ms (pts - clock, per tick)",
            "av_sync_error_ms (clock outside the frame's display interval)", "checkpoints", "end_event_after_last_frame_presented_ms",
            "present_interval_ms", "presents_over_16_7ms", "upload_cpu_ms", "upload_gpu_ms", "draw_gpu_ms", "decode_call_ms_per_frame",
            "hw_transfer_ms", "cpu", "audio_underrun_frames", "audio_device", "audio_clock_rate_vs_qpc_ppm",
        })
        {
            if (r.TryGetValue(key, out object? v))
            {
                Console.WriteLine($"{key}: {System.Text.Json.JsonSerializer.Serialize(v, Report.JsonOptions)}");
            }
        }
    }
}

// Spike S3 compositor (ROADMAP M0 scope 8; docs/status/spikes/S3.md): blend shaders in GLSL from
// tools/tsuki/compose.py, backdrop blits, rule wipe, timed at 1920 x 1080 in a GL 3.3 core context.
//
//   S3.Blend1080 verify [--data DIR]
//   S3.Blend1080 bench  [--data DIR] [--name N] [--frames 600] [--warmup 60] [--blit full|bounds]
//                       [--opacity 1.0] [--modes 11,17,19,23,24,11,17,19,23,24] [--sprites 2]
//                       [--transition 1] [--fads-ms 1500] [--vague 256] [--vsync 0] [--breakdown]
//                       [--window 1280x720] [--snapshot FRAME]
//
// DIR defaults to <repo>/samples/spikes/s3 (written by tools/export_s3.py). Every window closes by
// itself: the process exits after 55 s whatever happens.
using System.Globalization;
using Tsuki.Spikes.S3;

const int WatchdogSeconds = 55;
var watchdog = new Thread(() =>
{
    Thread.Sleep(TimeSpan.FromSeconds(WatchdogSeconds));
    Console.Error.WriteLine($"S3: watchdog after {WatchdogSeconds} s, exiting");
    Environment.Exit(3);
}) { IsBackground = true };
watchdog.Start();

if (args.Length == 0 || args[0] is not ("verify" or "bench"))
{
    Console.Error.WriteLine("usage: S3.Blend1080 verify|bench [options] (see Program.cs)");
    return 2;
}

var opts = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"unexpected argument {args[i]}");
        return 2;
    }
    string key = args[i][2..];
    bool flag = i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal);
    opts[key] = flag ? "1" : args[++i];
}

string Opt(string k, string d) => opts.TryGetValue(k, out var v) ? v : d;
int OptI(string k, int d) => int.Parse(Opt(k, d.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

string data = Path.GetFullPath(Opt("data", Path.Combine(RepoRoot(), "samples", "spikes", "s3")));
if (!File.Exists(Path.Combine(data, "pictures", "manifest.json")))
{
    Console.Error.WriteLine($"S3 data missing in {data}: run src/Spikes/S3.Blend1080/tools/export_s3.py first");
    return 2;
}

try
{
    if (args[0] == "verify")
        return Verify.Run(data);

    string[] win = Opt("window", "1280x720").Split('x');
    var cfg = new BenchConfig
    {
        Name = Opt("name", "bench"),
        DataDir = data,
        Frames = OptI("frames", 600),
        Warmup = OptI("warmup", 60),
        Blit = Opt("blit", "full") == "bounds" ? BlitMode.Bounds : BlitMode.Full,
        Opacity = float.Parse(Opt("opacity", "1.0"), CultureInfo.InvariantCulture),
        Modes = [.. Opt("modes", "11,17,19,23,24,11,17,19,23,24").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture))],
        Sprites = OptI("sprites", 2),
        Transition = OptI("transition", 1) != 0,
        TransitionMs = OptI("fads-ms", 1500),
        Vague = OptI("vague", 256),
        SwapInterval = OptI("vsync", 0),
        Breakdown = opts.ContainsKey("breakdown"),
        WindowWidth = int.Parse(win[0], CultureInfo.InvariantCulture),
        WindowHeight = int.Parse(win[1], CultureInfo.InvariantCulture),
        SnapshotFrame = OptI("snapshot", -1),
    };
    return Bench.Run(cfg);
}
catch (Exception e)
{
    Console.Error.WriteLine("S3 failed: " + e);
    return 1;
}

static string RepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "Directory.Build.props")))
        d = d.Parent;
    return d?.FullName ?? Directory.GetCurrentDirectory();
}

// Spike S3 benchmark: a 2070 x 1230 backdrop zooming every frame, N full-screen layers with affine
// transforms and backdrop-reading blends, optional partial-screen sprite layers and a continuous
// _FADS(2, 1500, 29) rule wipe, at 1920 x 1080; GPU time from GL_TIME_ELAPSED queries.
using System.Diagnostics;
using System.Text.Json;
using Silk.NET.OpenGL;

namespace Tsuki.Spikes.S3;

internal sealed class BenchConfig
{
    public string Name = "bench";
    public string DataDir = "";
    public int Frames = 600;
    public int Warmup = 60;
    public BlitMode Blit = BlitMode.Full;
    public float Opacity = 1f;
    public int[] Modes = [11, 17, 19, 23, 24, 11, 17, 19, 23, 24];
    public int Sprites = 2;
    public bool Transition = true;
    public int TransitionMs = 1500;
    public int Vague = 256;
    public int SwapInterval;
    public bool Breakdown;
    public int WindowWidth = 1280;
    public int WindowHeight = 720;
    public int SnapshotFrame = -1;
}

internal static class Bench
{
    private const int W = 1920, H = 1080;
    private const int Ring = 6;

    // pictures per blend mode, after their use in the opening montage (script 3; export_s3.py)
    private static readonly Dictionary<int, string[]> Pool = new()
    {
        [11] = ["img2230", "img2232"],
        [17] = ["img2183", "img0813", "img0700_06_06"],
        [19] = ["img2439", "img1685"],
        [22] = ["img0784", "img2345"],
        [23] = ["img1685", "img0597"],
        [24] = ["img2182", "img0597"],
        [13] = ["img2163"],
    };
    private static readonly string[] AnyPool = ["img2183", "img2230", "img2439", "img1685", "img2182", "img0813", "img0597", "img2232"];

    private sealed class Slot : IGpuMarks
    {
        public uint Composite, Present;
        public readonly uint[] Ts;
        public readonly string[] Labels;
        public int Used;
        public int Frame = -1;
        private readonly GL _gl;
        public bool Enabled;

        public Slot(GL gl, int capacity)
        {
            _gl = gl;
            Composite = gl.GenQuery();
            Present = gl.GenQuery();
            Ts = new uint[capacity];
            for (int i = 0; i < capacity; i++)
                Ts[i] = gl.GenQuery();
            Labels = new string[capacity];
        }

        public void Mark(string label)
        {
            if (!Enabled || Used >= Ts.Length)
                return;
            _gl.QueryCounter(Ts[Used], QueryCounterTarget.Timestamp);
            Labels[Used++] = label;
        }
    }

    private sealed class Samples
    {
        public readonly List<double> GpuComposite = [], GpuPresent = [], GpuFrame = [];
        public readonly List<double> CpuSubmit = [], CpuSwap = [], Interval = [];
        public readonly List<double> TsFrame = [], TsBlits = [], TsRule = [];
        public readonly List<int> Blits = [];
        public readonly List<long> BlitPixels = [];
    }

    public static int Run(BenchConfig cfg)
    {
        using var win = GlWindow.Create(cfg.WindowWidth, cfg.WindowHeight, hidden: false, cfg.SwapInterval);
        GL gl = win.Gl;
        using var c = new Compositor(gl, W, H) { Blit = cfg.Blit, Nearest = false };
        var pics = PictureSet.Load(c, cfg.DataDir, keepPixels: false);
        gl.Finish();
        Console.WriteLine($"[{cfg.Name}] {win.Info}");
        Console.WriteLine($"[{cfg.Name}] pictures {pics.All.Count}, {pics.Bytes:N0} bytes: read {pics.ReadMs:F0} ms, upload {pics.UploadMs:F0} ms; window {win.PixelWidth}x{win.PixelHeight}, swap interval {win.SwapInterval}");

        var layers = new List<(Picture Pic, int Mode)>();
        var used = new Dictionary<int, int>();
        foreach (int m in cfg.Modes)
        {
            string[] pool = Pool.TryGetValue(m, out var pp) ? pp : AnyPool;
            int k = used.TryGetValue(m, out int u) ? u : 0;
            used[m] = k + 1;
            layers.Add((pics[pool[k % pool.Length]], m));
        }
        Picture backdrop = pics["img2345"];
        Picture rule = pics["rule29"];
        Picture[] spritePics = [pics["img2163"], pics["img0758"]];
        int[] spriteModes = [13, 19];

        var slots = new Slot[Ring];
        for (int i = 0; i < Ring; i++)
            slots[i] = new Slot(gl, 96) { Enabled = cfg.Breakdown };
        var smp = new Samples();
        float step = Effects.FadsStep(cfg.TransitionMs);
        float progress = 0f;                          // 0 = no transition running: start one
        int total = cfg.Warmup + cfg.Frames;
        long prevStart = 0;
        bool aborted = false;

        for (int f = 0; f < total; f++)
        {
            long tStart = Stopwatch.GetTimestamp();
            if (!win.Pump())
            {
                aborted = true;
                break;
            }
            Slot slot = slots[f % Ring];
            if (slot.Frame >= 0)
                Collect(gl, slot, smp, cfg.Warmup);
            slot.Frame = f;
            slot.Used = 0;

            gl.BeginQuery(QueryTarget.TimeElapsed, slot.Composite);
            slot.Mark("frame+");
            c.Marks = slot;
            c.BeginFrame();
            progress = DrawFrame(c, cfg, f, backdrop, layers, spritePics, spriteModes, rule, progress, step);
            slot.Mark("frame-");
            c.Marks = null;
            gl.EndQuery(QueryTarget.TimeElapsed);
            int blits = c.BlitCount;
            long blitPx = c.BlitPixels;

            gl.BeginQuery(QueryTarget.TimeElapsed, slot.Present);
            c.Present(win.PixelWidth, win.PixelHeight);
            gl.EndQuery(QueryTarget.TimeElapsed);
            long tSubmit = Stopwatch.GetTimestamp();
            win.Swap();
            long tSwap = Stopwatch.GetTimestamp();
            if (f >= cfg.Warmup)
            {
                smp.CpuSubmit.Add(Stopwatch.GetElapsedTime(tStart, tSubmit).TotalMilliseconds);
                smp.CpuSwap.Add(Stopwatch.GetElapsedTime(tSubmit, tSwap).TotalMilliseconds);
                if (f > cfg.Warmup)
                    smp.Interval.Add(Stopwatch.GetElapsedTime(prevStart, tStart).TotalMilliseconds);
                smp.Blits.Add(blits);
                smp.BlitPixels.Add(blitPx);
            }
            prevStart = tStart;
        }
        gl.Finish();
        foreach (var s in slots.Where(x => x.Frame >= 0).OrderBy(x => x.Frame).ToList())
            Collect(gl, s, smp, cfg.Warmup);
        var err = gl.GetError();

        string? snapshot = null;
        if (cfg.SnapshotFrame >= 0)
        {
            // replay the transition state of that frame (progress after SnapshotFrame updates) and read it back
            float p = 0f;
            for (int f = 0; f <= cfg.SnapshotFrame; f++)
            {
                c.BeginFrame();
                p = DrawFrame(c, cfg, f, backdrop, layers, spritePics, spriteModes, rule, p, step);
            }
            byte[] px = c.Read(c.Target);
            snapshot = Path.Combine(cfg.DataDir, "bench", $"{cfg.Name}_frame{cfg.SnapshotFrame}.rgba");
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
            File.WriteAllBytes(snapshot, px);
        }

        var result = new
        {
            name = cfg.Name,
            date_utc = DateTime.UtcNow.ToString("o"),
            aborted,
            gl_error_at_end = err.ToString(),
            gl = win.Info,
            config = new
            {
                frames = cfg.Frames, warmup = cfg.Warmup, blit = cfg.Blit.ToString(), opacity = cfg.Opacity, modes = cfg.Modes,
                sprites = cfg.Sprites, transition = cfg.Transition, transition_ms = cfg.TransitionMs, vague = cfg.Vague,
                swap_interval_requested = cfg.SwapInterval, swap_interval = win.SwapInterval, breakdown = cfg.Breakdown,
                window = $"{win.PixelWidth}x{win.PixelHeight}", frame = $"{W}x{H}",
                layers = layers.Select(l => new { picture = l.Pic.Name, w = l.Pic.Width, h = l.Pic.Height, mode = l.Mode }).ToArray(),
                backdrop = new { picture = backdrop.Name, w = backdrop.Width, h = backdrop.Height },
                rule = rule.Name,
            },
            pictures = new { count = pics.All.Count, bytes = pics.Bytes, read_ms = pics.ReadMs, upload_ms = pics.UploadMs },
            summary = new
            {
                gpu_composite_ms = Summary.Of(smp.GpuComposite),
                gpu_present_ms = Summary.Of(smp.GpuPresent),
                gpu_frame_ms = Summary.Of(smp.GpuFrame),
                cpu_submit_ms = Summary.Of(smp.CpuSubmit),
                cpu_swap_ms = Summary.Of(smp.CpuSwap),
                frame_interval_ms = Summary.Of(smp.Interval),
                ts_frame_ms = Summary.Of(smp.TsFrame),
                ts_blits_ms = Summary.Of(smp.TsBlits),
                ts_rule_ms = Summary.Of(smp.TsRule),
                blits_per_frame = Summary.Of(smp.Blits.Select(x => (double)x).ToList()),
                blit_mpix_per_frame = Summary.Of(smp.BlitPixels.Select(x => x / 1e6).ToList()),
            },
            snapshot,
            samples = new
            {
                gpu_composite_ms = smp.GpuComposite, gpu_present_ms = smp.GpuPresent, cpu_submit_ms = smp.CpuSubmit,
                frame_interval_ms = smp.Interval, ts_blits_ms = smp.TsBlits,
            },
        };
        string outDir = Path.Combine(cfg.DataDir, "bench");
        Directory.CreateDirectory(outDir);
        string outFile = Path.Combine(outDir, cfg.Name + ".json");
        File.WriteAllText(outFile, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));

        Console.WriteLine($"[{cfg.Name}] blit {cfg.Blit}, opacity {cfg.Opacity}, modes {string.Join(",", cfg.Modes)}, sprites {cfg.Sprites}, transition {cfg.Transition} (vague {cfg.Vague}), {cfg.Frames} frames after {cfg.Warmup} warm-up");
        Console.WriteLine($"[{cfg.Name}] GPU composite ms : {Summary.Of(smp.GpuComposite)}");
        Console.WriteLine($"[{cfg.Name}] GPU present ms   : {Summary.Of(smp.GpuPresent)}");
        Console.WriteLine($"[{cfg.Name}] GPU frame ms     : {Summary.Of(smp.GpuFrame)}");
        Console.WriteLine($"[{cfg.Name}] CPU submit ms    : {Summary.Of(smp.CpuSubmit)}");
        Console.WriteLine($"[{cfg.Name}] frame interval ms: {Summary.Of(smp.Interval)}");
        Console.WriteLine($"[{cfg.Name}] blits/frame      : {Summary.Of(smp.Blits.Select(x => (double)x).ToList())}; Mpx/frame {Summary.Of(smp.BlitPixels.Select(x => x / 1e6).ToList())}");
        if (cfg.Breakdown)
        {
            Console.WriteLine($"[{cfg.Name}] TS frame ms      : {Summary.Of(smp.TsFrame)}");
            Console.WriteLine($"[{cfg.Name}] TS blits ms      : {Summary.Of(smp.TsBlits)}");
            Console.WriteLine($"[{cfg.Name}] TS rule wipe ms  : {Summary.Of(smp.TsRule)}");
        }
        Console.WriteLine($"[{cfg.Name}] GL error at end: {err}; aborted: {aborted}; wrote {outFile}");
        return aborted || err != GLEnum.NoError ? 1 : 0;
    }

    private static void Collect(GL gl, Slot s, Samples smp, int warmup)
    {
        long comp = gl.GetQueryObject(s.Composite, QueryObjectParameterName.Result);
        long pres = gl.GetQueryObject(s.Present, QueryObjectParameterName.Result);
        double tsFrame = 0, tsBlits = 0, tsRule = 0;
        if (s.Used > 0)
        {
            var ts = new long[s.Used];
            for (int i = 0; i < s.Used; i++)
                ts[i] = gl.GetQueryObject(s.Ts[i], QueryObjectParameterName.Result);
            for (int i = 0; i < s.Used; i++)
            {
                if (s.Labels[i] == "frame-")
                    tsFrame = (ts[i] - ts[0]) / 1e6;
                if (s.Labels[i] == "blit-" && i > 0 && s.Labels[i - 1] == "blit+")
                    tsBlits += (ts[i] - ts[i - 1]) / 1e6;
                if (s.Labels[i] == "rule-")
                    for (int j = i - 1; j >= 0; j--)
                        if (s.Labels[j] == "rule+")
                        {
                            tsRule += (ts[i] - ts[j]) / 1e6;
                            break;
                        }
            }
        }
        if (s.Frame >= warmup)
        {
            smp.GpuComposite.Add(comp / 1e6);
            smp.GpuPresent.Add(pres / 1e6);
            smp.GpuFrame.Add((comp + pres) / 1e6);
            if (s.Used > 0)
            {
                smp.TsFrame.Add(tsFrame);
                smp.TsBlits.Add(tsBlits);
                smp.TsRule.Add(tsRule);
            }
        }
        s.Frame = -1;
    }

    /// <summary>Picture with its anchor at (px, py), scaled by (sx, sy) and rotated by rot radians.</summary>
    public static Vertex[] Affine(Picture p, float ax, float ay, float px, float py, float sx, float sy, float rot, float alpha)
    {
        float cr = MathF.Cos(rot), sr = MathF.Sin(rot);
        var v = new Vertex[4];
        (float X, float Y, float U, float V)[] corners = [(0, 0, 0, 0), (p.Width, 0, 1, 0), (0, p.Height, 0, 1), (p.Width, p.Height, 1, 1)];
        for (int i = 0; i < 4; i++)
        {
            float lx = (corners[i].X - ax) * sx, ly = (corners[i].Y - ay) * sy;
            v[i] = new Vertex(px + lx * cr - ly * sr, py + lx * sr + ly * cr, corners[i].U, corners[i].V, 1, 1, 1, alpha);
        }
        return v;
    }

    private static float Osc(int t, float period, float phase = 0f) => MathF.Sin(2f * MathF.PI * t / period + phase);

    /// <summary>One frame of the benchmark scene; returns the transition progress for the next frame.</summary>
    private static float DrawFrame(Compositor c, BenchConfig cfg, int t, Picture backdrop, List<(Picture Pic, int Mode)> layers,
                                   Picture[] spritePics, int[] spriteModes, Picture rule, float progress, float step)
    {
        bool starting = cfg.Transition && progress <= 0f;
        if (starting)
        {
            c.BeginTransition();                       // capture the previous frame, alpha := 1
            progress = 1f;
        }
        c.Clear(0, 0, 0, 0);

        // backdrop: zoom 1.0 .. 1.25 about the frame centre, every frame (normal draw, word 0)
        float z = 1f + 0.25f * (0.5f - 0.5f * MathF.Cos(2f * MathF.PI * t / 240f));
        c.DrawQuad(backdrop.Texture, Affine(backdrop, backdrop.Width / 2f, backdrop.Height / 2f, W / 2f, H / 2f, z, z, 0f, 1f), 0);

        // full-screen blend layers: cover scale x 1.15 +- 0.05, rotation +- 2 deg, drift +- 20 px, every third mirrored
        for (int i = 0; i < layers.Count; i++)
        {
            var (pic, mode) = layers[i];
            float cover = MathF.Max(W / (float)pic.Width, H / (float)pic.Height);
            float s = cover * (1.15f + 0.05f * Osc(t, 200 + 13 * i, i));
            float rot = 2f * MathF.PI / 180f * Osc(t, 170 + 11 * i, 0.7f * i);
            float px = W / 2f + 20f * Osc(t, 150 + 7 * i), py = H / 2f + 20f * MathF.Cos(2f * MathF.PI * t / (160 + 5 * i));
            float sx = i % 3 == 2 ? -s : s;
            c.DrawQuad(pic.Texture, Affine(pic, pic.Width / 2f, pic.Height / 2f, px, py, sx, s, rot, cfg.Opacity), mode);
        }

        // partial-screen sprite layers (bounds blits smaller than the frame)
        for (int k = 0; k < cfg.Sprites; k++)
        {
            Picture p = spritePics[k % spritePics.Length];
            int mode = spriteModes[k % spriteModes.Length];
            float ph = k * 1.3f;
            if (k % 2 == 0)
                c.DrawQuad(p.Texture, Affine(p, p.Width / 2f, p.Height / 2f, W / 2f + 500f * Osc(t, 300, ph), 700f, -0.45f, 0.45f,
                                             5f * MathF.PI / 180f * Osc(t, 210, ph), cfg.Opacity), mode);
            else
                c.DrawQuad(p.Texture, Affine(p, p.Width / 2f, p.Height / 2f, W / 2f + 400f * MathF.Cos(2f * MathF.PI * t / 240f + ph),
                                             H / 2f + 200f * Osc(t, 240, ph), 0.9f, 0.9f, 2f * MathF.PI * t / 600f, cfg.Opacity), mode);
        }

        if (cfg.Transition && progress > 0f)
        {
            c.DrawRuleWipe(rule.Texture, progress, cfg.Vague);
            progress += step;                          // 0x68A70: progress += speed * step
            if (progress <= 0f)
                progress = 0f;
        }
        return progress;
    }
}

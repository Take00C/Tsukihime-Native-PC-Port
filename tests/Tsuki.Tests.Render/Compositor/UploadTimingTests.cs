using System.Diagnostics;
using Tsuki.Data.Containers;
using Tsuki.Data.Images;
using Tsuki.Engine.Contracts;
using Tsuki.Platform;
using Tsuki.Render.Compositor;
using Tsuki.Render.Textures;
using Tsuki.Tests.Render.Smoke;

namespace Tsuki.Tests.Render.Compositor;

/// <summary>
/// The upload scheduler with S4's five rules, timed (PACE 7.2 #5 D1 "upload with 0 frames over 16.7 ms"; S4.md 5): the
/// largest picture IMG1481 (allpac1 #1259, 330 live tiles, 330 MiB) is decoded by the decode workers and uploaded in
/// 16 MiB slices while a visible 1280 x 720 window presents at vsync and draws every resident tile through the
/// display-list executor. No presented frame of the load may take more than 16.7 ms. Holds artifacts/gpu.lock
/// (decision 4). The window closes itself well within 60 s.
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class UploadTimingTests(ITestOutputHelper output)
{
    private const int Runs = 5;
    private const int WarmFrames = 120;
    private const int TailFrames = 30;
    private const double FrameLimitMs = 16.7;

    [GameDataFact]
    public void UploadingTheLargestPictureKeepsEveryFrameUnder16_7Milliseconds() => Measure(prewarm: true, runs: Runs);

    /// <summary>
    /// The same load with lazily allocated textures (one glTexImage2D per uploaded tile; S4 rule 4 without the pool).
    /// It runs in every suite run (it holds gpu.lock and shows the window for about 2 s) and asserts the lazy path
    /// functionally: every tile is uploaded, the tiles of run 0 read back equal to a fresh decode, no GL error. Its
    /// frame times are reported, not asserted: on this shared machine at 93-100 % CPU it gave 0-1 frames over
    /// 16.7 ms per 113 load frames (docs/status/wave2/D1.md), which is why the runtime pre-creates the pool.
    /// </summary>
    [GameDataFact]
    public void LazyAllocationUploadsEveryTileAndReportsItsFrameTimes() => Measure(prewarm: false, runs: 1, assert: false);

    private void Measure(bool prewarm, int runs, bool assert = true)
    {
        string game = GameFolder.Require();
        using MrgArchive arc = MrgArchive.Open(Path.Combine(game, "image", "allpac1"));
        ImageMzp img = ImageMzp.Open(arc.OpenContainer(1259));
        ImageHeader h = img.Header;
        int[] live = [.. Enumerable.Range(0, h.Tiles).Where(h.IsLive)];
        output.WriteLine($"IMG1481: {h.DisplayWidth} x {h.DisplayHeight}, tiles {h.TilesX} x {h.TilesY} of {h.TileWidth} x {h.TileHeight}, live {live.Length}, {(long)live.Length * h.TileRgbaBytes >> 20} MiB");

        using GpuLock gpuLock = GpuLock.Acquire("D1 upload timing (UploadTimingTests)", TimeSpan.FromMinutes(8));
        using GlContext ctx = GlContext.Create(new WindowOptions("Tsuki D1 upload timing (closes itself)", 1280, 720, Hidden: false, Resizable: false, SwapInterval: 1));
        using var compositor = new GpuCompositor(ctx.Device.Gl);
        var pump = new EventPump(ctx.Window, null);
        var events = new List<PlatformEvent>();
        var watchdog = Stopwatch.StartNew();
        using var priority = new ThreadPriorityScope(ThreadPriority.AboveNormal);   // the frame thread above the BelowNormal decode workers
        var scratch = new ThreadLocal<TileScratch>(() => new TileScratch());

        var allLoad = new List<double>();
        int over = 0, idleFrames = 0, idleOver = 0;
        for (int run = 0; run < runs && watchdog.Elapsed < TimeSpan.FromSeconds(45); run++)
        {
            using var cache = new TextureCache(ctx.Device.Gl, spareCapacityBytes: 512L << 20);
            var pool = new StagingPool();
            var sched = new UploadScheduler(cache, pool);
            var exec = new DisplayListExecutor(compositor, cache);
            var list = new DisplayList(512);
            var intervals = new List<double>();
            var uploadMs = new List<double>();
            var slow = new List<string>();
            (ulong Idle, ulong Total, TimeSpan Own) cpu0 = default;
            int loadStart = -1, loadEnd = -1;
            long t0 = Stopwatch.GetTimestamp(), decodeStart = 0, lastTile = 0;
            double machineBusy = double.NaN, ownBusy = double.NaN;
            using (var workers = new DecodeWorkers(sched))
            {
                for (int frame = 0; watchdog.Elapsed < TimeSpan.FromSeconds(50); frame++)
                {
                    pump.Pump(events);
                    events.Clear();
                    if (prewarm && frame < WarmFrames)
                    {
                        cache.Prewarm(h.TileWidth, h.TileHeight, 8);               // texture pool over the warm-up frames (S4 rule 4)
                    }

                    if (frame == WarmFrames)
                    {
                        loadStart = frame;
                        cpu0 = CpuSample();
                        decodeStart = Stopwatch.GetTimestamp();
                        foreach (int t in live)
                        {
                            int tile = t;
                            workers.Enqueue(new DecodeJob(Key(run, tile), h.TileWidth, h.TileHeight, px => img.DecodeTile(tile, scratch.Value!, px)));
                        }
                    }

                    UploadSliceStats up = sched.UploadSlice();
                    if (loadStart >= 0 && loadEnd < 0)
                    {
                        uploadMs.Add(up.CpuMilliseconds);
                        if (sched.UploadedTiles == live.Length)
                        {
                            loadEnd = frame;
                            lastTile = Stopwatch.GetTimestamp();
                        }
                    }

                    long td = Stopwatch.GetTimestamp();
                    BuildList(list, frame, h, live, run, cache);
                    exec.Execute(list);
                    compositor.Present(1280, 720);
                    long ts = Stopwatch.GetTimestamp();
                    ctx.Window.Swap();
                    long now = Stopwatch.GetTimestamp();
                    if (frame >= 60 && frame < WarmFrames)
                    {
                        idleFrames++;                                                     // idle control: no load yet
                        idleOver += Stopwatch.GetElapsedTime(t0, now).TotalMilliseconds > FrameLimitMs ? 1 : 0;
                    }

                    if (loadStart >= 0 && (loadEnd < 0 || frame == loadEnd))
                    {
                        double ms = Stopwatch.GetElapsedTime(t0, now).TotalMilliseconds;   // frames from the decode start to the last upload
                        intervals.Add(ms);
                        if (ms > FrameLimitMs)
                        {
                            slow.Add(Report.Fmt($"frame {frame - loadStart}: {ms:F1} ms = upload {up.CpuMilliseconds:F1} ({up.Tiles} tiles, {up.Allocations} new) + draw {Stopwatch.GetElapsedTime(td, ts).TotalMilliseconds:F1} + swap {Stopwatch.GetElapsedTime(ts, now).TotalMilliseconds:F1}"));
                        }
                    }

                    if (loadEnd >= 0 && frame == loadEnd)
                    {
                        (ulong Idle, ulong Total, TimeSpan Own) cpu1 = CpuSample();
                        double total = cpu1.Total - cpu0.Total;
                        machineBusy = total > 0 ? 100.0 * (1 - ((cpu1.Idle - cpu0.Idle) / total)) : double.NaN;
                        ownBusy = total > 0 ? 100.0 * (cpu1.Own - cpu0.Own).Ticks / total : double.NaN;
                    }

                    t0 = now;
                    if (loadEnd >= 0 && frame >= loadEnd + TailFrames)
                    {
                        break;
                    }
                }

                Assert.Null(workers.Fault);
            }

            Assert.True(loadEnd >= 0, $"run {run}: the load did not finish ({sched.UploadedTiles} of {live.Length} tiles)");
            if (run == 0)
            {
                // Every tile on the GPU equals a fresh CPU decode (the copy is right, not only fast).
                var buf = new byte[h.TileRgbaBytes];
                foreach (int t in live)
                {
                    img.DecodeTile(t, scratch.Value!, buf);
                    Assert.True(buf.AsSpan().SequenceEqual(cache.ReadBack(Key(run, t))), $"tile {t} differs on the GPU");
                }
            }

            double[] iv = [.. intervals.Order()];
            int runOver = iv.Count(x => x > FrameLimitMs);
            over += runOver;
            allLoad.AddRange(intervals);
            output.WriteLine(
                Report.Fmt($"run {run}: load frames {intervals.Count}, decode start to last tile {Stopwatch.GetElapsedTime(decodeStart, lastTile).TotalMilliseconds:F1} ms; ") +
                Report.Fmt($"frame p50 {Pct(iv, 0.5):F2} / p99 {Pct(iv, 0.99):F2} / max {iv[^1]:F2} ms; over {FrameLimitMs} ms: {runOver}; ") +
                Report.Fmt($"upload CPU per frame max {uploadMs.Max():F2} ms; staging peak {pool.PeakTotalBytes >> 20} MiB, waits {pool.Waits}; ") +
                Report.Fmt($"textures {cache.Stats.Resident} ({cache.Stats.ResidentBytes >> 20} MiB), allocated {cache.Stats.Allocations}, recycled {cache.Stats.Recycles}; machine CPU busy {machineBusy:F0} % (this process {ownBusy:F0} %)"));
            foreach (string line in slow)
            {
                output.WriteLine("  over the limit: " + line);
            }
        }

        double[] all = [.. allLoad.Order()];
        output.WriteLine(Report.Fmt($"all runs: {all.Length} load frames, p50 {Pct(all, 0.5):F2}, max {all[^1]:F2} ms, over {FrameLimitMs} ms: {over}; idle control (warm-up frames 60..{WarmFrames - 1}): {idleOver} of {idleFrames} over; GL {ctx.Device.Info.Version}; watchdog {watchdog.Elapsed.TotalSeconds:F1} s"));
        output.WriteLine(prewarm ? "textures pre-created over the warm-up frames" : "textures allocated lazily at upload");
        Assert.Null(ctx.Device.TakeError());
        Assert.True(all.Length > 0);
        if (assert)
        {
            Assert.Equal(0, over);
        }
    }

    private static TextureRef Key(int run, int tile) => TextureRef.ForPictureTile(1, 1259 + (run * 100000), tile);

    /// <summary>Every resident tile on its inner rectangle, the picture fitted into the frame (S4's draw).</summary>
    private static void BuildList(DisplayList list, long frame, ImageHeader h, int[] live, int run, TextureCache cache)
    {
        list.Reset(frame);
        list.Add(Quads.Fill(0, 0, 1920, 1080, Quads.Rgba(16, 16, 32, 255), DrawEffect.Normal, blendState: 0));
        float s = MathF.Min(1920f / h.DisplayWidth, 1080f / h.DisplayHeight);
        foreach (int t in live)
        {
            TextureRef key = Key(run, t);
            if (!cache.Contains(key))
            {
                continue;
            }

            var (sx, sy, w, ht, x, y) = h.TileWindow(t);
            list.Add(Quads.Textured(key, x * s, y * s, (x + w) * s, (y + ht) * s,
                (float)sx / h.TileWidth, (float)sy / h.TileHeight, (float)(sx + w) / h.TileWidth, (float)(sy + ht) / h.TileHeight,
                Quads.White(), DrawEffect.Normal));
        }

        list.Seal();
    }

    private static (ulong Idle, ulong Total, TimeSpan Own) CpuSample()
    {
        (ulong idle, ulong total) = CpuTopology.SystemTimes() ?? (0, 0);
        using var p = Process.GetCurrentProcess();
        return (idle, total, p.TotalProcessorTime);
    }

    private static double Pct(double[] sorted, double q) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(q * (sorted.Length - 1)))];
}

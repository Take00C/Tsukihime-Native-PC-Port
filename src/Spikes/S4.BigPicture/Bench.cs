// Spike S4: CPU-side measurements (read, MZX0, conversion, workers, memory, SHA-1 vs the oracle).
using System.Diagnostics;

namespace S4.BigPicture;

internal static unsafe class Bench
{
    public static readonly int[] WorkerCounts = [1, 2, 4, 8, 16];

    private static double Ms(long t0) => Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

    private static double Median(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToArray();
        return a.Length == 0 ? double.NaN : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    private static double R(double v) => Math.Round(v, 2);

    public static Dictionary<string, object?> Memory()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        var gi = GC.GetGCMemoryInfo(GCKind.Any);
        return new()
        {
            ["peak_working_set_mb"] = R(p.PeakWorkingSet64 / 1048576.0),
            ["peak_private_mb"] = R(p.PeakPagedMemorySize64 / 1048576.0),
            ["working_set_mb"] = R(p.WorkingSet64 / 1048576.0),
            ["managed_heap_mb"] = R(GC.GetTotalMemory(false) / 1048576.0),
            ["gc_heap_size_mb"] = R(gi.HeapSizeBytes / 1048576.0),
            ["native_live_mb"] = R(NativeBuffer.LiveBytes / 1048576.0),
            ["native_peak_mb"] = R(NativeBuffer.PeakBytes / 1048576.0),
            ["gc_counts"] = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
            ["gc_total_pause_ms"] = R(GC.GetTotalPauseDuration().TotalMilliseconds),
            ["gc_last_pause_ms"] = gi.PauseDurations.Length > 0 ? R(gi.PauseDurations[0].TotalMilliseconds) : 0.0,
        };
    }

    /// <summary>Full measurement of one picture (spikes.md S4 rows 1-5, 8).</summary>
    public static Dictionary<string, object?> Picture(Game game, Oracle? oracle, string arcName, int index, int reps)
    {
        var res = new Dictionary<string, object?>();
        Triplet arc = game.Archive(arcName);
        HedEntry e = arc.Entries[index];
        res["arc"] = arcName;
        res["index"] = index;
        res["name"] = e.Name;
        res["entry_bytes"] = e.Size;
        res["memory_start"] = Memory();

        // -- read: first read in this process, then warm (file cache) reads, then uncached (disk) reads
        var readMs = new List<double>();
        byte[] entry = GC.AllocateUninitializedArray<byte>(checked((int)e.Size)); // one buffer reused by every read
        for (int r = 0; r < Math.Max(3, reps); r++)
        {
            long t0 = Stopwatch.GetTimestamp();
            arc.Read(index, entry);
            readMs.Add(Ms(t0));
        }
        var uncached = new List<double>();
        for (int r = 0; r < 3; r++)
            uncached.Add(arc.ReadUncached(index));
        res["read_ms_first"] = R(readMs[0]);
        res["read_ms_cached_median"] = R(Median(readMs.Skip(1)));
        res["read_ms_uncached"] = uncached.Select(R).ToArray();
        res["read_mb_s_uncached_median"] = R(e.Size / 1048576.0 / (Median(uncached) / 1000));

        var pic = global::S4.BigPicture.Picture.FromEntry(arcName, index, e.Name, entry);
        ImageHeader h = pic.Header;
        int[] live = pic.LiveTiles;
        long mzxOut = pic.MzxOutBytes();
        res["header"] = h.Describe();
        res["format"] = h.Format.ToString();
        res["stored"] = new[] { h.Width, h.Height };
        res["shown"] = new[] { h.OutW, h.OutH };
        res["tile"] = new[] { h.TileW, h.TileH };
        res["tiles"] = h.Tiles;
        res["live_tiles"] = live.Length;
        res["mzx_out_bytes"] = mzxOut;
        res["rgba_shown_bytes"] = 4L * h.OutW * h.OutH;
        res["rgba_tile_bytes"] = live.Length * pic.TileRgbaBytes;

        // -- tile buffers (one stored tile each, the texture upload source); allocated, not yet touched
        var tiles = new Dictionary<int, NativeBuffer>();
        foreach (int t in live)
            tiles[t] = new NativeBuffer(pic.TileRgbaBytes);
        nint TileOut(int t) => (nint)tiles[t].Ptr;

        // -- cold decode: first decode in the process (JIT, fresh pages), 16 workers, as the first scene load
        int gc0 = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        var pause0 = GC.GetTotalPauseDuration();
        long tc = Stopwatch.GetTimestamp();
        pic.DecodeAll(16, TileOut);
        res["decode_ms_cold_16w"] = R(Ms(tc));
        res["memory_after_cold_decode"] = Memory();

        // -- warm decode with 1..16 workers
        var byWorkers = new Dictionary<string, object?>();
        foreach (int w in WorkerCounts)
        {
            var ms = new List<double>();
            for (int r = 0; r < reps; r++)
            {
                long t0 = Stopwatch.GetTimestamp();
                pic.DecodeAll(w, TileOut);
                ms.Add(Ms(t0));
            }
            byWorkers[w.ToString()] = new Dictionary<string, object?> { ["min"] = R(ms.Min()), ["median"] = R(Median(ms)), ["max"] = R(ms.Max()) };
        }
        res["decode_ms_by_workers"] = byWorkers;
        res["gc_during_decode"] = new Dictionary<string, object?>
        {
            ["collections"] = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2) - gc0,
            ["pause_ms"] = R((GC.GetTotalPauseDuration() - pause0).TotalMilliseconds),
        };

        // -- single-thread split: MZX0 only, then conversion only
        var raw = new Dictionary<int, NativeBuffer>();
        foreach (int t in live)
            raw[t] = new NativeBuffer(h.TileBytes + Mzx.Overrun);
        foreach (int t in live)
            pic.Decompress(t, raw[t].Ptr); // touch pages once
        var mzxMs = new List<double>();
        var convMs = new List<double>();
        for (int r = 0; r < reps; r++)
        {
            long t0 = Stopwatch.GetTimestamp();
            foreach (int t in live)
                pic.Decompress(t, raw[t].Ptr);
            mzxMs.Add(Ms(t0));
            t0 = Stopwatch.GetTimestamp();
            foreach (int t in live)
                TileDecoder.Convert(h, raw[t].Ptr, (uint*)tiles[t].Ptr);
            convMs.Add(Ms(t0));
        }
        foreach (var b in raw.Values)
            b.Dispose();
        double mzxBest = mzxMs.Min();
        res["st_mzx_ms"] = new Dictionary<string, object?> { ["min"] = R(mzxBest), ["median"] = R(Median(mzxMs)) };
        res["st_mzx_mb_s_out"] = R(mzxOut / 1048576.0 / (mzxBest / 1000));
        res["st_mzx_mb_s_in"] = R(live.Sum(t => (long)pic.Sections.Sizes[1 + t]) / 1048576.0 / (mzxBest / 1000));
        res["st_convert_ms"] = new Dictionary<string, object?> { ["min"] = R(convMs.Min()), ["median"] = R(Median(convMs)) };
        res["st_convert_mpix_s"] = R(live.Length * (double)h.TilePixels / 1e6 / (convMs.Min() / 1000));

        // -- totals: read (cached) + decode
        double st = ((Dictionary<string, object?>)byWorkers["1"]!)["median"] is double d1 ? d1 : double.NaN;
        double p16 = ((Dictionary<string, object?>)byWorkers["16"]!)["median"] is double d16 ? d16 : double.NaN;
        res["total_ms_read_cached_plus_decode_1w"] = R(Median(readMs.Skip(1)) + st);
        res["total_ms_read_cached_plus_decode_16w"] = R(Median(readMs.Skip(1)) + p16);
        res["total_ms_read_uncached_plus_decode_16w"] = R(Median(uncached) + p16);

        // -- SHA-1 of the displayed RGBA vs the Python oracle
        string sha = pic.AssembleSha1(TileOut, out double asmMs);
        res["sha1"] = sha;
        res["assemble_ms"] = R(asmMs);
        if (oracle is not null && oracle.Pictures.TryGetValue((arcName, index), out var op))
        {
            res["oracle_sha1"] = op.Sha1;
            res["sha1_equal"] = op.Sha1 == sha;
            res["oracle_py_ms"] = op.PyMs;
        }
        foreach (var b in tiles.Values)
            b.Dispose();
        res["memory_end"] = Memory();
        return res;
    }

    /// <summary>Decodes every oracle picture once and compares SHA-1 (format coverage).</summary>
    public static Dictionary<string, object?> VerifyAll(Game game, Oracle oracle)
    {
        var rows = new List<Dictionary<string, object?>>();
        int ok = 0, bad = 0;
        foreach (var op in oracle.Pictures.Values.OrderBy(p => p.Arc).ThenBy(p => p.Index))
        {
            var pic = global::S4.BigPicture.Picture.Load(game.Archive(op.Arc), op.Index);
            var tiles = new Dictionary<int, NativeBuffer>();
            foreach (int t in pic.LiveTiles)
                tiles[t] = new NativeBuffer(pic.TileRgbaBytes);
            long t0 = Stopwatch.GetTimestamp();
            pic.DecodeAll(16, t => (nint)tiles[t].Ptr);
            double ms = Ms(t0);
            string sha = pic.AssembleSha1(t => (nint)tiles[t].Ptr, out _);
            foreach (var b in tiles.Values)
                b.Dispose();
            bool eq = sha == op.Sha1;
            if (eq) ok++; else bad++;
            rows.Add(new()
            {
                ["arc"] = op.Arc, ["index"] = op.Index, ["format"] = pic.Header.Format.ToString(),
                ["shown"] = new[] { pic.Header.OutW, pic.Header.OutH }, ["roles"] = op.Roles,
                ["sha1_equal"] = eq, ["sha1"] = sha, ["decode_ms_16w"] = R(ms), ["py_ms"] = op.PyMs,
            });
            if (!eq)
                Console.WriteLine($"MISMATCH {op.Arc}#{op.Index} {op.Name}: {sha} != {op.Sha1}");
        }
        var byFormat = rows.GroupBy(r => (string)r["format"]!).ToDictionary(g => g.Key, g => new Dictionary<string, object?>
        {
            ["pictures"] = g.Count(),
            ["sha1_equal"] = g.Count(r => (bool)r["sha1_equal"]!),
        });
        return new() { ["pictures"] = rows.Count, ["sha1_equal"] = ok, ["sha1_different"] = bad, ["by_format"] = byFormat, ["rows"] = rows };
    }

    /// <summary>Scene-load cost: read + decode all layers of a backdrop + 3 sprites, 1 and 16 workers.</summary>
    public static Dictionary<string, object?> Scene(Game game, Scene scene, int reps)
    {
        var pics = new List<Picture>();
        double readMs = 0;
        foreach (var l in scene.Layers)
        {
            long t0 = Stopwatch.GetTimestamp();
            pics.Add(global::S4.BigPicture.Picture.Load(game.Archive(l.Arc), l.Index));
            readMs += Ms(t0);
        }
        double uncachedMs = scene.Layers.Sum(l => game.Archive(l.Arc).ReadUncached(l.Index));
        var work = new List<(Picture P, int T)>();
        foreach (var p in pics)
            foreach (int t in p.LiveTiles)
                work.Add((p, t));
        var bufs = work.Select(w => new NativeBuffer(w.P.TileRgbaBytes)).ToArray();
        int scratchLen = pics.Max(p => p.Header.TileBytes) + Mzx.Overrun;

        double Run(int workers)
        {
            long t0 = Stopwatch.GetTimestamp();
            if (workers == 1)
            {
                using var s = new NativeBuffer(scratchLen);
                for (int i = 0; i < work.Count; i++)
                    work[i].P.DecodeTile(work[i].T, s.Ptr, (uint*)bufs[i].Ptr);
            }
            else
            {
                Parallel.For(0, work.Count, new ParallelOptions { MaxDegreeOfParallelism = workers },
                    () => new NativeBuffer(scratchLen),
                    (i, _, s) => { work[i].P.DecodeTile(work[i].T, s.Ptr, (uint*)bufs[i].Ptr); return s; },
                    s => s.Dispose());
            }
            return Ms(t0);
        }

        double cold = Run(16);
        var st = Enumerable.Range(0, reps).Select(_ => Run(1)).ToList();
        var p16 = Enumerable.Range(0, reps).Select(_ => Run(16)).ToList();
        foreach (var b in bufs)
            b.Dispose();
        long uploadBytes = work.Sum(w => w.P.TileRgbaBytes);
        return new()
        {
            ["label"] = scene.Label,
            ["bg_catalog"] = scene.BgCatalog,
            ["sprites"] = scene.Sprites,
            ["layers"] = scene.Layers.Select(l => $"{l.Arc}#{l.Index}").ToArray(),
            ["entry_bytes"] = pics.Sum(p => (long)p.Entry.Length),
            ["tiles"] = work.Count,
            ["upload_bytes"] = uploadBytes,
            ["shown_rgba_bytes"] = pics.Sum(p => 4L * p.Header.OutW * p.Header.OutH),
            ["read_ms"] = R(readMs),
            ["read_ms_uncached"] = R(uncachedMs),
            ["decode_ms_cold_16w"] = R(cold),
            ["decode_ms_1w_median"] = R(Median(st)),
            ["decode_ms_16w_median"] = R(Median(p16)),
            ["decode_ms_16w_max"] = R(p16.Max()),
        };
    }
}

// Spike S4: sliced texture upload while the main loop keeps presenting (ARCHITECTURE 4.3:
// one texture per stored tile, sampled on its inner rectangle; uploads sliced so that no
// presented frame waits more than one frame). SDL3 window + GL 3.3 core; the window closes
// itself (hard limit well under 60 s).
using System.Collections.Concurrent;
using System.Diagnostics;
using SDL;
using Silk.NET.OpenGL;
using static SDL.SDL3;

namespace S4.BigPicture;

internal sealed record UploadOptions
{
    public int TilesPerFrame { get; init; } = 4;
    /// <summary>Stop uploading in a frame once this much CPU time was spent (0 = no time cap).</summary>
    public double BudgetMs { get; init; }
    /// <summary>Stop uploading in a frame before this many MiB would be exceeded (0 = no byte cap); at least one tile per frame.</summary>
    public double BudgetMb { get; init; }
    public int Workers { get; init; } = 14;
    public bool LowPriorityWorkers { get; init; } = true;
    public bool Prealloc { get; init; }
    /// <summary>Create (and first-touch) every tile texture during the warm-up frames: a texture pool that already exists when the load starts.</summary>
    public bool TexPool { get; init; }
    /// <summary>Bounded, pre-touched CPU staging pool in MiB (0 = a fresh native buffer per decoded tile).</summary>
    public int StagingMb { get; init; }
    public bool Pbo { get; init; }
    public bool Verify { get; init; } = true;
    public int WarmFrames { get; init; } = 120;
    public int TailFrames { get; init; } = 60;
    public double TimeLimitS { get; init; } = 40;
    public int SwapInterval { get; init; } = 1;
    public int WindowW { get; init; } = 1280;
    public int WindowH { get; init; } = 720;
    public string Label { get; init; } = "";
}

internal static unsafe class GlUpload
{
    private sealed class Tex
    {
        public uint Id;
        public bool Uploaded;
    }

    private readonly record struct Ready(int Pic, int Tile, NativeBuffer Buf, long DecodedAt);

    private readonly record struct FrameRec(int Frame, string Phase, double Ms, int Tiles, double UploadCpuMs, double DrawMs, double SwapMs);

    private const string Vs = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        uniform vec4 uDst;
        uniform vec4 uSrc;
        out vec2 vUv;
        void main() {
            gl_Position = vec4(mix(uDst.xy, uDst.zw, aPos), 0.0, 1.0);
            vUv = mix(uSrc.xy, uSrc.zw, aPos);
        }
        """;

    private const string Fs = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uTex;
        out vec4 oColor;
        void main() { oColor = texture(uTex, vUv); }
        """;

    private static double Pct(List<double> xs, double q)
    {
        if (xs.Count == 0)
            return double.NaN;
        var a = xs.OrderBy(x => x).ToArray();
        double pos = q * (a.Length - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return Math.Round(a[lo] + (a[hi] - a[lo]) * (pos - lo), 3);
    }

    private static Dictionary<string, object?> Dist(List<double> xs) => new()
    {
        ["n"] = xs.Count,
        ["p50"] = Pct(xs, 0.5),
        ["p95"] = Pct(xs, 0.95),
        ["p99"] = Pct(xs, 0.99),
        ["max"] = xs.Count > 0 ? Math.Round(xs.Max(), 3) : double.NaN,
    };

    public static Dictionary<string, object?> Run(Game game, Oracle? oracle, IReadOnlyList<(string Arc, int Index)> layers, UploadOptions opt)
    {
        var res = new Dictionary<string, object?> { ["label"] = opt.Label, ["options"] = opt };
        // pictures are read and parsed before the window opens (the read is measured separately)
        var pics = layers.Select(l => Picture.Load(game.Archive(l.Arc), l.Index)).ToList();
        res["layers"] = pics.Select(p => $"{p.Archive}#{p.Index}").ToArray();

        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
            throw new InvalidOperationException("SDL_Init: " + SDL_GetError());
        SDL_Window* win = null;
        SDL_GLContextState* ctx = null;
        try
        {
            SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MAJOR_VERSION, 3);
            SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MINOR_VERSION, 3);
            SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_PROFILE_MASK, (int)SDL_GLProfile.SDL_GL_CONTEXT_PROFILE_CORE);
            SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_FLAGS, (int)SDL_GLContextFlag.SDL_GL_CONTEXT_FORWARD_COMPATIBLE_FLAG);
            SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_DOUBLEBUFFER, 1);
            win = SDL_CreateWindow("Tsuki spike S4 (closes itself)", opt.WindowW, opt.WindowH, SDL_WindowFlags.SDL_WINDOW_OPENGL);
            if (win == null)
                throw new InvalidOperationException("SDL_CreateWindow: " + SDL_GetError());
            ctx = SDL_GL_CreateContext(win);
            if (ctx == null)
                throw new InvalidOperationException("SDL_GL_CreateContext: " + SDL_GetError());
            SDL_GL_MakeCurrent(win, ctx);
            SDL_GL_SetSwapInterval(opt.SwapInterval);
            var gl = GL.GetApi(name => SDL_GL_GetProcAddress(name));
            res["gl_version"] = Str(gl.GetString(StringName.Version));
            res["gl_renderer"] = Str(gl.GetString(StringName.Renderer));
            RunLoop(gl, win, pics, opt, oracle, res);
        }
        finally
        {
            if (ctx != null)
                SDL_GL_DestroyContext(ctx);
            if (win != null)
                SDL_DestroyWindow(win);
            SDL_Quit();
        }
        return res;
    }

    private static string Str(byte* p) => p == null ? "" : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)p) ?? "";

    private static uint Compile(GL gl, ShaderType type, string src)
    {
        uint s = gl.CreateShader(type);
        gl.ShaderSource(s, src);
        gl.CompileShader(s);
        gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
            throw new InvalidOperationException("shader: " + gl.GetShaderInfoLog(s));
        return s;
    }

    private static void RunLoop(GL gl, SDL_Window* win, List<Picture> pics, UploadOptions opt, Oracle? oracle, Dictionary<string, object?> res)
    {
        // -- pipeline objects
        uint prog = gl.CreateProgram();
        uint vs = Compile(gl, ShaderType.VertexShader, Vs), fs = Compile(gl, ShaderType.FragmentShader, Fs);
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
            throw new InvalidOperationException("link: " + gl.GetProgramInfoLog(prog));
        int uDst = gl.GetUniformLocation(prog, "uDst"), uSrc = gl.GetUniformLocation(prog, "uSrc"), uTex = gl.GetUniformLocation(prog, "uTex");
        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        uint vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        float[] quad = [0, 0, 1, 0, 0, 1, 1, 1];
        fixed (float* q = quad)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * 4), q, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 8, (void*)0);
        gl.UseProgram(prog);
        gl.Uniform1(uTex, 0);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
        gl.Viewport(0, 0, (uint)opt.WindowW, (uint)opt.WindowH);

        // PBO ring for the optional asynchronous path
        const int PboRing = 64;
        uint[] pbos = [];
        int pboNext = 0;
        if (opt.Pbo)
        {
            pbos = new uint[PboRing];
            for (int i = 0; i < PboRing; i++)
                pbos[i] = gl.GenBuffer();
        }

        var tex = pics.Select(p => new Tex?[p.Header.Tiles]).ToArray();
        int totalTiles = pics.Sum(p => p.LiveTiles.Length);
        long totalBytes = pics.Sum(p => p.LiveTiles.Length * p.TileRgbaBytes);
        res["tiles"] = totalTiles;
        res["upload_bytes"] = totalBytes;

        // GPU timer queries around the upload block of each frame
        const int QRing = 8;
        uint[] queries = new uint[QRing];
        for (int i = 0; i < QRing; i++)
            queries[i] = gl.GenQuery();
        var qFrame = new int[QRing];
        Array.Fill(qFrame, -1);

        var queue = new ConcurrentQueue<Ready>();
        Thread[]? workers = null;
        long decodeStart = 0, decodeEnd = 0;
        int decodedCount = 0;

        // CPU staging: fresh native buffers per tile, or a bounded pre-touched pool
        long maxTileBytes = pics.Max(p => p.TileRgbaBytes);
        BlockingCollection<NativeBuffer>? staging = null;
        var stagingAll = new List<NativeBuffer>();
        if (opt.StagingMb > 0)
        {
            staging = new BlockingCollection<NativeBuffer>(new ConcurrentBag<NativeBuffer>());
            int count = (int)Math.Max(1, opt.StagingMb * 1048576L / maxTileBytes);
            for (int i = 0; i < count; i++)
            {
                var b = new NativeBuffer(maxTileBytes);
                System.Runtime.InteropServices.NativeMemory.Clear(b.Ptr, (nuint)maxTileBytes);
                stagingAll.Add(b);
                staging.Add(b);
            }
        }
        res["staging_buffers"] = stagingAll.Count;
        NativeBuffer Acquire(long bytes) => staging is not null ? staging.Take() : new NativeBuffer(bytes);
        void Release(NativeBuffer b)
        {
            if (staging is not null)
                staging.Add(b);
            else
                b.Dispose();
        }

        // GPU texture pool: every tile texture created and first-touched during the warm-up
        var poolTodo = new Queue<(int P, int T)>();
        if (opt.TexPool)
            for (int p = 0; p < pics.Count; p++)
                foreach (int t in pics[p].LiveTiles)
                    poolTodo.Enqueue((p, t));
        using var zeros = new NativeBuffer(maxTileBytes);
        System.Runtime.InteropServices.NativeMemory.Clear(zeros.Ptr, (nuint)maxTileBytes);
        double poolMs = 0;
        int poolLeftAtLoad = -1;

        var frameMs = new List<FrameRec>();
        var tileUploadMs = new List<double>();
        var latencyMs = new List<double>();
        var gpuMs = new Dictionary<int, double>();
        double prealloMs = 0;
        int uploadedTiles = 0, frame = 0, loadStartFrame = -1, loadEndFrame = -1;
        long tStart = Stopwatch.GetTimestamp(), tLast = tStart, loadEndTs = 0;
        bool aborted = false;
        float scaleWin = 1f;
        SystemCpu.Sample cpuWarm0 = default, cpuLoad0 = default, cpuLoad1 = default;

        while (true)
        {
            SDL_Event ev;
            while (SDL_PollEvent(&ev))
                if ((SDL_EventType)ev.type == SDL_EventType.SDL_EVENT_QUIT)
                    aborted = true;
            if (aborted || Stopwatch.GetElapsedTime(tStart).TotalSeconds > opt.TimeLimitS)
            {
                aborted = true;
                break;
            }

            string phase = loadStartFrame < 0 ? "warm" : loadEndFrame < 0 ? "load" : "tail";
            if (frame == 10)
                cpuWarm0 = SystemCpu.Now();
            if (frame >= 10 && loadStartFrame < 0 && poolTodo.Count > 0)
            {
                long tp = Stopwatch.GetTimestamp();
                for (int k = 0; k < 16 && poolTodo.Count > 0; k++)
                {
                    var (p, t) = poolTodo.Dequeue();
                    var tx = CreateTexture(gl, pics[p].Header.TileW, pics[p].Header.TileH, null);
                    gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)pics[p].Header.TileW, (uint)pics[p].Header.TileH,
                        Silk.NET.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, zeros.Ptr);
                    tx.Uploaded = false;
                    tex[p][t] = tx;
                }
                poolMs += Stopwatch.GetElapsedTime(tp).TotalMilliseconds;
            }
            // start the load after the warm-up frames
            if (frame == opt.WarmFrames)
            {
                loadStartFrame = frame;
                phase = "load";
                cpuLoad0 = SystemCpu.Now();
                poolLeftAtLoad = poolTodo.Count;
                if (opt.Prealloc)
                {
                    long tp = Stopwatch.GetTimestamp();
                    for (int p = 0; p < pics.Count; p++)
                        foreach (int t in pics[p].LiveTiles)
                            tex[p][t] = CreateTexture(gl, pics[p].Header.TileW, pics[p].Header.TileH, null);
                    prealloMs = Stopwatch.GetElapsedTime(tp).TotalMilliseconds;
                }
                decodeStart = Stopwatch.GetTimestamp();
                workers = StartWorkers(pics, opt, queue, Acquire, () =>
                {
                    if (Interlocked.Increment(ref decodedCount) == totalTiles)
                        Interlocked.Exchange(ref decodeEnd, Stopwatch.GetTimestamp());
                });
            }

            // -- sliced upload
            int slot = frame % QRing;
            if (qFrame[slot] >= 0)
            {
                gl.GetQueryObject(queries[slot], QueryObjectParameterName.Result, out ulong ns);
                gpuMs[qFrame[slot]] = ns / 1e6;
                qFrame[slot] = -1;
            }
            gl.BeginQuery(QueryTarget.TimeElapsed, queries[slot]);
            qFrame[slot] = frame;
            long tu = Stopwatch.GetTimestamp();
            int n = 0;
            long bytesThisFrame = 0;
            if (opt.TilesPerFrame == 0)
            {
                // decode-only control run: drop the decoded tiles, upload nothing
                while (queue.TryDequeue(out var d))
                {
                    Release(d.Buf);
                    uploadedTiles++;
                }
            }
            while (n < opt.TilesPerFrame)
            {
                if (opt.BudgetMs > 0 && n > 0 && Stopwatch.GetElapsedTime(tu).TotalMilliseconds >= opt.BudgetMs)
                    break;
                if (opt.BudgetMb > 0 && n > 0 && queue.TryPeek(out Ready nx)
                    && bytesThisFrame + pics[nx.Pic].TileRgbaBytes > opt.BudgetMb * 1048576.0)
                    break;
                if (!queue.TryDequeue(out Ready r))
                    break;
                long t1 = Stopwatch.GetTimestamp();
                Picture pic = pics[r.Pic];
                bytesThisFrame += pic.TileRgbaBytes;
                int tw = pic.Header.TileW, th = pic.Header.TileH;
                if (opt.Pbo)
                {
                    uint pbo = pbos[pboNext];
                    pboNext = (pboNext + 1) % PboRing;
                    gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, pbo);
                    long len = pic.TileRgbaBytes;
                    gl.BufferData(BufferTargetARB.PixelUnpackBuffer, (nuint)len, null, BufferUsageARB.StreamDraw);
                    void* map = gl.MapBufferRange(BufferTargetARB.PixelUnpackBuffer, 0, (nuint)len,
                        MapBufferAccessMask.WriteBit | MapBufferAccessMask.InvalidateBufferBit);
                    System.Buffer.MemoryCopy(r.Buf.Ptr, map, len, len);
                    gl.UnmapBuffer(BufferTargetARB.PixelUnpackBuffer);
                    Upload(gl, ref tex[r.Pic][r.Tile], tw, th, null);
                    gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
                }
                else
                {
                    Upload(gl, ref tex[r.Pic][r.Tile], tw, th, r.Buf.Ptr);
                }
                Release(r.Buf);
                long t2 = Stopwatch.GetTimestamp();
                tileUploadMs.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
                latencyMs.Add(Stopwatch.GetElapsedTime(r.DecodedAt, t2).TotalMilliseconds);
                n++;
                uploadedTiles++;
            }
            double uploadCpu = Stopwatch.GetElapsedTime(tu).TotalMilliseconds;
            gl.EndQuery(QueryTarget.TimeElapsed);
            if (loadStartFrame >= 0 && loadEndFrame < 0 && uploadedTiles == totalTiles)
            {
                loadEndFrame = frame;
                loadEndTs = Stopwatch.GetTimestamp();
                cpuLoad1 = SystemCpu.Now();
            }

            // -- draw every uploaded tile (all pictures fitted into the window, back to front)
            long td = Stopwatch.GetTimestamp();
            gl.ClearColor(0.1f, 0.1f, 0.12f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            for (int p = 0; p < pics.Count; p++)
            {
                ImageHeader h = pics[p].Header;
                scaleWin = Math.Min((float)opt.WindowW / h.OutW, (float)opt.WindowH / h.OutH);
                foreach (int t in pics[p].LiveTiles)
                {
                    Tex? tx = tex[p][t];
                    if (tx is null || !tx.Uploaded)
                        continue;
                    int x0 = t % h.TilesX * h.InnerW, y0 = t / h.TilesX * h.InnerH;
                    int cw = Math.Min(h.InnerW, h.OutW - x0), ch = Math.Min(h.InnerH, h.OutH - y0);
                    if (cw <= 0 || ch <= 0)
                        continue;
                    float px0 = x0 * scaleWin, py0 = y0 * scaleWin, px1 = (x0 + cw) * scaleWin, py1 = (y0 + ch) * scaleWin;
                    gl.Uniform4(uDst, px0 / opt.WindowW * 2 - 1, 1 - py0 / opt.WindowH * 2, px1 / opt.WindowW * 2 - 1, 1 - py1 / opt.WindowH * 2);
                    float b = h.Border;
                    gl.Uniform4(uSrc, b / h.TileW, b / h.TileH, (b + cw) / h.TileW, (b + ch) / h.TileH);
                    gl.BindTexture(TextureTarget.Texture2D, tx.Id);
                    gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
                }
            }
            long ts = Stopwatch.GetTimestamp();
            SDL_GL_SwapWindow(win);
            long now = Stopwatch.GetTimestamp();
            frameMs.Add(new FrameRec(frame, phase, Stopwatch.GetElapsedTime(tLast, now).TotalMilliseconds, n, uploadCpu,
                Stopwatch.GetElapsedTime(td, ts).TotalMilliseconds, Stopwatch.GetElapsedTime(ts, now).TotalMilliseconds));
            tLast = now;
            frame++;
            if (loadEndFrame >= 0 && frame > loadEndFrame + opt.TailFrames)
                break;
        }

        // drain the GPU queries
        gl.Finish();
        for (int i = 0; i < QRing; i++)
            if (qFrame[i] >= 0)
            {
                gl.GetQueryObject(queries[i], QueryObjectParameterName.Result, out ulong ns);
                gpuMs[qFrame[i]] = ns / 1e6;
            }
        if (workers is not null)
            foreach (var w in workers)
                w.Join();
        while (queue.TryDequeue(out var left))
            Release(left.Buf);
        foreach (var b in stagingAll)
            b.Dispose();
        staging?.Dispose();

        // -- results
        var warm = frameMs.Where(f => f.Phase == "warm").Skip(60).Select(f => f.Ms).ToList();
        var load = frameMs.Where(f => f.Phase == "load").Select(f => f.Ms).ToList();
        var tail = frameMs.Where(f => f.Phase == "tail").Select(f => f.Ms).ToList();
        var loadUploadCpu = frameMs.Where(f => f.Phase == "load" && f.Tiles > 0).Select(f => f.UploadCpuMs).ToList();
        var loadGpu = frameMs.Where(f => f.Phase == "load" && f.Tiles > 0 && gpuMs.ContainsKey(f.Frame)).Select(f => gpuMs[f.Frame]).ToList();
        res["aborted"] = aborted;
        if (cpuLoad0.Kernel != 0)
            res["cpu_warm"] = SystemCpu.Between(cpuWarm0, cpuLoad0);
        if (cpuLoad1.Kernel != 0)
            res["cpu_load"] = SystemCpu.Between(cpuLoad0, cpuLoad1);
        res["frames_total"] = frame;
        res["refresh_ms_warm"] = Dist(warm);
        res["frame_ms_load"] = Dist(load);
        res["frame_ms_tail"] = Dist(tail);
        res["frames_over_16_7ms_load"] = load.Count(x => x > 16.7);
        res["frames_over_16_7ms_warm"] = warm.Count(x => x > 16.7);
        res["frames_over_16_7ms_after_warmup"] = frameMs.Skip(60).Count(f => f.Ms > 16.7);
        double vsyncMs = Pct(warm, 0.5);
        res["frames_over_1_5_vsync_load"] = load.Count(x => x > 1.5 * vsyncMs);
        res["upload_cpu_ms_per_frame"] = Dist(loadUploadCpu);
        res["upload_gpu_ms_per_frame"] = Dist(loadGpu);
        res["upload_cpu_ms_per_tile"] = Dist(tileUploadMs);
        res["decode_to_upload_latency_ms"] = Dist(latencyMs);
        res["prealloc_ms"] = Math.Round(prealloMs, 3);
        res["texpool_warmup_ms"] = Math.Round(poolMs, 3);
        res["texpool_left_at_load"] = poolLeftAtLoad;
        res["draw_ms_load"] = Dist(frameMs.Where(f => f.Phase == "load").Select(f => f.DrawMs).ToList());
        res["swap_ms_load"] = Dist(frameMs.Where(f => f.Phase == "load").Select(f => f.SwapMs).ToList());
        res["load_frames"] = loadEndFrame >= 0 ? loadEndFrame - loadStartFrame + 1 : -1;
        res["decode_wall_ms"] = decodeEnd > 0 ? Math.Round(Stopwatch.GetElapsedTime(decodeStart, decodeEnd).TotalMilliseconds, 2) : -1;
        res["load_wall_ms"] = loadEndTs > 0 ? Math.Round(Stopwatch.GetElapsedTime(decodeStart, loadEndTs).TotalMilliseconds, 2) : -1;
        res["mb_per_load_frame"] = loadEndFrame >= 0 ? Math.Round(totalBytes / 1048576.0 / (loadEndFrame - loadStartFrame + 1), 2) : -1;
        res["gpu_texture_mb"] = Math.Round(totalBytes / 1048576.0, 1);
        res["memory_after_upload"] = Bench.Memory();
        res["worst_warm_frames"] = frameMs.Where(f => f.Phase == "warm").Skip(60).OrderByDescending(f => f.Ms).Take(5)
            .Select(f => new { f.Frame, ms = Math.Round(f.Ms, 2) }).ToArray();
        res["worst_load_frames"] = frameMs.Where(f => f.Phase == "load").OrderByDescending(f => f.Ms).Take(5)
            .Select(f => new
            {
                f.Frame, ms = Math.Round(f.Ms, 2), f.Tiles, upload_cpu_ms = Math.Round(f.UploadCpuMs, 2),
                draw_ms = Math.Round(f.DrawMs, 2), swap_ms = Math.Round(f.SwapMs, 2),
                gpu_ms = gpuMs.TryGetValue(f.Frame, out var g) ? Math.Round(g, 3) : -1,
            })
            .ToArray();

        // -- read back every tile texture and compare with the oracle
        if (opt.Verify && !aborted && opt.TilesPerFrame > 0)
        {
            var verify = new List<object>();
            for (int p = 0; p < pics.Count; p++)
            {
                Picture pic = pics[p];
                var back = new Dictionary<int, NativeBuffer>();
                long tr = Stopwatch.GetTimestamp();
                foreach (int t in pic.LiveTiles)
                {
                    var b = new NativeBuffer(pic.TileRgbaBytes);
                    gl.BindTexture(TextureTarget.Texture2D, tex[p][t]!.Id);
                    gl.GetTexImage(TextureTarget.Texture2D, 0, Silk.NET.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, b.Ptr);
                    back[t] = b;
                }
                double readbackMs = Stopwatch.GetElapsedTime(tr).TotalMilliseconds;
                string sha = pic.AssembleSha1(t => (nint)back[t].Ptr, out _);
                foreach (var b in back.Values)
                    b.Dispose();
                string? want = oracle is not null && oracle.Pictures.TryGetValue((pic.Archive, pic.Index), out var op) ? op.Sha1 : null;
                verify.Add(new { layer = $"{pic.Archive}#{pic.Index}", readback_sha1 = sha, oracle_sha1 = want, equal = want == sha, readback_ms = Math.Round(readbackMs, 1) });
            }
            res["readback_verify"] = verify;
        }

        for (int p = 0; p < pics.Count; p++)
            foreach (var tx in tex[p])
                if (tx is not null)
                    gl.DeleteTexture(tx.Id);
        foreach (uint q in queries)
            gl.DeleteQuery(q);
        foreach (uint b in pbos)
            gl.DeleteBuffer(b);
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
        gl.DeleteProgram(prog);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
    }

    private static Tex CreateTexture(GL gl, int w, int h, void* data)
    {
        var t = new Tex { Id = gl.GenTexture() };
        gl.BindTexture(TextureTarget.Texture2D, t.Id);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0,
            Silk.NET.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, data);
        t.Uploaded = data != null;
        return t;
    }

    /// <summary>Uploads one stored tile; <paramref name="data"/> null means "from the bound PBO at offset 0".</summary>
    private static void Upload(GL gl, ref Tex? t, int w, int h, void* data)
    {
        if (t is null)
        {
            // allocate + upload in one call (no preallocation)
            t = CreateTexture(gl, w, h, data);
            t.Uploaded = true;
            return;
        }
        gl.BindTexture(TextureTarget.Texture2D, t.Id);
        gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)w, (uint)h,
            Silk.NET.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, data);
        t.Uploaded = true;
    }

    /// <summary>Dedicated decode workers pulling tiles from a shared counter (the runtime's worker pool).</summary>
    private static Thread[] StartWorkers(List<Picture> pics, UploadOptions opt, ConcurrentQueue<Ready> queue, Func<long, NativeBuffer> acquire, Action onTile)
    {
        var work = new List<(int P, int T)>();
        for (int p = 0; p < pics.Count; p++)
            foreach (int t in pics[p].LiveTiles)
                work.Add((p, t));
        int next = -1;
        int scratchLen = pics.Max(p => p.Header.TileBytes) + Mzx.Overrun;
        var threads = new Thread[opt.Workers];
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                using var scratch = new NativeBuffer(scratchLen);
                int k;
                while ((k = Interlocked.Increment(ref next)) < work.Count)
                {
                    var (p, t) = work[k];
                    var buf = acquire(pics[p].TileRgbaBytes);
                    pics[p].DecodeTile(t, scratch.Ptr, (uint*)buf.Ptr);
                    queue.Enqueue(new Ready(p, t, buf, Stopwatch.GetTimestamp()));
                    onTile();
                }
            })
            {
                IsBackground = true,
                Name = $"s4-decode-{i}",
                Priority = opt.LowPriorityWorkers ? ThreadPriority.BelowNormal : ThreadPriority.Normal,
            };
            threads[i].Start();
        }
        return threads;
    }
}

// Spike S2: SDL3 window + GL 3.3 core, 60 Hz logic ticks that pick the movie frame from the audio clock,
// vsync present at the monitor rate (ARCHITECTURE 3.4, 6.2 default present mode, 7.5 audio clock master).
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SDL;
using Silk.NET.OpenGL;

namespace S2.MovieSync;

internal static unsafe class Player
{
    private const double TickHz = 60.0;

    public static Dictionary<string, object?> Run(Options o)
    {
        var report = new Dictionary<string, object?> { ["mode"] = "play" };
        if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_AUDIO))
        {
            throw new InvalidOperationException("SDL_Init: " + SDL3.SDL_GetError());
        }

        SDL_Window* window = null;
        SDL_GLContextState* glctx = null;
        MoviePipeline? movie = null;
        AudioOut? audio = null;
        YuvRenderer? renderer = null;
        try
        {
            SDL_DisplayMode* dm = SDL3.SDL_GetCurrentDisplayMode(SDL3.SDL_GetPrimaryDisplay());
            if (dm != null)
            {
                report["display_mode"] = $"{dm->w}x{dm->h}@{dm->refresh_rate.ToString("F2", CultureInfo.InvariantCulture)} density {dm->pixel_density.ToString("F2", CultureInfo.InvariantCulture)}";
            }

            SDL3.SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MAJOR_VERSION, 3);
            SDL3.SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MINOR_VERSION, 3);
            SDL3.SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_PROFILE_MASK, (int)SDL_GLProfile.SDL_GL_CONTEXT_PROFILE_CORE);
            SDL3.SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_DOUBLEBUFFER, 1);
            window = SDL3.SDL_CreateWindow("S2 MovieSync (closes itself)", o.WindowWidth, o.WindowHeight, SDL_WindowFlags.SDL_WINDOW_OPENGL);
            if (window == null)
            {
                throw new InvalidOperationException("SDL_CreateWindow: " + SDL3.SDL_GetError());
            }

            glctx = SDL3.SDL_GL_CreateContext(window);
            if (glctx == null)
            {
                throw new InvalidOperationException("SDL_GL_CreateContext: " + SDL3.SDL_GetError());
            }

            SDL3.SDL_GL_MakeCurrent(window, glctx);
            bool swapOk = SDL3.SDL_GL_SetSwapInterval(o.SwapInterval);
            int pw, ph;
            SDL3.SDL_GetWindowSizeInPixels(window, &pw, &ph);
            report["window_pixels"] = $"{pw}x{ph}";
            report["swap_interval"] = o.SwapInterval;
            report["swap_interval_ok"] = swapOk;

            var gl = GL.GetApi(name => SDL3.SDL_GL_GetProcAddress(name));
            report["gl_version"] = gl.GetStringS(StringName.Version);
            report["gl_renderer"] = gl.GetStringS(StringName.Renderer);

            movie = new MoviePipeline(o.Movie, o.Hw, o.Threads, o.Start, o.FrameBuffers);
            renderer = new YuvRenderer(gl, movie.Width, movie.Height, o.Upload == "pbo");
            report["glsl_es300_compile"] = renderer.EsSourceCheck();
            report["upload_path"] = o.Upload;
            report["decoder"] = movie.Video.CodecName;
            report["hw"] = o.Hw;
            report["hw_pix_fmt"] = movie.Video.HwPixFmtName;
            report["thread_count"] = movie.Video.ThreadCount;
            report["active_thread_type"] = movie.Video.ActiveThreadType;
            report["ffmpeg_worker_threads"] = movie.FfmpegThreadIds.Count;
            report["frame_buffers"] = o.FrameBuffers;
            report["probe"] = new Dictionary<string, object?>
            {
                ["format"] = movie.FormatName,
                ["size"] = $"{movie.Width}x{movie.Height}",
                ["fps"] = movie.Fps,
                ["nb_frames"] = movie.NbFrames,
                ["video_duration_s"] = Series.Round(movie.VideoDurationSeconds),
                ["audio_duration_s"] = Series.Round(movie.AudioDurationSeconds),
                ["pix_fmt"] = movie.PixFmt,
                ["profile_level"] = movie.Profile,
                ["color_primaries/trc/space/range"] = movie.ColorTags,
                ["audio"] = movie.Audio is { } a ? $"{a.CodecName} {a.InRate} Hz {a.InChannels} ch {a.InFormat}" : "none",
            };

            AudioOut? audioRef = null;
            movie.OnAudioEnd = () =>
            {
                if (audioRef != null)
                {
                    audioRef.EndOfStream = true;
                }
            };
            long tOpen = Stopwatch.GetTimestamp();
            movie.Start();

            // pre-roll: a few decoded frames and 250 ms of audio
            int wantFrames = Math.Min(4, o.FrameBuffers - 1);
            long deadline = tOpen + Stopwatch.Frequency * 15;
            while (movie.Frames.ReadyCount < wantFrames || (movie.HasAudio && movie.Pcm.BufferedFrames < AudioDecoder.OutRate / 4 && !movie.AudioEnded))
            {
                if (movie.Error != null)
                {
                    throw new InvalidOperationException("movie thread failed", movie.Error);
                }

                if (Stopwatch.GetTimestamp() > deadline)
                {
                    throw new TimeoutException("pre-roll did not complete in 15 s");
                }

                PumpEvents();
                gl.ClearColor(0, 0, 0, 1);
                gl.Clear(ClearBufferMask.ColorBufferBit);
                SDL3.SDL_GL_SwapWindow(window);
            }

            double prerollMs = Report.Ms(Stopwatch.GetTimestamp() - tOpen);

            // warm-up: first upload, draw and swap (shader and texture first use) outside the measured window
            long tWarm = Stopwatch.GetTimestamp();
            renderer.Upload(movie.Frames.Peek()!);
            renderer.Draw(pw, ph, timed: false);
            SDL3.SDL_GL_SwapWindow(window);
            gl.Finish();
            report["warmup_first_draw_ms"] = Series.Round(Report.Ms(Stopwatch.GetTimestamp() - tWarm));
            renderer.ResetStats();
            double basePts = movie.FirstAudioSample >= 0 ? movie.FirstAudioSample / (double)AudioDecoder.OutRate : o.Start;
            if (movie.HasAudio)
            {
                audio = new AudioOut(movie.Pcm, basePts);
                audioRef = audio;
                if (movie.AudioEnded)
                {
                    audio.EndOfStream = true;
                }

                report["audio_device"] = $"{audio.DeviceFormat} {audio.DeviceRate} Hz {audio.DeviceChannels} ch, buffer {audio.DeviceBufferFrames} frames";
                report["audio_assumed_latency_ms"] = Series.Round(audio.LatencySeconds * 1000);
            }

            var cpuStart = Native.ThreadCpuSnapshot();
            var sys0 = Native.SystemCpuSeconds();
            using var proc = Process.GetCurrentProcess();
            double procCpu0 = proc.TotalProcessorTime.TotalSeconds;
            int mainThreadId = (int)Native.GetCurrentThreadId();
            double mainCpu0 = Native.CurrentThreadCpuSeconds();
            long gc0 = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);

            var loop = new LoopState(movie, audio, o, basePts);
            long playStart = Stopwatch.GetTimestamp();
            loop.PlayStartTicks = playStart;
            audio?.Start();
            long nextTick = playStart;
            if (!double.IsNaN(o.TickPhaseMs) && audio != null)
            {
                // align the tick grid to the audio clock: the first tick samples the clock at base + phase
                double target = basePts + o.TickPhaseMs / 1000;
                long giveUp = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                while (Stopwatch.GetTimestamp() < giveUp)
                {
                    double c = audio.Clock(Stopwatch.GetTimestamp());
                    if (!double.IsNaN(c) && c >= target)
                    {
                        break;
                    }

                    Thread.SpinWait(200);
                }

                nextTick = Stopwatch.GetTimestamp();
                report["tick_phase_ms"] = o.TickPhaseMs;
            }

            report["cadence_lock_ms"] = o.CadenceLockMs;
            report["tick_clock"] = o.TickClock;
            long tickTicks = (long)(Stopwatch.Frequency / TickHz);
            long limitTicks = (long)(o.Seconds * Stopwatch.Frequency);
            long lastPresent = 0;
            VideoFrame? uploaded = null;
            string stopReason = "time";
            var frameTimes = new Series("ms");
            var workMs = new Series("ms");
            var presentCsv = new StringBuilder("present,t_s,interval_ms,work_ms,frame_idx\n");
            long presents = 0;
            bool readbackDone = o.Readback < 0;

            while (true)
            {
                if (!PumpEvents())
                {
                    stopReason = "user";
                    break;
                }

                if (movie.Error != null)
                {
                    stopReason = "error: " + movie.Error.Message;
                    break;
                }

                long now = Stopwatch.GetTimestamp();
                if (now - playStart > limitTicks)
                {
                    break;
                }

                while (now >= nextTick)
                {
                    loop.Tick(o.TickClock == "now" ? now : nextTick);
                    nextTick += tickTicks;
                    if (now - nextTick > 4 * tickTicks)
                    {
                        loop.SkippedTicks += (int)((now - nextTick) / tickTicks);
                        nextTick = now + tickTicks;
                    }
                }

                if (loop.Ended)
                {
                    stopReason = "movie ended";
                    break;
                }

                VideoFrame? cur = loop.Current;
                if (cur != null && !ReferenceEquals(cur, uploaded))
                {
                    renderer.Upload(cur);
                    uploaded = cur;
                    loop.UploadedIndex = cur.Index;
                    if (!readbackDone && cur.Index >= (long)Math.Round(o.Readback * movie.Fps))
                    {
                        readbackDone = true;
                        WriteReadback(o, renderer, cur);
                        report["readback_frame"] = cur.Index;
                    }
                }

                renderer.Draw(pw, ph, timed: true);
                long tWork = Stopwatch.GetTimestamp();
                SDL3.SDL_GL_SwapWindow(window);
                long tSwap = Stopwatch.GetTimestamp();
                loop.OnPresented(tSwap);
                presents++;
                double work = Report.Ms(tWork - now);
                workMs.Add(work);
                if (lastPresent != 0)
                {
                    double ft = Report.Ms(tSwap - lastPresent);
                    frameTimes.Add(ft);
                    presentCsv.Append(CultureInfo.InvariantCulture, $"{presents},{Report.F(Report.Seconds(tSwap - playStart), 6)},{Report.F(ft, 3)},{Report.F(work, 3)},{cur?.Index ?? -1}\n");
                }

                lastPresent = tSwap;
            }

            long playEnd = Stopwatch.GetTimestamp();
            double wall = Report.Seconds(playEnd - playStart);
            var cpuEnd = Native.ThreadCpuSnapshot();
            var sys1 = Native.SystemCpuSeconds();
            proc.Refresh();
            double procCpu = proc.TotalProcessorTime.TotalSeconds - procCpu0;
            double mainCpu = Native.CurrentThreadCpuSeconds() - mainCpu0;
            long gcCount = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2) - gc0;
            renderer.CollectQueries();

            double ThreadDelta(int id, double atExit)
            {
                double start = cpuStart.TryGetValue(id, out double s) ? s : 0;
                if (cpuEnd.TryGetValue(id, out double e))
                {
                    return e - start;
                }

                return double.IsNaN(atExit) ? 0 : atExit - start;
            }

            movie.Stop();
            double demuxCpu = ThreadDelta(movie.DemuxThreadId, movie.DemuxThreadCpuAtExit);
            double decodeCpu = ThreadDelta(movie.DecodeThreadId, movie.DecodeThreadCpuAtExit);
            double workersCpu = movie.FfmpegThreadIds.Sum(id => ThreadDelta(id, double.NaN));
            double audioCbCpu = audio != null && audio.CallbackThreadId != 0 ? ThreadDelta((int)audio.CallbackThreadId, double.NaN) : 0;
            double movieCpu = demuxCpu + decodeCpu + workersCpu;

            report["stop_reason"] = stopReason;
            report["start_s"] = o.Start;
            report["play_wall_s"] = Series.Round(wall);
            report["preroll_ms"] = Series.Round(prerollMs);
            report["audio_base_pts_s"] = Series.Round(basePts);
            loop.Fill(report);
            report["presents"] = presents;
            report["present_interval_ms"] = frameTimes.Summary();
            report["presents_over_16_7ms"] = frameTimes.CountAbove(16.7);
            report["main_loop_work_ms"] = workMs.Summary();
            report["upload_cpu_ms"] = renderer.UploadCpuMs.Summary();
            report["upload_gpu_ms"] = renderer.UploadGpuMs.Summary();
            report["draw_gpu_ms"] = renderer.DrawGpuMs.Summary();
            report["decode_call_ms_per_frame"] = movie.DecodeCallMs.Summary();
            report["hw_transfer_ms"] = o.Hw != "none" ? movie.TransferMs.Summary() : null;
            report["plane_copy_ms"] = movie.CopyMs.Summary();
            report["pool_wait_ms"] = movie.PoolWaitMs.Summary();
            report["frames_decoded"] = movie.FramesDecoded;
            report["frames_before_start_discarded"] = movie.FramesBeforeStart;
            report["video_packet_queue_max"] = movie.VideoPacketQueueMax;
            report["audio_samples_to_ring"] = movie.AudioSamplesOut;
            report["audio_samples_decoded"] = movie.AudioSamplesDecoded;
            if (audio != null)
            {
                report["audio_callbacks"] = audio.Callbacks;
                report["audio_request_frames_min_max"] = $"{audio.MinRequestFrames}/{audio.MaxRequestFrames}";
                report["audio_callback_interval_ms"] = audio.CallbackIntervalMs.Summary();
                report["audio_underrun_frames"] = audio.UnderrunFrames;
                report["audio_underrun_events"] = audio.UnderrunEvents;
                report["audio_media_frames_played"] = audio.MediaFramesPlayed;
            }

            report["cpu"] = new Dictionary<string, object?>
            {
                ["logical_cpus"] = Environment.ProcessorCount,
                ["process_cpu_s"] = Series.Round(procCpu),
                ["process_cores"] = Series.Round(procCpu / wall),
                ["process_percent_of_machine"] = Series.Round(procCpu / wall / Environment.ProcessorCount * 100),
                ["movie_cores (demux+aac + vdec thread + ffmpeg workers)"] = Series.Round(movieCpu / wall),
                ["demux_aac_thread_cores"] = Series.Round(demuxCpu / wall),
                ["vdec_thread_cores"] = Series.Round(decodeCpu / wall),
                ["ffmpeg_worker_cores"] = Series.Round(workersCpu / wall),
                ["main_thread_cores"] = Series.Round(mainCpu / wall),
                ["audio_callback_thread_cores"] = Series.Round(audioCbCpu / wall),
                ["other_cores (driver, GC, SDL, runtime)"] = Series.Round((procCpu - movieCpu - mainCpu - audioCbCpu) / wall),
                ["main_thread_id"] = mainThreadId,
                ["machine_busy_percent (all processes)"] = Series.Round((sys1.Busy - sys0.Busy) / ((sys1.Busy - sys0.Busy) + (sys1.Idle - sys0.Idle)) * 100),
                ["machine_busy_cores_other_processes"] = Series.Round(((sys1.Busy - sys0.Busy) - procCpu) / wall),
            };
            proc.Refresh();
            report["peak_working_set_mb"] = Series.Round(proc.PeakWorkingSet64 / 1048576.0);
            report["peak_paged_mb"] = Series.Round(proc.PeakPagedMemorySize64 / 1048576.0);
            report["gc_collections"] = gcCount;
            report["managed_heap_mb"] = Series.Round(GC.GetTotalMemory(false) / 1048576.0);

            Directory.CreateDirectory(o.OutDir);
            File.WriteAllText(Path.Combine(o.OutDir, o.Name + "_ticks.csv"), loop.TickCsv.ToString());
            File.WriteAllText(Path.Combine(o.OutDir, o.Name + "_presents.csv"), presentCsv.ToString());
            return report;
        }
        finally
        {
            audio?.Dispose();
            movie?.Dispose();
            renderer?.Dispose();
            if (glctx != null)
            {
                SDL3.SDL_GL_DestroyContext(glctx);
            }

            if (window != null)
            {
                SDL3.SDL_DestroyWindow(window);
            }

            SDL3.SDL_Quit();
        }
    }

    private static bool PumpEvents()
    {
        SDL_Event ev;
        bool keep = true;
        while (SDL3.SDL_PollEvent(&ev))
        {
            var t = (SDL_EventType)ev.type;
            if (t == SDL_EventType.SDL_EVENT_QUIT || t == SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
            {
                keep = false;
            }
        }

        return keep;
    }

    private static void WriteReadback(Options o, YuvRenderer renderer, VideoFrame f)
    {
        Directory.CreateDirectory(o.OutDir);
        byte[] rgb = renderer.ReadBackRgb();
        string stem = Path.Combine(o.OutDir, $"{o.Name}_frame{f.Index}");
        File.WriteAllBytes(stem + "_gl.rgb", rgb);
        using var fs = File.Create(stem + (f.Layout == PlaneLayout.Nv12 ? ".nv12" : ".yuv"));
        fs.Write(f.Y);
        if (f.Layout == PlaneLayout.Nv12)
        {
            fs.Write(f.C0);
        }
        else
        {
            fs.Write(f.C0.AsSpan(0, f.Width * f.Height / 4));
            fs.Write(f.C1);
        }

        Console.WriteLine($"readback: frame {f.Index} (pts {f.Pts:F4} s) -> {stem}_gl.rgb");
    }

    /// <summary>The 60 Hz logic side: frame selection from the clock, and every sync statistic.</summary>
    private sealed class LoopState(MoviePipeline movie, AudioOut? audio, Options o, double basePts)
    {
        private readonly double _frameDur = 1.0 / movie.Fps;
        private readonly Series _offset = new("ms");
        private readonly Series _syncErr = new("ms");
        private readonly Series _queueDepth = new("frames");
        private readonly Dictionary<long, int> _frameErr = [];
        private readonly Dictionary<int, int> _ticksPerFrame = [];
        private readonly Dictionary<int, int> _presentsPerFrame = [];
        private readonly List<long> _neverPresentedIdx = [];
        private readonly Dictionary<string, object?> _checkpoints = [];
        private readonly List<long> _droppedIdx = [];
        private readonly List<(double Wall, double Clock)> _clockSamples = [];
        private long _lastLateIdx = -1;
        private long _prevIdx = -1;
        private long _lastFramePresentTicks;
        private long _endTicks;
        private bool _pendingPresentStamp;
        private long _firstIdx = -1;
        private long _ticks;
        private long _playingTicks;
        private long _shownFrames;

        public long PlayStartTicks { get; set; }

        public VideoFrame? Current { get; private set; }

        public long UploadedIndex { get; set; } = -1;

        public bool Ended { get; private set; }

        public int SkippedTicks { get; set; }

        public long LateFrames { get; private set; }

        public long CadenceHolds { get; private set; }

        public long CadenceEarly { get; private set; }

        public long DroppedFrames { get; private set; }

        /// <summary>Frames selected by a tick but replaced before any swap showed them.</summary>
        public long NeverPresented { get; private set; }

        public StringBuilder TickCsv { get; } = new("tick,t_s,clock_s,frame_idx,frame_pts_s,offset_ms,sync_err_ms,queue_depth\n");

        public void Tick(long now)
        {
            _ticks++;
            double clock = audio != null ? audio.Clock(now) : basePts + Report.Seconds(now - PlayStartTicks);
            if (double.IsNaN(clock))
            {
                clock = basePts - 1; // no callback yet: hold the first frame
            }

            // advance to the newest frame that is due; a frame replaced before any tick showed it is dropped
            double lockS = o.CadenceLockMs / 1000;
            while (true)
            {
                VideoFrame? next = movie.Frames.Peek();
                if (next == null)
                {
                    break;
                }

                if (Current != null)
                {
                    bool due = next.Pts <= clock + 1e-9;
                    if (lockS > 0 && Current.Index + 1 == next.Index)
                    {
                        if (due && Current.TicksShown == 1 && next.Pts > clock - lockS)
                        {
                            due = false; // the boundary is within the lock window: give this frame its second tick
                            CadenceHolds++;
                        }
                        else if (!due && Current.TicksShown >= 2 && next.Pts <= clock + lockS)
                        {
                            due = true; // already had two ticks: switch up to lockS early
                            CadenceEarly++;
                        }
                    }

                    if (!due)
                    {
                        break;
                    }
                }

                movie.Frames.Dequeue();
                if (Current != null)
                {
                    Retire(Current);
                }

                Current = next;
            }

            if (Current == null)
            {
                return;
            }

            Current.TicksShown++;
            int depth = movie.Frames.ReadyCount;
            _queueDepth.Add(depth);
            string offsetCell = "", errCell = "";
            if (_firstIdx >= 0 || clock >= Current.Pts - 1e-9)
            {
                _playingTicks++;
                if (_firstIdx < 0)
                {
                    _firstIdx = Current.Index;
                    _checkpoints["first_frame"] = Checkpoint(Current, clock);
                }

                double off = (Current.Pts - clock) * 1000;
                double end = Current.Pts + _frameDur;
                // distance of the clock from the shown frame's display interval [pts, pts + 1/fps): late or early
                double err = clock >= end ? (clock - end) * 1000 : clock < Current.Pts ? (Current.Pts - clock) * 1000 : 0;
                long ideal = (long)Math.Floor(clock * movie.Fps + 1e-6);
                bool atEnd = movie.Frames.EndOfStream && depth == 0 && Current.Index == movie.LastVideoIndex;
                if (!(atEnd && clock >= end - 1e-9))
                {
                    // the tick that finds the clock past the last frame is the end event, not a sync sample
                    _offset.Add(off);
                    _syncErr.Add(err);
                }

                offsetCell = Report.F(off, 3);
                errCell = Report.F(err, 3);
                if (!atEnd)
                {
                    long fe = Current.Index - ideal;
                    _frameErr[fe] = _frameErr.GetValueOrDefault(fe) + 1;
                    if (ideal > Current.Index && depth == 0 && ideal != _lastLateIdx)
                    {
                        LateFrames++;
                        _lastLateIdx = ideal;
                    }
                }

                _clockSamples.Add((Report.Seconds(now - PlayStartTicks), clock));
                foreach (double t in new[] { 25.0, 60.0, 300.0, o.Start + 10, o.Start + 40 })
                {
                    string key = "t" + t.ToString("F0", CultureInfo.InvariantCulture) + "s";
                    if (!_checkpoints.ContainsKey(key) && Current.Index == (long)Math.Round(t * movie.Fps))
                    {
                        _checkpoints[key] = Checkpoint(Current, clock);
                    }
                }

                if (atEnd)
                {
                    _checkpoints.TryAdd("last_frame", Checkpoint(Current, clock));
                    if (clock >= end - 1e-9 && !Ended)
                    {
                        Ended = true;
                        _endTicks = now;
                    }
                }
            }

            if (Current.Index != _prevIdx)
            {
                _prevIdx = Current.Index;
                _pendingPresentStamp = true;
            }

            TickCsv.Append(CultureInfo.InvariantCulture,
                $"{_ticks},{Report.F(Report.Seconds(now - PlayStartTicks), 6)},{Report.F(clock, 6)},{Current.Index},{Report.F(Current.Pts, 6)},{offsetCell},{errCell},{depth}\n");
        }

        public void OnPresented(long t)
        {
            if (Current != null && UploadedIndex == Current.Index)
            {
                Current.PresentsShown++;
            }

            if (_pendingPresentStamp && Current != null && UploadedIndex == Current.Index)
            {
                _pendingPresentStamp = false;
                if (movie.Frames.EndOfStream && Current.Index == movie.LastVideoIndex)
                {
                    _lastFramePresentTicks = t;
                }
            }
        }

        private Dictionary<string, object?> Checkpoint(VideoFrame f, double clock) => new()
        {
            ["frame"] = f.Index,
            ["pts_s"] = Series.Round(f.Pts),
            ["clock_s"] = Series.Round(clock),
            ["offset_ms"] = Series.Round((f.Pts - clock) * 1000),
        };

        private void Retire(VideoFrame f)
        {
            if (f.PresentsShown == 0 && f.TicksShown > 0)
            {
                NeverPresented++;
                if (_neverPresentedIdx.Count < 50)
                {
                    _neverPresentedIdx.Add(f.Index);
                }
            }

            if (f.PresentsShown > 0)
            {
                _presentsPerFrame[f.PresentsShown] = _presentsPerFrame.GetValueOrDefault(f.PresentsShown) + 1;
            }

            if (f.TicksShown == 0)
            {
                DroppedFrames++;
                if (_droppedIdx.Count < 50)
                {
                    _droppedIdx.Add(f.Index);
                }
            }
            else
            {
                _shownFrames++;
                _ticksPerFrame[f.TicksShown] = _ticksPerFrame.GetValueOrDefault(f.TicksShown) + 1;
            }

            movie.Frames.Return(f);
        }

        public void Fill(Dictionary<string, object?> r)
        {
            r["ticks"] = _ticks;
            r["playing_ticks"] = _playingTicks;
            r["skipped_ticks"] = SkippedTicks;
            r["frames_shown_and_retired"] = _shownFrames;
            r["first_frame_index"] = _firstIdx;
            r["last_frame_index_current"] = Current?.Index;
            r["dropped_frames"] = DroppedFrames;
            r["selected_but_never_presented"] = NeverPresented;
            r["never_presented_indexes_first50"] = _neverPresentedIdx;
            r["presents_per_frame_histogram"] = _presentsPerFrame.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value);
            r["dropped_frame_indexes_first50"] = _droppedIdx;
            r["late_frames"] = LateFrames;
            r["cadence_lock_holds"] = CadenceHolds;
            r["cadence_lock_early"] = CadenceEarly;
            r["ticks_per_frame_histogram"] = _ticksPerFrame.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value);
            r["frame_index_error_histogram (shown - ideal)"] = _frameErr.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value);
            r["av_offset_ms (pts - clock, per tick)"] = _offset.Summary();
            r["av_sync_error_ms (clock outside the frame's display interval)"] = _syncErr.Summary();
            r["queue_depth_frames"] = _queueDepth.Summary();
            r["checkpoints"] = _checkpoints;
            r["movie_ended"] = Ended;
            if (Ended && _lastFramePresentTicks != 0)
            {
                r["end_event_after_last_frame_presented_ms"] = Series.Round(Report.Ms(_endTicks - _lastFramePresentTicks));
            }

            // audio clock rate against the Stopwatch (QPC): least-squares slope over the playing ticks
            if (_clockSamples.Count > 100)
            {
                double mx = _clockSamples.Average(s => s.Wall), my = _clockSamples.Average(s => s.Clock);
                double sxy = _clockSamples.Sum(s => (s.Wall - mx) * (s.Clock - my));
                double sxx = _clockSamples.Sum(s => (s.Wall - mx) * (s.Wall - mx));
                double slope = sxy / sxx;
                double resid = _clockSamples.Max(s => Math.Abs(s.Clock - (my + slope * (s.Wall - mx)))) * 1000;
                r["audio_clock_rate_vs_qpc_ppm"] = Series.Round((slope - 1) * 1e6);
                r["audio_clock_max_residual_ms"] = Series.Round(resid);
            }
        }
    }
}

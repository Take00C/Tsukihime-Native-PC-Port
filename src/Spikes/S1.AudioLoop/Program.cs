// Spike S1 loop (ROADMAP M0 scope 8, stream E): bgm #33 decoded through FFmpeg.AutoGen into the
// mixer; loop [962000, 8257999] inclusive; 3 loops rendered offline; played through SDL3.
// Pass: seam sample-exact; C# float PCM equals the Python decode within 1e-6.
// Results: docs/status/spikes/S1.md. No window is opened (SDL audio subsystem only).
//
//   S1.AudioLoop headless [--out DIR] [--speed-runs N] [--sdl-disk]
//   S1.AudioLoop probe [--driver NAME]          (physical formats SDL picks per open path)
//   S1.AudioLoop play [--lead S] [--after S] [--from-start] [--sample-frames N] [--tag NAME]
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SDL;

namespace S1.AudioLoop;

// Inside the namespace, so that the type Tsuki.Engine.Contracts.AudioLoop wins over the namespace S1.AudioLoop.
using Tsuki.Engine.Contracts;

internal static class Program
{
    private const int BgmIndex = 33;
    private const int Rate = 48000;
    private static readonly AudioLoop ExpectedLoop = new(962000, 8257999);   // ROADMAP M0 spike S1, audio.md 2.5

    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "headless";
        var opt = Options.Parse(args.Skip(1).ToArray());
        string repo = FindRepo();
        string outDir = opt.Get("out") ?? Path.Combine(repo, "samples", "spikes", "s1");
        Directory.CreateDirectory(outDir);
        string? game = GameFiles.GameDir();
        if (game is null)
        {
            Console.Error.WriteLine(GameFiles.MissingMessage);
            return 2;
        }

        FfmpegAtrac9.Init();
        try
        {
            return mode switch
            {
                "headless" => Headless(game, outDir, opt),
                "play" => Play(game, outDir, opt),
                "probe" => Probe(opt),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: S1.AudioLoop headless|play|probe [options] (see the header of Program.cs)");
        return 2;
    }

    // ------------------------------------------------------------------ headless

    private static int Headless(string game, string outDir, Options opt)
    {
        var res = new JsonObject { ["spike"] = "S1", ["mode"] = "headless", ["when_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
        res["versions"] = new JsonObject
        {
            ["ffmpeg"] = FfmpegAtrac9.VersionInfo(), ["avcodec_dll"] = LoadedModule("avcodec"),
            ["dotnet"] = RuntimeInformation.FrameworkDescription, ["os"] = RuntimeInformation.OSDescription,
            ["configuration"] = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
                .OfType<System.Reflection.AssemblyConfigurationAttribute>().FirstOrDefault()?.Configuration,
        };

        // 1. the entry, read with the tiny HED8/MRG reader
        var sw = Stopwatch.StartNew();
        (byte[] stream, long offset) = GameFiles.ReadBgmEntry(game, BgmIndex);
        double readMs = sw.Elapsed.TotalMilliseconds;
        string sha = Convert.ToHexStringLower(SHA1.HashData(stream));
        string exported = Path.Combine(outDir, "bgm_00033.at9");
        bool? sameAsExport = File.Exists(exported) ? File.ReadAllBytes(exported).AsSpan().SequenceEqual(stream) : null;
        var h = RiffAtrac9.Parse(stream);
        res["entry"] = new JsonObject
        {
            ["bank"] = "bgm", ["index"] = BgmIndex, ["mrg_offset"] = offset, ["size"] = stream.Length, ["sha1"] = sha,
            ["read_ms"] = Math.Round(readMs, 3), ["equals_oracle_export"] = sameAsExport,
            ["channels"] = h.Channels, ["rate"] = h.SampleRate, ["block_align"] = h.BlockAlign, ["config"] = h.ConfigWord.ToString("x8", CultureInfo.InvariantCulture),
            ["fact"] = new JsonArray([.. h.Fact.Select(v => (JsonNode)v)]), ["smpl"] = new JsonArray([.. h.SmplLoops.Select(l => (JsonNode)new JsonArray(l.Start, l.End))]),
            ["superframes"] = h.Superframes, ["decoded_frames"] = h.DecodedFrames, ["playable_frames"] = h.PlayableFrames,
        };
        Console.WriteLine($"bgm #{BgmIndex}: {stream.Length} B at 0x{offset:X}, sha1 {sha}, {h.Superframes} superframes, fact [{string.Join(", ", h.Fact)}]");

        // 2. decode speed (fresh decoder per run; run 0 is a warm-up and not counted)
        int runs = int.Parse(opt.Get("speed-runs") ?? "5", CultureInfo.InvariantCulture);
        float[] decoded = [];
        var times = new List<double>();
        var cpu = new List<double>();
        var alloc = new List<double>();
        // on one dedicated worker thread, as the port's decode workers would run it; CPU time is
        // that thread's own (Environment.CpuUsage is process-wide, so the thread's is used)
        var worker = new Thread(() =>
        {
            for (int r = 0; r <= runs; r++)
            {
                TimeSpan c0 = ThreadCpu();
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                long t0 = Stopwatch.GetTimestamp();
                decoded = FfmpegAudioDecoder.DecodeAll(h, stream);
                double s = Stopwatch.GetElapsedTime(t0).TotalSeconds;
                long a1 = GC.GetAllocatedBytesForCurrentThread();
                if (r > 0)
                {
                    times.Add(s);
                    cpu.Add((ThreadCpu() - c0).TotalSeconds);
                    alloc.Add((a1 - a0) / 1048576.0);
                }
            }
        });
        worker.Start();
        worker.Join();

        double audioS = h.DecodedFrames / (double)Rate;
        times.Sort();
        res["decode"] = new JsonObject
        {
            ["decoder"] = "libavcodec atrac9 via FFmpeg.AutoGen, 1 packet = 1 superframe, no demuxer",
            ["runs"] = runs, ["audio_seconds"] = Math.Round(audioS, 4),
            ["wall_s"] = new JsonArray([.. times.Select(t => (JsonNode)Math.Round(t, 4))]),
            ["cpu_s"] = new JsonArray([.. cpu.Select(t => (JsonNode)Math.Round(t, 4))]),
            ["x_realtime_median"] = Math.Round(audioS / times[times.Count / 2], 1),
            ["x_realtime_best"] = Math.Round(audioS / times[0], 1),
            ["x_realtime_worst"] = Math.Round(audioS / times[^1], 1),
            ["wall_ms_median"] = Math.Round(times[times.Count / 2] * 1000, 1),
            ["managed_alloc_mb_per_decode"] = new JsonArray([.. alloc.Distinct().Select(v => (JsonNode)Math.Round(v, 3))]),
            ["output_buffer_mb"] = Math.Round(h.DecodedFrames * h.Channels * 4 / 1048576.0, 3),
        };
        Console.WriteLine($"decode: median {times[times.Count / 2]:F3} s for {audioS:F2} s of audio = {audioS / times[times.Count / 2]:F0}x realtime (best {audioS / times[0]:F0}x, worst {audioS / times[^1]:F0}x)");

        // 3. the AudioClip (IAudioDecoder prototype) and its loop
        AudioClip clip = new FfmpegAudioDecoder().Decode(AudioBank.Bgm, BgmIndex, stream);
        if (clip.Loop != ExpectedLoop)
        {
            throw new InvalidDataException($"loop {clip.Loop} != expected {ExpectedLoop}");
        }

        AudioLoop lp = clip.Loop.Value;
        res["clip"] = new JsonObject
        {
            ["frames"] = clip.FrameCount, ["channels"] = clip.Channels, ["loop"] = new JsonArray(lp.Start, lp.End), ["loop_length"] = lp.Length,
            ["intro_s"] = Math.Round(lp.Start / (double)Rate, 4), ["loop_s"] = Math.Round(lp.Length / (double)Rate, 4),
            ["peak"] = Peak(clip.Samples.Span),
            ["sha256_f32le"] = Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(clip.Samples.Span))),
        };

        // 4. C# decode vs the Python oracle (tsuki.audio.decode_at9, ffmpeg CLI)
        var cmp = new JsonObject();
        foreach ((string key, string file, bool raw) in new[]
        {
            ("oracle_gyan_trim", Path.Combine(outDir, "oracle_gyan_trim.f32"), false),
            ("oracle_btbn_trim", Path.Combine(outDir, "oracle_btbn_trim.f32"), false),
            ("oracle_gyan_raw", Path.Combine(outDir, "oracle_gyan_raw.f32"), true),
            ("golden_slice", Path.Combine(FindRepo(), "samples", "golden", "slice", "audio", "bgm_00033.f32"), false),
        })
        {
            if (!File.Exists(file))
            {
                cmp[key] = "missing: " + file;
                continue;
            }

            float[] o = Wav.ReadF32(file);
            JsonObject c = Compare(raw ? decoded : clip.Samples.Span, o);
            c["sha256_f32le"] = Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(o.AsSpan())));
            cmp[key] = c;
        }

        res["vs_oracle"] = cmp;
        Console.WriteLine("vs oracle: " + cmp.ToJsonString());

        // 5. offline render through the mixer: intro + 3 x loop body (2 seams), 1024-frame periods
        int intro = lp.Start, body = lp.Length;
        long total = intro + (3L * body);
        float[] outPcm = new float[total * 2];
        var mixer = new Mixer();
        var voice = new LoopingVoice(clip, looping: true, startFrame: 0, loopEnds: 3) { Gain = 1f, Pan = 0.5f };
        mixer.Add(voice);
        long tr = Stopwatch.GetTimestamp();
        for (long f = 0; f < total; f += Mixer.MaxPeriodFrames)
        {
            int n = (int)Math.Min(Mixer.MaxPeriodFrames, total - f);
            mixer.Render(outPcm.AsSpan((int)(f * 2), n * 2));
        }

        double renderS = Stopwatch.GetElapsedTime(tr).TotalSeconds;
        string wavPath = Path.Combine(outDir, "cs_loop3.wav");
        long tw = Stopwatch.GetTimestamp();
        Wav.WriteFloat32(wavPath, outPcm, 2, Rate);
        double wavS = Stopwatch.GetElapsedTime(tw).TotalSeconds;
        float[] oracle = Wav.ReadF32(Path.Combine(outDir, "oracle_gyan_trim.f32"));
        long[] expectSeams = [intro + (long)body, intro + (2L * body)];
        res["render"] = new JsonObject
        {
            ["frames"] = total, ["seconds"] = Math.Round(total / (double)Rate, 3), ["render_s"] = Math.Round(renderS, 4),
            ["x_realtime"] = Math.Round(total / (double)Rate / renderS, 0), ["wav"] = wavPath, ["wav_write_s"] = Math.Round(wavS, 3),
            ["mixer"] = "1 voice, gain 1, pan 0.5 (gL = gR = 1), 1024-frame periods, no clipping",
            ["voice_finished"] = voice.Finished, ["voice_seams"] = voice.Seams, ["voice_frames_out"] = voice.FramesOut,
            ["seam_output_frames"] = new JsonArray([.. voice.SeamOutputFrames.Select(v => (JsonNode)v)]),
            ["seam_frames_expected"] = new JsonArray([.. expectSeams.Select(v => (JsonNode)v)]),
            ["seam_position_error_frames"] = new JsonArray([.. voice.SeamOutputFrames.Zip(expectSeams, (a, b) => (JsonNode)(a - b))]),
            ["vs_oracle_render"] = CompareRender(outPcm, 0, oracle, lp, expectSeams),
        };
        Console.WriteLine("render: " + res["render"]!["vs_oracle_render"]!.ToJsonString());

        // 6. click analysis on the rendered seams
        res["seam_analysis"] = new JsonArray([.. voice.SeamOutputFrames.Select(p => (JsonNode)SeamAnalysis.Analyse(outPcm, p, clip, decoded, h.Delay))]);
        Console.WriteLine("seam 1: " + res["seam_analysis"]![0]!.ToJsonString());

        // 7. engine-style loop (seek + decoder reset) vs the decoded-copy jump
        res["engine_style_seek"] = SeekCheck(h, stream, decoded, lp);
        Console.WriteLine("seek: " + res["engine_style_seek"]!.ToJsonString());

        // 8. the same render through an SDL3 audio stream + get-callback (no device), pulled in
        //    480-frame chunks (the WASAPI device period measured by `probe`)
        res["sdl_stream"] = SdlStream(clip, oracle, lp, total, expectSeams, 480);
        Console.WriteLine("sdl stream: " + res["sdl_stream"]!.ToJsonString());

        // 9. optional: SDL's disk driver (device thread writing a file). SDL 3.5.0 opens it at its
        //    own default S16LE 2 ch 44100 Hz whatever the stream asks for, so SDL converts and
        //    resamples and no sample comparison is possible; kept to record that measurement.
        if (opt.Has("sdl-disk"))
        {
            res["sdl_disk"] = SdlDisk(clip, oracle, lp, outDir, total, expectSeams);
            Console.WriteLine("sdl disk: " + res["sdl_disk"]!.ToJsonString());
        }

        string json = Path.Combine(outDir, opt.Has("sdl-disk") ? "results_headless_sdldisk.json" : "results_headless.json");
        File.WriteAllText(json, res.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("wrote " + json);
        bool pass = (long)res["render"]!["vs_oracle_render"]!["samples_not_equal"]! == 0
                    && (long)cmp["oracle_gyan_trim"]!["samples_not_bit_identical"]! == 0;
        return pass ? 0 : 1;
    }

    private static JsonObject Compare(ReadOnlySpan<float> a, float[] b)
    {
        long n = Math.Min(a.Length, b.Length);
        long notBit = 0, notEq = 0;
        double maxAbs = 0;
        long first = -1;
        for (long i = 0; i < n; i++)
        {
            float x = a[(int)i], y = b[i];
            if (BitConverter.SingleToInt32Bits(x) != BitConverter.SingleToInt32Bits(y))
            {
                notBit++;
                if (x != y)
                {
                    notEq++;
                    first = first < 0 ? i : first;
                }

                maxAbs = Math.Max(maxAbs, Math.Abs((double)x - y));
            }
        }

        return new JsonObject
        {
            ["samples_a"] = a.Length, ["samples_b"] = b.Length, ["same_length"] = a.Length == b.Length,
            ["samples_not_bit_identical"] = notBit, ["samples_not_equal"] = notEq, ["max_abs_diff"] = maxAbs,
            ["first_mismatch_sample"] = first, ["within_1e-6"] = a.Length == b.Length && maxAbs <= 1e-6,
        };
    }

    // got[gotOffset + 2 f + c] against the oracle's render_loop: frame f of intro + body * k.
    private static JsonObject CompareRender(ReadOnlySpan<float> got, long gotOffset, float[] oracle, AudioLoop lp, long[] seams, long frames = -1, long startFrame = 0)
    {
        long total = frames >= 0 ? frames : (got.Length - gotOffset) / 2;
        long notEq = 0, notBit = 0, first = -1;
        double maxAbs = 0;
        var seamErr = new long[seams.Length];
        for (long f = 0; f < total; f++)
        {
            long src = PlayableFrameAt(startFrame + f, lp);
            for (int c = 0; c < 2; c++)
            {
                float x = got[(int)(gotOffset + (2 * f) + c)];
                float y = oracle[(2 * src) + c];
                if (BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y))
                {
                    continue;
                }

                notBit++;
                if (x != y)
                {
                    notEq++;
                    first = first < 0 ? f : first;
                    maxAbs = Math.Max(maxAbs, Math.Abs((double)x - y));
                    for (int k = 0; k < seams.Length; k++)
                    {
                        if (Math.Abs(f - seams[k]) <= 4800)
                        {
                            seamErr[k]++;
                        }
                    }
                }
            }
        }

        return new JsonObject
        {
            ["frames_compared"] = total, ["samples_not_equal"] = notEq, ["samples_not_bit_identical"] = notBit,
            ["max_abs_diff"] = maxAbs, ["first_mismatch_frame"] = first,
            ["seam_window_frames"] = "+-4800 (100 ms) around each seam",
            ["seam_sample_errors"] = new JsonArray([.. seamErr.Select(v => (JsonNode)v)]),
        };
    }

    // Frame f of an unbounded loop rendering from frame 0 -> playable frame index.
    private static long PlayableFrameAt(long f, AudioLoop lp) => f <= lp.End ? f : lp.Start + ((f - lp.Start) % lp.Length);

    private static double Peak(ReadOnlySpan<float> x)
    {
        float m = 0;
        foreach (float v in x)
        {
            m = Math.Max(m, Math.Abs(v));
        }

        return m;
    }

    // The engine loops by seeking: superframe loop_start / 1024, decoder reset, drop
    // 256 + loop_start % 1024 samples (audio.md 2.3 / 7 step 3). Compare that output with the
    // decoded copy, after a reset of a warm decoder (loop wrap) and with a fresh decoder (start).
    private static JsonObject SeekCheck(RiffAtrac9 h, byte[] stream, float[] decoded, AudioLoop lp)
    {
        int sf = lp.Start / h.SamplesPerBlock;
        int skip = h.Delay + (lp.Start % h.SamplesPerBlock);
        const int Superframes = 160;                      // 160 x 1024 = 3.4 s
        var o = new JsonObject { ["seek_superframe"] = sf, ["dropped_samples"] = skip, ["compared_frames"] = (Superframes * h.SamplesPerBlock) - skip };
        foreach (bool warm in new[] { true, false })
        {
            using var dec = new FfmpegAtrac9(h);
            float[] scratch = new float[(long)h.DecodedFrames * 2];
            if (warm)
            {
                dec.Decode(stream, 0, h.Superframes, scratch);    // play to the end, then wrap
                dec.Flush();
            }

            float[] got = new float[Superframes * h.SamplesPerBlock * 2];
            dec.Decode(stream, sf, Superframes, got);
            int n = (Superframes * h.SamplesPerBlock) - skip;
            long notBit = 0, notEq = 0, lastDiff = -1, firstDiff = -1;
            double maxAbs = 0;
            for (int f = 0; f < n; f++)
            {
                for (int c = 0; c < 2; c++)
                {
                    float x = got[((skip + f) * 2) + c];
                    float y = decoded[((h.Delay + lp.Start + f) * 2) + c];
                    if (BitConverter.SingleToInt32Bits(x) != BitConverter.SingleToInt32Bits(y))
                    {
                        notBit++;
                        if (x != y)
                        {
                            notEq++;
                        }

                        firstDiff = firstDiff < 0 ? f : firstDiff;
                        lastDiff = f;
                        maxAbs = Math.Max(maxAbs, Math.Abs((double)x - y));
                    }
                }
            }

            o[warm ? "after_reset_of_warm_decoder" : "fresh_decoder"] = new JsonObject
            {
                ["samples_not_bit_identical"] = notBit, ["samples_not_equal"] = notEq, ["max_abs_diff"] = maxAbs,
                ["first_diff_frame"] = firstDiff, ["last_diff_frame"] = lastDiff,
            };
        }

        return o;
    }

    private static JsonObject SdlDisk(AudioClip clip, float[] oracle, AudioLoop lp, string outDir, long total, long[] seams)
    {
        string raw = Path.Combine(outDir, "sdl_disk_out.f32");
        string timescale = "0.05";
        SDL3.SDL_SetHint(SDL3.SDL_HINT_AUDIO_DRIVER, "disk");
        SDL3.SDL_SetHint(SDL3.SDL_HINT_AUDIO_DISK_OUTPUT_FILE, raw);
        SDL3.SDL_SetHint(SDL3.SDL_HINT_AUDIO_DISK_TIMESCALE, timescale);

        // The disk device ignores the stream's spec and opens at its own default (measured: S16LE
        // 2 ch 44100 Hz, i.e. SDL resamples); these hints make the device float32 stereo 48 kHz.
        SDL3.SDL_SetHint(SDL3.SDL_HINT_AUDIO_FORMAT, "F32");
        SDL3.SDL_SetHint(SDL3.SDL_HINT_AUDIO_CHANNELS, "2");
        SDL3.SDL_SetHint(SDL3.SDL_HINT_AUDIO_FREQUENCY, "48000");
        if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_AUDIO))
        {
            return new JsonObject { ["error"] = "SDL_Init: " + SDL3.SDL_GetError() };
        }

        var r = new JsonObject { ["sdl"] = SdlOutput.SdlVersion(), ["file"] = raw, ["disk_timescale"] = timescale };
        try
        {
            var mixer = new Mixer();
            var voice = new LoopingVoice(clip, looping: true, startFrame: 0, loopEnds: 3);
            mixer.Add(voice);
            long t0 = Stopwatch.GetTimestamp();
            using (var outp = SdlOutput.Open(mixer, total, capture: null))
            {
                outp.Start();
                bool ok = outp.WaitDone(TimeSpan.FromMinutes(8), 200);
                outp.Pause();
                r["completed"] = ok;
                r["stats"] = outp.Stats();
                r["wall_s"] = Math.Round(Stopwatch.GetElapsedTime(t0).TotalSeconds, 3);
            }

            r["voice_seams"] = voice.Seams;
            r["file_bytes"] = new FileInfo(raw).Length;
            SDL_AudioSpec ds = ((JsonObject)r["stats"]!)["device_spec"] is JsonObject spec && (string?)spec["format"] == "SDL_AUDIO_F32LE"
                               && (int)spec["channels"]! == 2 && (int)spec["freq"]! == Rate
                ? new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = 2, freq = Rate }
                : default;
            r["vs_oracle_render"] = ds.freq == Rate
                ? AlignedCompare(Wav.ReadF32(raw), oracle, lp, total, seams, 0)
                : "not compared: the disk device is not float32 stereo 48 kHz, SDL converted the stream";
        }
        finally
        {
            SDL3.SDL_Quit();
        }

        return r;
    }

    private static JsonObject SdlStream(AudioClip clip, float[] oracle, AudioLoop lp, long total, long[] seams, int chunkFrames)
    {
        SDL3.SDL_SetHint(SDL3.SDL_HINT_AUDIO_DRIVER, "dummy");
        if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_AUDIO))
        {
            return new JsonObject { ["error"] = "SDL_Init: " + SDL3.SDL_GetError() };
        }

        var r = new JsonObject { ["sdl"] = SdlOutput.SdlVersion(), ["path"] = "SDL_CreateAudioStream(F32 2 48000 -> same) + get-callback, pulled with SDL_GetAudioStreamData", ["pull_chunk_frames"] = chunkFrames };
        try
        {
            var mixer = new Mixer();
            var voice = new LoopingVoice(clip, looping: true, startFrame: 0, loopEnds: 3);
            mixer.Add(voice);
            float[] got = new float[(total + chunkFrames) * 2];
            long frames = 0;
            long t0 = Stopwatch.GetTimestamp();
            using (var s = SdlOutput.CreateStreamOnly(mixer, total))
            {
                while (true)
                {
                    int n = s.Pull(got.AsSpan((int)(frames * 2), Math.Min(chunkFrames * 2, got.Length - (int)(frames * 2))));
                    frames += n;
                    if (n == 0 || frames >= got.Length / 2)
                    {
                        break;
                    }
                }

                r["wall_s"] = Math.Round(Stopwatch.GetElapsedTime(t0).TotalSeconds, 3);
                r["stats"] = s.Stats();
            }

            r["frames_pulled"] = frames;
            r["voice_seams"] = voice.Seams;
            r["vs_oracle_render"] = frames == total
                ? CompareRender(got, 0, oracle, lp, seams, total)
                : $"pulled {frames} frames, expected {total}";
        }
        finally
        {
            SDL3.SDL_Quit();
        }

        return r;
    }

    // got = device-side capture (leading/trailing silence from the device running before/after
    // our data): align on the first non-zero sample, then compare `frames` frames exactly.
    private static JsonObject AlignedCompare(float[] got, float[] oracle, AudioLoop lp, long frames, long[] seams, long startFrame)
    {
        long firstNonZeroGot = Array.FindIndex(got, v => v != 0f);
        long firstNonZeroRef = -1;
        for (long f = 0; f < frames && firstNonZeroRef < 0; f++)
        {
            long src = PlayableFrameAt(startFrame + f, lp);
            if (oracle[2 * src] != 0f)
            {
                firstNonZeroRef = 2 * f;
            }
            else if (oracle[(2 * src) + 1] != 0f)
            {
                firstNonZeroRef = (2 * f) + 1;
            }
        }

        if (firstNonZeroGot < 0 || firstNonZeroRef < 0)
        {
            return new JsonObject { ["error"] = "no audio found" };
        }

        long offset = firstNonZeroGot - firstNonZeroRef;
        if (offset < 0 || offset % 2 != 0 || offset + (frames * 2) > got.Length)
        {
            return new JsonObject { ["error"] = $"cannot align: offset {offset}, file {got.Length} samples, need {frames * 2}" };
        }

        bool leadingSilent = true;
        for (long i = 0; i < offset; i++)
        {
            leadingSilent &= got[i] == 0f;
        }

        long tailStart = offset + (frames * 2);
        bool trailingSilent = true;
        for (long i = tailStart; i < got.Length; i++)
        {
            trailingSilent &= got[i] == 0f;
        }

        JsonObject c = CompareRender(got, offset, oracle, lp, seams, frames, startFrame);
        c["leading_silence_frames"] = offset / 2;
        c["leading_all_zero"] = leadingSilent;
        c["trailing_frames"] = (got.Length - tailStart) / 2;
        c["trailing_all_zero"] = trailingSilent;
        return c;
    }

    // ------------------------------------------------------------------ play (real device)

    private static int Play(string game, string outDir, Options opt)
    {
        double lead = double.Parse(opt.Get("lead") ?? "3", CultureInfo.InvariantCulture);
        double after = double.Parse(opt.Get("after") ?? "3", CultureInfo.InvariantCulture);
        bool fromStart = opt.Has("from-start");
        string tag = opt.Get("tag") ?? (fromStart ? "full" : "seam");
        (byte[] stream, _) = GameFiles.ReadBgmEntry(game, BgmIndex);
        var h = RiffAtrac9.Parse(stream);
        float[] decoded = FfmpegAudioDecoder.DecodeAll(h, stream);
        AudioClip clip = FfmpegAudioDecoder.ToClip(AudioBank.Bgm, BgmIndex, h, decoded);
        AudioLoop lp = clip.Loop ?? throw new InvalidDataException("no loop");
        float[] oracle = Wav.ReadF32(Path.Combine(outDir, "oracle_gyan_trim.f32"));

        int start = fromStart ? 0 : lp.End + 1 - (int)(lead * Rate);
        long budget = (lp.End + 1 - start) + (long)lp.Length + (long)(after * Rate);   // through seam 2, then `after` s
        long[] seams = [lp.End + 1 - start, lp.End + 1 - start + lp.Length];

        string? sampleFrames = opt.Get("sample-frames");
        if (sampleFrames is not null)
        {
            SDL3.SDL_SetHint(SDL3.SDL_HINT_AUDIO_DEVICE_SAMPLE_FRAMES, sampleFrames);
        }

        if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_AUDIO))
        {
            Console.Error.WriteLine("SDL_Init failed: " + SDL3.SDL_GetError());
            return 1;
        }

        var res = new JsonObject
        {
            ["spike"] = "S1", ["mode"] = "play", ["tag"] = tag, ["when_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["sdl"] = SdlOutput.SdlVersion(), ["ffmpeg"] = FfmpegAtrac9.VersionInfo(),
            ["sample_frames_hint"] = sampleFrames, ["start_frame"] = start, ["budget_frames"] = budget,
            ["budget_s"] = Math.Round(budget / (double)Rate, 3), ["seams_at_output_frames"] = new JsonArray([.. seams.Select(v => (JsonNode)v)]),
        };
        try
        {
            var mixer = new Mixer();
            var voice = new LoopingVoice(clip, looping: true, startFrame: start);
            mixer.Add(voice);
            float[] capture = new float[(budget + (4L * Rate)) * 2];
            using var outp = SdlOutput.Open(mixer, budget, capture);
            Console.WriteLine($"playing bgm #{BgmIndex} from frame {start} for {budget / (double)Rate:F1} s on '{SDL3.SDL_GetAudioDeviceName(outp.Device)}' " +
                              $"({SDL3.SDL_GetCurrentAudioDriver()}, {outp.DeviceSpec.format} {outp.DeviceSpec.channels} ch {outp.DeviceSpec.freq} Hz, {outp.DeviceSampleFrames} frames)");
            long t0 = Stopwatch.GetTimestamp();
            outp.Start();
            int drainMs = (int)(outp.DeviceSampleFrames * 4000L / Math.Max(outp.DeviceSpec.freq, 1)) + 300;
            bool ok = outp.WaitDone(TimeSpan.FromSeconds((budget / (double)Rate) + 30), drainMs);
            outp.Pause();
            res["completed"] = ok;
            res["wall_s"] = Math.Round(Stopwatch.GetElapsedTime(t0).TotalSeconds, 3);
            res["voice_seams"] = voice.Seams;
            res["voice_seam_output_frames"] = new JsonArray([.. voice.SeamOutputFrames.Select(v => (JsonNode)v)]);
            res["stats"] = outp.Stats();
            SDL_AudioSpec ps = outp.PostmixSpec;
            if (ps.channels == 2 && ps.freq == Rate)
            {
                res["postmix_vs_oracle"] = AlignedCompare(outp.Captured.ToArray(), oracle, lp, budget, seams, start);
            }
            else
            {
                res["postmix_vs_oracle"] = $"not compared: device mix is {ps.channels} ch {ps.freq} Hz";
            }
        }
        finally
        {
            SDL3.SDL_Quit();
        }

        string json = Path.Combine(outDir, $"results_play_{tag}.json");
        File.WriteAllText(json, res.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(res.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("wrote " + json);
        return 0;
    }

    // ------------------------------------------------------------------ probe (device formats)

    // Which physical format SDL picks for float32 stereo 48 kHz requests, per open path.
    private static unsafe int Probe(Options opt)
    {
        string? driver = opt.Get("driver");
        if (driver is not null)
        {
            SDL3.SDL_SetHint(SDL3.SDL_HINT_AUDIO_DRIVER, driver);
        }

        if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_AUDIO))
        {
            Console.Error.WriteLine("SDL_Init failed: " + SDL3.SDL_GetError());
            return 1;
        }

        try
        {
            Console.WriteLine($"{SdlOutput.SdlVersion()}, driver {SDL3.SDL_GetCurrentAudioDriver()}");
            SDL_AudioSpec want = new() { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = 2, freq = 48000 };
            SDL_AudioSpec got;
            int frames;
            using (var devs = SDL3.SDL_GetAudioPlaybackDevices())
            {
                if (devs is not null)
                {
                    for (int i = 0; i < devs.Count; i++)
                    {
                        SDL3.SDL_GetAudioDeviceFormat(devs[i], &got, &frames);
                        Console.WriteLine($"  physical device {(uint)devs[i]} '{SDL3.SDL_GetAudioDeviceName(devs[i])}': {got.format} {got.channels} ch {got.freq} Hz, {frames} frames (before open)");
                    }
                }
            }

            SDL3.SDL_GetAudioDeviceFormat(SDL3.SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &got, &frames);
            Console.WriteLine($"  default playback before open: {got.format} {got.channels} ch {got.freq} Hz, {frames} frames");
            SDL_AudioDeviceID d = SDL3.SDL_OpenAudioDevice(SDL3.SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &want);
            SDL3.SDL_GetAudioDeviceFormat(d, &got, &frames);
            Console.WriteLine($"  SDL_OpenAudioDevice(default, F32 2 48000): {got.format} {got.channels} ch {got.freq} Hz, {frames} frames");
            SDL3.SDL_CloseAudioDevice(d);
            SDL_AudioStream* st = SDL3.SDL_OpenAudioDeviceStream(SDL3.SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &want, null, IntPtr.Zero);
            SDL3.SDL_GetAudioDeviceFormat(SDL3.SDL_GetAudioStreamDevice(st), &got, &frames);
            Console.WriteLine($"  SDL_OpenAudioDeviceStream(default, F32 2 48000): {got.format} {got.channels} ch {got.freq} Hz, {frames} frames");
            SDL3.SDL_DestroyAudioStream(st);
        }
        finally
        {
            SDL3.SDL_Quit();
        }

        return 0;
    }

    // ------------------------------------------------------------------ helpers

    private static string? LoadedModule(string prefix)
    {
        foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
        {
            if (m.ModuleName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return m.FileName;
            }
        }

        return null;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    private static TimeSpan ThreadCpu() =>
        GetThreadTimes(GetCurrentThread(), out _, out _, out long k, out long u) ? TimeSpan.FromTicks(k + u) : TimeSpan.Zero;

    private static string FindRepo()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(d.FullName, "src")))
            {
                return d.FullName;
            }
        }

        throw new DirectoryNotFoundException("repository root not found above " + AppContext.BaseDirectory);
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _v = new(StringComparer.Ordinal);

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("unexpected argument " + args[i]);
                }

                string k = args[i][2..];
                string? v = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
                o._v[k] = v;
            }

            return o;
        }

        public string? Get(string k) => _v.GetValueOrDefault(k);

        public bool Has(string k) => _v.ContainsKey(k);
    }
}

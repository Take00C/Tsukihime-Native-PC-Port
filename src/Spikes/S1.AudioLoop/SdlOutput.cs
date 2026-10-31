using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SDL;

namespace S1.AudioLoop;

// SDL3 output: one audio stream (float32 stereo 48 kHz) opened on the default playback device with
// a get-callback that renders the mixer in periods of <= 1024 frames on SDL's audio thread
// (ARCHITECTURE 7.1). Optionally a postmix callback captures what SDL hands to the driver, so
// the device path can be checked sample by sample, and statistics are kept per callback:
// interval, requested bytes, time spent, shortfalls (= our underruns: fewer frames than SDL asked
// for while the play budget is not used up).
internal sealed unsafe class SdlOutput : IDisposable
{
    private const int MaxCallbacks = 1 << 20;
    private static SdlOutput? s_active;

    private readonly Mixer _mixer;
    private readonly long _budgetFrames;
    private readonly float[] _period = new float[Mixer.MaxPeriodFrames * 2];
    private readonly long[] _cbStart = new long[MaxCallbacks];
    private readonly int[] _cbBytes = new int[MaxCallbacks];
    private readonly int[] _cbQueued = new int[MaxCallbacks];
    private readonly long[] _cbTicks = new long[MaxCallbacks];
    private readonly float[]? _capture;
    private readonly ManualResetEventSlim _done = new(false);
    private SDL_AudioStream* _stream;
    private int _cbCount;
    private long _framesOut;
    private long _capturedSamples;
    private long _captureDropped;
    private int _postmixCalls;
    private SDL_AudioSpec _postmixSpec;
    private int _shortfalls;
    private long _shortfallFrames;
    private int _afterBudgetCallbacks;
    private string? _callbackError;
    private long _resumeTimestamp;
    private long _budgetDoneTimestamp;

    private SdlOutput(Mixer mixer, long budgetFrames, float[]? capture)
    {
        _mixer = mixer;
        _budgetFrames = budgetFrames;
        _capture = capture;
    }

    public SDL_AudioDeviceID Device { get; private set; }

    public SDL_AudioSpec DeviceSpec { get; private set; }

    public int DeviceSampleFrames { get; private set; }

    public long FramesOut => Interlocked.Read(ref _framesOut);

    public ReadOnlySpan<float> Captured => _capture is null ? [] : _capture.AsSpan(0, (int)Interlocked.Read(ref _capturedSamples));

    public SDL_AudioSpec PostmixSpec => _postmixSpec;

    public static string SdlVersion()
    {
        int v = SDL3.SDL_GetVersion();
        return $"SDL {v / 1000000}.{v / 1000 % 1000}.{v % 1000} ({SDL3.SDL_GetRevision()})";
    }

    public static SdlOutput Open(Mixer mixer, long budgetFrames, float[]? capture)
    {
        if (s_active is not null)
        {
            throw new InvalidOperationException("one SdlOutput at a time");
        }

        var o = new SdlOutput(mixer, budgetFrames, capture);
        var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = 2, freq = 48000 };
        s_active = o;
        o._stream = SDL3.SDL_OpenAudioDeviceStream(SDL3.SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, &OnGet, IntPtr.Zero);
        if (o._stream == null)
        {
            s_active = null;
            throw new InvalidOperationException("SDL_OpenAudioDeviceStream failed: " + SDL3.SDL_GetError());
        }

        o.Device = SDL3.SDL_GetAudioStreamDevice(o._stream);
        SDL_AudioSpec dev;
        int frames;
        if (!SDL3.SDL_GetAudioDeviceFormat(o.Device, &dev, &frames))
        {
            throw new InvalidOperationException("SDL_GetAudioDeviceFormat failed: " + SDL3.SDL_GetError());
        }

        o.DeviceSpec = dev;
        o.DeviceSampleFrames = frames;
        if (capture is not null && !SDL3.SDL_SetAudioPostmixCallback(o.Device, &OnPostmix, IntPtr.Zero))
        {
            throw new InvalidOperationException("SDL_SetAudioPostmixCallback failed: " + SDL3.SDL_GetError());
        }

        return o;
    }

    // No device: an SDL_AudioStream F32/2/48000 -> F32/2/48000 with the same get-callback, pulled
    // by the caller (SDL_GetAudioStreamData) in device-sized chunks. Exercises SDL's stream and
    // callback path deterministically, without a device thread.
    public static SdlOutput CreateStreamOnly(Mixer mixer, long budgetFrames)
    {
        if (s_active is not null)
        {
            throw new InvalidOperationException("one SdlOutput at a time");
        }

        var o = new SdlOutput(mixer, budgetFrames, null);
        var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = 2, freq = 48000 };
        o._stream = SDL3.SDL_CreateAudioStream(&spec, &spec);
        if (o._stream == null)
        {
            throw new InvalidOperationException("SDL_CreateAudioStream failed: " + SDL3.SDL_GetError());
        }

        s_active = o;
        if (!SDL3.SDL_SetAudioStreamGetCallback(o._stream, &OnGet, IntPtr.Zero))
        {
            throw new InvalidOperationException("SDL_SetAudioStreamGetCallback failed: " + SDL3.SDL_GetError());
        }

        o.DeviceSpec = spec;
        o._resumeTimestamp = Stopwatch.GetTimestamp();
        return o;
    }

    // Stream-only mode: reads up to dst.Length / 2 frames; returns frames read.
    public int Pull(Span<float> dst)
    {
        fixed (float* p = dst)
        {
            int got = SDL3.SDL_GetAudioStreamData(_stream, (IntPtr)p, dst.Length * 4);
            if (got < 0)
            {
                throw new InvalidOperationException("SDL_GetAudioStreamData failed: " + SDL3.SDL_GetError());
            }

            return got / 8;
        }
    }

    public void Start()
    {
        _resumeTimestamp = Stopwatch.GetTimestamp();
        if (!SDL3.SDL_ResumeAudioStreamDevice(_stream))
        {
            throw new InvalidOperationException("SDL_ResumeAudioStreamDevice failed: " + SDL3.SDL_GetError());
        }
    }

    // Waits until the budget has been handed to SDL, then for `drainMs` so the device plays it out.
    public bool WaitDone(TimeSpan timeout, int drainMs)
    {
        bool ok = _done.Wait(timeout);
        if (drainMs > 0)
        {
            Thread.Sleep(drainMs);
        }

        return ok;
    }

    public void Pause() => SDL3.SDL_PauseAudioStreamDevice(_stream);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnGet(IntPtr userdata, SDL_AudioStream* stream, int additionalAmount, int totalAmount)
    {
        SdlOutput? o = s_active;
        if (o is null)
        {
            return;
        }

        try
        {
            o.Feed(stream, additionalAmount, totalAmount);
        }
        catch (Exception ex)
        {
            o._callbackError ??= ex.ToString();
            o._done.Set();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPostmix(IntPtr userdata, SDL_AudioSpec* spec, float* buffer, int buflen)
    {
        SdlOutput? o = s_active;
        if (o?._capture is null)
        {
            return;
        }

        if (o._postmixCalls++ == 0)
        {
            o._postmixSpec = *spec;
        }

        int n = buflen / 4;
        long at = o._capturedSamples;
        int room = (int)Math.Min(n, o._capture.Length - at);
        if (room > 0)
        {
            new ReadOnlySpan<float>(buffer, room).CopyTo(o._capture.AsSpan((int)at));
            Interlocked.Add(ref o._capturedSamples, room);
        }

        o._captureDropped += n - Math.Max(room, 0);
    }

    private void Feed(SDL_AudioStream* stream, int additionalAmount, int totalAmount)
    {
        long t0 = Stopwatch.GetTimestamp();
        int idx = _cbCount;
        int frames = (additionalAmount + 7) / 8;
        long before = _framesOut;
        while (frames > 0 && _framesOut < _budgetFrames)
        {
            int n = (int)Math.Min(Math.Min(frames, Mixer.MaxPeriodFrames), _budgetFrames - _framesOut);
            Span<float> buf = _period.AsSpan(0, n * 2);
            _mixer.Render(buf);
            fixed (float* p = _period)
            {
                if (!SDL3.SDL_PutAudioStreamData(stream, (IntPtr)p, n * 8))
                {
                    throw new InvalidOperationException("SDL_PutAudioStreamData failed: " + SDL3.SDL_GetError());
                }
            }

            frames -= n;
            Interlocked.Add(ref _framesOut, n);
        }

        if (frames > 0)
        {
            if (before < _budgetFrames && _framesOut < _budgetFrames)
            {
                _shortfalls++;
                _shortfallFrames += frames;
            }
            else
            {
                _afterBudgetCallbacks++;
            }
        }

        if (_framesOut >= _budgetFrames && before < _budgetFrames)
        {
            _budgetDoneTimestamp = Stopwatch.GetTimestamp();
            _done.Set();
        }

        if (idx < MaxCallbacks)
        {
            _cbStart[idx] = t0;
            _cbBytes[idx] = additionalAmount;
            _cbQueued[idx] = totalAmount - additionalAmount;
            _cbTicks[idx] = Stopwatch.GetTimestamp() - t0;
            _cbCount = idx + 1;
        }
    }

    public JsonObject Stats()
    {
        int n = _cbCount;
        double tickMs = 1000.0 / Stopwatch.Frequency;
        var intervals = new List<double>();
        var durations = new List<double>();
        var bytes = new SortedDictionary<int, int>();
        int budgetCallbacks = 0;
        long consumed = 0;
        for (int i = 0; i < n; i++)
        {
            durations.Add(_cbTicks[i] * tickMs);
            bytes[_cbBytes[i]] = bytes.GetValueOrDefault(_cbBytes[i]) + 1;
            if (_budgetDoneTimestamp != 0 && _cbStart[i] > _budgetDoneTimestamp)
            {
                continue;
            }

            budgetCallbacks++;
            consumed += _cbBytes[i] / 8;
            if (i > 0)
            {
                intervals.Add((_cbStart[i] - _cbStart[i - 1]) * tickMs);
            }
        }

        intervals.Sort();
        durations.Sort();
        double nominalMs = DeviceSampleFrames * 1000.0 / Math.Max(DeviceSpec.freq, 1);
        double medianInterval = intervals.Count > 0 ? intervals[intervals.Count / 2] : 0;
        double elapsedS = _budgetDoneTimestamp != 0 && n > 0 ? (_budgetDoneTimestamp - _cbStart[0]) / (double)Stopwatch.Frequency : 0;
        var bytesHist = new JsonObject();
        foreach ((int k, int v) in bytes)
        {
            bytesHist[k.ToString(System.Globalization.CultureInfo.InvariantCulture)] = v;
        }

        return new JsonObject
        {
            ["driver"] = SDL3.SDL_GetCurrentAudioDriver(),
            ["device_name"] = SDL3.SDL_GetAudioDeviceName(Device),
            ["device_spec"] = new JsonObject { ["format"] = DeviceSpec.format.ToString(), ["channels"] = DeviceSpec.channels, ["freq"] = DeviceSpec.freq },
            ["device_sample_frames"] = DeviceSampleFrames,
            ["device_buffer_ms"] = Math.Round(nominalMs, 3),
            ["budget_frames"] = _budgetFrames,
            ["frames_out"] = FramesOut,
            ["callbacks"] = n,
            ["callbacks_within_budget"] = budgetCallbacks,
            ["callback_bytes_histogram"] = bytesHist,
            ["first_callback_after_resume_ms"] = n > 0 ? Math.Round((_cbStart[0] - _resumeTimestamp) * tickMs, 3) : null,
            ["interval_ms"] = Summary(intervals),
            ["intervals_over_1_5x_median"] = intervals.Count(v => v > 1.5 * medianInterval),
            ["intervals_over_device_buffer_x2"] = intervals.Count(v => v > 2 * nominalMs),
            ["callback_cpu_ms"] = Summary(durations),
            ["underruns_shortfall_callbacks"] = _shortfalls,
            ["underrun_shortfall_frames"] = _shortfallFrames,
            ["callbacks_after_budget"] = _afterBudgetCallbacks,
            ["elapsed_first_callback_to_budget_s"] = Math.Round(elapsedS, 4),
            ["effective_rate_hz"] = elapsedS > 0 ? Math.Round((consumed - (_cbBytes[0] / 8)) / elapsedS, 1) : null,
            ["postmix_calls"] = _postmixCalls,
            ["postmix_spec"] = _postmixCalls > 0 ? new JsonObject { ["format"] = _postmixSpec.format.ToString(), ["channels"] = _postmixSpec.channels, ["freq"] = _postmixSpec.freq } : null,
            ["captured_samples"] = Interlocked.Read(ref _capturedSamples),
            ["capture_dropped_samples"] = _captureDropped,
            ["callback_error"] = _callbackError,
        };
    }

    private static JsonObject Summary(List<double> sorted)
    {
        if (sorted.Count == 0)
        {
            return new JsonObject { ["n"] = 0 };
        }

        double P(double q) => sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];
        return new JsonObject
        {
            ["n"] = sorted.Count,
            ["min"] = Math.Round(sorted[0], 4),
            ["median"] = Math.Round(P(0.5), 4),
            ["p99"] = Math.Round(P(0.99), 4),
            ["p999"] = Math.Round(P(0.999), 4),
            ["max"] = Math.Round(sorted[^1], 4),
            ["mean"] = Math.Round(sorted.Average(), 4),
        };
    }

    public void Dispose()
    {
        if (_stream != null)
        {
            SDL3.SDL_DestroyAudioStream(_stream);
            _stream = null;
        }

        if (ReferenceEquals(s_active, this))
        {
            s_active = null;
        }

        _done.Dispose();
    }
}

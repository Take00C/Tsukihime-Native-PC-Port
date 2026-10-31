// Spike S2: SDL3 audio stream pulled by a callback (ARCHITECTURE 3.4 "audio thread SDL3 stream callback";
// 7.1: float32 stereo 48 kHz), and the audio clock that is the movie's master clock (ARCHITECTURE 7.5).
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SDL;

namespace S2.MovieSync;

/// <summary>
/// Plays a <see cref="PcmRing"/> through SDL3 and exposes the media clock: the stream position of the sample
/// leaving the device now, estimated from the last callback (cumulative media frames handed to SDL, its
/// timestamp) minus one device buffer of latency.
/// </summary>
internal sealed unsafe class AudioOut : IDisposable
{
    private readonly object _clockLock = new();
    private readonly PcmRing _ring;
    private readonly double _basePts;
    private SDL_AudioStream* _stream;
    private GCHandle _self;
    private float[] _scratch = new float[2 * 4096];

    // clock state written by the callback, read by the main thread under _clockLock
    private long _mediaFrames;   // media frames handed to SDL so far
    private long _lastCbTicks;   // Stopwatch timestamp of the last callback
    private int _lastChunkMedia; // media frames in the last callback
    private bool _drained;       // end of stream reached and the ring was empty
    private double _lastClock = double.NegativeInfinity;
    private long _prevCallbackTicks; // for the callback interval statistic only

    public AudioOut(PcmRing ring, double basePts)
    {
        _ring = ring;
        _basePts = basePts;
        _self = GCHandle.Alloc(this);
        var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = 2, freq = AudioDecoder.OutRate };
        _stream = SDL3.SDL_OpenAudioDeviceStream(SDL3.SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, &Callback, GCHandle.ToIntPtr(_self));
        if (_stream == null)
        {
            throw new InvalidOperationException("SDL_OpenAudioDeviceStream: " + SDL3.SDL_GetError());
        }

        SDL_AudioSpec dev;
        int frames;
        if (SDL3.SDL_GetAudioDeviceFormat(SDL3.SDL_GetAudioStreamDevice(_stream), &dev, &frames))
        {
            DeviceRate = dev.freq;
            DeviceChannels = dev.channels;
            DeviceFormat = dev.format.ToString();
            DeviceBufferFrames = frames;
        }

        LatencySeconds = DeviceRate > 0 ? DeviceBufferFrames / (double)DeviceRate : 0.01;
    }

    public int DeviceRate { get; }

    public int DeviceChannels { get; }

    public string DeviceFormat { get; } = "?";

    public int DeviceBufferFrames { get; }

    /// <summary>Assumed output latency: one device buffer.</summary>
    public double LatencySeconds { get; }

    /// <summary>Set by the producer when no more samples will come.</summary>
    public volatile bool EndOfStream;

    public bool Started { get; private set; }

    public int Callbacks { get; private set; }

    /// <summary>Frames of silence inserted because the ring ran dry while playing (not at the end).</summary>
    public long UnderrunFrames { get; private set; }

    public int UnderrunEvents { get; private set; }

    public int MaxRequestFrames { get; private set; }

    public int MinRequestFrames { get; private set; } = int.MaxValue;

    public uint CallbackThreadId { get; private set; }

    public long MediaFramesPlayed
    {
        get
        {
            lock (_clockLock)
            {
                return _mediaFrames;
            }
        }
    }

    public Series CallbackIntervalMs { get; } = new("ms");

    public void Start()
    {
        Started = true;
        if (!SDL3.SDL_ResumeAudioStreamDevice(_stream))
        {
            throw new InvalidOperationException("SDL_ResumeAudioStreamDevice: " + SDL3.SDL_GetError());
        }
    }

    /// <summary>Media clock in seconds at Stopwatch time <paramref name="now"/>, or NaN before the first callback.</summary>
    public double Clock(long now)
    {
        long m, tcb;
        int chunk;
        bool drained;
        lock (_clockLock)
        {
            m = _mediaFrames;
            tcb = _lastCbTicks;
            chunk = _lastChunkMedia;
            drained = _drained;
        }

        if (tcb == 0)
        {
            return double.NaN;
        }

        double since = Report.Seconds(now - tcb) - LatencySeconds;
        if (!drained)
        {
            since = Math.Min(since, chunk / (double)AudioDecoder.OutRate);
        }

        double c = _basePts + (m - chunk) / (double)AudioDecoder.OutRate + since;
        if (c < _lastClock)
        {
            c = _lastClock; // never run backwards
        }

        _lastClock = c;
        return c;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Callback(nint userdata, SDL_AudioStream* stream, int additional, int total)
    {
        var self = (AudioOut)GCHandle.FromIntPtr(userdata).Target!;
        self.OnCallback(stream, additional);
    }

    private void OnCallback(SDL_AudioStream* stream, int additionalBytes)
    {
        long now = Stopwatch.GetTimestamp();
        if (CallbackThreadId == 0)
        {
            CallbackThreadId = Native.GetCurrentThreadId();
        }

        int frames = additionalBytes / 8;
        if (frames <= 0)
        {
            return;
        }

        if (_scratch.Length < frames * 2)
        {
            _scratch = new float[frames * 2];
        }

        var span = _scratch.AsSpan(0, frames * 2);
        int got = _ring.Read(span);
        if (got < frames)
        {
            span[(got * 2)..].Clear();
            if (!EndOfStream)
            {
                UnderrunFrames += frames - got;
                UnderrunEvents++;
            }
        }

        fixed (float* p = _scratch)
        {
            SDL3.SDL_PutAudioStreamData(stream, (nint)p, frames * 8);
        }

        if (_prevCallbackTicks != 0)
        {
            CallbackIntervalMs.Add(Report.Ms(now - _prevCallbackTicks));
        }

        _prevCallbackTicks = now;
        lock (_clockLock)
        {
            if (got > 0 || !_drained)
            {
                _mediaFrames += got;
                _lastChunkMedia = got;
                _lastCbTicks = now;
            }

            if (EndOfStream && got < frames)
            {
                _drained = true;
            }
        }

        Callbacks++;
        MaxRequestFrames = Math.Max(MaxRequestFrames, frames);
        MinRequestFrames = Math.Min(MinRequestFrames, frames);
    }

    public void Dispose()
    {
        if (_stream != null)
        {
            SDL3.SDL_DestroyAudioStream(_stream);
            _stream = null;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}

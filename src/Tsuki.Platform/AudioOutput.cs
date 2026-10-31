// The audio-stream seam between Tsuki.Platform (SDL3) and Tsuki.Media's mixer (ARCHITECTURE 3.4
// "audio thread: SDL3 stream callback -> Mixer.Render(<= 1024 frames, float32 stereo, 48 kHz)",
// 7.1; PACE 7.2 #6 "H1's audio-stream seam"). Lifted from spike S1 SdlOutput, without its
// statistics and with one instance per stream instead of a static singleton.
//
// Two modes:
//  - device: SDL's default playback device; SDL calls the render callback on its audio thread;
//  - pull: no device; the caller pulls with Pull() and the callback runs on the caller's thread
//    inside SDL's stream path (the deterministic, sample-exact boundary of S1.md for headless tests).
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SDL;
using static SDL.SDL3;

namespace Tsuki.Platform;

/// <summary>
/// Renders the next <c>frames</c> frames as interleaved float32 stereo into <paramref name="interleaved"/>
/// (length = frames * 2, frames &lt;= <see cref="AudioOutput.MaxPeriodFrames"/>). Must fill all of it.
/// </summary>
public delegate void AudioRenderCallback(Span<float> interleaved);

/// <summary>One float32 stereo 48 kHz SDL audio stream fed by a render callback.</summary>
public sealed unsafe class AudioOutput : IDisposable
{
    /// <summary>Output rate (ARCHITECTURE 7.1).</summary>
    public const int SampleRate = 48_000;

    /// <summary>Output channels (stereo).</summary>
    public const int Channels = 2;

    /// <summary>The largest period the callback is asked for (ARCHITECTURE 7.1).</summary>
    public const int MaxPeriodFrames = 1024;

    private const int BytesPerFrame = Channels * sizeof(float);

    private readonly AudioRenderCallback _render;
    private readonly float[] _period = GC.AllocateArray<float>(MaxPeriodFrames * Channels, pinned: true);
    private GCHandle _self;
    private SDL_AudioStream* _stream;
    private long _framesRendered;
    private long _callbacks;
    private string? _callbackError;

    private AudioOutput(AudioRenderCallback render, bool device)
    {
        _render = render;
        IsDevice = device;
        _self = GCHandle.Alloc(this);
    }

    /// <summary>True for a device stream, false for a pull-only stream.</summary>
    public bool IsDevice { get; }

    /// <summary>SDL's audio driver ("wasapi"), or null for a pull-only stream.</summary>
    public string? Driver { get; private set; }

    /// <summary>The playback device's name, or null for a pull-only stream.</summary>
    public string? DeviceName { get; private set; }

    /// <summary>The device's own rate (SDL converts to it), or 48,000 for a pull-only stream.</summary>
    public int DeviceRate { get; private set; } = SampleRate;

    /// <summary>The device's channel count, or 2 for a pull-only stream.</summary>
    public int DeviceChannels { get; private set; } = Channels;

    /// <summary>The device buffer in sample frames, or 0 for a pull-only stream.</summary>
    public int DeviceBufferFrames { get; private set; }

    /// <summary>Frames the callback has rendered so far.</summary>
    public long FramesRendered => Interlocked.Read(ref _framesRendered);

    /// <summary>SDL get-callbacks so far.</summary>
    public long Callbacks => Interlocked.Read(ref _callbacks);

    /// <summary>The first exception the render callback threw (it then outputs silence), or null.</summary>
    public string? CallbackError => Volatile.Read(ref _callbackError);

    /// <summary>Opens the default playback device, paused. Call <see cref="Start"/> to play.</summary>
    public static AudioOutput OpenDevice(AudioRenderCallback render)
    {
        ArgumentNullException.ThrowIfNull(render);
        Sdl.Acquire(PlatformSubsystems.Audio);
        var o = new AudioOutput(render, device: true);
        try
        {
            SDL_AudioSpec spec = Spec();
            o._stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, &OnGet, GCHandle.ToIntPtr(o._self));
            if (o._stream == null)
            {
                throw new PlatformException("SDL_OpenAudioDeviceStream: " + SDL_GetError());
            }

            SDL_AudioDeviceID dev = SDL_GetAudioStreamDevice(o._stream);
            SDL_AudioSpec devSpec;
            int frames;
            if (SDL_GetAudioDeviceFormat(dev, &devSpec, &frames))
            {
                o.DeviceRate = devSpec.freq;
                o.DeviceChannels = devSpec.channels;
                o.DeviceBufferFrames = frames;
            }

            o.Driver = SDL_GetCurrentAudioDriver();
            o.DeviceName = SDL_GetAudioDeviceName(dev);
            return o;
        }
        catch
        {
            o.Dispose();
            throw;
        }
    }

    /// <summary>Creates a 48 kHz float stereo stream with no device; read it with <see cref="Pull"/>.</summary>
    public static AudioOutput CreatePullOnly(AudioRenderCallback render)
    {
        ArgumentNullException.ThrowIfNull(render);
        Sdl.Acquire(PlatformSubsystems.Audio);
        var o = new AudioOutput(render, device: false);
        try
        {
            SDL_AudioSpec spec = Spec();
            o._stream = SDL_CreateAudioStream(&spec, &spec);
            if (o._stream == null)
            {
                throw new PlatformException("SDL_CreateAudioStream: " + SDL_GetError());
            }

            if (!SDL_SetAudioStreamGetCallback(o._stream, &OnGet, GCHandle.ToIntPtr(o._self)))
            {
                throw new PlatformException("SDL_SetAudioStreamGetCallback: " + SDL_GetError());
            }

            return o;
        }
        catch
        {
            o.Dispose();
            throw;
        }
    }

    /// <summary>Pull mode: reads interleaved frames through SDL's stream; returns frames read.</summary>
    public int Pull(Span<float> interleaved)
    {
        if (IsDevice)
        {
            throw new InvalidOperationException("Pull is for pull-only streams");
        }

        ObjectDisposedException.ThrowIf(_stream == null, this);
        int bytes = interleaved.Length / Channels * BytesPerFrame;
        fixed (float* p = interleaved)
        {
            int got = SDL_GetAudioStreamData(_stream, (nint)p, bytes);
            if (got < 0)
            {
                throw new PlatformException("SDL_GetAudioStreamData: " + SDL_GetError());
            }

            return got / BytesPerFrame;
        }
    }

    /// <summary>Device mode: starts (resumes) playback.</summary>
    public void Start()
    {
        if (IsDevice && !SDL_ResumeAudioStreamDevice(_stream))
        {
            throw new PlatformException("SDL_ResumeAudioStreamDevice: " + SDL_GetError());
        }
    }

    /// <summary>Device mode: pauses playback.</summary>
    public void Pause()
    {
        if (IsDevice)
        {
            SDL_PauseAudioStreamDevice(_stream);
        }
    }

    /// <summary>Closes the stream (and its device) and releases the audio subsystem.</summary>
    public void Dispose()
    {
        if (_stream != null)
        {
            SDL_DestroyAudioStream(_stream); // returns after the callback has finished
            _stream = null;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
            Sdl.Release(PlatformSubsystems.Audio);
        }
    }

    private static SDL_AudioSpec Spec() => new() { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = Channels, freq = SampleRate };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnGet(nint userdata, SDL_AudioStream* stream, int additionalAmount, int totalAmount)
    {
        if (userdata == 0 || GCHandle.FromIntPtr(userdata).Target is not AudioOutput o)
        {
            return;
        }

        o.Feed(stream, additionalAmount);
    }

    private void Feed(SDL_AudioStream* stream, int additionalBytes)
    {
        Interlocked.Increment(ref _callbacks);
        int frames = (additionalBytes + BytesPerFrame - 1) / BytesPerFrame;
        while (frames > 0)
        {
            int n = Math.Min(frames, MaxPeriodFrames);
            Span<float> buf = _period.AsSpan(0, n * Channels);
            if (_callbackError is null)
            {
                try
                {
                    _render(buf);
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref _callbackError, e.ToString(), null);
                    buf.Clear();
                }
            }
            else
            {
                buf.Clear();
            }

            fixed (float* p = _period)
            {
                if (!SDL_PutAudioStreamData(stream, (nint)p, n * BytesPerFrame))
                {
                    Interlocked.CompareExchange(ref _callbackError, "SDL_PutAudioStreamData: " + SDL_GetError(), null);
                    return;
                }
            }

            Interlocked.Add(ref _framesRendered, n);
            frames -= n;
        }
    }
}

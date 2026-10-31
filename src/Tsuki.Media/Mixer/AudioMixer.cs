// The float mixer (ARCHITECTURE 3.4 "audio thread: SDL3 stream callback -> Mixer.Render(<= 1024 frames, float32
// stereo, 48 kHz)", 7.1, 7.2). Render is the AudioRenderCallback of Tsuki.Platform.AudioOutput: it runs on SDL's
// audio thread (device mode) or on the caller's thread (pull mode, the headless test boundary). The arithmetic is
// the one tools/oracle/audio.py render_mix defines, so a render is sample-exact against the oracle.
using System.Collections.Concurrent;
using Tsuki.Engine.Contracts;

namespace Tsuki.Media.Mixer;

/// <summary>Mixes clip and stream voices into float32 stereo at 48 kHz; nothing is clipped before the device.</summary>
public sealed class AudioMixer
{
    /// <summary>Output rate (the PS4 rate).</summary>
    public const int SampleRate = 48_000;

    /// <summary>Output channels.</summary>
    public const int Channels = 2;

    /// <summary>The largest period rendered in one piece (the engine's grain, audio.md 7).</summary>
    public const int MaxPeriodFrames = 1024;

    /// <summary>Voice slots: streams 0..25 (AudioStreams), the system voices 0x2C..0x2F and more.</summary>
    public const int MaxVoices = 64;

    /// <summary>The default gain / pan ramp: one 60 Hz step (800 frames).</summary>
    public const int DefaultRampFrames = SampleRate / 60;

    /// <summary>Output frames a change of <see cref="MasterGainTarget"/> takes to complete (half a second: the fade of the window losing or regaining the focus, package K21).</summary>
    public const int MasterFadeFrames = SampleRate / 2;

    private readonly ConcurrentQueue<MixerCommand> _inbox = new();
    private readonly List<MixerCommand> _pending = new(256);
    private readonly Voice[] _voices = new Voice[MaxVoices];
    private long _frame;
    private long _posted;
    private long _applied;
    private float _masterTarget = 1f;
    private float _master = 1f;

    /// <summary>Creates a mixer with every voice idle.</summary>
    public AudioMixer()
    {
        for (int i = 0; i < _voices.Length; i++)
        {
            _voices[i] = new Voice();
        }
    }

    /// <summary>Output frames rendered so far.</summary>
    public long FramesRendered => Interlocked.Read(ref _frame);

    /// <summary>Commands posted so far (any thread). A caller that must know when its command has taken effect remembers this value after its post.</summary>
    public long CommandsPosted => Interlocked.Read(ref _posted);

    /// <summary>
    /// Commands the audio thread has applied so far. Commands apply in posting order, so a command posted as number n (the value of
    /// <see cref="CommandsPosted"/> right after its post) has taken effect once this is at least n (commands stamped with a future output
    /// frame excepted).
    /// </summary>
    public long CommandsApplied => Interlocked.Read(ref _applied);

    /// <summary>Posts a command (any thread). It applies before frame <see cref="MixerCommand.Frame"/> or the next frame.</summary>
    public void Post(in MixerCommand command)
    {
        if ((uint)command.Voice >= MaxVoices)
        {
            throw new ArgumentOutOfRangeException(nameof(command), command.Voice, $"voice must be 0..{MaxVoices - 1}");
        }

        if (command.Op == MixerOp.Play)
        {
            AudioClip clip = command.Clip ?? throw new ArgumentException("play needs a clip", nameof(command));
            if (clip.Channels is not (1 or 2))
            {
                throw new ArgumentException("1 or 2 channels expected", nameof(command));
            }

            int end = command.Loop && clip.Loop is { } l ? l.End + 1 : clip.FrameCount;
            if (command.StartFrame < 0 || command.StartFrame >= end || end > clip.FrameCount)
            {
                throw new ArgumentOutOfRangeException(nameof(command), command.StartFrame, $"start frame outside [0, {end})");
            }
        }
        else if (command.Op == MixerOp.PlayStream && (command.Source is null || command.Source.Channels is not (1 or 2)))
        {
            throw new ArgumentException("play stream needs a 1- or 2-channel source", nameof(command));
        }

        _inbox.Enqueue(command);
        Interlocked.Increment(ref _posted);
    }

    /// <summary>True while <paramref name="voice"/> is playing (as of the last rendered period).</summary>
    public bool IsPlaying(int voice) => Volatile.Read(ref _voices[voice].Active);

    /// <summary>
    /// The gain the whole mix is multiplied by after the voices (package K21: 0 while the window is in the background with Mute in Background on, else 1). Any
    /// thread may set it; the audio thread glides towards it over <see cref="MasterFadeFrames"/> frames, so a change is a fade and never a click. The voices' own
    /// gains and the engine's volume bytes are untouched by it.
    /// </summary>
    public float MasterGainTarget
    {
        get => Volatile.Read(ref _masterTarget);
        set => Volatile.Write(ref _masterTarget, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>The master gain at the end of the last rendered period (where the fade stands).</summary>
    public float MasterGain => Volatile.Read(ref _master);

    /// <summary>
    /// Renders <c>interleaved.Length / 2</c> frames (any length) in periods of at most <see cref="MaxPeriodFrames"/>,
    /// applying due commands between periods. Audio thread only.
    /// </summary>
    public void Render(Span<float> interleaved)
    {
        if (interleaved.Length % Channels != 0)
        {
            throw new ArgumentException("an interleaved stereo buffer has an even length", nameof(interleaved));
        }

        Drain();
        interleaved.Clear();
        int total = interleaved.Length / Channels;
        int done = 0;
        long frame = _frame;
        while (done < total)
        {
            while (_pending.Count > 0 && _pending[0].Frame <= frame)
            {
                Apply(_pending[0], frame);
                _pending.RemoveAt(0);
            }

            int n = Math.Min(MaxPeriodFrames, total - done);
            if (_pending.Count > 0)
            {
                n = (int)Math.Min(n, _pending[0].Frame - frame);
            }

            Span<float> period = interleaved.Slice(done * Channels, n * Channels);
            foreach (Voice v in _voices)
            {
                if (v.Active)
                {
                    v.Mix(period, frame);
                }
            }

            ApplyMasterGain(period);
            frame += n;
            done += n;
            Interlocked.Exchange(ref _frame, frame);
        }
    }

    // the master gain glides linearly towards its target, one step per output frame; at 1 with nothing to do the period is left as it is
    private void ApplyMasterGain(Span<float> period)
    {
        float target = Volatile.Read(ref _masterTarget);
        float g = _master;
        if (g == 1f && target == 1f)
        {
            return;
        }

        const float step = 1f / MasterFadeFrames;
        for (int i = 0; i < period.Length; i += Channels)
        {
            if (g != target)
            {
                // the last step lands on the target exactly (the float sum of the steps would miss it by a few millionths)
                g = MathF.Abs(target - g) <= step ? target : g < target ? g + step : g - step;
            }

            period[i] *= g;
            period[i + 1] *= g;
        }

        Volatile.Write(ref _master, g);
    }

    private void Drain()
    {
        while (_inbox.TryDequeue(out MixerCommand c))
        {
            if (c.Frame < _frame)
            {
                c = c with { Frame = _frame };
            }

            // stable insert: after every pending command of the same or an earlier frame (oracle: sorted by frame, list order)
            int at = _pending.Count;
            while (at > 0 && _pending[at - 1].Frame > c.Frame)
            {
                at--;
            }

            _pending.Insert(at, c);
        }
    }

    private void Apply(in MixerCommand c, long frame)
    {
        int ramp = c.RampFrames < 0 ? DefaultRampFrames : c.RampFrames;
        switch (c.Op)
        {
            case MixerOp.Play:
                _voices[c.Voice].Start(c.Clip!, c.Loop, c.Repeats, c.StartFrame, c.Gain, c.Pan);
                break;
            case MixerOp.PlayStream:
                _voices[c.Voice].Start(c.Source!, c.Gain, c.Pan);
                break;
            case MixerOp.Stop:
                _voices[c.Voice].Stop();
                break;
            case MixerOp.StopAll:
                foreach (Voice v in _voices)
                {
                    v.Stop();
                }

                break;
            case MixerOp.SetGain:
                if (_voices[c.Voice].Active)
                {
                    _voices[c.Voice].Gain.Set(c.Gain, frame, ramp);
                }

                break;
            case MixerOp.SetPan:
                if (_voices[c.Voice].Active)
                {
                    _voices[c.Voice].Pan.Set(c.Pan, frame, ramp);
                }

                break;
            default:
                throw new InvalidOperationException($"unknown mixer op {c.Op}");
        }

        Interlocked.Increment(ref _applied);
    }
}

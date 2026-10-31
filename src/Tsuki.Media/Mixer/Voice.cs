using Tsuki.Engine.Contracts;

namespace Tsuki.Media.Mixer;

/// <summary>
/// A linear ramp of a float32 value (gain or pan). Set at frame F with length N from the current value g0 to g1, frame
/// F + k - 1 (k = 1..N) gets <c>g0 + (g1 - g0) * ((float)k / N)</c> and every later frame g1 (oracle: <c>_Ramp</c>).
/// </summary>
internal struct Ramp
{
    private float _g0;
    private float _g1;
    private long _start;
    private int _n;

    public Ramp(float value)
    {
        _g0 = value;
        _g1 = value;
        _start = 0;
        _n = 0;
    }

    /// <summary>True when every frame from <paramref name="t"/> on has the target value.</summary>
    public readonly bool ConstantFrom(long t) => _n == 0 || t - _start + 1 >= _n;

    /// <summary>The value of output frame <paramref name="t"/>.</summary>
    public readonly float At(long t)
    {
        if (_n == 0)
        {
            return _g1;
        }

        long k = t - _start + 1;
        if (k >= _n)
        {
            return _g1;
        }

        if (k <= 0)
        {
            return _g0;
        }

        return _g0 + ((_g1 - _g0) * ((float)k / _n));
    }

    /// <summary>Starts a ramp to <paramref name="target"/> at frame <paramref name="frame"/> over <paramref name="frames"/> frames.</summary>
    public void Set(float target, long frame, int frames)
    {
        float current = At(frame - 1);
        _g1 = target;
        if (frames <= 0)
        {
            _g0 = target;
            _n = 0;
        }
        else
        {
            _g0 = current;
            _start = frame;
            _n = frames;
        }
    }
}

/// <summary>One mixer voice: a clip with its loop, or a streaming source; gain and pan ramps; the engine pan law.</summary>
internal sealed class Voice
{
    public bool Active;
    public Ramp Gain;
    public Ramp Pan;

    private readonly float[] _scratch = new float[AudioMixer.MaxPeriodFrames * 2];
    private ReadOnlyMemory<float> _samples;
    private IMixerSource? _source;
    private int _channels;
    private int _frames;
    private bool _loop;
    private int _loopStart;
    private int _loopEnd;
    private int _repeats;
    private int _pos;

    public void Start(AudioClip clip, bool loop, int repeats, int startFrame, float gain, float pan)
    {
        _source = null;
        _samples = clip.Samples;
        _channels = clip.Channels;
        _frames = clip.FrameCount;
        _loop = loop && clip.Loop is not null;
        _loopStart = clip.Loop?.Start ?? 0;
        _loopEnd = clip.Loop?.End ?? 0;
        _repeats = repeats;
        _pos = startFrame;
        Gain = new Ramp(gain);
        Pan = new Ramp(pan);
        Volatile.Write(ref Active, true);
    }

    public void Start(IMixerSource source, float gain, float pan)
    {
        _source = source;
        _samples = default;
        _channels = source.Channels;
        _loop = false;
        Gain = new Ramp(gain);
        Pan = new Ramp(pan);
        Volatile.Write(ref Active, true);
    }

    public void Stop()
    {
        _source = null;
        _samples = default;
        Volatile.Write(ref Active, false);
    }

    /// <summary>Adds this voice into <paramref name="period"/> (interleaved stereo); <paramref name="t0"/> is its first output frame.</summary>
    public void Mix(Span<float> period, long t0)
    {
        int need = period.Length / 2;
        int done = 0;
        while (done < need && Active)
        {
            int n;
            ReadOnlySpan<float> s;
            if (_source is { } src)
            {
                Span<float> buf = _scratch.AsSpan(0, (need - done) * _channels);
                n = src.Read(buf);
                if (n <= 0)
                {
                    if (src.Ended)
                    {
                        Stop();
                    }

                    return; // underflow: silence for the rest of the period
                }

                s = buf[..(n * _channels)];
            }
            else
            {
                int segEnd = _loop ? _loopEnd + 1 : _frames;
                n = Math.Min(need - done, segEnd - _pos);
                s = _samples.Span.Slice(_pos * _channels, n * _channels);
            }

            Add(period.Slice(done * 2, n * 2), s, t0 + done);
            done += n;
            if (_source is not null)
            {
                if (done < need)
                {
                    if (_source.Ended)
                    {
                        Stop();
                    }

                    return; // one read per period: a short read is an underflow, the rest of the period is silence
                }
            }
            else
            {
                _pos += n;
                int segEnd = _loop ? _loopEnd + 1 : _frames;
                if (_pos == segEnd)
                {
                    if (_loop && _repeats != 0)
                    {
                        _pos = _loopStart;
                        if (_repeats > 0)
                        {
                            _repeats--;
                        }
                    }
                    else
                    {
                        Stop();
                    }
                }
            }
        }
    }

    private void Add(Span<float> d, ReadOnlySpan<float> s, long t)
    {
        int n = d.Length / 2;
        if (Gain.ConstantFrom(t) && Pan.ConstantFrom(t))
        {
            PanLaw(Gain.At(t), Pan.At(t), out float gl, out float gr);
            if (_channels == 2)
            {
                for (int i = 0; i < d.Length; i += 2)
                {
                    d[i] += gl * s[i];
                    d[i + 1] += gr * s[i + 1];
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    d[2 * i] += gl * s[i];
                    d[(2 * i) + 1] += gr * s[i];
                }
            }

            return;
        }

        for (int i = 0; i < n; i++)
        {
            PanLaw(Gain.At(t + i), Pan.At(t + i), out float gl, out float gr);
            float sl = _channels == 2 ? s[2 * i] : s[i];
            float sr = _channels == 2 ? s[(2 * i) + 1] : s[i];
            d[2 * i] += gl * sl;
            d[(2 * i) + 1] += gr * sr;
        }
    }

    // Engine stereo pan law (audio.md 7, VA 0x118658): q = (p + 1) / 2, gL = min(2 (1 - q), 1), gR = min(2 q, 1).
    private static void PanLaw(float g, float p, out float gl, out float gr)
    {
        float q = (p + 1f) * 0.5f;
        gl = g * MathF.Min(2f * (1f - q), 1f);
        gr = g * MathF.Min(2f * q, 1f);
    }
}

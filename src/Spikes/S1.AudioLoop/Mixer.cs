namespace S1.AudioLoop;

// Inside the namespace, so that the type Tsuki.Engine.Contracts.AudioLoop wins over the namespace S1.AudioLoop.
using Tsuki.Engine.Contracts;

// One voice of the mixer reading a decoded AudioClip with its loop applied to the decoded buffer
// (ARCHITECTURE 7.2): the read index runs from the start frame; when it passes loop.End
// (inclusive) it jumps to loop.Start. Sample-exact by construction: no decoder is involved
// at the seam. Without a loop (or with looping off) the voice stops at the clip end.
internal sealed class LoopingVoice
{
    private readonly ReadOnlyMemory<float> _pcm;
    private readonly int _channels;
    private readonly int _frames;
    private readonly AudioLoop? _loop;
    private readonly int _maxLoopEnds;
    private int _loopEndsReached;
    private int _pos;

    // loopEnds: how many times the loop end may be reached; the last time ends the voice
    // (int.MaxValue = loop forever). loopEnds = 3 from the start = intro + 3 x body, 2 seams,
    // the same as tools/tsuki/audio.py render_loop(pcm, loops=2).
    public LoopingVoice(AudioClip clip, bool looping, int startFrame = 0, int loopEnds = int.MaxValue)
    {
        _pcm = clip.Samples;
        _channels = clip.Channels;
        _frames = clip.FrameCount;
        _loop = looping ? clip.Loop : null;
        _maxLoopEnds = loopEnds;
        if (_channels is not (1 or 2))
        {
            throw new ArgumentException("1 or 2 channels expected", nameof(clip));
        }

        int segEnd = _loop is { } l ? l.End + 1 : _frames;
        if (startFrame < 0 || startFrame >= segEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(startFrame));
        }

        _pos = startFrame;
    }

    public float Gain { get; set; } = 1f;

    // 0 = left, 0.5 = centre, 1 = right. Engine stereo pan law (audio.md 7, VA 0x118658):
    // gL = min(2 (1 - p), 1), gR = min(2 p, 1); at the centre both gains are exactly 1.
    public float Pan { get; set; } = 0.5f;

    public int Position => _pos;

    public int Seams { get; private set; }

    public bool Finished { get; private set; }

    public long FramesOut { get; private set; }

    // Output frame (counted from the voice start) of the first frame after each seam.
    public List<long> SeamOutputFrames { get; } = [];

    // Adds the voice into dst (interleaved stereo float32). Returns the frames produced.
    public int MixInto(Span<float> dst)
    {
        int need = dst.Length / 2;
        int done = 0;
        ReadOnlySpan<float> src = _pcm.Span;
        float gl = Gain * MathF.Min(2f * (1f - Pan), 1f);
        float gr = Gain * MathF.Min(2f * Pan, 1f);
        while (done < need && !Finished)
        {
            int segEnd = _loop is { } l ? l.End + 1 : _frames;
            int n = Math.Min(need - done, segEnd - _pos);
            Span<float> d = dst.Slice(done * 2, n * 2);
            if (_channels == 2)
            {
                ReadOnlySpan<float> s = src.Slice(_pos * 2, n * 2);
                for (int i = 0; i < s.Length; i += 2)
                {
                    d[i] += gl * s[i];
                    d[i + 1] += gr * s[i + 1];
                }
            }
            else
            {
                ReadOnlySpan<float> s = src.Slice(_pos, n);
                for (int i = 0; i < s.Length; i++)
                {
                    d[2 * i] += gl * s[i];
                    d[(2 * i) + 1] += gr * s[i];
                }
            }

            _pos += n;
            done += n;
            FramesOut += n;
            if (_pos == segEnd)
            {
                if (_loop is { } lp && ++_loopEndsReached < _maxLoopEnds)
                {
                    _pos = lp.Start;
                    Seams++;
                    SeamOutputFrames.Add(FramesOut);
                }
                else
                {
                    Finished = true;
                }
            }
        }

        return done;
    }
}

// The float32 stereo 48 kHz mix (ARCHITECTURE 7.1): periods of at most 1024 frames, nothing
// clipped before the device.
internal sealed class Mixer
{
    public const int MaxPeriodFrames = 1024;

    private readonly List<LoopingVoice> _voices = [];

    public IReadOnlyList<LoopingVoice> Voices => _voices;

    public void Add(LoopingVoice v) => _voices.Add(v);

    public bool AllFinished => _voices.TrueForAll(v => v.Finished);

    // Renders dst.Length / 2 frames (any length; split into periods of <= 1024 frames).
    public void Render(Span<float> dst)
    {
        dst.Clear();
        for (int o = 0; o < dst.Length; o += MaxPeriodFrames * 2)
        {
            Span<float> period = dst.Slice(o, Math.Min(MaxPeriodFrames * 2, dst.Length - o));
            foreach (LoopingVoice v in _voices)
            {
                v.MixInto(period);
            }
        }
    }
}

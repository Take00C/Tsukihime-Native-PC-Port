// The AudioCommand -> mixer translation (ARCHITECTURE 3.2, 7.1, 7.3; docs/formats/sound_runtime.md 2.5, 10.1): turns the sound
// library calls the SoundModel makes into mixer voice commands. Stream n plays on mixer voice n; a system sound is a left / right
// pair on the system voices (MixerVoices); clips come from IGameData.Audio through a cache.
using Tsuki.Engine.Contracts;
using Tsuki.Media.Mixer;

namespace Tsuki.Media.Sound;

/// <summary>Options of an <see cref="AudioCommandPlayer"/>.</summary>
/// <param name="AsyncDecode">
/// Decode clips on the thread pool: a play whose clip is not cached waits (as a pending play that later commands of the stream still
/// modify) and starts in a later <see cref="AudioCommandPlayer.Pump"/>. Off: <see cref="AudioCommandPlayer.Perform(ReadOnlySpan{AudioCommand})"/>
/// decodes on the calling thread (a 172 s BGM takes about 0.1 s).
/// </param>
/// <param name="CacheBudgetBytes">Decoded PCM the clip cache keeps.</param>
public sealed record AudioPlayerOptions(bool AsyncDecode = false, long CacheBudgetBytes = 256L << 20);

/// <summary>A clip that could not be played (missing entry, decode error).</summary>
/// <param name="Bank">The bank.</param>
/// <param name="Entry">The entry.</param>
/// <param name="Error">What went wrong.</param>
public sealed record AudioPlayFailure(AudioBank Bank, int Entry, Exception Error);

/// <summary>
/// Performs <see cref="AudioCommand"/>s on a <see cref="AudioMixer"/>. Not thread-safe: call it from the engine thread (the thread
/// that steps the SoundModel); the mixer's audio thread only sees the commands it posts.
/// </summary>
/// <remarks>
/// <para>
/// A <c>Play</c> becomes a pending play. The commands that follow it in the same batch (<c>set_volume</c>, <c>set_pan</c>,
/// <c>set_loop</c>), which the sound library applies to the stream the instant it exists, change the pending play's gain, pan and
/// loop instead of ramping a running voice, so an SE starts at its pan with no 16 ms sweep. At the end of the batch the pending
/// plays whose clip is ready are posted to the mixer.
/// </para>
/// <para>
/// <c>set_volume</c> and <c>set_pan</c> on a running voice ramp over one 60 Hz step (the mixer's default ramp), so the SoundModel's
/// per-frame volume staircase becomes a continuous fade. <c>set_start_sample</c> is a register the next <c>play</c> of the stream
/// consumes. <c>set_loop</c> on a running voice is not applied (the model sets it on the BGM slot that is about to fade out; the mixer
/// has no such operation): the loop of a voice is the loop flag of its <c>play</c>. <c>stop</c> with a fade time stops at once (every
/// caller passes 0). <c>set_flag6</c> has no equivalent.
/// </para>
/// <para>
/// A clip without a loop chunk plays once even if the play asks for a loop (every SE and BGM of the PS4 data has a smpl chunk;
/// voices and system sounds never loop).
/// </para>
/// </remarks>
public sealed class AudioCommandPlayer : IDisposable
{
    private sealed class Pending
    {
        public AudioBank Bank;
        public int Entry;
        public AudioClip? Clip;
        public Task<AudioClip>? Loading;
        public bool Loop;
        public int Repeats = -1;
        public int StartFrame;
        public float Gain;
        public float Pan;
    }

    private sealed class StreamState
    {
        public bool Playing;
        public bool Posted;
        public long Serial;
        public int StartSample;
        public Pending? Pending;
    }

    private readonly AudioMixer _mixer;
    private readonly ClipCache _cache;
    private readonly bool _async;
    private readonly StreamState[] _streams = Enumerable.Range(0, AudioStreams.Count).Select(_ => new StreamState()).ToArray();
    private readonly List<AudioPlayFailure> _failures = [];

    /// <summary>Creates a player that decodes through <paramref name="data"/>.</summary>
    public AudioCommandPlayer(IGameData data, AudioMixer mixer, AudioPlayerOptions? options = null)
        : this((data ?? throw new ArgumentNullException(nameof(data))).Audio, mixer, options)
    {
    }

    /// <summary>Creates a player over a clip source (<see cref="IGameData.Audio"/>).</summary>
    public AudioCommandPlayer(Func<AudioBank, int, AudioClip> clips, AudioMixer mixer, AudioPlayerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clips);
        _mixer = mixer ?? throw new ArgumentNullException(nameof(mixer));
        options ??= new AudioPlayerOptions();
        _async = options.AsyncDecode;
        _cache = new ClipCache(clips, options.CacheBudgetBytes);
    }

    /// <summary>The decoded-clip cache.</summary>
    public ClipCache Cache => _cache;

    /// <summary>Clips that could not be played, oldest first.</summary>
    public IReadOnlyList<AudioPlayFailure> Failures => _failures;

    /// <summary>Plays started on the mixer so far.</summary>
    public long PlaysPosted { get; private set; }

    /// <summary>
    /// Stops decoding and waits for the decodes that are running (see <see cref="ClipCache.Shutdown"/>); plays that are already on the mixer go on. Call it
    /// before the data source is disposed.
    /// </summary>
    /// <param name="timeout">The longest wait.</param>
    /// <returns>True when no decode is running any more.</returns>
    public bool Shutdown(TimeSpan timeout) => _cache.Shutdown(timeout);

    /// <inheritdoc />
    public void Dispose() => _cache.Dispose();

    /// <summary>Starts decoding an entry on a worker (a BGM about to play); the play then finds it cached.</summary>
    public void Prefetch(AudioBank bank, int entry) => _cache.Prefetch(bank, entry);

    /// <summary>Performs one command.</summary>
    public void Perform(in AudioCommand command)
    {
        Apply(command);
        Pump();
    }

    /// <summary>Performs the commands of one step in order, then starts the plays whose clip is ready.</summary>
    public void Perform(ReadOnlySpan<AudioCommand> commands)
    {
        foreach (AudioCommand c in commands)
        {
            Apply(c);
        }

        Pump();
    }

    /// <summary>Performs a batch (the SoundModel's <c>TakeCommands()</c>).</summary>
    public void Perform(IReadOnlyList<AudioCommand> commands)
    {
        foreach (AudioCommand c in commands)
        {
            Apply(c);
        }

        Pump();
    }

    /// <summary>Starts every pending play whose clip has arrived (call it once per step when <see cref="AudioPlayerOptions.AsyncDecode"/> is on).</summary>
    public void Pump()
    {
        for (int s = 0; s < _streams.Length; s++)
        {
            StreamState st = _streams[s];
            if (st.Pending is not { } p)
            {
                continue;
            }

            if (p.Clip is null && p.Loading is { IsCompleted: true } done)
            {
                if (done.IsFaulted)
                {
                    Fail(p, done.Exception!.GetBaseException());
                    st.Pending = null;
                    st.Playing = false;
                    continue;
                }

                p.Clip = done.Result;
            }

            if (p.Clip is not null)
            {
                PostPlay(s, st, p);
            }
        }
    }

    /// <summary>
    /// True while the stream plays as the sound library would answer <c>is_playing</c>: from the <c>play</c> command (even while its clip
    /// still decodes) until a one-shot has ended or a stop / close.
    /// </summary>
    public bool IsPlaying(int stream)
    {
        StreamState st = _streams[MixerVoices.Stream(stream)];
        if (!st.Playing)
        {
            return false;
        }

        if (st.Pending is not null)
        {
            return true;
        }

        // the play was posted; until the audio thread has applied it the voice is not yet active, but it counts as playing
        return _mixer.CommandsApplied < st.Serial || _mixer.IsPlaying(MixerVoices.Stream(stream));
    }

    /// <summary>Stops every sound (scene reset, load).</summary>
    public void StopAll()
    {
        foreach (StreamState st in _streams)
        {
            st.Pending = null;
            st.Playing = false;
            st.Posted = false;
            st.StartSample = 0;
        }

        _mixer.Post(MixerCommand.StopAll());
    }

    private void Apply(in AudioCommand c)
    {
        switch (c.Op)
        {
            case AudioOp.OpenFile or AudioOp.OpenHandle:
                _ = MixerVoices.Stream(c.Stream);       // a stream number outside 0..25 is a model bug
                break;
            case AudioOp.Close or AudioOp.Stop:
                StopStream(c.Stream);
                break;
            case AudioOp.Play:
                BeginPlay(c);
                break;
            case AudioOp.SetVolume:
                {
                    StreamState st = _streams[MixerVoices.Stream(c.Stream)];
                    if (st.Pending is { } p)
                    {
                        p.Gain = c.Value0;
                    }
                    else if (st.Posted)
                    {
                        _mixer.Post(MixerCommand.SetGain(c.Stream, c.Value0, RampFrames(c.Value1)));
                    }

                    break;
                }

            case AudioOp.SetVolume0:
                {
                    StreamState st = _streams[MixerVoices.Stream(c.Stream)];
                    if (st.Pending is { } p)
                    {
                        p.Gain = c.Value0;
                    }

                    break;
                }

            case AudioOp.SetPan:
                {
                    StreamState st = _streams[MixerVoices.Stream(c.Stream)];
                    float pan = Math.Clamp(c.Value0, -1f, 1f);
                    if (st.Pending is { } p)
                    {
                        p.Pan = pan;
                    }
                    else if (st.Posted)
                    {
                        _mixer.Post(MixerCommand.SetPan(c.Stream, pan));
                    }

                    break;
                }

            case AudioOp.SetLoop:
                {
                    StreamState st = _streams[MixerVoices.Stream(c.Stream)];
                    if (st.Pending is { } p)
                    {
                        p.Loop = c.Arg0 != 0;
                        p.Repeats = c.Arg1 < 0 ? -1 : Math.Max(0, c.Arg1 - 1);
                    }

                    break;
                }

            case AudioOp.SetStartSample:
                _streams[MixerVoices.Stream(c.Stream)].StartSample = c.Arg0;
                break;
            case AudioOp.SetFlag6:
                break;
            case AudioOp.PlaySystem:
                PlaySystem(c);
                break;
            default:
                throw new InvalidOperationException($"unknown audio op {c.Op}");
        }
    }

    private static int RampFrames(float time) => time > 0f ? (int)Math.Round(time * AudioMixer.DefaultRampFrames) : -1;

    private void BeginPlay(in AudioCommand c)
    {
        StreamState st = _streams[MixerVoices.Stream(c.Stream)];
        var p = new Pending
        {
            Bank = c.Bank,
            Entry = c.Entry,
            Loop = c.Arg0 != 0,
            Gain = c.Value0,
            StartFrame = st.StartSample,
        };
        st.StartSample = 0;
        st.Pending = null;
        try
        {
            if (_async)
            {
                AudioBank bank = c.Bank;
                int entry = c.Entry;
                p.Loading = Task.Run(() => _cache.Get(bank, entry));
            }
            else
            {
                p.Clip = _cache.Get(c.Bank, c.Entry);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(p, e);
            st.Playing = false;
            return;
        }

        st.Pending = p;
        st.Playing = true;
    }

    private void PostPlay(int stream, StreamState st, Pending p)
    {
        AudioClip clip = p.Clip!;
        st.Pending = null;
        if (clip.FrameCount == 0 || clip.Channels is not (1 or 2))
        {
            Fail(p, new InvalidDataException($"clip {p.Bank}#{p.Entry} has {clip.FrameCount} frames and {clip.Channels} channels"));
            st.Playing = false;
            return;
        }

        bool loop = p.Loop && clip.Loop is not null;
        int end = loop ? clip.Loop!.Value.End + 1 : clip.FrameCount;
        if (end > clip.FrameCount)
        {
            end = clip.FrameCount;
            loop = false;
        }

        int start = p.StartFrame >= 0 && p.StartFrame < end ? p.StartFrame : 0;
        _mixer.Post(MixerCommand.Play(MixerVoices.Stream(stream), clip, loop, p.Gain, p.Pan, p.Repeats, start));
        st.Serial = _mixer.CommandsPosted;
        st.Posted = true;
        PlaysPosted++;
    }

    private void StopStream(int stream)
    {
        StreamState st = _streams[MixerVoices.Stream(stream)];
        st.Pending = null;
        st.Playing = false;
        if (st.Posted)
        {
            st.Posted = false;
            _mixer.Post(MixerCommand.Stop(stream));
        }
    }

    private void PlaySystem(in AudioCommand c)
    {
        int left = c.Entry;
        int right = c.Arg0;
        if (left <= 0)
        {
            return;
        }

        int voice = MixerVoices.System(c.Stream);
        try
        {
            AudioClip l = _cache.Get(AudioBank.Vag, left);
            if (right >= 0)
            {
                AudioClip r = _cache.Get(AudioBank.Vag, right);
                _mixer.Post(MixerCommand.Play(voice, l, false, c.Value0, -1f));
                _mixer.Post(MixerCommand.Play(MixerVoices.SystemRight(c.Stream), r, false, c.Value0, 1f));
            }
            else
            {
                _mixer.Post(MixerCommand.Play(voice, l, false, c.Value0, 0f));
            }

            PlaysPosted++;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _failures.Add(new AudioPlayFailure(AudioBank.Vag, left, e));
        }
    }

    private void Fail(Pending p, Exception e) => _failures.Add(new AudioPlayFailure(p.Bank, p.Entry, e));
}

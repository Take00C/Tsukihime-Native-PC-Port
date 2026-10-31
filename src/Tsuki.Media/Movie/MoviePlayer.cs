// One open movie (ARCHITECTURE 3.4, 7.5; S2.md 9): the pipeline, its audio voice in the mixer, the audio clock and the 60 Hz
// frame choice. The 60 Hz tick calls Tick(nominal tick time); the frame clock is used when the track is missing or the mixer
// never pulls it. Lifted from spike S2 Player.TickLoop.Tick.
using System.Diagnostics;
using Tsuki.Engine.Contracts;
using Tsuki.Media.Mixer;

namespace Tsuki.Media.Movie;

/// <summary>Options of a <see cref="MoviePlayer"/>.</summary>
/// <param name="Pipeline">The decode options.</param>
/// <param name="CadenceLockSeconds">The frame choice's cadence lock (4 ms, ARCHITECTURE 3.4).</param>
/// <param name="AudioLatencySeconds">The device buffer subtracted from the audio clock (480 frames = 10 ms on WASAPI).</param>
/// <param name="AudioClockTimeoutSeconds">With no mixer pull for this long after the voice started, the frame clock takes over.</param>
/// <param name="PrerollAudioFrames">Audio frames to hold in the ring before the voice starts (0.1 s).</param>
public sealed record MoviePlayerOptions(
    MoviePipelineOptions? Pipeline = null, double CadenceLockSeconds = FrameSelector.DefaultCadenceLockSeconds,
    double AudioLatencySeconds = 0.010, double AudioClockTimeoutSeconds = 2.0, int PrerollAudioFrames = 4800);

/// <summary>One movie playing in a slot. Every member except the pipeline's threads runs on the thread that ticks.</summary>
public sealed class MoviePlayer : IDisposable
{
    private readonly MoviePlayerOptions _options;
    private readonly AudioMixer? _mixer;
    private readonly int _voice;
    private readonly FrameSelector _selector;
    private readonly FrameStamp[] _stamps = new FrameStamp[16];
    private readonly double _frameDuration;
    private AudioClock? _audioClock;
    private float _volume;
    private bool _prerolled;
    private bool _frameClock;
    private double _basePts;
    private long _playStart;
    private long _voiceStarted;
    private double _lastAudioClock = double.NaN;
    private long _lastAudioChange;
    private bool _ended;
    private VideoFrame? _current;

    /// <summary>
    /// Opens <paramref name="path"/> and starts its threads. FFmpeg must be loaded. With a <paramref name="mixer"/> the audio
    /// track plays on the movie voice of <paramref name="slot"/>; without one the frame clock runs the movie.
    /// </summary>
    public MoviePlayer(int slot, int movieIndex, string path, float volume, AudioMixer? mixer, MoviePlayerOptions? options = null)
    {
        Slot = slot;
        MovieIndex = movieIndex;
        _options = options ?? new MoviePlayerOptions();
        _mixer = mixer;
        _voice = MixerVoices.Movie(slot);
        _volume = volume;
        _selector = new FrameSelector(_options.CadenceLockSeconds);
        Pipeline = new MoviePipeline(path, _options.Pipeline);
        _frameDuration = 1.0 / Pipeline.Fps;
        if (mixer is null)
        {
            Pipeline.DiscardAudio();                 // nobody pulls the audio: keep it from blocking the video
        }

        Pipeline.Start();
    }

    /// <summary>The movie slot.</summary>
    public int Slot { get; }

    /// <summary>The movie table index playing.</summary>
    public int MovieIndex { get; }

    /// <summary>The decode pipeline.</summary>
    public MoviePipeline Pipeline { get; }

    /// <summary>The frame choice (counts holds, early switches and drops).</summary>
    public FrameSelector Selector => _selector;

    /// <summary>The frame to draw, or null before the first one.</summary>
    public VideoFrame? Current => _current;

    /// <summary>True once the first frame is ready and the audio voice (if any) has started.</summary>
    public bool Started => _prerolled;

    /// <summary>True when the frame clock, not the audio, runs this movie.</summary>
    public bool UsesFrameClock => _frameClock || _audioClock is null;

    /// <summary>True after the last frame was presented (never for a looping movie).</summary>
    public bool Ended => _ended;

    /// <summary>The first error a movie thread died of, or null (the movie then ends).</summary>
    public Exception? Error => Pipeline.Error;

    /// <summary>Sets the audio volume (0..1, final).</summary>
    public void SetVolume(float volume)
    {
        _volume = volume;
        if (_audioClock is not null)
        {
            _mixer?.Post(MixerCommand.SetGain(_voice, volume));
        }
    }

    /// <summary>
    /// One 60 Hz tick: <paramref name="timestamp"/> is the tick's nominal time in <see cref="Stopwatch"/> ticks. Picks the frame
    /// and reports the movie clock.
    /// </summary>
    public MovieClock Tick(long timestamp)
    {
        if (_ended)
        {
            return Report(Clock(timestamp), playing: false);
        }

        if (!_prerolled && !TryPreroll(timestamp))
        {
            if (Pipeline.Error is not null || (Pipeline.Frames.EndOfStream && Pipeline.Frames.ReadyCount == 0))
            {
                _ended = true; // nothing will ever decode: do not hang the script
            }

            return Report(0, playing: !_ended);
        }

        double clock = Clock(timestamp);
        if (double.IsNaN(clock))
        {
            clock = _basePts - 1; // no mixer callback yet: hold the first frame
        }

        int ready = Pipeline.Frames.PeekStamps(_stamps);
        int consumed = _selector.Tick(clock, _stamps.AsSpan(0, ready));
        for (int i = 0; i < consumed; i++)
        {
            VideoFrame f = Pipeline.Frames.Dequeue();
            if (i == consumed - 1)
            {
                if (_current is not null)
                {
                    Pipeline.Frames.Return(_current);
                }

                _current = f;
            }
            else
            {
                Pipeline.Frames.Return(f); // replaced before any tick showed it
            }
        }

        if (!(_options.Pipeline?.Loop ?? false))
        {
            bool last = Pipeline.Frames.EndOfStream && Pipeline.Frames.ReadyCount == 0;
            if (last && (_current is null || (_current.Index == Pipeline.LastVideoIndex && clock >= _current.Pts + _frameDuration - 1e-9)))
            {
                _ended = true;
            }
        }

        return Report(clock, playing: !_ended);
    }

    private bool TryPreroll(long timestamp)
    {
        VideoFrame? first = Pipeline.Frames.Peek();
        bool audioReady = !Pipeline.HasAudio || _mixer is null
            || (Pipeline.FirstAudioSample >= 0 && (Pipeline.Pcm.BufferedFrames >= _options.PrerollAudioFrames || Pipeline.Pcm.Completed))
            || Pipeline.DemuxEnded;
        if (first is null || !audioReady)
        {
            return false;
        }

        if (Pipeline.HasAudio && _mixer is not null)
        {
            _basePts = Pipeline.FirstAudioSample >= 0 ? Pipeline.FirstAudioSample / (double)AudioStreamDecoderRate : first.Pts;
            _audioClock = new AudioClock(Stopwatch.Frequency, AudioStreamDecoderRate, _options.AudioLatencySeconds, _basePts);
            _mixer.Post(MixerCommand.PlayStream(_voice, new MovieAudioSource(Pipeline.Pcm, _audioClock), _volume));
            _voiceStarted = timestamp;
            _lastAudioChange = timestamp;
        }
        else
        {
            _basePts = first.Pts;
        }

        _playStart = timestamp;
        _prerolled = true;
        return true;
    }

    private const int AudioStreamDecoderRate = 48_000;

    private double Clock(long timestamp)
    {
        if (!_prerolled)
        {
            return 0;
        }

        if (_audioClock is not null && !_frameClock)
        {
            double c = _audioClock.ClockAt(timestamp);
            double sinceChange = (timestamp - _lastAudioChange) / (double)Stopwatch.Frequency;
            if (!double.IsNaN(c))
            {
                if (c != _lastAudioClock)
                {
                    _lastAudioClock = c;
                    _lastAudioChange = timestamp;
                    return c;
                }

                if (_audioClock.Drained || sinceChange <= _options.AudioClockTimeoutSeconds)
                {
                    return c;
                }

                // the clock stopped moving although the movie has not ended: the device stalled mid-play
            }
            else if ((timestamp - _voiceStarted) / (double)Stopwatch.Frequency <= _options.AudioClockTimeoutSeconds)
            {
                return double.NaN;
            }

            // no mixer pull for the timeout (no device, or it stalled): run on the frame clock instead of freezing, and stop feeding the ring
            _frameClock = true;
            _basePts = double.IsNaN(c) ? (_current?.Pts ?? _basePts) : c;
            _playStart = timestamp;
            Pipeline.DiscardAudio();
            _mixer?.Post(MixerCommand.Stop(_voice));
        }

        return _basePts + ((timestamp - _playStart) / (double)Stopwatch.Frequency);
    }

    private MovieClock Report(double clock, bool playing)
    {
        long micros = double.IsNaN(clock) ? 0 : (long)Math.Max(0, Math.Round(clock * 1_000_000));
        return new MovieClock(Slot, MovieIndex, playing, _ended, micros);
    }

    /// <summary>Stops the audio voice and the threads and frees the decoders.</summary>
    public void Dispose()
    {
        if (_audioClock is not null)
        {
            _mixer?.Post(MixerCommand.Stop(_voice));
        }

        Pipeline.Dispose();
    }
}

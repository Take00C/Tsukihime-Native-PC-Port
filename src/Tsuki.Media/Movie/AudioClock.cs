// The movie clock (ARCHITECTURE 7.5; S2.md 9 #2): the media frames the mixer callback has taken, plus the time
// since that callback, minus one device buffer. The extrapolation is clamped to one callback chunk until the
// stream has drained, the clock never runs backwards, and after the drain it runs on from the last value (the
// frame clock of a silent tail). Lifted from spike S2 AudioOut.Clock.

namespace Tsuki.Media.Movie;

/// <summary>An audio-master clock fed by the audio thread and read by the 60 Hz tick (thread-safe).</summary>
public sealed class AudioClock
{
    private readonly Lock _gate = new();
    private readonly int _rate;
    private readonly double _latency;
    private readonly double _frequency;
    private readonly double _basePts;
    private long _mediaFrames;
    private long _lastCallback;
    private bool _haveCallback;
    private int _lastChunk;
    private bool _drained;
    private double _last = double.NegativeInfinity;

    /// <summary>Creates a clock.</summary>
    /// <param name="timestampFrequency">Timestamp ticks per second (<c>Stopwatch.Frequency</c>, or a synthetic clock's).</param>
    /// <param name="sampleRate">Media frames per second (48,000).</param>
    /// <param name="latencySeconds">The device buffer subtracted from the extrapolation (480 frames = 10 ms on WASAPI).</param>
    /// <param name="basePts">The media time of the first frame handed to the device (a seek start).</param>
    public AudioClock(long timestampFrequency, int sampleRate = 48_000, double latencySeconds = 0.010, double basePts = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _frequency = timestampFrequency;
        _rate = sampleRate;
        _latency = latencySeconds;
        _basePts = basePts;
    }

    /// <summary>Media frames handed to the device so far.</summary>
    public long MediaFrames
    {
        get
        {
            lock (_gate)
            {
                return _mediaFrames;
            }
        }
    }

    /// <summary>True once the stream has ended and the source ran dry.</summary>
    public bool Drained
    {
        get
        {
            lock (_gate)
            {
                return _drained;
            }
        }
    }

    /// <summary>
    /// Audio thread: <paramref name="mediaFrames"/> media frames (of <paramref name="requestedFrames"/> asked for) were
    /// handed to the device at <paramref name="timestamp"/>; <paramref name="endOfStream"/> once the source has no more.
    /// </summary>
    public void OnCallback(int mediaFrames, int requestedFrames, long timestamp, bool endOfStream)
    {
        lock (_gate)
        {
            if (mediaFrames > 0 || !_drained)
            {
                _mediaFrames += mediaFrames;
                _lastChunk = mediaFrames;
                _lastCallback = timestamp;
                _haveCallback = true;
            }

            if (endOfStream && mediaFrames < requestedFrames)
            {
                _drained = true;
            }
        }
    }

    /// <summary>The media clock in seconds at <paramref name="timestamp"/>, or NaN before the first callback.</summary>
    public double ClockAt(long timestamp)
    {
        long m, tcb;
        int chunk;
        bool drained;
        lock (_gate)
        {
            if (!_haveCallback)
            {
                return double.NaN;
            }

            m = _mediaFrames;
            tcb = _lastCallback;
            chunk = _lastChunk;
            drained = _drained;
        }

        double since = ((timestamp - tcb) / _frequency) - _latency;
        if (!drained)
        {
            since = Math.Min(since, chunk / (double)_rate);
        }

        double c = _basePts + ((m - chunk) / (double)_rate) + since;
        lock (_gate)
        {
            if (c < _last)
            {
                c = _last; // never run backwards
            }

            _last = c;
        }

        return c;
    }
}

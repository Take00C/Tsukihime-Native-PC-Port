// The movie audio path into the mixer (ARCHITECTURE 7.5; S2.md 9 #1, #2): the movie thread writes float stereo
// 48 kHz into a lock-free ring (4 s), the mixer's stream voice reads it in the SDL callback, and every read moves
// the audio clock the 60 Hz tick samples. Lifted from spike S2 PcmRing and AudioOut.OnCallback.
using System.Diagnostics;
using Tsuki.Media.Mixer;

namespace Tsuki.Media.Movie;

/// <summary>Single-producer single-consumer float32 stereo ring between the movie thread and the audio callback.</summary>
public sealed class PcmRing
{
    private readonly float[] _buf;
    private long _written; // frames
    private long _read;    // frames
    private volatile bool _completed;

    /// <summary>Creates a ring of <paramref name="capacityFrames"/> stereo frames (4 s = 192,000 by default).</summary>
    public PcmRing(int capacityFrames = 4 * 48_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);
        CapacityFrames = capacityFrames;
        _buf = GC.AllocateArray<float>(capacityFrames * 2, pinned: true);
    }

    /// <summary>Capacity in frames.</summary>
    public int CapacityFrames { get; }

    /// <summary>Frames written so far.</summary>
    public long Written => Volatile.Read(ref _written);

    /// <summary>Frames read so far.</summary>
    public long ReadFrames => Volatile.Read(ref _read);

    /// <summary>Frames waiting to be read.</summary>
    public int BufferedFrames => (int)(Written - ReadFrames);

    /// <summary>Frames that can be written now.</summary>
    public int FreeFrames => CapacityFrames - BufferedFrames;

    /// <summary>Set by the producer after its last write.</summary>
    public bool Completed => _completed;

    /// <summary>Producer: no more frames will come.</summary>
    public void Complete() => _completed = true;

    /// <summary>Producer: writes as many frames as fit; returns the count written.</summary>
    public int Write(ReadOnlySpan<float> interleaved)
    {
        int frames = Math.Min(interleaved.Length / 2, FreeFrames);
        long w = _written;
        for (int i = 0; i < frames;)
        {
            int pos = (int)((w + i) % CapacityFrames);
            int run = Math.Min(frames - i, CapacityFrames - pos);
            interleaved.Slice(i * 2, run * 2).CopyTo(_buf.AsSpan(pos * 2, run * 2));
            i += run;
        }

        Volatile.Write(ref _written, w + frames);
        return frames;
    }

    /// <summary>Consumer: reads up to <c>dst.Length / 2</c> frames; returns the count read.</summary>
    public int Read(Span<float> dst)
    {
        long r = _read;
        int frames = (int)Math.Min(dst.Length / 2, Volatile.Read(ref _written) - r);
        for (int i = 0; i < frames;)
        {
            int pos = (int)((r + i) % CapacityFrames);
            int run = Math.Min(frames - i, CapacityFrames - pos);
            _buf.AsSpan(pos * 2, run * 2).CopyTo(dst.Slice(i * 2, run * 2));
            i += run;
        }

        Volatile.Write(ref _read, r + frames);
        return frames;
    }
}

/// <summary>
/// The mixer stream voice of a movie: reads the <see cref="PcmRing"/> and reports every read to the movie's
/// <see cref="AudioClock"/>. An underflow is silence and does not move the clock.
/// </summary>
/// <param name="ring">The ring the movie thread fills.</param>
/// <param name="clock">The clock the tick reads.</param>
/// <param name="timestamp">The timestamp source, in the clock's frequency (default <see cref="Stopwatch.GetTimestamp"/>).</param>
public sealed class MovieAudioSource(PcmRing ring, AudioClock clock, Func<long>? timestamp = null) : IMixerSource
{
    private readonly Func<long> _now = timestamp ?? Stopwatch.GetTimestamp;

    /// <inheritdoc />
    public int Channels => 2;

    /// <inheritdoc />
    public bool Ended => ring.Completed && ring.BufferedFrames == 0;

    /// <summary>Frames requested but not available while the movie still had audio to come.</summary>
    public long UnderrunFrames { get; private set; }

    /// <inheritdoc />
    public int Read(Span<float> interleaved)
    {
        int want = interleaved.Length / 2;
        bool completed = ring.Completed; // read before the data, so a frame written after the check is not lost
        int got = ring.Read(interleaved);
        if (got < want && !completed)
        {
            UnderrunFrames += want - got;
        }

        clock.OnCallback(got, want, _now(), completed);
        return got;
    }
}

// Movie frame choice at the 60 Hz tick (ARCHITECTURE 3.4 "Tick time and the cadence lock", 7.5; S2.md 7, 9 #3).
// The tick reads the movie clock at its NOMINAL grid time (the caller passes that clock value), and a cadence lock
// keeps every 30 fps frame at exactly two ticks when the frame boundaries sit within the lock window of the tick
// grid. A pure function of (frame pts list, clock, ticks shown so far): tested with a synthetic clock, no GPU.
// Lifted from spike S2 Player.TickLoop.Tick.

namespace Tsuki.Media.Movie;

/// <summary>A decoded frame waiting to be shown: its index in the movie and its presentation time.</summary>
/// <param name="Index">Frame number from 0.</param>
/// <param name="Pts">Presentation time in seconds.</param>
public readonly record struct FrameStamp(long Index, double Pts);

/// <summary>Chooses the frame each 60 Hz tick shows.</summary>
public sealed class FrameSelector
{
    /// <summary>The lock of ARCHITECTURE 3.4: 4 ms.</summary>
    public const double DefaultCadenceLockSeconds = 0.004;

    private const double Epsilon = 1e-9;

    /// <summary>Creates a selector with the given cadence lock (0 = off).</summary>
    public FrameSelector(double cadenceLockSeconds = DefaultCadenceLockSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cadenceLockSeconds);
        CadenceLockSeconds = cadenceLockSeconds;
    }

    /// <summary>The cadence lock in seconds.</summary>
    public double CadenceLockSeconds { get; }

    /// <summary>The frame shown, or null before the first one.</summary>
    public FrameStamp? Current { get; private set; }

    /// <summary>Ticks the current frame has had, this one included.</summary>
    public int TicksShown { get; private set; }

    /// <summary>Times the lock kept a frame for its second tick although the next one was due.</summary>
    public long Holds { get; private set; }

    /// <summary>Times the lock switched to the next frame early, after two ticks.</summary>
    public long EarlySwitches { get; private set; }

    /// <summary>Frames that were replaced before any tick showed them.</summary>
    public long Dropped { get; private set; }

    /// <summary>Called when a frame is replaced: the frame and the ticks it had.</summary>
    public event Action<FrameStamp, int>? Retired;

    /// <summary>
    /// One tick: <paramref name="clock"/> is the movie clock sampled at the tick's nominal time; <paramref name="ready"/>
    /// holds the decoded frames in order. Returns how many of them the caller removes from its queue (the last one
    /// removed is the new <see cref="Current"/>).
    /// </summary>
    public int Tick(double clock, ReadOnlySpan<FrameStamp> ready)
    {
        int consumed = 0;
        while (consumed < ready.Length)
        {
            FrameStamp next = ready[consumed];
            if (Current is { } cur)
            {
                bool due = next.Pts <= clock + Epsilon;
                if (CadenceLockSeconds > 0 && cur.Index + 1 == next.Index)
                {
                    if (due && TicksShown == 1 && next.Pts > clock - CadenceLockSeconds)
                    {
                        due = false; // the boundary is within the lock window: give this frame its second tick
                        Holds++;
                    }
                    else if (!due && TicksShown >= 2 && next.Pts <= clock + CadenceLockSeconds)
                    {
                        due = true; // already had two ticks: switch up to the lock early
                        EarlySwitches++;
                    }
                }

                if (!due)
                {
                    break;
                }

                if (TicksShown == 0)
                {
                    Dropped++;
                }

                Retired?.Invoke(cur, TicksShown);
            }

            Current = next;
            TicksShown = 0;
            consumed++;
        }

        if (Current is not null)
        {
            TicksShown++;
        }

        return consumed;
    }
}

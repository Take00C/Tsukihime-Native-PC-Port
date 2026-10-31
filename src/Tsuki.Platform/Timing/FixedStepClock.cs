// The fixed 60 Hz logic step of the main loop (ARCHITECTURE 1.5, 3.4) with spike S2's nominal tick
// clock (S2.md 7, 9 item 3; spikes.md "S2"): step n is due at the grid time start + n / 60 s and
// reads clocks at that nominal time, not at the moment the present loop gets to it. After a stall of
// more than 4 steps the backlog is dropped and the grid restarts one step after "now", as S2's
// Player did, so a long stall slows the game instead of bursting dozens of steps.
// Pure arithmetic on caller-supplied timestamps: testable with a synthetic clock.
namespace Tsuki.Platform.Timing;

/// <summary>One logic step.</summary>
/// <param name="Frame">The 60 Hz frame counter: steps run so far, 0 first (the only time logic sees).</param>
/// <param name="NominalTimestamp">The step's grid time in clock units; media clocks are sampled here (S2).</param>
public readonly record struct LogicTick(long Frame, long NominalTimestamp);

/// <summary>Hands out fixed steps on a drift-free grid of <c>frequency / stepsPerSecond</c> clock units.</summary>
public sealed class FixedStepClock
{
    private readonly long _frequency;
    private readonly int _stepsPerSecond;
    private readonly long _maxLagSteps;
    private long _gridBase;
    private long _gridBaseFrame;

    /// <summary>Starts the grid: step 0 is due at <paramref name="startTimestamp"/>.</summary>
    /// <param name="frequency">Clock units per second (Stopwatch.Frequency for the app).</param>
    /// <param name="startTimestamp">Due time of step 0.</param>
    /// <param name="stepsPerSecond">Logic rate; the game runs at 60.</param>
    /// <param name="maxLagSteps">Backlog beyond which pending steps are dropped (S2: 4).</param>
    public FixedStepClock(long frequency, long startTimestamp, int stepsPerSecond = 60, int maxLagSteps = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepsPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLagSteps);
        _frequency = frequency;
        _stepsPerSecond = stepsPerSecond;
        _maxLagSteps = maxLagSteps;
        _gridBase = startTimestamp;
        _gridBaseFrame = 0;
    }

    /// <summary>Clock units per second.</summary>
    public long Frequency => _frequency;

    /// <summary>Logic steps per second.</summary>
    public int StepsPerSecond => _stepsPerSecond;

    /// <summary>The frame number the next step will carry (= steps run so far).</summary>
    public long Frame { get; private set; }

    /// <summary>Steps dropped after stalls.</summary>
    public long SkippedSteps { get; private set; }

    /// <summary>How many times the grid was restarted after a stall.</summary>
    public long Resyncs { get; private set; }

    /// <summary>When the next step is due, in clock units.</summary>
    public long NextDueTimestamp => GridTime(Frame);

    /// <summary>
    /// Returns the next step if it is due at <paramref name="now"/>. Call it in a loop until it returns
    /// false, then render and present.
    /// </summary>
    public bool TryNextTick(long now, out LogicTick tick)
    {
        long due = GridTime(Frame);
        if (now < due)
        {
            tick = default;
            return false;
        }

        tick = new LogicTick(Frame, due);
        Frame++;
        long next = GridTime(Frame);
        long lag = now - next;
        if (_maxLagSteps >= 0 && lag > StepsToUnits(_maxLagSteps))
        {
            // S2 Player: skipped += (now - nextTick) / tick; nextTick = now + tick
            SkippedSteps += (long)((Int128)lag * _stepsPerSecond / _frequency);
            Resyncs++;
            _gridBase = now;
            _gridBaseFrame = Frame - 1;
        }

        return true;
    }

    /// <summary>Clock units in <paramref name="steps"/> steps (rounded down).</summary>
    public long StepsToUnits(long steps) => (long)((Int128)steps * _frequency / _stepsPerSecond);

    private long GridTime(long frame) => _gridBase + StepsToUnits(frame - _gridBaseFrame);
}

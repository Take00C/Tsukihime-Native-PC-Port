using Tsuki.Platform.Timing;

namespace Tsuki.Tests.Render.Smoke;

/// <summary>
/// The 60 Hz fixed-step loop with spike S2's nominal tick clock (S2.md 7 and 9 item 3), on a
/// synthetic 10 MHz clock (the Stopwatch frequency on this machine). CPU only: it lives here because
/// it is part of the app skeleton package.
/// </summary>
public sealed class FixedStepClockTests
{
    private const long Freq = 10_000_000;
    private const long Start = 123_456_789;

    [Fact]
    public void GridIsDriftFreeOverTenMinutes()
    {
        var clock = new FixedStepClock(Freq, Start);
        long frames = 0;
        for (long n = 0; n <= 36_000; n++)
        {
            long due = Start + (n * Freq / 60);
            Assert.False(clock.TryNextTick(due - 1, out _));
            Assert.True(clock.TryNextTick(due, out LogicTick t));
            Assert.Equal(n, t.Frame);
            Assert.Equal(due, t.NominalTimestamp);
            frames++;
        }

        // 10 min = 36,000 steps exactly on the grid; S2's integer step of 166,666 units would be 24 ms early by then.
        Assert.Equal(36_001, frames);
        Assert.Equal(Start + (600 * Freq), Start + clock.StepsToUnits(36_000));
        Assert.Equal(0, clock.SkippedSteps);
    }

    [Fact]
    public void StepReadsItsNominalTimeNotTheTimeTheLoopGotThere()
    {
        var clock = new FixedStepClock(Freq, Start);
        Assert.True(clock.TryNextTick(Start, out _));
        long due = Start + clock.StepsToUnits(1);
        long late = due + 70_000; // 7 ms late, the worst lag S2 saw at 144 Hz

        Assert.True(clock.TryNextTick(late, out LogicTick t));
        Assert.Equal(1, t.Frame);
        Assert.Equal(due, t.NominalTimestamp);
        Assert.False(clock.TryNextTick(late, out _));
        Assert.Equal(Start + clock.StepsToUnits(2), clock.NextDueTimestamp);
    }

    [Fact]
    public void PresentsAt144HzRunEveryStepExactlyOnce()
    {
        var clock = new FixedStepClock(Freq, Start);
        var perPresent = new Dictionary<int, int>();
        long steps = 0;
        uint lcg = 12345;
        const int Presents = 1440; // 10 s
        for (int i = 0; i < Presents; i++)
        {
            lcg = (lcg * 1664525) + 1013904223;
            long jitter = (long)(lcg >> 16) % 10_001 - 5_000; // +-0.5 ms
            long now = Start + (i * Freq / 144) + jitter;
            int n = 0;
            while (clock.TryNextTick(now, out LogicTick t))
            {
                Assert.Equal(steps, t.Frame);
                Assert.True(t.NominalTimestamp <= now);
                steps++;
                n++;
            }

            perPresent[n] = perPresent.GetValueOrDefault(n) + 1;
        }

        // the last present is at 9.993 s, so steps 0..599 have run (step 600 is due at 10 s)
        Assert.Equal(600, steps);
        Assert.Equal(0, clock.SkippedSteps);
        Assert.Equal(new[] { 0, 1 }, perPresent.Keys.Order());
        Assert.Equal(600, perPresent[1]);
        Assert.Equal(840, perPresent[0]);
    }

    [Fact]
    public void ShortStallCatchesUpEveryStep()
    {
        var clock = new FixedStepClock(Freq, Start);
        Assert.True(clock.TryNextTick(Start, out _));
        long now = Start + clock.StepsToUnits(4) + 10_000; // 4 steps and 1 ms late

        var frames = new List<long>();
        while (clock.TryNextTick(now, out LogicTick t))
        {
            frames.Add(t.Frame);
        }

        Assert.Equal(new long[] { 1, 2, 3, 4 }, frames);
        Assert.Equal(0, clock.SkippedSteps);
        Assert.Equal(0, clock.Resyncs);
    }

    [Fact]
    public void LongStallDropsTheBacklogAndRestartsTheGridAfterNow()
    {
        var clock = new FixedStepClock(Freq, Start);
        Assert.True(clock.TryNextTick(Start, out _));
        long now = Start + 2_000_000; // 200 ms stall

        Assert.True(clock.TryNextTick(now, out LogicTick t1));
        Assert.Equal(1, t1.Frame);
        Assert.Equal(Start + 166_666, t1.NominalTimestamp);
        Assert.False(clock.TryNextTick(now, out _));

        // S2 Player: skipped += (now - nextTick) / tick = (2,000,000 - 333,333) / 166,666 = 10; nextTick = now + tick
        Assert.Equal(10, clock.SkippedSteps);
        Assert.Equal(1, clock.Resyncs);
        Assert.Equal(now + 166_666, clock.NextDueTimestamp);
        Assert.True(clock.TryNextTick(now + 166_666, out LogicTick t2));
        Assert.Equal(2, t2.Frame);
        Assert.Equal(now + 166_666, t2.NominalTimestamp);
        Assert.Equal(now + clock.StepsToUnits(2), clock.NextDueTimestamp);
    }
}

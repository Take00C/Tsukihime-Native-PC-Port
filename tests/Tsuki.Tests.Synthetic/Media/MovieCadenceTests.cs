using Tsuki.Media.Movie;

namespace Tsuki.Tests.Synthetic.Media;

/// <summary>
/// The 60 Hz movie frame choice with a synthetic clock (ARCHITECTURE 3.4, 7.5; S2.md 7): a 30 fps movie, an audio clock
/// that runs 50 ppm fast with +-0.6 ms jitter, and a tick grid whose phase crosses the frame boundaries during the run.
/// The nominal tick time plus the 4 ms cadence lock gives every frame exactly two ticks; without the lock, or when the
/// tick reads the clock as late as a 144 Hz present loop reaches it, frames get 1 or 3 ticks.
/// </summary>
public sealed class MovieCadenceTests
{
    private const double Fps = 30;
    private const int Frames = 11_660; // prologue_en.mp4, 388.7 s (S2.md 4 #1)

    private sealed record Run(Dictionary<int, int> TicksPerFrame, long Dropped, double MaxSyncErrorMs, long Holds, long Early);

    /// <param name="lockSeconds">Cadence lock.</param>
    /// <param name="phaseSeconds">Clock offset against the tick grid at the start.</param>
    /// <param name="presentLoopLag">Read the clock when a 144 Hz present loop reaches the tick (the first S2 design).</param>
    /// <param name="driftPpm">How much faster the audio clock runs than the tick timer (+50 ppm measured, S2.md 4).</param>
    /// <param name="seed">Jitter seed.</param>
    private static Run Play(double lockSeconds, double phaseSeconds, bool presentLoopLag, double driftPpm = 0, int seed = 1)
    {
        var rng = new Random(seed);
        var sel = new FrameSelector(lockSeconds);
        var ticks = new Dictionary<long, int>();
        sel.Retired += (f, n) => ticks[f.Index] = n;
        var queue = new List<FrameStamp>();
        long nextDecoded = 0;
        double maxErr = 0;
        for (long tick = 0; ; tick++)
        {
            double nominal = tick / 60.0;
            double readAt = presentLoopLag ? Math.Ceiling((nominal * 144) - 1e-9) / 144 : nominal;
            double jitter = ((rng.NextDouble() * 2) - 1) * 0.0006;
            double clock = (readAt * (1 + (driftPpm * 1e-6))) + phaseSeconds + jitter;

            // the decoder is ahead: keep 8 frames ready (S2.md 4 #8)
            while (queue.Count < 8 && nextDecoded < Frames)
            {
                queue.Add(new FrameStamp(nextDecoded, nextDecoded / Fps));
                nextDecoded++;
            }

            int used = sel.Tick(clock, queue.ToArray());
            queue.RemoveRange(0, used);
            if (sel.Current is { } cur && clock >= 0)
            {
                double end = cur.Pts + (1 / Fps);
                if (cur.Index == Frames - 1 && clock >= end)
                {
                    break; // the movie-ended tick (S2.md 9 #6), not a sync sample
                }

                double err = clock >= end ? clock - end : clock < cur.Pts ? cur.Pts - clock : 0;
                maxErr = Math.Max(maxErr, err * 1000);
            }
        }

        var hist = new Dictionary<int, int>();
        foreach (var (index, n) in ticks)
        {
            if (index > 0)
            {
                hist[n] = hist.GetValueOrDefault(n) + 1; // the first frame waits for the clock to start
            }
        }

        return new Run(hist, sel.Dropped, maxErr, sel.Holds, sel.EarlySwitches);
    }

    private static int Judder(Run r) => r.TicksPerFrame.Where(kv => kv.Key != 2).Sum(kv => kv.Value);

    private static string Show(Run r) => string.Join(", ", r.TicksPerFrame.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} ticks: {kv.Value}"));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.0006)]
    [InlineData(0.0003)]
    [InlineData(-0.0081)]
    public void NominalTickTimeWithTheLockGivesEveryFrameTwoTicks(double phase)
    {
        // the clock jitter (+-0.6 ms) straddles the frame boundaries for phases near 0 (S2.md 7, cad_nominal_phm06)
        Run r = Play(FrameSelector.DefaultCadenceLockSeconds, phase, presentLoopLag: false);
        Assert.Equal(new Dictionary<int, int> { [2] = Frames - 2 }, r.TicksPerFrame); // every retired frame but the first
        Assert.Equal(0, r.Dropped);
        Assert.True(r.MaxSyncErrorMs <= 4.0 + 0.6 + 1e-6, $"sync error {r.MaxSyncErrorMs:F3} ms");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.0003)]
    [InlineData(0.0003)]
    public void WithoutTheLockTheJitterAtTheBoundaryJudders(double phase)
    {
        Run r = Play(0, phase, presentLoopLag: false);
        Assert.Equal(0, r.Dropped);
        Assert.True(Judder(r) > 100, Show(r));
        Assert.Equal(0.0, r.MaxSyncErrorMs, 9); // without the lock the shown frame always covers the clock
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.005, 1)]
    public void ThePhaseDriftOfAWholeMovieRephasesAtMostOnceWithTheLock(double phase, int oneTickFrames)
    {
        // 50 ppm over 388.7 s moves the phase by 19.4 ms, more than one tick (16.7 ms). The lock's hysteresis absorbs
        // 4 ms beyond a tick: from phase 0 the clock never gets 20.7 ms ahead, and the shortfall falls on the last frame;
        // from 5 ms it does once, and exactly one frame keeps a single tick. Without the lock the jitter makes the
        // boundary flip for as long as the phase is within +-0.6 ms of it (about 24 s).
        Run locked = Play(FrameSelector.DefaultCadenceLockSeconds, phase, presentLoopLag: false, driftPpm: 50);
        Run unlocked = Play(0, phase, presentLoopLag: false, driftPpm: 50);
        Assert.Equal(oneTickFrames, Judder(locked));
        Assert.Equal(oneTickFrames, locked.TicksPerFrame.GetValueOrDefault(1));
        Assert.True(Judder(unlocked) > 10, $"locked {Show(locked)}; unlocked {Show(unlocked)}");
        Assert.Equal(0, locked.Dropped);
        Assert.True(locked.MaxSyncErrorMs <= 4.6 + 1e-6, $"sync error {locked.MaxSyncErrorMs:F3} ms");
    }

    [Fact]
    public void ReadingTheClockWhenThePresentLoopReachesTheTickJudders()
    {
        // 144 Hz presents: the tick reads the clock 0..6.9 ms late in a 5-tick pattern (S2.md 7, the first design)
        Run late = Play(0, -0.003, presentLoopLag: true);
        Run nominal = Play(0, -0.003, presentLoopLag: false);
        Assert.True(Judder(late) > 100, Show(late));
        Assert.Equal(0, Judder(nominal));
    }

    [Fact]
    public void TheLockHoldsAndSwitchesEarlyOnlyAtTheBoundary()
    {
        var sel = new FrameSelector(0.004);
        FrameStamp[] q = [new(0, 0), new(1, 1 / 30.0), new(2, 2 / 30.0)];
        Assert.Equal(1, sel.Tick(0.0, q));
        // tick 2 at 16.7 ms: frame 1 not due
        Assert.Equal(0, sel.Tick(1 / 60.0, q[1..]));
        Assert.Equal(2, sel.TicksShown);
        // tick 3 at 30 ms: frame 1 is 3.3 ms ahead; frame 0 had two ticks: early switch
        Assert.Equal(1, sel.Tick(0.030, q[1..]));
        Assert.Equal(1, sel.EarlySwitches);
        // tick 4 at 70 ms: frame 2 (66.7 ms) became due 3.3 ms ago, frame 1 had one tick: hold
        Assert.Equal(0, sel.Tick(0.070, q[2..]));
        Assert.Equal(1, sel.Holds);
        Assert.Equal(2, sel.TicksShown);
        Assert.Equal(1, sel.Tick(0.071, q[2..]));
        Assert.Equal(0, sel.Dropped);
    }

    [Fact]
    public void AFrameReplacedBeforeAnyTickIsCountedAsDropped()
    {
        var sel = new FrameSelector(0.004);
        FrameStamp[] q = [new(0, 0), new(1, 1 / 30.0), new(2, 2 / 30.0), new(3, 3 / 30.0)];
        sel.Tick(0, q);
        Assert.Equal(2, sel.Tick(0.075, q[1..])); // a 75 ms stall: frame 1 never shown
        Assert.Equal(1, sel.Dropped);
        Assert.Equal(2, sel.Current!.Value.Index);
    }

    [Fact]
    public void TheAudioClockExtrapolatesClampsAndNeverRunsBackwards()
    {
        const long Hz = 1_000_000; // microsecond timestamps
        var c = new AudioClock(Hz, 48_000, latencySeconds: 0.010);
        Assert.True(double.IsNaN(c.ClockAt(0)));
        c.OnCallback(480, 480, 100_000, endOfStream: false);              // 10 ms of media at t = 100 ms
        Assert.Equal(0.0, c.ClockAt(110_000), 9);                          // minus one device buffer
        Assert.Equal(0.005, c.ClockAt(115_000), 9);
        Assert.Equal(0.010, c.ClockAt(150_000), 9);                        // clamped to one chunk
        c.OnCallback(480, 480, 110_000, endOfStream: false);
        Assert.Equal(0.010, c.ClockAt(111_000), 9);                        // would be 0.001: never backwards
        Assert.Equal(0.015, c.ClockAt(125_000), 9);
        c.OnCallback(100, 480, 120_000, endOfStream: true);                // the stream drains
        Assert.True(c.Drained);
        c.OnCallback(0, 480, 130_000, endOfStream: true);                  // silent callbacks do not move the anchor
        Assert.Equal(960 / 48_000.0 + 0.5, c.ClockAt(630_000), 9);         // after the drain it runs on, unclamped
        Assert.Equal(1060, c.MediaFrames);
    }
}

/// <summary>The movie audio path: ring, mixer stream voice and audio clock (ARCHITECTURE 7.5).</summary>
public sealed class MovieAudioTests
{
    [Fact]
    public void TheMixerCallbackDrivesTheMovieClockAndEndsTheVoiceWhenTheRingDrains()
    {
        var ring = new Tsuki.Media.Movie.PcmRing(4800);
        float[] pcm = [.. Enumerable.Range(0, 2 * 1200).Select(i => (i % 2 == 0 ? 1f : -1f) * (i / 2) / 1200f)];
        Assert.Equal(1200, ring.Write(pcm));
        long now = 0;
        var clock = new AudioClock(1_000_000, 48_000, latencySeconds: 0.010);
        var source = new MovieAudioSource(ring, clock, () => now);
        var mixer = new Tsuki.Media.Mixer.AudioMixer();
        mixer.Post(Tsuki.Media.Mixer.MixerCommand.PlayStream(40, source, gain: 1f));
        float[] o = new float[960];
        now = 10_000;
        mixer.Render(o); // 480 frames
        Assert.Equal(pcm.AsSpan(0, 960).ToArray(), o);
        Assert.Equal(480, clock.MediaFrames);
        Assert.Equal(0.005, clock.ClockAt(25_000), 9); // 15 ms after the callback, minus the 10 ms buffer
        now = 20_000;
        mixer.Render(o);
        now = 30_000;
        mixer.Render(o);                                    // 240 frames left: an underflow, the ring is not complete yet
        Assert.Equal(240, source.UnderrunFrames);
        Assert.True(mixer.IsPlaying(40));
        ring.Complete();
        now = 40_000;
        mixer.Render(o);
        Assert.False(mixer.IsPlaying(40));
        Assert.True(clock.Drained);
        Assert.Equal(1200, clock.MediaFrames);
        Assert.All(o, v => Assert.Equal(0f, v));
    }
}

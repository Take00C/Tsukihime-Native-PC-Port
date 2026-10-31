// The main-thread frame loop (ARCHITECTURE 3.4, 6.2): SDL events, then every fixed 60 Hz logic step
// that is due on the nominal tick grid (spike S2), then one present of the newest state. With
// vsync the present runs at the monitor rate; without it (hidden window, swap interval 0) the loop
// sleeps until the next step is due.
using System.Diagnostics;
using Tsuki.Platform;
using Tsuki.Platform.Timing;
using Tsuki.Render.Gl;

namespace Tsuki.App;

/// <summary>Counters of one loop run.</summary>
internal sealed class LoopStats
{
    public long Steps { get; set; }

    public long Presents { get; set; }

    public long SkippedSteps { get; set; }

    public long Resyncs { get; set; }

    public double WallSeconds { get; set; }

    public string StopReason { get; set; } = "";

    /// <summary>Present-to-present intervals in 0.5 ms buckets; the last bucket holds 100 ms and more.</summary>
    public int[] IntervalHistogram { get; } = new int[201];

    public double MaxIntervalMs { get; private set; }

    public void AddInterval(double ms)
    {
        IntervalHistogram[Math.Min((int)(ms * 2), IntervalHistogram.Length - 1)]++;
        MaxIntervalMs = Math.Max(MaxIntervalMs, ms);
    }

    /// <summary>The bucket (upper edge, ms) holding the median interval.</summary>
    public double MedianIntervalMs()
    {
        int total = IntervalHistogram.Sum(), seen = 0;
        for (int i = 0; i < IntervalHistogram.Length; i++)
        {
            seen += IntervalHistogram[i];
            if (seen * 2 >= total && total > 0)
            {
                return (i + 1) / 2.0;
            }
        }

        return 0;
    }

    public int IntervalsOver(double ms) => IntervalHistogram.Skip((int)(ms * 2)).Sum();

    public override string ToString() =>
        $"{Steps} steps, {Presents} presents in {WallSeconds:F3} s ({Presents / Math.Max(WallSeconds, 1e-9):F1} presents/s); " +
        $"present interval median <= {MedianIntervalMs():F1} ms, max {MaxIntervalMs:F1} ms, {IntervalsOver(17)} over 17 ms; " +
        $"skipped steps {SkippedSteps}, resyncs {Resyncs}; stopped: {StopReason}";
}

/// <summary>Runs the skeleton's loop on one window.</summary>
internal sealed class MainLoop(GameWindow window, GlDevice device, EventPump pump)
{
    private readonly List<PlatformEvent> _events = [];

    /// <summary>
    /// Runs until a quit request, <paramref name="onStep"/> returning false, or
    /// <paramref name="maxSeconds"/> of wall time.
    /// </summary>
    /// <param name="onStep">One logic step; false stops the loop after this frame's present.</param>
    /// <param name="onEvent">Called for each platform event; false stops the loop.</param>
    /// <param name="maxSeconds">Wall-time limit; 0 = none.</param>
    public LoopStats Run(Func<LogicTick, bool> onStep, Func<PlatformEvent, bool> onEvent, double maxSeconds)
    {
        var stats = new LoopStats();

        // The first swap can block for hundreds of ms (window shown, fullscreen transition); the step
        // grid starts after it so that wait is not counted as a stall.
        Present(-1);
        long start = Stopwatch.GetTimestamp();
        long limit = maxSeconds > 0 ? start + (long)(maxSeconds * Stopwatch.Frequency) : long.MaxValue;
        var clock = new FixedStepClock(Stopwatch.Frequency, start);
        long newestFrame = -1;
        long lastPresent = 0;
        bool running = true;
        while (running)
        {
            _events.Clear();
            pump.Pump(_events);
            foreach (PlatformEvent e in _events)
            {
                if (e.Kind == PlatformEventKind.QuitRequested)
                {
                    running = false;
                    stats.StopReason = "quit requested";
                }

                if (!onEvent(e))
                {
                    running = false;
                    stats.StopReason = "event handler";
                }
            }

            long now = Stopwatch.GetTimestamp();
            while (running && clock.TryNextTick(now, out LogicTick tick))
            {
                newestFrame = tick.Frame;
                stats.Steps++;
                if (!onStep(tick))
                {
                    running = false;
                    stats.StopReason = "step handler";
                }
            }

            Present(newestFrame);
            long presented = Stopwatch.GetTimestamp();
            if (lastPresent != 0)
            {
                stats.AddInterval((presented - lastPresent) * 1000.0 / Stopwatch.Frequency);
            }

            lastPresent = presented;
            stats.Presents++;
            if (Stopwatch.GetTimestamp() >= limit)
            {
                running = false;
                stats.StopReason = "time limit";
            }

            if (running && !window.VsyncPaced)
            {
                Pacing.WaitUntil(clock.NextDueTimestamp);
            }
        }

        stats.SkippedSteps = clock.SkippedSteps;
        stats.Resyncs = clock.Resyncs;
        stats.WallSeconds = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
        return stats;
    }

    /// <summary>Clears to a colour derived from the newest logic frame (a 4 s hue cycle) and presents.</summary>
    private void Present(long frame)
    {
        (int w, int h) = window.PixelSize;
        double phase = frame < 0 ? 0 : (frame % 240) / 240.0 * 2 * Math.PI;
        float r = (float)(0.15 + (0.1 * Math.Sin(phase)));
        float g = (float)(0.12 + (0.1 * Math.Sin(phase + 2.094)));
        float b = (float)(0.25 + (0.1 * Math.Sin(phase + 4.189)));
        device.ClearDefaultFramebuffer(w, h, r, g, b);
        window.Swap();
    }
}

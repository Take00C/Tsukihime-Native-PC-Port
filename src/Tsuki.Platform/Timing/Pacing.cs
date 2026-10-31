// Waiting for the next logic step when presents are not paced by vsync (hidden window, swap
// interval 0; ARCHITECTURE 6.2 present-mode option). SDL_DelayPrecise sleeps and then spins the
// last stretch.
using System.Diagnostics;
using static SDL.SDL3;

namespace Tsuki.Platform.Timing;

/// <summary>Sleeps until a Stopwatch timestamp.</summary>
public static class Pacing
{
    /// <summary>Returns once <see cref="Stopwatch.GetTimestamp"/> has reached <paramref name="timestamp"/>, at most one second from now.</summary>
    public static void WaitUntil(long timestamp)
    {
        long remaining = timestamp - Stopwatch.GetTimestamp();
        if (remaining <= 0)
        {
            return;
        }

        long ns = (long)((Int128)Math.Min(remaining, Stopwatch.Frequency) * 1_000_000_000 / Stopwatch.Frequency);
        SDL_DelayPrecise((ulong)ns);
    }
}

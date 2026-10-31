// Spike S4: whole-machine CPU load between two samples (GetSystemTimes), to record how busy the
// shared machine was while frame times were measured (other jobs may run at the same time).
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace S4.BigPicture;

internal static partial class SystemCpu
{
    internal readonly record struct Sample(ulong Idle, ulong Kernel, ulong User, TimeSpan Own);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    public static Sample Now()
    {
        GetSystemTimes(out ulong i, out ulong k, out ulong u);
        using var p = Process.GetCurrentProcess();
        return new Sample(i, k, u, p.TotalProcessorTime);
    }

    /// <summary>Machine busy % and this process's share (both in % of all logical processors).</summary>
    public static Dictionary<string, object?> Between(Sample a, Sample b)
    {
        double total = (b.Kernel - a.Kernel) + (b.User - a.User); // kernel time includes idle time
        double idle = b.Idle - a.Idle;
        double busy = total > 0 ? 100.0 * (1 - idle / total) : double.NaN;
        // FILETIME ticks are 100 ns summed over all processors
        double own = total > 0 ? 100.0 * (b.Own - a.Own).Ticks / total : double.NaN;
        return new()
        {
            ["machine_busy_pct"] = Math.Round(busy, 1),
            ["this_process_pct"] = Math.Round(own, 1),
            ["other_processes_pct"] = Math.Round(busy - own, 1),
        };
    }
}

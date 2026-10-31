// Spike S2: Win32 helpers for per-thread CPU accounting (ROADMAP M0 scope 8, spikes.md S2 #7).
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace S2.MovieSync;

/// <summary>Thread ids and CPU times from kernel32. Spike-only; the engine projects stay free of P/Invoke.</summary>
internal static partial class Native
{
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    /// <summary>Machine-wide (idle, busy) CPU seconds summed over all logical processors.</summary>
    internal static (double Idle, double Busy) SystemCpuSeconds()
    {
        // kernel time includes idle time
        return GetSystemTimes(out long idle, out long k, out long u) ? (idle / 1e7, (k + u - idle) / 1e7) : (double.NaN, double.NaN);
    }

    /// <summary>CPU seconds (user + kernel) consumed so far by the calling thread.</summary>
    internal static double CurrentThreadCpuSeconds()
    {
        return GetThreadTimes(GetCurrentThread(), out _, out _, out long k, out long u) ? (k + u) / 1e7 : double.NaN;
    }

    /// <summary>CPU seconds per OS thread id of this process, at the moment of the call.</summary>
    internal static Dictionary<int, double> ThreadCpuSnapshot()
    {
        var map = new Dictionary<int, double>();
        using var p = Process.GetCurrentProcess();
        foreach (ProcessThread t in p.Threads)
        {
            try
            {
                map[t.Id] = t.TotalProcessorTime.TotalSeconds;
            }
            catch (InvalidOperationException)
            {
                // the thread exited between enumeration and the query
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // access to the thread was refused
            }
        }

        return map;
    }

    /// <summary>The set of OS thread ids of this process.</summary>
    internal static HashSet<int> ThreadIds()
    {
        using var p = Process.GetCurrentProcess();
        var set = new HashSet<int>();
        foreach (ProcessThread t in p.Threads)
        {
            set.Add(t.Id);
        }

        return set;
    }
}

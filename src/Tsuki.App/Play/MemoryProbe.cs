// Memory diagnostics for the soak runs (package K11): one line with the process's memory split into the managed heap (by generation, LOH, POH) and the rest
// (native: the GL driver, FFmpeg, mapped archives), logged every N seconds by --mem-log N.
using System.Diagnostics;
using System.Globalization;

namespace Tsuki.App.Play;

/// <summary>Formats one line of process memory figures.</summary>
internal static class MemoryProbe
{
    /// <summary>The figures of the process right now, in MiB.</summary>
    public static string Line()
    {
        using Process p = Process.GetCurrentProcess();
        GCMemoryInfo gc = GC.GetGCMemoryInfo(GCKind.Any);
        long[] size = new long[5];
        ReadOnlySpan<GCGenerationInfo> gen = gc.GenerationInfo;
        for (int i = 0; i < gen.Length && i < size.Length; i++)
        {
            size[i] = gen[i].SizeAfterBytes >> 20;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"mem: working set {p.WorkingSet64 >> 20} private {p.PrivateMemorySize64 >> 20} MiB; managed live {GC.GetTotalMemory(false) >> 20} heap {gc.HeapSizeBytes >> 20} committed {gc.TotalCommittedBytes >> 20} " +
            $"frag {gc.FragmentedBytes >> 20} (gen0 {size[0]} gen1 {size[1]} gen2 {size[2]} loh {size[3]} poh {size[4]}); gcs {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, " +
            $"allocated {GC.GetTotalAllocatedBytes(false) >> 20} MiB, pinned {gc.PinnedObjectsCount}");
    }
}

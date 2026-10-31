// Working-set trimming of memory-mapped file pages (package K11). The archives are mapped whole (24 GB for allpac1); every page a decode touches stays in the
// process's working set until the operating system needs the memory, so the "working set" of a long play grows by the size of everything the game ever read, with
// nothing leaking. Dropping the pages from the working set moves them to the system cache (standby list): a later read of the same page is a soft fault, no disk read.
using System.Runtime.InteropServices;

namespace Tsuki.Platform;

/// <summary>Native page-table helpers for mapped files.</summary>
public static partial class MemoryPages
{
    /// <summary>
    /// Removes the pages of <c>[address, address + length)</c> from the process's working set (Windows: <c>VirtualUnlock</c> on pages that are not locked, which is its
    /// documented way to do that; the "not locked" error it reports is the normal result). Does nothing on other systems. Safe while other threads read the range.
    /// </summary>
    /// <param name="address">Start of the range (inside a mapped view).</param>
    /// <param name="length">Bytes.</param>
    public static void TrimResident(nint address, nuint length)
    {
        if (OperatingSystem.IsWindows() && length > 0)
        {
            _ = VirtualUnlock(address, length);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualUnlock(nint address, nuint size);
}

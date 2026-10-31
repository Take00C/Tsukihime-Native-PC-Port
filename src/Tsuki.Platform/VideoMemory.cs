// The dedicated memory of the display adapter (package R1; K11 section 8): the texture cache cap follows it, so a 4 GB card is not asked to hold 1.5 GiB of RGBA
// tiles plus the driver's own copies. Windows keeps each adapter's size in the registry (the display class key, HardwareInformation.qwMemorySize); WMI's AdapterRAM
// is a 32-bit field and stops at 4 GB, and the GL memory-info extensions exist on two vendors only.
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Tsuki.Platform;

/// <summary>What is known about the display adapter's memory, and the texture cache cap that follows from it.</summary>
public static class VideoMemory
{
    /// <summary>The smallest texture cache: a card with 1.5 GB or less still gets this (the game evicts and decodes again when it is full).</summary>
    public const long MinCacheBytes = 512L << 20;

    /// <summary>The largest texture cache, the fixed cap of packages K11..K19 (the driver holds about twice that for it).</summary>
    public const long MaxCacheBytes = 1536L << 20;

    /// <summary>The texture cache cap for a card with <paramref name="dedicatedBytes"/> of memory: a third of it, between <see cref="MinCacheBytes"/> and <see cref="MaxCacheBytes"/>; the maximum when the memory is unknown.</summary>
    public static long CacheCapacity(long? dedicatedBytes) =>
        dedicatedBytes is { } v && v > 0 ? Math.Clamp(v / 3, MinCacheBytes, MaxCacheBytes) : MaxCacheBytes;

    /// <summary>
    /// The dedicated memory of the adapter whose driver description occurs in <paramref name="rendererHint"/> (the GL_RENDERER string), else of the adapter with the
    /// most memory; null when nothing is known (not Windows, no registry entry, no access).
    /// </summary>
    public static (long Bytes, string Name)? Dedicated(string? rendererHint) => OperatingSystem.IsWindows() ? ReadWindows(rendererHint) : null;

    [SupportedOSPlatform("windows")]
    private static (long Bytes, string Name)? ReadWindows(string? hint)
    {
        try
        {
            using RegistryKey? display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (display is null)
            {
                return null;
            }

            (long Bytes, string Name)? largest = null;
            (long Bytes, string Name)? matched = null;
            foreach (string sub in display.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsAsciiDigit))
                {
                    continue;
                }

                using RegistryKey? adapter = display.OpenSubKey(sub);
                if (adapter is null)
                {
                    continue;
                }

                long bytes = adapter.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    int i => (uint)i,
                    byte[] { Length: >= 8 } b => BitConverter.ToInt64(b, 0),
                    _ => 0,
                };
                if (bytes <= 0)
                {
                    bytes = adapter.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int i => (uint)i,
                        byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0),
                        _ => 0,
                    };
                }

                if (bytes <= 0)
                {
                    continue;
                }

                string name = (adapter.GetValue("DriverDesc") as string)?.Trim() ?? sub;
                if (largest is null || bytes > largest.Value.Bytes)
                {
                    largest = (bytes, name);
                }

                if (matched is null && hint is not null && name.Length > 0 && hint.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    matched = (bytes, name);
                }
            }

            return matched ?? largest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

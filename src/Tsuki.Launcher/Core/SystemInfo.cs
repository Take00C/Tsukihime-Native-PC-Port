// What Windows says about the machine (package K17): the video adapters and the monitors, both from user32 (no WMI package needed).
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tsuki.Launcher.Core;

/// <summary>One monitor.</summary>
/// <param name="Index">1-based number in the list.</param>
/// <param name="X">Left edge in virtual-desktop pixels.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Primary">The primary monitor.</param>
/// <param name="Name">The adapter's name for the monitor.</param>
/// <param name="WorkWidth">Width of the work area (the desktop less the task bar; package K19), or 0 when unknown.</param>
/// <param name="WorkHeight">Height of the work area, or 0 when unknown.</param>
public sealed record MonitorEntry(int Index, int X, int Y, int Width, int Height, bool Primary, string Name, int WorkWidth = 0, int WorkHeight = 0)
{
    /// <summary>"Display 2: 2560 x 1440, primary".</summary>
    public string Label => $"Display {Index}  ·  {Width} × {Height}{(Primary ? "  ·  primary" : string.Empty)}";

    /// <summary>The work area the game's window is kept inside (the whole monitor when the work area is unknown).</summary>
    public (int Width, int Height) WorkArea => WorkWidth > 0 && WorkHeight > 0 ? (WorkWidth, WorkHeight) : (Width, Height);
}

/// <summary>Monitors and video adapters.</summary>
[SupportedOSPlatform("windows")]
public static class SystemInfo
{
    /// <summary>
    /// The names of the video adapters that drive a display, the primary one first (user32 EnumDisplayDevices). The registry's driver class
    /// keys are not used: they keep the entries of cards that were removed long ago, so an old card could be listed first.
    /// </summary>
    public static IReadOnlyList<string> VideoAdapters()
    {
        var attached = new List<(string Name, bool Primary)>();
        var present = new List<string>();
        var d = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        for (int i = 0; EnumDisplayDevices(null, i, ref d, 0); i++)
        {
            string name = d.DeviceString.Trim();
            if (name.Length > 0)
            {
                if ((d.StateFlags & AttachedToDesktop) != 0 && !attached.Exists(a => a.Name == name))
                {
                    attached.Add((name, (d.StateFlags & PrimaryDevice) != 0));
                }

                if (!present.Contains(name))
                {
                    present.Add(name);
                }
            }

            d = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        }

        // with no adapter on the desktop (remote sessions), the adapters Windows still enumerates; Microsoft's basic adapter last
        return attached.Count > 0
            ? [.. attached.OrderByDescending(a => a.Primary).Select(a => a.Name)]
            : [.. present.OrderBy(n => n.Contains("Basic", StringComparison.OrdinalIgnoreCase) || n.Contains("Remote", StringComparison.OrdinalIgnoreCase) ? 1 : 0)];
    }

    private const int AttachedToDesktop = 0x1;
    private const int PrimaryDevice = 0x4;

    /// <summary>The attached monitors, the primary first, then left to right.</summary>
    public static IReadOnlyList<MonitorEntry> Monitors()
    {
        var found = new List<(int X, int Y, int W, int H, bool Primary, string Device, int WorkW, int WorkH)>();
        bool Callback(nint monitor, nint hdc, nint rect, nint data)
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                found.Add((info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, (info.Flags & 1) != 0, info.Device,
                    info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
            }

            return true;
        }

        EnumDisplayMonitors(0, 0, Callback, 0);
        var ordered = found.OrderByDescending(m => m.Primary).ThenBy(m => m.X).ThenBy(m => m.Y).ToList();
        var result = new List<MonitorEntry>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var m = ordered[i];
            result.Add(new MonitorEntry(i + 1, m.X, m.Y, m.W, m.H, m.Primary, FriendlyName(m.Device), m.WorkW, m.WorkH));
        }

        return result;
    }

    private static string FriendlyName(string device)
    {
        var d = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevices(device, 0, ref d, 0) && d.DeviceString.Length > 0 ? d.DeviceString : string.Empty;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, int index, ref DisplayDevice displayDevice, int flags);
}

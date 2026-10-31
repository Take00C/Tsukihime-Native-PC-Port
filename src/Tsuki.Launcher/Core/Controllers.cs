// Connected game controllers by name (package K17), through the legacy Windows joystick API (winmm) and the name Windows keeps for the device in the registry. It needs no
// extra package; the game itself uses SDL and sees the same pads.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Tsuki.Launcher.Core;

/// <summary>Lists the game controllers that are plugged in.</summary>
[SupportedOSPlatform("windows")]
public static class Controllers
{
    /// <summary>The names of the connected controllers (empty when none is, or when Windows does not answer).</summary>
    public static IReadOnlyList<string> Connected()
    {
        var names = new List<string>();
        try
        {
            int count = Math.Min(joyGetNumDevs(), 16);
            for (int id = 0; id < count; id++)
            {
                var info = new JoyInfoEx { Size = Marshal.SizeOf<JoyInfoEx>(), Flags = 0xFF };
                if (joyGetPosEx(id, ref info) != 0)
                {
                    continue;
                }

                var caps = new JoyCaps();
                if (joyGetDevCaps((nuint)id, ref caps, Marshal.SizeOf<JoyCaps>()) != 0)
                {
                    continue;
                }

                names.Add(NameOf(caps));
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return names;
        }

        return names;
    }

    private static string NameOf(JoyCaps caps)
    {
        try
        {
            string key = $@"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{caps.Mid:X4}&PID_{caps.Pid:X4}";
            using RegistryKey? oem = Registry.LocalMachine.OpenSubKey(key) ?? Registry.CurrentUser.OpenSubKey(key);
            if (oem?.GetValue("OEMName") is string name && name.Length > 0)
            {
                return name;
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // the product name below is the fallback
        }

        return string.IsNullOrWhiteSpace(caps.ProductName) ? "Game controller" : caps.ProductName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
    {
        public ushort Mid;
        public ushort Pid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ProductName;
        public uint XMin, XMax, YMin, YMax, ZMin, ZMax, NumButtons, PeriodMin, PeriodMax, RMin, RMax, UMin, UMax, VMin, VMax, CapsFlags, MaxAxes, NumAxes, MaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string RegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string OemVxD;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
    {
        public int Size;
        public int Flags;
        public int X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2;
    }

    [DllImport("winmm.dll")]
    private static extern int joyGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern int joyGetPosEx(int id, ref JoyInfoEx info);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "joyGetDevCapsW")]
    private static extern int joyGetDevCaps(nuint id, ref JoyCaps caps, int size);
}

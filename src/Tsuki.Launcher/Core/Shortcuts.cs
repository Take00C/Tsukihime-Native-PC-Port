// Desktop and Start-menu shortcuts (package K20): Tsukihime.lnk pointing at Tsukihime.exe (the game without the launcher) and Tsukihime Launcher.lnk, written through
// the shell's IShellLinkW / IPersistFile (COM, no package). The folders are parameters, so the tests and the self-test write into a temporary folder, never onto the real
// desktop; the launcher's model passes the user's desktop and Programs folder.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Tsuki.Launcher.Core;

/// <summary>What a shortcut points at.</summary>
/// <param name="LinkPath">The .lnk file to write.</param>
/// <param name="TargetPath">The program.</param>
/// <param name="WorkingDirectory">Its folder (the game loads its libraries from there).</param>
/// <param name="Description">The tooltip.</param>
/// <param name="IconPath">The file whose icon the shortcut shows (the program itself: its ApplicationIcon).</param>
/// <param name="IconIndex">The icon in that file.</param>
/// <param name="Arguments">Arguments, or empty.</param>
public sealed record ShortcutSpec(string LinkPath, string TargetPath, string WorkingDirectory, string Description, string IconPath, int IconIndex = 0, string Arguments = "");

/// <summary>What a shortcut file holds (read back for the checks).</summary>
/// <param name="TargetPath">The program.</param>
/// <param name="WorkingDirectory">The start-in folder.</param>
/// <param name="Description">The tooltip.</param>
/// <param name="IconPath">The icon file.</param>
/// <param name="IconIndex">The icon index.</param>
/// <param name="Arguments">The arguments.</param>
public sealed record ShortcutInfo(string TargetPath, string WorkingDirectory, string Description, string IconPath, int IconIndex, string Arguments);

/// <summary>What the shortcuts are called (no shell code: usable on every platform, by the tests too).</summary>
public static class ShortcutNames
{
    /// <summary>The name on the desktop and in the Start menu.</summary>
    public const string ProductName = "Tsukihime";

    /// <summary>The game's shortcut file name.</summary>
    public const string GameLinkName = ProductName + ".lnk";

    /// <summary>The launcher's shortcut file name.</summary>
    public const string LauncherLinkName = ProductName + " Launcher.lnk";
}

/// <summary>Where shortcuts go.</summary>
/// <param name="Desktop">The desktop folder.</param>
/// <param name="StartMenu">The Start-menu folder of the port (a subfolder of Programs).</param>
public sealed record ShortcutFolders(string Desktop, string StartMenu)
{
    /// <summary>The current user's desktop and Start menu > Programs > Tsukihime.</summary>
    public static ShortcutFolders ForUser() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutNames.ProductName));
}

/// <summary>Creates and reads .lnk files.</summary>
[SupportedOSPlatform("windows")]
public static class Shortcuts
{
    /// <summary>The name on the desktop and in the Start menu.</summary>
    public const string ProductName = ShortcutNames.ProductName;

    /// <summary>The game's shortcut file name.</summary>
    public const string GameLinkName = ShortcutNames.GameLinkName;

    /// <summary>The launcher's shortcut file name.</summary>
    public const string LauncherLinkName = ShortcutNames.LauncherLinkName;

    /// <summary>
    /// The shortcut that starts the game directly (the remembered game folder and %APPDATA%\Tsuki are the game's own business). Its icon is <paramref name="iconPath"/>
    /// when given (package K23: the Arcueid icon the launcher built from the player's files into the profile folder), else the executable's crescent.
    /// </summary>
    public static ShortcutSpec Game(string folder, string gameExe, string? iconPath = null) => new(
        Path.Combine(folder, GameLinkName), gameExe, Path.GetDirectoryName(Path.GetFullPath(gameExe))!,
        "Tsukihime -A piece of blue glass moon- (PC port)", iconPath ?? gameExe);

    /// <summary>The shortcut that starts the launcher.</summary>
    public static ShortcutSpec Launcher(string folder, string launcherExe) => new(
        Path.Combine(folder, LauncherLinkName), launcherExe, Path.GetDirectoryName(Path.GetFullPath(launcherExe))!,
        "Tsukihime PC port: settings, saves and the game", launcherExe);

    /// <summary>Writes the shortcut (the folder is created; an existing file is replaced: ask first).</summary>
    public static void Create(ShortcutSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(spec.LinkPath))!);
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(spec.TargetPath);
            link.SetWorkingDirectory(spec.WorkingDirectory);
            link.SetDescription(spec.Description);
            link.SetIconLocation(spec.IconPath, spec.IconIndex);
            link.SetArguments(spec.Arguments);
            link.SetShowCmd(1);                                         // SW_SHOWNORMAL
            ((IPersistFile)link).Save(Path.GetFullPath(spec.LinkPath), true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>Reads a shortcut back; null when the file is missing or is not a shortcut.</summary>
    public static ShortcutInfo? Read(string linkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        if (!File.Exists(linkPath))
        {
            return null;
        }

        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(Path.GetFullPath(linkPath), 0);
            var target = new StringBuilder(1024);
            var folder = new StringBuilder(1024);
            var description = new StringBuilder(1024);
            var icon = new StringBuilder(1024);
            var arguments = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            link.GetWorkingDirectory(folder, folder.Capacity);
            link.GetDescription(description, description.Capacity);
            link.GetIconLocation(icon, icon.Capacity, out int index);
            link.GetArguments(arguments, arguments.Capacity);
            return new ShortcutInfo(target.ToString(), folder.ToString(), description.ToString(), icon.ToString(), index, arguments.ToString());
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    // the shell link object and the two interfaces of the shell's shortcut implementation (shobjidl_core.h / objidl.h)
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}

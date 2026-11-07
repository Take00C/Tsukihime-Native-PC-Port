using Tsuki.Launcher.Core;

namespace Tsuki.Launcher.Avalonia;

internal static class PlatformServices
{
    /// <summary>The game program's file name: Tsukihime on macOS and Linux (Tsukihime.exe where the tests run on Windows).</summary>
    public static string AppName => OperatingSystem.IsWindows() ? GameLauncher.AppFileName : "Tsukihime";

    public static string? FindApp(string launcherDirectory)
    {
        string app = Path.Combine(launcherDirectory, "Tsukihime");
        return !OperatingSystem.IsWindows() && File.Exists(app) ? app : GameLauncher.FindApp(launcherDirectory);
    }

    public static string? FindGuide(string launcherDirectory)
    {
        string root = OperatingSystem.IsMacOS() ? Path.GetFullPath(Path.Combine(launcherDirectory, "..", "Resources")) : launcherDirectory;
        return new[] { Path.Combine(root, "SETUP_GUIDE.txt"), Path.Combine(root, "docs", "SETUP_GUIDE.md") }.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The application-menu entry of the game (the Windows desktop shortcut's counterpart; Steam's "Add a Non-Steam Game" lists it): <paramref name="exe"/>, started in
    /// <paramref name="folder"/>; <paramref name="icon"/> is a PNG of Arcueid's face from the player's files (no Icon line without one). Escaped as the Desktop Entry
    /// specification reads it: the quoting of Exec's argument first, then the escapes of a string value (a backslash doubled), and %% for a percent sign.
    /// </summary>
    public static string DesktopEntry(string exe, string? icon = null, string? folder = null)
    {
        string quoted = exe.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");
        string pathLine = folder is null ? string.Empty : "Path=" + EntryString(folder) + "\n";
        string iconLine = icon is null ? string.Empty : "Icon=" + EntryString(icon) + "\n";
        return "[Desktop Entry]\nType=Application\nName=Tsukihime\nComment=Play Tsukihime\nExec=\"" + EntryString(quoted).Replace("%", "%%") + "\"\n" + pathLine + iconLine + "Terminal=false\nCategories=Game;\n";
    }

    /// <summary>A string value of a desktop entry: backslashes doubled, line breaks and tabs written as \n, \r and \t.</summary>
    private static string EntryString(string value) => value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    public static IReadOnlyList<string> MissingFfmpeg(string appExe)
    {
        string folder = Path.GetDirectoryName(appExe)!;
        string[] names = OperatingSystem.IsMacOS()
            ? ["libavutil.61.dylib", "libswresample.7.dylib", "libavcodec.63.dylib", "libavformat.63.dylib"]
            : OperatingSystem.IsLinux()
                ? ["libavutil.so.61", "libswresample.so.7", "libavcodec.so.63", "libavformat.so.63"]
                : GameLauncher.FfmpegDlls.ToArray();
        return names.Where(name => !File.Exists(Path.Combine(folder, name))).ToArray();
    }
}

/// <summary>The sidebar icons as path data (24 × 24, drawn with a 1.5 stroke): the Windows launcher's Segoe Fluent Icons are not on macOS or Linux.</summary>
public static class NavIcons
{
    public const string Play = "M7.5,4.8 L18.5,12 L7.5,19.2 Z";
    public const string Folder = "M3,6.5 L9,6.5 L11,8.5 L21,8.5 L21,18.5 L3,18.5 Z";
    public const string Display = "M3,4.5 L21,4.5 L21,15.5 L3,15.5 Z M12,15.5 L12,19.5 M7.5,19.5 L16.5,19.5";
    public const string Globe = "M12,3 A9,9 0 1 1 11.99,3 Z M3,12 L21,12 M12,3 C8.5,6.5 8.5,17.5 12,21 M12,3 C15.5,6.5 15.5,17.5 12,21";
    public const string Pad = "M7,7.5 L17,7.5 C19.5,7.5 21,10 21,13.5 C21,16.5 19,17.5 17.5,16 L15.5,14 L8.5,14 L6.5,16 C5,17.5 3,16.5 3,13.5 C3,10 4.5,7.5 7,7.5 Z M7.5,9.5 L7.5,12.5 M6,11 L9,11 M15.5,10 L15.6,10 M17.5,12 L17.6,12";
    public const string Save = "M4,4 L16,4 L20,8 L20,20 L4,20 Z M8,4 L8,9 L15,9 L15,4 M7,20 L7,14 L17,14 L17,20";
    public const string Help = "M9,9 C9,6.8 10.5,5.5 12.2,5.5 C14,5.5 15.5,6.8 15.5,8.6 C15.5,11.3 12.5,11.4 12.5,14.2 M12.5,18 L12.5,18.6";
}

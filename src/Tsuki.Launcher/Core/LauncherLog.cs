// The launcher's own log (package K40): one line per event of the update check and the update, in launcher.log next to the saves (the profile folder, %APPDATA%\Tsuki
// or --data). A check that fails (offline, GitHub busy) says so here and nowhere else: the launcher stays quiet about it. The file is kept small (the newest half is
// kept when it passes 256 KB). It is a log, not a setting: no other file of the profile is touched by it.
using System.Globalization;

namespace Tsuki.Launcher.Core;

/// <summary>Appends lines to launcher.log.</summary>
/// <param name="Path">The file.</param>
public sealed record LauncherLog(string Path)
{
    /// <summary>The file name in the profile folder.</summary>
    public const string FileName = "launcher.log";

    /// <summary>The size at which the older half is dropped.</summary>
    public const int MaxBytes = 256 * 1024;

    private static readonly Lock Gate = new();

    /// <summary>The log of a profile.</summary>
    public static LauncherLog For(LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new LauncherLog(System.IO.Path.Combine(paths.DataDirectory, FileName));
    }

    /// <summary>Appends "2026-11-01 14:02:11  text"; never throws (a log that cannot be written is not worth a message).</summary>
    public void Write(string text)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + (text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ') + Environment.NewLine;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    string all = File.ReadAllText(Path);
                    int cut = all.IndexOf('\n', all.Length / 2);
                    File.WriteAllText(Path, cut >= 0 ? all[(cut + 1)..] : string.Empty);
                }

                File.AppendAllText(Path, line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // nothing to do
            }
        }
    }

    /// <summary>The last lines of the log (the tests read what was written).</summary>
    public IReadOnlyList<string> Lines()
    {
        try
        {
            return File.Exists(Path) ? File.ReadAllLines(Path) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

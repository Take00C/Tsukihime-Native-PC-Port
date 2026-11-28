// Where the launcher and the game keep their files (package K17): the per-user profile, or save beside Windows / Linux programs. The programs became Tsukihime.exe
// and TsukihimeLauncher.exe in package K20 and the per-user folder kept its name, so existing saves keep working.
using Tsuki.App.Services;

namespace Tsuki.Launcher.Core;

/// <summary>The files of one user profile of the port.</summary>
/// <param name="DataDirectory">The folder (normally <c>%APPDATA%\Tsuki</c>).</param>
public sealed record LauncherPaths(string DataDirectory)
{
    /// <summary>The paths of the current user, or the Windows / Linux portable folder when it exists.</summary>
    public static LauncherPaths ForUser() => new(ProfileDirectory.ForUser());

    /// <summary>True when this is the profile the game selects without <c>--save-dir</c> (per-user, or portable when its folder exists).</summary>
    public bool IsUserProfile => SameFolder(DataDirectory, ForUser().DataDirectory);

    private static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The save file (140 slots, system data, config).</summary>
    public string SaveFile => Path.Combine(DataDirectory, "save.dat");

    /// <summary>The PC port's settings.</summary>
    public string SettingsFile => Path.Combine(DataDirectory, "settings.ini");

    /// <summary>
    /// The record of earned achievements (v0.1.9): the game writes it. The launcher reads it, and writes it only in a restore or an import of a save while no
    /// game runs (<see cref="Achievements.KeepBeforeSaveChange"/>, <see cref="Achievements.UniteBackupRecord"/>).
    /// </summary>
    public string TrophiesFile => Path.Combine(DataDirectory, Tsuki.Engine.Trophies.TrophyLog.FileName);

    /// <summary>The game folder the game remembers (<c>--game</c> or this file).</summary>
    public string GameDirFile => Path.Combine(DataDirectory, "game_dir.txt");

    /// <summary>The save backups, one folder per backup.</summary>
    public string BackupsDirectory => Path.Combine(DataDirectory, "backups");

    /// <summary>The log of the last run, written by the game because the launcher starts it with <c>--log</c>.</summary>
    public string LastRunLog => Path.Combine(DataDirectory, "last_run.log");

    /// <summary>The crash log the game appends to.</summary>
    public string CrashLog => Path.Combine(DataDirectory, "crash.log");

    /// <summary>The log to show: the last run's, else the crash log, else null.</summary>
    public string? LogToShow() =>
        File.Exists(LastRunLog) ? LastRunLog : File.Exists(CrashLog) ? CrashLog : null;

    /// <summary>Reads the remembered game folder; null when there is none.</summary>
    public string? ReadGameDirectory()
    {
        try
        {
            if (File.Exists(GameDirFile))
            {
                string text = File.ReadAllText(GameDirFile).Trim();
                return text.Length == 0 ? null : text;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // an unreadable file is the same as none
        }

        return null;
    }

    /// <summary>Remembers the game folder for the game and the launcher.</summary>
    public bool WriteGameDirectory(string folder)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(GameDirFile, folder);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

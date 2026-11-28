using Tsuki.Data;

namespace Tsuki.Launcher.Core;

/// <summary>A checked folder choice, shared by the WPF and Avalonia launchers.</summary>
public sealed record LauncherFolderChoice(string DisplayPath, string Message, bool Accepted, bool Saved)
{
    public static async Task<LauncherFolderChoice> ChooseAsync(LauncherPaths paths, string path, bool picker = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string written = path.Trim().Trim('"');
        string resolved = GameFolderCheck.Resolve(path);
        GameFolderInfo info = await Task.Run(() => GameFolderCheck.Inspect(resolved));
        if (info.State is FolderState.NotTheGame or FolderState.Missing or FolderState.NotSet or FolderState.PkgFile or FolderState.UpdateOnly or FolderState.Several)
        {
            return new LauncherFolderChoice(path, info.Summary + " The folder was not remembered.", false, false);
        }

        string chosen = info.Path ?? resolved;
        // a picked folder is remembered relative to the programs only in the portable profile, which moves with them; game_dir.txt of a per-user profile is
        // shared by every copy of the port on the PC, so there it stays absolute, as before 0.1.11. A typed path is kept as typed.
        string stored = picker ? (PortableProfile.InUse(paths, AppContext.BaseDirectory) ? GameFolderPath.ForPicker(chosen) : chosen) : Path.IsPathFullyQualified(written) ? chosen : written;
        bool saved = paths.WriteGameDirectory(stored);
        string message = saved ? (stored == path ? string.Empty : "Using " + resolved) : "The folder could not be remembered (" + paths.GameDirFile + ").";
        return new LauncherFolderChoice(stored, message, true, saved);
    }
}

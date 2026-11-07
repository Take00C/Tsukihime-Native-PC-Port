namespace Tsuki.Launcher.Core;

/// <summary>A checked folder choice, shared by the WPF and Avalonia launchers.</summary>
public sealed record LauncherFolderChoice(string DisplayPath, string Message, bool Accepted, bool Saved)
{
    public static async Task<LauncherFolderChoice> ChooseAsync(LauncherPaths paths, string path)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string resolved = GameFolderCheck.Resolve(path);
        GameFolderInfo info = await Task.Run(() => GameFolderCheck.Inspect(resolved));
        if (info.State is FolderState.NotTheGame or FolderState.Missing or FolderState.NotSet or FolderState.PkgFile or FolderState.UpdateOnly or FolderState.Several)
        {
            return new LauncherFolderChoice(info.Path ?? path, info.Summary + " The folder was not remembered.", false, false);
        }

        bool saved = paths.WriteGameDirectory(resolved);
        string message = saved ? (resolved == path ? string.Empty : "Using " + resolved) : "The folder could not be remembered (" + paths.GameDirFile + ").";
        return new LauncherFolderChoice(info.Path ?? path, message, true, saved);
    }
}

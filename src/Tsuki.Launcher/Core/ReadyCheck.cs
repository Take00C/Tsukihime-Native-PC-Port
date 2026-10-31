// The "Ready check" card of the Play page (package K17): one row per thing the game needs, each with a state and the page that fixes it. Package K25: the game folder
// row says which build was found (verified, another known eboot, another version of the release, an unknown build) and names an update folder beside the game.
using Tsuki.Data.Verification;

namespace Tsuki.Launcher.Core;

/// <summary>The pages of the launcher, in sidebar order.</summary>
public enum LauncherPage
{
    /// <summary>Hero, ready check, quick settings.</summary>
    Play,

    /// <summary>The game folder.</summary>
    GameFiles,

    /// <summary>Fullscreen, monitor, window size, vsync.</summary>
    Display,

    /// <summary>Game language and sound.</summary>
    Language,

    /// <summary>Button prompts, keys, controllers.</summary>
    Controls,

    /// <summary>Backups, restore, import.</summary>
    Saves,

    /// <summary>What this is, keys, licences.</summary>
    Help,
}

/// <summary>Whether a ready-check row is fine.</summary>
public enum CheckState
{
    /// <summary>Fine (a check mark).</summary>
    Ok,

    /// <summary>Needs attention (an exclamation mark and a fix link).</summary>
    Warning,
}

/// <summary>One row of the ready check.</summary>
/// <param name="Title">What is checked.</param>
/// <param name="Detail">What was found.</param>
/// <param name="State">Fine or not.</param>
/// <param name="Fix">The page that fixes it; null when nothing can be done there.</param>
public sealed record ReadyRow(string Title, string Detail, CheckState State, LauncherPage? Fix = null)
{
    /// <summary>True when a fix link is shown.</summary>
    public bool HasFix => Fix is not null;
}

/// <summary>Builds the ready-check rows from what was found.</summary>
public static class ReadyCheck
{
    /// <summary>
    /// The rows: game folder, game files, program and FFmpeg, graphics, saves.
    /// </summary>
    /// <param name="folder">The game folder.</param>
    /// <param name="appExe">Tsukihime.exe, or null when the launcher cannot find it.</param>
    /// <param name="missingFfmpeg">FFmpeg DLLs missing next to the game program.</param>
    /// <param name="graphics">The names of the video adapters (empty when unknown).</param>
    /// <param name="saves">The save file.</param>
    public static IReadOnlyList<ReadyRow> Build(GameFolderInfo folder, string? appExe, IReadOnlyList<string> missingFfmpeg, IReadOnlyList<string> graphics, SaveSummary saves)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(missingFfmpeg);
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(saves);
        var rows = new List<ReadyRow>();

        string version = folder.AppVersion is { Length: > 0 } v ? $"version {v}" : "version unknown";
        string update = folder.PatchFolder is { } patch ? $", update folder {Path.GetFileName(patch)} beside it" : string.Empty;
        rows.Add(folder.State switch
        {
            FolderState.NotSet => new ReadyRow("Game folder", "Not chosen yet. Point the launcher at your CUSA47999 folder.", CheckState.Warning, LauncherPage.GameFiles),
            FolderState.Missing => new ReadyRow("Game folder", "The remembered folder is gone.", CheckState.Warning, LauncherPage.GameFiles),
            FolderState.NotTheGame => new ReadyRow("Game folder", "eboot.bin is not in this folder.", CheckState.Warning, LauncherPage.GameFiles),
            FolderState.PkgFile => new ReadyRow("Game folder", "A .pkg file was chosen: extract it first, then choose the extracted CUSA47999 folder.", CheckState.Warning, LauncherPage.GameFiles),
            FolderState.UpdateOnly => new ReadyRow("Game folder", "Only the update folder was found; the base game must be beside it.", CheckState.Warning, LauncherPage.GameFiles),
            FolderState.Several => new ReadyRow("Game folder", "Several game folders are inside the chosen one; choose one of them.", CheckState.Warning, LauncherPage.GameFiles),
            FolderState.Ready when folder.IsVerifiedBuild || folder.Build == BuildMatch.Verified => new ReadyRow("Game folder", $"{folder.TitleId ?? "Game"} found, {version}{update}: {folder.BuildText}", CheckState.Ok),
            FolderState.Ready => new ReadyRow("Game folder", $"{folder.TitleId ?? "Game"} found, {version}{update}: {folder.BuildText}", CheckState.Warning, LauncherPage.GameFiles),
            _ => new ReadyRow("Game folder", $"{folder.TitleId ?? "Game"} found, {version}{update}", CheckState.Ok),
        });

        rows.Add(folder.State switch
        {
            FolderState.Ready => new ReadyRow("Game files", $"{folder.Archives} archives and {folder.Movies} movies {(folder.IsVerifiedBuild ? "verified" : "checked")}{(folder.TablesNote is { } note ? $"; {note}" : string.Empty)}", CheckState.Ok),
            FolderState.Incomplete => new ReadyRow("Game files", folder.Summary, CheckState.Warning, LauncherPage.GameFiles),
            _ => new ReadyRow("Game files", "Waiting for the game folder", CheckState.Warning, LauncherPage.GameFiles),
        });

        rows.Add(appExe is null
            ? new ReadyRow("Game program", $"{GameLauncher.AppFileName} is not next to the launcher", CheckState.Warning, LauncherPage.Help)
            : missingFfmpeg.Count > 0
                ? new ReadyRow("Game program", $"{missingFfmpeg[0]} is missing{(missingFfmpeg.Count > 1 ? $" (and {missingFfmpeg.Count - 1} more)" : string.Empty)}", CheckState.Warning, LauncherPage.Help)
                : new ReadyRow("Game program", $"{GameLauncher.AppFileName} and the FFmpeg libraries found", CheckState.Ok));

        rows.Add(new ReadyRow(
            "Graphics",
            graphics.Count == 0 ? "OpenGL 3.3 is checked when the game starts" : $"{graphics[0]}; OpenGL 3.3 is checked at start",
            CheckState.Ok));

        rows.Add(saves.WriteError is not null || saves.Error is not null
            ? new ReadyRow("Saves", saves.Describe(), CheckState.Warning, LauncherPage.Saves)
            : new ReadyRow("Saves", saves.Exists ? saves.Describe() + " in %APPDATA%\\Tsuki" : saves.Describe(), CheckState.Ok));
        return rows;
    }

    /// <summary>True when Play can start the game: the folder is ready and the program is there.</summary>
    public static bool CanPlay(GameFolderInfo folder, string? appExe, IReadOnlyList<string> missingFfmpeg)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folder.IsReady && appExe is not null && missingFfmpeg.Count == 0;
    }
}

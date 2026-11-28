// What both launchers say (v0.1.7 review): the Windows launcher (WPF) and the macOS / Linux launcher (Avalonia) take these sentences and the small rules behind them
// from here, so that the two copies of the pages cannot drift apart ("1 thing need attention"). Only plain text and plain data: no WPF or Avalonia types.
using System.Reflection;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Trophies;

namespace Tsuki.Launcher.Core;

/// <summary>The sentences of the launcher pages, the toasts and the update overlay that both launchers show.</summary>
public static class LauncherTexts
{
    // -- the Play page ------------------------------------------------------------------------------------------------

    /// <summary>"All set" or "2 things need attention".</summary>
    public static string ReadySummary(IEnumerable<ReadyRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int n = rows.Count(r => r.State == CheckState.Warning);
        return n == 0 ? "All set" : n == 1 ? "1 thing needs attention" : $"{n} things need attention";
    }

    /// <summary>"Launcher 0.1.6": the launcher's own version, without the commit an informational version carries.</summary>
    public static string LauncherVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return "Launcher " + (assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0");
    }

    /// <summary>"CUSA47999 · game version 01.01" (a folder that is set but incomplete says "version unknown", not "not set"; package R1).</summary>
    public static string GameChip(GameFolderInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folder.State switch
        {
            FolderState.NotSet or FolderState.Missing => "CUSA47999  ·  game folder not set",
            FolderState.NotTheGame or FolderState.PkgFile or FolderState.UpdateOnly or FolderState.Several => "CUSA47999  ·  not the game folder",
            FolderState.Ready when !folder.IsVerifiedBuild => $"{folder.TitleId ?? "CUSA47999"}  ·  game version {folder.AppVersion ?? "unknown"}  ·  not the verified build",
            _ => $"{folder.TitleId ?? "CUSA47999"}  ·  game version {folder.AppVersion ?? "unknown"}",
        };
    }

    /// <summary>The toast of PLAY when the game cannot start (the launcher shows the Game files page with it).</summary>
    public static string CannotPlay(GameFolderInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folder.IsReady
            ? "The game program or its libraries are missing (see Help)."
            : folder.State == FolderState.Incomplete ? "The game files are not complete: see the problems on this page." : "Choose your game folder first.";
    }

    /// <summary>The toast of PLAY while a game runs already (started by hand, or by another launcher).</summary>
    public const string GameAlreadyRunning = "The game is already running.";

    /// <summary>The toast of PLAY, a backup, a restore, an import or a setting while the portable switch copies the profile.</summary>
    public const string PortableCopyRunning = "Wait until the profile copy has finished.";

    /// <summary>The toast of PLAY when the game program could not be started.</summary>
    public static string GameNotStarted(string why) => "The game could not be started: " + why;

    /// <summary>The toast after the game ended, or null for a clean exit.</summary>
    public static string? GameExited(int code) => code == GameLauncher.ExitAlreadyRunning
        ? "The game is already running in another window."
        : code != 0 ? $"The game closed with an error (code {code}). Press Log to see why." : null;

    // -- the Game files page ------------------------------------------------------------------------------------------

    /// <summary>"Ready" or why not (package K25: a usable copy that is not the verified build says so).</summary>
    public static string StatusText(GameFolderInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return !folder.IsReady ? folder.Summary
            : folder.IsVerifiedBuild ? "Ready: this is the game the port was made for"
            : $"Ready, but not the verified build: {folder.BuildText}";
    }

    /// <summary>What the dump is, in words (package K25): "known build: CUSA47999 01.01 (official 1.01 update eboot)", with the update folder when there is one.</summary>
    public static string BuildDetail(GameFolderInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folder.BuildName is null
            ? "unknown"
            : folder.BuildName + (folder.PatchFolder is { } patch ? $"; update folder {Path.GetFileName(patch)} beside the game, its files take precedence" : string.Empty)
                + (folder.IgnoredPatchFolder is { } ignored ? $"; the update folder {Path.GetFileName(ignored)} beside the game is not needed (the game folder already holds an update as new)" : string.Empty)
                + (folder.TablesNote is { } note ? $"; {note}" : string.Empty);
    }

    /// <summary>The languages the data carries, in words (package K25): "日本語, English, 简体中文, 繁體中文".</summary>
    public static string LanguagesDetail(GameFolderInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folder.State == FolderState.Ready
            ? string.Join(", ", folder.LanguageList.Select(LanguageName))
            : "unknown";
    }

    /// <summary>A language in its own script.</summary>
    public static string LanguageName(Language language) => language switch
    {
        Language.Ja => "日本語",
        Language.Zc => "简体中文",
        Language.Zt => "繁體中文",
        Language.Ko => "한국어",
        _ => "English",
    };

    /// <summary>"CUSA47999, version 01.01".</summary>
    public static string TitleText(GameFolderInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folder.TitleId is null
            ? "unknown"
            : $"{folder.TitleId}  ·  version {folder.AppVersion ?? "?"}{(folder.IsReady && folder.SfoVersion is { } sfo && sfo != folder.AppVersion ? $" (param.sfo says {sfo})" : string.Empty)}";
    }

    /// <summary>"120 of 120 passed".</summary>
    public static string ChecksText(GameFolderInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folder.ChecksTotal == 0 ? "not run" : $"{folder.ChecksPassed} of {folder.ChecksTotal} passed";
    }

    /// <summary>"20 archives and data files, 134 movies".</summary>
    public static string ContentsText(GameFolderInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folder.ChecksTotal == 0 ? "unknown" : $"{folder.Archives} archives and data files, {folder.Movies} movies";
    }

    /// <summary>"46.3 GB".</summary>
    public static string FolderSize(GameFolderInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folder.TotalBytes > 0 ? GameFolderCheck.FormatSize(folder.TotalBytes) : "unknown";
    }

    // -- the Saves page -----------------------------------------------------------------------------------------------

    /// <summary>The question before a backup replaces save.dat: title, body, the confirming button.</summary>
    public static (string Title, string Body, string ConfirmLabel) RestoreQuestion(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return (
            "Restore this backup?",
            $"save.dat will be replaced by the backup of {backup.Created.ToString("d MMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture)} ({backup.Save.Describe()}). " +
            "Your current save is backed up first, so you can undo this. Settings are kept as they are. Achievements are kept too, and the backup's are added to them.",
            "Restore");
    }

    /// <summary>The question before a PS4 save image replaces save.dat.</summary>
    public static (string Title, string Body, string ConfirmLabel) ImportQuestion(string imagePath) => (
        "Import this PS4 save?",
        $"{Path.GetFileName(imagePath)} is read (the file itself is never changed). Its slots and system data replace save.dat; your current save is backed up first.",
        "Import");

    // -- the Achievements page (v0.1.9) -------------------------------------------------------------------------------
    // Every word here is the port's own. The names of the 27 chapter achievements are the game's chapter titles, read from the player's game folder at run time;
    // the names below stand in for them when there is no game folder. No official trophy name or description is in this file.

    /// <summary>The page's name: its sidebar entry and its heading.</summary>
    public const string AchievementsTitle = "Achievements";

    /// <summary>The line under the page's heading.</summary>
    public const string AchievementsDescription = "The game's 30 achievements, its PS4 trophies. Play to earn them; progress already in your saves counts too.";

    /// <summary>The gold line over the page's heading, as on every page: the page's name in capitals.</summary>
    public const string AchievementsOverline = "ACHIEVEMENTS";

    /// <summary>The page's heading.</summary>
    public const string AchievementsHeading = "What you have earned";

    /// <summary>The summary card's small capitals.</summary>
    public const string AchievementsProgressCaps = "PROGRESS";

    /// <summary>The switch card's small capitals.</summary>
    public const string AchievementsNoticeCaps = "NOTIFY";

    /// <summary>The link beside the note that asks for a game folder: it opens the Game files page (that page's sidebar name).</summary>
    public const string AchievementsOpenGameFiles = "Game files";

    /// <summary>The Earned tab while nothing is earned.</summary>
    public const string AchievementsNothingEarned = "Nothing earned yet. Play the story and your achievements appear here.";

    /// <summary>The Locked tab once everything is earned.</summary>
    public const string AchievementsNothingLocked = "Nothing is locked any more: you have earned every achievement.";

    /// <summary>A grade's count under its emblem on the summary card: "3 / 21".</summary>
    public static string AchievementGradeCount(int earned, int total) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{earned} / {total}");

    /// <summary>The tab that lists every achievement.</summary>
    public const string AchievementsTabAll = "All";

    /// <summary>The tab that lists the earned ones.</summary>
    public const string AchievementsTabEarned = "Earned";

    /// <summary>The tab that lists the ones still to earn.</summary>
    public const string AchievementsTabLocked = "Locked";

    /// <summary>A tab's label with its count: "Earned (12)".</summary>
    public static string AchievementsTab(string name, int count) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name} ({count})");

    /// <summary>The label of the notice's switch row (settings.ini <c>trophy_notice</c>, off until the player turns it on).</summary>
    public const string AchievementsNoticeLabel = "Notify in the game";

    /// <summary>What the notice's switch does.</summary>
    public const string AchievementsNoticeDescription = "Shows a short message at the top right of the game when you earn an achievement. Achievements are recorded whether it is on or off.";

    /// <summary>The label of the sound's switch row, under the notice's (settings.ini <c>trophy_sound</c>, off until the player turns it on).</summary>
    public const string AchievementsSoundLabel = "Sound when notified";

    /// <summary>What the sound's switch does.</summary>
    public const string AchievementsSoundDescription = "Plays a soft chime with the message. Needs \"Notify in the game\" to be on.";

    /// <summary>Where the record is kept.</summary>
    public const string AchievementsFileNote =
        "Achievements are kept in " + TrophyLog.FileName + " beside your saves and are part of every backup on the Saves page. Restoring an older save or importing a PS4 save never removes one.";

    /// <summary>Shown when the record could not be read.</summary>
    public const string AchievementsDamagedNote = TrophyLog.FileName + " could not be read, so only what your saves show is listed here, without dates.";

    /// <summary>Shown when the names and the icons could not be read from a game folder.</summary>
    public const string AchievementsNoGameNote =
        "Choose your game folder (Game files) to see the chapter names and the icons cut from your own game's pictures, and to count the chapters your saves have already finished.";

    /// <summary>"12 of 30 earned".</summary>
    public static string AchievementCount(int earned, int total) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{earned} of {total} earned");

    /// <summary>"40%".</summary>
    public static string AchievementPercent(int percent) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{percent}%");

    /// <summary>A grade's word: "Platinum", "Gold", "Silver", "Bronze".</summary>
    public static string AchievementGrade(TrophyGrade grade) => grade switch
    {
        TrophyGrade.Platinum => "Platinum",
        TrophyGrade.Gold => "Gold",
        TrophyGrade.Silver => "Silver",
        _ => "Bronze",
    };

    /// <summary>The state of an achievement that is not earned.</summary>
    public const string AchievementLocked = "Locked";

    /// <summary>The state of an achievement earned at a time nobody recorded (progress made before achievements existed).</summary>
    public const string AchievementEarned = "Earned";

    /// <summary>The state of an achievement earned at a known time, in local time: "12 Nov 2026, 14:03".</summary>
    public static string AchievementEarnedAt(DateTime earnedUtc) =>
        (earnedUtc.Kind == DateTimeKind.Local ? earnedUtc : DateTime.SpecifyKind(earnedUtc, DateTimeKind.Utc).ToLocalTime())
        .ToString("d MMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The name of an achievement when the game folder gives none: always for 0, 28 and 29 (the game has no chapter title for them), and for the chapter
    /// achievements 1..27 while no game folder is set.
    /// </summary>
    public static string AchievementFallbackName(int id) => id switch
    {
        TrophyTable.Platinum => "All Achievements",
        >= 1 and <= 4 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Day {id}"),
        >= 5 and <= 13 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Arcueid's route, day {id}"),
        14 => "Arcueid's route, the last day",
        >= 15 and <= 23 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Ciel's route, day {id - 10}"),
        24 => "Ciel's route, day 14 (first path)",
        25 => "Ciel's route, day 14 (second path)",
        26 => "Ciel's route, first ending",
        27 => "Ciel's route, second ending",
        TrophyTable.FirstLesson => "First Lesson",
        TrophyTable.AllLessons => "Every Lesson",
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Achievement {id}"),
    };

    /// <summary>How an achievement is earned, in one line (empty for an id that is not 0..29).</summary>
    public static string AchievementDetail(int id) => id switch
    {
        TrophyTable.Platinum => "Earn every other achievement.",
        >= 1 and <= 4 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Finish day {id}."),
        >= 5 and <= 13 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Finish day {id} on Arcueid's route."),
        14 => "Finish Arcueid's route.",
        >= 15 and <= 23 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Finish day {id - 10} on Ciel's route."),
        24 => "Finish day 14 on Ciel's route, on the way to its first ending.",
        25 => "Finish day 14 on Ciel's route, on the way to its second ending.",
        26 => "Reach the first ending of Ciel's route.",
        27 => "Reach the second ending of Ciel's route.",
        TrophyTable.FirstLesson => "Attend one of Ciel's lessons.",
        TrophyTable.AllLessons => "Attend all 25 of Ciel's lessons.",
        _ => string.Empty,
    };

    // -- updates ------------------------------------------------------------------------------------------------------

    /// <summary>The Help page's status line while a check runs.</summary>
    public const string Checking = "Checking for updates...";

    /// <summary>The Help page's status line after a check that failed (a release found earlier stays offered).</summary>
    public static string CheckFailed(string error) => $"Could not check for updates ({error}). The launcher works as before; Check now tries again.";

    /// <summary>The Help page's status line after a check that found a newer release.</summary>
    public static string UpdateAvailable(string tag, string at) => $"{tag} is available (checked at {at}). Press \"Update to {tag}\" at the bottom of the window.";

    /// <summary>The Help page's status line after a check that found nothing newer.</summary>
    public static string Newest(ReleaseVersion current, string at) => $"You have the newest version, {current.Tag} (checked at {at}).";

    /// <summary>Why the update does not start while the game of the launcher's folder runs (<paramref name="appFileName"/>: Tsukihime.exe, or Tsukihime on macOS and Linux).</summary>
    public static string UpdateGameRunning(string appFileName) =>
        $"Close the game first: {appFileName} of this folder is running, and its files are the ones the update replaces. Quit the game (your progress is in your saves), then press Update now again.";

    /// <summary>Why the update does not start while the files of the last one are still in use.</summary>
    public const string UpdateLastStillInUse = "The files of the last update are still in use. Close the launcher, start it again, then press Update now.";

    /// <summary>The overlay after Cancel: not a failure.</summary>
    public const string UpdateCancelled = "The download was cancelled. Nothing was changed.";

    /// <summary>The overlay after a download, check or unpack that failed (<paramref name="why"/>: <see cref="UpdateClient.Describe"/>).</summary>
    public static string UpdateFailed(string why) => $"The update did not work: {why}. Nothing was changed; the launcher you have keeps working.";

    /// <summary>The overlay when the game was started while the update downloaded (nothing was replaced).</summary>
    public const string UpdateGameStartedMeanwhile = "The game was started while the update downloaded. Quit it, then press Update now again.";

    /// <summary>The Help page's status line of the launcher an update restarted into.</summary>
    public static string UpdatedStatus(ReleaseVersion from, ReleaseVersion to) => $"Updated from {from.Tag} to {to.Tag}.";

    /// <summary>The toast of the launcher an update restarted into.</summary>
    public static string UpdatedToast(ReleaseVersion from, ReleaseVersion to) => $"Updated from {from.Tag} to {to.Tag}. Your saves and settings are as they were.";

    /// <summary>The launcher.log line of the launcher an update restarted into.</summary>
    public static string UpdatedLog(ReleaseVersion from, ReleaseVersion to, string informationalVersion) => $"updated from {from.Tag} to {to.Tag} ({informationalVersion})";
}

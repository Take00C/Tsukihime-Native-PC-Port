using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Avalonia;

/// <summary>
/// The sentences both launchers take from Core/LauncherTexts. Each expected string is the Windows launcher's own literal of v0.1.6 (LauncherModel.cs and
/// LauncherModel.Updates.cs before the sentences moved), so the move cannot have changed what Windows players read.
/// </summary>
public sealed class LauncherTextsTests
{
    private static readonly ReleaseVersion From = new(0, 1, 5);
    private static readonly ReleaseVersion To = new(0, 1, 6);

    [Fact]
    public void TheReadySummaryCountsInGoodEnglish()
    {
        ReadyRow ok = new("Graphics", "fine", CheckState.Ok);
        ReadyRow warning = new("Game folder", "gone", CheckState.Warning, LauncherPage.GameFiles);
        Assert.Equal("All set", LauncherTexts.ReadySummary([ok, ok]));
        Assert.Equal("1 thing needs attention", LauncherTexts.ReadySummary([ok, warning]));
        Assert.Equal("3 things need attention", LauncherTexts.ReadySummary([warning, warning, ok, warning]));
    }

    [Fact]
    public void TheGameToastsAreTheWindowsOnes()
    {
        Assert.Null(LauncherTexts.GameExited(0));
        Assert.Equal("The game is already running in another window.", LauncherTexts.GameExited(GameLauncher.ExitAlreadyRunning));
        Assert.Equal("The game closed with an error (code -1073741819). Press Log to see why.", LauncherTexts.GameExited(-1073741819));
        Assert.Equal("The game is already running.", LauncherTexts.GameAlreadyRunning);
        Assert.Equal("The game could not be started: access denied", LauncherTexts.GameNotStarted("access denied"));
        Assert.Equal("Choose your game folder first.", LauncherTexts.CannotPlay(GameFolderCheck.NotSet));
        Assert.Equal("CUSA47999  ·  game folder not set", LauncherTexts.GameChip(GameFolderCheck.NotSet));
        Assert.Matches(@"^Launcher \d+\.\d+\.\d+$", LauncherTexts.LauncherVersion(typeof(LauncherTexts).Assembly));
    }

    [Fact]
    public void TheFolderFactsOfAFolderNotSetAreTheWindowsOnes()
    {
        GameFolderInfo folder = GameFolderCheck.NotSet;
        Assert.Equal(folder.Summary, LauncherTexts.StatusText(folder));
        Assert.Equal("unknown", LauncherTexts.BuildDetail(folder));
        Assert.Equal("unknown", LauncherTexts.LanguagesDetail(folder));
        Assert.Equal("unknown", LauncherTexts.TitleText(folder));
        Assert.Equal("not run", LauncherTexts.ChecksText(folder));
        Assert.Equal("unknown", LauncherTexts.ContentsText(folder));
        Assert.Equal("unknown", LauncherTexts.FolderSize(folder));
        Assert.Equal("日本語", LauncherTexts.LanguageName(Tsuki.Engine.Contracts.Language.Ja));
        Assert.Equal("繁體中文", LauncherTexts.LanguageName(Tsuki.Engine.Contracts.Language.Zt));
    }

    [Fact]
    public void TheUpdateSentencesAreTheWindowsOnes()
    {
        Assert.Equal("Checking for updates...", LauncherTexts.Checking);
        Assert.Equal("Could not check for updates (offline). The launcher works as before; Check now tries again.", LauncherTexts.CheckFailed("offline"));
        Assert.Equal("v0.1.6 is available (checked at 14:02). Press \"Update to v0.1.6\" at the bottom of the window.", LauncherTexts.UpdateAvailable("v0.1.6", "14:02"));
        Assert.Equal("You have the newest version, v0.1.6 (checked at 14:02).", LauncherTexts.Newest(To, "14:02"));
        Assert.Equal("Close the game first: Tsukihime.exe of this folder is running, and its files are the ones the update replaces. Quit the game (your progress is in your saves), then press Update now again.",
            LauncherTexts.UpdateGameRunning(GameLauncher.AppFileName));
        Assert.Equal("The files of the last update are still in use. Close the launcher, start it again, then press Update now.", LauncherTexts.UpdateLastStillInUse);
        Assert.Equal("The download was cancelled. Nothing was changed.", LauncherTexts.UpdateCancelled);
        Assert.Equal("The update did not work: no connection (HostNotFound). Nothing was changed; the launcher you have keeps working.", LauncherTexts.UpdateFailed("no connection (HostNotFound)"));
        Assert.Equal("The game was started while the update downloaded. Quit it, then press Update now again.", LauncherTexts.UpdateGameStartedMeanwhile);
        Assert.Equal("Updated from v0.1.5 to v0.1.6.", LauncherTexts.UpdatedStatus(From, To));
        Assert.Equal("Updated from v0.1.5 to v0.1.6. Your saves and settings are as they were.", LauncherTexts.UpdatedToast(From, To));
        Assert.Equal("updated from v0.1.5 to v0.1.6 (0.1.6+abc)", LauncherTexts.UpdatedLog(From, To, "0.1.6+abc"));
    }

    [Fact]
    public void TheSaveQuestionsAreTheWindowsOnes()
    {
        var save = new SaveSummary(true, 3, 1, null, null);
        var backup = new BackupInfo("20261027-153000", "/tmp/b", new DateTime(2026, 10, 27, 15, 30, 0), ["save.dat"], 100, save);
        Assert.Equal(
            ("Restore this backup?", $"save.dat will be replaced by the backup of 27 Oct 2026, 15:30 ({save.Describe()}). Your current save is backed up first, so you can undo this. Settings are kept as they are.", "Restore"),
            LauncherTexts.RestoreQuestion(backup));
        Assert.Equal(
            ("Import this PS4 save?", "savedata000 is read (the file itself is never changed). Its slots and system data replace save.dat; your current save is backed up first.", "Import"),
            LauncherTexts.ImportQuestion(Path.Combine("x", "savedata000")));
    }
}

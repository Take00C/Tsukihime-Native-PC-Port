using System.Diagnostics;
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Launcher.Core;
using Tsuki.Ui;

namespace Tsuki.Tests.Data.Launcher;

/// <summary>
/// Package K17 on the real game: the launcher's own logic (folder check, plan, process start) runs Tsukihime.exe with the arguments it builds, hidden and muted,
/// and the keys it wrote to settings.ini reach the game. Nothing is written into the game folder; the profile is a temporary folder.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class LauncherLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k17-launch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [GameDataFact]
    public void TheRealFolderIsReadyWithTheAnalysedVersionAndEveryReadyCheckRowIsFine()
    {
        string game = GameFolder.Require();
        GameFolderInfo info = GameFolderCheck.Inspect(game);
        Assert.Equal(FolderState.Ready, info.State);
        Assert.Equal("CUSA47999", info.TitleId);
        Assert.Equal(GameFolderCheck.AnalysedVersion, info.AppVersion);
        Assert.Equal(info.ChecksTotal, info.ChecksPassed);
        Assert.True(info.TotalBytes > 10_000_000_000);
        Assert.Equal(134, info.Movies);

        string app = Path.Combine(AppContext.BaseDirectory, GameLauncher.AppFileName);
        Assert.True(File.Exists(app), "the test output holds Tsukihime.exe");
        IReadOnlyList<ReadyRow> rows = ReadyCheck.Build(info, app, GameLauncher.MissingFfmpegDlls(AppContext.BaseDirectory), SystemInfo.VideoAdapters(), new SaveSummary(false, 0, 0, null, null));
        Assert.All(rows, r => Assert.True(r.State == CheckState.Ok, r.Title + ": " + r.Detail));
        Assert.True(ReadyCheck.CanPlay(info, app, []));
    }

    [GameDataFact]
    public void ThePlanStartsTheGameHiddenAndMutedAndTheSettingsKeysReachIt()
    {
        string game = GameFolder.Require();
        string app = Path.Combine(AppContext.BaseDirectory, GameLauncher.AppFileName);
        var paths = new LauncherPaths(Path.Combine(_root, "Tsuki"));
        Directory.CreateDirectory(paths.DataDirectory);
        AppSettings.Save(
            paths.SettingsFile,
            new AppSettingsData(PromptSetting.Keyboard, Fullscreen: true, WindowWidth: 1600, WindowHeight: 900, VSync: false, GameLanguage: Language.Ja));

        // the arguments the launcher builds (--game, --log) plus the ones that keep a test quiet
        LaunchPlan plan = GameLauncher.Plan(app, game, paths, fullscreen: true, ["--hidden", "--mute", "--fast", "--save-dir", paths.DataDirectory, "--exit-after", "90"]);
        string gameFilesBefore = Fingerprint(game);
        using Process process = GameLauncher.Start(plan, paths);
        Assert.True(process.WaitForExit(180_000), "the game did not exit after 90 steps");
        Assert.Equal(0, process.ExitCode);

        // the log the launcher asked for names the folder it was given
        Assert.True(File.Exists(paths.LastRunLog));
        string log = File.ReadAllText(paths.LastRunLog);
        Assert.Contains("game folder " + game, log, StringComparison.Ordinal);
        Assert.Contains("analysed build: True", log, StringComparison.Ordinal);

        // the keys that apply once were applied to the config block and taken out of the file; the others stay
        SaveSummary save = SaveTools.Summarize(paths.SaveFile);
        Assert.True(save.Exists, log);
        Assert.Null(save.Error);
        Assert.Equal((int)Language.Ja, save.ConfigLanguage);
        Assert.Equal(1, save.WindowMode);
        AppSettingsData left = AppSettings.Load(paths.SettingsFile);
        Assert.Null(left.GameLanguage);
        Assert.Null(left.Fullscreen);
        Assert.Equal(PromptSetting.Keyboard, left.Prompts);
        Assert.Equal((1600, 900), left.WindowSize);
        Assert.Equal(false, left.VSync);

        // and the launcher's reading of the profile now shows what the game did
        var profile = LauncherProfile.Read(paths);
        Assert.Equal(Language.Ja, profile.Language);
        Assert.True(profile.Fullscreen);

        Assert.Equal(gameFilesBefore, Fingerprint(game));             // the game folder was only read
    }

    /// <summary>File count, total size and newest write time of the folder's top two levels (enough to see any write; the archives are tens of gigabytes).</summary>
    private static string Fingerprint(string folder)
    {
        var entries = new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 }).ToList();
        long size = entries.OfType<FileInfo>().Sum(f => f.Length);
        long newest = entries.Max(e => e.LastWriteTimeUtc.Ticks);
        return $"{entries.Count}|{size}|{newest}";
    }
}

using System.Diagnostics;
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Data.Launcher;

/// <summary>
/// Package K18 on the real game, in the real program: a fresh profile reads the prologue (the skipping autoplay, virtual movies, about 500 steps), the title comes up
/// with PRESS ANY BUTTON, Esc asks the quit question, Yes ends the process with exit code 0 and leaves a readable save file. The window is hidden and muted; the
/// profile is a temporary folder and the game folder is only read. Also: the window mode of the command line is remembered and a pending launcher line is consumed.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ExitFromTitleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k18-exit-" + Guid.NewGuid().ToString("N"));

    public ExitFromTitleTests()
    {
        Directory.CreateDirectory(_root);
    }

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

    private (int ExitCode, string Log) Run(string saveDir, params string[] more)
    {
        string game = GameFolder.Require();
        string app = Path.Combine(AppContext.BaseDirectory, GameLauncher.AppFileName);
        string log = Path.Combine(_root, "run-" + Guid.NewGuid().ToString("N") + ".log");
        var info = new ProcessStartInfo(app) { WorkingDirectory = Path.GetDirectoryName(app)!, UseShellExecute = false };
        info.Environment["SDL_AUDIO_DRIVER"] = "dummy";
        foreach (string a in new[] { "--game", game, "--log", log, "--hidden", "--mute", "--fast", "--save-dir", saveDir }.Concat(more))
        {
            info.ArgumentList.Add(a);
        }

        using Process process = Process.Start(info)!;
        Assert.True(process.WaitForExit(180_000), "the game did not exit");
        return (process.ExitCode, File.ReadAllText(log));
    }

    [GameDataFact]
    public void EscapeThenYesOnTheRealTitleEndsTheProcessCleanlyWithAReadableSave()
    {
        string save = Path.Combine(_root, "profile");
        Directory.CreateDirectory(save);

        // the prologue is skipped by the autoplay and ends at step 494; the autoplay then stops, the title fades in (121 steps), Esc asks, Left goes to Yes, Enter answers
        (int code, string log) = Run(save, "--autoplay", "skip", "--script", "496:autopause;700:Esc;760:Left;780:Enter;2000:quit");
        Assert.Equal(0, code);
        Assert.Contains("quit requested (EXIT on the title)", log, StringComparison.Ordinal);
        Assert.DoesNotContain("script quit", log, StringComparison.Ordinal);
        Assert.DoesNotContain("save file not written", log, StringComparison.Ordinal);
        Assert.DoesNotContain("damaged", log, StringComparison.Ordinal);

        string path = Path.Combine(save, "save.dat");
        Assert.True(File.Exists(path));
        SaveStore? store = SaveFile.ReadWithFallback(path, out bool usedBackup);
        Assert.NotNull(store);
        Assert.False(usedBackup);
        Assert.Empty(Directory.GetFiles(save, "*.broken"));
        Assert.Empty(Directory.GetFiles(save, "*.tmp"));
    }

    [GameDataFact]
    public void NoAnswersTheQuitQuestionAndTheGameKeepsRunningUntilTheScriptEnds()
    {
        string save = Path.Combine(_root, "profile-no");
        Directory.CreateDirectory(save);
        (int code, string log) = Run(save, "--autoplay", "skip", "--script", "496:autopause;700:Esc;780:Enter;900:quit");
        Assert.Equal(0, code);
        Assert.Contains("script quit", log, StringComparison.Ordinal);                  // No: the run ended by the script, not by the question
        Assert.DoesNotContain("quit requested (EXIT on the title)", log, StringComparison.Ordinal);
    }

    [GameDataFact]
    public void TheWindowModeOfTheCommandLineIsRememberedAndAPendingLauncherLineIsConsumed()
    {
        string save = Path.Combine(_root, "profile-mode");
        Directory.CreateDirectory(save);
        string settings = Path.Combine(save, AppSettings.FileName);
        AppSettings.Save(settings, new AppSettingsData(Fullscreen: false, VSync: false));

        // a hidden run never changes its window, but it stores the choice like a visible one: --fullscreen first, then --windowed over the remembered fullscreen
        (int code, _) = Run(save, "--fullscreen", "--exit-after", "30");
        Assert.Equal(0, code);
        Assert.Equal(1, SaveTools.Summarize(Path.Combine(save, "save.dat")).WindowMode);
        AppSettingsData left = AppSettings.Load(settings);
        Assert.Null(left.Fullscreen);                                                   // consumed
        Assert.Equal(false, left.VSync);                                                // the other keys stay

        (code, string log) = Run(save, "--windowed", "--exit-after", "30");
        Assert.Equal(0, code);
        Assert.Contains("window mode: windowed (command line, remembered)", log, StringComparison.Ordinal);
        Assert.Equal(0, SaveTools.Summarize(Path.Combine(save, "save.dat")).WindowMode);

        // a start with neither keeps what was remembered
        AppSettings.Save(settings, new AppSettingsData(Fullscreen: true));
        (code, log) = Run(save, "--exit-after", "30");
        Assert.Equal(0, code);
        Assert.Contains("window mode: windowed (remembered)", log, StringComparison.Ordinal);
        Assert.Equal(0, SaveTools.Summarize(Path.Combine(save, "save.dat")).WindowMode);
        Assert.Equal(true, AppSettings.Load(settings).Fullscreen);                      // the file alone decides nothing
    }
}

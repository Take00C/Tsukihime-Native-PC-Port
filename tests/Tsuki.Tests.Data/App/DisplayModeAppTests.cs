using System.Diagnostics;
using Tsuki.App.Services;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Data.App;

/// <summary>
/// Package K19 on the real game, in the real program (hidden, muted, a temporary profile): the config screen's Display Mode row, reached with the keys from the
/// title, switches the window mode and the choice lands in the config block byte the launcher reads; the Window Size row writes <c>window=</c> into settings.ini
/// next to the other keys. The title shows its menu only to a profile whose story was started (a fresh one goes PRESS ANY KEY, cautions, story, as on the PS4),
/// so each test first lets the autoplay run into the story once.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DisplayModeAppTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k19-display-" + Guid.NewGuid().ToString("N"));

    public DisplayModeAppTests()
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
        Assert.True(process.WaitForExit(240_000), "the game did not exit");
        return (process.ExitCode, File.ReadAllText(log));
    }

    // a profile whose story was started: the skipping autoplay reads the prologue (ends at step 494), presses PRESS ANY KEY, sits out the cautions and starts the story
    private string StartedProfile(string name)
    {
        string save = Path.Combine(_root, name);
        Directory.CreateDirectory(save);
        (int code, string log) = Run(save, "--windowed", "--autoplay", "skip", "--exit-after", "2600");
        Assert.Equal(0, code);
        Assert.Contains("story ended: ReturnKeepBgm", log, StringComparison.Ordinal);
        return save;
    }

    // the returning start (measured on this build with --fast: boot notice, the cautions skipped by Enter at 60 and 300, the title's menu up and taking keys by step 500):
    // Up three times from NEW GAME wraps to EXIT, GALLERY, CONFIG (with or without a CONTINUE item), Enter opens the config screen; on its System tab the cursor starts on the
    // first slider's minus button and 16 Downs walk the rows (4 sliders, 2 + 6 radio rows, 4 languages) to the Display Mode row
    private const string ToConfig = "60:Enter;300:Enter;600:Up;620:Up;640:Up;660:Enter";

    private static string ToDisplayRow()
    {
        var steps = new List<string> { ToConfig };
        for (int i = 0; i < 16; i++)
        {
            steps.Add($"{800 + (12 * i)}:Down");
        }

        return string.Join(";", steps);
    }

    [GameDataFact]
    public void TheDisplayModeRowSwitchesToFullscreenAndTheLauncherReadsTheRememberedByte()
    {
        string save = StartedProfile("profile-mode");
        (int code, string log) = Run(save, "--windowed", "--script", $"{ToDisplayRow()};990:log;1000:Right;1020:Enter;1060:shot=display_row;1060:log;1080:quit", "--shot-dir", save);
        Assert.Equal(0, code);
        Assert.Contains("window mode: windowed (command line, remembered)", log, StringComparison.Ordinal);
        Assert.Contains("ui Title [TitleScreen>ConfigScreen]", log, StringComparison.Ordinal);               // the state line of the script's log verb: the config screen is open over the title
        Assert.Contains("[   1020] window mode: fullscreen (remembered)", log, StringComparison.Ordinal);   // the row's choice at the Enter, stored by the display settings
        Assert.Contains("win 0]", log, StringComparison.Ordinal);                                             // the state line before ...
        Assert.Contains("win 1]", log, StringComparison.Ordinal);                                             // ... and after
        Assert.Equal(1, SaveTools.Summarize(Path.Combine(save, "save.dat")).WindowMode);
        Assert.True(new LauncherProfile(AppSettings.Load(Path.Combine(save, AppSettings.FileName)), SaveTools.Summarize(Path.Combine(save, "save.dat"))).Fullscreen);
        Assert.True(File.Exists(Path.Combine(save, "display_row.png")));

        // the next start with no flag keeps what the row chose, and --windowed from the launcher overrides it
        (code, log) = Run(save, "--exit-after", "30");
        Assert.Equal(0, code);
        Assert.Contains("window mode: fullscreen (remembered)", log, StringComparison.Ordinal);
        (code, log) = Run(save, "--windowed", "--exit-after", "30");
        Assert.Equal(0, code);
        Assert.Equal(0, SaveTools.Summarize(Path.Combine(save, "save.dat")).WindowMode);
    }

    [GameDataFact]
    public void TheWindowSizeRowWritesTheSizeIntoSettingsIniNextToTheOtherKeys()
    {
        string save = StartedProfile("profile-size");
        string settings = Path.Combine(save, AppSettings.FileName);
        AppSettings.Save(settings, new AppSettingsData(Tsuki.Ui.PromptSetting.Xbox, VSync: false, WindowWidth: 1920, WindowHeight: 1080));
        // Down once more to the Window Size row (the cursor keeps its column: the first preset), Right to the second one (1600 x 900), Enter
        (int code, string log) = Run(save, "--windowed", "--script", $"{ToDisplayRow()};1000:Down;1010:Right;1030:Enter;1070:log;1090:quit");
        Assert.Equal(0, code);
        Assert.Contains("ui Title [TitleScreen>ConfigScreen]", log, StringComparison.Ordinal);
        Assert.Contains("window size: 1600 x 900 (config screen)", log, StringComparison.Ordinal);
        Assert.Contains("window size 1600 x 900 saved to settings.ini", log, StringComparison.Ordinal);
        AppSettingsData left = AppSettings.Load(settings);
        Assert.Equal((1600, 900), left.WindowSize);
        Assert.Equal(Tsuki.Ui.PromptSetting.Xbox, left.Prompts);                                           // the other keys stay
        Assert.Equal(false, left.VSync);
        Assert.Equal(0, SaveTools.Summarize(Path.Combine(save, "save.dat")).WindowMode);
    }
}

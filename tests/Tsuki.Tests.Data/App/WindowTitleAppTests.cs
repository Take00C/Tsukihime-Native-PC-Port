using System.Diagnostics;
using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Launcher.Core;
using Tsuki.Tests.Data.Source;

namespace Tsuki.Tests.Data.App;

/// <summary>
/// Package K20 on the real game, in the real program (hidden, muted, a temporary profile): the window title is the game's own name from the folder's param.sfo in the
/// profile's language, and it changes at once when the config screen's language row is used. The hidden window's caption is read from the log line the game writes
/// whenever it sets it.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowTitleAppTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k20-title-" + Guid.NewGuid().ToString("N"));

    public WindowTitleAppTests()
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

    [GameDataFact]
    public void TheRealParamSfoHoldsTheFourTitlesAndTheGameWindowTakesTheOneOfItsLanguage()
    {
        string game = GameFolder.Require();
        ParamSfo sfo = ParamSfo.TryRead(Path.Combine(game, "sce_sys", "param.sfo"))!;
        Assert.NotNull(sfo);
        Assert.Equal("月姫 -A piece of blue glass moon-", GameTitles.Of(sfo, Language.Ja));
        Assert.Equal("月姬 -A piece of blue glass moon-", GameTitles.Of(sfo, Language.Zc));
        Assert.Equal("月姬 -A piece of blue glass moon-", GameTitles.Of(sfo, Language.Zt));
        Assert.StartsWith("TSUKIHIME", GameTitles.Of(sfo, Language.En), StringComparison.Ordinal);
        Assert.Equal(SharedSource.Source.Title(Language.Ja), GameTitles.Of(sfo, Language.Ja));

        string save = Path.Combine(_root, "profile-ja");
        Directory.CreateDirectory(save);
        (int code, string log) = Run(save, "--lang", "ja", "--exit-after", "30");
        Assert.Equal(0, code);
        Assert.Contains("window title: 月姫 -A piece of blue glass moon-", log, StringComparison.Ordinal);
        Assert.DoesNotContain("window title: TSUKIHIME", log, StringComparison.Ordinal);

        string saveEn = Path.Combine(_root, "profile-en");
        Directory.CreateDirectory(saveEn);
        (code, log) = Run(saveEn, "--lang", "en", "--exit-after", "30");
        Assert.Equal(0, code);
        Assert.Contains("window title: " + GameTitles.Of(sfo, Language.En), log, StringComparison.Ordinal);
        // the test output's Tsukihime.exe is the apphost with the icon group, so the window class takes it; a host without the group takes the embedded picture
        Assert.Contains("window icon: the executable's icon group", log, StringComparison.Ordinal);
        Assert.DoesNotContain("window icon: none", log, StringComparison.Ordinal);
        Assert.DoesNotContain("window icon: refused", log, StringComparison.Ordinal);
    }

    // the returning start (DisplayModeAppTests): the cautions skipped, Up three times from NEW GAME wraps to CONFIG, Enter opens it; 12 Downs on the System tab walk
    // the 4 sliders and the 2 + 6 radio rows to the first language row (日本語), and Enter picks it
    private const string ToConfig = "60:Enter;300:Enter;600:Up;620:Up;640:Up;660:Enter";

    [GameDataFact]
    public void ChoosingJapaneseInTheConfigScreenRetitlesTheWindowAtOnce()
    {
        string save = Path.Combine(_root, "profile-switch");
        Directory.CreateDirectory(save);
        (int code, string log) = Run(save, "--lang", "en", "--windowed", "--autoplay", "skip", "--exit-after", "2600");
        Assert.Equal(0, code);
        Assert.Contains("story ended: ReturnKeepBgm", log, StringComparison.Ordinal);

        var steps = new List<string> { ToConfig };
        for (int i = 0; i < 12; i++)
        {
            steps.Add($"{800 + (12 * i)}:Down");
        }

        steps.Add("980:log;1000:Enter;1100:log;1120:quit");
        (code, log) = Run(save, "--windowed", "--script", string.Join(";", steps));
        Assert.Equal(0, code);
        Assert.Contains("ui Title [TitleScreen>ConfigScreen]", log, StringComparison.Ordinal);
        int english = log.IndexOf("window title: TSUKIHIME", StringComparison.Ordinal);
        int japanese = log.IndexOf("window title: 月姫 -A piece of blue glass moon-", StringComparison.Ordinal);
        Assert.True(english >= 0, "the run started with the English title");
        Assert.True(japanese > english, "the Japanese title followed the language row's Enter");
        Assert.Contains("[   1000] window title: 月姫", log, StringComparison.Ordinal);
    }
}

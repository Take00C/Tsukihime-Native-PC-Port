using Tsuki.App;
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;
using Tsuki.Launcher.Core;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>Package K17: settings.ini as the launcher writes it and the game reads it, and what the game does with it at start.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-k17-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    private string SettingsPath => Path.Combine(_dir, AppSettings.FileName);

    private static AppSettingsData Everything() => new(
        PromptSetting.Xbox,
        Fullscreen: true,
        WindowWidth: 2560,
        WindowHeight: 1440,
        Monitor: new MonitorOrigin(-1920, 0),
        VSync: false,
        GameLanguage: Language.Zt,
        MuteInBackground: true,
        CopyText: true,
        TextHooker: false,
        TextWebSocket: true,
        TextWebSocketPort: 6700,
        ScreenshotNotice: false);

    [Fact]
    public void EveryKeyRoundTripsThroughTheFile()
    {
        AppSettingsData data = Everything();
        Assert.Equal(data, AppSettings.Parse(AppSettings.Format(data)));
        Assert.True(AppSettings.Save(SettingsPath, data));
        Assert.Equal(data, AppSettings.Load(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void KeysThatAreNotSetStayOutOfTheFile()
    {
        string text = AppSettings.Format(new AppSettingsData());
        Assert.Contains("prompts=auto", text, StringComparison.Ordinal);
        foreach (string key in new[] { "fullscreen=", "window=", "monitor=", "vsync=", "language=", "mute_in_background=", "copy_text=", "text_hooker=", "text_websocket=", "text_websocket_port=", "screenshot_notice=" })
        {
            Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        }

        Assert.Equal(new AppSettingsData(), AppSettings.Parse(text));
    }

    [Theory]
    [InlineData("text_websocket_port=6677", 6677)]
    [InlineData("text_websocket_port = 1", 1)]
    [InlineData("text_websocket_port=65535", 65535)]
    [InlineData("text_websocket_port=0", null)]
    [InlineData("text_websocket_port=65536", null)]
    [InlineData("text_websocket_port=-5", null)]
    [InlineData("text_websocket_port=ws", null)]
    public void TheWebSocketPortReadsOneToSixtyFiveThousandAndIgnoresTheRest(string line, int? expected)
    {
        // package K23: the port key; the switches read on / off like the others and the hooker one is on when absent (PortSettings decides that)
        Assert.Equal(expected, AppSettings.Parse(line).TextWebSocketPort);
        Assert.Equal(6677, AppSettingsData.DefaultTextWebSocketPort);
        AppSettingsData both = AppSettings.Parse("text_hooker=off\ntext_websocket=on");
        Assert.Equal((false, true), (both.TextHooker, both.TextWebSocket));
        Assert.Null(AppSettings.Parse("text_hooker=sometimes").TextHooker);
    }

    [Theory]
    [InlineData("screenshot_notice=off", false)]
    [InlineData("SCREENSHOT_NOTICE = 0", false)]
    [InlineData("screenshot_notice=on", true)]
    [InlineData("screenshot_notice=yes", true)]
    [InlineData("screenshot_notice=sometimes", null)]
    [InlineData("prompts=auto", null)]
    public void TheScreenshotNoticeKeyReadsOnAndOffAndIsWrittenWithItsComment(string line, bool? expected)
    {
        // package K24: the key behind Config > System > Screenshot Notice; absent means on (PortSettings decides that)
        Assert.Equal(expected, AppSettings.Parse(line).ScreenshotNotice);
        string text = AppSettings.Format(new AppSettingsData(ScreenshotNotice: false));
        Assert.Contains("# screenshot_notice:", text, StringComparison.Ordinal);
        Assert.Contains("screenshot_notice=off\n", text, StringComparison.Ordinal);
        Assert.Equal(false, AppSettings.Parse(text).ScreenshotNotice);
        Assert.Contains("screenshot_notice=on\n", AppSettings.Format(new AppSettingsData(ScreenshotNotice: true)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fullscreen=on", true)]
    [InlineData("fullscreen=ON", true)]
    [InlineData("fullscreen = 1", true)]
    [InlineData("fullscreen=true", true)]
    [InlineData("fullscreen=off", false)]
    [InlineData("fullscreen=0", false)]
    [InlineData("fullscreen=maybe", null)]
    public void FullscreenReadsOnAndOffAndIgnoresNonsense(string line, bool? expected)
    {
        Assert.Equal(expected, AppSettings.Parse(line).Fullscreen);
    }

    [Theory]
    [InlineData("window=1600x900", 1600, 900)]
    [InlineData("window=1920 x 1080", 1920, 1080)]
    [InlineData("window=2560×1440", 2560, 1440)]
    [InlineData("window=640x360", 640, 360)]
    public void TheWindowSizeIsReadInSeveralSpellings(string line, int width, int height)
    {
        Assert.Equal((width, height), AppSettings.Parse(line).WindowSize);
    }

    [Theory]
    [InlineData("window=100x100")]
    [InlineData("window=99999x1080")]
    [InlineData("window=1600")]
    [InlineData("window=axb")]
    [InlineData("window=-1600x900")]
    public void ASizeOutOfRangeOrBrokenKeepsTheDefault(string line)
    {
        Assert.Null(AppSettings.Parse(line).WindowSize);
    }

    [Theory]
    [InlineData("monitor=1920,0", 1920, 0)]
    [InlineData("monitor= -1080 , 240", -1080, 240)]
    public void TheMonitorIsTheCornerOfTheDisplay(string line, int x, int y)
    {
        Assert.Equal(new MonitorOrigin(x, y), AppSettings.Parse(line).Monitor);
    }

    [Theory]
    [InlineData("monitor=primary")]
    [InlineData("monitor=1")]
    [InlineData("monitor=1,2,3")]
    public void ABrokenMonitorKeepsTheDefault(string line)
    {
        Assert.Null(AppSettings.Parse(line).Monitor);
    }

    [Theory]
    [InlineData("language=ja", Language.Ja)]
    [InlineData("language=EN", Language.En)]
    [InlineData("language=zc", Language.Zc)]
    [InlineData("language=zt", Language.Zt)]
    [InlineData("language=zh-tw", Language.Zt)]
    [InlineData("language=3", Language.Zt)]
    public void TheLanguageIsReadByCode(string line, Language expected)
    {
        Assert.Equal(expected, AppSettings.Parse(line).GameLanguage);
    }

    [Theory]
    [InlineData("language=ko")]
    [InlineData("language=klingon")]
    [InlineData("language=")]
    public void KoreanIsNeverSelectableAndNonsenseIsIgnored(string line)
    {
        Assert.Null(AppSettings.Parse(line).GameLanguage);
    }

    [Fact]
    public void UpdateKeepsTheKeysItDoesNotTouch()
    {
        AppSettings.Save(SettingsPath, Everything());
        Assert.True(AppSettings.Update(SettingsPath, s => s with { VSync = true }));
        AppSettingsData now = AppSettings.Load(SettingsPath);
        Assert.Equal(Everything() with { VSync = true }, now);
    }

    [Fact]
    public void TheGamesPromptSettingKeepsTheLaunchersKeys()
    {
        // K9's prompt state writes settings.ini when the player changes the button prompts in the game; it must not drop what the launcher put there
        AppSettings.Save(SettingsPath, Everything() with { Prompts = PromptSetting.Auto });
        var prompts = new PromptState(SettingsPath);
        prompts.Setting = PromptSetting.PlayStation;
        AppSettingsData now = AppSettings.Load(SettingsPath);
        Assert.Equal(PromptSetting.PlayStation, now.Prompts);
        Assert.Equal(Everything() with { Prompts = PromptSetting.PlayStation }, now);
    }

    [Fact]
    public void AMissingOrUnreadableFileMeansTheDefaults()
    {
        Assert.Equal(new AppSettingsData(), AppSettings.Load(Path.Combine(_dir, "nothing.ini")));
        Assert.Equal(new AppSettingsData(), AppSettings.Load(null));
        Assert.False(AppSettings.Save(null, Everything()));
        Assert.False(AppSettings.Update(null, s => s));
        Directory.CreateDirectory(Path.Combine(_dir, "folder.ini"));
        Assert.Equal(new AppSettingsData(), AppSettings.Load(Path.Combine(_dir, "folder.ini")));
    }

    // -- what the game does with the file at start ----------------------------------------------------------------------

    private static AppArgs Args(params string[] args)
    {
        Assert.True(AppArgs.TryParse(args, out AppArgs? parsed, out string? error), error);
        return parsed!;
    }

    [Fact]
    public void TheGameReadsTheFileOfItsSaveFolderAndNoFileWithNoSave()
    {
        AppSettings.Save(SettingsPath, Everything());
        Assert.Equal(Everything(), StartupSettings.Load(Args("--save-dir", _dir)).Data);
        Assert.Equal(new AppSettingsData(), StartupSettings.Load(Args("--save-dir", _dir, "--no-save")).Data);
        Assert.Null(StartupSettings.Load(Args("--save-dir", _dir, "--no-save")).Path);
        Assert.Equal(new AppSettingsData(), StartupSettings.Load(Args("--save-dir", Path.Combine(_dir, "elsewhere"))).Data);
    }

    [Fact]
    public void TheWindowOptionsFollowTheFileAndTheWindowIsCreatedHiddenUntilItIsPlaced()
    {
        // package K19: the window is created hidden at the requested size; the app clamps and centres it (WindowPlacement.Place), shows it, and only then asks for
        // --fullscreen, so a window never comes up at the wrong size or place and fullscreen takes the display the window was moved to
        var options = StartupSettings.From(Everything()).WindowOptions();
        Assert.Equal((2560, 1440), (options.Width, options.Height));
        Assert.Equal((2560, 1440), StartupSettings.From(Everything()).RequestedWindowSize);
        Assert.Equal(0, options.SwapInterval);
        Assert.True(options.Hidden);
        Assert.False(options.Fullscreen);
        Assert.True(options.Resizable);

        var defaults = StartupSettings.From(new AppSettingsData()).WindowOptions();
        Assert.Equal((Tsuki.Platform.WindowFit.DefaultWidth, Tsuki.Platform.WindowFit.DefaultHeight), (defaults.Width, defaults.Height));
        Assert.Equal((1600, 900), (defaults.Width, defaults.Height));
        Assert.Equal(1, defaults.SwapInterval);
        Assert.True(defaults.Hidden);
        Assert.Equal(1, StartupSettings.From(new AppSettingsData(VSync: true)).WindowOptions().SwapInterval);
    }

    [Fact]
    public void LanguageAndTheCommandLinesWindowModeAreAppliedToTheConfigBlockOnceAndRemovedFromTheFile()
    {
        AppSettings.Save(SettingsPath, Everything());
        StartupSettings startup = StartupSettings.Load(Args("--save-dir", _dir, "--fullscreen"));
        ConfigBlock config = ConfigBlock.CreateBootDefaults();
        config.Language = -1;

        Assert.True(startup.ApplyToConfig(config, languageGivenOnCommandLine: false, Args("--fullscreen").WindowModeChoice));
        Assert.Equal((short)Language.Zt, config.Language);
        Assert.Equal(1, config.Bytes[StartupSettings.WindowModeOffset]);

        AppSettingsData left = AppSettings.Load(SettingsPath);
        Assert.Null(left.GameLanguage);
        Assert.Null(left.Fullscreen);
        Assert.Equal(Everything() with { Fullscreen = null, GameLanguage = null }, left);     // window, monitor, vsync and prompts are not one-shot

        // the next start has nothing to apply, so the config screen's choice and Alt+Enter keep the last word
        ConfigBlock again = ConfigBlock.CreateBootDefaults();
        again.Language = 1;
        Assert.False(StartupSettings.Load(Args("--save-dir", _dir)).ApplyToConfig(again, false));
        Assert.Equal((short)1, again.Language);
    }

    [Fact]
    public void TheLangOptionBeatsTheFileAndKeepsItsLanguageKey()
    {
        AppSettings.Save(SettingsPath, new AppSettingsData(GameLanguage: Language.Ja, Fullscreen: false));
        ConfigBlock config = ConfigBlock.CreateBootDefaults();
        config.Language = (short)Language.En;
        config.Bytes[StartupSettings.WindowModeOffset] = 1;

        Assert.True(StartupSettings.Load(Args("--save-dir", _dir, "--lang", "en", "--windowed")).ApplyToConfig(config, languageGivenOnCommandLine: true, windowMode: false));
        Assert.Equal((short)Language.En, config.Language);
        Assert.Equal(0, config.Bytes[StartupSettings.WindowModeOffset]);        // --windowed was on the command line
        AppSettingsData left = AppSettings.Load(SettingsPath);
        Assert.Equal(Language.Ja, left.GameLanguage);                           // not applied, so not consumed
        Assert.Null(left.Fullscreen);                                           // the window mode was
    }

    // -- package K18: the launcher's window mode wins on every launch ---------------------------------------------------------

    [Fact]
    public void TheCommandLineSaysFullscreenWindowedOrNothingAndTheLastOneWins()
    {
        Assert.Null(Args().WindowModeChoice);
        Assert.Equal(true, Args("--fullscreen").WindowModeChoice);
        Assert.Equal(false, Args("--windowed").WindowModeChoice);
        Assert.Equal(false, Args("--fullscreen", "--windowed").WindowModeChoice);
        Assert.Equal(true, Args("--windowed", "--fullscreen").WindowModeChoice);
        Assert.True(Args("--windowed").Windowed);
        Assert.False(Args("--windowed").Fullscreen);
    }

    [Fact]
    public void WindowedBeatsARememberedFullscreenAndFullscreenBeatsARememberedWindow()
    {
        ConfigBlock config = ConfigBlock.CreateBootDefaults();
        config.Bytes[StartupSettings.WindowModeOffset] = 1;                       // Alt+Enter / F11 left the game fullscreen last time
        StartupSettings startup = StartupSettings.From(new AppSettingsData());
        Assert.True(startup.ApplyToConfig(config, false, Args("--windowed").WindowModeChoice));
        Assert.Equal(0, config.Bytes[StartupSettings.WindowModeOffset]);          // this run and the next one are windowed
        Assert.False(Args("--windowed").Fullscreen);                              // and the app asks for no fullscreen after placing the window

        Assert.True(startup.ApplyToConfig(config, false, Args("--fullscreen").WindowModeChoice));
        Assert.Equal(1, config.Bytes[StartupSettings.WindowModeOffset]);
        Assert.True(Args("--fullscreen").Fullscreen);
    }

    [Fact]
    public void ARepeatedWindowModeChangesNothingToSaveButStillConsumesThePendingKey()
    {
        AppSettings.Save(SettingsPath, new AppSettingsData(Fullscreen: false));
        ConfigBlock config = ConfigBlock.CreateBootDefaults();                    // windowed
        StartupSettings startup = StartupSettings.Load(Args("--save-dir", _dir, "--windowed"));
        Assert.False(startup.ApplyToConfig(config, false, windowMode: false));    // nothing new for the save file: the launcher passes --windowed on every PLAY
        Assert.Equal(0, config.Bytes[StartupSettings.WindowModeOffset]);
        Assert.Null(AppSettings.Load(SettingsPath).Fullscreen);
    }

    [Fact]
    public void AStartWithoutAWindowModeKeepsTheRememberedOneAndIgnoresALeftOverFileKey()
    {
        AppSettings.Save(SettingsPath, new AppSettingsData(Fullscreen: true));    // a pending launcher choice nobody has played with yet
        foreach (byte remembered in new byte[] { 0, 1 })
        {
            ConfigBlock config = ConfigBlock.CreateBootDefaults();
            config.Bytes[StartupSettings.WindowModeOffset] = remembered;
            Assert.False(StartupSettings.Load(Args("--save-dir", _dir)).ApplyToConfig(config, false, Args().WindowModeChoice));
            Assert.Equal(remembered, config.Bytes[StartupSettings.WindowModeOffset]);
        }

        Assert.Equal(true, AppSettings.Load(SettingsPath).Fullscreen);            // untouched: only a PLAY consumes it
    }

    [Fact]
    public void ThePendingFileKeyIsConsumedByTheWindowModeOfAStartAndTheRememberedByteReachesTheSaveFileTheLauncherReads()
    {
        AppSettings.Save(SettingsPath, new AppSettingsData(Fullscreen: false, VSync: false));
        var store = new SaveStore();
        store.SystemData.Config.CopyFrom(ConfigBlock.CreateBootDefaults());
        store.SystemData.Config.Bytes[AppSettings.WindowModeOffset] = 1;
        Assert.Equal(StartupSettings.WindowModeOffset, AppSettings.WindowModeOffset);

        // the launcher shows the pending choice (off) although the remembered state is fullscreen ...
        string savePath = Path.Combine(_dir, "save.dat");
        SaveFile.WriteAtomic(savePath, store);
        var before = new LauncherProfile(AppSettings.Load(SettingsPath), SaveTools.Summarize(savePath));
        Assert.False(before.Fullscreen);
        Assert.Equal(1, before.Saves.WindowMode);

        // ... PLAY passes --windowed, the game stores it and removes the key ...
        Assert.True(StartupSettings.Load(Args("--save-dir", _dir, "--windowed")).ApplyToConfig(store.SystemData.Config, false, windowMode: false));
        SaveFile.WriteAtomic(savePath, store);

        // ... and now the key is gone and the switch reads the remembered value, which is what the next start will use
        var after = new LauncherProfile(AppSettings.Load(SettingsPath), SaveTools.Summarize(savePath));
        Assert.Null(after.Settings.Fullscreen);
        Assert.Equal(false, after.Settings.VSync);                                // the other keys stay
        Assert.Equal(0, after.Saves.WindowMode);
        Assert.False(after.Fullscreen);
    }

    [Fact]
    public void ANewProfileTakesTheLaunchersLanguage()
    {
        AppSettings.Save(SettingsPath, new AppSettingsData(GameLanguage: Language.Zc));
        var store = new SaveStore();
        store.SystemData.Config.CopyFrom(ConfigBlock.CreateBootDefaults());
        store.SystemData.Config.Language = -1;                                   // what the game writes for a profile that has none
        Assert.True(StartupSettings.Load(Args("--save-dir", _dir)).ApplyToConfig(store.SystemData.Config, false));
        Assert.Equal((short)Language.Zc, store.SystemData.Config.Language);
    }

    // -- the launcher's reading of the same two facts -----------------------------------------------------------------------

    [Fact]
    public void TheLauncherShowsThePendingKeyOrWhatTheSaveFileSays()
    {
        var saved = new SaveSummary(true, 3, 2, null, null, ConfigLanguage: 2, WindowMode: 1);
        var profile = new LauncherProfile(new AppSettingsData(), saved);
        Assert.Equal(Language.Zc, profile.Language);
        Assert.True(profile.Fullscreen);

        var pending = new LauncherProfile(new AppSettingsData(Fullscreen: false, GameLanguage: Language.Ja), saved);
        Assert.Equal(Language.Ja, pending.Language);
        Assert.False(pending.Fullscreen);

        var fresh = new LauncherProfile(new AppSettingsData(), new SaveSummary(false, 0, 0, null, null));
        Assert.Equal(Language.En, fresh.Language);
        Assert.False(fresh.Fullscreen);
        Assert.True(fresh.VSync);
        Assert.Equal((1600, 900), fresh.WindowSize);                              // package K19: the default windowed size is 1600 x 900
        Assert.Equal("1600 × 900  ·  Windowed  ·  English  ·  Auto prompts", fresh.StatusLine());
        Assert.Equal("Fullscreen  ·  简体中文  ·  Auto prompts", profile.StatusLine());
        Assert.Equal("2560 × 1440  ·  Fullscreen  ·  简体中文  ·  Auto prompts", profile.StatusLine((2560, 1440), (2560, 1392)));
    }

    // -- package K19: the launcher offers the presets that fit the monitor and shows the size the game will use ------------------------------------

    [Fact]
    public void TheLauncherShowsTheSizeTheGameWillUseOnTheChosenMonitor()
    {
        // the user's file: window=2560x1440 for a 2560 x 1440 monitor whose work area is 2560 x 1392; the game clamps that to 2224 x 1251
        var profile = new LauncherProfile(new AppSettingsData(WindowWidth: 2560, WindowHeight: 1440), new SaveSummary(false, 0, 0, null, null));
        Assert.Equal((2560, 1440), profile.WindowSize);
        Assert.Equal((2224, 1251), profile.EffectiveWindowSize((2560, 1392)));
        Assert.Equal((2560, 1440), profile.EffectiveWindowSize(null));              // no monitor known: shown as asked for
        Assert.Equal((2560, 1440), profile.EffectiveWindowSize((3840, 2100)));      // a monitor it fits
        Assert.Equal("2224 × 1251  ·  Windowed  ·  English  ·  Auto prompts", profile.StatusLine((2560, 1440), (2560, 1392)));
        Assert.Equal("2560 × 1440  ·  Windowed  ·  English  ·  Auto prompts", profile.StatusLine());
    }

    [Fact]
    public void TheLauncherOffersOnlyThePresetsThatFitTheMonitor()
    {
        Assert.Equal(["1280 × 720", "1600 × 900", "1920 × 1080"], WindowSizeChoice.For((2560, 1392)).Select(c => c.Label));
        Assert.Equal(4, WindowSizeChoice.For((3840, 2100)).Count);
        Assert.Empty(WindowSizeChoice.For((1366, 728)));
        Assert.Equal(4, WindowSizeChoice.All.Count);
        Assert.Equal(new WindowSizeChoice(1600, 900), WindowSizeChoice.All[1]);      // value equality: the Display page's selection finds its item

        var monitor = new MonitorEntry(1, 0, 0, 2560, 1440, true, "Display", 2560, 1392);
        Assert.Equal((2560, 1392), monitor.WorkArea);
        Assert.Equal((2560, 1440), new MonitorEntry(1, 0, 0, 2560, 1440, true, "Display").WorkArea);       // unknown work area: the monitor itself
    }
}

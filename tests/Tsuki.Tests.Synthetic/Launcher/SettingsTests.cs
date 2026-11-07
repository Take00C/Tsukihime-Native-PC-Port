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
        ScreenshotNotice: false,
        UpdateCheck: false,
        TextWebSocketOrigin: "https://reader.example",
        RememberedFullscreen: true);

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
        foreach (string key in new[] { "fullscreen=", "display_mode=", "window=", "monitor=", "vsync=", "language=", "mute_in_background=", "copy_text=", "text_hooker=", "text_websocket=", "text_websocket_port=", "text_websocket_origin=", "screenshot_notice=", "update_check=" })
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
    [InlineData("https://reader.example/", "https://reader.example")]
    [InlineData("HTTPS://READER.EXAMPLE:443", "https://reader.example")]
    [InlineData("http://127.0.0.1:8000", "http://127.0.0.1:8000")]
    [InlineData("null", null)]
    [InlineData("*", null)]
    [InlineData("file:///reader.html", null)]
    [InlineData("https://reader.example/page", null)]
    [InlineData("https://reader.example?query", null)]
    [InlineData("https://reader.example#fragment", null)]
    [InlineData("https://user@reader.example", null)]
    [InlineData("https://reader.example\\page", null)]
    public void BrowserAuthorizationUsesOneExplicitOrigin(string text, string? expected)
    {
        AppSettingsData data = AppSettings.Parse("text_websocket_origin=" + text);
        Assert.Equal(expected, data.TextWebSocketOrigin);
        Assert.Equal(expected, AppSettings.Parse(AppSettings.Format(data)).TextWebSocketOrigin);
        // K46: an absent or refused value leaves the game trusting texthooker-ui's page, as v0.1.5 let it in
        Assert.Equal(expected ?? AppSettingsData.DefaultTextWebSocketOrigin, new PortSettings(null, data).TextWebSocketOrigin);
        Assert.Equal("https://renji-xd.github.io", new PortSettings(null, new AppSettingsData()).TextWebSocketOrigin);
    }

    [Fact]
    public void UpdatingOtherSettingsPreservesTheTrustedBrowserOrigin()
    {
        Assert.True(AppSettings.Save(SettingsPath, Everything()));
        Assert.True(AppSettings.Update(SettingsPath, data => data with { TextWebSocket = false }));
        AppSettingsData loaded = AppSettings.Load(SettingsPath);
        Assert.Equal("https://reader.example", loaded.TextWebSocketOrigin);
        Assert.False(loaded.TextWebSocket);
        Assert.False(AppSettings.TryParseWebSocketOrigin("https://reader.example\r\nOrigin: https://untrusted.example", out _));
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
    [InlineData("update_check=off", false)]
    [InlineData("UPDATE_CHECK = no", false)]
    [InlineData("update_check=on", true)]
    [InlineData("update_check=maybe", null)]
    [InlineData("prompts=auto", null)]
    public void TheUpdateCheckKeyReadsOnAndOffAndTheGameKeepsIt(string line, bool? expected)
    {
        // package K40: Help & about > Check for updates when the launcher starts; absent means on (the launcher decides that)
        Assert.Equal(expected, AppSettings.Parse(line).UpdateCheck);
        string text = AppSettings.Format(new AppSettingsData(UpdateCheck: false));
        Assert.Contains("# update_check:", text, StringComparison.Ordinal);
        Assert.Contains("update_check=off\n", text, StringComparison.Ordinal);

        // the game rewrites settings.ini when its prompt row changes: the launcher's key survives
        string path = Path.Combine(_dir, "update_check.ini");
        Assert.True(AppSettings.Save(path, new AppSettingsData(UpdateCheck: false)));
        Assert.True(AppSettings.Update(path, s => s with { Prompts = PromptSetting.PlayStation }));
        Assert.Equal(false, AppSettings.Load(path).UpdateCheck);
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

    // -- the window mode of a start: one decision (StartupSettings.DecideWindowMode), which GameApp calls once ---------------------------------------------------

    // v0.1.6's start, written out as GameApp.cs of c3e9698 did it: fullscreen right after showing the window for --fullscreen, the log line after ApplyToConfig,
    // fullscreen once the game is set up when the byte says so
    private static (bool AtShow, string Log, bool AfterSetUp) V016Start(AppArgs args, ConfigBlock config, StartupSettings startup)
    {
        bool atShow = args.Fullscreen;
        startup.ApplyToConfig(config, false, args.WindowModeChoice);
        bool byteSaysFullscreen = config.Bytes[StartupSettings.WindowModeOffset] == 1;
        string log = args.WindowModeChoice is { } choice
            ? $"window mode: {(choice ? "fullscreen" : "windowed")} (command line, remembered)"
            : $"window mode: {(byteSaysFullscreen ? "fullscreen" : "windowed")} (remembered)";
        return (atShow, log, byteSaysFullscreen);
    }

    [Fact]
    public void WindowsAndLinuxWithoutTheSteamDeckStartAsV016DidForEveryKindOfStart()
    {
        var platforms = new (bool Linux, string? Marker)[] { (false, null), (false, "1"), (false, "0"), (true, ""), (true, "0"), (true, "true") };
        string[][] options = [[], ["--fullscreen"], ["--windowed"]];
        bool?[] switches = [null, true, false];
        int starts = 0;
        foreach ((bool linux, string? marker) in platforms)
        {
            foreach (string[] option in options)
            {
                foreach (byte remembered in new byte[] { 0, 1 })
                {
                    foreach (bool? displayMode in switches)
                    {
                        foreach (bool? pending in switches)
                        {
                            AppSettings.Save(SettingsPath, new AppSettingsData(Fullscreen: pending, RememberedFullscreen: displayMode));
                            AppArgs args = Args([.. option, "--save-dir", _dir]);
                            ConfigBlock config = ConfigBlock.CreateBootDefaults();
                            config.Bytes[StartupSettings.WindowModeOffset] = remembered;
                            StartupSettings startup = StartupSettings.Load(args);

                            StartWindowMode mode = startup.DecideWindowMode(args, config, marker, linux);
                            (bool atShow, string log, bool afterSetUp) = V016Start(args, config, startup);
                            string start = $"linux {linux}, SteamDeck {marker}, [{string.Join(' ', option)}], byte {remembered}, display_mode {displayMode}, fullscreen= {pending}";
                            Assert.False(mode.SteamDeck, start);
                            Assert.True(atShow == mode.FullscreenAtShow, start);
                            Assert.True(log == mode.LogLine, $"{start}: {mode.LogLine}");
                            Assert.True(afterSetUp == mode.FullscreenAfterSetUp, start);
                            starts++;
                        }
                    }
                }
            }
        }

        Assert.Equal(6 * 3 * 2 * 3 * 3, starts);
    }

    [Theory]
    [InlineData(false, null, 0, "window mode: windowed (remembered)")]
    [InlineData(false, null, 1, "window mode: fullscreen (remembered)")]
    [InlineData(false, "--fullscreen", 0, "window mode: fullscreen (command line, remembered)")]
    [InlineData(false, "--windowed", 1, "window mode: windowed (command line, remembered)")]
    [InlineData(true, null, 0, "window mode: windowed (remembered)")]
    [InlineData(true, null, 1, "window mode: fullscreen (remembered)")]
    [InlineData(true, "--fullscreen", 0, "window mode: fullscreen (command line, remembered)")]
    [InlineData(true, "--windowed", 1, "window mode: windowed (command line, remembered)")]
    public void TheStartLogLineOfWindowsAndLinuxIsV016s(bool linux, string? option, byte remembered, string expected)
    {
        ConfigBlock config = ConfigBlock.CreateBootDefaults();
        config.Bytes[StartupSettings.WindowModeOffset] = remembered;
        AppArgs args = option is null ? Args() : Args(option);
        Assert.Equal(expected, StartupSettings.From(new AppSettingsData()).DecideWindowMode(args, config, "0", linux).LogLine);
    }

    [Theory]
    [InlineData(null, null, true, "SteamDeckDefault", "window mode: fullscreen (Steam Deck default)")]
    [InlineData(null, false, false, "Remembered", "window mode: windowed (remembered)")]
    [InlineData(null, true, true, "Remembered", "window mode: fullscreen (remembered)")]
    [InlineData("--windowed", null, false, "CommandLine", "window mode: windowed (command line)")]
    [InlineData("--fullscreen", false, true, "CommandLine", "window mode: fullscreen (command line)")]
    [InlineData("--windowed", true, false, "CommandLine", "window mode: windowed (command line)")]
    public void OnTheSteamDeckTheCommandLineThenTheDisplayModeChoiceThenFullscreen(string? option, bool? displayMode, bool fullscreen, string source, string log)
    {
        AppArgs args = option is null ? Args() : Args(option);
        foreach (byte remembered in new byte[] { 0, 1 })
        {
            ConfigBlock config = ConfigBlock.CreateBootDefaults();
            config.Bytes[StartupSettings.WindowModeOffset] = remembered;      // the Deck does not read the byte
            StartWindowMode mode = StartupSettings.From(new AppSettingsData(RememberedFullscreen: displayMode)).DecideWindowMode(args, config, "1", linux: true);
            Assert.Equal(new StartWindowMode(fullscreen, Enum.Parse<StartWindowSource>(source), SteamDeck: true), mode);
            Assert.Equal(log, mode.LogLine);
            Assert.Equal(fullscreen, mode.FullscreenAtShow);                    // asked for when the window is shown ...
            Assert.False(mode.FullscreenAfterSetUp);                            // ... and the byte does not switch it later
        }
    }

    [Theory]
    [InlineData("--windowed", true, false)]
    [InlineData("--fullscreen", false, true)]
    public void OnTheSteamDeckTheCommandLineBeatsTheRememberedChoice(string option, bool remembered, bool expected)
    {
        StartupSettings startup = StartupSettings.From(new AppSettingsData(RememberedFullscreen: remembered));
        Assert.Equal(expected, startup.DecideWindowMode(Args(option), ConfigBlock.CreateBootDefaults(), "1", linux: true).Fullscreen);
    }

    [Fact]
    public void TheSteamDeckDefaultIsLinuxOnly()
    {
        ConfigBlock config = ConfigBlock.CreateBootDefaults();
        StartupSettings startup = StartupSettings.From(new AppSettingsData());
        Assert.Equal(new StartWindowMode(false, StartWindowSource.Remembered, SteamDeck: false), startup.DecideWindowMode(Args(), config, "1", linux: false));
        Assert.Equal(new StartWindowMode(false, StartWindowSource.Remembered, SteamDeck: false), startup.DecideWindowMode(Args(), config, "0", linux: true));
        Assert.Equal(new StartWindowMode(true, StartWindowSource.SteamDeckDefault, SteamDeck: true), startup.DecideWindowMode(Args(), config, "1", linux: true));
    }

    private sealed class DeckWindow : IDisplayWindow
    {
        public bool Fullscreen { get; set; }

        public (int Width, int Height) Size => (1280, 800);

        public bool Maximized => false;

        public (int Width, int Height) WorkArea => (1280, 800);

        public bool SetFullscreen(bool fullscreen)
        {
            Fullscreen = fullscreen;
            return true;
        }

        public (int Width, int Height) Resize(int width, int height) => (width, height);
    }

    // one Steam Deck start as GameApp makes it: the decision from the save folder and save.dat, the command line applied to the config block, save.dat written
    private StartWindowMode DeckStart(Action<DisplaySettings, SaveStore>? play = null)
    {
        AppArgs args = Args("--save-dir", _dir);
        string savePath = Path.Combine(_dir, "save.dat");
        SaveStore? saved = SaveFile.ReadWithFallback(savePath, out _);
        SaveStore store = saved ?? new SaveStore();
        if (saved is null)
        {
            store.SystemData.Config.CopyFrom(ConfigBlock.CreateBootDefaults());
        }

        StartupSettings startup = StartupSettings.Load(args);
        StartWindowMode mode = startup.DecideWindowMode(args, store.SystemData.Config, "1", linux: true);
        Assert.False(startup.ApplyToConfig(store.SystemData.Config, false, args.WindowModeChoice));
        var window = new DeckWindow { Fullscreen = mode.FullscreenAtShow };
        play?.Invoke(new DisplaySettings(window, store.SystemData.Config, () => { }, startup.Path, window.Size, linux: true), store);
        SaveFile.WriteAtomic(savePath, store);
        return mode;
    }

    [Fact]
    public void SteamDeckTwoStartsWithoutAChoiceAreBothFullscreenAndAWindowedChoiceIsKeptByTheNext()
    {
        for (int start = 0; start < 2; start++)
        {
            StartWindowMode mode = DeckStart();
            Assert.Equal(new StartWindowMode(true, StartWindowSource.SteamDeckDefault, SteamDeck: true), mode);
            Assert.Equal("window mode: fullscreen (Steam Deck default)", mode.LogLine);
            Assert.True(mode.FullscreenAtShow);
            Assert.False(File.Exists(SettingsPath));                         // the default writes nothing
        }

        Assert.Equal(0, SaveTools.Summarize(Path.Combine(_dir, "save.dat")).WindowMode);

        // Config > Display Mode > Windowed: the byte is already 0, the choice is display_mode=windowed
        DeckStart((display, _) => display.Fullscreen = false);
        Assert.Equal(false, AppSettings.Load(SettingsPath).RememberedFullscreen);

        StartWindowMode next = DeckStart();
        Assert.Equal(new StartWindowMode(false, StartWindowSource.Remembered, SteamDeck: true), next);
        Assert.Equal("window mode: windowed (remembered)", next.LogLine);
        Assert.False(next.FullscreenAtShow);
        Assert.False(next.FullscreenAfterSetUp);

        // back to Fullscreen on the row, then F11 to a window while the byte is left at 1 (as a desktop start leaves it): the next start follows the choice
        DeckStart((display, _) => display.Fullscreen = true);
        Assert.True(DeckStart().FullscreenAtShow);
        DeckStart((display, store) =>
        {
            display.NoteFullscreen(false);
            store.SystemData.Config.Bytes[StartupSettings.WindowModeOffset] = 1;
        });
        StartWindowMode windowed = DeckStart();
        Assert.Equal(1, SaveTools.Summarize(Path.Combine(_dir, "save.dat")).WindowMode);
        Assert.Equal(new StartWindowMode(false, StartWindowSource.Remembered, SteamDeck: true), windowed);
        Assert.False(windowed.FullscreenAfterSetUp);
    }

    [Fact]
    public void WindowedBeatsARememberedFullscreenAndFullscreenBeatsARememberedWindow()
    {
        ConfigBlock config = ConfigBlock.CreateBootDefaults();
        config.Bytes[StartupSettings.WindowModeOffset] = 1;                       // Alt+Enter / F11 left the game fullscreen last time
        StartupSettings startup = StartupSettings.From(new AppSettingsData());
        Assert.True(startup.ApplyToConfig(config, false, Args("--windowed").WindowModeChoice));
        Assert.Equal(0, config.Bytes[StartupSettings.WindowModeOffset]);          // this run and the next one are windowed
        Assert.False(startup.DecideWindowMode(Args("--windowed"), config, "0", linux: false).FullscreenAtShow);   // and the app asks for no fullscreen after placing the window

        Assert.True(startup.ApplyToConfig(config, false, Args("--fullscreen").WindowModeChoice));
        Assert.Equal(1, config.Bytes[StartupSettings.WindowModeOffset]);
        Assert.True(startup.DecideWindowMode(Args("--fullscreen"), config, "0", linux: false).FullscreenAtShow);
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

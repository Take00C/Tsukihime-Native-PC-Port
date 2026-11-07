using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Platform;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package K19: the display settings behind the config screen's Display Mode and Window Size rows, over a fake window: the mode goes to the window and to the
/// config block byte the launcher reads, Alt+Enter is remembered the same way, a size preset leaves fullscreen and lands in settings.ini next to the other keys,
/// a dragged size is remembered at exit.
/// </summary>
public sealed class DisplaySettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-k19-display-" + Guid.NewGuid().ToString("N"));

    public DisplaySettingsTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    private string SettingsPath => Path.Combine(_dir, AppSettings.FileName);

    private sealed class FakeWindow : IDisplayWindow
    {
        private (int Width, int Height) _windowed = (1600, 900);

        public List<string> Calls { get; } = [];

        public bool Refuse { get; set; }

        public bool Fullscreen { get; set; }

        public (int Width, int Height) Size { get; set; } = (1600, 900);

        public bool Maximized { get; set; }

        public (int Width, int Height) WorkArea { get; set; } = (2560, 1392);

        public bool SetFullscreen(bool fullscreen)
        {
            Calls.Add("fullscreen " + fullscreen);
            if (Refuse)
            {
                return false;
            }

            Fullscreen = fullscreen;
            Size = fullscreen ? (2560, 1440) : _windowed;
            return true;
        }

        public (int Width, int Height) Resize(int width, int height)
        {
            Calls.Add($"resize {width}x{height}");
            _windowed = WindowFit.Clamp(width, height, WorkArea.Width, WorkArea.Height);
            Size = _windowed;
            return _windowed;
        }
    }

    // linux and macOS: null is the platform the tests run on, as in the app
    private (DisplaySettings Settings, FakeWindow Window, ConfigBlock Config, List<string> Log, Counter Commits) Make(string? path, (int, int)? placed = null, bool? linux = null, bool? macOS = null)
    {
        var window = new FakeWindow();
        ConfigBlock config = ConfigBlock.CreateBootDefaults();
        var log = new List<string>();
        var commits = new Counter();
        var settings = new DisplaySettings(window, config, () => commits.Count++, path, placed ?? (1600, 900), log.Add, linux, macOS);
        return (settings, window, config, log, commits);
    }

    private sealed class Counter
    {
        public int Count { get; set; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheRowSwitchesTheWindowAndRemembersTheModeInTheConfigByteOnce(bool linux)
    {
        (DisplaySettings s, FakeWindow w, ConfigBlock c, List<string> log, Counter commits) = Make(SettingsPath, linux: linux);
        Assert.False(s.Fullscreen);
        Assert.False(s.Remembered);

        s.Fullscreen = true;
        Assert.True(w.Fullscreen);
        Assert.Equal(["fullscreen True"], w.Calls);
        Assert.Equal(1, c.Bytes[AppSettings.WindowModeOffset]);
        Assert.Equal(1, commits.Count);
        Assert.Equal(["window mode: fullscreen (remembered)"], log);
        Assert.Equal(linux ? true : null, AppSettings.Load(SettingsPath).RememberedFullscreen);      // display_mode= is written on Linux only

        s.Fullscreen = true;                                            // the same choice again: nothing to do, nothing to save
        Assert.Single(w.Calls);
        Assert.Equal(1, commits.Count);
        Assert.Single(log);

        s.Fullscreen = false;
        Assert.Equal(0, c.Bytes[AppSettings.WindowModeOffset]);
        Assert.Equal(2, commits.Count);
        Assert.Equal(2, log.Count);
        Assert.Equal(linux ? false : null, AppSettings.Load(SettingsPath).RememberedFullscreen);
        Assert.Equal((1600, 900), s.WindowSize);                        // the windowed size the run started with
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChoosingTheModeTheWindowAlreadyShowsIsRecordedOnLinuxOnly(bool linux)
    {
        // a Steam Deck start with --windowed in Launch Options: the window is windowed, nothing was chosen yet; the player picks Windowed to keep it
        (DisplaySettings s, FakeWindow w, _, List<string> log, Counter commits) = Make(SettingsPath, linux: linux);
        s.Fullscreen = false;
        Assert.Empty(w.Calls);                                          // the window is not asked again
        Assert.Equal(0, commits.Count);                                 // the byte was 0 already
        Assert.Equal(linux ? false : null, AppSettings.Load(SettingsPath).RememberedFullscreen);
        Assert.Equal(linux ? ["window mode: windowed (remembered)"] : [], log);

        // the Steam Deck default (fullscreen with the byte at 0), and the player picks Fullscreen: on Linux that is the choice, byte and display_mode
        (DisplaySettings d, FakeWindow dw, ConfigBlock dc, List<string> dlog, Counter dcommits) = Make(Path.Combine(_dir, "deck.ini"), linux: linux);
        dw.Fullscreen = true;
        d.Fullscreen = true;
        Assert.Empty(dw.Calls);
        Assert.Equal(linux ? 1 : 0, dc.Bytes[AppSettings.WindowModeOffset]);
        Assert.Equal(linux ? 1 : 0, dcommits.Count);
        Assert.Equal(linux ? true : null, AppSettings.Load(Path.Combine(_dir, "deck.ini")).RememberedFullscreen);
        Assert.Equal(linux ? ["window mode: fullscreen (remembered)"] : [], dlog);
    }

    [Fact]
    public void AnExplicitWindowedChoiceIsWrittenOnLinuxEvenWhenTheConfigByteWasAlreadyZero()
    {
        (DisplaySettings s, FakeWindow w, ConfigBlock c, List<string> log, Counter commits) = Make(SettingsPath, linux: true);
        w.SetFullscreen(true);                                          // the Steam Deck default: fullscreen with the byte at 0
        s.NoteFullscreen(false);
        Assert.Equal(0, c.Bytes[AppSettings.WindowModeOffset]);
        Assert.Equal(0, commits.Count);
        Assert.False(AppSettings.Load(SettingsPath).RememberedFullscreen);
        Assert.Equal(["window mode: windowed (remembered)"], log);
    }

    [Fact]
    public void WindowsRemembersOnlyTheByteAndLogsOnlyAChangeAsV016Did()
    {
        (DisplaySettings s, FakeWindow w, ConfigBlock c, List<string> log, Counter commits) = Make(SettingsPath, linux: false);
        w.SetFullscreen(true);
        s.NoteFullscreen(false);                                        // the byte is already 0: nothing to save, nothing to say
        Assert.Empty(log);
        Assert.Equal(0, commits.Count);

        s.NoteFullscreen(true);
        s.NoteFullscreen(true);
        Assert.Equal(["window mode: fullscreen (remembered)"], log);
        Assert.Equal(1, commits.Count);
        Assert.Equal(1, c.Bytes[AppSettings.WindowModeOffset]);
        Assert.False(File.Exists(SettingsPath));                        // no display_mode=: settings.ini is not even created
    }

    [Fact]
    public void ThePlatformDecidesWhetherTheDisplayModeKeyIsWritten()
    {
        (DisplaySettings s, _, _, _, _) = Make(SettingsPath);           // what the app builds: no platform given
        s.NoteFullscreen(true);
        Assert.Equal(OperatingSystem.IsLinux() ? true : null, AppSettings.Load(SettingsPath).RememberedFullscreen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AltEnterIsRememberedTheSameWayAndTheRowShowsIt(bool linux)
    {
        (DisplaySettings s, FakeWindow w, ConfigBlock c, List<string> log, Counter commits) = Make(SettingsPath, linux: linux);
        w.SetFullscreen(true);                                          // the event pump toggled the window on Alt+Enter
        s.NoteFullscreen(true);                                         // the app passes the FullscreenToggled event on
        Assert.True(s.Fullscreen);                                      // the row reads the window, so it is in sync
        Assert.Equal(1, c.Bytes[AppSettings.WindowModeOffset]);
        Assert.Equal(1, commits.Count);
        s.NoteFullscreen(true);
        Assert.Equal(1, commits.Count);
        Assert.Single(log);

        w.SetFullscreen(false);
        s.NoteFullscreen(false);
        Assert.False(s.Fullscreen);
        Assert.Equal(0, c.Bytes[AppSettings.WindowModeOffset]);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void OnMacOSTheGreenButtonIsRememberedAsF11Is()
    {
        (DisplaySettings s, FakeWindow w, ConfigBlock c, List<string> log, Counter commits) = Make(SettingsPath, linux: false, macOS: true);
        w.Fullscreen = true;                                            // the green button: the system switched the window, the app asked for nothing
        s.NoteFullscreenEvent(new PlatformEvent(PlatformEventKind.EnteredFullscreen, Fullscreen: true));
        Assert.Equal(1, c.Bytes[AppSettings.WindowModeOffset]);
        Assert.Equal(1, commits.Count);
        Assert.Equal(["window mode: fullscreen (remembered)"], log);

        w.Fullscreen = false;
        s.NoteFullscreenEvent(new PlatformEvent(PlatformEventKind.LeftFullscreen));
        Assert.Equal(0, c.Bytes[AppSettings.WindowModeOffset]);
        Assert.Equal(2, commits.Count);
        Assert.Equal(2, log.Count);

        // F11: the pump switched the window and the app passed FullscreenToggled on; the SDL event that follows says the same and adds nothing
        w.SetFullscreen(true);
        s.NoteFullscreen(true);
        s.NoteFullscreenEvent(new PlatformEvent(PlatformEventKind.EnteredFullscreen, Fullscreen: true));
        Assert.Equal(3, commits.Count);
        Assert.Equal(3, log.Count);
        s.NoteFullscreenEvent(new PlatformEvent(PlatformEventKind.FocusLost));
        Assert.Equal(1, c.Bytes[AppSettings.WindowModeOffset]);
        Assert.Equal(3, log.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WindowsAndLinuxIgnoreTheFullscreenEventsAsV016Did(bool linux)
    {
        (DisplaySettings s, _, ConfigBlock c, List<string> log, Counter commits) = Make(SettingsPath, linux: linux, macOS: false);
        s.NoteFullscreenEvent(new PlatformEvent(PlatformEventKind.EnteredFullscreen, Fullscreen: true));
        c.Bytes[AppSettings.WindowModeOffset] = 1;
        s.NoteFullscreenEvent(new PlatformEvent(PlatformEventKind.LeftFullscreen));
        Assert.Equal(1, c.Bytes[AppSettings.WindowModeOffset]);
        Assert.Equal(0, commits.Count);
        Assert.Empty(log);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void OnlyMacOSRemembersTheFullscreenEventsWhenNoPlatformIsGiven()
    {
        (DisplaySettings s, _, ConfigBlock c, _, _) = Make(null);       // what the app builds
        s.NoteFullscreenEvent(new PlatformEvent(PlatformEventKind.EnteredFullscreen, Fullscreen: true));
        Assert.Equal(OperatingSystem.IsMacOS() ? 1 : 0, c.Bytes[AppSettings.WindowModeOffset]);
    }

    [Fact]
    public void ARefusedRequestChangesNothing()
    {
        (DisplaySettings s, FakeWindow w, ConfigBlock c, List<string> log, Counter commits) = Make(SettingsPath);
        w.Refuse = true;
        s.Fullscreen = true;
        Assert.False(s.Fullscreen);
        Assert.Equal(0, c.Bytes[AppSettings.WindowModeOffset]);
        Assert.Equal(0, commits.Count);
        Assert.Contains(log, l => l.Contains("refused", StringComparison.Ordinal));
    }

    [Fact]
    public void ASizePresetLeavesFullscreenResizesTheWindowAndWritesTheFileNextToTheOtherKeys()
    {
        AppSettings.Save(SettingsPath, new AppSettingsData(PromptSetting.Xbox, VSync: false, WindowWidth: 1600, WindowHeight: 900));
        (DisplaySettings s, FakeWindow w, ConfigBlock c, _, _) = Make(SettingsPath);
        s.Fullscreen = true;
        w.Calls.Clear();

        s.WindowSize = (1920, 1080);
        Assert.Equal(["fullscreen False", "resize 1920x1080"], w.Calls);
        Assert.False(s.Fullscreen);
        Assert.Equal(0, c.Bytes[AppSettings.WindowModeOffset]);
        Assert.Equal((1920, 1080), s.WindowSize);
        AppSettingsData file = AppSettings.Load(SettingsPath);
        Assert.Equal((1920, 1080), file.WindowSize);
        Assert.Equal(PromptSetting.Xbox, file.Prompts);
        Assert.Equal(false, file.VSync);

        // a preset too big for the monitor is clamped by the window, and the clamped size is what is remembered
        w.WorkArea = (1920, 1040);
        s.WindowSize = (2560, 1440);
        Assert.Equal(WindowFit.Clamp(2560, 1440, 1920, 1040), s.WindowSize);
        Assert.Equal(s.WindowSize, AppSettings.Load(SettingsPath).WindowSize);
    }

    [Fact]
    public void ChoosingTheSizeTheRunStartedWithStillWritesTheKey()
    {
        // the file says 1920 x 1080 but the window came up at 1600 x 900 (a hidden run, or a smaller monitor): the row's explicit choice of 1600 x 900 lands in the file
        AppSettings.Save(SettingsPath, new AppSettingsData(WindowWidth: 1920, WindowHeight: 1080));
        (DisplaySettings s, _, _, List<string> log, _) = Make(SettingsPath, (1600, 900));
        s.WindowSize = (1600, 900);
        Assert.Equal((1600, 900), AppSettings.Load(SettingsPath).WindowSize);
        Assert.Contains("window size 1600 x 900 saved to settings.ini", log);
        Assert.False(s.SaveWindowSize());                                           // and the exit write has nothing more to do
    }

    [Fact]
    public void ADraggedSizeIsRememberedAtExitAndNotWhileFullscreenOrMaximized()
    {
        AppSettings.Save(SettingsPath, new AppSettingsData(WindowWidth: 1600, WindowHeight: 900));
        (DisplaySettings s, FakeWindow w, _, _, _) = Make(SettingsPath);
        w.Size = (1500, 844);
        s.NoteResized();
        Assert.Equal((1500, 844), s.WindowSize);
        Assert.Equal((1600, 900), AppSettings.Load(SettingsPath).WindowSize);       // not yet: a drag fires many size events

        Assert.True(s.SaveWindowSize());
        Assert.Equal((1500, 844), AppSettings.Load(SettingsPath).WindowSize);
        Assert.False(s.SaveWindowSize());                                           // nothing new

        w.SetFullscreen(true);
        s.NoteResized();                                                            // the fullscreen size is not a windowed size
        Assert.Equal((1500, 844), s.WindowSize);
        w.SetFullscreen(false);
        w.Maximized = true;
        w.Size = (2544, 1361);
        s.NoteResized();
        Assert.Equal((1500, 844), s.WindowSize);
        w.Maximized = false;
        w.Size = (300, 200);
        s.NoteResized();                                                            // below the minimum
        Assert.Equal((1500, 844), s.WindowSize);
    }

    [Fact]
    public void AnUntouchedSizeIsNotWrittenBackEvenWhenTheFileAskedForMore()
    {
        // settings.ini says 2560 x 1440, the window was placed at the clamped 2224 x 1251: the file keeps the user's value until the user changes the size
        AppSettings.Save(SettingsPath, new AppSettingsData(WindowWidth: 2560, WindowHeight: 1440));
        (DisplaySettings s, _, _, _, _) = Make(SettingsPath, (2224, 1251));
        Assert.Equal((2224, 1251), s.WindowSize);
        Assert.False(s.SaveWindowSize());
        Assert.Equal((2560, 1440), AppSettings.Load(SettingsPath).WindowSize);
    }

    [Fact]
    public void WithNoSettingsFileNothingIsWrittenAndTheWindowStillChanges()
    {
        (DisplaySettings s, FakeWindow w, _, _, _) = Make(null);
        s.WindowSize = (1280, 720);
        Assert.Equal((1280, 720), s.WindowSize);
        Assert.Contains("resize 1280x720", w.Calls);
        Assert.False(s.SaveWindowSize());
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void TheSizeChoicesAreThePresetsThatFitTheMonitorTheWindowIsOn()
    {
        (DisplaySettings s, FakeWindow w, _, _, _) = Make(null);
        Assert.Equal([(1280, 720), (1600, 900), (1920, 1080)], s.SizeChoices);
        w.WorkArea = (3840, 2100);
        Assert.Equal(4, s.SizeChoices.Count);
        w.WorkArea = (1366, 728);
        Assert.Empty(s.SizeChoices);
    }
}

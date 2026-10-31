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

    private (DisplaySettings Settings, FakeWindow Window, ConfigBlock Config, List<string> Log, Counter Commits) Make(string? path, (int, int)? placed = null)
    {
        var window = new FakeWindow();
        ConfigBlock config = ConfigBlock.CreateBootDefaults();
        var log = new List<string>();
        var commits = new Counter();
        var settings = new DisplaySettings(window, config, () => commits.Count++, path, placed ?? (1600, 900), log.Add);
        return (settings, window, config, log, commits);
    }

    private sealed class Counter
    {
        public int Count { get; set; }
    }

    [Fact]
    public void TheRowSwitchesTheWindowAndRemembersTheModeInTheConfigByteOnce()
    {
        (DisplaySettings s, FakeWindow w, ConfigBlock c, List<string> log, Counter commits) = Make(SettingsPath);
        Assert.False(s.Fullscreen);
        Assert.False(s.Remembered);

        s.Fullscreen = true;
        Assert.True(w.Fullscreen);
        Assert.Equal(["fullscreen True"], w.Calls);
        Assert.Equal(1, c.Bytes[AppSettings.WindowModeOffset]);
        Assert.Equal(1, commits.Count);
        Assert.Contains("window mode: fullscreen (remembered)", log);

        s.Fullscreen = true;                                            // the same choice again: nothing to do, nothing to save
        Assert.Single(w.Calls);
        Assert.Equal(1, commits.Count);

        s.Fullscreen = false;
        Assert.Equal(0, c.Bytes[AppSettings.WindowModeOffset]);
        Assert.Equal(2, commits.Count);
        Assert.Equal((1600, 900), s.WindowSize);                        // the windowed size the run started with
    }

    [Fact]
    public void AltEnterIsRememberedTheSameWayAndTheRowShowsIt()
    {
        (DisplaySettings s, FakeWindow w, ConfigBlock c, _, Counter commits) = Make(SettingsPath);
        w.SetFullscreen(true);                                          // the event pump toggled the window on Alt+Enter
        s.NoteFullscreen(true);                                         // the app passes the FullscreenToggled event on
        Assert.True(s.Fullscreen);                                      // the row reads the window, so it is in sync
        Assert.Equal(1, c.Bytes[AppSettings.WindowModeOffset]);
        Assert.Equal(1, commits.Count);
        s.NoteFullscreen(true);
        Assert.Equal(1, commits.Count);

        w.SetFullscreen(false);
        s.NoteFullscreen(false);
        Assert.False(s.Fullscreen);
        Assert.Equal(0, c.Bytes[AppSettings.WindowModeOffset]);
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

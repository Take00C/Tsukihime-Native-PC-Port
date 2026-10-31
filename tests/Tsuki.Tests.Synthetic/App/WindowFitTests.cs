using Tsuki.App.Services;
using Tsuki.Platform;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package K19: the arithmetic that keeps the game's window inside the monitor's work area. The user's report: with Fullscreen off the game still filled the
/// screen; settings.ini held window=2560x1440 (the launcher's largest preset) for a 2560 x 1440 monitor, so the client area covered the monitor and the title bar
/// sat above its top edge.
/// </summary>
public sealed class WindowFitTests
{
    [Fact]
    public void TheUsersWindowIsMadeToFitTheWorkAreaOfTheUsersMonitor()
    {
        // the real numbers: a 2560 x 1440 monitor whose work area is 2560 x 1392 (the task bar), settings.ini asking for window=2560x1440
        Assert.Equal(2304, WindowFit.MaxWidth(2560));
        Assert.Equal(1252, WindowFit.MaxHeight(1392));
        Assert.False(WindowFit.Fits(2560, 1440, 2560, 1392));
        (int w, int h) = WindowFit.Clamp(2560, 1440, 2560, 1392);
        Assert.Equal((2224, 1251), (w, h));
        Assert.True(w <= 2304 && h <= 1252);
        Assert.InRange(w / (double)h, 16.0 / 9.0 - 0.01, 16.0 / 9.0 + 0.01);         // still 16:9
        Assert.Equal((2224, 1251), WindowFit.Largest(2560, 1392));
        Assert.Equal((1600, 900), WindowFit.Default(2560, 1392));
        Assert.Equal([(1280, 720), (1600, 900), (1920, 1080)], WindowFit.PresetsFor(2560, 1392));      // 2560 x 1440 is not offered on this monitor
        Assert.Equal((168, 70), WindowFit.Centre(0, 0, 2560, 1392, 2224, 1251));                     // the title bar has 70 px above the client area
    }

    [Theory]
    [InlineData(1920, 1080, 2560, 1392, 1920, 1080, false)]          // fits: untouched
    [InlineData(1600, 900, 2560, 1392, 1600, 900, false)]
    [InlineData(1600, 900, 1366, 728, 1164, 655, true)]              // a laptop: 90 % of 1366 x 728 is 1229 x 655; the height limits, 16:9 kept, even width
    [InlineData(3840, 2160, 3840, 2100, 3360, 1890, true)]           // a 4K monitor asked for its own size
    [InlineData(2000, 2000, 2560, 1392, 1252, 1252, true)]           // a square window the player dragged keeps its own shape
    [InlineData(100, 100, 2560, 1392, 100, 100, false)]              // small sizes are not grown
    public void ASizeIsScaledDownOnlyWhenItDoesNotFitAndKeepsItsShape(int w, int h, int workW, int workH, int ew, int eh, bool clamped)
    {
        Assert.Equal((ew, eh), WindowFit.Clamp(w, h, workW, workH));
        Assert.Equal(!clamped, WindowFit.Fits(w, h, workW, workH));
        (int cw, int ch) = WindowFit.Clamp(w, h, workW, workH);
        Assert.True(WindowFit.Fits(cw, ch, workW, workH));
    }

    [Fact]
    public void TheDefaultIs1600x900WhereItFitsAndTheLargest16By9SizeElsewhere()
    {
        Assert.Equal((1600, 900), WindowFit.Default(1920, 1040));
        Assert.Equal((1600, 900), WindowFit.Default(3840, 2100));
        Assert.Equal((1164, 655), WindowFit.Default(1366, 728));
        Assert.Equal(WindowFit.Largest(1366, 728), WindowFit.Default(1366, 728));
        Assert.Empty(WindowFit.PresetsFor(1366, 728));                                              // no preset fits a small laptop: the size row is empty there
        Assert.Equal(4, WindowFit.PresetsFor(3840, 2100).Count);
        Assert.Equal(4, WindowFit.Presets.Count);
    }

    [Fact]
    public void TheCentreNeverPutsTheClientAreaAboveOrLeftOfTheWorkArea()
    {
        Assert.Equal((1920 + 160, 0 + 70), WindowFit.Centre(1920, 0, 1920, 1040, 1600, 900));
        Assert.Equal((-2560, 0), WindowFit.Centre(-2560, 0, 2560, 1392, 3000, 3000));
    }

    [Fact]
    public void TheMinimumSizeIsTheOneTheSettingsFileAccepts()
    {
        Assert.Equal(AppSettingsData.MinWindowWidth, WindowFit.MinWidth);
        Assert.Equal(AppSettingsData.MinWindowHeight, WindowFit.MinHeight);
        Assert.Equal("1600 x 900", WindowFit.Name(1600, 900));
    }
}

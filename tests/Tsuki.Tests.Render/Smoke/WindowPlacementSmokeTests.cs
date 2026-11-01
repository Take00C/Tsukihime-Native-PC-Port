using Tsuki.Platform;

namespace Tsuki.Tests.Render.Smoke;

/// <summary>
/// Package K17: the launcher's monitor and window-size settings reach a real (hidden) SDL window. Package K19: the size is clamped to the display's work area and
/// the window is centred in it (the user's 2560 x 1440 window on a 2560 x 1440 monitor looked like fullscreen).
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class WindowPlacementSmokeTests
{
    [Fact]
    public void DisplaysAreListedWithTheirWorkAreasAndASizeThatFitsIsAppliedAndCentred()
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("Tsuki.Tests.Render placement", 320, 180, Hidden: true, Resizable: true));
        IReadOnlyList<DisplayInfo> displays = WindowPlacement.Displays();
        Assert.NotEmpty(displays);
        Assert.All(displays, d =>
        {
            Assert.True(d.Width > 0 && d.Height > 0);
            Assert.NotNull(d.Name);
            Assert.True(d.WorkWidth > 0 && d.WorkWidth <= d.Width, $"{d}: work width");
            Assert.True(d.WorkHeight > 0 && d.WorkHeight <= d.Height, $"{d}: work height");
            Assert.True(d.WorkX >= d.X && d.WorkY >= d.Y, $"{d}: work corner");
        });

        DisplayInfo first = displays[0];
        Placement placed = WindowPlacement.Place(window, first.X, first.Y, 800, 450);
        Assert.Equal(first, placed.Display);
        Assert.True(placed.MonitorMatched);
        Assert.False(placed.Clamped);
        Assert.Equal((800, 450), (placed.Width, placed.Height));
        Assert.Equal((800, 450), window.WindowSize);
        window.Sync();
        AssertPixelSize(window, 800, 450);
        Assert.InRange(placed.X, first.WorkX, first.WorkX + first.WorkWidth - 800);
        Assert.InRange(placed.Y, first.WorkY, first.WorkY + first.WorkHeight - 450);
        Assert.Equal(WindowFit.Centre(first.WorkX, first.WorkY, first.WorkWidth, first.WorkHeight, 800, 450), (placed.X, placed.Y));
    }

    [Fact]
    public void AMonitorThatIsNotAttachedUsesTheWindowsOwnDisplayAndStillTakesTheSize()
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("Tsuki.Tests.Render placement 2", 320, 180, Hidden: true, Resizable: true));
        Placement placed = WindowPlacement.Place(window, 123_456, 654_321, 640, 360);
        Assert.False(placed.MonitorMatched);
        Assert.Equal(WindowPlacement.DisplayOf(window), placed.Display);
        window.Sync();
        AssertPixelSize(window, 640, 360);
        Placement again = WindowPlacement.Place(window, null, null, null, null);          // no size asked: the window's own, which fits
        Assert.Equal((640, 360), (again.Width, again.Height));
        Assert.False(again.Clamped);
        window.Sync();
        AssertPixelSize(window, 640, 360);
    }

    [Fact]
    public void ASizeAsBigAsTheMonitorIsClampedToItsWorkAreaAndCentredInIt()
    {
        // the user's case: settings.ini asked for the monitor's own size, and the window covered the monitor with its title bar above the top edge
        using GameWindow window = GameWindow.Create(new WindowOptions("Tsuki.Tests.Render placement 3", 320, 180, Hidden: true, Resizable: true));
        DisplayInfo d = WindowPlacement.DisplayOf(window);
        Placement placed = WindowPlacement.Place(window, null, null, d.Width, d.Height);
        Assert.True(placed.Clamped);
        Assert.True(placed.Width <= WindowFit.MaxWidth(d.WorkWidth), $"{placed}");
        Assert.True(placed.Height <= WindowFit.MaxHeight(d.WorkHeight), $"{placed}");
        Assert.Equal(WindowFit.Clamp(d.Width, d.Height, d.WorkWidth, d.WorkHeight), (placed.Width, placed.Height));
        Assert.True(placed.X >= d.WorkX && placed.Y > d.WorkY, $"{placed}: room for the title bar above the client area");
        Assert.True(placed.X + placed.Width <= d.WorkX + d.WorkWidth && placed.Y + placed.Height <= d.WorkY + d.WorkHeight, $"{placed}: inside the work area");
        window.Sync();
        Assert.Equal((placed.Width, placed.Height), window.WindowSize);
        AssertPixelSize(window, placed.Width, placed.Height);

        Placement small = WindowPlacement.Resize(window, 640, 360);
        Assert.Equal((640, 360), (small.Width, small.Height));
        Assert.False(small.Clamped);
        window.Sync();
        Assert.Equal((640, 360), window.WindowSize);
    }

    private static unsafe void AssertPixelSize(GameWindow window, int width, int height)
    {
        Assert.Equal((width, height), window.WindowSize);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal((width, height), window.PixelSize);
            return;
        }

        float density = SDL.SDL3.SDL_GetWindowPixelDensity(window.Handle);
        Assert.True(density > 0, "SDL did not report the window pixel density.");
        (int pw, int ph) = window.PixelSize;
        Assert.InRange(Math.Abs(pw - width * density), 0, 1);
        Assert.InRange(Math.Abs(ph - height * density), 0, 1);
    }
}

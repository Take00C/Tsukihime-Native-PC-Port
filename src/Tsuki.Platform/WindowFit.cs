// The size of the game window when it is not fullscreen (package K19): the presets, the share of the monitor a window may take, and the clamp that keeps a
// window inside the monitor's work area with its title bar on the screen. Pure arithmetic, no SDL: the same file is compiled into TsukiLauncher.exe (a file link,
// like AppSettings.cs), so the launcher's Display page offers the presets the game accepts and its status line shows the size the game will use.
namespace Tsuki.Platform;

/// <summary>Window sizes: presets, the clamp to a monitor's work area, the default.</summary>
public static class WindowFit
{
    /// <summary>The windowed size of a profile that has none: 1600 x 900, or the largest 16:9 size that fits a smaller monitor.</summary>
    public const int DefaultWidth = 1600;

    /// <summary>The height that goes with <see cref="DefaultWidth"/>.</summary>
    public const int DefaultHeight = 900;

    /// <summary>The smallest width the window accepts (SDL's minimum size and the smallest <c>window=</c> value).</summary>
    public const int MinWidth = 640;

    /// <summary>The smallest height the window accepts.</summary>
    public const int MinHeight = 360;

    /// <summary>The largest share of the work area's width and of its height a window may take (90 %): room for the frame, the title bar and a gap to the task bar.</summary>
    public const double Share = 0.9;

    /// <summary>The window size presets, smallest first (the launcher's Display page and the config screen's Window Size row offer the ones that fit).</summary>
    public static IReadOnlyList<(int Width, int Height)> Presets { get; } = [(1280, 720), (1600, 900), (1920, 1080), (2560, 1440)];

    /// <summary>The largest width a window may have in a work area <paramref name="workWidth"/> wide.</summary>
    public static int MaxWidth(int workWidth) => Math.Max(2, (int)Math.Floor(workWidth * Share));

    /// <summary>The largest height a window may have in a work area <paramref name="workHeight"/> tall.</summary>
    public static int MaxHeight(int workHeight) => Math.Max(2, (int)Math.Floor(workHeight * Share));

    /// <summary>True when a <paramref name="width"/> x <paramref name="height"/> window stays inside the share of the work area.</summary>
    public static bool Fits(int width, int height, int workWidth, int workHeight) => width <= MaxWidth(workWidth) && height <= MaxHeight(workHeight);

    /// <summary>The largest 16:9 size that fits the work area (even width, so the height is whole).</summary>
    public static (int Width, int Height) Largest(int workWidth, int workHeight)
    {
        int w = Math.Min(MaxWidth(workWidth), (int)Math.Floor(MaxHeight(workHeight) * 16.0 / 9.0)) & ~1;
        w = Math.Max(2, w);
        return (w, Math.Max(1, (int)Math.Round(w * 9.0 / 16.0)));
    }

    /// <summary>
    /// A requested size made to fit: unchanged when it fits, else scaled down (its own aspect ratio kept, so a 16:9 request stays 16:9; even width) until both
    /// sides are inside the share of the work area. The user's report (package K19): a 2560 x 1440 window on a 2560 x 1440 monitor filled the screen with its
    /// title bar above the top edge and looked like fullscreen.
    /// </summary>
    public static (int Width, int Height) Clamp(int width, int height, int workWidth, int workHeight)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (Fits(width, height, workWidth, workHeight))
        {
            return (width, height);
        }

        double scale = Math.Min(MaxWidth(workWidth) / (double)width, MaxHeight(workHeight) / (double)height);
        int w = Math.Max(2, (int)Math.Floor(width * scale) & ~1);
        int h = Math.Max(1, (int)Math.Round(w * (double)height / width));
        return (w, h);
    }

    /// <summary>The windowed size for a profile that has none: <see cref="DefaultWidth"/> x <see cref="DefaultHeight"/> when it fits, else <see cref="Largest"/>.</summary>
    public static (int Width, int Height) Default(int workWidth, int workHeight) =>
        Fits(DefaultWidth, DefaultHeight, workWidth, workHeight) ? (DefaultWidth, DefaultHeight) : Largest(workWidth, workHeight);

    /// <summary>The presets that fit the work area, smallest first (empty on a very small screen).</summary>
    public static IReadOnlyList<(int Width, int Height)> PresetsFor(int workWidth, int workHeight) =>
        [.. Presets.Where(p => Fits(p.Width, p.Height, workWidth, workHeight))];

    /// <summary>The top-left corner of a <paramref name="width"/> x <paramref name="height"/> client area centred in the work area (never above or left of it).</summary>
    public static (int X, int Y) Centre(int workX, int workY, int workWidth, int workHeight, int width, int height) =>
        (workX + Math.Max(0, (workWidth - width) / 2), workY + Math.Max(0, (workHeight - height) / 2));

    /// <summary>"1600 x 900".</summary>
    public static string Name(int width, int height) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{width} x {height}");
}

// Which display a window opens on, and how big it is (package K17: the launcher's monitor and window-size settings; package K19: the size is clamped to the
// display's work area and the window is centred in it, so a window can never cover the whole monitor and look like fullscreen). Kept apart from GameWindow so
// that the window code itself stays small; it only uses the window's SDL handle.
using SDL;
using static SDL.SDL3;

namespace Tsuki.Platform;

/// <summary>One display as SDL reports it.</summary>
/// <param name="X">Left edge in virtual-desktop pixels.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Name">The display name.</param>
/// <param name="WorkX">Left edge of the work area (the desktop less the task bar).</param>
/// <param name="WorkY">Top edge of the work area.</param>
/// <param name="WorkWidth">Width of the work area.</param>
/// <param name="WorkHeight">Height of the work area.</param>
public readonly record struct DisplayInfo(int X, int Y, int Width, int Height, string Name, int WorkX, int WorkY, int WorkWidth, int WorkHeight);

/// <summary>What <see cref="WindowPlacement.Place"/> did.</summary>
/// <param name="Display">The display the window was placed on.</param>
/// <param name="MonitorMatched">The requested monitor corner named an attached display (false: none was requested, or none matched and the window's own display was used).</param>
/// <param name="Width">The client width applied.</param>
/// <param name="Height">The client height applied.</param>
/// <param name="X">The client area's left edge.</param>
/// <param name="Y">The client area's top edge.</param>
/// <param name="Clamped">The requested size did not fit the work area and was made smaller.</param>
public readonly record struct Placement(DisplayInfo Display, bool MonitorMatched, int Width, int Height, int X, int Y, bool Clamped)
{
    /// <inheritdoc/>
    public override string ToString() =>
        $"{Width} x {Height} at ({X}, {Y}) on \"{Display.Name}\" {Display.Width} x {Display.Height} (work area {Display.WorkWidth} x {Display.WorkHeight} at ({Display.WorkX}, {Display.WorkY})){(Clamped ? ", clamped to the work area" : string.Empty)}";
}

/// <summary>Display enumeration and window placement.</summary>
public static unsafe class WindowPlacement
{
    /// <summary>Every display, in SDL's order. Needs the video subsystem (a window exists).</summary>
    public static IReadOnlyList<DisplayInfo> Displays()
    {
        var list = new List<DisplayInfo>();
        using var ids = SDL_GetDisplays();
        if (ids == null)
        {
            return list;
        }

        for (int i = 0; i < ids.Count; i++)
        {
            if (Describe(ids[i]) is { } d)
            {
                list.Add(d);
            }
        }

        return list;
    }

    /// <summary>The display the window is on (the primary one's shape when SDL cannot say).</summary>
    public static DisplayInfo DisplayOf(GameWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return CurrentDisplay(window.Handle);
    }

    /// <summary>The client size of the window in window coordinates (what <c>window=</c> stores; on Windows these are pixels).</summary>
    public static (int Width, int Height) WindowSize(GameWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        int w, h;
        SDL_GetWindowSize(window.Handle, &w, &h);
        return (w, h);
    }

    /// <summary>
    /// Sizes and centres a window that is not fullscreen: on the display whose top-left corner is (<paramref name="monitorX"/>, <paramref name="monitorY"/>),
    /// or, with no monitor or one that matches no display, on the display it is on. The size is the one asked for, else the window's own; either is clamped by
    /// <see cref="WindowFit.Clamp"/> to the display's work area, and the client area is centred in that work area (package K19). The window's minimum size is set to
    /// <see cref="WindowFit.MinWidth"/> x <see cref="WindowFit.MinHeight"/>.
    /// </summary>
    public static Placement Place(GameWindow window, int? monitorX, int? monitorY, int? width, int? height)
    {
        ArgumentNullException.ThrowIfNull(window);
        SDL_Window* handle = window.Handle;
        DisplayInfo? target = null;
        if (monitorX is { } mx && monitorY is { } my)
        {
            foreach (DisplayInfo d in Displays())
            {
                if (d.X == mx && d.Y == my)
                {
                    target = d;
                    break;
                }
            }
        }

        DisplayInfo display = target ?? CurrentDisplay(handle);
        int cw, ch;
        SDL_GetWindowSize(handle, &cw, &ch);
        int rw = width ?? cw;
        int rh = height ?? ch;
        (int w, int h) = WindowFit.Clamp(rw, rh, display.WorkWidth, display.WorkHeight);
        SDL_SetWindowMinimumSize(handle, Math.Min(WindowFit.MinWidth, w), Math.Min(WindowFit.MinHeight, h));
        if (w != cw || h != ch)
        {
            SDL_SetWindowSize(handle, w, h);
        }

        (int x, int y) = WindowFit.Centre(display.WorkX, display.WorkY, display.WorkWidth, display.WorkHeight, w, h);
        SDL_SetWindowPosition(handle, x, y);
        SDL_SyncWindow(handle);
        return new Placement(display, target is not null, w, h, x, y, w != rw || h != rh);
    }

    /// <summary>Resizes a window that is not fullscreen to <paramref name="width"/> x <paramref name="height"/> (clamped) and centres it on the display it is on.</summary>
    public static Placement Resize(GameWindow window, int width, int height) => Place(window, null, null, width, height);

    private static DisplayInfo CurrentDisplay(SDL_Window* handle)
    {
        SDL_DisplayID id = SDL_GetDisplayForWindow(handle);
        if (id == 0)
        {
            id = SDL_GetPrimaryDisplay();
        }

        return (id != 0 ? Describe(id) : null) ?? new DisplayInfo(0, 0, 1920, 1080, string.Empty, 0, 0, 1920, 1080);
    }

    private static DisplayInfo? Describe(SDL_DisplayID id)
    {
        SDL_Rect bounds;
        if (!SDL_GetDisplayBounds(id, &bounds))
        {
            return null;
        }

        // the work area: the desktop less the task bar (SDL_GetDisplayUsableBounds); the whole display when SDL cannot say
        SDL_Rect work;
        if (!SDL_GetDisplayUsableBounds(id, &work) || work.w <= 0 || work.h <= 0)
        {
            work = bounds;
        }

        return new DisplayInfo(bounds.x, bounds.y, bounds.w, bounds.h, SDL_GetDisplayName(id) ?? string.Empty, work.x, work.y, work.w, work.h);
    }
}

// The SDL3 window and its OpenGL 3.3 core context (ARCHITECTURE 3.1, 6.2, 6.5; ROADMAP M0 scope 7).
// Lifted from spike S3 GlWindow (and S2 Player). Tsuki.Render builds its bindings from
// GetProcAddress, so neither library references the other.
using SDL;
using static SDL.SDL3;

namespace Tsuki.Platform;

/// <summary>How to create the window.</summary>
/// <param name="Title">Window title.</param>
/// <param name="Width">Client width in window coordinates.</param>
/// <param name="Height">Client height in window coordinates.</param>
/// <param name="Hidden">Create it hidden (headless GPU tests, the smoke check).</param>
/// <param name="Resizable">Let the user resize it.</param>
/// <param name="SwapInterval">1 = vsync at the monitor rate (ARCHITECTURE 6.2 default), 0 = off.</param>
/// <param name="Fullscreen">Start in borderless desktop fullscreen.</param>
/// <param name="IconResource">
/// The id of an icon group in the executable's resources that the window class takes as its icon (Windows; package K20): the one <c>ApplicationIcon</c> of the project
/// puts there, so the title bar and the taskbar pick the crisp 16 / 32 / 48 px pictures of the group instead of scaling one bitmap. Null: SDL's default. It only works for the first
/// window of the process (the class is registered once), and only when the process is the executable that holds the icon (not <c>dotnet.exe</c> or a test host); see <see cref="GameWindow.SetIcon"/> for the other way.
/// </param>
public sealed record WindowOptions(
    string Title = "Tsukihime",
    int Width = 1280,
    int Height = 720,
    bool Hidden = false,
    bool Resizable = true,
    int SwapInterval = 1,
    bool Fullscreen = false,
    int? IconResource = null);

/// <summary>The GL context SDL created, as SDL reports it.</summary>
/// <param name="Major">SDL_GL_CONTEXT_MAJOR_VERSION.</param>
/// <param name="Minor">SDL_GL_CONTEXT_MINOR_VERSION.</param>
/// <param name="CoreProfile">SDL_GL_CONTEXT_PROFILE_MASK is core.</param>
/// <param name="SwapInterval">The swap interval in effect.</param>
public sealed record GlContextInfo(int Major, int Minor, bool CoreProfile, int SwapInterval)
{
    /// <inheritdoc/>
    public override string ToString() => $"SDL reports GL {Major}.{Minor} {(CoreProfile ? "core" : "non-core")}, swap interval {SwapInterval}";
}

/// <summary>An SDL3 window with a current OpenGL 3.3 core context. Use it on the thread that created it.</summary>
public sealed unsafe partial class GameWindow : IDisposable
{
    private SDL_Window* _window;
    private SDL_GLContextState* _context;

    private GameWindow(SDL_Window* window, SDL_GLContextState* context, GlContextInfo info, bool hidden, string title)
    {
        _window = window;
        _context = context;
        Context = info;
        IsHidden = hidden;
        FullscreenRequested = IsFullscreen;
        Title = title;
    }

    /// <summary>
    /// The resource id of the icon group that the C# compiler writes for a project's <c>ApplicationIcon</c> (Roslyn's <c>/win32icon</c>: RT_GROUP_ICON 32512, the value of
    /// IDI_APPLICATION) and that the .NET apphost copies into the executable; <see cref="WindowOptions.IconResource"/> takes it.
    /// </summary>
    public const int ApplicationIconResource = 32512;

    /// <summary>The GL context as SDL reports it.</summary>
    public GlContextInfo Context { get; }

    /// <summary>The title last set (the window's caption; package K20: the game's localized name).</summary>
    public string Title { get; private set; }

    /// <summary>True when the executable that runs this process carries an icon group with id <see cref="ApplicationIconResource"/> (its <c>ApplicationIcon</c>), so a <see cref="WindowOptions.IconResource"/> of that id gives the window the crisp per-size icons; false under <c>dotnet.exe</c> or a test host.</summary>
    public static bool ExecutableHasApplicationIcon()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string? process = Environment.ProcessPath;
        if (process is null || Path.GetFileName(process).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        nint module = NativeIcon.GetModuleHandleW(null);
        return module != 0 && NativeIcon.FindResourceW(module, ApplicationIconResource, NativeIcon.GroupIcon) != 0;
    }

    /// <summary>True while the window is hidden.</summary>
    public bool IsHidden { get; private set; }

    /// <summary>The fullscreen state last asked for (Alt+Enter / F11 flips it).</summary>
    public bool FullscreenRequested { get; private set; }

    /// <summary>The SDL window id, as carried by window and keyboard events.</summary>
    public uint WindowId => (uint)SDL_GetWindowID(Handle);

    /// <summary>True when SDL reports the window as fullscreen.</summary>
    public bool IsFullscreen => (SDL_GetWindowFlags(Handle) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0;

    /// <summary>True while the window is maximized (its size then is the work area's, not the windowed size to remember; package K19).</summary>
    public bool IsMaximized => (SDL_GetWindowFlags(Handle) & SDL_WindowFlags.SDL_WINDOW_MAXIMIZED) != 0;

    /// <summary>The client size in window coordinates (pixels on Windows); in fullscreen it is the display's size.</summary>
    public (int Width, int Height) WindowSize
    {
        get
        {
            int w, h;
            SDL_GetWindowSize(Handle, &w, &h);
            return (w, h);
        }
    }

    /// <summary>True when presents are paced by vsync (visible window, swap interval not 0).</summary>
    public bool VsyncPaced => !IsHidden && Context.SwapInterval != 0;

    /// <summary>The drawable size in pixels.</summary>
    public (int Width, int Height) PixelSize
    {
        get
        {
            int w, h;
            SDL_GetWindowSizeInPixels(Handle, &w, &h);
            return (w, h);
        }
    }

    internal SDL_Window* Handle => _window != null ? _window : throw new ObjectDisposedException(nameof(GameWindow));

    /// <summary>Creates the window and a GL 3.3 core context and makes the context current.</summary>
    public static GameWindow Create(WindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.IconResource is { } icon && OperatingSystem.IsWindows())
        {
            // read when the video subsystem registers the window class (the first window of the process): the class icon is the executable's own group,
            // and Windows picks the right size of it for the title bar and the taskbar; the small icon is found in the same group when none is named
            _ = SDL_SetHint(SDL_HINT_WINDOWS_INTRESOURCE_ICON, icon.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Sdl.Acquire(PlatformSubsystems.Video);
        SDL_Window* window = null;
        SDL_GLContextState* context = null;
        try
        {
            SDL_GL_ResetAttributes();
            SetAttr(SDL_GLAttr.SDL_GL_CONTEXT_MAJOR_VERSION, 3);
            SetAttr(SDL_GLAttr.SDL_GL_CONTEXT_MINOR_VERSION, 3);
            SetAttr(SDL_GLAttr.SDL_GL_CONTEXT_PROFILE_MASK, (int)SDL_GLProfile.SDL_GL_CONTEXT_PROFILE_CORE);
            SetAttr(SDL_GLAttr.SDL_GL_DOUBLEBUFFER, 1);
            SetAttr(SDL_GLAttr.SDL_GL_RED_SIZE, 8);
            SetAttr(SDL_GLAttr.SDL_GL_GREEN_SIZE, 8);
            SetAttr(SDL_GLAttr.SDL_GL_BLUE_SIZE, 8);
            SetAttr(SDL_GLAttr.SDL_GL_ALPHA_SIZE, 0);
            SetAttr(SDL_GLAttr.SDL_GL_DEPTH_SIZE, 0);
            SetAttr(SDL_GLAttr.SDL_GL_STENCIL_SIZE, 0);
            SDL_WindowFlags flags = SDL_WindowFlags.SDL_WINDOW_OPENGL | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY;
            if (options.Hidden)
            {
                flags |= SDL_WindowFlags.SDL_WINDOW_HIDDEN;
            }

            if (options.Resizable)
            {
                flags |= SDL_WindowFlags.SDL_WINDOW_RESIZABLE;
            }

            window = SDL_CreateWindow(options.Title, options.Width, options.Height, flags);
            if (window == null)
            {
                throw new PlatformException("SDL_CreateWindow: " + SDL_GetError());
            }

            context = SDL_GL_CreateContext(window);
            if (context == null)
            {
                throw new PlatformException("SDL_GL_CreateContext (3.3 core): " + SDL_GetError());
            }

            if (!SDL_GL_MakeCurrent(window, context))
            {
                throw new PlatformException("SDL_GL_MakeCurrent: " + SDL_GetError());
            }

            // A refused interval keeps the driver default; the interval actually in effect is reported.
            _ = SDL_GL_SetSwapInterval(options.SwapInterval);
            int major, minor, profile, interval;
            SDL_GL_GetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MAJOR_VERSION, &major);
            SDL_GL_GetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MINOR_VERSION, &minor);
            SDL_GL_GetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_PROFILE_MASK, &profile);
            SDL_GL_GetSwapInterval(&interval);
            var info = new GlContextInfo(major, minor, (profile & (int)SDL_GLProfile.SDL_GL_CONTEXT_PROFILE_CORE) != 0, interval);
            var w = new GameWindow(window, context, info, options.Hidden, options.Title);
            if (options.Fullscreen)
            {
                w.SetFullscreen(true);
            }

            return w;
        }
        catch
        {
            if (context != null)
            {
                SDL_GL_DestroyContext(context);
            }

            if (window != null)
            {
                SDL_DestroyWindow(window);
            }

            Sdl.Release(PlatformSubsystems.Video);
            throw;
        }
    }

    /// <summary>The address of a GL entry point of this context (for Tsuki.Render's bindings).</summary>
    public nint GetProcAddress(string name) => SDL_GL_GetProcAddress(name);

    /// <summary>Makes this window's context current on the calling thread.</summary>
    public void MakeCurrent()
    {
        if (!SDL_GL_MakeCurrent(Handle, _context))
        {
            throw new PlatformException("SDL_GL_MakeCurrent: " + SDL_GetError());
        }
    }

    /// <summary>
    /// Enters or leaves borderless desktop fullscreen (no display-mode change). Returns false when
    /// SDL refused the request. The change itself may complete asynchronously; see <see cref="Sync"/>.
    /// </summary>
    public bool SetFullscreen(bool fullscreen)
    {
        FullscreenRequested = fullscreen;
        if (fullscreen && !SDL_SetWindowFullscreenMode(Handle, null))
        {
            return false;
        }

        return SDL_SetWindowFullscreen(Handle, fullscreen);
    }

    /// <summary>Flips the requested fullscreen state (Alt+Enter, F11; ARCHITECTURE 9.4).</summary>
    public bool ToggleFullscreen() => SetFullscreen(!FullscreenRequested);

    /// <summary>Waits (bounded by SDL) until pending window state changes have been applied.</summary>
    public bool Sync() => SDL_SyncWindow(Handle);

    /// <summary>Sets the caption (UTF-8; a Japanese title shows as such on Windows). False when SDL refused it.</summary>
    public bool SetTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        Title = title;
        return SDL_SetWindowTitle(Handle, title);
    }

    /// <summary>
    /// Sets the window's icon from straight (not premultiplied) RGBA pixels, row-major, <paramref name="width"/> x <paramref name="height"/> (package K20: the port's own
    /// crescent, embedded in the game). On Windows this is one bitmap that the system scales for the title bar and the taskbar, so a process whose executable carries the
    /// icon group prefers <see cref="WindowOptions.IconResource"/>; this is the way for every other host. False when SDL refused it.
    /// </summary>
    public bool SetIcon(int width, int height, ReadOnlySpan<byte> rgba)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException($"{width} x {height} RGBA needs {width * height * 4} bytes, not {rgba.Length}", nameof(rgba));
        }

        fixed (byte* pixels = rgba)
        {
            // ABGR8888 is the little-endian name of bytes R, G, B, A in memory (SDL_PIXELFORMAT_RGBA32)
            SDL_Surface* surface = SDL_CreateSurfaceFrom(width, height, SDL_PixelFormat.SDL_PIXELFORMAT_ABGR8888, (nint)pixels, width * 4);
            if (surface == null)
            {
                return false;
            }

            try
            {
                return SDL_SetWindowIcon(Handle, surface);          // SDL converts and keeps its own copy; the pixels are not referenced afterwards
            }
            finally
            {
                SDL_DestroySurface(surface);
            }
        }
    }

    /// <summary>
    /// Windows only (package K23): gives the window the frames of a .ico file at the system's small and large icon sizes, so the title bar, Alt+Tab and the taskbar
    /// show the picture drawn for their size instead of one bitmap the system scales (which is what <see cref="SetIcon"/> amounts to on Windows). The handles are
    /// kept until the window is disposed; the icon SDL set is left to SDL. False on another platform, when the file has no usable frame or the window has no HWND.
    /// </summary>
    public bool SetIconFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        nint hwnd = SDL_GetPointerProperty(SDL_GetWindowProperties(Handle), SDL_PROP_WINDOW_WIN32_HWND_POINTER, 0);
        if (hwnd == 0)
        {
            return false;
        }

        nint small = NativeIcon.LoadImageW(0, path, NativeIcon.ImageIcon, NativeIcon.GetSystemMetrics(NativeIcon.SmallIconWidth), NativeIcon.GetSystemMetrics(NativeIcon.SmallIconHeight), NativeIcon.LoadFromFile);
        nint big = NativeIcon.LoadImageW(0, path, NativeIcon.ImageIcon, NativeIcon.GetSystemMetrics(NativeIcon.IconWidth), NativeIcon.GetSystemMetrics(NativeIcon.IconHeight), NativeIcon.LoadFromFile);
        if (small == 0 && big == 0)
        {
            return false;
        }

        if (small != 0)
        {
            _ = NativeIcon.SendMessageW(hwnd, NativeIcon.SetIconMessage, NativeIcon.IconSmall, small);
        }

        if (big != 0)
        {
            _ = NativeIcon.SendMessageW(hwnd, NativeIcon.SetIconMessage, NativeIcon.IconBig, big);
        }

        DestroyFileIcons();
        _fileIconSmall = small;
        _fileIconBig = big;
        return true;
    }

    private nint _fileIconSmall;
    private nint _fileIconBig;

    private void DestroyFileIcons()
    {
        if (OperatingSystem.IsWindows())
        {
            if (_fileIconSmall != 0)
            {
                _ = NativeIcon.DestroyIcon(_fileIconSmall);
            }

            if (_fileIconBig != 0)
            {
                _ = NativeIcon.DestroyIcon(_fileIconBig);
            }
        }

        _fileIconSmall = 0;
        _fileIconBig = 0;
    }

    /// <summary>Shows the window.</summary>
    public void Show()
    {
        SDL_ShowWindow(Handle);
        IsHidden = false;
    }

    /// <summary>Presents the back buffer.</summary>
    public void Swap() => SDL_GL_SwapWindow(Handle);

    /// <summary>Destroys the context and the window.</summary>
    public void Dispose()
    {
        if (_context != null)
        {
            SDL_GL_DestroyContext(_context);
            _context = null;
        }

        if (_window != null)
        {
            SDL_DestroyWindow(_window);
            _window = null;
            Sdl.Release(PlatformSubsystems.Video);
        }

        DestroyFileIcons();
    }

    private static void SetAttr(SDL_GLAttr attr, int value)
    {
        if (!SDL_GL_SetAttribute(attr, value))
        {
            throw new PlatformException($"SDL_GL_SetAttribute({attr}, {value}): {SDL_GetError()}");
        }
    }

    /// <summary>
    /// The kernel32 calls that tell whether the running executable carries the application icon group, and the user32 calls that give a window the icons of a .ico
    /// file (package K23) (Windows only; no SDL equivalent).
    /// </summary>
    private static partial class NativeIcon
    {
        /// <summary>RT_GROUP_ICON.</summary>
        public const int GroupIcon = 14;

        /// <summary>LoadImage: IMAGE_ICON.</summary>
        public const uint ImageIcon = 1;

        /// <summary>LoadImage: LR_LOADFROMFILE.</summary>
        public const uint LoadFromFile = 0x10;

        /// <summary>WM_SETICON.</summary>
        public const uint SetIconMessage = 0x80;

        /// <summary>WM_SETICON: ICON_SMALL (the title bar).</summary>
        public const nint IconSmall = 0;

        /// <summary>WM_SETICON: ICON_BIG (Alt+Tab, the taskbar).</summary>
        public const nint IconBig = 1;

        /// <summary>GetSystemMetrics: SM_CXICON, SM_CYICON, SM_CXSMICON, SM_CYSMICON.</summary>
        public const int IconWidth = 11, IconHeight = 12, SmallIconWidth = 49, SmallIconHeight = 50;

        [System.Runtime.InteropServices.LibraryImport("kernel32.dll", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
        public static partial nint GetModuleHandleW(string? moduleName);

        [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
        public static partial nint FindResourceW(nint module, nint name, nint type);

        [System.Runtime.InteropServices.LibraryImport("user32.dll", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
        public static partial nint LoadImageW(nint instance, string name, uint type, int cx, int cy, uint flags);

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        public static partial nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        public static partial int GetSystemMetrics(int index);

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static partial bool DestroyIcon(nint icon);
    }
}

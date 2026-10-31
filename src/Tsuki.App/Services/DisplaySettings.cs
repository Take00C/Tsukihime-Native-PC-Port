// The display settings of the running game (package K19): what the config screen's System tab shows and changes (the window mode, the windowed size), what
// Alt+Enter / F11 report, and what is remembered: the window mode in the config block byte the launcher's switch reads (AppSettings.WindowModeOffset, the byte
// --fullscreen / --windowed store at start) and the windowed size in settings.ini, which the launcher's Display page reads. The window is behind a small
// interface so that the logic runs in tests without SDL.
using Tsuki.Engine.Contracts;
using Tsuki.Platform;
using Tsuki.Ui;

namespace Tsuki.App.Services;

/// <summary>What the display settings need from the window (the game window through <see cref="SdlDisplayWindow"/>; a fake in the tests).</summary>
public interface IDisplayWindow
{
    /// <summary>The fullscreen state asked for last (SDL applies a request asynchronously; this is what the row shows).</summary>
    bool Fullscreen { get; }

    /// <summary>The client size in window coordinates (the display's size while fullscreen).</summary>
    (int Width, int Height) Size { get; }

    /// <summary>True while the window is maximized (its size then is not a windowed size to remember).</summary>
    bool Maximized { get; }

    /// <summary>The work area of the display the window is on.</summary>
    (int Width, int Height) WorkArea { get; }

    /// <summary>Asks for borderless fullscreen or a window; false when SDL refused.</summary>
    bool SetFullscreen(bool fullscreen);

    /// <summary>Resizes a window that is not fullscreen to a size clamped to the work area and centres it; returns the size applied.</summary>
    (int Width, int Height) Resize(int width, int height);
}

/// <summary>The display settings over the game window.</summary>
public sealed class DisplaySettings : IUiDisplaySettings
{
    private readonly IDisplayWindow _window;
    private readonly ConfigBlock _config;
    private readonly Action _commit;
    private readonly string? _settingsPath;
    private readonly Action<string>? _log;
    private (int Width, int Height) _windowed;
    private (int Width, int Height) _reference;

    /// <summary>Creates the settings over a placed window.</summary>
    /// <param name="window">The window.</param>
    /// <param name="config">The live config block; its window-mode byte is the remembered mode.</param>
    /// <param name="commit">Writes the save file (called when the byte changed, so the choice survives a crash).</param>
    /// <param name="settingsPath">settings.ini, or null when nothing is written (<c>--no-save</c>).</param>
    /// <param name="windowedSize">The windowed size the window was placed at, before any fullscreen request (the size to go back to).</param>
    /// <param name="log">Where to say what was remembered.</param>
    public DisplaySettings(IDisplayWindow window, ConfigBlock config, Action commit, string? settingsPath, (int Width, int Height) windowedSize, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(commit);
        _window = window;
        _config = config;
        _commit = commit;
        _settingsPath = settingsPath;
        _log = log;
        _windowed = windowedSize;
        _reference = windowedSize;
    }

    /// <inheritdoc/>
    public bool Fullscreen
    {
        get => _window.Fullscreen;
        set
        {
            if (_window.Fullscreen == value)
            {
                return;
            }

            if (!_window.SetFullscreen(value))
            {
                _log?.Invoke($"window mode: SDL refused {(value ? "fullscreen" : "windowed")}");
                return;
            }

            Remember(value);
        }
    }

    /// <inheritdoc/>
    public (int Width, int Height) WindowSize
    {
        get => _windowed;
        set
        {
            if (_window.Fullscreen)
            {
                Fullscreen = false;
            }

            _windowed = _window.Resize(value.Width, value.Height);
            _log?.Invoke($"window size: {_windowed.Width} x {_windowed.Height} (config screen)");
            SaveWindowSize(chosen: true);           // an explicit choice is always written, even when it is the size the run started with
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<(int Width, int Height)> SizeChoices
    {
        get
        {
            (int w, int h) = _window.WorkArea;
            return WindowFit.PresetsFor(w, h);
        }
    }

    /// <summary>The remembered mode (the config block byte): true for fullscreen.</summary>
    public bool Remembered => _config.Bytes[AppSettings.WindowModeOffset] == 1;

    /// <summary>Alt+Enter or F11 switched the window (the event pump did that): the state is remembered the way the row's choice is.</summary>
    public void NoteFullscreen(bool fullscreen) => Remember(fullscreen);

    /// <summary>The window's size changed: the size of a window that is neither fullscreen nor maximized is the windowed size to remember (the player dragged an edge).</summary>
    public void NoteResized()
    {
        if (_window.Fullscreen || _window.Maximized)
        {
            return;
        }

        (int w, int h) = _window.Size;
        if (w >= WindowFit.MinWidth && h >= WindowFit.MinHeight)
        {
            _windowed = (w, h);
        }
    }

    /// <summary>
    /// Writes <c>window=WxH</c> into settings.ini when the windowed size is not the one the run started with (or the one written last), or always with
    /// <paramref name="chosen"/> (the config screen's row); true when it was written.
    /// </summary>
    public bool SaveWindowSize(bool chosen = false)
    {
        if (_settingsPath is null || (!chosen && _windowed == _reference))
        {
            return false;
        }

        (int w, int h) = _windowed;
        bool ok = AppSettings.Update(_settingsPath, s => s with { WindowWidth = w, WindowHeight = h });
        if (ok)
        {
            _reference = _windowed;
            _log?.Invoke($"window size {w} x {h} saved to settings.ini");
        }
        else
        {
            _log?.Invoke("settings.ini not written (window size)");
        }

        return ok;
    }

    private void Remember(bool fullscreen)
    {
        byte wanted = (byte)(fullscreen ? 1 : 0);
        if (_config.Bytes[AppSettings.WindowModeOffset] == wanted)
        {
            return;
        }

        _config.Bytes[AppSettings.WindowModeOffset] = wanted;
        _commit();
        _log?.Invoke($"window mode: {(fullscreen ? "fullscreen" : "windowed")} (remembered)");
    }
}

/// <summary>The game window as the display settings see it.</summary>
internal sealed class SdlDisplayWindow(GameWindow window) : IDisplayWindow
{
    /// <inheritdoc/>
    public bool Fullscreen => window.FullscreenRequested;

    /// <inheritdoc/>
    public (int Width, int Height) Size => window.WindowSize;

    /// <inheritdoc/>
    public bool Maximized => window.IsMaximized;

    /// <inheritdoc/>
    public (int Width, int Height) WorkArea
    {
        get
        {
            DisplayInfo d = WindowPlacement.DisplayOf(window);
            return (d.WorkWidth, d.WorkHeight);
        }
    }

    /// <inheritdoc/>
    public bool SetFullscreen(bool fullscreen) => window.SetFullscreen(fullscreen);

    /// <inheritdoc/>
    public (int Width, int Height) Resize(int width, int height)
    {
        window.Sync();                      // a window that just left fullscreen gets its old shape back first
        Placement placed = WindowPlacement.Resize(window, width, height);
        return (placed.Width, placed.Height);
    }
}

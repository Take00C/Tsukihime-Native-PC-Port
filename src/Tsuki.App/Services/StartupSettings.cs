// The launcher's choices at start (package K17). TsukiLauncher.exe writes settings.ini; this reads it once, before the window exists, and turns it into window options,
// a window placement (package K19: clamped to the monitor's work area and centred) and two values for the config block. Nothing here runs per frame.
using Tsuki.Engine.Contracts;
using Tsuki.Platform;

namespace Tsuki.App.Services;

/// <summary>What the game does with <c>settings.ini</c> when it starts.</summary>
internal sealed class StartupSettings
{
    /// <summary>The config block byte that holds the window mode of the last run (0 = windowed, 1 = borderless fullscreen).</summary>
    public const int WindowModeOffset = AppSettings.WindowModeOffset;

    private StartupSettings(string? path, AppSettingsData data)
    {
        Path = path;
        Data = data;
    }

    /// <summary>The settings file, or null when nothing is read or written (<c>--no-save</c>).</summary>
    public string? Path { get; }

    /// <summary>The values read.</summary>
    public AppSettingsData Data { get; }

    /// <summary>The folder of <c>save.dat</c> and <c>settings.ini</c>: <c>--save-dir</c>, else <c>%APPDATA%\Tsuki</c>.</summary>
    public static string DataDirectory(AppArgs args)
    {
        if (args.SaveDir is { } directory)
        {
            return directory;
        }

        string profile = ProfileDirectory.ForUser();
        Directory.CreateDirectory(profile);
        return profile;
    }

    /// <summary>Reads the file of this run.</summary>
    public static StartupSettings Load(AppArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? path = args.NoSave ? null : System.IO.Path.Combine(DataDirectory(args), AppSettings.FileName);
        return new StartupSettings(path, AppSettings.Load(path));
    }

    /// <summary>Wraps values (tests).</summary>
    public static StartupSettings From(AppSettingsData data) => new(null, data);

    /// <summary>The windowed size the file asks for, else the default (1600 x 900; <see cref="PlaceWindow"/> clamps either to the monitor).</summary>
    public (int Width, int Height) RequestedWindowSize => Data.WindowSize ?? (WindowFit.DefaultWidth, WindowFit.DefaultHeight);

    /// <summary>
    /// The options of the visible window: the requested size and the swap interval, and hidden: the app shows the window once <see cref="PlaceWindow"/> has
    /// clamped and centred it, and asks for fullscreen after that (borderless fullscreen takes the display the window is on), so the window never comes up at a
    /// wrong size or place first (package K19).
    /// </summary>
    public WindowOptions WindowOptions(string title = "Tsukihime")
    {
        (int w, int h) = RequestedWindowSize;
        return new(title, w, h, Hidden: true, SwapInterval: Data.VSync == false ? 0 : 1);
    }

    /// <summary>Sizes the window (clamped to the work area) and centres it on the chosen monitor, else on the one it is on; see <see cref="WindowPlacement.Place"/>.</summary>
    public Placement PlaceWindow(GameWindow window) => WindowPlacement.Place(window, Data.Monitor?.X, Data.Monitor?.Y, Data.WindowWidth, Data.WindowHeight);

    /// <summary>
    /// Puts what applies once into the config block: the language (unless <c>--lang</c> was given) and the window mode of the command line. The window mode is the
    /// remembered state of the game (the byte Alt+Enter and F11 keep): <c>--fullscreen</c> and <c>--windowed</c> store their choice there and a pending
    /// <c>fullscreen=</c> line of the launcher is removed from the file; without either the remembered byte stays as it is (package K18: the file alone never
    /// decides the window mode, so a direct start keeps using the remembered one). Applied keys are removed from the file, so the config screen and Alt+Enter keep
    /// the last word afterwards. Returns true when the config block changed (a language was applied or the window mode byte is not what it was), so that the caller has something to save.
    /// </summary>
    public bool ApplyToConfig(ConfigBlock config, bool languageGivenOnCommandLine, bool? windowMode = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        bool language = false;
        if (Data.GameLanguage is { } lang && lang != Language.Ko && !languageGivenOnCommandLine)
        {
            config.Language = (short)lang;
            language = true;
        }

        bool windowChanged = false;
        if (windowMode is { } mode)
        {
            byte wanted = (byte)(mode ? 1 : 0);
            windowChanged = config.Bytes[WindowModeOffset] != wanted;
            config.Bytes[WindowModeOffset] = wanted;
        }

        bool consumed = language || windowMode is not null;
        if (consumed && Path is not null && (language || Data.Fullscreen is not null))
        {
            AppSettings.Update(Path, saved => saved with
            {
                GameLanguage = language ? null : saved.GameLanguage,
                Fullscreen = windowMode is not null ? null : saved.Fullscreen,
            });
        }

        bool applied = language || windowChanged;
        return applied;
    }
}

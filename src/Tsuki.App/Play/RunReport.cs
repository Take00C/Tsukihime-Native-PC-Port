// The lines a Linux or macOS run log carries so that a first report from a new machine (a Steam Deck) can be read: SDL and its video driver, Steam's variables,
// the window's real state after a fullscreen request, the pads at start, the audio device's comings and goings, device callbacks that stopped, presents that do not
// wait for the display. They only log; nothing here changes what the game does. Windows writes none of them, so its log stays v0.1.6's.
using System.Globalization;
using Tsuki.Platform;

namespace Tsuki.App.Play;

/// <summary>The extra run-log lines of Linux and macOS (see the file header).</summary>
internal static class RunReport
{
    /// <summary>The variables Steam sets for the games it starts that tell a Steam Deck and its Game Mode apart.</summary>
    public static IReadOnlyList<string> SteamVariables { get; } = ["SteamDeck", "SteamOS", "SteamGamepadUI"];

    /// <summary>True where these lines are written: everywhere but Windows.</summary>
    public static bool Enabled => !OperatingSystem.IsWindows();

    /// <summary>The SDL line: the version, where the library came from and the video driver (x11, wayland, cocoa, offscreen).</summary>
    public static string Sdl(string version, string? library, string? videoDriver) =>
        $"SDL {version} ({library ?? "loaded by the system's search"}), video driver {videoDriver ?? "none"}";

    /// <summary>The Steam line: each of <see cref="SteamVariables"/> as the game was given it.</summary>
    public static string Steam(Func<string, string?> variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        return "steam: " + string.Join(", ", SteamVariables.Select(n => variable(n) is { } v ? $"{n}={v}" : $"{n} unset"));
    }

    /// <summary>A pad that was attached when the game started (the hot-plugged ones have their own line).</summary>
    public static string PadAtStart(GamepadInfo pad)
    {
        ArgumentNullException.ThrowIfNull(pad);
        return string.Create(CultureInfo.InvariantCulture, $"gamepad at start: {pad.Name} ({pad.Type}, {pad.VendorId:x4}:{pad.ProductId:x4})");
    }

    /// <summary>What the window is once the first frame is up: the drawable, SDL's fullscreen flag and the display.</summary>
    public static string WindowState((int Width, int Height) pixels, bool fullscreen, DisplayInfo display) =>
        $"window state: {pixels.Width} x {pixels.Height} pixels, fullscreen {(fullscreen ? "on" : "off")}, display \"{display.Name}\" {display.Width} x {display.Height}";

    /// <summary>The line of an audio device event for a playback device, or null for a recording device and any other event.</summary>
    public static string? AudioDevice(PlatformEvent e, string? name, uint gameDevice)
    {
        if (e.Recording)
        {
            return null;
        }

        string what = e.Kind switch
        {
            PlatformEventKind.AudioDeviceAdded => "added",
            PlatformEventKind.AudioDeviceRemoved => "removed",
            PlatformEventKind.AudioDeviceFormatChanged => "format changed",
            _ => "",
        };
        return what.Length == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"audio device {what}: #{e.Device} \"{name ?? "?"}\"{(e.Device != 0 && e.Device == gameDevice ? " (the game's device)" : string.Empty)}");
    }
}

/// <summary>
/// Watches the audio device's callbacks: when none has come for a second while the game plays sound, the sound no longer reaches the device (a stream that failed
/// after a sleep says nothing else); says so once, and again when they come back.
/// </summary>
/// <param name="frequency">Timestamps per second.</param>
internal sealed class AudioCallbackWatch(long frequency)
{
    private long _callbacks = -1;
    private long _since;
    private bool _stalled;

    /// <summary>The line to log for the callback count at <paramref name="now"/>, or null.</summary>
    public string? Note(long callbacks, long now)
    {
        if (callbacks != _callbacks)
        {
            bool resumed = _stalled && _callbacks >= 0;
            double gap = (now - _since) / (double)frequency;
            _callbacks = callbacks;
            _since = now;
            _stalled = false;
            return resumed ? string.Create(CultureInfo.InvariantCulture, $"audio: the device's callbacks came back after {gap:F1} s") : null;
        }

        if (!_stalled && now - _since > frequency)
        {
            _stalled = true;
            return "audio: no device callback for 1 s (no sound reaches the device)";
        }

        return null;
    }
}

/// <summary>
/// Watches the presents of a window whose swaps should wait for the display (swap interval 1): many hundreds a second mean they do not, and the loop redraws as fast
/// as the machine allows. Says so once per run, with the rate measured over two seconds.
/// </summary>
/// <param name="frequency">Timestamps per second.</param>
internal sealed class PresentRateWatch(long frequency)
{
    /// <summary>More presents a second than any display shows.</summary>
    public const double Limit = 400;

    private long _start = -1;
    private long _presents;
    private bool _said;

    /// <summary>The line to log for the present count at <paramref name="now"/> (counting only while the swaps should be paced), or null.</summary>
    public string? Note(long presents, long now, bool paced)
    {
        if (_said)
        {
            return null;
        }

        if (!paced || _start < 0)
        {
            _start = paced ? now : -1;
            _presents = presents;
            return null;
        }

        double seconds = (now - _start) / (double)frequency;
        if (seconds < 2)
        {
            return null;
        }

        double rate = (presents - _presents) / seconds;
        _start = now;
        _presents = presents;
        if (rate <= Limit)
        {
            return null;
        }

        _said = true;
        return string.Create(CultureInfo.InvariantCulture, $"vsync: {rate:F0} presents a second with swap interval 1: the swaps do not wait for the display");
    }
}

// The PC port's own switches (packages K21, K23 and K24): Mute in Background (Config > Sound), Screenshot Notice, Copy Text to Clipboard, Text Hooker Output and Text
// WebSocket (Config > System). They belong to the PC program, not to the PS4 game, so they live in settings.ini next to the button prompts and the window size (AppSettings), never in
// the config block of save.dat. The config screen reads and sets them through IUiPortSettings; the app acts on them (the mixer's master gain, the text outputs)
// through Changed.
using Tsuki.Ui;

namespace Tsuki.App.Services;

/// <summary>The port's switches, applied at once and saved to settings.ini.</summary>
public sealed class PortSettings : IUiPortSettings
{
    private readonly string? _settingsPath;
    private readonly Action<string>? _log;
    private bool _mute;
    private bool _copy;
    private bool _hooker;
    private bool _socket;
    private bool _notice;

    /// <summary>Creates the switches from the file's values (<paramref name="data"/>, read at start).</summary>
    /// <param name="settingsPath">settings.ini, or null when nothing is written (<c>--no-save</c>).</param>
    /// <param name="data">What the file held at start.</param>
    /// <param name="log">Where to say what changed.</param>
    public PortSettings(string? settingsPath, AppSettingsData data, Action<string>? log = null)
    {
        _settingsPath = settingsPath;
        _log = log;
        _mute = data.MuteInBackground ?? false;
        _copy = data.CopyText ?? false;
        _hooker = data.TextHooker ?? true;
        _socket = data.TextWebSocket ?? false;
        _notice = data.ScreenshotNotice ?? true;
        TextWebSocketPort = data.TextWebSocketPort ?? AppSettingsData.DefaultTextWebSocketPort;
    }

    /// <summary>Raised after a switch changed (and was saved).</summary>
    public event Action? Changed;

    /// <inheritdoc/>
    public bool MuteInBackground
    {
        get => _mute;
        set
        {
            if (_mute == value)
            {
                return;
            }

            _mute = value;
            Save(s => s with { MuteInBackground = value }, "mute_in_background", value);
        }
    }

    /// <inheritdoc/>
    public bool CopyTextToClipboard
    {
        get => _copy;
        set
        {
            if (_copy == value)
            {
                return;
            }

            _copy = value;
            Save(s => s with { CopyText = value }, "copy_text", value);
        }
    }

    /// <inheritdoc/>
    public bool TextHookerOutput
    {
        get => _hooker;
        set
        {
            if (_hooker == value)
            {
                return;
            }

            _hooker = value;
            Save(s => s with { TextHooker = value }, "text_hooker", value);
        }
    }

    /// <inheritdoc/>
    public bool TextWebSocket
    {
        get => _socket;
        set
        {
            if (_socket == value)
            {
                return;
            }

            _socket = value;
            Save(s => s with { TextWebSocket = value }, "text_websocket", value);
        }
    }

    /// <inheritdoc/>
    public bool ScreenshotNotice
    {
        get => _notice;
        set
        {
            if (_notice == value)
            {
                return;
            }

            _notice = value;
            Save(s => s with { ScreenshotNotice = value }, "screenshot_notice", value);
        }
    }

    /// <summary>The WebSocket's port (settings.ini <c>text_websocket_port=</c>, else 6677); read at start, the file alone changes it.</summary>
    public int TextWebSocketPort { get; }

    private void Save(Func<AppSettingsData, AppSettingsData> change, string key, bool value)
    {
        if (_settingsPath is null)
        {
            _log?.Invoke($"{key} {(value ? "on" : "off")} (not saved: no settings file)");
        }
        else if (AppSettings.Update(_settingsPath, change))
        {
            _log?.Invoke($"{key} {(value ? "on" : "off")} saved to settings.ini");
        }
        else
        {
            _log?.Invoke($"settings.ini not written ({key})");
        }

        Changed?.Invoke();
    }
}

// The small settings file of the app (packages K9 and K17): choices that belong to the PC port and not to the PS4 game, so they stay out of the config block of the save
// file. It is plain text next to save.dat, one "key=value" per line; unknown keys are ignored and a missing or unreadable file means the defaults.
//
// The same source file is compiled into TsukiLauncher.exe (the launcher writes the file, the game reads it), so it may only use Tsuki.Engine and Tsuki.Ui.
using System.Globalization;
using Tsuki.Engine.Contracts;
using Tsuki.Ui;

namespace Tsuki.App.Services;

/// <summary>The left / top corner of a display in virtual-desktop pixels: how <c>monitor=</c> names a monitor (the same on every program that asks Windows).</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
public readonly record struct MonitorOrigin(int X, int Y)
{
    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X},{Y}");
}

/// <summary>The values of <c>settings.ini</c>. A null value is a key that is not in the file: the game keeps what it did before.</summary>
/// <param name="Prompts">The button prompts: Auto follows the last input device.</param>
/// <param name="Fullscreen"><c>fullscreen=</c>: the launcher's pending window-mode choice (package K18). The game does not read it by itself; the launcher turns it into <c>--fullscreen</c> / <c>--windowed</c> on PLAY, the game remembers that in the config block of the save file and removes the key.</param>
/// <param name="WindowWidth"><c>window=WxH</c>: the windowed size at start, width.</param>
/// <param name="WindowHeight"><c>window=WxH</c>: the windowed size at start, height.</param>
/// <param name="Monitor"><c>monitor=x,y</c>: the display to open on.</param>
/// <param name="VSync"><c>vsync=</c>: presents wait for the monitor refresh (the default) or run on the game's own 60 Hz timer.</param>
/// <param name="GameLanguage"><c>language=</c>: the game language for the next start (applied once to the config block; the config screen changes it later).</param>
/// <param name="MuteInBackground"><c>mute_in_background=</c> (package K21): every sound fades out while the window has no focus (off when the key is absent).</param>
/// <param name="CopyText"><c>copy_text=</c> (package K21): each new message's plain text goes to the clipboard when it starts displaying (off when the key is absent).</param>
/// <param name="TextHooker"><c>text_hooker=</c> (package K23): each new line goes once through the GDI text calls a text hooker attaches to (on when the key is absent).</param>
/// <param name="TextWebSocket"><c>text_websocket=</c> (package K23): a WebSocket on 127.0.0.1 broadcasts each new line (off when the key is absent).</param>
/// <param name="TextWebSocketPort"><c>text_websocket_port=</c> (package K23): the WebSocket's port, 1..65535 (6677 when the key is absent).</param>
/// <param name="ScreenshotNotice"><c>screenshot_notice=</c> (package K24): the short "Screenshot saved" notice after F12 (on when the key is absent).</param>
public readonly record struct AppSettingsData(
    PromptSetting Prompts = PromptSetting.Auto,
    bool? Fullscreen = null,
    int? WindowWidth = null,
    int? WindowHeight = null,
    MonitorOrigin? Monitor = null,
    bool? VSync = null,
    Language? GameLanguage = null,
    bool? MuteInBackground = null,
    bool? CopyText = null,
    bool? TextHooker = null,
    bool? TextWebSocket = null,
    int? TextWebSocketPort = null,
    bool? ScreenshotNotice = null)
{
    /// <summary>The WebSocket port when the file names none (texthooker-ui's default).</summary>
    public const int DefaultTextWebSocketPort = 6677;

    /// <summary>The smallest window the game accepts from the file.</summary>
    public const int MinWindowWidth = 640;

    /// <summary>The smallest window height.</summary>
    public const int MinWindowHeight = 360;

    /// <summary>The largest window side.</summary>
    public const int MaxWindowSide = 16384;

    /// <summary>The window size when both sides are set.</summary>
    public (int Width, int Height)? WindowSize => WindowWidth is { } w && WindowHeight is { } h ? (w, h) : null;
}

/// <summary>Reads and writes <c>settings.ini</c>.</summary>
public static class AppSettings
{
    /// <summary>The file name, next to <c>save.dat</c>.</summary>
    public const string FileName = "settings.ini";

    /// <summary>
    /// The byte of the config block of <c>save.dat</c> that remembers the window mode (0 = windowed, 1 = borderless fullscreen): Alt+Enter, F11, <c>--fullscreen</c> and
    /// <c>--windowed</c> write it in the game, the launcher's fullscreen switch reads it (package K18: one constant for both).
    /// </summary>
    public const int WindowModeOffset = 0xA9;

    /// <summary>The file and command-line name of a language: ja, en, zc or zt.</summary>
    public static string LanguageName(Language language) => language switch
    {
        Language.Ja => "ja",
        Language.Zc => "zc",
        Language.Zt => "zt",
        _ => "en",
    };

    /// <summary>Reads a language name (ja, en, zc, zt; a few aliases); false when it is none. Korean is never selectable.</summary>
    public static bool TryParseLanguage(string? text, out Language language)
    {
        language = Language.En;
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "ja" or "jp" or "japanese" or "0":
                language = Language.Ja;
                return true;
            case "en" or "english" or "1":
                language = Language.En;
                return true;
            case "zc" or "zh-cn" or "zh-hans" or "2":
                language = Language.Zc;
                return true;
            case "zt" or "zh-tw" or "zh-hant" or "3":
                language = Language.Zt;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads on / off (also true, false, yes, no, 1, 0); false when it is neither.</summary>
    public static bool TryParseSwitch(string? text, out bool value)
    {
        value = false;
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "on" or "true" or "yes" or "1":
                value = true;
                return true;
            case "off" or "false" or "no" or "0":
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads <c>WxH</c> (also <c>W x H</c> and the multiplication sign); false when it is not two sizes in range.</summary>
    public static bool TryParseWindow(string? text, out int width, out int height)
    {
        width = height = 0;
        string[] parts = (text ?? string.Empty).Replace('×', 'x').Replace('X', 'x').Split('x', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height)
            && width is >= AppSettingsData.MinWindowWidth and <= AppSettingsData.MaxWindowSide
            && height is >= AppSettingsData.MinWindowHeight and <= AppSettingsData.MaxWindowSide;
    }

    /// <summary>Reads <c>x,y</c>; false when it is not two integers.</summary>
    public static bool TryParseMonitor(string? text, out MonitorOrigin origin)
    {
        origin = default;
        string[] parts = (text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y))
        {
            origin = new MonitorOrigin(x, y);
            return true;
        }

        return false;
    }

    /// <summary>Reads settings from text: lines of <c>key=value</c>, <c>#</c> comments, unknown keys and bad values left at their defaults.</summary>
    public static AppSettingsData Parse(string? text)
    {
        var data = new AppSettingsData();
        foreach (string raw in (text ?? string.Empty).Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Length == 0 || line[0] == '#' || eq <= 0)
            {
                continue;
            }

            string key = line[..eq].Trim().ToLowerInvariant();
            string value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "prompts" when PromptStyles.TryParse(value, out PromptSetting setting):
                    data = data with { Prompts = setting };
                    break;
                case "fullscreen" when TryParseSwitch(value, out bool full):
                    data = data with { Fullscreen = full };
                    break;
                case "vsync" when TryParseSwitch(value, out bool vsync):
                    data = data with { VSync = vsync };
                    break;
                case "window" when TryParseWindow(value, out int w, out int h):
                    data = data with { WindowWidth = w, WindowHeight = h };
                    break;
                case "monitor" when TryParseMonitor(value, out MonitorOrigin origin):
                    data = data with { Monitor = origin };
                    break;
                case "language" when TryParseLanguage(value, out Language language):
                    data = data with { GameLanguage = language };
                    break;
                case "mute_in_background" when TryParseSwitch(value, out bool mute):
                    data = data with { MuteInBackground = mute };
                    break;
                case "copy_text" when TryParseSwitch(value, out bool copy):
                    data = data with { CopyText = copy };
                    break;
                case "text_hooker" when TryParseSwitch(value, out bool hooker):
                    data = data with { TextHooker = hooker };
                    break;
                case "text_websocket" when TryParseSwitch(value, out bool socket):
                    data = data with { TextWebSocket = socket };
                    break;
                case "text_websocket_port" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is >= 1 and <= ushort.MaxValue:
                    data = data with { TextWebSocketPort = port };
                    break;
                case "screenshot_notice" when TryParseSwitch(value, out bool notice):
                    data = data with { ScreenshotNotice = notice };
                    break;
            }
        }

        return data;
    }

    /// <summary>The text of a settings file: the keys that are set, each after a comment that says what it does.</summary>
    public static string Format(AppSettingsData data)
    {
        var text = new System.Text.StringBuilder();
        text.Append("# Tsuki app settings (kept apart from the game's save data; the launcher writes this file, the game reads it at start)\n");
        text.Append("# prompts: auto, keyboard, playstation or xbox\n");
        text.Append(CultureInfo.InvariantCulture, $"prompts={PromptStyles.Name(data.Prompts)}\n");
        if (data.Fullscreen is { } full)
        {
            text.Append("# fullscreen: on or off, for the next start only (then Alt+Enter / F11 and the game's own memory take over)\n");
            text.Append(CultureInfo.InvariantCulture, $"fullscreen={(full ? "on" : "off")}\n");
        }

        if (data.WindowSize is { } size)
        {
            text.Append("# window: the windowed size at start, width x height\n");
            text.Append(CultureInfo.InvariantCulture, $"window={size.Width}x{size.Height}\n");
        }

        if (data.Monitor is { } monitor)
        {
            text.Append("# monitor: left,top of the display to open on, in virtual-desktop pixels (the launcher lists them)\n");
            text.Append(CultureInfo.InvariantCulture, $"monitor={monitor}\n");
        }

        if (data.VSync is { } vsync)
        {
            text.Append("# vsync: on (presents wait for the monitor) or off (the game's own 60 Hz timer)\n");
            text.Append(CultureInfo.InvariantCulture, $"vsync={(vsync ? "on" : "off")}\n");
        }

        if (data.GameLanguage is { } language)
        {
            text.Append("# language: ja, en, zc or zt, for the next start only (the config screen changes it later)\n");
            text.Append(CultureInfo.InvariantCulture, $"language={LanguageName(language)}\n");
        }

        if (data.MuteInBackground is { } mute)
        {
            text.Append("# mute_in_background: on fades every sound out while the game window has no focus (Config > Sound > Mute in Background)\n");
            text.Append(CultureInfo.InvariantCulture, $"mute_in_background={(mute ? "on" : "off")}\n");
        }

        if (data.CopyText is { } copy)
        {
            text.Append("# copy_text: on puts each new message's text on the clipboard for text hookers and dictionaries (Config > System > Copy Text to Clipboard)\n");
            text.Append(CultureInfo.InvariantCulture, $"copy_text={(copy ? "on" : "off")}\n");
        }

        if (data.TextHooker is { } hooker)
        {
            text.Append("# text_hooker: on (the default) passes each new line through the GDI text calls LunaHook / Textractor attach to (Config > System > Text Hooker Output)\n");
            text.Append(CultureInfo.InvariantCulture, $"text_hooker={(hooker ? "on" : "off")}\n");
        }

        if (data.TextWebSocket is { } socket)
        {
            text.Append("# text_websocket: on serves each new line as a text frame on ws://127.0.0.1:<text_websocket_port> for texthooker pages (Config > System > Text WebSocket)\n");
            text.Append(CultureInfo.InvariantCulture, $"text_websocket={(socket ? "on" : "off")}\n");
        }

        if (data.TextWebSocketPort is { } port)
        {
            text.Append("# text_websocket_port: the WebSocket's port, 1..65535 (6677 when the line is missing)\n");
            text.Append(CultureInfo.InvariantCulture, $"text_websocket_port={port}\n");
        }

        if (data.ScreenshotNotice is { } notice)
        {
            text.Append("# screenshot_notice: on (the default) shows the short \"Screenshot saved\" notice after F12; off saves the picture silently (Config > System > Screenshot Notice)\n");
            text.Append(CultureInfo.InvariantCulture, $"screenshot_notice={(notice ? "on" : "off")}\n");
        }

        return text.ToString();
    }

    /// <summary>Reads the file at <paramref name="path"/>; the defaults when it is missing or cannot be read.</summary>
    public static AppSettingsData Load(string? path)
    {
        if (path is null)
        {
            return default;
        }

        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <summary>Writes the file (through a temporary file, so a crash leaves the old one); false when it could not be written.</summary>
    public static bool Save(string? path, AppSettingsData data)
    {
        if (path is null)
        {
            return false;
        }

        try
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, Format(data));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Reads the file, lets <paramref name="change"/> alter the values and writes it back, so that a program that owns one key keeps the others (the game's prompts, the launcher's window keys).</summary>
    public static bool Update(string? path, Func<AppSettingsData, AppSettingsData> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return path is not null && Save(path, change(Load(path)));
    }
}

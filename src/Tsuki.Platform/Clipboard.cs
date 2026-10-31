// The system clipboard through SDL (package K21): the game puts each new message's text there for text hookers and dictionary tools when the player asks for it.
using static SDL.SDL3;

namespace Tsuki.Platform;

/// <summary>The text clipboard of the desktop.</summary>
public static class Clipboard
{
    /// <summary>Puts <paramref name="text"/> (UTF-8) on the clipboard; false when SDL refused (no video subsystem, or the clipboard is held by another program).</summary>
    public static bool SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SDL_SetClipboardText(text);
    }

    /// <summary>The text on the clipboard, or an empty string when it holds none.</summary>
    public static string GetText() => SDL_GetClipboardText() ?? string.Empty;
}

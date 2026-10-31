// Native message boxes for first-run and error UI before any game font exists (ARCHITECTURE 9.5).
using SDL;
using static SDL.SDL3;

namespace Tsuki.Platform;

/// <summary>SDL's simple message box (system font, Unicode).</summary>
public static unsafe class MessageBox
{
    /// <summary>Shows a modal error box; false when SDL could not show it.</summary>
    public static bool ShowError(string title, string message, GameWindow? parent = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);
        SDL_Window* owner = parent != null ? parent.Handle : null;
        return SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR, title, message, owner);
    }
}

// SDL3 subsystem lifetime for Tsuki.Platform (ARCHITECTURE 3.1). SDL_InitSubSystem /
// SDL_QuitSubSystem are reference-counted per subsystem, so windows and gamepad sets can come and
// go independently (several GPU tests in one test host included); only the app's final
// Shutdown calls SDL_Quit.
using SDL;
using static SDL.SDL3;

namespace Tsuki.Platform;

/// <summary>The SDL subsystems Tsuki.Platform uses.</summary>
[Flags]
public enum PlatformSubsystems
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Video: windows, GL contexts, dialogs, message boxes, keyboard and mouse events.</summary>
    Video = 1,

    /// <summary>Gamepads (and joysticks).</summary>
    Gamepad = 2,

    /// <summary>The audio device.</summary>
    Audio = 4,
}

/// <summary>Reference-counted SDL initialisation and the error text.</summary>
public static class Sdl
{
    /// <summary>The linked SDL version, "major.minor.micro".</summary>
    public static string Version
    {
        get
        {
            int v = SDL_GetVersion();
            return $"{v / 1000000}.{v / 1000 % 1000}.{v % 1000}";
        }
    }

    /// <summary>The full path SDL3.dll was loaded from (the app folder), or null when the runtime's default probing loaded it.</summary>
    public static string? LibraryPath => NativeSdl.LoadedFrom;

    /// <summary>The current SDL video driver ("windows"), or null before video is initialised.</summary>
    public static string? VideoDriver => SDL_GetCurrentVideoDriver();

    /// <summary>Initialises the subsystems (one reference each); throws with SDL's message on failure.</summary>
    public static void Acquire(PlatformSubsystems subsystems)
    {
        SDL_InitFlags flags = ToFlags(subsystems);
        if (flags != 0 && !SDL_InitSubSystem(flags))
        {
            throw new PlatformException($"SDL_InitSubSystem({subsystems}): {SDL_GetError()}");
        }
    }

    /// <summary>Drops one reference of each subsystem.</summary>
    public static void Release(PlatformSubsystems subsystems)
    {
        SDL_InitFlags flags = ToFlags(subsystems);
        if (flags != 0)
        {
            SDL_QuitSubSystem(flags);
        }
    }

    /// <summary>Shuts SDL down completely. Only the app's last line calls this.</summary>
    public static void Shutdown() => SDL_Quit();

    /// <summary>SDL's last error message.</summary>
    public static string LastError => SDL_GetError() ?? "";

    private static SDL_InitFlags ToFlags(PlatformSubsystems s)
    {
        SDL_InitFlags f = 0;
        if ((s & PlatformSubsystems.Video) != 0)
        {
            f |= SDL_InitFlags.SDL_INIT_VIDEO;
        }

        if ((s & PlatformSubsystems.Gamepad) != 0)
        {
            f |= SDL_InitFlags.SDL_INIT_GAMEPAD;
        }

        if ((s & PlatformSubsystems.Audio) != 0)
        {
            f |= SDL_InitFlags.SDL_INIT_AUDIO;
        }

        return f;
    }
}

/// <summary>An SDL call failed.</summary>
public sealed class PlatformException(string message) : Exception(message);

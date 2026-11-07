using System.Reflection;
using System.Runtime.CompilerServices;
using SDL;
using Tsuki.Platform;

namespace Tsuki.Tests.Synthetic.Platform;

public sealed class FullscreenEventTests
{
    private static (GameWindow Window, List<PlatformEvent> Events, Action<SDL_EventType> Dispatch) Pump(bool? fromEvents)
    {
        var window = (GameWindow)RuntimeHelpers.GetUninitializedObject(typeof(GameWindow));
        EventPump pump = fromEvents is { } sync ? new EventPump(window, null) { FullscreenFromEvents = sync } : new EventPump(window, null);
        var events = new List<PlatformEvent>();
        MethodInfo handle = typeof(EventPump).GetMethod("Handle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Dispatch(SDL_EventType type)
        {
            SDL_Event ev = default;
            ev.window.type = type;
            handle.Invoke(pump, [ev, events]);
        }

        return (window, events, Dispatch);
    }

    [Fact]
    public void OnMacOSTheWindowFullscreenEventsKeepTheRequestedStateInSync()
    {
        (GameWindow window, List<PlatformEvent> events, Action<SDL_EventType> dispatch) = Pump(fromEvents: true);
        dispatch(SDL_EventType.SDL_EVENT_WINDOW_ENTER_FULLSCREEN);
        Assert.True(window.FullscreenRequested);
        dispatch(SDL_EventType.SDL_EVENT_WINDOW_LEAVE_FULLSCREEN);
        Assert.False(window.FullscreenRequested);
        Assert.Equal([PlatformEventKind.EnteredFullscreen, PlatformEventKind.LeftFullscreen], events.Select(e => e.Kind));
    }

    [Fact]
    public void WindowsAndLinuxKeepTheStateTheAppAskedForAsV016Did()
    {
        (GameWindow window, List<PlatformEvent> events, Action<SDL_EventType> dispatch) = Pump(fromEvents: false);
        window.NoteFullscreenFromEvent(true);                                   // the app asked for fullscreen
        dispatch(SDL_EventType.SDL_EVENT_WINDOW_LEAVE_FULLSCREEN);
        Assert.True(window.FullscreenRequested);
        window.NoteFullscreenFromEvent(false);                                  // and for a window
        dispatch(SDL_EventType.SDL_EVENT_WINDOW_ENTER_FULLSCREEN);
        Assert.False(window.FullscreenRequested);
        Assert.Equal([PlatformEventKind.LeftFullscreen, PlatformEventKind.EnteredFullscreen], events.Select(e => e.Kind));
    }

    [Fact]
    public void OnlyMacOSSyncsByDefault()
    {
        (GameWindow window, _, Action<SDL_EventType> dispatch) = Pump(fromEvents: null);
        dispatch(SDL_EventType.SDL_EVENT_WINDOW_ENTER_FULLSCREEN);
        Assert.Equal(OperatingSystem.IsMacOS(), window.FullscreenRequested);
    }
}

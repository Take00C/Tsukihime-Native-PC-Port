using System.Reflection;
using System.Runtime.CompilerServices;
using SDL;
using Tsuki.Platform;

namespace Tsuki.Tests.Synthetic.Platform;

/// <summary>
/// deck-gamemode-3: SDL's audio device events reach the game on Linux and macOS, where the run log names them (a Steam Deck's sleep, a headset); Windows passes
/// none of them, as in v0.1.6.
/// </summary>
public sealed class AudioDeviceEventTests
{
    private static List<PlatformEvent> Dispatch(bool? audioEvents, SDL_EventType type, uint device, bool recording)
    {
        var window = (GameWindow)RuntimeHelpers.GetUninitializedObject(typeof(GameWindow));
        EventPump pump = audioEvents is { } on ? new EventPump(window, null) { AudioDeviceEvents = on } : new EventPump(window, null);
        var events = new List<PlatformEvent>();
        SDL_Event ev = default;
        ev.adevice.type = type;
        ev.adevice.which = (SDL_AudioDeviceID)device;
        ev.adevice.recording = recording;
        typeof(EventPump).GetMethod("Handle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pump, [ev, events]);
        return events;
    }

    [Theory]
    [InlineData(SDL_EventType.SDL_EVENT_AUDIO_DEVICE_ADDED, PlatformEventKind.AudioDeviceAdded)]
    [InlineData(SDL_EventType.SDL_EVENT_AUDIO_DEVICE_REMOVED, PlatformEventKind.AudioDeviceRemoved)]
    [InlineData(SDL_EventType.SDL_EVENT_AUDIO_DEVICE_FORMAT_CHANGED, PlatformEventKind.AudioDeviceFormatChanged)]
    public void TheDeviceEventsCarryTheDeviceAndWhetherItRecords(SDL_EventType type, PlatformEventKind kind)
    {
        Assert.Equal([new PlatformEvent(kind, Device: 7)], Dispatch(true, type, 7, recording: false));
        Assert.Equal([new PlatformEvent(kind, Device: 8, Recording: true)], Dispatch(true, type, 8, recording: true));
        Assert.Empty(Dispatch(false, type, 7, recording: false));
    }

    [Fact]
    public void OnlyWindowsLeavesThemOutByDefault()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? 0 : 1, Dispatch(null, SDL_EventType.SDL_EVENT_AUDIO_DEVICE_REMOVED, 3, recording: false).Count);
    }
}

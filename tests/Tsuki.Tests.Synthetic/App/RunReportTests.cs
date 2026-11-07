using Tsuki.App.Play;
using Tsuki.Platform;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// The lines a Linux or macOS run log carries for the first reports from a Steam Deck (deck-gamemode-2/3/5, deck-evidence-2, critic-2): what they say, and when the
/// watches speak. Windows writes none of them (its log stays v0.1.6's), and nothing here changes what the game does.
/// </summary>
public sealed class RunReportTests
{
    private const long Second = 1000;

    [Fact]
    public void TheLinesAreLinuxAndMacOSOnly()
    {
        Assert.Equal(!OperatingSystem.IsWindows(), RunReport.Enabled);
    }

    [Fact]
    public void TheStartLinesNameSdlItsDriverAndSteamsVariables()
    {
        Assert.Equal("SDL 3.5.0 (/opt/Tsukihime/libSDL3.so), video driver x11", RunReport.Sdl("3.5.0", "/opt/Tsukihime/libSDL3.so", "x11"));
        Assert.Equal("SDL 3.5.0 (loaded by the system's search), video driver none", RunReport.Sdl("3.5.0", null, null));

        var deck = new Dictionary<string, string> { ["SteamDeck"] = "1", ["SteamGamepadUI"] = "1" };
        Assert.Equal("steam: SteamDeck=1, SteamOS unset, SteamGamepadUI=1", RunReport.Steam(n => deck.GetValueOrDefault(n)));
        Assert.Equal("steam: SteamDeck unset, SteamOS unset, SteamGamepadUI unset", RunReport.Steam(_ => null));
    }

    [Fact]
    public void ThePadsAtStartAndTheWindowStateAreNamed()
    {
        var pad = new GamepadInfo(7, "Steam Deck", "Xbox360", "A", "B", 0x28de, 0x1205);
        Assert.Equal("gamepad at start: Steam Deck (Xbox360, 28de:1205)", RunReport.PadAtStart(pad));
        var display = new DisplayInfo(0, 0, 1280, 800, "ANX7530 U 3\"", 0, 0, 1280, 800);
        Assert.Equal("window state: 1280 x 800 pixels, fullscreen on, display \"ANX7530 U 3\"\" 1280 x 800", RunReport.WindowState((1280, 800), true, display));
        Assert.Equal("window state: 1152 x 648 pixels, fullscreen off, display \"ANX7530 U 3\"\" 1280 x 800", RunReport.WindowState((1152, 648), false, display));
    }

    [Fact]
    public void PlaybackDeviceEventsAreNamedAndTheGamesOwnIsMarked()
    {
        var added = new PlatformEvent(PlatformEventKind.AudioDeviceAdded, Device: 3);
        Assert.Equal("audio device added: #3 \"Speakers\"", RunReport.AudioDevice(added, "Speakers", gameDevice: 9));
        Assert.Equal("audio device removed: #9 \"?\" (the game's device)", RunReport.AudioDevice(new PlatformEvent(PlatformEventKind.AudioDeviceRemoved, Device: 9), null, gameDevice: 9));
        Assert.Equal("audio device format changed: #4 \"HDMI\"", RunReport.AudioDevice(new PlatformEvent(PlatformEventKind.AudioDeviceFormatChanged, Device: 4), "HDMI", gameDevice: 0));
        Assert.Null(RunReport.AudioDevice(new PlatformEvent(PlatformEventKind.AudioDeviceAdded, Device: 5, Recording: true), "Microphone", gameDevice: 0));
        Assert.Null(RunReport.AudioDevice(new PlatformEvent(PlatformEventKind.FocusLost), null, gameDevice: 0));
    }

    [Fact]
    public void CallbacksThatStopForASecondAreSaidOnceAndTheirReturnToo()
    {
        var watch = new AudioCallbackWatch(Second);
        Assert.Null(watch.Note(0, 0));
        Assert.Null(watch.Note(10, 500));
        Assert.Null(watch.Note(20, 1400));
        Assert.Null(watch.Note(20, 2400));                                  // 1.0 s without a callback is not yet a stall
        Assert.Equal("audio: no device callback for 1 s (no sound reaches the device)", watch.Note(20, 2401));
        Assert.Null(watch.Note(20, 9000));                                  // once
        Assert.Equal("audio: the device's callbacks came back after 7.8 s", watch.Note(21, 9200));
        Assert.Null(watch.Note(22, 9300));
        Assert.Equal("audio: no device callback for 1 s (no sound reaches the device)", watch.Note(22, 10400));
    }

    [Fact]
    public void PresentsThatDoNotWaitForTheDisplayAreSaidOncePerRun()
    {
        var watch = new PresentRateWatch(Second);
        Assert.Null(watch.Note(0, 0, paced: true));
        Assert.Null(watch.Note(120, 1000, paced: true));
        Assert.Null(watch.Note(180, 2000, paced: true));                    // 90 a second: an OLED Deck's panel
        Assert.Null(watch.Note(180, 2100, paced: false));                   // not paced: the loop waits by the clock, nothing to measure
        Assert.Null(watch.Note(5000, 3100, paced: true));                   // measuring starts again
        Assert.Null(watch.Note(6000, 4000, paced: true));
        Assert.Equal("vsync: 1000 presents a second with swap interval 1: the swaps do not wait for the display", watch.Note(7000, 5100, paced: true));
        Assert.Null(watch.Note(99000, 9000, paced: true));
    }
}

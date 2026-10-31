using Tsuki.App.Services;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>Package K21: the port's switches (Mute in Background, Copy Text to Clipboard) start from settings.ini, save to it next to the other keys and announce a change.</summary>
public sealed class PortSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-k21-port-" + Guid.NewGuid().ToString("N"));

    public PortSettingsTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    private string SettingsPath => Path.Combine(_dir, AppSettings.FileName);

    [Fact]
    public void BothSwitchesAreOffUnlessTheFileSaysOtherwise()
    {
        var fresh = new PortSettings(SettingsPath, new AppSettingsData());
        Assert.False(fresh.MuteInBackground);
        Assert.False(fresh.CopyTextToClipboard);

        var set = new PortSettings(SettingsPath, new AppSettingsData(MuteInBackground: true, CopyText: true));
        Assert.True(set.MuteInBackground);
        Assert.True(set.CopyTextToClipboard);
    }

    [Fact]
    public void TheHookerOutputIsOnAndTheWebSocketOffUnlessTheFileSaysOtherwiseAndThePortDefaultsTo6677()
    {
        // package K23
        var fresh = new PortSettings(SettingsPath, new AppSettingsData());
        Assert.True(fresh.TextHookerOutput);
        Assert.False(fresh.TextWebSocket);
        Assert.Equal(6677, fresh.TextWebSocketPort);

        var set = new PortSettings(SettingsPath, new AppSettingsData(TextHooker: false, TextWebSocket: true, TextWebSocketPort: 7000));
        Assert.False(set.TextHookerOutput);
        Assert.True(set.TextWebSocket);
        Assert.Equal(7000, set.TextWebSocketPort);

        var log = new List<string>();
        var port = new PortSettings(SettingsPath, new AppSettingsData(), log.Add);
        int changes = 0;
        port.Changed += () => changes++;
        port.TextHookerOutput = false;
        port.TextWebSocket = true;
        port.TextWebSocket = true;                                                       // the same value again: nothing to do
        Assert.Equal(2, changes);
        AppSettingsData file = AppSettings.Load(SettingsPath);
        Assert.Equal((false, true), (file.TextHooker, file.TextWebSocket));
        Assert.Null(file.TextWebSocketPort);                                              // the port is the file's business, never written by the switches
        Assert.Contains("text_hooker off saved to settings.ini", log);
        Assert.Contains("text_websocket on saved to settings.ini", log);
        var next = new PortSettings(SettingsPath, file);
        Assert.False(next.TextHookerOutput);
        Assert.True(next.TextWebSocket);
    }

    [Fact]
    public void TheScreenshotNoticeIsOnUnlessTheFileSaysOffAndTheSwitchSavesTheKey()
    {
        // package K24: Config > System > Screenshot Notice; Off saves the picture without the toast
        var fresh = new PortSettings(SettingsPath, new AppSettingsData());
        Assert.True(fresh.ScreenshotNotice);
        Assert.False(new PortSettings(SettingsPath, new AppSettingsData(ScreenshotNotice: false)).ScreenshotNotice);
        Assert.True(new PortSettings(SettingsPath, new AppSettingsData(ScreenshotNotice: true)).ScreenshotNotice);

        AppSettings.Save(SettingsPath, new AppSettingsData(PromptSetting.Keyboard, TextWebSocketPort: 7000));
        var log = new List<string>();
        var port = new PortSettings(SettingsPath, AppSettings.Load(SettingsPath), log.Add);
        int changes = 0;
        port.Changed += () => changes++;
        port.ScreenshotNotice = true;                                                    // already on: nothing to do
        Assert.Equal(0, changes);
        Assert.Empty(log);
        port.ScreenshotNotice = false;
        Assert.Equal(1, changes);
        Assert.Contains("screenshot_notice off saved to settings.ini", log);
        AppSettingsData file = AppSettings.Load(SettingsPath);
        Assert.Equal(false, file.ScreenshotNotice);
        Assert.Equal(PromptSetting.Keyboard, file.Prompts);                            // the other keys stay
        Assert.Equal(7000, file.TextWebSocketPort);
        Assert.False(new PortSettings(SettingsPath, file).ScreenshotNotice);            // the next start reads it back
        port.ScreenshotNotice = true;
        Assert.Equal(true, AppSettings.Load(SettingsPath).ScreenshotNotice);
        Assert.Contains("screenshot_notice on saved to settings.ini", log);
    }

    [Fact]
    public void AChangeIsSavedNextToTheOtherKeysAndAnnouncedOnce()
    {
        AppSettings.Save(SettingsPath, new AppSettingsData(PromptSetting.Xbox, VSync: false, WindowWidth: 1600, WindowHeight: 900));
        var log = new List<string>();
        var port = new PortSettings(SettingsPath, AppSettings.Load(SettingsPath), log.Add);
        int changes = 0;
        port.Changed += () => changes++;

        port.MuteInBackground = true;
        Assert.Equal(1, changes);
        AppSettingsData file = AppSettings.Load(SettingsPath);
        Assert.Equal(true, file.MuteInBackground);
        Assert.Null(file.CopyText);
        Assert.Equal(PromptSetting.Xbox, file.Prompts);                                // the other keys stay
        Assert.Equal(false, file.VSync);
        Assert.Equal((1600, 900), file.WindowSize);
        Assert.Contains("mute_in_background on saved to settings.ini", log);

        port.MuteInBackground = true;                                                  // the same value again: nothing to do
        Assert.Equal(1, changes);

        port.CopyTextToClipboard = true;
        port.MuteInBackground = false;
        Assert.Equal(3, changes);
        file = AppSettings.Load(SettingsPath);
        Assert.Equal(false, file.MuteInBackground);
        Assert.Equal(true, file.CopyText);
        Assert.Contains("copy_text on saved to settings.ini", log);
        Assert.Contains("mute_in_background off saved to settings.ini", log);

        // the next start reads them back
        var next = new PortSettings(SettingsPath, AppSettings.Load(SettingsPath));
        Assert.False(next.MuteInBackground);
        Assert.True(next.CopyTextToClipboard);
    }

    [Fact]
    public void WithoutASettingsFileTheSwitchStillChangesAndNothingIsWritten()
    {
        var log = new List<string>();
        var port = new PortSettings(null, new AppSettingsData(), log.Add);
        int changes = 0;
        port.Changed += () => changes++;
        port.CopyTextToClipboard = true;
        Assert.True(port.CopyTextToClipboard);
        Assert.Equal(1, changes);
        Assert.Contains("copy_text on (not saved: no settings file)", log);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void TheTwoKeysRoundTripThroughTheFileAndAreUnderstoodInEverySpelling()
    {
        var data = new AppSettingsData(MuteInBackground: true, CopyText: false);
        string text = AppSettings.Format(data);
        Assert.Contains("mute_in_background=on", text, StringComparison.Ordinal);
        Assert.Contains("copy_text=off", text, StringComparison.Ordinal);
        Assert.Equal(data, AppSettings.Parse(text));
        Assert.Equal(true, AppSettings.Parse("mute_in_background = 1").MuteInBackground);
        Assert.Equal(true, AppSettings.Parse("COPY_TEXT=true").CopyText);
        Assert.Null(AppSettings.Parse("copy_text=sometimes").CopyText);
        Assert.Null(AppSettings.Parse("prompts=auto").MuteInBackground);
    }
}

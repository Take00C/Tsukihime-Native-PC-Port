using Tsuki.App;
using Tsuki.App.Services;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>Package K9: the small settings file next to the save file, and the prompt state that keeps its one setting in it.</summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-settings-" + Guid.NewGuid().ToString("N"));

    public AppSettingsTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData("prompts=auto", PromptSetting.Auto)]
    [InlineData("prompts=keyboard", PromptSetting.Keyboard)]
    [InlineData("prompts=playstation", PromptSetting.PlayStation)]
    [InlineData("prompts=xbox", PromptSetting.Xbox)]
    [InlineData("  Prompts =  XBOX  ", PromptSetting.Xbox)]
    [InlineData("prompts=ps5", PromptSetting.PlayStation)]
    [InlineData("# prompts=xbox\nprompts=keyboard", PromptSetting.Keyboard)]
    [InlineData("prompts=nonsense", PromptSetting.Auto)]
    [InlineData("other=1\nprompts=xbox\nmore=2", PromptSetting.Xbox)]
    [InlineData("", PromptSetting.Auto)]
    public void TheFileIsReadLineByLineAndABadValueKeepsTheDefault(string text, PromptSetting expected)
    {
        Assert.Equal(expected, AppSettings.Parse(text).Prompts);
    }

    [Fact]
    public void FormatAndParseAreInverse()
    {
        foreach (PromptSetting s in Enum.GetValues<PromptSetting>())
        {
            Assert.Equal(s, AppSettings.Parse(AppSettings.Format(new AppSettingsData(s))).Prompts);
        }
    }

    [Fact]
    public void ASettingIsSavedWhenItChangesAndReadBackOnTheNextRun()
    {
        string path = Path.Combine(_dir, AppSettings.FileName);
        var first = new PromptState(path);
        Assert.Equal(PromptSetting.Auto, first.Setting);
        Assert.False(File.Exists(path));                      // nothing is written until the player changes something
        first.Setting = PromptSetting.Xbox;
        Assert.True(File.Exists(path));
        Assert.Contains("prompts=xbox", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(PromptSetting.Xbox, new PromptState(path).Setting);

        first.Setting = PromptSetting.Auto;
        Assert.Equal(PromptSetting.Auto, new PromptState(path).Setting);
    }

    [Theory]
    [InlineData("auto", PromptSetting.Auto)]
    [InlineData("keyboard", PromptSetting.Keyboard)]
    [InlineData("PlayStation", PromptSetting.PlayStation)]
    [InlineData("xbox", PromptSetting.Xbox)]
    public void ThePromptsOptionOfTheAppIsReadAndABadNameIsAnError(string name, PromptSetting expected)
    {
        Assert.True(AppArgs.TryParse(["--prompts", name], out AppArgs? args, out string? error));
        Assert.Null(error);
        Assert.Equal(expected, args!.Prompts);
        Assert.False(AppArgs.TryParse(["--prompts", "gamepad"], out _, out string? bad));
        Assert.Contains("--prompts", bad, StringComparison.Ordinal);
        Assert.True(AppArgs.TryParse([], out AppArgs? none, out _));
        Assert.Null(none!.Prompts);
    }

    [Fact]
    public void ARunOverrideReplacesTheSavedSettingButIsNotSavedUntilTheMenuChangesIt()
    {
        string path = Path.Combine(_dir, AppSettings.FileName);
        AppSettings.Save(path, new AppSettingsData(PromptSetting.PlayStation));
        var state = new PromptState(path, PromptSetting.Keyboard);
        Assert.Equal(PromptSetting.Keyboard, state.Setting);
        Assert.Equal(PromptSetting.PlayStation, AppSettings.Load(path).Prompts);
        state.Setting = PromptSetting.Xbox;
        Assert.Equal(PromptSetting.Xbox, AppSettings.Load(path).Prompts);
    }

    [Fact]
    public void WithoutAPathNothingIsReadOrWrittenAndAnUnwritablePathIsLoggedNotThrown()
    {
        var quiet = new PromptState(null) { Setting = PromptSetting.Xbox };
        Assert.Equal(PromptSetting.Xbox, quiet.Setting);
        Assert.False(AppSettings.Save(null, default));
        Assert.Equal(default, AppSettings.Load(null));

        var logged = new List<string>();
        var broken = new PromptState(Path.Combine(_dir, "no-such-folder", AppSettings.FileName), null, logged.Add);
        broken.Setting = PromptSetting.Keyboard;
        Assert.Equal(PromptSetting.Keyboard, broken.Setting);
        Assert.Single(logged);
    }
}

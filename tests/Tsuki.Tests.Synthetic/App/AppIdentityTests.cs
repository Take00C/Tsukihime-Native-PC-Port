using Tsuki.App;
using Tsuki.App.Play;
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>Package K20: the program's name, the language of its start-up messages, the embedded icon and the guard names.</summary>
public sealed class AppIdentityTests
{
    private static AppArgs Parse(params string[] args)
    {
        Assert.True(AppArgs.TryParse(args, out AppArgs? parsed, out string? error), error);
        return parsed!;
    }

    private static SaveStore StoreWithLanguage(short language)
    {
        var store = new SaveStore();
        store.SystemData.Block.Magic = SystemBlock.MagicValue;
        store.SystemData.Config.CopyFrom(ConfigBlock.CreateBootDefaults());
        store.SystemData.Config.Language = language;
        return store;
    }

    [Fact]
    public void TheStartupLanguageIsTheCommandLinesThenTheLaunchersThenTheProfilesThenWindows()
    {
        var pendingZt = new AppSettingsData(PromptSetting.Auto, GameLanguage: Language.Zt);
        var nothing = new AppSettingsData(PromptSetting.Auto);
        Assert.Equal(Language.Ja, AppIdentity.StartupLanguage(Parse("--lang", "ja"), pendingZt, StoreWithLanguage(2)));
        Assert.Equal(Language.Zt, AppIdentity.StartupLanguage(Parse(), pendingZt, StoreWithLanguage(2)));
        Assert.Equal(Language.Zc, AppIdentity.StartupLanguage(Parse(), nothing, StoreWithLanguage(2)));
        Assert.Equal(Language.En, AppIdentity.StartupLanguage(Parse(), nothing, StoreWithLanguage(1)));
        // a fresh profile (language -1) and no hint: the Windows display language, mapped to one of the four
        Language windows = PortText.FromCultureName(System.Globalization.CultureInfo.CurrentUICulture.Name);
        Assert.Equal(windows, AppIdentity.StartupLanguage(Parse(), nothing, StoreWithLanguage(-1)));
        Assert.Equal(windows, AppIdentity.StartupLanguage(Parse(), nothing, null));
        Assert.Equal(windows, AppIdentity.StartupLanguage(Parse(), new AppSettingsData(PromptSetting.Auto, GameLanguage: Language.Ko), null));   // Korean is not selectable
    }

    [Fact]
    public void TheWindowTitleWithoutAFolderIsTheAnalysedCopysNameInTheLanguage()
    {
        Assert.Equal("月姫 -A piece of blue glass moon-", AppIdentity.WindowTitle(null, Language.Ja));
        Assert.Equal("TSUKIHIME -A piece of blue glass moon-", AppIdentity.WindowTitle(null, Language.En));
        Assert.Equal("月姬 -A piece of blue glass moon-", AppIdentity.WindowTitle(null, Language.Zc));
        Assert.Equal("月姬 -A piece of blue glass moon-", AppIdentity.WindowTitle(null, Language.Zt));
        Assert.Equal("Tsukihime", AppIdentity.ProductName);
        Assert.Equal("Tsukihime.exe", AppIdentity.ExecutableName);
        Assert.StartsWith("usage: Tsukihime ", AppArgs.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmbeddedIconIsA64PixelPictureWithAnOpaqueMoonOnAnIndigoPlate()
    {
        byte[] icon = AppIdentity.EmbeddedIcon()!;
        Assert.NotNull(icon);
        Assert.Equal(AppIdentity.IconSize * AppIdentity.IconSize * 4, icon.Length);
        int opaque = 0;
        int blueish = 0;
        int moon = 0;
        for (int i = 0; i < icon.Length; i += 4)
        {
            if (icon[i + 3] > 250)
            {
                opaque++;
                if (icon[i + 2] > icon[i])
                {
                    blueish++;
                }

                if (icon[i + 2] > 200 && icon[i + 1] > 120)
                {
                    moon++;
                }
            }
        }

        Assert.InRange(opaque, 3000, 4096);                        // the rounded plate covers most of the square
        Assert.InRange(blueish, 2800, 4096);                       // indigo plate, blue moon, pale sparkle: blue above red everywhere
        Assert.InRange(moon, 600, 2000);                           // the bright crescent is a good part of the picture
        Assert.Equal(0, icon[3]);                                  // the corner outside the rounded square is transparent
    }

    [Fact]
    public void TheGuardMutexIsNamedAfterTheProgramAndTheProfile()
    {
        string a = SingleInstance.MutexName(@"D:\Profiles\x\AppData\Roaming\Tsuki");
        string b = SingleInstance.MutexName(@"d:\profiles\X\appdata\roaming\tsuki\");
        Assert.StartsWith(@"Local\Tsukihime.", a, StringComparison.Ordinal);
        Assert.Equal(a, b);                                                                      // the same folder spelt differently
        Assert.NotEqual(a, SingleInstance.MutexName(@"D:\x\save"));
    }
}

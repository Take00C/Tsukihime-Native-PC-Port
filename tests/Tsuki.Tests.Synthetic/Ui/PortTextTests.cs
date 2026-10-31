using System.Text.RegularExpressions;
using Tsuki.Engine.Contracts;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>Package K20: every string the port adds exists in the four selectable languages, keeps its placeholders, and never shows the short project name to the player.</summary>
public sealed partial class PortTextTests
{
    private static readonly Language[] Languages = [Language.Ja, Language.En, Language.Zc, Language.Zt];

    [GeneratedRegex(@"\{(\d)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\bTsuki\b(?!hime)")]
    private static partial Regex ShortName();

    [Fact]
    public void EveryStringExistsInTheFourLanguagesWithTheSamePlaceholders()
    {
        foreach (PortString id in Enum.GetValues<PortString>())
        {
            string en = PortText.Get(id, Language.En);
            Assert.False(string.IsNullOrWhiteSpace(en), $"{id} en");
            var slots = Placeholder().Matches(en).Select(m => m.Value).Order().ToArray();
            foreach (Language lang in Languages)
            {
                string text = PortText.Get(id, lang);
                Assert.False(string.IsNullOrWhiteSpace(text), $"{id} {lang}");
                Assert.Equal(slots, Placeholder().Matches(text).Select(m => m.Value).Order().ToArray());
                Assert.False(ShortName().IsMatch(text), $"{id} {lang} shows the project name: {text}");
            }

            // the translations are translations (the product name and the sizes are the same everywhere by design)
            if (id != PortString.ProductName)
            {
                Assert.NotEqual(PortText.Get(id, Language.Ja), en);
                Assert.NotEqual(PortText.Get(id, Language.Zc), en);
            }
        }

        Assert.Equal(PortText.Get(PortString.AlreadyRunning, Language.En), PortText.Get(PortString.AlreadyRunning, Language.Ko));       // Korean has no UI: English
    }

    [Fact]
    public void FormatFillsThePlaceholdersInEveryLanguage()
    {
        Assert.Equal("The game folder D:\\g no longer exists.", PortText.Format(PortString.FolderGone, Language.En, @"D:\g"));
        Assert.Equal("ゲームフォルダ D:\\g が見つかりません。", PortText.Format(PortString.FolderGone, Language.Ja, @"D:\g"));
        Assert.Contains("eboot.bin", PortText.Format(PortString.FolderIncomplete, Language.Zt, "eboot.bin", @"D:\g"), StringComparison.Ordinal);
        Assert.Contains("Tsukihime.exe", PortText.Format(PortString.FfmpegMissing, Language.Zc, "avutil-61.dll"), StringComparison.Ordinal);
        Assert.StartsWith("Details: ", PortText.Format(PortString.Details, Language.En, "x"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDialogButtonsAreTranslatedWhereTheSheetIsNot()
    {
        Assert.Equal(("はい", "いいえ"), (PortText.Get(PortString.DialogYes, Language.Ja), PortText.Get(PortString.DialogNo, Language.Ja)));
        Assert.Equal(("Yes", "No"), (PortText.Get(PortString.DialogYes, Language.En), PortText.Get(PortString.DialogNo, Language.En)));
        Assert.Equal(("是", "否"), (PortText.Get(PortString.DialogYes, Language.Zc), PortText.Get(PortString.DialogNo, Language.Zc)));
    }

    [Theory]
    [InlineData("ja-JP", Language.Ja)]
    [InlineData("ja", Language.Ja)]
    [InlineData("zh-CN", Language.Zc)]
    [InlineData("zh-Hans-CN", Language.Zc)]
    [InlineData("zh-SG", Language.Zc)]
    [InlineData("zh-TW", Language.Zt)]
    [InlineData("zh-Hant-TW", Language.Zt)]
    [InlineData("zh-HK", Language.Zt)]
    [InlineData("en-US", Language.En)]
    [InlineData("de-DE", Language.En)]
    [InlineData("", Language.En)]
    [InlineData(null, Language.En)]
    public void TheWindowsDisplayLanguageMapsToOneOfTheFour(string? culture, Language expected)
    {
        Assert.Equal(expected, PortText.FromCultureName(culture));
    }
}

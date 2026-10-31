using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Tests.Synthetic.Launcher;

namespace Tsuki.Tests.Synthetic.Data;

/// <summary>Package K20: the window title comes from the user's own param.sfo, by the PS4's language numbers, with the analysed copy's words as the fallback.</summary>
public sealed class GameTitlesTests
{
    private static readonly byte[] Retail = GameFolderTests.Sfo(
        ("APP_VER", "01.00"),
        ("CONTENT_ID", "HP3279-CUSA47999_00-GAME000TSUKIHIME"),
        ("TITLE", "TSUKIHIME A piece of blue glass moon"),
        ("TITLE_00", "月姫 -A piece of blue glass moon-"),
        ("TITLE_10", "月姬 -A piece of blue glass moon-"),
        ("TITLE_11", "月姬 -A piece of blue glass moon-"));

    [Fact]
    public void TheKeysFollowThePs4LanguageNumbers()
    {
        Assert.Equal("TITLE_00", GameTitles.Key(Language.Ja));
        Assert.Equal("TITLE_01", GameTitles.Key(Language.En));
        Assert.Equal("TITLE_11", GameTitles.Key(Language.Zc));          // 11 = Simplified Chinese
        Assert.Equal("TITLE_10", GameTitles.Key(Language.Zt));          // 10 = Traditional Chinese
        Assert.Equal("TITLE_01", GameTitles.Key(Language.Ko));
    }

    [Fact]
    public void TheRetailFileGivesEachLanguageItsOwnTitleAndEnglishThePlainOne()
    {
        ParamSfo sfo = ParamSfo.TryParse(Retail)!;
        Assert.Equal("月姫 -A piece of blue glass moon-", GameTitles.Of(sfo, Language.Ja));
        Assert.Equal("TSUKIHIME A piece of blue glass moon", GameTitles.Of(sfo, Language.En));     // no TITLE_01 in the retail file: the plain TITLE
        Assert.Equal("月姬 -A piece of blue glass moon-", GameTitles.Of(sfo, Language.Zc));
        Assert.Equal("月姬 -A piece of blue glass moon-", GameTitles.Of(sfo, Language.Zt));
    }

    [Fact]
    public void AMissingOrBlankKeyFallsBackToTheAnalysedCopysWordsNotToTheEnglishLine()
    {
        ParamSfo sfo = ParamSfo.TryParse(GameFolderTests.Sfo(("TITLE", "Something else"), ("TITLE_00", "  ")))!;
        Assert.Equal("月姫 -A piece of blue glass moon-", GameTitles.Of(sfo, Language.Ja));           // blank counts as missing, and Japanese never takes the English TITLE
        Assert.Equal("月姬 -A piece of blue glass moon-", GameTitles.Of(sfo, Language.Zt));
        Assert.Equal("Something else", GameTitles.Of(sfo, Language.En));
        Assert.Equal("TSUKIHIME -A piece of blue glass moon-", GameTitles.Of(null, Language.En));
        Assert.Equal(GameTitles.Fallback(Language.Ja), GameTitles.Of(null, Language.Ja));
        Assert.Equal("Tsukihime", GameTitles.ProductName);
        foreach (Language l in new[] { Language.Ja, Language.En, Language.Zc, Language.Zt })
        {
            Assert.False(string.IsNullOrWhiteSpace(GameTitles.Fallback(l)));
            Assert.DoesNotContain("Tsuki ", GameTitles.Fallback(l), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AFolderIsReadThroughItsSceSysAndAFolderWithoutOneFallsBack()
    {
        string root = Path.Combine(Path.GetTempPath(), "tsuki-k20-titles-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sce_sys"));
            File.WriteAllBytes(Path.Combine(root, "sce_sys", "param.sfo"), Retail);
            Assert.Equal("月姫 -A piece of blue glass moon-", GameTitles.ForFolder(root, Language.Ja));
            Assert.Equal("TSUKIHIME A piece of blue glass moon", GameTitles.ForFolder(root, Language.En));
            Assert.Equal(GameTitles.Fallback(Language.Zc), GameTitles.ForFolder(Path.Combine(root, "nowhere"), Language.Zc));
            Assert.Equal(GameTitles.Fallback(Language.En), GameTitles.ForFolder(null, Language.En));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

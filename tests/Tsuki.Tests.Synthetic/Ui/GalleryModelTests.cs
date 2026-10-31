using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>The unlock rules of the gallery, the music player, the movie list and the ending list (docs/formats/gallery.md 2.3, 4.3, 6.2, 7, 8) on small fake tables.</summary>
public sealed class GalleryModelTests
{
    private static readonly FakeGalleryTables Tables = new();

    private static GalleryModel Model => GalleryModel.For(Tables);

    private static PreviewProgress Progress() => new();

    [Fact]
    public void NothingIsUnlockedWithoutProgressExceptTheFirstCategory()
    {
        int[][] unlocked = Model.Unlocked(Progress());
        Assert.All(unlocked, g => Assert.Empty(g));
    }

    [Fact]
    public void ASeenPictureUnlocksItsEntryInTheOpenCategories()
    {
        PreviewProgress p = Progress();
        p.Pictures.Add(1);
        p.Pictures.Add(3);
        int[][] unlocked = Model.Unlocked(p);
        Assert.Equal([0], unlocked[0]);
        Assert.Equal([1], unlocked[1]);
        Assert.Empty(unlocked[2]);
    }

    [Fact]
    public void ACategoryOpensWhenAnyOfItsScenesWasStarted()
    {
        PreviewProgress p = Progress();
        p.Pictures.Add(5);
        Assert.Empty(Model.Unlocked(p)[2]);
        p.Seen.Add(2);
        Assert.Equal([0], Model.Unlocked(p)[2]);
        Assert.True(Model.CategoryOpen(0, p));
        Assert.False(Model.CategoryOpen(2, p));
        p.Seen.Add(4);
        Assert.True(Model.CategoryOpen(2, p));
    }

    [Fact]
    public void AGalleryOnlyPictureUnlocksThroughTheLastQaSceneInOpenCategoriesOnly()
    {
        PreviewProgress p = Progress();
        p.Done.Add(GalleryModel.AllCgScene);
        int[][] unlocked = Model.Unlocked(p);
        Assert.Equal([0], unlocked[3]);
        Assert.Empty(unlocked[2]);
        Assert.Empty(unlocked[5]);
        Assert.Equal([0, 1, 2], unlocked[1]);
    }

    [Fact]
    public void AnAliasUnlocksWithAnyOfItsSizeLettersOrTheFirstResolvableAlias()
    {
        PreviewProgress p = Progress();
        p.Pictures.Add(20);
        Assert.Equal([0], Model.Unlocked(p)[4]);
        p.Pictures.Clear();
        p.Pictures.Add(21);
        Assert.Equal([0], Model.Unlocked(p)[4]);
        p.Pictures.Clear();
        p.Pictures.Add(22);
        Assert.Empty(Model.Unlocked(p)[4]);
    }

    [Fact]
    public void ASeenAliasPictureOfAShortAliasNeverUnlocks()
    {
        PreviewProgress p = Progress();
        p.Seen.Add(3);
        p.Pictures.Add(0);
        Assert.Empty(Model.Unlocked(p)[5]);
    }

    [Fact]
    public void TheCgModelKnowsWhichPicturesStackOnePerLanguage()
    {
        Assert.True(Model.Groups[2][0].LanguageStrip);
        Assert.False(Model.Groups[1][0].LanguageStrip);
        Assert.Equal(5, Model.Groups[2][0].DirectSeen);
        Assert.Equal(-1, Model.Groups[3][0].DirectSeen);
    }

    [Fact]
    public void ATrackUnlocksWhenItsBgmOrAVariantWasHeard()
    {
        PreviewProgress p = Progress();
        Assert.False(Model.TrackUnlocked(0, p));
        p.Bgm.Add(1);
        Assert.True(Model.TrackUnlocked(0, p));
        Assert.False(Model.TrackUnlocked(1, p));
        p.Bgm.Add(50);
        Assert.True(Model.TrackUnlocked(1, p));
    }

    [Fact]
    public void TheSpecialTracksFollowTheirMoviesAndScenes()
    {
        PreviewProgress p = Progress();
        Assert.False(Model.TrackUnlocked(2, p));
        p.Movies.Add(0);
        Assert.True(Model.TrackUnlocked(2, p));
        Assert.False(Model.TrackUnlocked(3, p));
        p.Seen.Add(560);
        Assert.True(Model.TrackUnlocked(3, p));
        Assert.False(Model.TrackUnlocked(4, p));
        p.Movies.Add(1);
        Assert.True(Model.TrackUnlocked(4, p));
        Assert.False(Model.TrackUnlocked(5, p));
        p.Seen.Add(562);
        Assert.True(Model.TrackUnlocked(5, p));
    }

    [Fact]
    public void AnEntryUnlockedByAVariantGetsItsOwnHeardBit()
    {
        var s = GalleryKit.Services();
        s.Progress.Bgm.Add(50);
        bool[] ok = Model.TracksUnlocked(s, s);
        Assert.True(ok[1]);
        Assert.Contains("bgm heard 2", s.Calls);
        Assert.DoesNotContain("bgm heard 1", s.Calls);
    }

    [Fact]
    public void OnlyThePrologueMovieIsAlwaysThere()
    {
        PreviewProgress p = Progress();
        Assert.True(GalleryModel.MovieUnlocked(0, p));
        Assert.False(GalleryModel.MovieUnlocked(1, p));
        Assert.False(GalleryModel.MovieUnlocked(2, p));
        p.Movies.Add(1);
        Assert.True(GalleryModel.MovieUnlocked(2, p));
        Assert.Equal([4, 5, 6], GalleryModel.Movies.Select(m => m.Movie));
    }

    [Fact]
    public void AnEndingNeedsOneOfItsTwoScenesAndTheLastNeedsEveryOther()
    {
        PreviewProgress p = Progress();
        Assert.False(Model.EndingUnlocked(0, p));
        p.Done.Add(100);
        Assert.True(Model.EndingUnlocked(0, p));
        Assert.False(Model.EndingUnlocked(5, p));
        p.Done.Add(300);
        Assert.True(Model.EndingUnlocked(5, p));
        Assert.False(Model.EndingUnlocked(24, p));
        for (int i = 0; i < 24; i++)
        {
            p.Done.Add(100 + i);
        }

        Assert.True(Model.AllEndings(p));
        Assert.True(Model.EndingUnlocked(24, p));
        Assert.True(Model.IsEndingNode(557));
        Assert.False(Model.IsEndingNode(100));
    }
}

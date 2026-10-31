using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>The gallery hub and its modes as state machines (docs/formats/gallery.md 1, 3; knife <c>cGallery</c>) over fake tables and an in-memory game.</summary>
public sealed class GalleryScreenTests
{
    private static void Press(UiRoot root, PadButtons b) => UiTestKit.Press(root, b);

    private static void Run(UiRoot root, int frames) => UiTestKit.Run(root, frames);

    private static void ToMode(UiRoot root, int downs)
    {
        for (int i = 0; i < downs; i++)
        {
            Press(root, PadButtons.Down);
        }

        Press(root, PadButtons.Decide);
        Run(root, 70);
    }

    // ---- the hub ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ItOpensOnTheCgGalleryWithTheFocusOnTheButtonsAndFadesIn()
    {
        PreviewServices s = GalleryKit.Services();
        UiRoot root = GalleryKit.Root(s);
        var gallery = root.Push(new GalleryScreen());
        Assert.Equal(0f, gallery.Fade);
        Run(root, 40);
        Assert.Equal(1f, gallery.Fade);
        Assert.Equal(GalleryMode.Cg, gallery.Mode);
        Assert.True(gallery.SideFocus);
        Assert.Equal(0, gallery.Tab);
        Assert.IsType<GalleryCgPanel>(gallery.Panel);
        Assert.True(gallery.Opaque);
    }

    [Fact]
    public void TheButtonCursorWrapsOnAFreshPressOnly()
    {
        (UiRoot root, _, GalleryScreen gallery) = GalleryKit.OpenGallery();
        for (int i = 0; i < 5; i++)
        {
            Press(root, PadButtons.Down);
        }

        Assert.Equal(5, gallery.Tab);
        Press(root, PadButtons.Down);
        Assert.Equal(0, gallery.Tab);
        Press(root, PadButtons.Up);
        Assert.Equal(5, gallery.Tab);

        // a held repeat (no new press) stops at the end instead of wrapping
        Press(root, PadButtons.Up);
        Assert.Equal(4, gallery.Tab);
        for (int i = 0; i < 5; i++)
        {
            root.Step(new InputFrame { Frame = root.Context.Frame + 1, Repeated = PadButtons.Down, Held = PadButtons.Down });
        }

        Assert.Equal(5, gallery.Tab);
    }

    [Fact]
    public void DecideOnAnotherModeCrossfadesToItAndTakesTheContentFocus()
    {
        (UiRoot root, PreviewServices s, GalleryScreen gallery) = GalleryKit.OpenGallery();
        Press(root, PadButtons.Down);
        Press(root, PadButtons.Decide);
        Assert.True(gallery.Switching);
        Assert.IsType<GalleryCgPanel>(gallery.Panel);
        Run(root, 70);
        Assert.False(gallery.Switching);
        Assert.Equal(GalleryMode.Music, gallery.Mode);
        Assert.IsType<GalleryMusicPanel>(gallery.Panel);
        Assert.False(gallery.SideFocus);
        Assert.Contains("music stop", s.Calls);
    }

    [Fact]
    public void RightGivesTheContentTheFocusAndCancelTakesItBack()
    {
        (UiRoot root, _, GalleryScreen gallery) = GalleryKit.OpenGallery(s => s.Progress.Pictures.Add(1));
        Press(root, PadButtons.Right);
        Assert.False(gallery.SideFocus);
        Press(root, PadButtons.Cancel);
        Assert.True(gallery.SideFocus);
        Assert.Equal(0, gallery.Tab);
        Assert.True(root.Active);
    }

    [Fact]
    public void CancelOnTheButtonsLeavesTheGalleryAndRemembersTheMode()
    {
        (UiRoot root, _, GalleryScreen gallery) = GalleryKit.OpenGallery();
        ToMode(root, 3);
        Assert.Equal(GalleryMode.Movies, gallery.Mode);
        Press(root, PadButtons.Cancel);
        Press(root, PadButtons.Cancel);
        Run(root, 60);
        Assert.False(root.Active);
        Assert.Equal((int)GalleryMode.Movies, root.Context.Gallery.Mode);
    }

    [Fact]
    public void TheNextVisitOpensOnTheModeItClosedWith()
    {
        PreviewServices s = GalleryKit.Services();
        UiRoot root = GalleryKit.Root(s);
        root.Context.Gallery.Mode = (int)GalleryMode.Endings;
        var gallery = root.Push(new GalleryScreen());
        Run(root, 40);
        Assert.Equal(GalleryMode.Endings, gallery.Mode);
        Assert.Equal((int)GalleryMode.Endings, gallery.Tab);
        Assert.Equal(-1, root.Context.Gallery.Mode);
    }

    [Fact]
    public void TheTitleButtonClosesTheGallery()
    {
        (UiRoot root, _, _) = GalleryKit.OpenGallery();
        for (int i = 0; i < 5; i++)
        {
            Press(root, PadButtons.Down);
        }

        Press(root, PadButtons.Decide);
        Run(root, 60);
        Assert.False(root.Active);
    }

    [Fact]
    public void TheFlowchartButtonNeedsTheFirstSceneStarted()
    {
        (UiRoot root, PreviewServices s, _) = GalleryKit.OpenGallery();
        Press(root, PadButtons.Down);
        Press(root, PadButtons.Down);
        Press(root, PadButtons.Decide);
        Assert.IsType<MessagePopup>(root.Top);
        Assert.Contains("sound Cursor", s.Calls);

        (UiRoot root2, PreviewServices s2, _) = GalleryKit.OpenGallery(p => GalleryKit.Read(p, 1));
        Press(root2, PadButtons.Down);
        Press(root2, PadButtons.Down);
        Press(root2, PadButtons.Decide);
        var chart = Assert.IsType<FlowchartScreen>(root2.Top);
        Assert.True(chart.Gallery);
        Assert.Contains("sound Decide", s2.Calls);
    }

    [Fact]
    public void ThePointerHoversAndClicksTheButtons()
    {
        (UiRoot root, _, GalleryScreen gallery) = GalleryKit.OpenGallery();
        UiTestKit.Point(root, 200f, 160f + (3 * 80f) + 40f);
        Assert.Equal(3, gallery.Tab);
        Assert.True(gallery.SideFocus);
        UiTestKit.Point(root, 200f, 160f + (4 * 80f) + 40f, click: true);
        Run(root, 70);
        Assert.Equal(GalleryMode.Endings, gallery.Mode);
    }

    // ---- CG ---------------------------------------------------------------------------------------------------------------------------------------

    private static (UiRoot Root, PreviewServices S, GalleryScreen Gallery, GalleryCgPanel Cg) OpenCg()
    {
        (UiRoot root, PreviewServices s, GalleryScreen gallery) = GalleryKit.OpenGallery(p =>
        {
            foreach (int pic in new[] { 1, 2, 3, 4 })
            {
                p.Progress.Pictures.Add(pic);
            }
        });
        return (root, s, gallery, (GalleryCgPanel)gallery.Panel);
    }

    [Fact]
    public void TheGroupGridListsTheGroupsWithAnUnlockedPicture()
    {
        (UiRoot root, _, GalleryScreen gallery, GalleryCgPanel cg) = OpenCg();
        Assert.Equal(2, cg.Count);
        Assert.Equal([1, 3], cg.Unlocked.Select(u => u.Length).Where(n => n > 0));
        Assert.Equal(-1, cg.Cursor);
        Press(root, PadButtons.Right);
        Assert.Equal(0, cg.Cursor);
        Press(root, PadButtons.Right);
        Assert.Equal(1, cg.Cursor);
        Press(root, PadButtons.Right);
        Assert.Equal(1, cg.Cursor);
        Press(root, PadButtons.Left);
        Assert.Equal(0, cg.Cursor);
        Press(root, PadButtons.Left);
        Assert.True(gallery.SideFocus);
        Assert.Equal(-1, cg.Cursor);
    }

    [Fact]
    public void AGroupWithSeveralPicturesOpensItsVariationsGridAndCancelGoesBack()
    {
        (UiRoot root, PreviewServices s, _, GalleryCgPanel cg) = OpenCg();
        Press(root, PadButtons.Right);
        Press(root, PadButtons.Right);
        Press(root, PadButtons.Decide);
        Assert.Equal(1, cg.Sub);
        Assert.Equal(3, cg.Count);
        Assert.Contains("sound Decide", s.Calls);
        Run(root, 40);
        Press(root, PadButtons.Cancel);
        Assert.Null(cg.Sub);
        Assert.Equal(2, cg.Count);
        Assert.Equal(1, cg.Cursor);
    }

    [Fact]
    public void AGroupWithOnePictureOpensTheViewerWhichStepsThroughEveryUnlockedPicture()
    {
        (UiRoot root, _, GalleryScreen gallery, _) = OpenCg();
        Press(root, PadButtons.Right);
        Press(root, PadButtons.Decide);
        var viewer = Assert.IsType<GalleryCgViewer>(root.Top);
        Assert.Equal((0, 0), (viewer.Group, viewer.Pos));
        Run(root, 40);
        Press(root, PadButtons.Right);
        Assert.Equal((1, 0), (viewer.Group, viewer.Pos));
        Run(root, 40);
        Press(root, PadButtons.Right);
        Run(root, 40);
        Press(root, PadButtons.Right);
        Run(root, 40);
        Assert.Equal((1, 2), (viewer.Group, viewer.Pos));
        Press(root, PadButtons.Right);
        Assert.Equal((0, 0), (viewer.Group, viewer.Pos));
        Run(root, 40);
        Press(root, PadButtons.Cancel);
        Run(root, 40);
        Assert.Same(gallery, root.Top);
    }

    [Fact]
    public void TheViewerStaysInsideTheScreenWhenZoomedAndMoved()
    {
        (RectF rect, float zoom, (float X, float Y) pan) = GalleryCgViewer.Geometry(2106f, 1266f, 0f, (0f, 0f));
        Assert.Equal(MathF.Min(1920f / 2106f, 1080f / 1266f), zoom, 4);
        Assert.Equal(UiMetrics.Height, rect.H, 2);
        (rect, zoom, pan) = GalleryCgViewer.Geometry(2106f, 1266f, 5f, (9999f, -9999f));
        Assert.Equal(1f, zoom);
        Assert.Equal(((2106f - 1920f) / 2f, -(1266f - 1080f) / 2f), pan);
        Assert.True(rect.X <= 0f && rect.Right >= UiMetrics.Width);

        // a picture smaller than the screen stays at the fit scale
        (_, zoom, _) = GalleryCgViewer.Geometry(960f, 540f, 0.2f, (0f, 0f));
        Assert.Equal(2f, zoom);
    }

    // ---- music ------------------------------------------------------------------------------------------------------------------------------------

    private static (UiRoot Root, PreviewServices S, GalleryScreen Gallery, GalleryMusicPanel Music) OpenMusic()
    {
        (UiRoot root, PreviewServices s, GalleryScreen gallery) = GalleryKit.OpenGallery(p =>
        {
            p.Progress.Bgm.Add(1);
            p.Progress.Bgm.Add(2);
            p.Progress.Bgm.Add(126);
            p.PlayMusic(2, 1f);
        });
        ToMode(root, 1);
        return (root, s, gallery, (GalleryMusicPanel)gallery.Panel);
    }

    [Fact]
    public void ThePlayerMarksTheBgmThatPlayedHeardThenStopsIt()
    {
        (_, PreviewServices s, _, GalleryMusicPanel music) = OpenMusic();
        Assert.Contains("bgm heard 2", s.Calls);
        Assert.True(s.Calls.IndexOf("bgm heard 2") < s.Calls.IndexOf("music stop"));
        Assert.Equal(0, s.PlayingBgm);
        Assert.Equal([true, true, false, false, false, true], music.Unlocked);
    }

    [Fact]
    public void ADecideOnARowPlaysTheTrackAndAnotherOneOnTheSameRowStopsIt()
    {
        (UiRoot root, PreviewServices s, _, GalleryMusicPanel music) = OpenMusic();
        Press(root, PadButtons.Decide);
        Assert.Equal(0, music.Playing);
        Assert.Equal(1, s.PlayingBgm);
        Run(root, 60);
        Assert.InRange(music.Clock, 0.9f, 1.1f);
        Press(root, PadButtons.Decide);
        Assert.Equal(-1, music.Playing);
        Assert.Equal(0, s.PlayingBgm);
        Assert.Equal(0f, music.Clock);

        // a locked row only stops the player
        Press(root, PadButtons.Down);
        Press(root, PadButtons.Down);
        Press(root, PadButtons.Decide);
        Assert.Equal(-1, music.Playing);
    }

    [Fact]
    public void TheControlsPauseStopRepeatAndStepOverLockedTracks()
    {
        (UiRoot root, PreviewServices s, _, GalleryMusicPanel music) = OpenMusic();
        Press(root, PadButtons.Decide);
        Press(root, PadButtons.Right);
        Assert.Equal(0, music.ControlCursor);
        Press(root, PadButtons.Right);
        Press(root, PadButtons.Decide);
        Assert.True(music.Paused);
        Assert.Contains("music pause True", s.Calls);
        Press(root, PadButtons.Right);
        Press(root, PadButtons.Right);
        Press(root, PadButtons.Decide);
        Assert.True(music.Repeat);
        Press(root, PadButtons.Down);
        Assert.Equal(5, music.ControlCursor);
        Press(root, PadButtons.Decide);

        // next from track 0 skips the locked ones: 1 is unlocked
        Assert.Equal(1, music.Playing);
        Press(root, PadButtons.Decide);
        Assert.Equal(5, music.Playing);
        Press(root, PadButtons.Left);
        Press(root, PadButtons.Decide);
        Assert.Equal(1, music.Playing);
        Press(root, PadButtons.Up);
        Press(root, PadButtons.Right);
        Assert.Equal(2, music.ControlCursor);
        Press(root, PadButtons.Decide);
        Assert.Equal(-1, music.Playing);
    }

    [Fact]
    public void AnUnrepeatedTrackFadesOutAtItsEndAndTheVolumeSliderMovesInHundredths()
    {
        (UiRoot root, PreviewServices s, _, GalleryMusicPanel music) = OpenMusic();
        Press(root, PadButtons.Decide);
        Run(root, (10 * 60) + 10);
        Assert.Contains("bgm fade 1.5", s.Calls);
        Assert.Equal(0, music.Playing);
        Run(root, 100);
        Assert.Equal(-1, music.Playing);

        // volume: right from the list to the controls, down twice to the slider
        Press(root, PadButtons.Right);
        Press(root, PadButtons.Down);
        Press(root, PadButtons.Down);
        Assert.True(music.OnVolume);
        Press(root, PadButtons.Left);
        Press(root, PadButtons.Left);
        Assert.Equal(0.98f, music.Volume, 3);
        Assert.Contains("music volume 0.98", s.Calls);
        Press(root, PadButtons.Up);
        Assert.False(music.OnVolume);
    }

    [Fact]
    public void LeavingThePlayerStopsTheTrack()
    {
        (UiRoot root, PreviewServices s, GalleryScreen gallery, GalleryMusicPanel music) = OpenMusic();
        Press(root, PadButtons.Decide);
        Assert.Equal(0, music.Playing);
        Press(root, PadButtons.Cancel);
        Assert.True(gallery.SideFocus);
        int stops = s.Calls.Count(c => c == "music stop");
        Press(root, PadButtons.Up);
        Press(root, PadButtons.Decide);
        Run(root, 70);
        Assert.Equal(GalleryMode.Cg, gallery.Mode);
        Assert.Equal(stops + 1, s.Calls.Count(c => c == "music stop"));
        Assert.Equal(0, s.PlayingBgm);
    }

    // ---- movies -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheMovieListHoldsThePrologueAndTheWatchedOpenings()
    {
        (UiRoot root, _, GalleryScreen gallery) = GalleryKit.OpenGallery(p => p.Progress.Movies.Add(0));
        ToMode(root, 3);
        var movies = Assert.IsType<GalleryMoviesPanel>(gallery.Panel);
        Assert.Equal([0, 1], movies.Items);
        Assert.Equal(0, movies.Cursor);
        Press(root, PadButtons.Right);
        Assert.Equal(1, movies.Cursor);
        Press(root, PadButtons.Right);
        Assert.Equal(1, movies.Cursor);
        Press(root, PadButtons.Left);
        Press(root, PadButtons.Left);
        Assert.True(gallery.SideFocus);
    }

    [Fact]
    public void APlayedMovieFadesInStartsStopsOnCancelOnceAndBringsTheBgmBack()
    {
        (UiRoot root, PreviewServices s, _) = GalleryKit.OpenGallery(p =>
        {
            p.Progress.Movies.Add(0);
            p.PlayMusic(2, 1f);
        });
        ToMode(root, 3);
        Press(root, PadButtons.Decide);
        var player = Assert.IsType<GalleryMoviePlayer>(root.Top);
        Assert.Equal(0, player.Phase);
        Assert.Contains("bgm fade 0.5", s.Calls);
        Run(root, 35);
        Assert.Equal(1, player.Phase);
        Assert.Contains("intro movie 4", s.Calls);
        Assert.True(s.IntroMoviePlaying);
        Press(root, PadButtons.Cancel);
        Assert.Equal(1, s.Calls.Count(c => c == "intro movie stop"));
        Run(root, 40);
        Assert.Equal(2, player.Phase);
        Run(root, 40);
        Assert.IsType<GalleryScreen>(root.Top);
        Assert.Contains("bgm 2", s.Calls);
    }

    // ---- endings ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AReachedEndingAsksAndYesClosesTheGalleryAndReplaysTheScene()
    {
        (UiRoot root, PreviewServices s, GalleryScreen gallery) = GalleryKit.OpenGallery(p => GalleryKit.Read(p, 100, 101));
        ToMode(root, 4);
        var endings = Assert.IsType<GalleryEndingsPanel>(gallery.Panel);
        Assert.Equal(0, endings.Cursor);
        Press(root, PadButtons.Right);
        Assert.Equal(1, endings.Cursor);
        Press(root, PadButtons.Left);
        Press(root, PadButtons.Decide);
        Assert.IsType<ConfirmDialog>(root.Top);
        Assert.Equal(1000, ((ConfirmDialog)root.Top).MessageId(root.Context.Assets));
        Run(root, 20);
        Assert.False(((ConfirmDialog)root.Top).CursorOnYes);
        Press(root, PadButtons.Left);
        Press(root, PadButtons.Decide);
        Run(root, 120);
        Assert.Contains("replay 1000 -1", s.Calls);
        Assert.False(root.Active);
        Assert.Equal((int)GalleryMode.Endings, root.Context.Gallery.Mode);
        Assert.Equal(1000, root.Context.Gallery.Node);
    }

    [Fact]
    public void AnsweringNoKeepsTheGalleryAndNothingReplays()
    {
        (UiRoot root, PreviewServices s, GalleryScreen gallery) = GalleryKit.OpenGallery(p => GalleryKit.Read(p, 100));
        ToMode(root, 4);
        Press(root, PadButtons.Decide);
        Run(root, 20);
        Press(root, PadButtons.Decide);
        Run(root, 60);
        Assert.Same(gallery, root.Top);
        Assert.DoesNotContain(s.Calls, c => c.StartsWith("replay", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEndingCursorKeepsToReachedEntriesAndFollowsTheGrid()
    {
        (UiRoot root, _, GalleryScreen gallery) = GalleryKit.OpenGallery(p =>
        {
            for (int i = 0; i < 24; i++)
            {
                GalleryKit.Read(p, 100 + i);
            }
        });
        ToMode(root, 4);
        var e = Assert.IsType<GalleryEndingsPanel>(gallery.Panel);
        Assert.Equal(1, e.NextEntry(0, 1, 0));
        Assert.Null(e.NextEntry(2, 1, 0));
        Assert.Equal(-1, e.NextEntry(0, -1, 0));

        // down from the third main ending lands on column 7 of the first bad end row; up from there on the third
        Assert.Equal(10, e.NextEntry(2, 0, 1));
        Assert.Equal(2, e.NextEntry(10, 0, -1));

        // the last scene is entry 24: down from columns 6 / 7 of the middle row lands on it, up from it on column 7
        Assert.Equal(24, e.NextEntry(17, 0, 1));
        Assert.Equal(18, e.NextEntry(24, 0, -1));
        Assert.Equal((5, 3), GalleryEndingsPanel.Position(24));
        Assert.Equal(24, GalleryEndingsPanel.EntryAt(5, 3));
    }

    [Fact]
    public void NothingReachedMeansTheEndingListTakesNoFocus()
    {
        (UiRoot root, _, GalleryScreen gallery) = GalleryKit.OpenGallery();
        ToMode(root, 4);
        Assert.True(gallery.SideFocus);
        Press(root, PadButtons.Right);
        Assert.True(gallery.SideFocus);
    }

    // ---- the flowchart from the gallery -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADecideOnAFlowchartNodeReplaysItAndTheGalleryComesBackOnTheChart()
    {
        (UiRoot root, PreviewServices s, _) = GalleryKit.OpenGallery(GalleryKit.ReadChain);
        Press(root, PadButtons.Down);
        Press(root, PadButtons.Down);
        Press(root, PadButtons.Decide);
        var chart = Assert.IsType<FlowchartScreen>(root.Top);
        Run(root, 40);
        Press(root, PadButtons.Decide);
        Run(root, 100);
        Assert.Contains("replay 301 1", s.Calls);
        Assert.False(root.Active);
        Assert.Equal((int)GalleryMode.Flowchart, root.Context.Gallery.Mode);
        Assert.Equal(1, root.Context.Gallery.Node);
        Assert.NotNull(chart);

        // the host reopens the gallery when the replay has ended: title, then the gallery with the chart on its node
        Assert.True(root.ReturnToGallery());
        Run(root, 5);
        var back = Assert.IsType<FlowchartScreen>(root.Top);
        Assert.True(back.Gallery);
        Assert.Equal(1, back.Chart.RecordAt(back.Cell)!.Value.Node);
        Assert.IsType<TitleScreen>(root.Stack[0]);
        Assert.Equal(UiMode.Title, root.Mode);
    }

    // ---- the title -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheTitleOpensTheGalleryAndTheGalleryHandsTheTitleBackWithTheCursorOnGallery()
    {
        PreviewServices s = GalleryKit.Services();
        UiRoot root = GalleryKit.Root(s);
        root.Context.TitleIntroShown = true;
        var title = root.Push(new TitleScreen());
        Run(root, 300);
        Press(root, PadButtons.Up);
        Assert.Equal(TitleItem.Exit, title.Cursor);                       // package K18: EXIT is the last item, GALLERY the one before it
        Press(root, PadButtons.Up);
        Assert.Equal(TitleItem.Gallery, title.Cursor);
        Press(root, PadButtons.Decide);
        Run(root, 100);
        Assert.Contains("open gallery", s.Calls);
        Assert.Contains("title movie stop", s.Calls);
        var gallery = Assert.IsType<GalleryScreen>(root.Top);
        Assert.Equal(UiMode.Title, root.Mode);
        for (int i = 0; i < 5; i++)
        {
            Press(root, PadButtons.Down);
        }

        Press(root, PadButtons.Decide);
        Run(root, 60);
        Assert.Same(title, root.Top);
        Assert.Equal(TitleItem.Gallery, title.Cursor);
        Assert.Equal(TitlePhase.FadeIn, title.Phase);
        Assert.Equal(GalleryMode.Cg, gallery.Mode);
    }
}

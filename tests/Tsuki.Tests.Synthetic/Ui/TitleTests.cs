using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

public sealed class TitleTests
{
    private static (UiRoot Root, PreviewServices Services, TitleScreen Title) Open(Action<PreviewServices>? tweak = null, bool introShown = true)
    {
        var services = new PreviewServices();
        tweak?.Invoke(services);
        UiRoot root = UiTestKit.Root(services);
        root.Context.TitleIntroShown = introShown;
        var title = root.Push(new TitleScreen());
        return (root, services, title);
    }

    private static void ToIdle(UiRoot root, TitleScreen title)
    {
        for (int i = 0; i < 400 && !title.Idle; i++)
        {
            UiTestKit.Run(root, 1);
        }

        Assert.True(title.Idle);
    }

    [Fact]
    public void StageRuleStepsOnePhaseAtATime()
    {
        Assert.Equal(0, TitleStageRule.Advance(0, true, false, false, false));
        Assert.Equal(1, TitleStageRule.Advance(0, true, true, true, true));
        Assert.Equal(3, TitleStageRule.Advance(1, true, true, true, true));
        Assert.Equal(5, TitleStageRule.Advance(3, true, true, true, true));
        Assert.Equal(5, TitleStageRule.Advance(5, true, false, false, false));
        Assert.Equal(0, TitleStageRule.Advance(2, true, true, true, true));
        Assert.Equal(0, TitleStageRule.Advance(4, true, true, true, true));
        Assert.Equal(0, TitleStageRule.Advance(9, true, false, false, false));
        Assert.Equal(3, TitleStageRule.Advance(3, false, true, true, true));
    }

    [Fact]
    public void ThePrologueStartsAtOnceWhenItWasNeverRead()
    {
        (UiRoot root, PreviewServices s, _) = Open(x => x.Title = new UiTitleInfo(false, false, 0, -1));
        Assert.Contains("start scene 260", s.Calls);
        Assert.DoesNotContain("bgm 2", s.Calls);
        UiTestKit.Run(root, 3);
        Assert.NotEmpty(UiTestKit.Draw(root).Commands.ToArray());
    }

    [Fact]
    public void TheStageRuleDecidesTheBackgroundMovieAndTheBgm()
    {
        (_, PreviewServices s, TitleScreen title) = Open(x => x.SetSceneBit(560, true));
        Assert.Equal(1, title.Stage);
        Assert.Equal(1, s.Global(2));
        Assert.Contains("title movie 1", s.Calls);
        Assert.Contains("bgm 2", s.Calls);

        (_, PreviewServices s5, TitleScreen t5) = Open(x => x.Title = new UiTitleInfo(true, true, 5, 100));
        Assert.Equal(5, t5.Stage);
        Assert.Contains("title movie 3", s5.Calls);
        Assert.Contains("bgm 72", s5.Calls);
        Assert.Equal(UiSheets.TitleStill(4), t5.Still);
    }

    [Fact]
    public void TheStillIsTheBackgroundUntilAMovieFrameIsReadyAndAfterItsLoopPoint()
    {
        (UiRoot root, PreviewServices s, TitleScreen title) = Open();
        ToIdle(root, title);
        Assert.DoesNotContain(UiTestKit.Draw(root).Commands.ToArray(), c => c.Texture.Kind == TextureKind.MovieFrame);

        s.TitleMovieTime = 3.0;
        UiTestKit.Run(root, 1);
        Assert.Contains(UiTestKit.Draw(root).Commands.ToArray(), c => c.Texture.Kind == TextureKind.MovieFrame && c.Texture.A == s.TitleMovieSlot);

        s.TitleMovieTime = 9.0;
        UiTestKit.Run(root, 1);
        s.TitleMovieTime = 1.0;
        UiTestKit.Run(root, 2);
        Assert.Single(s.Calls, c => c.StartsWith("seek title movie 8.0333", StringComparison.Ordinal));
        Assert.DoesNotContain(UiTestKit.Draw(root).Commands.ToArray(), c => c.Texture.Kind == TextureKind.MovieFrame);
    }

    [Fact]
    public void AReturningPlayerSeesTheIntroOncePerPowerOn()
    {
        (UiRoot root, PreviewServices s, TitleScreen title) = Open(introShown: false);
        Assert.Equal(TitlePhase.Intro, title.Phase);
        UiTestKit.Run(root, 70);
        Assert.Contains("intro movie 38", s.Calls);
        Assert.Equal(TitlePhase.Intro, title.Phase);
        s.IntroMoviePlaying = false;
        UiTestKit.Run(root, 200);
        Assert.Equal(TitlePhase.Intro, title.Phase);
        UiTestKit.Run(root, 400);
        ToIdle(root, title);
        Assert.True(root.Context.TitleIntroShown);
        UiRoot again = UiTestKit.Root(new PreviewServices());
        again.Context.TitleIntroShown = true;
        Assert.Equal(TitlePhase.FadeIn, again.Push(new TitleScreen()).Phase);
    }

    [Fact]
    public void AButtonSkipsTheIntroMovieAndTheCaution()
    {
        (UiRoot root, PreviewServices s, TitleScreen title) = Open(introShown: false);
        UiTestKit.Run(root, 70);
        UiTestKit.Run(root, 125);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Contains("intro movie stop", s.Calls);
        UiTestKit.Run(root, 70);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 80);
        Assert.NotEqual(TitlePhase.Intro, title.Phase);
    }

    [Fact]
    public void TheMenuListsContinueOnlyWhenThereIsASave()
    {
        (UiRoot root, _, TitleScreen title) = Open();
        ToIdle(root, title);
        Assert.Equal(TitleItem.Continue, title.Cursor);
        Assert.Equal(5, UiTestKit.Draw(root).Commands.ToArray().Count(c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.TitleParts, C: 0 }));

        (UiRoot root2, _, TitleScreen title2) = Open(x => x.Title = new UiTitleInfo(true, true, 0, -1));
        ToIdle(root2, title2);
        Assert.Equal(TitleItem.NewGame, title2.Cursor);
        Assert.Equal(4, UiTestKit.Draw(root2).Commands.ToArray().Count(c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.TitleParts, C: 0 }));
    }

    [Fact]
    public void TheCursorMovesWithSoundAndWraps()
    {
        (UiRoot root, PreviewServices s, TitleScreen title) = Open();
        ToIdle(root, title);
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(TitleItem.NewGame, title.Cursor);
        Assert.Contains("sound Cursor", s.Calls);
        UiTestKit.Press(root, PadButtons.Up);
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(TitleItem.Exit, title.Cursor);                       // package K18: the last item is EXIT
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(TitleItem.Gallery, title.Cursor);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(TitleItem.Continue, title.Cursor);                   // and Down from EXIT wraps to CONTINUE
    }

    [Fact]
    public void ThePointerHoversAndClicksTheItems()
    {
        (UiRoot root, PreviewServices s, TitleScreen title) = Open();
        ToIdle(root, title);
        RectF load = TitleScreen.ItemRect(2, 6);                           // CONTINUE, NEW GAME, LOAD GAME, CONFIG, GALLERY, EXIT
        UiTestKit.Point(root, load.X + 150, load.Y + 30);
        Assert.Equal(TitleItem.Load, title.Cursor);
        UiTestKit.Point(root, load.X + 150, load.Y + 30, click: true);
        Run(root, 3);
        Assert.IsType<SaveLoadScreen>(root.Top);
    }

    private static void Run(UiRoot root, int n) => UiTestKit.Run(root, n);

    [Fact]
    public void NewGameAfterThePrologueFadesToWhiteAndStartsTheStory()
    {
        (UiRoot root, PreviewServices s, TitleScreen title) = Open();
        ToIdle(root, title);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Contains("sound Start", s.Calls);
        Assert.Contains("bgm fade 1.5", s.Calls);
        Assert.Equal(TitlePhase.WhiteOut, title.Phase);
        UiTestKit.Run(root, 60);
        Assert.DoesNotContain("start scene 110", s.Calls);
        UiTestKit.Run(root, 20);
        Assert.Single(s.Calls, "start scene 110");
        Assert.False(root.Active);
    }

    [Fact]
    public void PressAnyButtonLeadsThroughBothCautionsToTheStory()
    {
        (UiRoot root, PreviewServices s, TitleScreen title) = Open(x => x.Title = new UiTitleInfo(true, false, 0, -1));
        ToIdle(root, title);
        Assert.True(title.PressMode);
        Assert.Null(title.Cursor);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(TitlePhase.Caution, title.Phase);
        Assert.Contains("sound Start", s.Calls);
        // a button is ignored for the first 240 frames of each caution's hold; then it skips
        long start = root.Context.Frame;
        int guard = 0;
        while (!s.Calls.Contains("start scene 110") && guard < 4000)
        {
            guard++;
            if (guard % 3 == 0)
            {
                UiTestKit.Press(root, PadButtons.Decide);
            }
            else
            {
                UiTestKit.Run(root, 1);
            }
        }

        Assert.Contains("start scene 110", s.Calls);
        Assert.True(root.Context.Frame - start > 2 * (60 + 60 + 240));
        Assert.False(root.Active);
    }

    [Fact]
    public void ContinueLoadsTheNewestSaveBehindALoadingPopup()
    {
        (UiRoot root, PreviewServices s, TitleScreen title) = Open();
        ToIdle(root, title);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.IsType<MessagePopup>(root.Top);
        Assert.Equal(TitlePhase.Loading, title.Phase);
        UiTestKit.Run(root, 400);
        Assert.Contains("load 100", s.Calls);
        Assert.Contains("resume loaded", s.Calls);
        Assert.False(root.Active);
    }

    [Fact]
    public void ALoadPickedInTheLoadListFadesTheTitleOutThenLoads()
    {
        (UiRoot root, PreviewServices s, TitleScreen title) = Open();
        ToIdle(root, title);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 60);
        var list = Assert.IsType<SaveLoadScreen>(root.Top);
        Assert.False(list.Save);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 20);
        Assert.IsType<ConfirmDialog>(root.Top);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 500);
        Assert.Contains("bgm fade 1.5", s.Calls);
        Assert.Contains(s.Calls, c => c.StartsWith("load ", StringComparison.Ordinal));
        Assert.Contains("resume loaded", s.Calls);
        Assert.False(root.Active);
    }

    [Fact]
    public void GalleryAndConfigGoToTheirOwners()
    {
        (UiRoot root, PreviewServices s, TitleScreen title) = Open();
        ToIdle(root, title);
        UiTestKit.Press(root, PadButtons.Up);
        UiTestKit.Press(root, PadButtons.Up);                             // wraps past EXIT to GALLERY
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 80);
        Assert.Contains("open gallery", s.Calls);

        (UiRoot root2, _, TitleScreen title2) = Open();
        ToIdle(root2, title2);
        UiTestKit.Press(root2, PadButtons.Down);
        UiTestKit.Press(root2, PadButtons.Down);
        UiTestKit.Press(root2, PadButtons.Down);
        UiTestKit.Press(root2, PadButtons.Decide);
        Assert.IsType<ConfigScreen>(root2.Top);
    }

    [Fact]
    public void TheIdleTitleDrawsTheLogoGlowMenuAndCopyright()
    {
        (UiRoot root, _, TitleScreen title) = Open();
        ToIdle(root, title);
        UiTestKit.Run(root, 10);
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.TitleParts, C: 1 });
        Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.TitleParts, C: 4 } && c.EffectWord == 52);
        Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.TitleParts, C: 3 });
        Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.TitleParts, C: 2 });
        Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.TitleParts, C: 10 });
        Assert.False(root.Context.Backdrop.Visible);
    }

    // CONFIG over a settled title, 90 frames in it (the backdrop is up), Cancel, and the config screen is gone: the title is the top screen again
    private static void ThroughTheConfigScreen(UiRoot root)
    {
        UiTestKit.Run(root, 250);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.IsType<ConfigScreen>(root.Top);
        UiTestKit.Run(root, 90);
        Assert.True(root.Context.Backdrop.Visible);
        UiTestKit.Press(root, PadButtons.Cancel);
        for (int i = 0; i < 120 && root.Top is not TitleScreen; i++)
        {
            UiTestKit.Run(root, 1);
        }

        Assert.IsType<TitleScreen>(root.Top);
    }

    private static int Strong(DrawCmd[] cmds, int child) => cmds.Count(c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.TitleParts } t && t.C == child && (c.V0.Rgba >> 24) > 250);

    [Fact]
    public void ABackdropLeftUpUnderTheTitleFadesOutAndTheLogoComesBack()
    {
        // package K14: the load list and the config screen fade the backdrop in over the title and never took it down; the title counted it as a screen on top, so the
        // logo fell to nothing and the menu (which follows the logo) never returned
        (UiRoot root, _, TitleScreen title) = Open();
        ToIdle(root, title);
        ThroughTheConfigScreen(root);
        UiTestKit.Run(root, 1);
        Assert.True(root.Context.Backdrop.Visible);
        Assert.True(root.Context.Backdrop.Speed < 0f);                                // the title turned it round at once
        Assert.Equal(0, Strong(UiTestKit.Draw(root).Commands.ToArray(), 0));          // the menu is still down while the backdrop goes
        UiTestKit.Run(root, 60);
        Assert.False(root.Context.Backdrop.Visible);
        UiTestKit.Run(root, 200);
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        Assert.Equal(5, Strong(cmds, 0));
        Assert.True(Strong(cmds, 1) > 0);
    }

    [Fact]
    public void ThePointerOrAKeyShowsTheLogoAndMenuAtOnceEvenWhileTheBackdropIsStillGoing()
    {
        (UiRoot root, _, TitleScreen title) = Open();
        ToIdle(root, title);
        ThroughTheConfigScreen(root);
        UiTestKit.Run(root, 3);
        Assert.True(root.Context.Backdrop.Visible);
        Assert.Equal(0, Strong(UiTestKit.Draw(root).Commands.ToArray(), 0));

        UiTestKit.MouseHover(root, 100f, 100f);                                       // anywhere in the window
        UiTestKit.MouseHover(root, 140f, 130f);
        Assert.False(root.Context.Backdrop.Visible);
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        Assert.Equal(5, Strong(cmds, 0));
        Assert.True(Strong(cmds, 1) > 0);
        Assert.Equal(TitleItem.Config, title.Cursor);                                 // hovering where there is no item moves no cursor
    }

    [Fact]
    public void ANewTitleStartsWithNoBackdrop()
    {
        var services = new PreviewServices();
        UiRoot root = UiTestKit.Root(services);
        root.Context.TitleIntroShown = true;
        root.Context.Backdrop.Fade(0.0555f);
        UiTestKit.Run(root, 40);
        Assert.True(root.Context.Backdrop.Visible);
        root.Push(new TitleScreen());
        Assert.False(root.Context.Backdrop.Visible);
    }

    [Fact]
    public void WASDMoveTheTitleCursorLikeTheArrowKeys()
    {
        (UiRoot root, _, TitleScreen title) = Open();
        ToIdle(root, title);

        void Letter(PadButtons b)
        {
            root.Step(new InputFrame { Frame = root.Context.Frame + 1, MenuHeld = b, MenuPressed = b, MenuRepeated = b });
            root.Step(new InputFrame { Frame = root.Context.Frame + 1 });
        }

        Assert.Equal(TitleItem.Continue, title.Cursor);
        Letter(PadButtons.Down);
        Assert.Equal(TitleItem.NewGame, title.Cursor);
        Letter(PadButtons.Down);
        Assert.Equal(TitleItem.Load, title.Cursor);
        Letter(PadButtons.Up);
        Assert.Equal(TitleItem.NewGame, title.Cursor);
        Letter(PadButtons.Up);
        Letter(PadButtons.Up);
        Assert.Equal(TitleItem.Exit, title.Cursor);                                   // wraps like the arrows, to the last item (EXIT, package K18)
        Letter(PadButtons.Up);
        Assert.Equal(TitleItem.Gallery, title.Cursor);
        Letter(PadButtons.Down);
        Assert.Equal(TitleItem.Exit, title.Cursor);
    }

    // -- package K18: EXIT ---------------------------------------------------------------------------------------------------------------------

    private static (UiRoot Root, PreviewServices Services, TitleScreen Title) Settled(Action<PreviewServices>? tweak = null)
    {
        (UiRoot root, PreviewServices s, TitleScreen title) = Open(tweak);
        ToIdle(root, title);
        UiTestKit.Run(root, 250);
        return (root, s, title);
    }

    private static readonly Action<PreviewServices> NoSave = x => x.Title = new UiTitleInfo(true, true, 0, -1);

    private static void RightClick(UiRoot root, float x, float y)
    {
        root.Step(UiInput.PointerFrame(x, y, PointerButtons.Right, PointerButtons.Right, 0, root.Context.Frame + 1));
        root.Step(UiInput.PointerFrame(x, y, PointerButtons.None, PointerButtons.None, 0, root.Context.Frame + 1));
    }

    [Theory]
    [InlineData(false, TitleItem.Continue)]      // a save: CONTINUE, NEW GAME, LOAD GAME, CONFIG, GALLERY, EXIT
    [InlineData(true, TitleItem.NewGame)]        // none: NEW GAME, LOAD GAME, CONFIG, GALLERY, EXIT
    public void ExitIsTheLastItemAfterGalleryWithAndWithoutContinue(bool noSave, TitleItem first)
    {
        (UiRoot root, _, TitleScreen title) = Settled(noSave ? NoSave : null);
        Assert.Equal(first, title.Cursor);
        UiTestKit.Press(root, PadButtons.Up);                             // wraps from the first item to the last
        Assert.Equal(TitleItem.Exit, title.Cursor);
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(TitleItem.Gallery, title.Cursor);
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(TitleItem.Exit, title.Cursor);
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(first, title.Cursor);
    }

    [Fact]
    public void ExitHasASlotInsideTheScreenAboveTheCopyrightAndNoItemOverlapsAnother()
    {
        for (int n = 5; n <= 6; n++)
        {
            for (int i = 1; i < n; i++)
            {
                Assert.True(TitleScreen.ItemRect(i, n).Y - TitleScreen.ItemRect(i - 1, n).Y >= 47f, $"rows {n}: item {i} is too close to the one above");
            }
        }

        // package K28: the letters of an item fill rows 28..46 of its slot; the gap between EXIT's letters and the copyright's is at least the gap between two items,
        // and the first row sits a little above the PS4's five-row position but CONTINUE stays well under the Japanese logo's subtitle (its lowest row is 710)
        RectF exit = TitleScreen.ItemRect(5, 6);
        float pitch = TitleScreen.ItemRect(1, 6).Y - TitleScreen.ItemRect(0, 6).Y;
        float itemGap = pitch - 19f;
        float copyrightGap = TitleScreen.CopyrightInkTop - (exit.Y + TitleScreen.ItemInkTop + 19f);
        Assert.True(copyrightGap >= itemGap + 2f, $"EXIT is {copyrightGap} px above the copyright, two items are {itemGap} px apart");
        Assert.True(copyrightGap < itemGap + 12f, $"EXIT is {copyrightGap} px above the copyright: the menu need not float that high");
        RectF first = TitleScreen.ItemRect(0, 6);
        Assert.True(first.Y < TitleScreen.ItemRect(0, 5).Y, "the six rows start a little above the five");
        Assert.True(first.Y >= TitleScreen.ItemRect(0, 5).Y - 8f, "but only a little");
        Assert.True(first.Y + TitleScreen.ItemInkTop - 710f >= 30f, "CONTINUE stays clearly under the subtitle of the logo");
        Assert.Equal(TitleScreen.SixRowTop * UiMetrics.UnitY, first.Y, 2);
        Assert.Equal(TitleScreen.SixRowPitch * UiMetrics.UnitY, pitch, 2);
        // the five-row menu without a save keeps the PS4's rows: EXIT is its fifth row, 56 px under GALLERY and well above the copyright
        RectF exit5 = TitleScreen.ItemRect(4, 5);
        Assert.Equal(23.22963f * UiMetrics.UnitY, exit5.Y - TitleScreen.ItemRect(3, 5).Y, 2);
        Assert.True(TitleScreen.CopyrightInkTop - (exit5.Y + TitleScreen.ItemInkTop + 19f) >= 23.22963f * UiMetrics.UnitY - 19f);
    }

    [Fact]
    public void TheSixHitRectsTileTheColumnWithoutOverlapAndEachHoldsItsLetters()
    {
        (UiRoot root, _, TitleScreen title) = Settled();
        TitleItem[] order = [TitleItem.Continue, TitleItem.NewGame, TitleItem.Load, TitleItem.Config, TitleItem.Gallery, TitleItem.Exit];
        for (int i = 0; i < 6; i++)
        {
            RectF slot = TitleScreen.ItemRect(i, 6);
            UiTestKit.Point(root, slot.X + 150f, slot.Y + TitleScreen.ItemInkTop + 9f);          // the middle of the letters
            Assert.Equal(order[i], title.Cursor);
            UiTestKit.Point(root, slot.X + 150f, slot.Y + 50f);                                  // the cursor underline's row still belongs to the item
            Assert.Equal(order[i], title.Cursor);
        }

        // just above the first item's rect and just under the last one's: the cursor stays where it was (EXIT)
        UiTestKit.Point(root, 966f, TitleScreen.ItemRect(0, 6).Y + 2f);
        Assert.Equal(TitleItem.Exit, title.Cursor);
        UiTestKit.Point(root, 966f, TitleScreen.ItemRect(5, 6).Y + (4f * UiMetrics.UnitY) + (TitleScreen.SixRowPitch * UiMetrics.UnitY) + 2f);
        Assert.Equal(TitleItem.Exit, title.Cursor);
    }

    [Fact]
    public void ExitFallsBackToGlyphsInItsSlotWhenTheSheetCannotBeCutAndTheOtherItemsAreSheetCells()
    {
        // the test kit's sheet is one blank tile: TitleExitArt (package K33) cannot cut letters out of it, so the title draws K18's glyph-font EXIT; the composed
        // cell is covered by TitleExitArtTests on a sheet with letters
        (UiRoot root, _, _) = Settled();
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        Assert.Equal(5, Strong(cmds, 0));                                              // CONTINUE, NEW GAME, LOAD GAME, CONFIG, GALLERY are sheet cells
        Assert.DoesNotContain(cmds, c => c.Texture.Kind == TextureKind.UiComposed);
        RectF slot = TitleScreen.ItemRect(5, 6);
        DrawCmd[] glyphs = [.. cmds.Where(c => c.Texture.Kind == TextureKind.GlyphPage)];
        Assert.NotEmpty(glyphs);
        Assert.All(glyphs, c => Assert.True(c.V0.X >= slot.X && c.V0.X <= slot.Right && c.V0.Y >= slot.Y && c.V0.Y <= slot.Bottom, $"a glyph quad at ({c.V0.X}, {c.V0.Y}) lies outside the slot of EXIT"));
        Assert.Contains(glyphs, c => (c.V0.Rgba >> 24) > 250);                          // the letters themselves are opaque; the rest is glow
    }

    [Fact]
    public void ExitTakesTheCursorColourWhenTheCursorIsOnIt()
    {
        // the glyph-font fallback (the kit's blank sheet); the composed cell's cursor look is covered by TitleExitArtTests
        (UiRoot root, _, _) = Settled();
        static (int R, int G, int B) Rgb(in DrawCmd c) => ((int)(c.V0.Rgba & 0xFF), (int)((c.V0.Rgba >> 8) & 0xFF), (int)((c.V0.Rgba >> 16) & 0xFF));
        static DrawCmd Letter(DrawCmd[] cmds) => cmds.First(c => c.Texture.Kind == TextureKind.GlyphPage && (c.V0.Rgba >> 24) > 250);

        (int r, int g, int b) = Rgb(Letter(UiTestKit.Draw(root).Commands.ToArray()));
        Assert.True(r > 240 && g > 240 && b > 240, "EXIT is white while the cursor is elsewhere");

        UiTestKit.Press(root, PadButtons.Up);
        UiTestKit.Run(root, 5);
        (r, g, b) = Rgb(Letter(UiTestKit.Draw(root).Commands.ToArray()));
        Assert.True(r < 60 && g > 200 && b > 230, $"EXIT is cyan under the cursor: {r},{g},{b}");
    }

    [Fact]
    public void EnterOnExitAsksThePs4QuitQuestionWithTheCursorOnNoAndYesQuitsOnce()
    {
        (UiRoot root, PreviewServices s, _) = Settled();
        UiTestKit.Press(root, PadButtons.Up);
        UiTestKit.Press(root, PadButtons.Decide);
        var dialog = Assert.IsType<ConfirmDialog>(root.Top);
        Assert.Equal(TitleScreen.QuitMessage, dialog.MessageId(root.Context.Assets));
        Assert.False(dialog.CursorOnYes);
        UiTestKit.Run(root, 30);
        Assert.DoesNotContain("quit", s.Calls);

        UiTestKit.Press(root, PadButtons.Left);                           // to Yes
        Assert.True(dialog.CursorOnYes);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 40);
        Assert.Single(s.Calls, "quit");
        UiTestKit.Run(root, 60);
        Assert.Single(s.Calls, "quit");                                   // asked once
    }

    [Fact]
    public void NoAndCancelInTheQuitQuestionGoBackToTheTitleWithTheLogoStillUp()
    {
        (UiRoot root, PreviewServices s, TitleScreen title) = Settled();
        UiTestKit.Press(root, PadButtons.Up);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.IsType<ConfirmDialog>(root.Top);
        UiTestKit.Run(root, 60);
        DrawCmd[] behind = UiTestKit.Draw(root).Commands.ToArray();
        Assert.Equal(5, Strong(behind, 0));                                // the dialog does not take the logo and the menu down
        Assert.True(Strong(behind, 1) > 0);

        UiTestKit.Press(root, PadButtons.Decide);                          // the cursor starts on No
        UiTestKit.Run(root, 60);
        Assert.Same(title, root.Top);
        Assert.Equal(TitleItem.Exit, title.Cursor);
        Assert.DoesNotContain("quit", s.Calls);
        DrawCmd[] after = UiTestKit.Draw(root).Commands.ToArray();
        Assert.Equal(5, Strong(after, 0));
        Assert.True(Strong(after, 1) > 0);

        UiTestKit.Press(root, PadButtons.Decide);                          // asks again
        UiTestKit.Run(root, 30);
        UiTestKit.Press(root, PadButtons.Cancel);                          // and Cancel in the dialog answers No
        UiTestKit.Run(root, 60);
        Assert.Same(title, root.Top);
        Assert.DoesNotContain("quit", s.Calls);
    }

    [Fact]
    public void EscapeRightClickAndThePadsCancelAskTheSameQuestionFromAnyItem()
    {
        foreach (string how in new[] { "pad", "right click" })
        {
            (UiRoot root, PreviewServices s, TitleScreen title) = Settled();
            Assert.Equal(TitleItem.Continue, title.Cursor);
            if (how == "pad")
            {
                UiTestKit.Press(root, PadButtons.Cancel);
            }
            else
            {
                RightClick(root, 100f, 100f);
            }

            var dialog = Assert.IsType<ConfirmDialog>(root.Top);
            Assert.Equal(TitleScreen.QuitMessage, dialog.MessageId(root.Context.Assets));
            Assert.Equal(TitleItem.Continue, title.Cursor);                // the cursor was not moved
            Assert.DoesNotContain("quit", s.Calls);
        }
    }

    [Fact]
    public void CancelOverPressAnyButtonAsksToQuitAndAnyOtherButtonStillGoesOn()
    {
        (UiRoot root, _, TitleScreen title) = Open(x => x.Title = new UiTitleInfo(true, false, 0, -1));
        ToIdle(root, title);
        Assert.True(title.PressMode);
        UiTestKit.Press(root, PadButtons.Cancel);
        Assert.IsType<ConfirmDialog>(root.Top);
        UiTestKit.Run(root, 30);
        UiTestKit.Press(root, PadButtons.Decide);                          // No
        UiTestKit.Run(root, 60);
        Assert.Same(title, root.Top);
        Assert.Equal(TitlePhase.Idle, title.Phase);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(TitlePhase.Caution, title.Phase);
    }

    [Fact]
    public void AClickOnExitAsksAndAClickBelowItOnTheCopyrightDoesNot()
    {
        (UiRoot root, _, TitleScreen title) = Settled();
        RectF exit = TitleScreen.ItemRect(5, 6);
        UiTestKit.Point(root, exit.X + 150, exit.Y + 38);                  // on the letters: the cursor goes there
        Assert.Equal(TitleItem.Exit, title.Cursor);
        UiTestKit.Point(root, exit.X + 150, 1060f, click: true);           // under the item, on the copyright: nothing
        Assert.Same(title, root.Top);
        UiTestKit.Point(root, exit.X + 150, exit.Y + 38, click: true);
        UiTestKit.Run(root, 2);
        Assert.IsType<ConfirmDialog>(root.Top);
    }

    [Fact]
    public void CancelIsIgnoredWhileTheTitleFadesInAndOnceAStoryIsStarting()
    {
        (UiRoot root, PreviewServices s, TitleScreen title) = Open();
        UiTestKit.Run(root, 20);
        UiTestKit.Press(root, PadButtons.Cancel);
        Assert.Same(title, root.Top);                                      // the menu is not up yet

        ToIdle(root, title);
        UiTestKit.Run(root, 250);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Decide);                          // NEW GAME: the white-out starts
        Assert.Equal(TitlePhase.WhiteOut, title.Phase);
        UiTestKit.Press(root, PadButtons.Cancel);
        Assert.DoesNotContain("quit", s.Calls);
        Assert.False(root.Top is ConfirmDialog);
    }

    // -- package K18: the logo and the menu come up by themselves, with no input at all -----------------------------------------------------------

    private static void AssertLogoAndMenuUp(UiRoot root, int items, string when)
    {
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        Assert.True(Strong(cmds, 0) == items, $"{when}: {Strong(cmds, 0)} of {items} menu items at full strength");
        Assert.True(Strong(cmds, 1) > 0, $"{when}: the logo is not at full strength");
        Assert.False(root.Context.Backdrop.Visible, $"{when}: a backdrop is still up");
    }

    /// <summary>Runs with no input and looks at 3.3, 6 and 10 seconds (the user's own times).</summary>
    private static void AssertUpAt3And6And10Seconds(UiRoot root, int items, string what)
    {
        UiTestKit.Run(root, 200);
        AssertLogoAndMenuUp(root, items, what + " at 3.3 s");
        UiTestKit.Run(root, 160);
        AssertLogoAndMenuUp(root, items, what + " at 6 s");
        UiTestKit.Run(root, 240);
        AssertLogoAndMenuUp(root, items, what + " at 10 s");
    }

    [Theory]
    [InlineData(false, 6)]
    [InlineData(true, 5)]
    public void ANewTitleShowsItsLogoAndMenuByItselfWithNoInputAtAll(bool noSave, int items)
    {
        (UiRoot root, _, _) = Open(noSave ? NoSave : null);
        AssertUpAt3And6And10Seconds(root, items - 1, "a new title");      // the sheet items; EXIT is drawn with glyphs
    }

    [Fact]
    public void TheBootNoticeTheIntroAndTheTitleShowTheLogoAndMenuByThemselvesWithNoInputAtAll()
    {
        var services = new PreviewServices();
        UiRoot root = UiTestKit.Root(services);
        root.Push(new BootScreen());                                      // a returning player's first title of this run: the intro plays as well
        UiTestKit.Run(root, 500);
        services.IntroMoviePlaying = false;                               // the TM_CI movie ends by itself
        UiTestKit.Run(root, 2500);
        var title = Assert.IsType<TitleScreen>(root.Top);
        Assert.Equal(TitlePhase.Idle, title.Phase);
        AssertUpAt3And6And10Seconds(root, 5, "after boot and intro");
    }

    [Theory]
    [InlineData(2)]       // CONFIG
    [InlineData(1)]       // LOAD GAME
    public void TheTitleShowsItsLogoAndMenuByItselfAfterAScreenOpenedFromItClosedWithNoInputAtAll(int downs)
    {
        (UiRoot root, _, TitleScreen title) = Open(NoSave);
        ToIdle(root, title);
        UiTestKit.Run(root, 250);
        for (int i = 0; i < downs; i++)
        {
            UiTestKit.Press(root, PadButtons.Down);
        }

        UiTestKit.Press(root, PadButtons.Decide);
        Assert.True(root.Top is ConfigScreen or SaveLoadScreen);
        UiTestKit.Run(root, 90);
        Assert.True(root.Context.Backdrop.Visible);
        UiTestKit.Press(root, PadButtons.Cancel);
        for (int i = 0; i < 120 && root.Top is not TitleScreen; i++)
        {
            UiTestKit.Run(root, 1);
        }

        Assert.IsType<TitleScreen>(root.Top);
        // from here nothing at all: not a key, not a pointer
        AssertUpAt3And6And10Seconds(root, 4, "after the screen closed");
    }

    public static TheoryData<string> BackdropsLeftUp() => new() { "cut while fading in", "still fading in", "fading out" };

    [Theory]
    [MemberData(nameof(BackdropsLeftUp))]
    public void WhateverBackdropAClosedScreenLeavesUpTheTitleTakesItDownAndShowsItsLogoAndMenuByItself(string state)
    {
        (UiRoot root, _, TitleScreen title) = Open(NoSave);
        ToIdle(root, title);
        UiTestKit.Run(root, 250);
        UiBackdrop backdrop = root.Context.Backdrop;
        switch (state)
        {
            case "cut while fading in":
                backdrop.Fade(0.05f);
                for (int i = 0; i < 10; i++)
                {
                    backdrop.Update();
                }

                backdrop.Fade(0f);                                        // Speed 0, alpha 1: nothing fades it any more
                Assert.True(backdrop.Visible);
                Assert.Equal(0f, backdrop.Speed);
                break;
            case "still fading in":
                backdrop.Fade(0.02f);
                break;
            default:
                backdrop.Fade(0.2f);
                for (int i = 0; i < 6; i++)
                {
                    backdrop.Update();
                }

                backdrop.Fade(-0.02f);
                break;
        }

        UiTestKit.Run(root, 1);
        Assert.Same(title, root.Top);
        AssertUpAt3And6And10Seconds(root, 4, "a backdrop left up (" + state + ")");
    }

    [Fact]
    public void ReturningFromTheGalleryShowsTheLogoAndMenuByItselfWithTheCursorOnGallery()
    {
        (UiRoot root, _, TitleScreen title) = Open(NoSave);
        ToIdle(root, title);
        UiTestKit.Run(root, 250);
        root.Context.Backdrop.Fade(0.04f);                                // what the gallery has up when it closes (the title below it does not step as the top screen)
        for (int i = 0; i < 30; i++)
        {
            root.Context.Backdrop.Update();
        }

        Assert.True(root.Context.Backdrop.Visible);
        title.ReturnFromGallery();
        Assert.Equal(TitleItem.Gallery, title.Cursor);
        AssertUpAt3And6And10Seconds(root, 4, "after the gallery");
        Assert.Equal(TitleItem.Gallery, title.Cursor);
    }

    // -- package K18: the idle fade-out (the PS4's 60 s) ---------------------------------------------------------------------------------------

    /// <summary>A settled title whose idle clock has just been started by a pointer that moves far from every item (the clock itself is the unit of the tests below).</summary>
    private static (UiRoot Root, PreviewServices Services, TitleScreen Title) SettledAndTouched()
    {
        (UiRoot root, PreviewServices s, TitleScreen title) = Settled();
        UiTestKit.MouseHover(root, 100f, 100f);
        UiTestKit.MouseHover(root, 140f, 130f);
        return (root, s, title);
    }

    private static int OnShow(UiRoot root)
    {
        // quads of the menu cells, the logo, the moon glow and the glyphs of EXIT that have any alpha at all
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        return cmds.Count(c => ((c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.TitleParts } t && t.C is 0 or 1 or 4) || c.Texture.Kind == TextureKind.GlyphPage) && (c.V0.Rgba >> 24) > 3);
    }

    private static void AssertShown(UiRoot root, string when)
    {
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        Assert.True(Strong(cmds, 0) == 5 && Strong(cmds, 1) > 0, $"{when}: the logo and the menu are not fully up");
    }

    private static void AssertFadedOut(UiRoot root, TitleScreen title, string when)
    {
        Assert.True(title.IdleHidden, when + ": not idle-hidden");
        Assert.Equal(0, OnShow(root));
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.TitleParts, C: 3 });                // the copyright stays
        Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.TitleParts } t && t.C >= 10);        // so does the background
    }

    [Fact]
    public void NoInputForTheIdleTimeFadesTheLogoAndMenuOutAndLeavesTheBackground()
    {
        Assert.Equal(3600, TitleScreen.IdleFrames);                          // 60 s at 60 steps, the PS4's idle counter (0xE10)
        (UiRoot root, _, TitleScreen title) = SettledAndTouched();
        AssertShown(root, "when settled");
        UiTestKit.Run(root, TitleScreen.IdleFrames - 300);
        AssertShown(root, "five seconds before the idle time");
        Assert.False(title.IdleHidden);
        UiTestKit.Run(root, 290);
        AssertShown(root, "a few steps before the idle time");
        UiTestKit.Run(root, 20);                                              // the idle time is over: the logo starts to fall at 1 / 60 a step
        Assert.True(title.IdleHidden);
        UiTestKit.Run(root, 70);
        AssertFadedOut(root, title, "a second and a bit after the idle time");
        UiTestKit.Run(root, 3000);
        AssertFadedOut(root, title, "long after");
        Assert.Same(title, root.Top);
        Assert.Equal(TitlePhase.Idle, title.Phase);
    }

    [Fact]
    public void ThePointerMovingABackClickWheelKeyAndPadButtonEachBringThemBackAtOnceAndRestartTheClock()
    {
        string[] inputs = ["pointer", "click", "wheel", "direction", "pad button"];
        foreach (string input in inputs)
        {
            (UiRoot root, _, TitleScreen title) = Settled();
            UiTestKit.Run(root, TitleScreen.IdleFrames + 100);
            AssertFadedOut(root, title, input + ": before");

            switch (input)
            {
                case "pointer":
                    UiTestKit.MouseHover(root, 100f, 100f);
                    UiTestKit.MouseHover(root, 140f, 130f);
                    break;
                case "click":
                    UiTestKit.Point(root, 100f, 100f, click: true);          // far from every item
                    break;
                case "wheel":
                    UiTestKit.MouseWheel(root, 100f, 100f, 1);
                    break;
                case "direction":
                    UiTestKit.Press(root, PadButtons.Down);
                    break;
                default:
                    UiTestKit.Press(root, PadButtons.Triangle);
                    break;
            }

            Assert.False(title.IdleHidden, input);
            AssertShown(root, input + ": after");
            UiTestKit.Run(root, TitleScreen.IdleFrames - 100);
            AssertShown(root, input + ": the clock started again");
            UiTestKit.Run(root, 200);
            Assert.True(title.IdleHidden, input + ": and runs out again");
        }
    }

    [Fact]
    public void InputBeforeTheTimeoutNeverHidesThem()
    {
        (UiRoot root, _, TitleScreen title) = SettledAndTouched();
        for (int round = 0; round < 6; round++)
        {
            UiTestKit.Run(root, TitleScreen.IdleFrames - 20);                // 6 rounds are far more than one idle time
            AssertShown(root, $"round {round}");
            Assert.False(title.IdleHidden);
            if (round % 2 == 0)
            {
                UiTestKit.MouseHover(root, 100f + (round * 40f), 100f);
                UiTestKit.MouseHover(root, 140f + (round * 40f), 130f);
            }
            else
            {
                UiTestKit.Press(root, round % 4 == 1 ? PadButtons.Down : PadButtons.Up);
            }
        }
    }

    [Fact]
    public void ThePressThatBringsAHiddenMenuBackIsNotAnAnswerToItButTheNextOneIs()
    {
        (UiRoot root, PreviewServices s, TitleScreen title) = Settled();
        UiTestKit.Run(root, TitleScreen.IdleFrames + 100);
        AssertFadedOut(root, title, "before");
        UiTestKit.Press(root, PadButtons.Decide);                              // CONTINUE is under the cursor, and not chosen
        Assert.Equal(TitlePhase.Idle, title.Phase);
        Assert.DoesNotContain(s.Calls, c => c.StartsWith("load", StringComparison.Ordinal));
        AssertShown(root, "after the press");

        UiTestKit.Run(root, TitleScreen.IdleFrames + 100);
        AssertFadedOut(root, title, "again");
        UiTestKit.Press(root, PadButtons.Cancel);                              // Cancel wakes it too, without the quit question
        Assert.Same(title, root.Top);
        AssertShown(root, "after Cancel");
        UiTestKit.Press(root, PadButtons.Cancel);                              // and now it asks
        Assert.IsType<ConfirmDialog>(root.Top);
    }

    [Fact]
    public void TheIdleClockDoesNotRunWhileAScreenIsOnTopOfTheTitleAndStartsWhenItIsBack()
    {
        (UiRoot root, _, TitleScreen title) = Settled(NoSave);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.IsType<ConfigScreen>(root.Top);
        UiTestKit.Run(root, TitleScreen.IdleFrames + 600);                     // a long time on the config screen
        UiTestKit.Press(root, PadButtons.Cancel);
        for (int i = 0; i < 120 && root.Top is not TitleScreen; i++)
        {
            UiTestKit.Run(root, 1);
        }

        Assert.False(title.IdleHidden);
        UiTestKit.Run(root, 400);                                              // the logo and menu came back (3.3 s, no input)
        Assert.Equal(4, Strong(UiTestKit.Draw(root).Commands.ToArray(), 0));
        UiTestKit.Run(root, TitleScreen.IdleFrames - 600);
        Assert.False(title.IdleHidden);                                        // the count began on the way back, not before the config screen
        UiTestKit.Run(root, 700);
        Assert.True(title.IdleHidden);
    }

    [Fact]
    public void PressAnyButtonAndTheQuitQuestionAreNeverFadedOutByTheIdleClock()
    {
        (UiRoot root, _, TitleScreen title) = Open(x => x.Title = new UiTitleInfo(true, false, 0, -1));
        ToIdle(root, title);
        UiTestKit.Run(root, TitleScreen.IdleFrames + 300);
        Assert.True(title.PressMode);
        Assert.False(title.IdleHidden);
        Assert.True(Strong(UiTestKit.Draw(root).Commands.ToArray(), 1) > 0);

        (UiRoot root2, _, TitleScreen title2) = Settled();
        UiTestKit.Press(root2, PadButtons.Cancel);
        Assert.IsType<ConfirmDialog>(root2.Top);
        UiTestKit.Run(root2, TitleScreen.IdleFrames + 300);                    // the question stays up and the title below keeps its logo
        Assert.IsType<ConfirmDialog>(root2.Top);
        Assert.False(title2.IdleHidden);
        Assert.True(Strong(UiTestKit.Draw(root2).Commands.ToArray(), 1) > 0);
    }

    [Fact]
    public void ThePressModeBackgroundIsTheOffsetLayer()
    {
        (UiRoot root, _, TitleScreen title) = Open(x => x.Title = new UiTitleInfo(true, false, 0, -1));
        ToIdle(root, title);
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        DrawCmd bg = cmds.First(c => c.Texture is { Kind: TextureKind.UiTile, C: 9 });
        Assert.Equal(-375f, bg.V0.X);
        Assert.Equal(-110f, bg.V0.Y);
        Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile, C: 14 });
    }

    [Fact]
    public void TheMenuSlotsFollowTheTable()
    {
        RectF first4 = TitleScreen.ItemRect(0, 4);
        RectF first5 = TitleScreen.ItemRect(0, 5);
        Assert.Equal(816f, first4.X);
        Assert.Equal(312.35553f * UiMetrics.UnitY, first4.Y, 2);
        Assert.Equal(298.66666f * UiMetrics.UnitY, first5.Y, 2);
        Assert.Equal(23.22963f * UiMetrics.UnitY, TitleScreen.ItemRect(1, 5).Y - first5.Y, 2);
    }
}

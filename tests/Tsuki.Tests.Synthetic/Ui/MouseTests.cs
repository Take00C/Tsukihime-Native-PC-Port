using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>
/// The mouse in the menus (package K12): the scroll bar of every list (drag the thumb, click the track for a page, the thumb lights), the config sliders (click a step
/// dot or the line, drag the knob, a drag that leaves the row), the click on a word of the button guide, the mouse taking the focus back from the system menu's tabs,
/// the CG viewer's click to the next picture. Pointers are exact canvas pixels (<see cref="UiInput.PointerFrame"/>).
/// </summary>
public sealed class MouseTests
{
    private const float BarX = 1855f;          // the middle of the scroll bar's grab area

    private static (UiRoot Root, PreviewServices Services, ConfigScreen Screen) OpenConfig(Action<PreviewServices>? tweak = null)
    {
        var services = new PreviewServices(Language.En);
        tweak?.Invoke(services);
        UiRoot root = UiTestKit.Root(services);
        root.OpenSystemMenu();
        UiTestKit.Run(root, 20);
        root.Context.MenuTab = 2;
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 60);
        return (root, services, Assert.IsType<ConfigScreen>(root.Top));
    }

    // ---- the scroll bar widget ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheThumbIsGrabbedWhereItWasHitAndFollowsThePointerWhereverItGoes()
    {
        var bar = new UiScrollBar();
        float top = UiScrollBar.ThumbTop(0.25f, 608f);
        UiInput Frame(float x, float y, PointerButtons held, PointerButtons pressed) => UiInput.From(UiInput.PointerFrame(x, y, held, pressed), default);

        ScrollBarUse down = bar.Step(Frame(BarX, top + 50f, PointerButtons.Left, PointerButtons.Left), 0.25f, 608f, true);
        Assert.True(down.Consumed);
        Assert.Null(down.Fraction);                                       // grabbing does not move it
        Assert.True(bar.Dragging);

        ScrollBarUse move = bar.Step(Frame(100f, top + 50f + 152f, PointerButtons.Left, PointerButtons.None), 0.25f, 608f, true);      // far left of the bar: still held
        Assert.Equal(0.5f, move.Fraction!.Value, 3);
        ScrollBarUse beyond = bar.Step(Frame(BarX, 5000f, PointerButtons.Left, PointerButtons.None), 0.5f, 608f, true);
        Assert.Equal(1f, beyond.Fraction!.Value);
        ScrollBarUse above = bar.Step(Frame(BarX, -300f, PointerButtons.Left, PointerButtons.None), 1f, 608f, true);
        Assert.Equal(0f, above.Fraction!.Value);

        ScrollBarUse up = bar.Step(Frame(BarX, 10f, PointerButtons.None, PointerButtons.None), 0f, 608f, true);
        Assert.False(up.Consumed);
        Assert.False(bar.Dragging);
    }

    [Fact]
    public void AClickOnTheTrackPagesTowardsThePointerAndRepeatsWhileHeldUntilTheThumbArrives()
    {
        var bar = new UiScrollBar();
        UiInput Held(float y, bool pressed) =>
            UiInput.From(UiInput.PointerFrame(BarX, y, PointerButtons.Left, pressed ? PointerButtons.Left : PointerButtons.None), default);

        ScrollBarUse first = bar.Step(Held(800f, true), 0f, 608f, true);              // thumb at the top, pointer far below
        Assert.True(first.Consumed);
        Assert.Equal(1, first.Page);
        int pages = 0;
        for (int i = 0; i < 60; i++)
        {
            ScrollBarUse r = bar.Step(Held(800f, false), 0f, 608f, true);
            Assert.True(r.Consumed);
            pages += r.Page;
        }

        Assert.InRange(pages, 5, 8);                                                    // after 24 frames, every 6
        // the thumb has reached the pointer: no more paging
        ScrollBarUse arrived = bar.Step(Held(800f, false), 0.9f, 608f, true);
        Assert.False(arrived.Consumed);

        ScrollBarUse above = bar.Step(Held(300f, true), 1f, 608f, true);              // thumb at the bottom: a click above it pages back
        Assert.Equal(-1, above.Page);
    }

    [Fact]
    public void ABarWithNothingToScrollTakesNothingAndLightsOnlyUnderThePointer()
    {
        var bar = new UiScrollBar();
        Assert.False(bar.Step(UiInput.From(UiInput.PointerFrame(BarX, 200f, PointerButtons.Left, PointerButtons.Left), default), 0f, 608f, enabled: false).Consumed);
        bar.Step(UiInput.From(UiInput.PointerFrame(BarX, UiScrollBar.ThumbTop(0f, 608f) + 60f, PointerButtons.None, PointerButtons.None), default), 0f, 608f, true);
        Assert.True(bar.Hover);
        bar.Step(UiInput.From(UiInput.PointerFrame(100f, 100f, PointerButtons.None, PointerButtons.None), default), 0f, 608f, true);
        Assert.False(bar.Hover);
        Assert.False(bar.Pressed);
    }

    // ---- the config screen -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void DraggingTheConfigScrollThumbScrollsTheTabAndAClickOnTheTrackPagesIt()
    {
        (UiRoot root, _, ConfigScreen screen) = OpenConfig();
        Assert.Equal(0f, screen.Scroll);
        float y = UiScrollBar.ThumbTop(0f, 608f) + 60f;
        UiTestKit.MouseDown(root, BarX, y);
        Assert.True(screen.Bar.Dragging);
        UiTestKit.MouseMove(root, BarX, y + 304f);                    // half the travel
        Assert.Equal(screen.ScrollMaximum / 2f, screen.Scroll, 1);
        UiTestKit.MouseMove(root, 20f, y + 9999f);                    // the pointer leaves the bar: the thumb goes to the end
        Assert.Equal(screen.ScrollMaximum, screen.Scroll);
        UiTestKit.MouseUp(root, 20f, 500f);
        Assert.False(screen.Bar.Dragging);

        // the thumb is at the bottom: a click on the track above it pages back (758 px, the view less a few rows; the System tab scrolls 1022 px since the port's rows of
        // packages K19 and K21, so the first click lands 264 px short of the top and the second one reaches it), a click below it pages on
        UiTestKit.Click(root, BarX, 200f);
        UiTestKit.Run(root, 30);
        Assert.Equal(MathF.Max(0f, screen.ScrollMaximum - 758f), screen.Scroll);
        UiTestKit.Click(root, BarX, 200f);
        UiTestKit.Run(root, 30);
        Assert.Equal(0f, screen.Scroll);
        UiTestKit.Click(root, BarX, 950f);
        UiTestKit.Run(root, 30);
        Assert.Equal(758f, screen.Scroll);
        UiTestKit.Click(root, BarX, 950f);
        UiTestKit.Run(root, 30);
        Assert.Equal(screen.ScrollMaximum, screen.Scroll);
    }

    [Fact]
    public void TheWheelStillScrollsTheConfigTabAndTheThumbLightsOnHover()
    {
        (UiRoot root, _, ConfigScreen screen) = OpenConfig();
        UiTestKit.MouseWheel(root, 900f, 500f, -3);
        UiTestKit.Run(root, 30);
        Assert.True(screen.Scroll > 100f);
        UiTestKit.MouseHover(root, BarX, UiScrollBar.ThumbTop(screen.Scroll / screen.ScrollMaximum, 608f) + 100f);
        Assert.True(screen.Bar.Hover);
        UiTestKit.MouseHover(root, 900f, 500f);
        Assert.False(screen.Bar.Hover);
    }

    [Fact]
    public void TheControlsTabScrollsWithItsBarToo()
    {
        (UiRoot root, _, ConfigScreen screen) = OpenConfig();
        screen.Change(new Ctl(CtlKind.Tab, 2), 0);
        UiTestKit.Run(root, 60);
        Assert.Equal(2, screen.CurrentTab);
        Assert.True(screen.ScrollMaximum > 300f);
        float y = UiScrollBar.ThumbTop(0f, 608f) + 60f;
        UiTestKit.Drag(root, BarX, y, BarX, y + 9999f);
        Assert.True(Math.Abs(screen.ScrollMaximum - screen.Scroll) < 1f, $"{screen.Scroll} of {screen.ScrollMaximum}");               // (the scroll is cut to whole pixels)
        Assert.True(screen.Scroll > 300f);
        UiTestKit.Click(root, BarX, 200f);
        UiTestKit.Run(root, 30);
        Assert.True(screen.Scroll < screen.ScrollMaximum);
    }

    [Fact]
    public void AClickOnTheShoulderPairOfTheGuideSwitchesTabsLeftHalfBackRightHalfOn()
    {
        (UiRoot root, _, ConfigScreen screen) = OpenConfig();
        UiTestKit.Draw(root);
        GuideHit pair = Assert.Single(root.Painter.GuideHits, h => h.Action == GuideAction.Shoulders);
        UiTestKit.Click(root, pair.Rect.X + (pair.Rect.W * 0.8f), pair.Rect.Y + 20f);          // R1: the next tab
        UiTestKit.Run(root, 60);
        Assert.Equal(1, screen.CurrentTab);
        UiTestKit.Draw(root);
        UiTestKit.Click(root, pair.Rect.X + (pair.Rect.W * 0.2f), pair.Rect.Y + 20f);          // L1: the previous tab
        UiTestKit.Run(root, 60);
        Assert.Equal(0, screen.CurrentTab);
    }

    private static (float X, float Y) SliderOrigin(ConfigScreen screen, int index)
    {
        (Ctl _, float x, float y) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.Slider, index));
        return (x, y);
    }

    private static float DotX(float sliderX, float dot) => sliderX + 468f + (dot * 127f);

    private static float TrackY(float sliderY) => sliderY + (39f * UiMetrics.UnitY) + 20f;

    [Fact]
    public void AClickOnAStepDotSetsTheSliderToThatDot()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = OpenConfig();
        (float x, float y) = SliderOrigin(screen, 0);
        UiTestKit.Click(root, DotX(x, 2f), TrackY(y));
        Assert.Equal(125, s.Config.UnseenTextSpeed);                    // the middle of 0..250
        UiTestKit.Click(root, DotX(x, 4f) + 4f, TrackY(y));            // a few pixels beside the last dot: still the maximum
        Assert.Equal(250, s.Config.UnseenTextSpeed);
        UiTestKit.Click(root, DotX(x, 0f) - 6f, TrackY(y));
        Assert.Equal(0, s.Config.UnseenTextSpeed);
        UiTestKit.Click(root, DotX(x, 1.5f), TrackY(y));               // between dots: where the pointer is
        Assert.InRange(s.Config.UnseenTextSpeed, 93, 95);
    }

    [Fact]
    public void ClickingTheKnobGrabsItWithoutAJumpAndDraggingMovesItWhereverThePointerGoes()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = OpenConfig();
        (float x, float y) = SliderOrigin(screen, 0);
        ushort start = s.Config.UnseenTextSpeed;
        float knob = x + 468f + (start / 250f * 508f);
        UiTestKit.MouseDown(root, knob + 25f, TrackY(y));              // at the knob's edge, not its centre
        Assert.Equal(start, s.Config.UnseenTextSpeed);
        UiTestKit.MouseMove(root, knob + 25f + 127f, TrackY(y) + 400f);    // a quarter of the track to the right and far off the row
        Assert.InRange(s.Config.UnseenTextSpeed, start + 61, start + 64);
        UiTestKit.MouseMove(root, 9999f, 20f);
        Assert.Equal(250, s.Config.UnseenTextSpeed);
        UiTestKit.MouseUp(root, 9999f, 20f);
        UiTestKit.MouseHover(root, knob - 200f, TrackY(y));            // released: moving does nothing more
        Assert.Equal(250, s.Config.UnseenTextSpeed);
    }

    [Fact]
    public void TheAutoModeSlidersKeepTheirFiftyMillisecondSteps()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = OpenConfig();
        (float x, float y) = SliderOrigin(screen, 2);
        UiTestKit.Click(root, DotX(x, 3f), TrackY(y));                // far from the knob (at 3000): the knob jumps to the dot
        Assert.Equal(7500, s.Config.AutoModePageSpeed);
        UiTestKit.Drag(root, DotX(x, 3f), TrackY(y), DotX(x, 1.31f), TrackY(y));
        Assert.Equal(0, s.Config.AutoModePageSpeed % 50);
        Assert.InRange(s.Config.AutoModePageSpeed, 3200, 3350);
    }

    [Fact]
    public void TheMinusAndPlusButtonsRepeatWhileTheButtonIsHeld()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = OpenConfig();
        (Ctl _, float x, float y) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.Plus, 0));
        UiTestKit.MouseDown(root, x + 10f, y + 10f);
        Assert.Equal(36, s.Config.UnseenTextSpeed);
        for (int i = 0; i < 50; i++)
        {
            UiTestKit.MouseMove(root, x + 10f, y + 10f);
        }

        UiTestKit.MouseUp(root, x + 10f, y + 10f);
        Assert.True(s.Config.UnseenTextSpeed > 40);
    }

    [Fact]
    public void ARadioButtonACheckBoxAndTheRecommendedSettingsButtonAnswerClicks()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = OpenConfig();
        (Ctl _, float tx, float ty) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.Toggle, 0));
        byte before = s.Config.PageEndInstantUnseen;
        UiTestKit.Click(root, tx + 20f, ty + 20f);
        Assert.NotEqual(before, s.Config.PageEndInstantUnseen);

        (Ctl _, float rx, float ry) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.Radio, 0, false));
        UiTestKit.Click(root, rx + 20f, ry + 20f);
        Assert.False(screen.IsOn(new Ctl(CtlKind.Radio, 0, true)));
        (Ctl _, float dx, float dy) = screen.Layout.First(t => t.Ctl.Kind == CtlKind.Default);
        UiTestKit.Click(root, dx + 20f, dy + 20f);
        UiTestKit.Run(root, 40);
        var dialog = Assert.IsType<ConfirmDialog>(root.Top);
        Assert.NotNull(dialog);
    }

    // ---- the system menu's tabs --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePointerTakesTheFocusBackFromTheTabsWhenItMovesOntoThePage()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = OpenConfig();
        UiTestKit.Press(root, PadButtons.Left);
        Assert.True(screen.Side.Active);
        UiTestKit.MouseHover(root, 1000f, 500f);
        Assert.False(screen.Side.Active);
        // a click on a slider's dot now acts (while the tabs had the focus the page ignored the mouse)
        (float x, float y) = SliderOrigin(screen, 0);
        UiTestKit.Click(root, DotX(x, 2f), TrackY(y));
        Assert.Equal(125, s.Config.UnseenTextSpeed);
    }

    // ---- the save list -----------------------------------------------------------------------------------------------------------------------------

    private static (UiRoot Root, PreviewServices Services, SaveLoadScreen Screen) OpenSave(bool save, int used)
    {
        var services = new PreviewServices(Language.En, used);
        UiRoot root = UiTestKit.Root(services);
        root.OpenSystemMenu();
        UiTestKit.Run(root, 20);
        root.Context.MenuTab = save ? 0 : 1;
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 60);
        return (root, services, Assert.IsType<SaveLoadScreen>(root.Top));
    }

    [Fact]
    public void TheSaveListScrollBarDragsAndPages()
    {
        (UiRoot root, _, SaveLoadScreen screen) = OpenSave(save: true, used: 20);
        Assert.Equal(21, screen.List.Count);
        int range = 21 - 4;
        float y = UiScrollBar.ThumbTop(0f, 608f) + 60f;
        UiTestKit.MouseDown(root, BarX, y);
        UiTestKit.MouseMove(root, BarX, y + 304f);
        Assert.InRange(screen.Scroll, range / 2, (range / 2) + 1);
        UiTestKit.MouseMove(root, BarX, y + 700f);
        Assert.Equal(range, screen.Scroll);
        UiTestKit.MouseUp(root, BarX, y + 700f);
        UiTestKit.Click(root, BarX, 150f);                           // the track above the thumb at the bottom: a page back
        Assert.Equal(range - 4, screen.Scroll);
        UiTestKit.Click(root, BarX, 990f);
        Assert.Equal(range, screen.Scroll);
        Assert.DoesNotContain(root.Stack, x => x is ConfirmDialog);    // none of it picked a slot
    }

    [Fact]
    public void AClickOnASlotOpensItAndAClickOnTheGuidesBackWordClosesTheList()
    {
        (UiRoot root, PreviewServices s, SaveLoadScreen screen) = OpenSave(save: false, used: 6);
        (float fx, float fy) = SaveLoadScreen.FramePosition(1);
        UiTestKit.Click(root, fx + 200f, fy + 100f);
        Assert.IsType<ConfirmDialog>(root.Top);
        UiTestKit.Run(root, 30);                                      // the dialog takes input once it has faded in
        UiTestKit.Click(root, 860f, 628f);                           // the No button
        UiTestKit.Run(root, 60);

        // the guide: draw once so the root knows where its words are, then click the Back word
        UiTestKit.Draw(root);
        Assert.Contains(root.Painter.GuideHits, h => h.Action == GuideAction.Back);
        GuideHit back = root.Painter.GuideHits.First(h => h.Action == GuideAction.Back);
        UiTestKit.Click(root, back.Rect.X + 10f, back.Rect.Y + 20f);
        UiTestKit.Run(root, 80);
        Assert.DoesNotContain(screen, root.Stack);
        Assert.Contains(s.Calls, c => c.StartsWith("sound", StringComparison.Ordinal));
    }

    [Fact]
    public void AClickOnTheDeleteDataWordOfTheSaveListAsksToDeleteTheSlotUnderTheCursor()
    {
        (UiRoot root, _, SaveLoadScreen screen) = OpenSave(save: true, used: 6);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Draw(root);
        GuideHit del = Assert.Single(root.Painter.GuideHits, h => h.Action == GuideAction.DeleteData);
        UiTestKit.Click(root, del.Rect.X + 20f, del.Rect.Y + 20f);
        Assert.IsType<ConfirmDialog>(root.Top);
        Assert.Same(screen, root.Stack[^2]);
    }

    // ---- the backlog -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheBacklogScrollBarDragsThroughThePagesAndAClickOnTheTrackStepsFive()
    {
        var services = new PreviewServices();
        services.SetBacklog(Enumerable.Range(0, 12).Select(_ => new BacklogPage([new BacklogMessage(System.Text.Encoding.UTF8.GetBytes("line"))])));
        UiRoot root = UiTestKit.Root(services);
        Assert.True(root.OpenBacklog());
        UiTestKit.Run(root, 30);
        var screen = Assert.IsType<BacklogScreen>(root.Top);
        Assert.Equal(0, screen.Page);

        // the newest page has the thumb at the bottom; dragging it to the top goes to the oldest
        float y = UiScrollBar.ThumbTop(1f, 608f) + 60f;
        UiTestKit.MouseDown(root, BarX, y);
        UiTestKit.MouseMove(root, BarX, y - 304f);
        Assert.InRange(screen.Page, 5, 6);
        UiTestKit.MouseMove(root, BarX, 0f);
        Assert.Equal(11, screen.Page);
        UiTestKit.MouseUp(root, BarX, 0f);
        UiTestKit.Click(root, BarX, 990f);                            // below the thumb (now at the top): five pages newer
        Assert.Equal(6, screen.Page);
        UiTestKit.MouseWheel(root, 500f, 500f, 1);
        Assert.Equal(7, screen.Page);
        Assert.IsType<BacklogScreen>(root.Top);                       // none of it asked for a jump
    }

    // ---- the gallery -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheMusicListScrollBarAndTheVolumeSliderFollowADrag()
    {
        (UiRoot root, PreviewServices s, _, GalleryMusicPanel music) = OpenMusic();
        float y = UiScrollBar.ThumbTop(0f, 610f) + 60f;
        UiTestKit.MouseDown(root, BarX, y);
        for (int i = 0; i < 12; i++)
        {
            UiTestKit.MouseMove(root, BarX, y + 305f);
        }

        Assert.InRange(music.Scroll, 2300f, 2420f);
        UiTestKit.MouseUp(root, BarX, y + 305f);
        UiTestKit.Click(root, BarX, 990f);
        UiTestKit.Run(root, 30);
        Assert.True(music.Scroll > 2800f);

        // the volume slider: a click sets it, a drag follows the pointer along the line and past its ends
        UiTestKit.MouseDown(root, 1412f + (364f * 0.5f), 800f);
        Assert.Equal(0.5f, music.Volume, 3);
        UiTestKit.MouseMove(root, 1412f + (364f * 0.25f), 20f);
        Assert.Equal(0.25f, music.Volume, 3);
        UiTestKit.MouseMove(root, 9000f, 900f);
        Assert.Equal(1f, music.Volume, 3);
        UiTestKit.MouseUp(root, 9000f, 900f);
        Assert.Contains(s.Calls, c => c.StartsWith("music volume", StringComparison.Ordinal));
    }

    private static (UiRoot Root, PreviewServices S, GalleryScreen Gallery, GalleryMusicPanel Music) OpenMusic()
    {
        (UiRoot root, PreviewServices s, GalleryScreen gallery) = GalleryKit.OpenGallery(p =>
        {
            p.Progress.Bgm.Add(1);
            p.Progress.Bgm.Add(2);
            p.Progress.Bgm.Add(126);
        });
        UiTestKit.Click(root, 200f, 160f + (1 * 80f) + 40f);          // the Music Player button
        UiTestKit.Run(root, 80);
        return (root, s, gallery, Assert.IsType<GalleryMusicPanel>(gallery.Panel));
    }

    [Fact]
    public void AClickOnTheCgViewerGoesToTheNextPictureAndTheLeftQuarterToThePreviousOneButADragOnlyPans()
    {
        (UiRoot root, _, GalleryScreen _, _) = OpenCg();
        UiTestKit.Click(root, 500f, 170f);                            // the first group's only picture opens the viewer
        UiTestKit.Run(root, 40);
        var viewer = Assert.IsType<GalleryCgViewer>(root.Top);
        UiTestKit.Run(root, 40);
        Assert.Equal((0, 0), (viewer.Group, viewer.Pos));
        UiTestKit.Drag(root, 900f, 500f, 1000f, 560f);                // a drag: no picture change
        Assert.Equal((0, 0), (viewer.Group, viewer.Pos));
        UiTestKit.Click(root, 1500f, 500f);
        Assert.Equal((1, 0), (viewer.Group, viewer.Pos));
        UiTestKit.Run(root, 40);
        UiTestKit.Click(root, 100f, 500f);
        Assert.Equal((0, 0), (viewer.Group, viewer.Pos));
    }

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
}

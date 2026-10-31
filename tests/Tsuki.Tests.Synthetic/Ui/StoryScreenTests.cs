using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

public sealed class StoryScreenTests
{
    private static BacklogPage Page(int voice, bool jumpable = true) =>
        new([new BacklogMessage(Encoding.UTF8.GetBytes("hello"), 0, voice), new BacklogMessage(Encoding.UTF8.GetBytes("world"))], jumpable);

    private static (UiRoot Root, PreviewServices Services, BacklogScreen Screen) OpenBacklog(int pages = 5)
    {
        var services = new PreviewServices();
        services.SetBacklog(Enumerable.Range(0, pages).Select(i => Page(i == 2 ? 77 : -1, jumpable: i != 3)));
        UiRoot root = UiTestKit.Root(services);
        Assert.True(root.OpenBacklog());
        UiTestKit.Run(root, 30);
        return (root, services, Assert.IsType<BacklogScreen>(root.Top));
    }

    [Fact]
    public void TheBacklogOpensOnlyWithHistoryAndNotOverAnotherScreen()
    {
        var services = new PreviewServices();
        UiRoot root = UiTestKit.Root(services);
        Assert.False(root.OpenBacklog());
        services.SetBacklog([Page(-1)]);
        Assert.True(root.OpenBacklog());
        Assert.False(root.OpenBacklog());
        Assert.True(root.BlurScene);
        Assert.Contains("sound Open", services.Calls);
    }

    [Fact]
    public void UpAndDownStepPagesAndAFreshPressWrapsHeldRepeatsDoNot()
    {
        (UiRoot root, _, BacklogScreen screen) = OpenBacklog();
        Assert.Equal(0, screen.Page);
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(4, screen.Page);
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(0, screen.Page);
        UiTestKit.Press(root, PadButtons.Up);
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(2, screen.Page);
        root.Step(new InputFrame { Frame = 900, Repeated = PadButtons.Down, Held = PadButtons.Down });
        root.Step(new InputFrame { Frame = 901, Repeated = PadButtons.Down, Held = PadButtons.Down });
        root.Step(new InputFrame { Frame = 902, Repeated = PadButtons.Down, Held = PadButtons.Down });
        Assert.Equal(0, screen.Page);
    }

    [Fact]
    public void LeftRightAndTheShouldersGoToTheEnds()
    {
        (UiRoot root, _, BacklogScreen screen) = OpenBacklog();
        UiTestKit.Press(root, PadButtons.Left);
        Assert.Equal(4, screen.Page);
        UiTestKit.Press(root, PadButtons.Right);
        Assert.Equal(0, screen.Page);
        UiTestKit.Press(root, PadButtons.L1);
        Assert.Equal(4, screen.Page);
        UiTestKit.Press(root, PadButtons.R1);
        Assert.Equal(0, screen.Page);
    }

    [Fact]
    public void ADecideAsksAndYesJumpsBackToThePageAndClosesTheUi()
    {
        (UiRoot root, PreviewServices s, BacklogScreen screen) = OpenBacklog();
        UiTestKit.Press(root, PadButtons.Up);
        UiTestKit.Press(root, PadButtons.Decide);
        var dialog = Assert.IsType<ConfirmDialog>(root.Top);
        UiTestKit.Run(root, 20);
        Assert.False(dialog.CursorOnYes);
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 80);
        Assert.Contains("backlog jump 1", s.Calls);
        Assert.False(root.Active);
        _ = screen;
    }

    [Fact]
    public void APageThatCannotBeJumpedToBuzzes()
    {
        (UiRoot root, PreviewServices s, _) = OpenBacklog();
        for (int i = 0; i < 3; i++)
        {
            UiTestKit.Press(root, PadButtons.Up);
        }

        UiTestKit.Press(root, PadButtons.Decide);
        Assert.IsType<BacklogScreen>(root.Top);
        Assert.Contains("sound Buzzer", s.Calls);
    }

    [Fact]
    public void ClickingAMessageWithAVoiceReplaysItAndOtherClicksAskForTheJump()
    {
        (UiRoot root, PreviewServices s, BacklogScreen screen) = OpenBacklog();
        UiTestKit.Press(root, PadButtons.Up);
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(2, screen.Page);
        // the first message of the page starts at the message origin (30, 35) of the 640 x 344.7 text space
        UiTestKit.Point(root, 40f * 3f, 40f * (1080f / 344.7f), click: true);
        Assert.Contains("backlog voice 77", s.Calls);
        UiTestKit.Point(root, 40f * 3f, 300f * (1080f / 344.7f), click: true);
        Assert.IsType<ConfirmDialog>(root.Top);
    }

    [Fact]
    public void TheReplayShortcutPlaysTheFirstVoiceOfThePage()
    {
        (UiRoot root, PreviewServices s, _) = OpenBacklog();
        UiTestKit.Press(root, PadButtons.Up);
        UiTestKit.Press(root, PadButtons.Up);
        root.Step(new InputFrame { Frame = 700, ShellPressed = ShellActions.ReplayVoice });
        Assert.Contains("backlog voice 77", s.Calls);
    }

    [Fact]
    public void CancelTriangleAndL2CloseTheBacklogWithoutAJump()
    {
        foreach (PadButtons close in new[] { PadButtons.Cancel, PadButtons.Triangle, PadButtons.L2 })
        {
            (UiRoot root, PreviewServices s, _) = OpenBacklog();
            UiTestKit.Press(root, close);
            UiTestKit.Run(root, 40);
            Assert.False(root.Active);
            Assert.DoesNotContain(s.Calls, c => c.StartsWith("backlog jump", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheBacklogDrawsTheDimmerTheScrollBarAndTheGuide()
    {
        (UiRoot root, _, _) = OpenBacklog();
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        Assert.Contains(cmds, c => c.Kind == DrawKind.FillQuad && (c.V0.Rgba >> 24) == 191);
        Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.DispParts, C: 14 });
        Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.DispParts, C: 16 });
    }

    // ---- mode panel ---------------------------------------------------------------------------------------------------------

    private static (UiRoot Root, PreviewServices Services, ModePanelScreen Panel) OpenPanel(Action<PreviewServices>? tweak = null)
    {
        var services = new PreviewServices();
        tweak?.Invoke(services);
        UiRoot root = UiTestKit.Root(services);
        Assert.True(root.OpenModePanel());
        UiTestKit.Run(root, 30);
        return (root, services, Assert.IsType<ModePanelScreen>(root.Top));
    }

    [Fact]
    public void ThePanelSlidesDownInTwentyFramesAndStartsOnTheSkipButton()
    {
        var services = new PreviewServices();
        UiRoot root = UiTestKit.Root(services);
        root.OpenModePanel();
        var panel = (ModePanelScreen)root.Top!;
        Assert.Equal(-43.14f, panel.PanelY, 2);
        UiTestKit.Run(root, 10);
        Assert.InRange(panel.PanelY, -11.5f, -10f);
        UiTestKit.Run(root, 12);
        Assert.Equal(0f, panel.PanelY, 3);
        Assert.True(panel.IsOpen);
        Assert.Equal(3, panel.Key);
    }

    [Fact]
    public void ThePanelDoesNotOpenWhereTheStoryForbidsItOrOverAScreen()
    {
        var services = new PreviewServices { PanelAllowed = false };
        UiRoot root = UiTestKit.Root(services);
        Assert.False(root.OpenModePanel());
        services.PanelAllowed = true;
        Assert.True(root.OpenModePanel());
        Assert.False(root.OpenModePanel());
    }

    [Fact]
    public void LeftAndRightWalkTheEightButtonsAndWrapOnAFreshPress()
    {
        (UiRoot root, _, ModePanelScreen panel) = OpenPanel();
        UiTestKit.Press(root, PadButtons.Right);
        Assert.Equal(4, panel.Key);
        for (int i = 0; i < 3; i++)
        {
            UiTestKit.Press(root, PadButtons.Right);
        }

        Assert.Equal(7, panel.Key);
        UiTestKit.Press(root, PadButtons.Right);
        Assert.Equal(0, panel.Key);
        UiTestKit.Press(root, PadButtons.Left);
        Assert.Equal(7, panel.Key);
    }

    [Fact]
    public void AutoTogglesAndShowsItsBannerWhenItTurnsOn()
    {
        (UiRoot root, PreviewServices s, ModePanelScreen panel) = OpenPanel();
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Left);
        Assert.Equal(1, panel.Key);
        UiTestKit.Press(root, PadButtons.Right);
        Assert.Equal(2, panel.Key);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.True(s.AutoMode);
        Assert.Equal(BannerKind.Auto, root.Banner!.Kind);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.False(s.AutoMode);
    }

    [Fact]
    public void QuickSaveShowsTheBannerAndQuickLoadAndSceneJumpsAskFirst()
    {
        (UiRoot root, PreviewServices s, ModePanelScreen panel) = OpenPanel();
        for (int i = 0; i < 2; i++)
        {
            UiTestKit.Press(root, PadButtons.Right);
        }

        Assert.Equal(5, panel.Key);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Contains("quick save", s.Calls);
        Assert.Equal(BannerKind.QuickSave, root.Banner!.Kind);

        UiTestKit.Press(root, PadButtons.Right);
        UiTestKit.Press(root, PadButtons.Decide);
        var dialog = Assert.IsType<ConfirmDialog>(root.Top);
        Assert.False(dialog.CursorOnYes);
        UiTestKit.Run(root, 20);
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 60);
        Assert.Contains("quick load", s.Calls);
        Assert.False(root.Active);
    }

    [Fact]
    public void NextAndPreviousSceneAskThenJumpAndCloseThePanel()
    {
        (UiRoot root, PreviewServices s, ModePanelScreen panel) = OpenPanel();
        UiTestKit.Press(root, PadButtons.Right);
        Assert.Equal(4, panel.Key);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.IsType<ConfirmDialog>(root.Top);
        UiTestKit.Run(root, 20);
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 80);
        Assert.Contains("next scene", s.Calls);
        Assert.False(root.Active);
    }

    [Fact]
    public void UnavailableButtonsBuzzAndDrawDark()
    {
        (UiRoot root, PreviewServices s, ModePanelScreen panel) = OpenPanel(x =>
        {
            x.SceneJumpEnabled = false;
            x.SavingDisabled = true;
        });
        Assert.True(panel.Disabled(0));
        Assert.True(panel.Disabled(4));
        Assert.True(panel.Disabled(5));
        Assert.True(panel.Disabled(6));
        Assert.False(panel.Disabled(2));
        UiTestKit.Press(root, PadButtons.Right);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Contains("sound Buzzer", s.Calls);
        Assert.IsType<ModePanelScreen>(root.Top);
        // disabled buttons draw the first row (y 8) of the sheet
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        Assert.Equal(4, cmds.Count(c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.ModePnlParts, C: 1 } && Math.Abs(c.V0.V - (8f / 4000f)) < 1e-6));
    }

    [Fact]
    public void RewindAndSkipActWhileTheirButtonIsHeld()
    {
        (UiRoot root, PreviewServices s, _) = OpenPanel();
        // the pad starts on skip (3): hold decide
        root.Step(new InputFrame { Frame = 800, Pressed = PadButtons.Decide, Held = PadButtons.Decide });
        root.Step(new InputFrame { Frame = 801, Held = PadButtons.Decide });
        Assert.Contains("fast forward True", s.Calls);
        root.Step(new InputFrame { Frame = 802 });
        Assert.Contains("fast forward False", s.Calls);
    }

    [Fact]
    public void DownCancelAndTheMenuButtonsCloseThePanelAndAClickBelowTooButTheBannerOutlivesIt()
    {
        foreach (PadButtons close in new[] { PadButtons.Down, PadButtons.Cancel, PadButtons.Square })
        {
            (UiRoot root, _, _) = OpenPanel();
            UiTestKit.Press(root, close);
            UiTestKit.Run(root, 30);
            Assert.False(root.Active);
        }

        (UiRoot r2, _, _) = OpenPanel();
        UiTestKit.Point(r2, 960, 800, click: true);
        UiTestKit.Run(r2, 30);
        Assert.False(r2.Active);
    }

    [Fact]
    public void ThePointerHoversAndClicksTheButtons()
    {
        (UiRoot root, PreviewServices s, ModePanelScreen panel) = OpenPanel();
        RectF auto = panel.ButtonRect(2);
        UiTestKit.Point(root, auto.X + 20, auto.Y + 20);
        Assert.Equal(2, panel.Hover);
        UiTestKit.Point(root, auto.X + 20, auto.Y + 20, click: true);
        Assert.True(s.AutoMode);
    }

    // ---- dialogs, popups, banners -----------------------------------------------------------------------------------------

    [Fact]
    public void ADialogStartsOnItsDefaultButtonAndAnyDirectionToggles()
    {
        var services = new PreviewServices();
        UiRoot root = UiTestKit.Root(services);
        var yesFirst = root.Push(new ConfirmDialog(ConfirmKind.Load, _ => { }));
        UiTestKit.Run(root, 20);
        Assert.True(yesFirst.CursorOnYes);
        UiTestKit.Press(root, PadButtons.Up);
        Assert.False(yesFirst.CursorOnYes);
        UiTestKit.Press(root, PadButtons.Left);
        Assert.True(yesFirst.CursorOnYes);
    }

    [Fact]
    public void YesActsAfterAFifteenFrameHoldAndNoAfterOne()
    {
        var services = new PreviewServices();
        UiRoot root = UiTestKit.Root(services);
        var answers = new List<bool>();
        root.Push(new ConfirmDialog(ConfirmKind.Load, answers.Add));
        UiTestKit.Run(root, 20);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 10);
        Assert.Empty(answers);
        UiTestKit.Run(root, 10);
        Assert.Equal([true], answers);

        root.Push(new ConfirmDialog(ConfirmKind.Load, answers.Add));
        UiTestKit.Run(root, 20);
        UiTestKit.Press(root, PadButtons.Cancel);
        UiTestKit.Run(root, 3);
        Assert.Equal([true, false], answers);
        UiTestKit.Run(root, 40);
        Assert.False(root.Active);
    }

    [Fact]
    public void TheDialogMessagesComeFromTheEbootsDialogTableWithThePs4Shift()
    {
        var services = new PreviewServices();
        UiRoot root = UiTestKit.Root(services);
        UiAssets assets = root.Context.Assets;
        Assert.Equal(1000, root.Push(new ConfirmDialog(ConfirmKind.Load, _ => { })).MessageId(assets));
        Assert.Equal(1002, new ConfirmDialog(ConfirmKind.Overwrite, _ => { }).MessageId(assets));
        Assert.Equal(1008, new ConfirmDialog(ConfirmKind.Title, _ => { }).MessageId(assets));
        Assert.Equal(1011, new ConfirmDialog(ConfirmKind.PrevScene, _ => { }).MessageId(assets));
        Assert.Equal(1010, new ConfirmDialog(ConfirmKind.NextScene, _ => { }).MessageId(assets));
        Assert.Equal(1009, new ConfirmDialog(ConfirmKind.QuickLoad, _ => { }).MessageId(assets));
        // knife's dialogs 21 / 22 / 23 are the PS4 table's entries 23 / 24 / 25
        Assert.Equal(1023, new ConfirmDialog(ConfirmKind.Delete, _ => { }).MessageId(assets));
        Assert.Equal(1024, new ConfirmDialog(ConfirmKind.Copy, _ => { }).MessageId(assets));
        Assert.Equal(1025, new ConfirmDialog(ConfirmKind.BacklogJump, _ => { }).MessageId(assets));
        Assert.Equal(160, new ConfirmDialog(ConfirmKind.Defaults, _ => { }).MessageId(assets));
    }

    [Fact]
    public void APopupStaysItsFramesThenFadesAndAnyButtonClosesItEarly()
    {
        var services = new PreviewServices();
        UiRoot root = UiTestKit.Root(services);
        var popup = root.Push(new MessagePopup("hi", frames: 30f));
        UiTestKit.Run(root, 10);
        Assert.False(popup.FullyShown);
        UiTestKit.Run(root, 10);
        Assert.True(popup.FullyShown);
        UiTestKit.Run(root, 60);
        Assert.False(root.Active);

        root.Push(new MessagePopup("again", frames: 600f));
        UiTestKit.Run(root, 20);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 20);
        Assert.False(root.Active);
    }

    [Fact]
    public void AHeldPopupStaysUntilReleasedAndItsMinimumHasPassed()
    {
        var services = new PreviewServices();
        UiRoot root = UiTestKit.Root(services);
        var popup = root.Push(new MessagePopup("saving", held: true, minimumFrames: 30f));
        UiTestKit.Run(root, 100);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.True(root.Active);
        popup.Release();
        UiTestKit.Run(root, 30);
        Assert.False(root.Active);
    }

    [Fact]
    public void TheBannerPopsInHoldsAndGoesAndAButtonSendsItAway()
    {
        var services = new PreviewServices();
        UiRoot root = UiTestKit.Root(services);
        root.ShowBanner(BannerKind.QuickSave);
        UiTestKit.Run(root, 10);
        Assert.NotNull(root.Banner);
        Assert.InRange(root.Banner!.T, 0.3f, 0.5f);
        UiTestKit.Run(root, 30);
        root.DismissBanner();
        Assert.Equal(3f, root.Banner.T);
        UiTestKit.Run(root, 3);
        Assert.True(root.Banner is null || root.Banner.Leaving);
        UiTestKit.Run(root, 40);
        Assert.Null(root.Banner);
        // drawn from the second row of DISP_PARTS child 10
        root.ShowBanner(BannerKind.Skip);
        UiTestKit.Run(root, 5);
        Assert.Contains(UiTestKit.Draw(root).Commands.ToArray(), c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.DispParts, C: 10 });
    }

    [Fact]
    public void TheBootNoticeFadesInHoldsAndHandsOverToTheTitle()
    {
        var services = new PreviewServices();
        UiRoot root = UiTestKit.Root(services);
        root.Context.TitleIntroShown = true;
        var boot = root.Push(new BootScreen());
        UiTestKit.Run(root, 44);
        Assert.True(boot.Alpha < 1.1f);
        UiTestKit.Run(root, 2);
        Assert.True(boot.Alpha >= 1.1f);
        UiTestKit.Run(root, 239);
        Assert.IsType<BootScreen>(root.Stack[0]);
        UiTestKit.Run(root, 60);
        Assert.IsType<TitleScreen>(root.Stack[0]);
        Assert.Equal(UiMode.Title, root.Mode);

        // a button ends the hold at once: 45 + 1 + 45 frames
        UiRoot fast = UiTestKit.Root(new PreviewServices());
        fast.Context.TitleIntroShown = true;
        fast.Push(new BootScreen());
        UiTestKit.Run(fast, 50);
        UiTestKit.Press(fast, PadButtons.Decide);
        UiTestKit.Run(fast, 50);
        Assert.IsType<TitleScreen>(fast.Stack[0]);
    }
}

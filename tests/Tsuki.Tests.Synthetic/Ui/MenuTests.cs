using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

public sealed class MenuTests
{
    private static (UiRoot Root, PreviewServices Services, SystemMenuScreen Menu) OpenMenu(Action<PreviewServices>? tweak = null)
    {
        var services = new PreviewServices();
        tweak?.Invoke(services);
        UiRoot root = UiTestKit.Root(services);
        Assert.True(root.OpenSystemMenu());
        SystemMenuScreen menu = Assert.IsType<SystemMenuScreen>(root.Top);
        UiTestKit.Run(root, 30);
        return (root, services, menu);
    }

    [Fact]
    public void TheMenuOpensWithASoundAndABackdropAndFadesInOver18Frames()
    {
        var services = new PreviewServices();
        UiRoot root = UiTestKit.Root(services);
        root.OpenSystemMenu();
        Assert.Contains("sound Open", services.Calls);
        Assert.False(root.OpenSystemMenu());
        UiTestKit.Run(root, 9);
        Assert.InRange(root.Top!.Fade, 0.4f, 0.6f);
        UiTestKit.Run(root, 10);
        Assert.Equal(1f, root.Top.Fade, 3);
        Assert.True(root.Context.Backdrop.Visible);
        Assert.True(root.BlurScene);
        Assert.Equal(UiMode.Overlay, root.Mode);
    }

    [Fact]
    public void TheSceneBehindTheMenuIsBlurredAndTheBlurFadesInOverTwelveFrames()
    {
        // package K16: capture, blur along x into an offscreen target, capture, blur along y over the frame at the fade alpha, all before the menu's own quads
        var services = new PreviewServices();
        UiRoot root = UiTestKit.Root(services);
        Assert.Equal(0f, root.BlurAlpha);
        Assert.DoesNotContain(UiTestKit.Draw(root).Commands.ToArray(), c => c.Fx.Pass == QuadPass.Blur);
        root.OpenSystemMenu();
        UiTestKit.Run(root, 1);
        Assert.InRange(root.BlurAlpha, 0.08f, 0.09f);
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        int firstBlur = Array.FindIndex(cmds, c => c.Kind == DrawKind.Capture);
        Assert.True(firstBlur >= 0);
        Assert.Equal(DrawKind.Capture, cmds[firstBlur].Kind);
        Assert.Equal((byte)0, cmds[firstBlur].Target);
        Assert.Equal(UiMenuBlur.SceneSurface, cmds[firstBlur].Texture.A);
        Assert.Equal(QuadPass.Blur, cmds[firstBlur + 1].Fx.Pass);
        Assert.Equal(UiMenuBlur.PassTarget, cmds[firstBlur + 1].Target);
        Assert.Equal(1f / 1920f, cmds[firstBlur + 1].Fx.A0);                                      // the first pass runs along x
        Assert.Equal((DrawKind.Capture, UiMenuBlur.PassTarget, UiMenuBlur.PassSurface), (cmds[firstBlur + 2].Kind, cmds[firstBlur + 2].Target, cmds[firstBlur + 2].Texture.A));
        DrawCmd second = cmds[firstBlur + 3];
        Assert.Equal((QuadPass.Blur, (byte)0, (byte)1), (second.Fx.Pass, second.Target, second.BlendState));
        Assert.Equal(1f / 1080f, second.Fx.B0 + second.Fx.A1);                                    // the second runs along y
        Assert.InRange((int)(second.V0.Rgba >> 24), 20, 22);                                      // 1/12 of 255
        Assert.Equal(firstBlur, Array.FindIndex(cmds, c => c.Fx.Pass == QuadPass.Blur) - 1);      // before every sprite of the menu
        Assert.All(cmds.Take(firstBlur), c => Assert.NotEqual(QuadPass.Blur, c.Fx.Pass));

        UiTestKit.Run(root, 20);
        Assert.Equal(1f, root.BlurAlpha);
        Assert.Equal(255, (int)(UiTestKit.Draw(root).Commands.ToArray().First(c => c.Fx.Pass == QuadPass.Blur && c.Target == 0).V0.Rgba >> 24));
    }

    [Fact]
    public void TheBlurEndsWithTheMenuAndStartsFromNothingAgain()
    {
        var services = new PreviewServices();
        UiRoot root = UiTestKit.Root(services);
        root.OpenSystemMenu();
        UiTestKit.Run(root, 30);
        Assert.Equal(1f, root.BlurAlpha);
        root.Clear();
        UiTestKit.Run(root, 2);
        Assert.Equal(0f, root.BlurAlpha);
        Assert.DoesNotContain(UiTestKit.Draw(root).Commands.ToArray(), c => c.Kind == DrawKind.Capture || c.Fx.Pass == QuadPass.Blur);
        root.OpenSystemMenu();
        UiTestKit.Run(root, 1);
        Assert.InRange(root.BlurAlpha, 0.08f, 0.09f);
    }

    [Fact]
    public void UpAndDownWalkTheFiveTabsAndWrap()
    {
        (UiRoot root, PreviewServices s, _) = OpenMenu();
        Assert.Equal(0, root.Context.MenuTab);
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(4, root.Context.MenuTab);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(1, root.Context.MenuTab);
        Assert.Equal(3, s.Calls.Count(c => c == "sound Cursor"));
    }

    [Fact]
    public void TheTabIsRememberedAcrossInvocations()
    {
        (UiRoot root, _, SystemMenuScreen menu) = OpenMenu();
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Down);
        menu.Close();
        UiTestKit.Run(root, 40);
        Assert.False(root.Active);
        root.OpenSystemMenu();
        Assert.Equal(2, root.Context.MenuTab);
    }

    [Fact]
    public void CancelSquareAndL2CloseTheMenuAndCutTheBackdrop()
    {
        foreach (PadButtons close in new[] { PadButtons.Cancel, PadButtons.Square, PadButtons.L2 })
        {
            (UiRoot root, PreviewServices s, _) = OpenMenu();
            UiTestKit.Press(root, close);
            Assert.True(root.Top!.Closing);
            UiTestKit.Run(root, 40);
            Assert.False(root.Active);
            Assert.False(root.Context.Backdrop.Visible);
            Assert.Contains("sound Buzzer", s.Calls);
        }
    }

    [Fact]
    public void SaveAndLoadAreRefusedWhileSavingIsOff()
    {
        (UiRoot root, PreviewServices s, _) = OpenMenu(x => x.SavingDisabled = true);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.IsType<SystemMenuScreen>(root.Top);
        Assert.Contains("sound Buzzer", s.Calls);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.IsType<SystemMenuScreen>(root.Top);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.IsType<ConfigScreen>(root.Top);
    }

    [Fact]
    public void TheTabsOpenTheirScreensAndKeepTheMenuDrawnUnderneath()
    {
        (UiRoot root, _, SystemMenuScreen menu) = OpenMenu();
        UiTestKit.Press(root, PadButtons.Decide);
        var save = Assert.IsType<SaveLoadScreen>(root.Top);
        Assert.True(save.Save);
        Assert.Equal(0, menu.OpenTab);
        Assert.Equal(2, root.Stack.Count);
        UiTestKit.Run(root, 60);
        // the menu's own quads are still part of the frame
        Assert.Contains(UiTestKit.Draw(root).Commands.ToArray(), c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.MenuParts, C: 0 });
    }

    [Fact]
    public void FlowChartIsHandedToPackageK5b()
    {
        (UiRoot root, PreviewServices s, _) = OpenMenu();
        for (int i = 0; i < 3; i++)
        {
            UiTestKit.Press(root, PadButtons.Down);
        }

        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Contains("open flowchart", s.Calls);
    }

    [Fact]
    public void TitleWritesAnAutoSaveShowsTheBannerAndReturnsOnceBothHaveGone()
    {
        (UiRoot root, PreviewServices s, _) = OpenMenu();
        UiTestKit.Press(root, PadButtons.Up);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Contains("auto save", s.Calls);
        Assert.NotNull(root.Banner);
        Assert.Equal(BannerKind.AutoSave, root.Banner!.Kind);
        Assert.DoesNotContain("return to title", s.Calls);
        UiTestKit.Run(root, 200);
        Assert.Contains("return to title", s.Calls);
        Assert.False(root.Active);
        Assert.Null(root.Banner);
    }

    [Fact]
    public void ThePointerPicksATabAndAClickOpensIt()
    {
        (UiRoot root, _, _) = OpenMenu();
        RectF config = SystemMenuScreen.TabRect(2);
        UiTestKit.Point(root, config.X + 30, config.Y + 20);
        Assert.Equal(2, root.Context.MenuTab);
        UiTestKit.Point(root, config.X + 30, config.Y + 20, click: true);
        Assert.IsType<ConfigScreen>(root.Top);
    }

    [Fact]
    public void ASubmenuHandsTheFocusToTheTabsWithLeftAndSwitchesTab()
    {
        (UiRoot root, _, SystemMenuScreen menu) = OpenMenu();
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 60);
        var save = Assert.IsType<SaveLoadScreen>(root.Top);
        UiTestKit.Press(root, PadButtons.Left);
        Assert.True(menu.SideFocus);
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(1, root.Context.MenuTab);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 60);
        var load = Assert.IsType<SaveLoadScreen>(root.Top);
        Assert.False(load.Save);
        Assert.True(save.Finished || save.Closing);
        Assert.False(menu.SideFocus);
        // right goes back to the page; cancel from the tabs closes the whole thing
        UiTestKit.Press(root, PadButtons.Left);
        Assert.True(menu.SideFocus);
        UiTestKit.Press(root, PadButtons.Right);
        Assert.False(menu.SideFocus);
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Cancel);
        UiTestKit.Run(root, 80);
        Assert.False(root.Active);
    }

    [Fact]
    public void TheMenuDrawsTheChapterCellThePhaseSelects()
    {
        (UiRoot root, PreviewServices s, _) = OpenMenu(x => x.Phase = 5);
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        // the fake phase cell 5 is (0, 400)-(568, 480) in MENU_PARTS child 2: the quad lands at (8, 0), 568 x 80
        DrawCmd cell = cmds.First(c => c.Texture is { Kind: TextureKind.UiTile, C: 2 });
        Assert.Equal((8f, 0f, 576f, 80f), (cell.V0.X, cell.V0.Y, cell.V1.X, cell.V2.Y));
        Assert.Equal(400f / 4000f, cell.V0.V, 5);
        _ = s;
    }

    [Fact]
    public void LockedSaveAndLoadButtonsAreDimmed()
    {
        (UiRoot root, _, _) = OpenMenu(x => x.SavingDisabled = true);
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        // buttons 0 and 1 draw their normal cell at 143 / 255 alpha
        Assert.Equal(2, cmds.Count(c => c.Texture is { Kind: TextureKind.UiTile, C: 0, A: (int)UiSet.MenuParts } && Math.Abs((int)(c.V0.Rgba >> 24) - 143) <= 1));
    }
}

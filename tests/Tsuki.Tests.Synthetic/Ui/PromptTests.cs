using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>Package K9: the button prompts (PlayStation icons, key caps, Xbox glyphs) and the controls-tab setting row.</summary>
public sealed class PromptTests
{
    private static UiRoot Guides(PromptSetting setting, Language language = Language.En, bool decideIsCross = true)
    {
        var services = new PreviewServices(language) { PromptSetting = setting, DecideIsCross = decideIsCross };
        UiRoot root = UiTestKit.Root(services);
        root.Push(new GuidesScreen());
        UiTestKit.Run(root, 2);
        return root;
    }

    private static DrawCmd[] Cmds(UiRoot root) => UiTestKit.Draw(root).Commands.ToArray();

    private static int Tiles(DrawCmd[] cmds, UiSet set, int child) => cmds.Count(c => c.Texture is { Kind: TextureKind.UiTile } t && t.A == (int)set && t.C == child);

    [Fact]
    public void ASettingResolvesToAStyleAndOnlyAutoFollowsTheDevice()
    {
        foreach (PromptStyle device in Enum.GetValues<PromptStyle>())
        {
            Assert.Equal(device, PromptStyles.Resolve(PromptSetting.Auto, device));
            Assert.Equal(PromptStyle.Keyboard, PromptStyles.Resolve(PromptSetting.Keyboard, device));
            Assert.Equal(PromptStyle.PlayStation, PromptStyles.Resolve(PromptSetting.PlayStation, device));
            Assert.Equal(PromptStyle.Xbox, PromptStyles.Resolve(PromptSetting.Xbox, device));
        }
    }

    [Theory]
    [InlineData("auto", true, PromptSetting.Auto)]
    [InlineData("Keyboard", true, PromptSetting.Keyboard)]
    [InlineData("keys", true, PromptSetting.Keyboard)]
    [InlineData("PlayStation", true, PromptSetting.PlayStation)]
    [InlineData("ps4", true, PromptSetting.PlayStation)]
    [InlineData("xbox", true, PromptSetting.Xbox)]
    [InlineData("gamepad", false, PromptSetting.Auto)]
    [InlineData("", false, PromptSetting.Auto)]
    [InlineData(null, false, PromptSetting.Auto)]
    public void SettingNamesParseAndNamesRoundTrip(string? text, bool ok, PromptSetting expected)
    {
        Assert.Equal(ok, PromptStyles.TryParse(text, out PromptSetting s));
        Assert.Equal(expected, s);
        if (ok)
        {
            Assert.True(PromptStyles.TryParse(PromptStyles.Name(s), out PromptSetting again));
            Assert.Equal(s, again);
        }
    }

    [Fact]
    public void TheKeyboardPromptOfAGuideIconIsTheKeyThatDoesWhatItsWordSays()
    {
        static string[] Names(PromptItem[] items) => [.. items.Select(i => i.Kind == PromptItemKind.Arrows ? "arrows" + i.Mask : i.Kind == PromptItemKind.Mouse ? "mouse" : i.Text)];

        Assert.Equal(["Enter"], Names(PromptBindings.Keyboard(PromptBindings.CellEast, 1)));             // Confirm
        Assert.Equal(["Esc"], Names(PromptBindings.Keyboard(PromptBindings.CellSouth, 0)));             // Back
        Assert.Equal(["arrows15", "WASD"], Names(PromptBindings.Keyboard(0, 2)));                       // the letter keys are arrow keys in the menus (package K14)
        Assert.Equal(["arrows12", "A D"], Names(PromptBindings.Keyboard(1, 2)));
        Assert.Equal(["arrows3", "W S"], Names(PromptBindings.Keyboard(2, 2)));
        Assert.Equal(["arrows15"], Names(PromptBindings.Keyboard(0, 2, menu: false)));                  // not in the story's own yes / no prompt
        Assert.Equal(["arrows12"], Names(PromptBindings.Keyboard(1, 2, menu: false)));
        Assert.Equal(["mouse", "PgUp", "PgDn"], Names(PromptBindings.Keyboard(PromptBindings.CellLeftStick, PromptBindings.WordScroll)));
        Assert.Equal(["Drag"], Names(PromptBindings.Keyboard(PromptBindings.CellLeftStick, PromptBindings.WordNavigate)));
        Assert.Equal(["mouse"], Names(PromptBindings.Keyboard(PromptBindings.CellRightStick, PromptBindings.WordZoom)));
        Assert.Equal(["Del"], Names(PromptBindings.Keyboard(PromptBindings.CellNorth, PromptBindings.WordDeleteData)));
        Assert.Empty(PromptBindings.Keyboard(PromptBindings.CellNorth, 22));                           // Change Loop has no key
        Assert.Equal(["Q"], Names(PromptBindings.Keyboard(13, 13)));
        Assert.Equal(["E"], Names(PromptBindings.Keyboard(17, 13)));
        Assert.Equal(["Q", "E"], Names(PromptBindings.Keyboard(15, 13)));
        Assert.Equal(["Backspace"], Names(PromptBindings.Keyboard(19, 0)));
        Assert.Equal(["Ctrl"], Names(PromptBindings.Keyboard(20, 0)));
        Assert.Equal(["F5"], Names(PromptBindings.Keyboard(22, 0)));
        Assert.All(Enumerable.Range(0, 23), c => Assert.NotNull(PromptBindings.Keyboard(c, -1)));
    }

    [Fact]
    public void TheXboxPromptsSitByPositionOnThePadsButtons()
    {
        static string Face(int cell) => PromptBindings.Xbox(cell).Single().Text;

        Assert.Equal("A", Face(PromptBindings.CellEast));       // the decide role is the south button of a PC pad
        Assert.Equal("B", Face(PromptBindings.CellSouth));
        Assert.Equal("X", Face(PromptBindings.CellWest));
        Assert.Equal("Y", Face(PromptBindings.CellNorth));
        Assert.Equal(["LB", "RB"], PromptBindings.Xbox(15).Select(i => i.Text));
        Assert.Equal("LB", Face(13));
        Assert.Equal("RB", Face(14));
        Assert.Equal("LT", Face(19));
        Assert.Equal("RT", Face(20));
        Assert.Equal("LS", Face(PromptBindings.CellLeftStick));
        Assert.Equal("RS", Face(PromptBindings.CellRightStick));
        Assert.Equal("Menu", Face(22));
        Assert.All(Enumerable.Range(0, 7), c => Assert.Equal(PromptItemKind.Dpad, PromptBindings.Xbox(c).Single().Kind));
    }

    [Fact]
    public void TheGuideShowsTheGamesPadIconsOnlyInThePlayStationStyleAndKeepsItsWordsInAll()
    {
        DrawCmd[] ps = Cmds(Guides(PromptSetting.PlayStation));
        DrawCmd[] kb = Cmds(Guides(PromptSetting.Keyboard));
        DrawCmd[] xb = Cmds(Guides(PromptSetting.Xbox));
        Assert.True(Tiles(ps, UiSet.DispParts, 16) > 40);
        Assert.Equal(0, Tiles(kb, UiSet.DispParts, 16));
        Assert.Equal(0, Tiles(xb, UiSet.DispParts, 16));
        int words = Tiles(ps, UiSet.DispParts, 17);
        Assert.True(words > 40);
        Assert.Equal(words, Tiles(kb, UiSet.DispParts, 17));
        Assert.Equal(words, Tiles(xb, UiSet.DispParts, 17));
        // the caps and glyphs are fills
        Assert.True(kb.Count(c => c.Kind == DrawKind.FillQuad) > ps.Count(c => c.Kind == DrawKind.FillQuad) + 200);
        Assert.True(xb.Count(c => c.Kind == DrawKind.FillQuad) > ps.Count(c => c.Kind == DrawKind.FillQuad) + 200);
    }

    [Fact]
    public void TheGuideAdvancesPastACapSoNothingOverlaps()
    {
        // every quad of a keyboard guide line sits inside the screen and the caps do not run into the next word: the words' x positions only grow
        DrawCmd[] kb = Cmds(Guides(PromptSetting.Keyboard));
        Assert.All(kb, c =>
        {
            Assert.InRange(c.V0.X, -1f, 1921f);
            Assert.InRange(c.V3.X, -1f, 1921f);
            Assert.InRange(c.V0.Y, -1f, 1083f);
            Assert.InRange(c.V3.Y, -1f, 1083f);
        });
        float last = -1f;
        foreach (DrawCmd w in kb.Where(c => c.Texture is { Kind: TextureKind.UiTile } t && t.A == (int)UiSet.DispParts && t.C == 17 && c.V0.Y > 1000f))
        {
            Assert.True(w.V0.X >= last - 0.01f, "a word starts left of the previous one");
            last = w.V0.X;
        }
    }

    private static DrawCmd[] Fill(Action<UiPainter> draw)
    {
        UiRoot root = UiTestKit.Root(new PreviewServices());
        var painter = new UiPainter(root.Context.Assets);
        var list = new DisplayList(512);
        list.Reset(0);
        painter.Begin(list);
        draw(painter);
        list.Seal();
        return list.Commands.ToArray();
    }

    [Fact]
    public void FillRoundedStaysInsideItsRectangleAndCoversMostOfIt()
    {
        DrawCmd[] cmds = Fill(p => p.FillRounded(new RectF(10, 20, 60, 30), 7f, 1f, 1f, 1f, 1f));
        Assert.All(cmds, c =>
        {
            Assert.Equal(DrawKind.FillQuad, c.Kind);
            Assert.InRange(c.V0.X, 10f, 70f);
            Assert.InRange(c.V3.X, 10f, 70f);
            Assert.InRange(c.V0.Y, 20f, 50f);
            Assert.InRange(c.V3.Y, 20f, 50f);
        });
        float area = cmds.Sum(c => (c.V3.X - c.V0.X) * (c.V3.Y - c.V0.Y) * ((c.V0.Rgba >> 24) / 255f));
        float expected = (60f * 30f) - ((4f - MathF.PI) * 49f);        // the rectangle less the cut of its four corners
        Assert.InRange(area, expected - 6f, expected + 6f);
        Assert.Contains(cmds, c => (c.V0.Rgba >> 24) is > 0 and < 255);   // anti-aliased corner pixels

        Assert.Single(Fill(p => p.FillRounded(new RectF(0, 0, 20, 10), 0f, 1f, 0f, 0f, 1f)));
        DrawCmd[] disc = Fill(p => p.FillRounded(new RectF(0, 0, 30, 30), 100f, 0f, 1f, 0f, 1f, smooth: false));      // the radius is capped at half the short side
        Assert.All(disc, c =>
        {
            Assert.InRange(c.V0.X, 0f, 30f);
            Assert.InRange(c.V3.X, 0f, 30f);
        });
        float discArea = disc.Sum(c => (c.V3.X - c.V0.X) * (c.V3.Y - c.V0.Y));
        Assert.InRange(discArea, MathF.PI * 225f - 12f, MathF.PI * 225f + 12f);
    }

    [Fact]
    public void ThePageKeysAndDeleteReachTheScreensAsWheelStepsAndADeleteFlag()
    {
        UiInput page = UiInput.From(new InputFrame { ShellPressed = ShellActions.PageUp }, default);
        Assert.Equal(UiInput.PageSteps, page.Wheel);
        Assert.Equal(-UiInput.PageSteps, UiInput.From(new InputFrame { ShellPressed = ShellActions.PageDown }, default).Wheel);
        Assert.Equal(1 + UiInput.PageSteps, UiInput.From(new InputFrame { ShellPressed = ShellActions.PageUp, Pointer = new PointerState(0, 0, true, PointerButtons.None, PointerButtons.None, 1) }, default).Wheel);
        Assert.False(page.DeleteData);
        Assert.True(UiInput.From(new InputFrame { ShellPressed = ShellActions.DeleteData }, default).DeleteData);
        Assert.False(UiInput.From(new InputFrame { ShellHeld = ShellActions.DeleteData | ShellActions.PageUp }, default).DeleteData);       // a held key is not a press
    }

    [Fact]
    public void TheKeyThatOpensTheBacklogClosesItAgainButTheWheelAndThePageKeysScrollIt()
    {
        UiRoot Open(out BacklogScreen screen)
        {
            var services = new PreviewServices();
            services.SetBacklog(Enumerable.Range(0, 5).Select(_ => new BacklogPage([new BacklogMessage(System.Text.Encoding.UTF8.GetBytes("line"))])));
            UiRoot root = UiTestKit.Root(services);
            Assert.True(root.OpenBacklog());
            UiTestKit.Run(root, 30);
            screen = Assert.IsType<BacklogScreen>(root.Top);
            return root;
        }

        UiRoot closing = Open(out _);
        closing.Step(new InputFrame { Frame = 1000, ShellPressed = ShellActions.Backlog });                // the I key
        UiTestKit.Run(closing, 40);
        Assert.False(closing.Active);

        UiRoot paging = Open(out BacklogScreen page);
        paging.Step(new InputFrame { Frame = 1000, ShellPressed = ShellActions.PageUp | ShellActions.Backlog });          // Page Up sets both
        Assert.True(paging.Active);
        Assert.Equal(1, page.Page);
        paging.Step(new InputFrame { Frame = 1001, ShellPressed = ShellActions.PageDown });
        Assert.Equal(0, page.Page);
        paging.Step(new InputFrame { Frame = 1002, ShellPressed = ShellActions.Backlog, Pointer = new PointerState(0, 0, true, PointerButtons.None, PointerButtons.None, 1) });   // wheel up
        Assert.True(paging.Active);
        Assert.Equal(1, page.Page);
    }

    // ---- the controls tab --------------------------------------------------------------------------------------------------------------------------

    private static (UiRoot Root, PreviewServices Services, ConfigScreen Screen) ControlsTab(PromptSetting setting = PromptSetting.PlayStation, bool decideIsCross = true)
    {
        var services = new PreviewServices(Language.En) { PromptSetting = setting, DecideIsCross = decideIsCross };
        UiRoot root = UiTestKit.Root(services);
        ConfigScreen screen = root.Push(new ConfigScreen());
        UiTestKit.Run(root, 60);
        screen.Change(new Ctl(CtlKind.Tab, 2), 0);
        UiTestKit.Run(root, 60);
        Assert.Equal(2, screen.CurrentTab);
        return (root, services, screen);
    }

    [Fact]
    public void TheControlsTabHasAnOptionRowOfFourPromptChoicesTheCursorAndTheMouseCanSelect()
    {
        (UiRoot root, PreviewServices services, ConfigScreen screen) = ControlsTab();
        Assert.Equal([0, 1, 2, 3], screen.Layout.Where(t => t.Ctl.Kind == CtlKind.Prompt).Select(t => t.Ctl.Index));
        Assert.Single(screen.GridRows());
        Assert.True(screen.IsOn(new Ctl(CtlKind.Prompt, 2)));                  // the preview game's setting: PlayStation
        Assert.False(screen.IsOn(new Ctl(CtlKind.Prompt, 0)));

        UiTestKit.Press(root, PadButtons.Down);                                // from the tab bar to the first choice
        Assert.Equal(new Ctl(CtlKind.Prompt, 0), screen.Layout[screen.Cursor].Ctl);
        UiTestKit.Press(root, PadButtons.Right);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(PromptSetting.Keyboard, services.PromptSetting);
        Assert.True(screen.IsOn(new Ctl(CtlKind.Prompt, 1)));
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(PromptSetting.Auto, services.PromptSetting);

        (float x, float y) = (screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.Prompt, 3)).X, screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.Prompt, 3)).Y);
        UiTestKit.Point(root, x + 20f, y + 12f, click: true);
        Assert.Equal(PromptSetting.Xbox, services.PromptSetting);
        UiTestKit.Press(root, PadButtons.Cancel);
        UiTestKit.Run(root, 60);
        Assert.False(root.Active);
    }

    [Fact]
    public void TheControlsPageIsTheCrossVariantWhenDecideIsCrossAndIsDrawnInPiecesWhenTheIconsAreReplaced()
    {
        DrawCmd[] circle = Cmds(ControlsTab(PromptSetting.PlayStation, decideIsCross: false).Root);
        DrawCmd[] cross = Cmds(ControlsTab(PromptSetting.PlayStation, decideIsCross: true).Root);
        Assert.Equal(1, Tiles(circle, UiSet.ConfParts, 16));
        Assert.Equal(0, Tiles(circle, UiSet.ConfParts, 17));
        Assert.Equal(0, Tiles(cross, UiSet.ConfParts, 16));
        Assert.Equal(1, Tiles(cross, UiSet.ConfParts, 17));

        DrawCmd[] keys = Cmds(ControlsTab(PromptSetting.Keyboard, decideIsCross: true).Root);
        Assert.True(Tiles(keys, UiSet.ConfParts, 17) > 40);                       // pieces of the page around the icon boxes, and the filler slices
        Assert.True(keys.Count(c => c.Kind == DrawKind.FillQuad) > 600);         // 12 rows of key caps
        DrawCmd[] pad = Cmds(ControlsTab(PromptSetting.Xbox, decideIsCross: true).Root);
        Assert.True(pad.Count(c => c.Kind == DrawKind.FillQuad) > 600);
    }

    [Fact]
    public void EveryRowOfTheControlsPageHasAKeyPromptAndAnXboxPrompt()
    {
        Assert.Equal(12, UiPrompts.ControlRowCentres.Length);
        for (int row = 0; row < 12; row++)
        {
            Assert.NotEmpty(UiPrompts.ControlRow(row, PromptStyle.Keyboard));
            Assert.NotEmpty(UiPrompts.ControlRow(row, PromptStyle.Xbox));
        }

        Assert.Equal(["Enter", "Space", "Z"], UiPrompts.ControlRow(0, PromptStyle.Keyboard).Select(i => i.Text));
        Assert.Equal(["Tab", "E"], UiPrompts.ControlRow(6, PromptStyle.Keyboard).Select(i => i.Text));
        Assert.Equal(["A", "Q"], UiPrompts.ControlRow(7, PromptStyle.Keyboard).Select(i => i.Text));
        Assert.Equal("RB", UiPrompts.ControlRow(6, PromptStyle.Xbox).Single().Text);
        Assert.Equal("LB", UiPrompts.ControlRow(7, PromptStyle.Xbox).Single().Text);
        Assert.Equal(["A", "B", "X", "Y"], Enumerable.Range(0, 4).Select(r => UiPrompts.ControlRow(r, PromptStyle.Xbox).Single().Text));
    }

    // ---- the title and the save list ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void PressAnyButtonBecomesPressAnyKeyForTheKeyboardOnly()
    {
        foreach ((PromptSetting setting, bool picture) in new[] { (PromptSetting.PlayStation, true), (PromptSetting.Xbox, true), (PromptSetting.Keyboard, false) })
        {
            var services = new PreviewServices { PromptSetting = setting, Title = new UiTitleInfo(true, false, 0, -1) };
            UiRoot root = UiTestKit.Root(services);
            root.Context.TitleIntroShown = true;
            var title = root.Push(new TitleScreen());
            UiTestKit.Run(root, 200);
            Assert.True(title.PressMode);
            Assert.Equal(picture, Tiles(Cmds(root), UiSet.TitleParts, 14) > 0);
        }
    }

    [Fact]
    public void TheSaveListGuideAsksForDeleteDataOnTheKeyboardToo()
    {
        var services = new PreviewServices { PromptSetting = PromptSetting.Keyboard, DecideIsCross = true };
        UiRoot root = UiTestKit.Root(services);
        root.OpenSystemMenu();
        UiTestKit.Run(root, 20);
        root.Context.MenuTab = 0;
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 60);
        Assert.IsType<SaveLoadScreen>(root.Top);
        DrawCmd[] cmds = Cmds(root);
        Assert.Equal(0, Tiles(cmds, UiSet.DispParts, 16));
        Assert.True(Tiles(cmds, UiSet.DispParts, 17) >= 5);                 // Back, Confirm, Delete Data, Navigate, Scroll
    }
}

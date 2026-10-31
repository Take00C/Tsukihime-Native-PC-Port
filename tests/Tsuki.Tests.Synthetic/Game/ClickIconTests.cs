using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Text;

namespace Tsuki.Tests.Synthetic.Game;

/// <summary>
/// Package K34, the click-wait icon (docs/formats/textbox.md 7.1): the alpha ramp of HUD VA 0x53FC4..0x5400C, the anchor of VA 0xCA690, the choice
/// of the mark by F799 / the window / F822, the position offsets, the two-tick animation of the tick counter VA 0x78122C, and what hides it.
/// </summary>
public sealed class ClickIconTests
{
    private const string Page = "_WKST(F822,eq,1);_WKAD(F823,1);";
    private const string Twenty = "abcdefghijklmnopqrst";

    private static (GameRig Rig, GameHost Host) Start(string script, Language language = Language.En)
    {
        var rig = new GameRig(language).Line(1, Twenty).Line(2, "second line").Line(3, "third").Script(script);
        GameHost host = rig.Build();
        host.NewGame(100);
        host.Wait.WindowAlpha = 1f;
        host.Wait.WindowTarget = 1;
        return (rig, host);
    }

    private static DisplayList StepDrawn(GameHost host, InputFrame input = default)
    {
        var list = new DisplayList(4096);
        list.Reset(host.Frame + 1);
        host.Step(input, list);
        return list;
    }

    private static List<DrawCmd> IconQuads(DisplayList list, int child)
    {
        var found = new List<DrawCmd>();
        foreach (DrawCmd c in list.Commands)
        {
            if (c.Texture.Kind == TextureKind.UiTile && c.Texture.A == (int)UiSet.DispParts && c.Texture.C == child)
            {
                found.Add(c);
            }
        }

        return found;
    }

    private static int RunToShown(GameHost host)
    {
        int n = 0;
        while ((host.Wait.Bits & WaitBits.Shown) == 0 && n < 400)
        {
            host.Step(default);
            n++;
        }

        Assert.True((host.Wait.Bits & WaitBits.Shown) != 0, "the message was not revealed");
        return n;
    }

    [Fact]
    public void TheAlphaRisesByATenthPerFrameFromTheRevealFrameClampsAtOneAndDropsToZeroOnTheAdvance()
    {
        (GameRig rig, GameHost host) = Start(Page + "_WNTY(0);_ZM00101($000001);_END_();");
        host.Step(default);
        Assert.Equal(0f, host.ClickIconAlpha);
        var alphas = new List<float>();
        while ((host.Wait.Bits & WaitBits.Shown) == 0 && host.Frame < 400)
        {
            Assert.Equal(0f, host.ClickIconAlpha);          // the text is revealing: 0xCA690 reports it incomplete
            host.Step(default);
            alphas.Add(host.ClickIconAlpha);
        }

        // the reveal frame itself ends with 0.1 (the HUD runs after tick_reveal), then +0.1 per frame to 1
        Assert.Equal(0.1f, alphas[^1]);
        for (int i = 1; i <= 12; i++)
        {
            host.Step(default);
            float expected = Math.Min(1f, 0.1f * (i + 1));
            Assert.True(Math.Abs(host.ClickIconAlpha - expected) < 1e-6f, $"frame +{i}: alpha {host.ClickIconAlpha}, expected {expected}");
        }

        Assert.Equal(1f, host.ClickIconAlpha);
        rig.Click();                                          // the advance clears wait bit 0x400 in Adv_Update; the HUD of the same frame sets the alpha to 0
        Assert.Equal(0f, host.ClickIconAlpha);
    }

    [Fact]
    public void ANoWaitMessageAndAnAtNMessageNeverShowTheIcon()
    {
        foreach (string end in new[] { "@e", "@n" })
        {
            (GameRig _, GameHost host) = Start(Page + $"_WNTY(0);_ZM00101($000001{end});_WTTM(500,1);_END_();");
            for (int i = 0; i < 90 && host.Phase == GamePhase.Running; i++)
            {
                DisplayList list = StepDrawn(host);
                Assert.Equal(0f, host.ClickIconAlpha);
                Assert.Empty(IconQuads(list, GameHost.LineIconChild));
                Assert.Empty(IconQuads(list, GameHost.PageIconChild));
            }
        }
    }

    [Fact]
    public void TheLineMarkShowsInsideAPageAndThePageMarkAtItsEndAtHalfAlpha()
    {
        (GameRig rig, GameHost host) = Start(Page + "_WNTY(0);_ZM00101($000001);_WKAD(F823,1);_WNTY(1);_ZM00102($000002);_END_();");
        RunToShown(host);
        for (int i = 0; i < 9; i++)
        {
            host.Step(default);
        }

        Assert.Equal(1f, host.ClickIconAlpha);
        DisplayList list = StepDrawn(host);
        Assert.Equal(0, host.F(799));
        Assert.Equal(ClickIconKind.Line, host.LastClickIcon.Kind);
        Assert.Equal(GameHost.LineIconChild, host.LastClickIcon.DispChild);
        List<DrawCmd> line = IconQuads(list, GameHost.LineIconChild);
        Assert.Single(line);
        Assert.Empty(IconQuads(list, GameHost.PageIconChild));
        Assert.Equal(1.0, host.LastClickIcon.Alpha, 6);
        Assert.Equal(255u, line[0].V0.Rgba >> 24);

        // the second message is the last of the page (_WNTY(1) -> F799 = 1): the page mark, half alpha
        rig.Click();
        RunToShown(host);
        for (int i = 0; i < 12; i++)
        {
            host.Step(default);
        }

        list = StepDrawn(host);
        Assert.Equal(1, host.F(799));
        Assert.Equal(ClickIconKind.Page, host.LastClickIcon.Kind);
        List<DrawCmd> page = IconQuads(list, GameHost.PageIconChild);
        Assert.Single(page);
        Assert.Empty(IconQuads(list, GameHost.LineIconChild));
        Assert.Equal(0.5, host.LastClickIcon.Alpha, 6);
        Assert.Equal(128u, page[0].V0.Rgba >> 24);
    }

    [Fact]
    public void AFinishedPageShowsThePageEndMark()
    {
        // the regression of GitHub issue #1: a page whose last message is complete and waits for the click shows the page-end icon
        (GameRig _, GameHost host) = Start(Page + "_WNTY(1);_ZM00101($000001);_END_();");
        RunToShown(host);
        DisplayList list = StepDrawn(host);
        Assert.Single(IconQuads(list, GameHost.PageIconChild));
        Assert.Equal(ClickIconKind.Page, host.LastClickIcon.Kind);
        Assert.True(host.ClickIconAlpha > 0f);
    }

    [Fact]
    public void TheQuadSitsAfterTheLastGlyphAtTheOffsetsOfTheHud()
    {
        (GameRig _, GameHost host) = Start(Page + "_WNTY(0);_ZM00101($000001);_END_();");
        RunToShown(host);
        DisplayList list = StepDrawn(host);
        PageMessage m = host.CurrentMessage!;
        ClickAnchor anchor = MessageWindow.ClickIconAnchor(m.Block, host.Wait.Head, host.Wait.Tail, Language.En);
        Assert.True(anchor.Complete);
        Assert.Equal(anchor, host.ClickIconAnchor);
        GlyphRecord last = m.Block.Records[^1];
        int expectedX = (int)((last.X2 - last.X) * last.Sx + (last.Dx + 88f) + 3f);
        Assert.Equal(expectedX, anchor.X);
        Assert.Equal(0, anchor.Y);

        // the page's own origin is (30, 35): the stored position is the anchor with the window shift (30 - 30 = 0) and the message's y
        Assert.Equal((anchor.X, 35f), host.ClickIconStored);
        // the story-page helper: x - 57 (English) - 3.7, y + 4.3, in text units; 0.333 units per texel of the 39 x 39 cell
        double x = anchor.X - 57f + -3.7f;
        double y = 35f + 4.3f;
        Assert.Equal(x, host.LastClickIcon.X, 4);
        Assert.Equal(y, host.LastClickIcon.Y, 4);
        DrawCmd q = Assert.Single(IconQuads(list, GameHost.LineIconChild));
        Assert.Equal(x * 3.0, q.V0.X, 3);
        Assert.Equal(y * GameHost.PageScaleY, q.V0.Y, 3);
        Assert.Equal(39 * 0.333f * 3.0, q.V3.X - q.V0.X, 3);
        Assert.Equal(39 * 0.333f * GameHost.PageScaleY, q.V3.Y - q.V0.Y, 3);
        // cell 0 of the 4095 x 39 sheet
        Assert.Equal(0f, q.V0.U);
        Assert.Equal(39f / 4095f, q.V3.U, 6);
        Assert.Equal(1f, q.V3.V);
    }

    [Fact]
    public void TheAnchorFollowsTheHeadAndTheTailLikeTheEngine()
    {
        (GameRig _, GameHost host) = Start(Page + "_WNTY(0);_ZM00101($000001);_END_();");
        host.Step(default);
        TextBlock block = host.CurrentMessage!.Block;
        GlyphRecord[] recs = block.Records;
        Assert.Equal(20, recs.Length);

        // nothing revealed: the function's own origin and no completion
        Assert.Equal(new ClickAnchor(88, 0, false), MessageWindow.ClickIconAnchor(block, 0, 0, Language.En));

        // the head past glyph 9, the tail behind it: glyph 9 anchors the position at a partial alpha
        GlyphRecord g9 = recs[9];
        ClickAnchor a = MessageWindow.ClickIconAnchor(block, g9.Ctr, g9.Ctr - 2, Language.En);
        Assert.False(a.Complete);
        Assert.Equal((int)((g9.X2 - g9.X) * g9.Sx + (g9.Dx + 88f) + 3f), a.X);

        // the tail past it: glyph 9 is fully revealed, but the record list goes on past the head, so the text is not complete
        a = MessageWindow.ClickIconAnchor(block, g9.Ctr, g9.Ctr + 1, Language.En);
        Assert.False(a.Complete);

        // everything revealed
        GlyphRecord last = recs[^1];
        a = MessageWindow.ClickIconAnchor(block, 768, 768, Language.En);
        Assert.True(a.Complete);
        Assert.Equal((int)((last.X2 - last.X) * last.Sx + (last.Dx + 88f) + 3f), a.X);

        // the head on the last glyph with the tail behind it: alpha below 1, not complete
        a = MessageWindow.ClickIconAnchor(block, last.Ctr, last.Ctr - 1, Language.En);
        Assert.False(a.Complete);
        Assert.Equal((int)((last.X2 - last.X) * last.Sx + (last.Dx + 88f) + 3f), a.X);
    }

    [Fact]
    public void TheAnimationShowsEachCellForTwoFramesWrapsAtTheCellCountAndRepeatsCell103OfThePageMark()
    {
        (GameRig rig, GameHost host) = Start(Page + "_WNTY(0);_ZM00101($000001);_WKAD(F823,1);_WNTY(1);_ZM00102($000002);_END_();");
        RunToShown(host);
        var cells = new List<int>();
        for (int i = 0; i < 2 * 105 + 4; i++)
        {
            StepDrawn(host);
            cells.Add(host.LastClickIcon.Frame);
        }

        // the counter starts at -1 (data segment), so the first drawn frame is cell 0; two frames per cell; 105 cells, then cell 0 again
        for (int i = 0; i < 210; i++)
        {
            Assert.Equal(i / 2, cells[i]);
        }

        Assert.Equal([0, 0, 1, 1], cells[210..214]);

        // the page mark: the counter carries on where the line mark left it, and row 104 of its table repeats cell 103
        rig.Click();
        int tick = host.ClickIconTick;
        RunToShown(host);
        cells.Clear();
        for (int i = 0; i < 2 * 105; i++)
        {
            StepDrawn(host);
            cells.Add(host.LastClickIcon.Frame);
        }

        Assert.Equal(ClickIconKind.Page, host.LastClickIcon.Kind);
        int expectedTick = (tick + 1) % 210;
        Assert.Equal(GameHost.ClickIconCell(GameHost.PageIconChild, expectedTick / 2), cells[0]);
        Assert.Contains(103, cells);
        Assert.DoesNotContain(104, cells);
        int at103 = cells.FindIndex(c => c == 103);
        Assert.Equal(4, cells.Skip(at103).TakeWhile(c => c == 103).Count());
    }

    [Fact]
    public void AutoModeKeepsTheMarkOffWhileTheAlphaStillRises()
    {
        (GameRig _, GameHost host) = Start(Page + "_WNTY(0);_ZM00101($000001);_END_();");
        host.SetAutoMode(true);
        RunToShown(host);
        DisplayList list = StepDrawn(host);
        Assert.True(host.ClickIconAlpha > 0f);
        Assert.Equal(ClickIconKind.None, host.LastClickIcon.Kind);
        Assert.Empty(IconQuads(list, GameHost.LineIconChild));
        Assert.Equal(-1, host.ClickIconTick);
    }

    [Fact]
    public void AHiddenWindowAndSkipShowNoMark()
    {
        (GameRig _, GameHost host) = Start(Page + "_WNTY(0);_ZM00101($000001);_END_();");
        RunToShown(host);
        for (int i = 0; i < 10; i++)
        {
            host.Step(default);
        }

        StepDrawn(host);
        Assert.Equal(ClickIconKind.Line, host.LastClickIcon.Kind);
        DisplayList list = StepDrawn(host, new InputFrame { ShellPressed = ShellActions.HideWindow });
        Assert.True(host.WindowHidden);
        Assert.Empty(IconQuads(list, GameHost.LineIconChild));
        Assert.Equal(ClickIconKind.None, host.LastClickIcon.Kind);         // the report is per drawn frame, not the last mark drawn (package K35)
        list = StepDrawn(host, new InputFrame { ShellPressed = ShellActions.HideWindow });
        Assert.False(host.WindowHidden);
        Assert.Single(IconQuads(list, GameHost.LineIconChild));
        Assert.Equal(ClickIconKind.Line, host.LastClickIcon.Kind);

        // the window fade at 0 (a page transition) draws no page and no mark, and reports none
        host.Wait.WindowAlpha = 0f;
        host.Wait.WindowTarget = 0;
        list = StepDrawn(host);
        Assert.Empty(IconQuads(list, GameHost.LineIconChild));
        Assert.Equal(ClickIconKind.None, host.LastClickIcon.Kind);

        // fast-forward (the latched skip would stop at these unread lines under the default read-only rule): a message is revealed in the frame it
        // starts and advanced in the next, so the alpha never gets past the first steps (the HUD only adds 0.1 while the text is complete and the click
        // bit is set: a message advanced and replaced by a complete one in the same frame carries the alpha over, as on the PS4)
        (GameRig _, GameHost skip) = Start(Page + "_WNTY(0);_ZM00101($000001);_ZM00102($000002);_END_();");
        float maxAlpha = 0f;
        int frames = 0;
        for (; frames < 400 && skip.Phase == GamePhase.Running; frames++)
        {
            skip.Step(new InputFrame { ShellHeld = ShellActions.FastForward });
            maxAlpha = Math.Max(maxAlpha, skip.ClickIconAlpha);
        }

        Assert.Equal(GamePhase.Ended, skip.Phase);
        Assert.True(frames <= 4, $"fast-forward took {frames} frames for two messages");
        Assert.True(maxAlpha <= 0.2f + 1e-6f, $"fast-forward let the icon alpha reach {maxAlpha} in {frames} frames");
    }

    [Fact]
    public void OutsidePageModeThePageMarkSitsRightOfTheAnchorAtHalfAlpha()
    {
        (GameRig _, GameHost host) = Start("_WNTY(0);_ZM00101($000001);_END_();");
        Assert.NotEqual(1, host.F(822));
        RunToShown(host);
        DisplayList list = StepDrawn(host);
        Assert.Equal(ClickIconKind.Page, host.LastClickIcon.Kind);
        Assert.Equal(host.ClickIconAnchor.X + 115f, host.LastClickIcon.X, 4);
        Assert.Equal(35f + 2f, host.LastClickIcon.Y, 4);
        Assert.Single(IconQuads(list, GameHost.PageIconChild));
        Assert.Equal(0.5 * host.ClickIconAlpha, host.LastClickIcon.Alpha, 6);
    }

    [Fact]
    public void ASpeechBubbleShowsTheBubbleMarkAfterItsOwnTextOrigin()
    {
        (GameRig _, GameHost host) = Start(Page + "_WKST(F1105,eq,5);_WNTY(0);_ZM00101($000001);_END_();");
        RunToShown(host);
        DisplayList list = StepDrawn(host);
        Assert.Equal(ClickIconKind.Bubble, host.LastClickIcon.Kind);
        (double ox, double oy) = MessageWindow.BubbleTextOrigin(5);
        // 0x54B6A..0x54B80: the anchor x plus the bubble's text origin minus 87, truncated; the bubble mark at (x - 2, y + 2)
        int xInt = (int)(((float)ox - 87f) + host.ClickIconAnchor.X);
        Assert.Equal((xInt, (float)oy), host.ClickIconStored);
        Assert.Equal(xInt - 2f, host.LastClickIcon.X, 4);
        Assert.Equal((float)oy + 2f, host.LastClickIcon.Y, 4);
        DrawCmd q = Assert.Single(IconQuads(list, GameHost.BubbleIconChild));
        Assert.Equal(43 * 0.333f * 3.0, q.V3.X - q.V0.X, 3);
        Assert.Equal(46 * 0.333f * GameHost.PageScaleY, q.V3.Y - q.V0.Y, 3);
        Assert.Equal(43f / 2537f, q.V3.U, 6);
    }

    [Fact]
    public void TheIconFadesWithTheWindowTwiceAndIsStoredEvenWhenF1103HidesTheText()
    {
        (GameRig _, GameHost host) = Start(Page + "_WNTY(0);_ZM00101($000001);_END_();");
        RunToShown(host);
        for (int i = 0; i < 10; i++)
        {
            host.Step(default);
        }

        host.Wait.WindowAlpha = 0.5f;
        DisplayList list = StepDrawn(host);                    // the fade climbs back by 0.06 in this step before the draw
        float fade = host.Wait.WindowAlpha;
        Assert.Equal(0.56f, fade, 5);
        DrawCmd q = Assert.Single(IconQuads(list, GameHost.LineIconChild));
        // colour alpha 1 x fade x 1, then the list alpha fade x 1 (text fade): the fade enters twice
        Assert.Equal(fade, host.LastClickIcon.Alpha, 6);
        Assert.Equal((uint)Math.Round(fade * fade * 255.0), q.V0.Rgba >> 24);

        host.Wait.WindowAlpha = 1f;
        host.Vars.SetF(1103, 1);
        list = StepDrawn(host);
        Assert.Equal(0, list.Commands.ToArray().Count(c => c.Texture.Kind == TextureKind.GlyphPage));
        Assert.Equal((host.ClickIconAnchor.X, 35f), host.ClickIconStored);
    }
}

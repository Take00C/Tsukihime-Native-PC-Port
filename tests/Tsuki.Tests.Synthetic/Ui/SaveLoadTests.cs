using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

public sealed class SaveLoadTests
{
    private static (UiRoot Root, PreviewServices Services, SaveLoadScreen Screen) Open(bool save, int used = 6)
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
    public void TheSaveListOffersTheFirstFreeSlotThenTheUsedOnesNewestFirst()
    {
        (_, _, SaveLoadScreen screen) = Open(save: true);
        Assert.Equal(6, screen.List[0]);
        Assert.Equal([6, 5, 4, 3, 2, 1, 0], screen.List);
        Assert.Equal(6, screen.CursorSlot);
    }

    [Fact]
    public void TheLoadListHoldsTheUsedSlotsAndTheAutoSavesNewestFirstButNotTheQuickSave()
    {
        (_, PreviewServices s, SaveLoadScreen screen) = Open(save: false);
        Assert.Equal(screen.List.OrderByDescending(i => s.Slots[i].Date).ThenByDescending(i => i), screen.List);
        Assert.DoesNotContain(139, screen.List);
        Assert.Contains(100, screen.List);
        Assert.Contains(101, screen.List);
        Assert.Equal(8, screen.List.Count);
        Assert.False(screen.Save);
    }

    [Fact]
    public void TheCursorScrollsFourFramesAtATimeAndWrapsOnAFreshPress()
    {
        (UiRoot root, _, SaveLoadScreen screen) = Open(save: true);
        for (int i = 0; i < 3; i++)
        {
            UiTestKit.Press(root, PadButtons.Down);
        }

        Assert.Equal(0, screen.Scroll);
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(1, screen.Scroll);
        for (int i = 0; i < 2; i++)
        {
            UiTestKit.Press(root, PadButtons.Down);
        }

        Assert.Equal(3, screen.Scroll);
        Assert.Equal(0, screen.CursorSlot);
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(0, screen.Scroll);
        Assert.Equal(6, screen.CursorSlot);
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(0, screen.CursorSlot);
    }

    [Fact]
    public void AHeldDirectionDoesNotWrap()
    {
        (UiRoot root, _, SaveLoadScreen screen) = Open(save: true);
        // a fresh press up at the top wraps to the last entry; an auto-repeat down there stays
        UiTestKit.Hold(root, PadButtons.Up, 1);
        Assert.Equal(0, screen.CursorSlot);
        root.Step(new InputFrame { Frame = 1000, Repeated = PadButtons.Down, Held = PadButtons.Down });
        Assert.Equal(0, screen.CursorSlot);
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(6, screen.CursorSlot);
    }

    [Fact]
    public void SavingIntoTheBlankSlotWritesAtOnceAndShowsAHeldPopup()
    {
        (UiRoot root, PreviewServices s, SaveLoadScreen screen) = Open(save: true);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Contains("save 6", s.Calls);
        Assert.IsType<MessagePopup>(root.Top);
        UiTestKit.Run(root, 140);
        Assert.Contains("sound Done", s.Calls);
        Assert.IsType<SaveLoadScreen>(root.Top);
        Assert.Equal(6, screen.CursorSlot);
        Assert.Equal(7, screen.List[0]);
    }

    [Fact]
    public void OverwritingAnUsedSlotAsksFirstAndNoLeavesItAlone()
    {
        (UiRoot root, PreviewServices s, SaveLoadScreen screen) = Open(save: true);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Decide);
        var dialog = Assert.IsType<ConfirmDialog>(root.Top);
        UiTestKit.Run(root, 20);
        // overwrite starts on No
        Assert.False(dialog.CursorOnYes);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 60);
        Assert.DoesNotContain(s.Calls, c => c.StartsWith("save ", StringComparison.Ordinal));
        Assert.Same(screen, root.Top);

        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 20);
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 60);
        Assert.Contains("save 5", s.Calls);
    }

    [Fact]
    public void LoadingAsksThenOpensAPopupThenLoadsAndClosesEverything()
    {
        (UiRoot root, PreviewServices s, SaveLoadScreen screen) = Open(save: false);
        int newest = screen.CursorSlot;
        UiTestKit.Press(root, PadButtons.Decide);
        var dialog = Assert.IsType<ConfirmDialog>(root.Top);
        UiTestKit.Run(root, 20);
        Assert.True(dialog.CursorOnYes);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 100);
        Assert.Contains($"load {newest}", s.Calls);
        Assert.False(root.Active);
    }

    [Fact]
    public void ACancelAnswersNoAfterOneFrame()
    {
        (UiRoot root, PreviewServices s, _) = Open(save: false);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 20);
        UiTestKit.Press(root, PadButtons.Cancel);
        UiTestKit.Run(root, 60);
        Assert.DoesNotContain(s.Calls, c => c.StartsWith("load ", StringComparison.Ordinal));
        Assert.IsType<SaveLoadScreen>(root.Top);
    }

    [Fact]
    public void AnEmptyLoadListAnswersWithTheNoDataPopup()
    {
        (UiRoot root, PreviewServices s, SaveLoadScreen screen) = Open(save: false, used: 0);
        // the preview game keeps its two auto saves; remove them to get an empty list
        s.Delete(100);
        s.Delete(101);
        screen.Rebuild(resetCursor: true);
        Assert.Empty(screen.List);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.IsType<MessagePopup>(root.Top);
        Assert.Contains("sound Buzzer", s.Calls);
    }

    [Fact]
    public void CopyAndDeleteHaveTheirOwnButtonsAndDialogs()
    {
        (UiRoot root, PreviewServices s, SaveLoadScreen screen) = Open(save: true);
        UiTestKit.Press(root, PadButtons.Down);
        int slot = screen.CursorSlot;
        UiTestKit.Press(root, PadButtons.Square);                                  // Copy Data has no guide word: SQUARE
        Assert.IsType<ConfirmDialog>(root.Top);
        UiTestKit.Run(root, 20);
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 60);
        Assert.Contains($"copy {slot}", s.Calls);
        Assert.True(s.Slots[6].Used);
        Assert.Equal(7, screen.List[0]);

        UiTestKit.Press(root, PadButtons.Triangle);                                // the guide's "Delete Data" sits beside the TRIANGLE icon
        Assert.IsType<ConfirmDialog>(root.Top);
        UiTestKit.Run(root, 20);
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 200);
        Assert.Contains(s.Calls, c => c.StartsWith("delete ", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAnsweredDialogKeepsFadingUnderThePopupItsAnswerOpened()
    {
        // package R1: Yes on Delete opens the held "Deleting..." popup over the dialog; the dialog's fade-out used to stop there, its window left under the popup
        (UiRoot root, _, _) = Open(save: true);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Triangle);
        var dialog = Assert.IsType<ConfirmDialog>(root.Top);
        UiTestKit.Run(root, 20);
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 16);
        Assert.IsType<MessagePopup>(root.Top);
        Assert.Contains(dialog, root.Stack);                                        // the 15-frame hold has just run out: the fade-out has begun under the popup
        UiTestKit.Run(root, 16);
        Assert.IsType<MessagePopup>(root.Top);
        Assert.DoesNotContain(dialog, root.Stack);                                  // 14 frames of 0.07 later the dialog is invisible and gone
    }

    [Fact]
    public void TheDeleteKeyDeletesLikeTriangleAndThePageKeysScrollLikeFiveWheelSteps()
    {
        (UiRoot root, PreviewServices s, SaveLoadScreen screen) = Open(save: true);
        UiTestKit.Press(root, PadButtons.Down);
        root.Step(new InputFrame { Frame = root.Context.Frame + 1, ShellPressed = ShellActions.DeleteData });
        Assert.IsType<ConfirmDialog>(root.Top);
        UiTestKit.Run(root, 20);
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 200);
        Assert.Contains(s.Calls, c => c.StartsWith("delete ", StringComparison.Ordinal));

        // Page Down moves the cursor five rows down the list, Page Up five up
        (UiRoot root2, _, SaveLoadScreen list) = Open(save: true);
        int before = list.CursorSlot;
        root2.Step(new InputFrame { Frame = root2.Context.Frame + 1, ShellPressed = ShellActions.PageDown });
        UiTestKit.Run(root2, 5);
        Assert.NotEqual(before, list.CursorSlot);
        int after = list.CursorSlot;
        root2.Step(new InputFrame { Frame = root2.Context.Frame + 1, ShellPressed = ShellActions.PageUp });
        UiTestKit.Run(root2, 5);
        Assert.Equal(before, list.CursorSlot);
        Assert.NotEqual(after, list.CursorSlot);
    }

    [Fact]
    public void CancelLeavesTheListWithAFadeOut()
    {
        (UiRoot root, _, SaveLoadScreen screen) = Open(save: true);
        UiTestKit.Press(root, PadButtons.Cancel);
        Assert.True(screen.Closing);
        UiTestKit.Run(root, 30);
        Assert.IsType<SystemMenuScreen>(root.Top);
    }

    [Fact]
    public void TheListDrawsFramesThumbnailsDigitsAndTheRouteCard()
    {
        (UiRoot root, _, _) = Open(save: false);
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        // 4 frames; 4 thumbnails; date and time digits; the route card of the cursor slot; the scroll bar
        Assert.Equal(4, cmds.Count(c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.SaveParts, C: 8 }));
        Assert.Equal(4, cmds.Count(c => c.Texture.Kind == TextureKind.Thumbnail));
        Assert.Equal(4 * 8 + 4 * 2, cmds.Count(c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.SaveParts, C: 1 }));
        Assert.Equal(4 * 4 + 4, cmds.Count(c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.SaveParts, C: 6 }));
        Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.SaveParts, C: 4 });
        Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.DispParts, C: 15 });
        // the auto saves are at the end of the list and carry their tag
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Contains(UiTestKit.Draw(root).Commands.ToArray(), c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.SaveParts, C: 0 });
    }

    [Fact]
    public void FramePositionsFollowThePitchOfTheFrames()
    {
        (float x0, float y0) = SaveLoadScreen.FramePosition(0);
        (float x1, float y1) = SaveLoadScreen.FramePosition(1);
        Assert.Equal(133.667f * 3f, x0, 2);
        Assert.Equal(x0, x1);
        Assert.Equal(93.748f * UiMetrics.UnitY, y1 - y0, 2);
    }
}

using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>
/// Package K45 (issue #10): the save list's delete as the PS4 runs it (save/load screen 0x71C90 states 22..26). Yes holds "Deleting..." while the frame under
/// the cursor fills in for 20 frames, then the slot is deleted; 42 frames later the list is rebuilt and "Deleted." shows. A delete the services refuse says
/// "Unable to save data." instead, and a blank slot says "No save data found.".
/// </summary>
public sealed class SaveDeletionScreenTests
{
    private static (UiRoot Root, PreviewServices Services, SaveLoadScreen Screen) Open(bool save)
    {
        var services = new PreviewServices(Language.En);
        UiRoot root = UiTestKit.Root(services);
        root.OpenSystemMenu();
        UiTestKit.Run(root, 20);
        root.Context.MenuTab = save ? 0 : 1;
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 60);
        return (root, services, Assert.IsType<SaveLoadScreen>(root.Top));
    }

    private static void CursorTo(UiRoot root, SaveLoadScreen screen, int slot)
    {
        for (int i = 0; i < screen.List.Count && screen.CursorSlot != slot; i++)
        {
            UiTestKit.Press(root, PadButtons.Down);
        }

        Assert.Equal(slot, screen.CursorSlot);
        UiTestKit.Run(root, 10);
    }

    // TRIANGLE, Yes; returns once the dialog has answered and "Deleting..." is up
    private static void AnswerYes(UiRoot root)
    {
        UiTestKit.Press(root, PadButtons.Triangle);
        Assert.IsType<ConfirmDialog>(root.Top);
        UiTestKit.Run(root, 20);
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Decide);
        for (int i = 0; i < 30 && root.Top is not MessagePopup; i++)
        {
            UiTestKit.Run(root, 1);
        }

        Assert.Equal("text 188", Assert.IsType<MessagePopup>(root.Top).Message);
    }

    private static RectF Bounds(DrawCmd c)
    {
        float x0 = MathF.Min(MathF.Min(c.V0.X, c.V1.X), MathF.Min(c.V2.X, c.V3.X));
        float y0 = MathF.Min(MathF.Min(c.V0.Y, c.V1.Y), MathF.Min(c.V2.Y, c.V3.Y));
        float x1 = MathF.Max(MathF.Max(c.V0.X, c.V1.X), MathF.Max(c.V2.X, c.V3.X));
        float y1 = MathF.Max(MathF.Max(c.V0.Y, c.V1.Y), MathF.Max(c.V2.Y, c.V3.Y));
        return new RectF(x0, y0, x1 - x0, y1 - y0);
    }

    private static int FramesUntil(UiRoot root, Func<bool> done, int limit = 200)
    {
        for (int n = 1; n <= limit; n++)
        {
            UiTestKit.Run(root, 1);
            if (done())
            {
                return n;
            }
        }

        return -1;
    }

    [Fact]
    public void AnAutoSaveInTheLoadListIsDeletedAndLeavesTheList()
    {
        (UiRoot root, PreviewServices s, SaveLoadScreen screen) = Open(save: false);
        CursorTo(root, screen, 100);
        int count = screen.List.Count;
        AnswerYes(root);

        int fill = FramesUntil(root, () => s.Calls.Contains("delete 100"));
        Assert.Equal(20, fill);                                                    // 0.05 a frame (0x72696)
        int sounds = s.Calls.Count(c => c.StartsWith("sound ", StringComparison.Ordinal));
        int wait = FramesUntil(root, () => root.Top is MessagePopup { Message: "text 217" });
        Assert.Equal(42, wait);                                                    // state 24, then 41 frames of state 25
        Assert.Equal(sounds, s.Calls.Count(c => c.StartsWith("sound ", StringComparison.Ordinal)));   // SE 13 is an empty entry

        Assert.False(s.Slots[100].Used);
        Assert.DoesNotContain(100, screen.List);
        Assert.Equal(count - 1, screen.List.Count);
        UiTestKit.Run(root, 480 + 40);
        Assert.Same(screen, root.Top);
    }

    [Fact]
    public void AManualSaveInTheSaveListIsDeletedAndItsSlotBecomesTheBlankOne()
    {
        (UiRoot root, PreviewServices s, SaveLoadScreen screen) = Open(save: true);
        Assert.Equal([6, 5, 4, 3, 2, 1, 0], screen.List);
        CursorTo(root, screen, 5);
        AnswerYes(root);
        Assert.True(FramesUntil(root, () => root.Top is MessagePopup { Message: "text 217" }) > 0);
        Assert.Contains("delete 5", s.Calls);
        Assert.Equal([5, 4, 3, 2, 1, 0], screen.List);
    }

    [Fact]
    public void TheFrameUnderTheCursorFillsInWhileTheDeleteRuns()
    {
        (UiRoot root, _, SaveLoadScreen screen) = Open(save: true);
        CursorTo(root, screen, 4);
        int row = screen.List.ToList().IndexOf(4) - screen.Scroll;
        AnswerYes(root);
        UiTestKit.Run(root, 10);
        Assert.Equal(0.5f, screen.DeleteFill, 3);

        (float fx, float fy) = SaveLoadScreen.FramePosition(row);
        RectF[] fills = [.. UiTestKit.Draw(root).Commands.ToArray()
            .Where(c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.SaveParts, C: 8 })
            .Select(Bounds)
            .Where(b => MathF.Abs(b.X - (fx + 32f)) < 0.5f && MathF.Abs(b.Y - (fy + 32f)) < 0.5f)];
        Assert.Single(fills);
        Assert.Equal(896f, fills[0].W, 1);                                          // 0x7A84F0: (63, 368)-(959, 576) of the frame sheet
        Assert.Equal(208f, fills[0].H, 1);
        UiTestKit.Run(root, 10);
        Assert.Equal(0f, screen.DeleteFill);                                       // the slot is gone, the fill with it
    }

    [Fact]
    public void ARefusedDeleteNeverSaysDeleted()
    {
        (UiRoot root, PreviewServices s, SaveLoadScreen screen) = Open(save: false);
        s.DeleteFails = true;
        CursorTo(root, screen, 101);
        AnswerYes(root);
        Assert.Equal(20, FramesUntil(root, () => s.Calls.Contains("delete 101")));
        Assert.Equal("text 189", Assert.IsType<MessagePopup>(root.Top).Message);   // "Unable to save data." (state 24's failure, 0x7276A)
        UiTestKit.Run(root, 120);
        Assert.DoesNotContain(root.Stack, x => x is MessagePopup { Message: "text 217" });
        Assert.True(s.Slots[101].Used);
        Assert.Contains(101, screen.List);
    }

    [Fact]
    public void TheBlankSlotOfTheSaveListSaysNoSaveDataWithoutADialog()
    {
        (UiRoot root, PreviewServices s, SaveLoadScreen screen) = Open(save: true);
        Assert.Equal(6, screen.CursorSlot);
        s.Calls.Clear();
        UiTestKit.Press(root, PadButtons.Triangle);
        Assert.Equal("text 193", Assert.IsType<MessagePopup>(root.Top).Message);
        Assert.Contains("sound Done", s.Calls);                                     // SE 1 (0x735AE)
        Assert.DoesNotContain(s.Calls, c => c.StartsWith("delete ", StringComparison.Ordinal));
        Assert.DoesNotContain(root.Stack, x => x is ConfirmDialog);
    }

    [Fact]
    public void TheQuickSaveIsInNoListAndTheServicesRefuseIt()
    {
        (_, PreviewServices s, SaveLoadScreen screen) = Open(save: false);
        Assert.True(s.Slots[139].Used);
        Assert.DoesNotContain(139, screen.List);
        Assert.False(s.Delete(139));
        Assert.True(s.Slots[139].Used);
    }
}

using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>
/// Package K21: the port's toast fades in, holds, fades out and goes; it is drawn last and left out while a screenshot is being captured. Package K24: it is a small box
/// in the bottom left corner, above the menus' guide bar, on show for a second and a half; a notice with something to read may ask for the longer hold.
/// </summary>
public sealed class ToastTests
{
    private static UiRoot Open()
    {
        UiRoot root = UiTestKit.Root(new PreviewServices(Language.En));
        root.Push(new ModePanelScreen());
        UiTestKit.Run(root, 30);
        return root;
    }

    [Fact]
    public void TheToastFadesInHoldsFadesOutAndGoesInASecondAndAHalf()
    {
        UiRoot root = Open();
        Assert.Null(root.Toast);
        root.ShowToast("Screenshot saved");
        UiToast toast = Assert.IsType<UiToast>(root.Toast);
        Assert.Equal("Screenshot saved", toast.Text);
        Assert.Equal(UiToast.HoldFrames, toast.Hold);
        Assert.Equal(0f, toast.Alpha);
        UiTestKit.Run(root, UiToast.FadeInFrames);
        Assert.Equal(1f, toast.Alpha);
        UiTestKit.Run(root, UiToast.HoldFrames - 1);
        Assert.Equal(1f, toast.Alpha);
        Assert.Same(toast, root.Toast);
        UiTestKit.Run(root, UiToast.FadeOutFrames / 2);
        Assert.InRange(toast.Alpha, 0.3f, 0.7f);
        UiTestKit.Run(root, UiToast.FadeOutFrames);
        Assert.True(toast.Done);
        Assert.Null(root.Toast);
        Assert.Equal(90, UiToast.FadeInFrames + UiToast.HoldFrames + UiToast.FadeOutFrames);       // 1.5 s at 60 Hz (package K24; K21's was 3 s)
        Assert.True(UiToast.FadeInFrames <= 8 && UiToast.FadeOutFrames <= 15, "the fades are quick");
    }

    [Fact]
    public void ANoticeWithSomethingToReadMayHoldLonger()
    {
        UiRoot root = Open();
        root.ShowToast("Text WebSocket: ws://127.0.0.1:6677", brief: false);
        UiToast toast = root.Toast!;
        Assert.Equal(UiToast.LongHoldFrames, toast.Hold);
        Assert.True(UiToast.LongHoldFrames > UiToast.HoldFrames);
        UiTestKit.Run(root, UiToast.FadeInFrames + UiToast.HoldFrames + UiToast.FadeOutFrames);     // where a brief toast has gone, the long one is still fully up
        Assert.Same(toast, root.Toast);
        Assert.Equal(1f, toast.Alpha);
        UiTestKit.Run(root, UiToast.LongHoldFrames - UiToast.HoldFrames - UiToast.FadeOutFrames + UiToast.FadeOutFrames);
        Assert.True(toast.Done);
        Assert.Null(root.Toast);
    }

    [Fact]
    public void TheToastIsASmallBoxInTheBottomLeftCornerAboveTheGuideBarDrawnLastAndNotWhileHidden()
    {
        UiRoot root = Open();
        int plain = UiTestKit.Draw(root).Count;
        root.ShowToast("Screenshot saved");
        UiTestKit.Run(root, UiToast.FadeInFrames);
        DisplayList list = UiTestKit.Draw(root);
        Assert.True(list.Count > plain + 2, $"the toast adds its box and its glyphs ({list.Count} commands against {plain})");
        // the box lies in the bottom left corner: inside the canvas, left of the menus' page (x 407), above the guide bar (y 1023) and clear of the mode panel at the top
        RectF box = root.Toast!.Box(root.Painter);
        Assert.Equal(40f, box.X);
        Assert.True(box.X + box.W < 407f, $"box {box} runs into the page");
        Assert.True(box.Y > 900f && box.Y + box.H <= 1023f - 8f, $"box {box} is not above the guide bar");
        Assert.True(box.H <= 40f, $"box {box} is not small");
        Assert.True(box.W is > 100f and < 320f, $"box {box}");

        // a longer text widens the box to the left margin's right, never moves it
        root.ShowToast("Text copied to the clipboard");
        UiTestKit.Run(root, UiToast.FadeInFrames);
        RectF wide = root.Toast!.Box(root.Painter);
        Assert.Equal((box.X, box.Y, box.H), (wide.X, wide.Y, wide.H));
        Assert.True(wide.W > box.W);

        root.HideToast = true;
        Assert.Equal(plain, UiTestKit.Draw(root).Count);                     // captured frames never show it
        root.HideToast = false;
        Assert.True(UiTestKit.Draw(root).Count > plain + 2);

        root.ShowToast("Another");                                           // a new toast replaces the one on show
        Assert.Equal("Another", root.Toast!.Text);
        Assert.Equal(0f, root.Toast.Alpha);
    }
}

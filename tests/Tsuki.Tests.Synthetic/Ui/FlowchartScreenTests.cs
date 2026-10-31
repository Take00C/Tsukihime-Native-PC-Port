using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>The flowchart screen in game (system menu: jump to a node) and its cursor, scrolling and availability (docs/formats/gallery.md 5.5).</summary>
public sealed class FlowchartScreenTests
{
    private static void Press(UiRoot root, PadButtons b) => UiTestKit.Press(root, b);

    private static void Run(UiRoot root, int frames) => UiTestKit.Run(root, frames);

    private static (UiRoot Root, PreviewServices S) OpenMenu(Action<PreviewServices>? progress = null)
    {
        PreviewServices s = GalleryKit.Services();
        progress?.Invoke(s);
        UiRoot root = GalleryKit.Root(s);
        Assert.True(root.OpenSystemMenu());
        Run(root, 40);
        return (root, s);
    }

    private static void ToFlowChart(UiRoot root)
    {
        for (int i = 0; i < 3; i++)
        {
            Press(root, PadButtons.Down);
        }

        Press(root, PadButtons.Decide);
    }

    [Fact]
    public void WithoutThePlaythroughTheChartDoesNotOpenAndTheMenuSaysSo()
    {
        (UiRoot root, PreviewServices s) = OpenMenu();
        ToFlowChart(root);
        Assert.Contains("open flowchart", s.Calls);
        // "You cannot use the flowchart yet." (ui_text 1497) over the menu for 480 frames, as the gallery's button shows it; no buzzer (package R1)
        var popup = Assert.IsType<MessagePopup>(root.Top);
        Assert.Equal("text 1497", popup.Message);
        Assert.DoesNotContain("sound Buzzer", s.Calls);
        Assert.IsType<SystemMenuScreen>(root.Stack[^2]);
        Run(root, 520);
        Assert.IsType<SystemMenuScreen>(root.Top);
    }

    [Fact]
    public void TheChartOpensOnTheCurrentNodeAndHidesTheMenuButNotTheBlur()
    {
        (UiRoot root, _) = OpenMenu(GalleryKit.ReadChain);
        ToFlowChart(root);
        var chart = Assert.IsType<FlowchartScreen>(root.Top);
        Assert.False(chart.Gallery);
        Assert.True(chart.Opaque);
        Assert.Equal(3, chart.Chart.RecordAt(chart.Cell)!.Value.Node);
        Assert.True(root.BlurScene);
        Run(root, 40);
        Assert.Equal(1f, chart.Fade);

        // the menu is not drawn under the opaque chart
        DisplayList list = UiTestKit.Draw(root);
        Assert.DoesNotContain(list.Commands.ToArray(), c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.MenuParts, C: 1 });
    }

    [Fact]
    public void TheCurrentNodeOutsideTheEndingListIsRequired()
    {
        (UiRoot root, PreviewServices s) = OpenMenu(p =>
        {
            GalleryKit.ReadChain(p);
            p.CurrentNode = 540;
        });
        ToFlowChart(root);
        Assert.IsType<MessagePopup>(root.Top);
        Assert.IsType<SystemMenuScreen>(root.Stack[^2]);
        Assert.DoesNotContain("sound Buzzer", s.Calls);
    }

    [Fact]
    public void DecideAsksToRewindAndYesJumpsToTheNodeAndClearsTheScreens()
    {
        (UiRoot root, PreviewServices s) = OpenMenu(GalleryKit.ReadChain);
        ToFlowChart(root);
        Run(root, 40);
        Press(root, PadButtons.Decide);
        var dialog = Assert.IsType<ConfirmDialog>(root.Top);
        Assert.Equal(1498, dialog.MessageId(root.Context.Assets));
        Run(root, 20);
        Assert.True(dialog.CursorOnYes);
        Press(root, PadButtons.Decide);
        Run(root, 40);
        Assert.Contains("commit", s.Calls);
        Assert.Contains("flow jump 2 3 303", s.Calls);
        Assert.False(root.Active);
    }

    [Fact]
    public void NoLeavesTheChartOpenAndNothingJumps()
    {
        (UiRoot root, PreviewServices s) = OpenMenu(GalleryKit.ReadChain);
        ToFlowChart(root);
        Run(root, 40);
        Press(root, PadButtons.Decide);
        Run(root, 20);
        Press(root, PadButtons.Right);
        Press(root, PadButtons.Decide);
        Run(root, 40);
        Assert.IsType<FlowchartScreen>(root.Top);
        Assert.DoesNotContain(s.Calls, c => c.StartsWith("flow jump", StringComparison.Ordinal));
    }

    [Fact]
    public void CancelClosesTheChartBackToTheMenu()
    {
        (UiRoot root, _) = OpenMenu(GalleryKit.ReadChain);
        ToFlowChart(root);
        Run(root, 40);
        Press(root, PadButtons.Cancel);
        Run(root, 40);
        Assert.IsType<SystemMenuScreen>(root.Top);
    }

    [Fact]
    public void InGameTheCursorFollowsTheNodesOfThisPlaythroughOnly()
    {
        (UiRoot root, _) = OpenMenu(GalleryKit.ReadChain);
        ToFlowChart(root);
        Run(root, 40);
        var chart = (FlowchartScreen)root.Top!;
        Assert.Equal(new ChartCell(0, 2), chart.Cell);
        Press(root, PadButtons.Down);
        Assert.Equal(3, chart.Chart.RecordAt(chart.Cell)!.Value.Column);
        Press(root, PadButtons.Up);
        Assert.Equal(new ChartCell(0, 2), chart.Cell);
    }

    [Fact]
    public void TheGalleryChartMovesBetweenFinishedNodesAndSelectionPlaysTheCursorSound()
    {
        (UiRoot root, PreviewServices s, _) = GalleryKit.OpenGallery(GalleryKit.ReadChain);
        Press(root, PadButtons.Down);
        Press(root, PadButtons.Down);
        Press(root, PadButtons.Decide);
        var chart = (FlowchartScreen)root.Top!;
        Run(root, 40);
        Assert.Equal(new ChartCell(0, 0), chart.Cell);
        Press(root, PadButtons.Down);
        Assert.Equal(new ChartCell(0, 1), chart.Cell);
        Press(root, PadButtons.Down);
        Press(root, PadButtons.Right);
        Assert.Equal(new ChartCell(1, 2), chart.Cell);
        Assert.Contains("sound Cursor", s.Calls);
        Press(root, PadButtons.Cancel);
        Run(root, 40);
        Assert.IsType<GalleryScreen>(root.Top);
    }

    [Fact]
    public void ThePointerSelectsANodeAndAClickPressesIt()
    {
        (UiRoot root, PreviewServices s, _) = GalleryKit.OpenGallery(GalleryKit.ReadChain);
        Press(root, PadButtons.Down);
        Press(root, PadButtons.Down);
        Press(root, PadButtons.Decide);
        var chart = (FlowchartScreen)root.Top!;
        Run(root, 40);
        ChartNode node = chart.Chart.Nodes.First(n => n.Node == 4);
        UiTestKit.Point(root, node.X + 20f, node.Y + 20f);
        Assert.Equal(4, chart.Chart.RecordAt(chart.Cell)!.Value.Node);
        UiTestKit.Point(root, node.X + 20f, node.Y + 20f, click: true);
        Run(root, 100);
        Assert.Contains("replay 304 4", s.Calls);
    }

    [Fact]
    public void AvailabilityFollowsTheGalleryAndTheInGameRules()
    {
        PreviewServices s = GalleryKit.Services();
        UiRoot root = GalleryKit.Root(s);
        Assert.False(FlowchartScreen.Available(root.Context, gallery: true));
        Assert.False(FlowchartScreen.Available(root.Context, gallery: false));
        GalleryKit.Read(s, 1);
        Assert.True(FlowchartScreen.Available(root.Context, gallery: true));
        Assert.False(FlowchartScreen.Available(root.Context, gallery: false));
        s.Progress.Route.Add(1);
        Assert.True(FlowchartScreen.Available(root.Context, gallery: false));
        s.CurrentNode = 533;
        Assert.False(FlowchartScreen.Available(root.Context, gallery: false));
    }

    [Fact]
    public void DuringAReplayFromTheChartTheMenuTabEndsTheReplay()
    {
        (UiRoot root, PreviewServices s) = OpenMenu(p =>
        {
            GalleryKit.ReadChain(p);
            p.ReplayActive = true;
        });
        root.Context.Gallery.Mode = (int)GalleryMode.Flowchart;
        ToFlowChart(root);
        var dialog = Assert.IsType<ConfirmDialog>(root.Top);
        Run(root, 20);
        Assert.True(dialog.CursorOnYes);
        Press(root, PadButtons.Decide);
        Run(root, 40);
        Assert.Contains("end replay", s.Calls);
    }

    [Fact]
    public void ThePreviewServicesBelieveEmptyDataGracefully()
    {
        var s = new PreviewServices();
        s.Believe(UiTestKit.Data(), "mid");
        Assert.False(s.SceneDone(1));
    }
}

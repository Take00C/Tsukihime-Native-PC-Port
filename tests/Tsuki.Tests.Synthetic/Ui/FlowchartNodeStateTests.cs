using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>
/// Package K44 (issue #9): what a node of the chart looks like and whether the cursor takes it, by the PS4's node function (0x25950) on the fake chart:
/// an unfinished node is a black frame (also while it is the current scene), selectable in game once this playthrough reached it, never in the gallery.
/// </summary>
public sealed class FlowchartNodeStateTests
{
    private static readonly FakeGalleryTables Tables = new();

    // scenes 1..2 read; scene 3 (column 2, row 0, link 566) entered in this playthrough and not finished yet: the player is inside it
    private static PreviewProgress InsideScene3()
    {
        var p = new PreviewProgress();
        foreach (int n in new[] { 1, 2 })
        {
            p.Seen.Add(n);
            p.Done.Add(n);
            p.Route.Add(n);
        }

        p.Edges.Add(565);
        p.RouteEdges.Add(565);
        p.Seen.Add(3);
        p.Route.Add(3);
        p.Edges.Add(566);
        p.RouteEdges.Add(566);
        return p;
    }

    private static bool HasThumbnailAt(FlowchartChart chart, float x, float y) =>
        chart.Sprites.Any(s => s.Sheet == ChartSheet.Mosaic && s.Dst.X == x + 1f && s.Dst.Y == y + 1f);

    private static bool HasFrameAt(FlowchartChart chart, float x, float y) =>
        chart.Sprites.Any(s => s.Sheet == ChartSheet.NodeFrame && s.Dst.X == x && s.Dst.Y == y);

    [Fact]
    public void InGameTheCurrentUnfinishedSceneIsABlackFrameTheCursorCanTake()
    {
        FlowchartChart chart = FlowchartChart.Build(Tables, Language.En, InsideScene3(), gallery: false);
        ChartNode n3 = Assert.Single(chart.Nodes, n => n.Node == 3);
        Assert.False(HasThumbnailAt(chart, n3.X, n3.Y));
        Assert.True(HasFrameAt(chart, n3.X, n3.Y));

        // the chart opens on the current node (F110)
        Assert.Equal(3, chart.Nodes[chart.NodeAt(chart.OpenCursor(3))].Node);
    }

    [Fact]
    public void TheGalleryShowsTheUnfinishedSceneAsABlackFrameItCannotSelect()
    {
        PreviewProgress p = InsideScene3();
        FlowchartChart chart = FlowchartChart.Build(Tables, Language.En, p, gallery: true);
        Assert.DoesNotContain(chart.Nodes, n => n.Node == 3);
        Assert.True(HasFrameAt(chart, 438f, chart.RowY[2]));

        // once its _RDED has run it has its picture and replays
        p.Done.Add(3);
        chart = FlowchartChart.Build(Tables, Language.En, p, gallery: true);
        ChartNode n3 = Assert.Single(chart.Nodes, n => n.Node == 3);
        Assert.True(HasThumbnailAt(chart, n3.X, n3.Y));
    }

    [Fact]
    public void AFinishedNodeDrawsItsConnectorEvenWhenItsOwnEdgeWasNotSeen()
    {
        // 0x25F9E: the grey connector of a record skips the edge test when the node function drew the node (state bit 0x80000000); node 3 finished
        // through a jump that did not set its link 566
        PreviewProgress p = InsideScene3();
        p.Edges.Remove(566);
        p.RouteEdges.Remove(566);
        p.Done.Add(3);
        FlowchartChart chart = FlowchartChart.Build(Tables, Language.En, p, gallery: true);
        ChartNode n3 = Assert.Single(chart.Nodes, n => n.Node == 3);
        Assert.Contains(chart.Sprites, s => s.Sheet == ChartSheet.LineGrey && s.Dst.X == n3.X && s.Dst.Y == n3.Y - 5f);
    }
}

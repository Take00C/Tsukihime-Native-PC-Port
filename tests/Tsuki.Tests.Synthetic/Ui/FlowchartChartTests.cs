using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>The flowchart's column filter, layout and cursor movement (docs/formats/gallery.md 5.4; knife <c>flow.rs</c>) on the fake chart.</summary>
public sealed class FlowchartChartTests
{
    private static readonly FakeGalleryTables Tables = new();

    private static FlowchartChart Build(PreviewProgress p, bool gallery, Language language = Language.En) => FlowchartChart.Build(Tables, language, p, gallery);

    private static PreviewProgress ReadTo(int lastScene)
    {
        var p = new PreviewProgress();
        for (int n = 1; n <= lastScene; n++)
        {
            p.Seen.Add(n);
            p.Done.Add(n);
            p.Route.Add(n);
        }

        // the edge into each scene read (the marker -4 of column 3 shares the edge 569 with scene 5's branch)
        foreach ((int scene, int id) in new[] { (2, 565), (3, 566), (4, 567), (5, 568), (5, 569), (6, 570), (7, 571) })
        {
            if (scene <= lastScene)
            {
                p.Edges.Add(id);
                p.RouteEdges.Add(id);
            }
        }

        return p;
    }

    [Fact]
    public void NoProgressKeepsNoColumn()
    {
        Assert.Empty(FlowchartChart.KeptColumns(Tables.ChartColumns, new PreviewProgress()));
        FlowchartChart chart = Build(new PreviewProgress(), gallery: true);
        Assert.Empty(chart.Nodes);
        Assert.Equal(0, chart.Rows);
    }

    [Fact]
    public void AFinishedSingleNodeColumnIsKeptAndLaterColumnsFollowTheirScenes()
    {
        var p = new PreviewProgress();
        p.Done.Add(1);
        Assert.Equal([0], FlowchartChart.KeptColumns(Tables.ChartColumns, p));
        p.Done.Add(2);
        p.Done.Add(3);
        Assert.Equal([0, 1, 2], FlowchartChart.KeptColumns(Tables.ChartColumns, p));
    }

    [Fact]
    public void AnEdgeAloneLightsAMarkerNodeAndTheFilterReadsOnePastTheEndOfTheColumn()
    {
        // marker -4 in column 3, row 1: lit by its edge 569 while scene 4 is not finished; its previous column has two nodes (3, 4)
        var p = new PreviewProgress();
        p.Done.Add(5);
        p.Edges.Add(569);
        List<int> kept = FlowchartChart.KeptColumns(Tables.ChartColumns, p);
        Assert.Contains(3, kept);

        // a column whose neighbour has fewer rows reads the record after its end: the column before the last has one node, so row 1 of column 5 reads the overrun
        ChartColumnRow[] columns = [new(0, [new(1, -1, -1, 0, 0, 0, 0, 0, 0)], 77, 88), new(0, [new(2, -1, 5, 0, 0, 0, 0, 0, 0), new(-9, -1, 6, 0, 0, 0, 0, 0, 0)], 0, 0)];
        var q = new PreviewProgress();
        q.Done.Add(2);
        q.Edges.Add(6);
        Assert.Equal([1], FlowchartChart.KeptColumns(columns, q));
    }

    [Fact]
    public void TheGalleryLaysOutTheReadColumnsAndOnlyFinishedNodesAreSelectable()
    {
        FlowchartChart chart = Build(ReadTo(4), gallery: true);
        Assert.Equal(3, chart.Rows);
        Assert.Equal([1, 2, 3, 4], chart.Nodes.Select(n => n.Node));
        Assert.All(chart.Nodes, n => Assert.True(n.Node > 0));

        // nodes 438 + 96 j wide, rows 56 apart
        Assert.Contains(chart.Nodes, n => n.Node == 4 && n.X == 438f + 96f);
        Assert.Equal(56f, chart.RowY[1] - chart.RowY[0], 3);
    }

    [Fact]
    public void InGameOnlyTheNodesOfThisPlaythroughAreSelectableFromPhaseOne()
    {
        PreviewProgress p = ReadTo(4);
        p.Route.Remove(4);
        FlowchartChart chart = Build(p, gallery: false);
        Assert.DoesNotContain(chart.Nodes, n => n.Node == 4);
        Assert.Contains(chart.Nodes, n => n.Node == 3);

        // phase 0 (the prologue) is not selectable in game
        Assert.DoesNotContain(chart.Nodes, n => n.Node is 1 or 2);
    }

    [Fact]
    public void SectionsGetBandsDividersAndTheirTitles()
    {
        FlowchartChart chart = Build(ReadTo(7), gallery: true);
        Assert.Contains(chart.Sprites, s => s.Sheet == ChartSheet.BandMiddle);
        Assert.Contains(chart.Sprites, s => s.Sheet == ChartSheet.BandUnder);
        Assert.Contains(chart.Sprites, s => s.Sheet == ChartSheet.BandUpper);
        Assert.Equal(2, chart.Limits.Count);
        Assert.NotEmpty(chart.Titles[1]);

        // English labels sit at x 2, the other languages at 208
        Assert.Equal(2f, chart.Titles[1][0].Dst.X);
        Assert.Equal(208f, Build(ReadTo(7), gallery: true, Language.Ja).Titles[1][0].Dst.X);

        // the first chapter also shows the Prologue label (the last non-empty cell, index 28)
        Assert.Equal(2, chart.Titles[0].Count);
        Assert.Equal(new RectF(25, 28 * 30, 440, 30), chart.Titles[0][1].Src);

        // farther pieces are drawn first
        int[] prios = [.. chart.Sprites.Select(s => s.Prio)];
        Assert.Equal(prios.OrderByDescending(x => x), prios);
    }

    [Fact]
    public void ThumbnailsComeFromTheMosaicCellOfTheNode()
    {
        FlowchartChart chart = Build(ReadTo(3), gallery: true);
        ChartSprite thumb = chart.Sprites.First(s => s.Sheet == ChartSheet.Mosaic);
        Assert.Equal(79f, thumb.Src.W);
        Assert.Equal(43f, thumb.Src.H);
        Assert.Equal(102f, thumb.Dst.W);
        Assert.Equal(62f, thumb.Dst.H);
    }

    [Fact]
    public void EveryMoveOfTheCursorLandsOnANode()
    {
        foreach (bool gallery in new[] { false, true })
        {
            FlowchartChart chart = Build(ReadTo(7), gallery);
            for (int k = 0; k < chart.Rows; k++)
            {
                for (int j = 0; j < 4; j++)
                {
                    var cell = new ChartCell(j, k);
                    foreach (ChartCell? to in new[] { chart.MoveLeft(cell), chart.MoveRight(cell), chart.MoveUp(cell), chart.MoveDown(cell), chart.KeepInView(cell, _ => 500f) })
                    {
                        if (to is { } c)
                        {
                            Assert.True(chart.NodeAt(c) >= 0, $"{gallery} ({j}, {k}) -> {c}");
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void TheCursorOpensOnTheLastRowThatHoldsTheNodeId()
    {
        FlowchartChart chart = Build(ReadTo(7), gallery: false);
        var seen = new List<ChartCell>();
        ChartCell cur = chart.OpenCursor(5, seen.Add);
        Assert.Equal(seen[^1], cur);
        Assert.Equal(5, chart.RecordAt(cur)!.Value.Node);
        Assert.Equal(new ChartCell(0, 0), chart.OpenCursor(-1, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void ARecordGivesItsColumnNodeAndScene()
    {
        FlowchartChart chart = Build(ReadTo(7), gallery: true);
        ChartRecord rec = chart.RecordAt(new ChartCell(1, 2))!.Value;
        Assert.Equal(2, rec.Column);
        Assert.Equal(4, rec.Node);
        Assert.Equal(304, rec.ScriptScene);
        Assert.Null(chart.RecordAt(new ChartCell(9, 9)));
    }

    [Fact]
    public void TheScrollRangeIsTheChartHeightBeyondTheView()
    {
        FlowchartChart small = Build(ReadTo(3), gallery: true);
        Assert.Equal(0f, small.ScrollMax);
        FlowchartChart big = Build(ReadTo(7), gallery: true);
        Assert.True(big.ScrollMax >= 0f);
    }
}

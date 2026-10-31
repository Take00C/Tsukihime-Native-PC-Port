using Tsuki.Engine.Contracts;
using Tsuki.Tests.Data.Source;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Data.Ui;

/// <summary>
/// The mouse on screens whose size comes from the user's own data (package K12): the flowchart and the CG grid only scroll with the real tables, and the text speed samples
/// of the config page are laid out with the real glyph pages of each language.
/// </summary>
public sealed class MouseRealDataTests
{
    private static readonly Language[] Languages = [Language.Ja, Language.En, Language.Zc, Language.Zt];

    private static void Click(UiRoot root, float x, float y)
    {
        root.Step(UiInput.PointerFrame(x, y, PointerButtons.Left, PointerButtons.Left, 0, root.Context.Frame + 1));
        root.Step(UiInput.PointerFrame(x, y, PointerButtons.None, PointerButtons.None, 0, root.Context.Frame + 1));
    }

    private static void Drag(UiRoot root, float x0, float y0, float x1, float y1, int steps = 10)
    {
        root.Step(UiInput.PointerFrame(x0, y0, PointerButtons.Left, PointerButtons.Left, 0, root.Context.Frame + 1));
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            root.Step(UiInput.PointerFrame(x0 + ((x1 - x0) * t), y0 + ((y1 - y0) * t), PointerButtons.Left, PointerButtons.None, 0, root.Context.Frame + 1));
        }

        root.Step(UiInput.PointerFrame(x1, y1, PointerButtons.None, PointerButtons.None, 0, root.Context.Frame + 1));
    }

    private static (UiRoot Root, PreviewServices Services, FlowchartScreen Chart) OpenChart()
    {
        var services = new PreviewServices(Language.En);
        UiRoot root = UiPreview.Open("flowchart-gallery", SharedSource.Source, services, 80);
        var chart = Assert.IsType<FlowchartScreen>(root.Top);
        return (root, services, chart);
    }

    // ---- the flowchart ----------------------------------------------------------------------------------------------------------------------------

    [GameDataFact]
    public void TheFlowchartScrollBarDragsAndPagesTheChart()
    {
        (UiRoot root, _, FlowchartScreen chart) = OpenChart();
        float max = chart.Chart.ScrollMax;
        Assert.True(max > 1000f);
        float y = UiScrollBar.ThumbTop(0f, 610f) + 100f;
        Drag(root, 1855f, y, 1855f, y + 305f);
        UiPreview.Run(root, 40);
        Assert.InRange(chart.Scroll, (max / 2f) - 40f, (max / 2f) + 40f);                // the middle of the travel, to the pixel row of the pointer

        // the track below the thumb pages on by 840, the track above it back
        float before = chart.Scroll;
        Click(root, 1855f, 990f);
        UiPreview.Run(root, 40);
        Assert.Equal(before + 840f, chart.Scroll, 1);
        Click(root, 1855f, 120f);
        UiPreview.Run(root, 40);
        Assert.Equal(before, chart.Scroll, 1);
    }

    [GameDataFact]
    public void ADragOnTheChartPansItAndOnlyAClickOnANodePressesIt()
    {
        (UiRoot root, PreviewServices s, FlowchartScreen chart) = OpenChart();
        float max = chart.Chart.ScrollMax;
        chart.ScrollNow(2000f);
        UiPreview.Run(root, 40);
        Assert.Equal(2000f, chart.Scroll, 1);

        // press on an empty spot (far left of the chart) and drag down 300 px: the chart scrolls up by as much
        Drag(root, 40f, 400f, 40f, 700f);
        UiPreview.Run(root, 20);
        Assert.Equal(1700f, chart.Scroll, 1);
        Drag(root, 40f, 700f, 40f, 100f, steps: 20);                                  // up 600: 2300
        UiPreview.Run(root, 20);
        Assert.Equal(2300f, chart.Scroll, 1);
        Drag(root, 40f, 100f, 40f, 9000f, steps: 20);                                 // far past the top: clamped
        UiPreview.Run(root, 20);
        Assert.Equal(0f, chart.Scroll, 1);
        Drag(root, 40f, 900f, 40f, -9000f, steps: 20);
        UiPreview.Run(root, 20);
        Assert.Equal(max, chart.Scroll, 1);
        Assert.DoesNotContain(s.Calls, c => c.StartsWith("replay", StringComparison.Ordinal));

        // a node that is on screen: dragging it pans, a click presses it
        chart.ScrollNow(0f);
        UiPreview.Run(root, 40);
        ChartNode node = chart.Chart.Nodes.First(n => n.Y - chart.Scroll is > 100f and < 800f && n.X < 1300f);
        Drag(root, node.X + 20f, node.Y - chart.Scroll + 20f, node.X + 20f, node.Y - chart.Scroll + 120f);
        UiPreview.Run(root, 20);
        Assert.DoesNotContain(s.Calls, c => c.StartsWith("replay", StringComparison.Ordinal));
        Assert.Equal(0f, chart.Scroll, 1);                                               // (already at the top: the pan was clamped)
        chart.ScrollNow(0f);
        Click(root, node.X + 20f, node.Y + 20f);
        UiPreview.Run(root, 100);
        Assert.Contains(s.Calls, c => c.StartsWith("replay", StringComparison.Ordinal));
    }

    // ---- the CG grid ------------------------------------------------------------------------------------------------------------------------------

    [GameDataFact]
    public void TheCgGridScrollBarDragsAndPages()
    {
        var services = new PreviewServices(Language.En);
        UiRoot root = UiPreview.Open("gallery-cg", SharedSource.Source, services, 80);
        var gallery = root.Find<GalleryScreen>()!;
        var cg = (GalleryCgPanel)gallery.Panel;
        float max = cg.ScrollMax;
        Assert.True(max > 600f, $"the grid of the mid-game state scrolls ({max})");
        float y = UiScrollBar.ThumbTop(0f, 610f) + 100f;
        Drag(root, 1855f, y, 1855f, y + 305f);
        UiPreview.Run(root, 40);
        Assert.InRange(cg.Scroll, (max / 2f) - 30f, (max / 2f) + 30f);
        float before = cg.Scroll;
        Click(root, 1855f, 990f);
        UiPreview.Run(root, 40);
        Assert.Equal(Math.Min(before + (5 * 139f), max), cg.Scroll, 1);
    }

    // ---- the text speed samples -------------------------------------------------------------------------------------------------------------------

    [GameDataTheory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    [InlineData(Language.Zc)]
    [InlineData(Language.Zt)]
    public void TheTextSpeedSamplesEndInsideTheirBoxInEveryLanguage(Language lang)
    {
        var services = new PreviewServices(lang);
        UiRoot root = UiPreview.Open("config-system", SharedSource.Source, services, 5);
        var config = root.Find<ConfigScreen>()!;
        float scale = config.SampleScale;
        Assert.InRange(scale, 0.8f, 1f);
        UiContext ctx = root.Context;
        for (int k = 0; k < 2; k++)
        {
            string text = ctx.Message(138 + k);
            Assert.False(string.IsNullOrEmpty(text));
            (float left, float right) = ctx.Text.InkSpan(text, ConfigScreen.SampleStyle(scale));
            Assert.True(left >= -1f, $"{lang} {k}: ink starts at {left}");
            Assert.True(ConfigScreen.SampleLeft + right <= ConfigScreen.SampleBoxRight + 0.5f, $"{lang} sample {k} ends at {ConfigScreen.SampleLeft + right} (box {ConfigScreen.SampleBoxRight})");
        }

        // English and Chinese needed the fit; Japanese keeps the PS4's own size
        if (lang == Language.Ja)
        {
            Assert.Equal(1f, scale);
        }
        else
        {
            Assert.True(scale < 1f);
        }
    }
}

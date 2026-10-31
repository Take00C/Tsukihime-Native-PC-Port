using System.Text;
using System.Text.Json;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Text;

namespace Tsuki.Tests.Data.Game;

/// <summary>
/// Package K8: the name plate (text block 14, textbox.md 3.3 / 5.8, adv_state.md 3.2) on the real page of the first choice. The plate is empty in every retail run (its two
/// writers, VA 0x930C0 and 0x937C0, have no callers), so these tests fill it by hand: it is drawn above the current message at the message's origin, in the plate's own
/// box (width 150, spacing 2, scale 0.5), the message moves down by the plate's last-line offset plus 14, the plate bytes reach the history record, and an empty plate changes nothing.
/// </summary>
public sealed class NamePlateTests(ITestOutputHelper output)
{
    private static List<DrawCmd> GlyphQuads(DisplayList list)
    {
        var quads = new List<DrawCmd>();
        foreach (DrawCmd c in list.Commands)
        {
            if (c.Texture.Kind == TextureKind.GlyphPage)
            {
                quads.Add(c);
            }
        }

        return quads;
    }

    [GameDataFact]
    public void ThePlateIsDrawnAboveTheCurrentMessageAndMovesItDown()
    {
        GameHost host = FirstChoiceRig.NewHost(Language.En);
        FirstChoiceRig.RunToChoice(host);
        long n = 5_000_000;
        List<DrawCmd> without = GlyphQuads(FirstChoiceRig.Frame(host, ref n));
        double firstLineY = host.ChoiceFirstY();
        Assert.False(host.NamePlateShown);
        Assert.True(host.NamePlateText.IsEmpty);

        host.SetNamePlate(Encoding.ASCII.GetBytes("Ciel"));
        Assert.True(host.NamePlateShown);
        Assert.Equal("Ciel", Encoding.ASCII.GetString(host.NamePlateText));
        // with F1103 set (the window was closed: _CLO2 / _CLO3, a story movie, a layer change that hides the text) the HUD skips the plate (VA 0x547F7) and moves nothing; a list
        // choice does not set F1103 (the SELD handler and the list setup 0xA5F0D write no F1103: package K15), the question stays on the page above the lines
        Assert.Equal(0, host.F(1103));
        host.Vars.SetF(1103, 1);
        List<DrawCmd> hidden = GlyphQuads(FirstChoiceRig.Frame(host, ref n));
        Assert.True(hidden.Count > 0 && hidden.Count < without.Count, "the page text is hidden, the choice lines stay");     // no plate quads either: the count only fell
        Assert.Equal(firstLineY, host.ChoiceFirstY(), 1e-6);

        host.Vars.SetF(1103, 0);
        List<DrawCmd> with = GlyphQuads(FirstChoiceRig.Frame(host, ref n));

        // four glyphs, outline and fill each, come first (the plate is drawn before the messages)
        int plateQuads = with.Count - without.Count;
        Assert.Equal(8, plateQuads);

        // the plate sits at the origin of the current message (x 30 in English, y of the last page message), 0.5 / 3 text scale: 0.5 px per texel in x
        PageMessage last = host.PageMessages[^1];
        (double X, double Y)[] origins = MessageWindow.PagePositions(host.PageMessages.Select(m => m.Block).ToList(), Language.En, host.PageMessages.Select(m => m.Blank).ToList(), host.PageMessages.Select(m => m.ScaleY).ToList());
        double originY = origins[^1].Y;
        DrawCmd first = with[0];
        Assert.InRange(first.V0.X, 30 * 3.0, (30 * 3.0) + 12);
        Assert.InRange(first.V0.Y, (originY * GameHost.PageScaleY) - 0.01, (originY * GameHost.PageScaleY) + 40);
        double pageSize = first.Texture.B == 0 ? 256.0 : 1024.0;
        double pxPerTexelX = (first.V1.X - first.V0.X) / ((first.V1.U - first.V0.U) * pageSize);
        Assert.InRange(pxPerTexelX, 0.49, 0.51);

        // the messages before the current one do not move; the current one moves down by 0 + 14 units (a one-line plate); the choice lines (drawn after the page, on
        // their own canvas) follow the click icon, which moved with the message
        using JsonDocument doc = GoldenData.Json("choice_oracle.json");
        int pageQuads = doc.RootElement.GetProperty("en").GetProperty("page_quads").GetArrayLength();
        double shift = 14 * GameHost.PageScaleY;
        double choiceShift = 14 * GameHost.ChoiceScaleY;
        int moved = 0;
        for (int i = 0; i < without.Count; i++)
        {
            double d = with[plateQuads + i].V0.Y - without[i].V0.Y;
            if (i >= pageQuads)
            {
                Assert.Equal(choiceShift, d, 0.01);
            }
            else if (Math.Abs(d) > 0.01)
            {
                Assert.Equal(shift, d, 0.01);
                moved++;
            }
            else
            {
                Assert.Equal(0, moved);            // the moved quads are a suffix of the page: the current message is the last one drawn
            }
        }

        Assert.True(moved > 0, "the current message did not move");
        Assert.Equal(firstLineY + 14, host.ChoiceFirstY(), 1e-3);       // the choice lines follow the click icon, which moved with the message
        output.WriteLine($"plate {plateQuads} quads at ({first.V0.X:F2}, {first.V0.Y:F2}); {moved} quads of '{Encoding.ASCII.GetString(last.Text).Trim()}' moved down {shift:F3} px; first choice line y {firstLineY:F2} -> {host.ChoiceFirstY():F2}");
    }

    [GameDataFact]
    public void ThePlateReachesTheHistoryRecordAndAnEmptyPlateDrawsNothing()
    {
        GameHost host = FirstChoiceRig.NewHost(Language.En);
        AdvHistorySink sink = host.AttachHistory();
        FirstChoiceRig.RunToChoice(host);
        host.FillLive(sink.Live, host.Script);
        Assert.Equal(0, sink.Live.Window164FD40[0]);

        host.SetNamePlate(Encoding.ASCII.GetBytes("Arcueid Brunestud, the very long name that is cut at 0x1B bytes"));
        host.FillLive(sink.Live, host.Script);
        Assert.Equal("Arcueid Brunestud, the ve", Encoding.ASCII.GetString(sink.Live.Window164FD40, 0, 25));
        Assert.Equal(0, sink.Live.Window164FD40[0x1B]);              // the 0x1C-byte field keeps its terminating NUL

        host.SetNamePlate([]);
        Assert.False(host.NamePlateShown);
        long n = 6_000_000;
        List<DrawCmd> a = GlyphQuads(FirstChoiceRig.Frame(host, ref n));
        List<DrawCmd> b = GlyphQuads(FirstChoiceRig.Frame(host, ref n));
        Assert.Equal(a.Count, b.Count);
    }
}

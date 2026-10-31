using System.Text.Json;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Game.Effects;

namespace Tsuki.Tests.Data.Game;

/// <summary>
/// Package K8: the draw list of the first story choice (script 01_00_ARC01_1) equals the PS4 choice function's list that the Python oracle
/// (<c>tsuki.misc_boot_title_choice_flow.choice_draw_list</c>, checked against the eboot by <c>tools/eboot/uicalls.py</c>) gives, number by number: the first line's
/// y from the click-icon y, the line pitch, the glow copies, the underline and the flare, the decided phase, the layout width, and the pixel scale of the canvas.
/// The expectations come from <c>Oracle/gen_choice_golden.py</c> (<c>samples/golden_data/choice_oracle.json</c>).
/// </summary>
public sealed class ChoiceDrawOracleTests(ITestOutputHelper output)
{
    private const int Script = FirstChoiceRig.Script;

    private static void AssertView(GameHost host, JsonElement view, string what)
    {
        Assert.Equal(view.GetProperty("phase").GetInt32(), host.ChoicePhase);
        Assert.Equal(view.GetProperty("cursor").GetInt32(), host.ChoiceCursor);
        Assert.Equal(view.GetProperty("prev").GetInt32(), host.ChoicePreviousCursor);
        Assert.True(Math.Abs(view.GetProperty("alpha").GetDouble() - host.ChoiceAlpha) < 1e-6, $"{what}: alpha {host.ChoiceAlpha} vs {view.GetProperty("alpha").GetDouble()}");
        Assert.True(Math.Abs(view.GetProperty("highlight").GetDouble() - host.ChoiceHighlight) < 1e-6, $"{what}: highlight {host.ChoiceHighlight} vs {view.GetProperty("highlight").GetDouble()}");
        Assert.True(Math.Abs(view.GetProperty("flash").GetDouble() - host.ChoiceFlash) < 1e-6, $"{what}: flash {host.ChoiceFlash} vs {view.GetProperty("flash").GetDouble()}");
        // with no cursor nothing reads the flare pulse (it runs from the opening of the choice)
        Assert.True(host.ChoiceCursor < 0 || Math.Abs(view.GetProperty("pulse").GetDouble() - host.ChoicePulse) < 1e-6, $"{what}: pulse {host.ChoicePulse} vs {view.GetProperty("pulse").GetDouble()}");
    }

    private int CompareDraws(GameHost host, JsonElement scenario, string what)
    {
        AssertView(host, scenario.GetProperty("view"), what);
        List<ChoiceDraw> got = host.ChoiceTrace!;
        JsonElement[] want = scenario.GetProperty("draws").EnumerateArray().ToArray();
        Assert.True(want.Length == got.Count, $"{what}: {got.Count} draws, the oracle has {want.Length}\n" + string.Join('\n', got.Select(g => $"  {g.Kind} {g.Slot} ({g.X:F4}, {g.Y:F4}) a {g.Alpha:F4}")));
        int fields = 0;
        for (int i = 0; i < want.Length; i++)
        {
            JsonElement w = want[i];
            ChoiceDraw g = got[i];
            string at = $"{what} draw {i} ({w.GetProperty("kind").GetString()} {w.GetProperty("slot").GetInt32()})";
            Assert.Equal(w.GetProperty("kind").GetString(), g.Kind);
            Assert.Equal(w.GetProperty("slot").GetInt32(), g.Slot);
            Near(w.GetProperty("pos")[0].GetDouble(), g.X, 1e-3, at + " x");
            Near(w.GetProperty("pos")[1].GetDouble(), g.Y, 1e-3, at + " y");
            Near(w.GetProperty("alpha").GetDouble(), g.Alpha, 1e-5, at + " alpha");
            if (g.Kind == "text")
            {
                // the engine's flags word: 0x1020 plus the shadow bit (config +0x28, off by default); the glow copies draw with 0
                Assert.Equal(w.GetProperty("flags").GetInt32(), g.Flags);
                Assert.Equal(w.GetProperty("colour").GetInt32(), g.Colour);
                fields += 2;
            }
            else
            {
                Near(w.GetProperty("scale")[0].GetDouble(), g.ScaleX, 1e-5, at + " scale x");
                Near(w.GetProperty("scale")[1].GetDouble(), g.ScaleY, 1e-5, at + " scale y");
                fields += 2;
            }

            fields += 5;
        }

        return fields;
    }

    private static void Near(double want, double got, double tolerance, string what) =>
        Assert.True(Math.Abs(want - got) <= tolerance, $"{what}: {got} but the oracle has {want}");

    [GameDataTheory]
    [InlineData("en", Language.En)]
    [InlineData("ja", Language.Ja)]
    public void TheFirstChoiceDrawListEqualsTheOracleNumberByNumber(string key, Language language)
    {
        using JsonDocument doc = GoldenData.Json("choice_oracle.json");
        JsonElement e = doc.RootElement.GetProperty(key);
        GameHost host = FirstChoiceRig.NewHost(language);
        FirstChoiceRig.RunToChoice(host);

        // the layout: three lines, the oracle's line counts at the oracle's width (574 in English: line 3 stays on one line), the first line below the page
        Assert.Equal(e.GetProperty("count").GetInt32(), host.ChoiceLines.Count);
        JsonElement counts = e.GetProperty("line_counts");
        for (int k = 0; k < host.ChoiceLines.Count; k++)
        {
            Assert.Equal(counts[k].GetInt32(), host.ChoiceLines[k].Block!.NLines);
            Assert.Equal(e.GetProperty("choice_width").GetInt32(), host.ChoiceLines[k].Block!.WidthArg);
        }

        Assert.Equal(e.GetProperty("f823").GetInt32(), host.F(823));
        Assert.Equal(e.GetProperty("page_messages").GetInt32(), host.PageMessages.Count);
        Near(e.GetProperty("y0").GetDouble(), host.ChoiceFirstY(), 1e-4, "first line y");

        long n = 1_000_000;
        int fields = 0;
        JsonElement s = e.GetProperty("scenarios");
        FirstChoiceRig.Frame(host, ref n);                                                 // the choice is selectable: nothing highlighted yet
        fields += CompareDraws(host, s.GetProperty("A_open"), key + " A_open");
        if (language == Language.Ja)
        {
            Assert.True(host.HoverChoice(1));
            FirstChoiceRig.Frames(host, ref n, 5);
            fields += CompareDraws(host, s.GetProperty("B_cursor1_5f"), key + " B_cursor1_5f");
            output.WriteLine($"{key}: {fields} field comparisons");
            return;
        }

        Assert.True(host.HoverChoice(1));
        FirstChoiceRig.Frames(host, ref n, 5);
        fields += CompareDraws(host, s.GetProperty("B_cursor1_5f"), key + " B_cursor1_5f");
        Assert.True(host.HoverChoice(2));
        FirstChoiceRig.Frames(host, ref n, 3);
        fields += CompareDraws(host, s.GetProperty("C_cursor2_from1_3f"), key + " C_cursor2_from1_3f");

        // a decision within 10 frames of a move: the previous line's glow is still drawn (system_flow.md 5.3)
        Assert.True(host.Choose(2));
        FirstChoiceRig.Frames(host, ref n, 2);
        fields += CompareDraws(host, s.GetProperty("E_decided_2_after_move"), key + " E_decided_2_after_move");

        GameHost second = FirstChoiceRig.NewHost(language);
        FirstChoiceRig.RunToChoice(second);
        n = 2_000_000;
        FirstChoiceRig.Frame(second, ref n);
        Assert.True(second.HoverChoice(2));
        FirstChoiceRig.Frames(second, ref n, 12);
        Assert.True(second.Choose(1));
        FirstChoiceRig.Frames(second, ref n, 18);
        fields += CompareDraws(second, s.GetProperty("D_decided_1_18f"), key + " D_decided_1_18f");
        output.WriteLine($"{key}: {fields} field comparisons");
    }

    [GameDataTheory]
    [InlineData("en", Language.En)]
    [InlineData("ja", Language.Ja)]
    public void TheChoiceTextIsDrawnAtThePs4PixelScaleOfItsCanvas(string key, Language language)
    {
        GameHost host = FirstChoiceRig.NewHost(language);
        FirstChoiceRig.RunToChoice(host);
        long n = 3_000_000;
        DisplayList list = FirstChoiceRig.Frame(host, ref n);
        using JsonDocument doc = GoldenData.Json("choice_oracle.json");
        double y0 = doc.RootElement.GetProperty(key).GetProperty("y0").GetDouble();
        double scale = 1080f / 344.7f;

        // the lowest glyph rows of the three lines sit at (y0 + 26.02 k + glyph offset) * 1080 / 344.7; the glyph quads of line k all lie within its 26.02-unit row
        // (a quad of the page text above starts higher, the quads of the next line lower)
        var rows = new List<double>();
        foreach (DrawCmd c in list.Commands)
        {
            if (c.Texture.Kind == TextureKind.GlyphPage && c.V0.Rgba >> 24 > 0)
            {
                rows.Add(c.V0.Y);
            }
        }

        double firstChoiceRowTop = y0 * scale;
        double belowPage = rows.Where(r => r >= firstChoiceRowTop - 1).Min();
        Assert.InRange(belowPage, firstChoiceRowTop - 0.01, firstChoiceRowTop + (26.02 * scale));
        output.WriteLine($"{key}: y0 {y0:F3} units = {firstChoiceRowTop:F1} px; the first choice glyph starts at {belowPage:F1} px");
    }

    [GameDataTheory]
    [InlineData("en", Language.En)]
    [InlineData("ja", Language.Ja)]
    public void ThePageTextIsDrawnAtThePs4VerticalScaleAndPositions(string key, Language language)
    {
        using JsonDocument doc = GoldenData.Json("choice_oracle.json");
        JsonElement e = doc.RootElement.GetProperty(key);
        JsonElement[] want = e.GetProperty("page_quads").EnumerateArray().ToArray();
        Near(3.1339285, e.GetProperty("page_scale")[1].GetDouble(), 1e-6, "oracle page scale");
        Near(e.GetProperty("page_scale")[1].GetDouble(), GameHost.PageScaleY, 1e-6, "engine page scale");
        Near(e.GetProperty("choice_scale")[1].GetDouble(), GameHost.ChoiceScaleY, 1e-6, "engine choice scale");

        GameHost host = FirstChoiceRig.NewHost(language);
        FirstChoiceRig.RunToChoice(host);
        long n = 4_000_000;
        DisplayList list = FirstChoiceRig.Frame(host, ref n);
        var got = new List<DrawCmd>();
        foreach (DrawCmd c in list.Commands)
        {
            if (c.Texture.Kind == TextureKind.GlyphPage)
            {
                got.Add(c);
            }
        }

        // the page's glyph quads come first (outline, fill per glyph), the choice lines after them
        Assert.True(got.Count >= want.Length, $"{got.Count} glyph quads, the oracle's page has {want.Length}");
        double worstPosition = 0;
        for (int i = 0; i < want.Length; i++)
        {
            DrawCmd c = got[i];
            double pageSize = c.Texture.B == 0 ? 256.0 : 1024.0;
            double texelsV = (c.V2.V - c.V0.V) * pageSize;
            double texelsU = (c.V1.U - c.V0.U) * pageSize;
            double pxPerTexelY = (c.V2.Y - c.V0.Y) / texelsV;
            double pxPerTexelX = (c.V1.X - c.V0.X) / texelsU;
            Near(want[i][0].GetDouble(), c.V0.X, 0.02, $"quad {i} x");
            Near(want[i][1].GetDouble(), c.V0.Y, 0.02, $"quad {i} y");
            Near(want[i][2].GetDouble(), pxPerTexelY, 2e-3, $"quad {i} pixels per texel, y");
            Near(want[i][3].GetDouble(), pxPerTexelX, 2e-3, $"quad {i} pixels per texel, x");
            worstPosition = Math.Max(worstPosition, Math.Abs(want[i][1].GetDouble() - c.V0.Y));
        }

        output.WriteLine($"{key}: {want.Length} page glyph quads equal the oracle at {GameHost.PageScaleY:F7} px per unit; largest y difference {worstPosition:F5} px");
    }
}

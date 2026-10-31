using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tsuki.Data.IO;
using Tsuki.Data.Verification;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Text;
using Tsuki.Tests.Data.Source;
using Tsuki.Tests.Data.Text.Layout;

namespace Tsuki.Tests.Data.Game;

/// <summary>
/// Package K34: the click-wait icon against the PS4 HUD run under emulation (<c>Oracle/gen_click_icon_golden.py</c> ->
/// <c>samples/golden_data/click_icon_oracle.json</c>): the anchor of VA 0xCA690 for the real message <c>$000003</c> in English and Japanese, the quad the
/// story page draws for it (position, size, cell, alpha ramp), the bubble and the F822 != 1 variants, and the eboot's own frame tables and slot
/// registrations. The last test plays the real prologue to a click wait and checks the host draws the mark there.
/// </summary>
public sealed class ClickIconOracleTests(ITestOutputHelper output)
{
    private static readonly (string Key, Language Lang, int Window, int Flags)[] Layouts =
    [
        ("line_en", Language.En, 0, 0), ("page_en", Language.En, 0, 0), ("line_ja", Language.Ja, 0, 0), ("page_ja", Language.Ja, 0, 0),
        ("bubble", Language.En, 5, 1 << 20), ("f822_0", Language.En, 0, 0), ("partial", Language.En, 0, 0), ("noclick", Language.En, 0, 0),
        ("line_zc", Language.Zc, 0, 0), ("bubble_ja", Language.Ja, 5, 1 << 20),                 // package K35: a third language and a non-English bubble
    ];

    private static TextBlock LayoutLine3(Language lang, int window, int flags)
    {
        ReadOnlyMemory<byte> text = SharedSource.Source.TextLine(lang, 3);
        return TextLayout.Layout(text.Span, lang, LayoutData.FontTables, new LayoutParams(Flags: flags, Window: window, Block: 4));
    }

    private static void Near(double ours, double hud, double tolerance, string what) =>
        Assert.True(Math.Abs(ours - hud) <= tolerance, $"{what}: ours {ours}, the emulated HUD {hud}");

    /// <summary>The current message's origin as the HUD computes it: the bubble table for a bubble window, the bottom-window origin (float32 169.333 / 43.1407 at VA 0x1A7154 / 0x1A7158, code 0x54A19) when F822 != 1, else the page origin.</summary>
    private static (double X, double Y) OriginOf(string key, Language lang, int window) =>
        window >= 3 ? MessageWindow.BubbleTextOrigin(window)
        : key == "f822_0" ? (BitConverter.Int32BitsToSingle(0x43295555), BitConverter.Int32BitsToSingle(0x422C901E))
        : lang == Language.En ? (30.0, 35.0) : (87.0, 49.0);

    /// <summary>The stored x (VA 0xDF088C): the anchor plus <c>origin - 87</c> for a bubble (VA 0x54B6A..0x54B80), unchanged for the page and when F822 != 1 (the jump at 0x54A37 skips the adjustment).</summary>
    private static int StoredX(string key, int window, double ox, ClickAnchor anchor) =>
        window >= 3 && key != "f822_0" ? (int)(((float)ox - 87f) + anchor.X) : anchor.X;

    [GameDataFact]
    public void TheAnchorAfterTheLastGlyphEqualsTheEmulatedHudForEveryScenario()
    {
        using JsonDocument doc = GoldenData.Json("click_icon_oracle.json");
        int compared = 0;
        foreach ((string key, Language lang, int window, int flags) in Layouts)
        {
            JsonElement s = doc.RootElement.GetProperty(key);
            TextBlock block = LayoutLine3(lang, window, flags);
            JsonElement[] recs = s.GetProperty("records").EnumerateArray().ToArray();
            Assert.Equal(recs.Length, block.Records.Count(r => !(r.Index == 0 && r.Flags == 0)));
            JsonElement last = recs[^1];
            GlyphRecord ours = block.Records.Last(r => !(r.Index == 0 && r.Flags == 0));
            Near(last.GetProperty("dx").GetDouble(), ours.Dx, 1e-4, key + " last dx");
            Assert.Equal(last.GetProperty("x2").GetInt32() - last.GetProperty("x0").GetInt32(), ours.X2 - ours.X);

            double head = s.GetProperty("head").GetDouble();
            double tail = s.GetProperty("tail").GetDouble();
            ClickAnchor anchor = MessageWindow.ClickIconAnchor(block, head, tail, lang);
            JsonElement f0 = s.GetProperty("frames")[0];
            // the HUD stores float(x_int) after the window shift (origin - 87 for the bubble, 0 for the page) and y_int + the message's y
            (double ox, double oy) = OriginOf(key, lang, window);
            int xInt = StoredX(key, window, ox, anchor);
            Assert.Equal(f0.GetProperty("icon_x").GetDouble(), xInt);
            Near(f0.GetProperty("icon_y").GetDouble(), anchor.Y + (float)oy, 1e-3, key + " icon y");
            bool complete = f0.GetProperty("alpha").GetDouble() > 0 || key == "noclick";
            Assert.Equal(key != "partial", anchor.Complete);
            Assert.Equal(key is not ("partial" or "noclick"), complete && key != "noclick");
            output.WriteLine($"{key}: anchor ({anchor.X}, {anchor.Y}) complete {anchor.Complete}; stored ({xInt}, {anchor.Y + oy}); last glyph dx {ours.Dx:F3} w {ours.X2 - ours.X}");
            compared++;
        }

        Assert.Equal(Layouts.Length, compared);
    }

    [GameDataTheory]
    [InlineData("line_en")]
    [InlineData("page_en")]
    [InlineData("line_ja")]
    [InlineData("page_ja")]
    [InlineData("bubble")]
    [InlineData("f822_0")]
    [InlineData("line_zc")]
    [InlineData("bubble_ja")]
    public void TheQuadOfTheStoryPageEqualsTheEmulatedHudFrameByFrame(string key)
    {
        using JsonDocument doc = GoldenData.Json("click_icon_oracle.json");
        JsonElement s = doc.RootElement.GetProperty(key);
        (string _, Language lang, int window, int flags) = Layouts.First(l => l.Key == key);
        TextBlock block = LayoutLine3(lang, window, flags);
        ClickAnchor anchor = MessageWindow.ClickIconAnchor(block, 768, 768, lang);
        (double ox, double oy) = OriginOf(key, lang, window);
        int xInt = StoredX(key, window, ox, anchor);
        int f822 = s.GetProperty("f822").GetInt32();
        int f799 = s.GetProperty("f799").GetInt32();
        int child = window >= 3 ? GameHost.BubbleIconChild : f822 != 1 || f799 == 1 ? GameHost.PageIconChild : GameHost.LineIconChild;
        (int cells, int cellW, int cellH) = GameHost.ClickIconCells(child);
        int uiSlot = child switch { GameHost.LineIconChild => 2, GameHost.PageIconChild => 3, _ => 4 };
        (int sw, int sh) = SharedSource.Source.UiSheetSize(UiSet.DispParts, lang, child);
        Assert.Equal(cells * cellW, sw);
        Assert.Equal(cellH, sh);

        // the same arithmetic as GameHost.DrawClickIcon, in float32
        float x;
        float y;
        float factor;
        if (window >= 3)
        {
            x = xInt + -2f;
            y = (anchor.Y + (float)oy) + 2f;
            factor = 1f;
        }
        else if (f822 == 1)
        {
            x = ((lang == Language.En ? -57f : 0f) + xInt) + -3.7f;
            y = (anchor.Y + (float)oy) + 4.3f;
            factor = f799 == 1 ? 0.5f : 1f;
        }
        else
        {
            x = xInt + 115f;
            y = (anchor.Y + (float)oy) + 2f;
            factor = 0.5f;
        }

        int frames = 0;
        float alpha = 0f;
        int tick = -1;
        foreach (JsonElement f in s.GetProperty("frames").EnumerateArray())
        {
            alpha = Math.Min(1f, alpha + 0.1f);
            tick = (tick + 1) % (2 * cells);
            int cell = GameHost.ClickIconCell(child, tick / 2);
            Near(alpha, f.GetProperty("alpha").GetDouble(), 1e-6, $"{key} frame {frames} alpha");
            Assert.Equal(tick, f.GetProperty("tick").GetInt32());
            JsonElement d = Assert.Single(f.GetProperty("draw").EnumerateArray());
            Assert.Equal(uiSlot, d.GetProperty("ui_slot").GetInt32());
            JsonElement rect = d.GetProperty("rect");
            Assert.Equal(cell * cellW, rect[0].GetInt32());
            Assert.Equal((cell * cellW) + cellW, rect[2].GetInt32());
            Assert.Equal(cellH, rect[3].GetInt32());
            Near(x, d.GetProperty("x").GetDouble(), 1e-4, $"{key} frame {frames} x");
            Near(y, d.GetProperty("y").GetDouble(), 1e-4, $"{key} frame {frames} y");
            Near(GameHost.ClickIconScale, d.GetProperty("scale")[0].GetDouble(), 1e-7, $"{key} scale");
            Near(alpha * factor, d.GetProperty("colour_alpha").GetDouble(), 1e-6, $"{key} frame {frames} colour alpha");
            JsonElement q = Assert.Single(f.GetProperty("quads").EnumerateArray());
            JsonElement px = q.GetProperty("px");
            // list 0x1E: 3 px per unit in x, 1.3 x 1080 / 448 in y; with F822 != 1 the list is offset by 302.8148 canvas units (text_vertical_scale.md 3), the bottom window
            double listY = f822 == 1 ? 0.0 : BitConverter.Int32BitsToSingle(0x4397684C) * 1080.0 / 448.0;
            Near(x * 3.0, px[0][0].GetDouble(), 1e-3, $"{key} px x0");
            Near((y * GameHost.PageScaleY) + listY, px[0][1].GetDouble(), 1e-2, $"{key} px y0");
            Near((x * 3.0) + (cellW * GameHost.ClickIconScale * 3.0), px[3][0].GetDouble(), 1e-3, $"{key} px x1");
            Near((y * GameHost.PageScaleY) + listY + (cellH * GameHost.ClickIconScale * GameHost.PageScaleY), px[3][1].GetDouble(), 1e-2, $"{key} px y1");
            // the emulated slot is one tile of the next power of two (4096 / 2560 texels wide), so its uv names texels relative to that; ours uses the sheet width
            JsonElement uv = q.GetProperty("uv");
            int tileW = ((sw + 255) / 256) * 256;
            Near(cell * cellW, uv[0][0].GetDouble() * tileW, 1e-3, $"{key} u0 texel");
            Near((cell * cellW) + cellW, uv[3][0].GetDouble() * tileW, 1e-3, $"{key} u1 texel");
            Near(cellH, uv[3][1].GetDouble() * (((sh + 63) / 64) * 64), 1e-3, $"{key} v1 texel");
            frames++;
        }

        Assert.Equal(14, frames);
        output.WriteLine($"{key}: child {child}, {frames} frames, quad at ({x * 3.0:F3}, {y * GameHost.PageScaleY:F3}) px, {cellW * GameHost.ClickIconScale * 3.0:F3} x {cellH * GameHost.ClickIconScale * GameHost.PageScaleY:F3} px, colour alpha factor {factor}");
    }

    [GameDataFact]
    public void TheEmulatedHudDrawsNothingWithoutTheClickBitOrWithAutoModeOrWhileTheTextReveals()
    {
        using JsonDocument doc = GoldenData.Json("click_icon_oracle.json");
        foreach (string key in new[] { "noclick", "partial", "auto" })
        {
            foreach (JsonElement f in doc.RootElement.GetProperty(key).GetProperty("frames").EnumerateArray())
            {
                Assert.Empty(f.GetProperty("draw").EnumerateArray());
                Assert.Empty(f.GetProperty("quads").EnumerateArray());
                Assert.Equal(-1, f.GetProperty("tick").GetInt32());
                if (key == "auto")
                {
                    Assert.True(f.GetProperty("alpha").GetDouble() > 0, "auto mode still raises the alpha");
                }
                else
                {
                    Assert.Equal(0.0, f.GetProperty("alpha").GetDouble());
                }
            }
        }
    }

    [GameDataFact]
    public void TheEbootFrameTablesAreTheCellGridsTheDrawUsesAndTheSlotsLoadTheIconChildren()
    {
        var files = new DirectoryGameFiles(GameFolder.Require());
        byte[] eboot = files.ReadAll("eboot.bin");
        string sha = Convert.ToHexStringLower(SHA256.HashData(eboot));
        if (!string.Equals(sha, KnownBuilds.Verified.EbootSha256, StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine($"not the verified eboot ({sha}): the table addresses are not checked");
            return;
        }

        static int FileOf(int dataVa) => dataVa - 0x600000 + 0x365FA0;
        // the frame tables of 0x53630 / the HUD: slot 2 (line) 0x781760, slot 3 (page) 0x781410, slot 4 (bubble) 0x781230; rows of 4 x i16 (x0, y0, x1, y1)
        foreach ((int va, int child) in new[] { (0x781760, GameHost.LineIconChild), (0x781410, GameHost.PageIconChild), (0x781230, GameHost.BubbleIconChild) })
        {
            (int cells, int cellW, int cellH) = GameHost.ClickIconCells(child);
            for (int row = 0; row < cells; row++)
            {
                int o = FileOf(va) + (8 * row);
                int x0 = BitConverter.ToInt16(eboot, o);
                int y0 = BitConverter.ToInt16(eboot, o + 2);
                int x1 = BitConverter.ToInt16(eboot, o + 4);
                int y1 = BitConverter.ToInt16(eboot, o + 6);
                int cell = GameHost.ClickIconCell(child, row);
                Assert.Equal((cell * cellW, 0, (cell * cellW) + cellW, cellH), (x0, y0, x1, y1));
            }
        }

        // registration table 0x780C90: UI slots 2 / 3 / 4 load disp_parts children 4 / 9 / 53 (records at 0x780FC0 / 0x780FD8 / 0x780FF0: +4 slot, +0x10 child)
        foreach ((int record, int slot, int child) in new[] { (0x780FC0, 2, GameHost.LineIconChild), (0x780FD8, 3, GameHost.PageIconChild), (0x780FF0, 4, GameHost.BubbleIconChild) })
        {
            Assert.Equal(slot, BitConverter.ToInt32(eboot, FileOf(record) + 4));
            Assert.Equal(child, BitConverter.ToInt32(eboot, FileOf(record) + 0x10));
            Assert.Equal(-1, BitConverter.ToInt32(eboot, FileOf(record) + 0x14));
        }

        // the constants of the draw: 0.333 (0x1A7094, 0x1A7170), 0.5 (0x1A7090), 0.1 (0x1A70DC), -57 / -3.7 / 4.3 / 115 / 2 / -2 (0x1A7178 / 0x1A717C / 0x1A7180 / 0x1A7174 / 0x1A7168 / 0x1A716C), 88 (0x2D3048), 3 (0x2D3058), -16 (0x2D3054)
        static float TextF(byte[] e, int va) => BitConverter.ToSingle(e, va + 0x2280);
        Assert.Equal(GameHost.ClickIconScale, TextF(eboot, 0x1A7094));
        Assert.Equal(GameHost.ClickIconScale, TextF(eboot, 0x1A7170));
        Assert.Equal(0.5f, TextF(eboot, 0x1A7090));
        Assert.Equal(0.1f, TextF(eboot, 0x1A70DC));
        Assert.Equal(-57f, TextF(eboot, 0x1A7178));
        Assert.Equal(-3.7f, TextF(eboot, 0x1A717C));
        Assert.Equal(4.3f, TextF(eboot, 0x1A7180));
        Assert.Equal(115f, TextF(eboot, 0x1A7174));
        Assert.Equal(2f, TextF(eboot, 0x1A7168));
        Assert.Equal(-2f, TextF(eboot, 0x1A716C));
        Assert.Equal(MessageWindow.AnchorOriginX, TextF(eboot, 0x2D3048));
        Assert.Equal(3f, TextF(eboot, 0x2D3058));
        Assert.Equal(-16f, TextF(eboot, 0x2D3054));
        Assert.Equal(-1, BitConverter.ToInt32(eboot, FileOf(0x78122C)));
    }

    [GameDataTheory]
    [InlineData(Language.En)]
    [InlineData(Language.Ja)]
    public void TheRealPrologueShowsTheLineMarkAtAWaitInsideAPageAndThePageMarkAtItsEnd(Language language)
    {
        GameHost host = FirstChoiceRig.NewHost(language);
        host.NewGame(host.NewGameSceneId());                           // the prologue movie scene (00_01), then START_SCRIPT -> 00_00: the hospital pages
        for (int guard = 0; guard < 400_000 && host.Phase == GamePhase.Running; guard++)
        {
            host.SetSkipLatch(true);
            host.Step(new InputFrame { Pressed = (guard & 1) == 0 ? PadButtons.Decide : PadButtons.None });
        }

        host.NewGame(host.NewGameSceneId());
        host.SetSkipLatch(false);
        bool sawLine = false;
        bool sawPage = false;
        int clicks = 0;
        long n = 0;
        for (int guard = 0; guard < 60_000 && host.Phase == GamePhase.Running && !(sawLine && sawPage); guard++)
        {
            bool waiting = (host.Wait.Bits & (WaitBits.Shown | WaitBits.Click)) == (WaitBits.Shown | WaitBits.Click);
            if (waiting && host.ClickIconAlpha >= 1f)
            {
                DisplayList list = FirstChoiceRig.Frame(host, ref n);
                ClickIconDraw d = host.LastClickIcon;
                Assert.NotEqual(ClickIconKind.None, d.Kind);
                Assert.Equal(1, host.F(822));
                int expectedChild = host.F(799) == 1 ? GameHost.PageIconChild : GameHost.LineIconChild;
                Assert.Equal(expectedChild, d.DispChild);
                int quads = 0;
                foreach (DrawCmd c in list.Commands)
                {
                    if (c.Texture.Kind == TextureKind.UiTile && c.Texture.A == (int)UiSet.DispParts && c.Texture.C == expectedChild)
                    {
                        quads++;
                        Near(d.X * 3.0, c.V0.X, 1e-3, "quad x");
                        Near(d.Y * GameHost.PageScaleY, c.V0.Y, 1e-3, "quad y");
                    }
                }

                Assert.Equal(1, quads);
                // the position rule of the story page from the host's own anchor and the current message's origin
                PageMessage m = host.CurrentMessage!;
                ClickAnchor a = MessageWindow.ClickIconAnchor(m.Block, host.Wait.Head, host.Wait.Tail, language);
                Assert.True(a.Complete);
                Assert.Equal(a, host.ClickIconAnchor);
                float originY = host.ClickIconStored.Y - a.Y;
                Near(((language == Language.En ? -57f : 0f) + host.ClickIconStored.X) + -3.7f, d.X, 1e-4, "x rule");
                Near((a.Y + originY) + 4.3f, d.Y, 1e-4, "y rule");
                Near(host.F(799) == 1 ? 0.5 : 1.0, d.Alpha, 1e-6, "alpha factor");
                if (!sawLine && host.F(799) == 0)
                {
                    sawLine = true;
                    output.WriteLine($"line mark at frame {host.Frame}: message {host.F(823)} of the page, anchor ({a.X}, {a.Y}), stored ({host.ClickIconStored.X}, {host.ClickIconStored.Y}), quad at ({d.X * 3.0:F2}, {d.Y * GameHost.PageScaleY:F2}) px, cell {d.Frame}");
                }

                if (!sawPage && host.F(799) == 1)
                {
                    sawPage = true;
                    output.WriteLine($"page mark at frame {host.Frame}: message {host.F(823)} of the page, anchor ({a.X}, {a.Y}), stored ({host.ClickIconStored.X}, {host.ClickIconStored.Y}), quad at ({d.X * 3.0:F2}, {d.Y * GameHost.PageScaleY:F2}) px, cell {d.Frame}, alpha {d.Alpha}");
                }

                host.Step(new InputFrame { Pressed = PadButtons.Decide, Held = PadButtons.Decide });
                clicks++;
                Assert.Equal(0f, host.ClickIconAlpha);
            }
            else
            {
                host.Step(default);
            }
        }

        Assert.True(sawLine, $"no wait inside a page was found ({clicks} clicks)");
        Assert.True(sawPage, $"no page end was found ({clicks} clicks)");
    }
}

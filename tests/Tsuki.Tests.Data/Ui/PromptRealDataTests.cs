using Tsuki.Engine.Contracts;
using Tsuki.Tests.Data.Source;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Data.Ui;

/// <summary>
/// Package K9 on the user's own data: the key caps, Xbox glyphs and the controls page replacement against the real guide sheet, the real controls pages and the
/// real glyph pages in all four languages.
/// </summary>
public sealed class PromptRealDataTests
{
    private static readonly Language[] Languages = [Language.Ja, Language.En, Language.Zc, Language.Zt];

    private static readonly string[] Scenes = ["guides", "save", "load", "config-controls", "config-controls-cursor", "config-display", "backlog", "confirm", "title-press", "gallery-cg-view", "flowchart-game"];

    public static TheoryData<string, Language, PromptSetting> Cases()
    {
        var data = new TheoryData<string, Language, PromptSetting>();
        foreach (string scene in Scenes)
        {
            foreach (Language l in Languages)
            {
                foreach (PromptSetting s in new[] { PromptSetting.Keyboard, PromptSetting.Xbox })
                {
                    data.Add(scene, l, s);
                }
            }
        }

        return data;
    }

    private static DrawCmd[] DrawList(UiRoot root)
    {
        var list = new DisplayList(2048);
        list.Reset(root.Context.Frame);
        root.Draw(list);
        list.Seal();
        return list.Commands.ToArray();
    }

    [GameDataTheory]
    [MemberData(nameof(Cases))]
    public void EveryScreenDrawsItsPromptsOnTheRealSheetsAndGlyphPages(string scene, Language lang, PromptSetting setting)
    {
        var services = new PreviewServices(lang) { PromptSetting = setting, DecideIsCross = true };
        UiRoot root = UiPreview.Open(scene, SharedSource.Source, services);
        DrawCmd[] cmds = DrawList(root);
        Assert.Empty(root.Context.Assets.Missing);
        Assert.Empty(root.Context.Assets.MissingPictures);
        Assert.Equal(setting == PromptSetting.Keyboard ? PromptStyle.Keyboard : PromptStyle.Xbox, root.Painter.PromptStyle);
        // the guide icon sheet is not drawn at all: the caps and glyphs replace it
        Assert.DoesNotContain(cmds, c => c.Texture is { Kind: TextureKind.UiTile } t && t.A == (int)UiSet.DispParts && t.C == 16);
        if (scene is "guides" or "save" or "load" or "config-controls" or "backlog")
        {
            // the English glyph pages carry the cap labels whatever the UI language is
            Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.GlyphPage } t && t.A == (int)Language.En);
            foreach (DrawCmd c in cmds.Where(c => c.Texture.Kind == TextureKind.GlyphPage && c.Texture.A == (int)Language.En).Take(40))
            {
                Assert.Equal(1024, SharedSource.Source.FontPage(Language.En, c.Texture.B).Width);
            }
        }

        if (scene == "title-press" && setting == PromptSetting.Keyboard)
        {
            Assert.DoesNotContain(cmds, c => c.Texture is { Kind: TextureKind.UiTile } t && t.A == (int)UiSet.TitleParts && t.C == 14);
        }
    }

    [GameDataFact]
    public void TheGuideSheetHasTheIconsAndWordsThePromptTablesAssume()
    {
        var tables = new UiAssets(SharedSource.Source, Language.En).Tables;
        IReadOnlyList<SheetCell> icons = tables.GuideIconCells;
        Assert.Equal(23, icons.Count);
        Assert.All(new[] { PromptBindings.CellEast, PromptBindings.CellWest, PromptBindings.CellNorth, PromptBindings.CellSouth }, c =>
        {
            Assert.InRange(icons[c].Width, 26, 40);                            // the four round face buttons
            Assert.InRange(icons[c].Height, 26, 40);
        });
        Assert.All(new[] { 16, 17, 19, 20, 22 }, c => Assert.True(icons[c].Width > icons[c].Height, $"icon {c} is a wide label: {icons[c]}"));
        Assert.All(Enumerable.Range(0, 7), c => Assert.InRange(Math.Abs(icons[c].Width - icons[c].Height), 0, 6));       // the d-pads are square
        foreach (Language l in Languages)
        {
            Assert.Equal(27, new UiAssets(SharedSource.Source, l).Tables.GuideWordCells(l).Count);
        }
    }

    // ---- the controls page ------------------------------------------------------------------------------------------------------------------------

    private static (int W, int H, byte[] Rgba) Compose(Picture p)
    {
        var rgba = new byte[p.Width * p.Height * 4];
        foreach (PictureTile t in p.Tiles)
        {
            if (t.Flags == TileFlags.Empty)
            {
                continue;
            }

            ReadOnlySpan<byte> src = t.Rgba.Span;
            for (int y = 0; y < t.Source.Height; y++)
            {
                for (int x = 0; x < t.Source.Width; x++)
                {
                    int s = (((t.Source.Y + y) * t.StoredWidth) + t.Source.X + x) * 4;
                    int dx = t.X + x;
                    int dy = t.Y + y;
                    if ((uint)dx < (uint)p.Width && (uint)dy < (uint)p.Height)
                    {
                        src.Slice(s, 4).CopyTo(rgba.AsSpan(((dy * p.Width) + dx) * 4, 4));
                    }
                }
            }
        }

        return (p.Width, p.Height, rgba);
    }

    private static bool Bright(byte[] rgba, int w, int x, int y)
    {
        int i = ((y * w) + x) * 4;
        return rgba[i + 3] > 200 && rgba[i] > 200 && rgba[i + 1] > 200 && rgba[i + 2] > 200;
    }

    [GameDataTheory]
    [InlineData(Language.Ja, 16)]
    [InlineData(Language.En, 16)]
    [InlineData(Language.Zc, 17)]
    [InlineData(Language.Zt, 17)]
    [InlineData(Language.En, 17)]
    public void TheIconColumnOfTheControlsPageSitsInTheBoxesTheCapsReplace(Language lang, int child)
    {
        (int w, int h, byte[] px) = Compose(SharedSource.Source.UiSheet(UiSet.ConfParts, lang, child));
        Assert.Equal((1416, 1528), (w, h));
        float[] centres = UiPrompts.ControlRowCentres;
        int colX = (int)UiPrompts.ControlColumnX - 16;
        for (int y = (int)UiPrompts.ControlRowsTop; y < (int)UiPrompts.ControlRowsBottom; y++)
        {
            bool inBox = centres.Any(c => y >= MathF.Floor(c) - 24f && y < MathF.Floor(c) + 24f);
            bool icon = false;
            for (int x = colX; x < colX + 140; x++)
            {
                icon |= Bright(px, w, x, y);
            }

            if (!inBox)
            {
                Assert.False(icon, $"{lang} sheet {child}: an icon pixel at page row {y} is outside every icon box");
            }
        }

        // every row has its icon, centred on the measured centre (within 3 rows)
        foreach (float c in centres)
        {
            var rows = new List<int>();
            for (int y = (int)c - 30; y <= (int)c + 30; y++)
            {
                for (int x = colX; x < colX + 140; x++)
                {
                    if (Bright(px, w, x, y))
                    {
                        rows.Add(y);
                        break;
                    }
                }
            }

            Assert.NotEmpty(rows);
            Assert.InRange((rows.Min() + rows.Max()) / 2f, c - 3f, c + 3f);
        }

        // the filler slices (rows c-27 .. c-21 of the first row below it) are flat band: no icon, and no row line in them
        for (int i = 1; i < centres.Length; i++)
        {
            int y0 = (int)MathF.Floor(centres[i]) - 27;
            for (int y = y0; y < y0 + 6; y++)
            {
                for (int x = colX; x < colX + 140; x++)
                {
                    Assert.False(Bright(px, w, x, y), $"{lang} sheet {child}: slice row {y} holds a bright pixel at x {x}");
                }
            }
        }
    }

    // ---- the words -------------------------------------------------------------------------------------------------------------------------------

    private static int GlyphQuads(Action<UiPainter> draw, Language lang)
    {
        var painter = new UiPainter(new UiAssets(SharedSource.Source, lang));
        var list = new DisplayList(256);
        list.Reset(0);
        painter.Begin(list);
        draw(painter);
        list.Seal();
        return list.Commands.ToArray().Count(c => c.Texture.Kind == TextureKind.GlyphPage);
    }

    [GameDataFact]
    public void EveryCapLabelAndEveryTranslatedWordHasItsGlyphsInTheGlyphPages()
    {
        string[] caps = [.. PromptBindings.Keys.SelectMany(k => k.Keys), "Drag", "LB", "RB", "LT", "RT", "LS", "RS", "Menu", "A", "B", "X", "Y", "Enter", "Space", "WASD", "A D", "W S"];      // the last three: the guide's cursor icons (package K14)
        foreach (string label in caps.Distinct())
        {
            int expected = label.Count(ch => !char.IsWhiteSpace(ch));
            foreach (Language l in Languages)
            {
                // the caps always use the English pages, so the UI language must not matter
                Assert.InRange(GlyphQuads(p => UiPrompts.DrawPill(p, label, new RectF(0, 0, 100, 34), false, false, 1f), l), expected, label.Length);      // a space may be a quad of its own
            }
        }

        foreach (Language l in Languages)
        {
            string heading = UiPrompts.SettingHeading(l);
            Assert.InRange(GlyphQuads(p => UiPrompts.DrawText(p, heading, 0, 20, 1f), l), heading.Count(ch => !char.IsWhiteSpace(ch)), heading.Length);      // a space may be a quad of its own
            for (int i = 0; i < 4; i++)
            {
                string label = UiPrompts.SettingLabel(l, i);
                bool translated = i < 2 && l != Language.En;
                Assert.InRange(GlyphQuads(p => UiPrompts.DrawPill(p, label, new RectF(0, 0, 120, 34), false, false, 1f, translated), l), label.Count(ch => !char.IsWhiteSpace(ch)), label.Length);
            }

            // package K19: the Display Mode and Window Size rows of the System tab (the headings and the mode words in the language's own pages, the sizes in the English pages)
            foreach (string word in new[] { UiPrompts.DisplayHeading(l), UiPrompts.SizeHeading(l) })
            {
                Assert.InRange(GlyphQuads(p => UiPrompts.DrawText(p, word, 0, 20, 1f), l), word.Count(ch => !char.IsWhiteSpace(ch)), word.Length);
            }

            for (int i = 0; i < 2; i++)
            {
                string label = UiPrompts.DisplayLabel(l, i);
                Assert.InRange(GlyphQuads(p => UiPrompts.DrawPill(p, label, new RectF(0, 0, UiPrompts.DisplayPillWidth(i), 34), false, false, 1f, l != Language.En), l), label.Count(ch => !char.IsWhiteSpace(ch)), label.Length);
            }

            foreach ((int w, int h) in Tsuki.Platform.WindowFit.Presets)
            {
                string label = UiPrompts.SizeLabel(w, h);
                Assert.Equal(label.Length, GlyphQuads(p => UiPrompts.DrawPill(p, label, new RectF(0, 0, UiPrompts.SizePillWidth, 34), false, false, 1f), l));
            }

            // package K21: the headings of the clipboard, opacity and background-mute rows (a ^ breaks the two-line English ones), the Off / On words, the percent label;
            // package K23: the Text Hooker Output and Text WebSocket headings; package K24: the Screenshot Notice heading
            foreach (string word in new[] { UiPrompts.CopyTextHeading(l), UiPrompts.OpacityHeading(l), UiPrompts.MuteHeading(l), UiPrompts.TextHookerHeading(l), UiPrompts.WebSocketHeading(l), UiPrompts.ScreenshotNoticeHeading(l) })
            {
                int ink = word.Count(ch => !char.IsWhiteSpace(ch) && ch != '^');
                Assert.InRange(GlyphQuads(p => UiPrompts.DrawText(p, word, 0, 20, 1f), l), ink, word.Length);
            }

            for (int i = 0; i < 2; i++)
            {
                string label = UiPrompts.SwitchLabel(l, i);
                Assert.InRange(GlyphQuads(p => UiPrompts.DrawPill(p, label, new RectF(0, 0, UiPrompts.SwitchPillWidth, 34), false, false, 1f, l != Language.En), l), label.Length, label.Length);
            }

            Assert.Equal(4, GlyphQuads(p => UiPrompts.DrawText(p, UiPrompts.PercentLabel(100), 0, 20, 1f, translated: false), l));
            // package K24: the two screenshot toasts are short sentences without a file name or an error, drawn by the language's own pages at the toast's scale
            foreach (PortString id in new[] { PortString.ScreenshotSaved, PortString.ScreenshotFailed })
            {
                string toast = PortText.Get(id, l);
                Assert.DoesNotContain("{0}", toast, StringComparison.Ordinal);
                Assert.InRange(GlyphQuads(p => UiPrompts.DrawText(p, toast, 0, 20, 1f, 0.32), l), toast.Count(ch => !char.IsWhiteSpace(ch)), toast.Length);      // a space may be a quad of its own
            }
            // package K23: the WebSocket toasts ("ws://127.0.0.1:6677" has three colons and two slashes, which may be quads or not)
            string listening = PortText.Format(PortString.TextWebSocketListening, l, 6677);
            Assert.InRange(GlyphQuads(p => UiPrompts.DrawText(p, listening, 0, 20, 1f), l), listening.Count(ch => !char.IsWhiteSpace(ch)) - 6, listening.Length);
            string failed = PortText.Format(PortString.TextWebSocketFailed, l, 6677, "in use");
            Assert.InRange(GlyphQuads(p => UiPrompts.DrawText(p, failed, 0, 20, 1f), l), failed.Count(ch => !char.IsWhiteSpace(ch)) - 4, failed.Length);
        }

        Assert.True(GlyphQuads(p => UiPrompts.DrawPressAnyKey(p, 1f), Language.En) >= "PRESSANYKEY".Length);      // the glyphs, with their outline copies
    }
}

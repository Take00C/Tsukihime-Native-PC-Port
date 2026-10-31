using Tsuki.Data.Containers;
using Tsuki.Data.Images;
using Tsuki.Engine.Contracts;
using Tsuki.Tests.Data.Source;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Data.Ui;

/// <summary>
/// The menus on the user's own data (package K5a): every screen builds its draw list from the real UI sheets and eboot tables in every language, the sheets
/// are the ones docs/formats/archives_ui_movies.md lists, and the tables the screens read agree with knife's layout numbers (docs/research/knife_tables.md).
/// </summary>
public sealed class UiRealDataTests
{
    private static readonly Language[] Languages = [Language.Ja, Language.En, Language.Zc, Language.Zt];

    /// <summary>parts.mrg pictures for the boot notice, as the screenshot tool serves them.</summary>
    private sealed class Parts : IUiPictureSource
    {
        public Picture? Parts_(int index)
        {
            MrgArchive a = SharedSource.Source.Archive("parts");
            return (uint)index < (uint)a.Count ? ImageMzp.Open(a.Memory(index)).ToPicture(1) : null;
        }

        Picture? IUiPictureSource.Parts(int index) => Parts_(index);
    }

    private static (UiRoot Root, PreviewServices Services) Open(string screen, Language lang)
    {
        var services = new PreviewServices(lang);
        UiRoot root = UiPreview.Open(screen, SharedSource.Source, services, -1, new Parts());
        return (root, services);
    }

    public static TheoryData<string, Language> AllScreens()
    {
        var data = new TheoryData<string, Language>();
        foreach (string s in UiPreview.Screens.Where(s => s != "fills"))
        {
            foreach (Language l in Languages)
            {
                data.Add(s, l);
            }
        }

        return data;
    }

    [GameDataTheory]
    [MemberData(nameof(AllScreens))]
    public void EveryScreenBuildsItsDrawListFromTheRealSheets(string screen, Language lang)
    {
        (UiRoot root, _) = Open(screen, lang);
        DrawCmd[] cmds = DrawList(root);
        Assert.InRange(cmds.Length, 3, 5000);
        Assert.Empty(root.Context.Assets.Missing);
        Assert.Empty(root.Context.Assets.MissingPictures);
        // the blur of the scene behind the system menu and the backlog (package K16) reads capture surfaces; every other quad names game data
        Assert.All(cmds.Where(c => c.Fx.Pass == QuadPass.Blur), c => Assert.Equal(TextureKind.Capture, c.Texture.Kind));
        foreach (DrawCmd c in cmds.Where(c => c.Kind == DrawKind.TexturedQuad && c.Fx.Pass != QuadPass.Blur))
        {
            // the title's EXIT cell (UiComposed, package K33) is composed from the player's sheets at run time
            Assert.True(c.Texture.Kind is TextureKind.UiTile or TextureKind.GlyphPage or TextureKind.PictureTile or TextureKind.Thumbnail or TextureKind.UiComposed, $"{screen} {lang}: {c.Texture}");
            Assert.True(float.IsFinite(c.V0.X) && float.IsFinite(c.V3.Y) && c.V0.U is >= -0.001f and <= 1.001f && c.V3.V is >= -0.001f and <= 1.001f);
        }
    }

    [GameDataTheory]
    [MemberData(nameof(AllScreens))]
    public void EveryTextureTheDrawListNamesExistsInTheGameData(string screen, Language lang)
    {
        (UiRoot root, _) = Open(screen, lang);
        var sheets = new Dictionary<(UiSet, Language, int), Picture>();
        var pages = new HashSet<(Language, int)>();
        var pictureTiles = new HashSet<(int, int, int)>();
        int pictures = 0;
        foreach (DrawCmd c in DrawList(root).Where(c => c.Kind == DrawKind.TexturedQuad))
        {
            TextureRef t = c.Texture;
            switch (t.Kind)
            {
                case TextureKind.UiTile:
                {
                    var key = ((UiSet)t.A, (Language)t.B, t.C);
                    if (!sheets.TryGetValue(key, out Picture? pic))
                    {
                        pic = SharedSource.Source.UiSheet(key.Item1, key.Item2, key.C);
                        sheets[key] = pic;
                    }

                    PictureTile tile = Assert.Single(pic.Tiles, x => x.Index == t.D);
                    Assert.NotEqual(TileFlags.Empty, tile.Flags);
                    break;
                }

                case TextureKind.PictureTile when t.A != UiPictureArchive.Parts:
                    if (pictureTiles.Add((t.A, t.B, t.C)))
                    {
                        Picture pic = SharedSource.Source.Picture(t.A, t.B);
                        Assert.Contains(pic.Tiles, x => x.Index == t.C);
                        pictures++;
                    }

                    break;
                case TextureKind.GlyphPage:
                    Assert.InRange(t.B, 0, 64);
                    pages.Add(((Language)t.A, t.B));
                    break;
                case TextureKind.UiComposed:
                {
                    // composed from the sheets of the key's own language (package K33); 296 x 72 like the item cells it is cut from
                    UiComposedPicture? pic = root.Context.Assets.Composed(t.A, (Language)t.B);
                    Assert.NotNull(pic);
                    Assert.Equal((296, 72), (pic.Width, pic.Height));
                    Assert.Equal(pic.Width * pic.Height * 4, pic.Rgba.Length);
                    break;
                }
            }
        }

        Assert.True(sheets.Count + pictures > 0);
        foreach ((Language l, int page) in pages)
        {
            Assert.Equal(1024, SharedSource.Source.FontPage(l, page).Width);
        }
    }

    private static DrawCmd[] DrawList(UiRoot root)
    {
        var list = new DisplayList(2048);
        list.Reset(root.Context.Frame);
        root.Draw(list);
        list.Seal();
        return list.Commands.ToArray();
    }

    // sizes of the sheets the screens draw (docs/formats/archives_ui_movies.md 5.2, 5.3; samples/ui_misc/ui_parts.csv); language-dependent ones are marked
    [GameDataTheory]
    [InlineData(Language.En)]
    [InlineData(Language.Ja)]
    public void TheSheetsHaveTheSizesTheLayoutsAssume(Language lang)
    {
        var assets = new UiAssets(SharedSource.Source, lang);
        void Is(UiSheet s, int w, int h)
        {
            SheetGeometry? g = assets.Geometry(s);
            Assert.NotNull(g);
            Assert.Equal((w, h), (g.Width, g.Height));
        }

        Is(UiSheets.TitleMenu, 968, 472);
        Is(UiSheets.TitleLogo, lang == Language.En ? 1850 : 560, lang == Language.En ? 440 : 648);
        Is(UiSheets.TitleUnderline, 360, 48);
        Is(UiSheets.TitleCopyright, 184, 64);
        Is(UiSheets.TitleGlow, 488, 256);
        Is(UiSheets.TitleBg0, 2670, 1266);
        Is(UiSheets.TitleStill(1), 1920, 1080);
        Is(UiSheets.TitleStill(4), 1920, 1080);
        Is(UiSheets.PressAnyButton, 472, 88);
        Is(UiSheets.TitleShard(0), 33, 44);
        Is(UiSheets.TitleShard(1), 32, 55);
        Is(UiSheets.TitleShard(2), 29, 45);
        Is(UiSheets.MenuButtons, 752, 552);
        Is(UiSheets.MenuWindow, 312, 1080);
        Is(UiSheets.SceneBand, 1168, 80);
        Is(UiSheets.TitleBand, 1920, 392);
        Is(UiSheets.CursorLight, 528, 240);
        Is(UiSheets.CursorLine, 224, 56);
        Is(UiSheets.Underline, 1920, 16);
        Is(UiSheets.Spark, 82, 82);
        Is(UiSheets.Backdrop, 1920, 1080);
        Is(UiSheets.StripLow(0), 1920, 396);
        Is(UiSheets.StripLow(3), 1920, 165);
        Is(UiSheets.StripHigh(1), 1700, 236);
        Is(UiSheets.StripHigh(3), 1497, 80);
        Is(UiSheets.SaveAuto, 192, 32);
        Is(UiSheets.SaveDateDigits, 376, 64);
        Is(UiSheets.SaveInfoArc, 472, 712);
        Is(UiSheets.SaveSceneTag, 64, 24);
        Is(UiSheets.SaveTimeDigits, 376, 64);
        Is(UiSheets.SaveFrame, 1024, 640);
        Is(UiSheets.SavePanel, 1560, 1080);
        Is(UiSheets.ConfTabs, 608, 272);
        Is(UiSheets.ConfDefault, 352, 312);
        Is(UiSheets.ConfPanel, 1560, 1080);
        Is(UiSheets.ConfHeader, 1416, 72);
        Is(UiSheets.ConfKnob, 936, 504);
        Is(UiSheets.ConfStep, 168, 137);
        Is(UiSheets.ConfSoundPage, 1432, 328);
        Is(UiSheets.ConfVoicePage, 1432, 2872);
        Is(UiSheets.ConfFaces, 688, 1008);
        Is(UiSheets.ConfControlsPad, 1416, 1528);
        Is(UiSheets.ConfDigits, 320, 32);
        Is(UiSheets.ConfLanguage, 760, 344);
        Is(UiSheets.ConfMute, 504, 128);
        Is(UiSheets.ConfTest, 408, 120);
        Is(UiSheets.Banner, 432, 264);
        Is(UiSheets.ScrollBar, 16, 888);
        Is(UiSheets.ScrollSlider, 208, 336);
        Is(UiSheets.GuideIcons, 384, 256);
        Is(UiSheets.GuideWords, 512, 256);
        Is(UiSheets.DialogButtons, 448, 264);
        Is(UiSheets.DialogWindow, 824, 416);
        Is(UiSheets.DialogBackdrop, 1920, 1080);
        Is(UiSheets.ModeBar, 1920, 96);
        Is(UiSheets.ModeButtons, 1864, 296);
        if (lang == Language.En)
        {
            Is(UiSheets.PhaseTitles, 3664, 992);
            Is(UiSheets.SaveTitles, 2176, 496);
            Is(UiSheets.ConfRadio, 1336, 696);
            Is(UiSheets.ConfNames, 504, 624);
        }
    }

    [GameDataFact]
    public void EveryCellTableLiesInsideItsSheetInEveryLanguage()
    {
        foreach (Language lang in Languages)
        {
            var assets = new UiAssets(SharedSource.Source, lang);
            IUiTables t = assets.Tables;
            void Inside(IEnumerable<SheetCell> cells, UiSheet sheet, string what)
            {
                SheetGeometry g = assets.Geometry(sheet)!;
                foreach ((SheetCell c, int i) in cells.Select((c, i) => (c, i)))
                {
                    Assert.True(c.X0 >= 0 && c.Y0 >= 0 && c.X1 <= g.Width && c.Y1 <= g.Height && c.Width > 0 && c.Height > 0, $"{what} {lang} cell {i}: {c} in {g.Width} x {g.Height}");
                }
            }

            Inside(t.PhaseTitleCells(lang), UiSheets.PhaseTitles, "phase titles");
            Inside(t.SaveTitleCells(lang), UiSheets.SaveTitles, "save titles");
            Inside(t.GuideIconCells.Take(23), UiSheets.GuideIcons, "guide icons");
            Inside(t.GuideWordCells(lang), UiSheets.GuideWords, "guide words");
            Inside(t.VoiceFaceCells, UiSheets.ConfFaces, "voice faces");
            Inside(t.VoiceNameCells(lang), UiSheets.ConfNames, "voice names");
            Inside(t.BannerCells, UiSheets.Banner, "banners");
        }
    }

    [GameDataFact]
    public void TheTablesGiveTheShapesTheScreensRely()
    {
        IUiTables t = new UiAssets(SharedSource.Source, Language.En).Tables;
        Assert.Equal(38, t.SaveTitleCells(Language.En).Count);
        Assert.Equal(38, t.PhaseTitleCells(Language.Ja).Count);
        Assert.True(t.GuideIconCells.Count >= 23);
        Assert.Equal(27, t.GuideWordCells(Language.En).Count);
        Assert.Equal(20, t.VoiceFaceCells.Count);
        Assert.Equal(20, t.VoiceNameCells(Language.En).Count);
        Assert.Equal(55, t.DialogMessages.Count);
        Assert.Equal(19, t.VoiceRowWords.Count);
        Assert.Equal(14, t.TestSeNames.Count);
        Assert.Equal([1, 2, 3, 3, 2, 4], t.TitleBackgroundTable);
        Assert.Equal(2, t.ConfigPages.Count);
        Assert.Equal(16, t.ConfigPages[0].Rows.Count);
        Assert.Equal(26, t.ConfigPages[1].Rows.Count);
        Assert.Equal(10, t.BannerKeys.Count);
    }

    [GameDataFact]
    public void TheGuideSequencesPointAtRealCellsOfTheIconAndWordSheets()
    {
        // the sequences are inline code on the PS4; this checks every pair the screens use resolves, through the screens' own draw of each guide
        var services = new PreviewServices(Language.En);
        UiRoot root = new(SharedSource.Source, services);
        for (int g = 0; g < 12; g++)
        {
            var list = new DisplayList(64);
            list.Reset(0);
            root.Painter.Begin(list);
            UiWidgets.Guide(root.Painter, g);
            Assert.True(list.Count >= 5, $"guide {g}");
            Assert.All(list.Commands.ToArray().Skip(1), c => Assert.Equal(TextureKind.UiTile, c.Texture.Kind));
        }
    }

    [GameDataFact]
    public void ConfigPageControlsMatchKnifesCoordinatesOfTheSystemAndSoundTabs()
    {
        // knife conf_layout_at (640 x 448 units; the radio rows carry +6.637 there): checks the eboot pages give the same controls
        IReadOnlyList<UiConfigPage> pages = new UiAssets(SharedSource.Source, Language.En).Tables.ConfigPages;
        (int Row, int Kind, float X, float Y)[] system =
        [
            (0, 3, 245.0f, 100.8f), (0, 4, 120.666664f, 56.000004f), (0, 5, 452.0f, 100.8f), (0, 2, 475.0f, 94.162964f),
            (1, 3, 245.0f, 175.05185f), (1, 4, 120.666664f, 130.25185f), (1, 2, 475.0f, 168.41481f),
            (2, 3, 245.0f, 246.4f), (2, 4, 120.666664f, 201.59999f), (3, 3, 245.0f, 306.54816f), (3, 4, 120.666664f, 261.74817f),
            (4, 2, 290.33334f, 360.05927f), (12, 10, 243.66667f, 616.4148f), (15, 10, 243.66667f, 677.3926f),
        ];
        foreach ((int row, int kind, float x, float y) in system)
        {
            UiConfigControl c = pages[0].Rows[row].Controls.First(k => k.Kind == kind && Math.Abs(k.X - x) < 0.01f && Math.Abs(k.Y - y) < 0.01f);
            Assert.Equal(kind, c.Kind);
        }

        // the radio pairs: knife's first button of pair 0 at y 364.62222 = the table's 357.99 + 6.637
        UiConfigControl radio = pages[0].Rows[4].Controls.First(k => k.Kind == 6);
        Assert.Equal(364.62222f, radio.Y + 6.637f, 0.01f);
        (int Row, int Kind, float X, float Y)[] sound =
        [
            (0, 3, 245.33333f, 104.94815f), (0, 4, 120.99999f, 60.14815f), (1, 1, 560.6667f, 128.59259f), (2, 2, 476.66666f, 155.55556f),
        ];
        foreach ((int row, int kind, float x, float y) in sound)
        {
            Assert.Contains(pages[1].Rows[row].Controls, k => k.Kind == kind && Math.Abs(k.X - x) < 0.01f && Math.Abs(k.Y - y) < 0.01f);
        }

        Assert.All(pages[1].Rows.Skip(7), r => Assert.Equal(-9999f, r.Controls[0].X));
    }

    [GameDataFact]
    public void EveryDialogHasAMessageInEveryLanguage()
    {
        foreach (Language lang in Languages)
        {
            var services = new PreviewServices(lang);
            UiRoot root = new(SharedSource.Source, services);
            foreach (ConfirmKind kind in Enum.GetValues<ConfirmKind>())
            {
                var dialog = new ConfirmDialog(kind, _ => { });
                int id = dialog.MessageId(root.Context.Assets);
                Assert.True(id > 0, $"{kind}");
                Assert.False(string.IsNullOrWhiteSpace(root.Context.Message(id)), $"{kind} {lang}: ui_text {id}");
            }

            foreach (int knife in new[] { 183, 184, 188, 189, 190, 193 })
            {
                Assert.False(string.IsNullOrWhiteSpace(root.Context.Sysmes(knife)), $"sysmes {knife} {lang}");
            }

            Assert.False(string.IsNullOrWhiteSpace(root.Context.Message(217)), "Deleted");
        }
    }

    [GameDataFact]
    public void TheSceneTitleTextExistsForEveryScene()
    {
        var assets = new UiAssets(SharedSource.Source, Language.En);
        int shown = 0;
        for (int f22 = 0; f22 < 100; f22++)
        {
            if (!string.IsNullOrWhiteSpace(assets.Text(396 + f22)))
            {
                shown++;
            }
        }

        Assert.True(shown >= 60, $"{shown} scene titles");
    }

    [GameDataFact]
    public void TheCautionPicturesAreFoundForEveryLanguage()
    {
        var assets = new UiAssets(SharedSource.Source, Language.En);
        foreach (string name in new[]
        {
            "caution_ja.mzp", "caution_en.mzp", "caution_zc.mzp", "caution_zt.mzp", "caution1.mzp", "caution1_en.mzp", "caution1_zc.mzp", "caution1_zt.mzp",
            "caution2.mzp", "caution2_en.mzp", "caution2_zc.mzp", "caution2_zt.mzp",
        })
        {
            (int Archive, int Entry)? f = assets.Tables.FindPicture(name);
            Assert.NotNull(f);
            SheetGeometry? g = assets.Geometry(new UiPicture(f.Value.Archive, f.Value.Entry));
            Assert.NotNull(g);
            Assert.Equal((1920, 1080), (g.Width, g.Height));
        }

        Assert.Null(assets.Tables.FindPicture("no_such_picture.mzp"));
    }

    [GameDataFact]
    public void TheBootNoticeHasATextPicturePerLanguageInThePartsArchive()
    {
        foreach (int index in new[] { 8, 9, 10, 11 })
        {
            Picture? p = new Parts().Parts_(index);
            Assert.NotNull(p);
            Assert.Equal((1920, 1080), (p.Width, p.Height));
        }
    }

    [GameDataFact]
    public void TextDrawsInEveryLanguageThroughTheGlyphPages()
    {
        foreach (Language lang in Languages)
        {
            var assets = new UiAssets(SharedSource.Source, lang);
            var text = new UiText(assets);
            var painter = new UiPainter(assets);
            var list = new DisplayList(256);
            list.Reset(0);
            painter.Begin(list);
            string message = assets.Text(UiAssets.Sysmes(179));
            int n = text.Draw(painter, message, TextStyle.Slot(0.6, 0.7) with { Width = 640 }, 20f, 100f);
            Assert.True(n >= 6, $"{lang}: {n} glyph quads for '{message}'");
            Assert.All(list.Commands.ToArray(), c => Assert.Equal(TextureKind.GlyphPage, c.Texture.Kind));
            Assert.True(text.Width(message, TextStyle.Slot(0.6, 0.7)) > 20f);
        }
    }

    [GameDataFact]
    public void TheEnglishSheetsAreWiderThanTheJapaneseOnesWhereTheLayoutSwitches()
    {
        var en = new UiAssets(SharedSource.Source, Language.En);
        var ja = new UiAssets(SharedSource.Source, Language.Ja);
        Assert.True(en.Geometry(UiSheets.ConfRadio)!.Width > ja.Geometry(UiSheets.ConfRadio)!.Width);
        Assert.True(en.Geometry(UiSheets.ConfToggle)!.Width > ja.Geometry(UiSheets.ConfToggle)!.Width);
        Assert.True(en.Geometry(UiSheets.TitleLogo)!.Width > ja.Geometry(UiSheets.TitleLogo)!.Width);
        Assert.True(en.Geometry(UiSheets.PhaseTitles)!.Width > ja.Geometry(UiSheets.PhaseTitles)!.Width);
    }

    [GameDataFact]
    public void TheConfigScreenLaysOutEveryTabOnTheRealPagesInEveryLanguage()
    {
        foreach (Language lang in Languages)
        {
            var services = new PreviewServices(lang);
            foreach (int word in new UiAssets(SharedSource.Source, lang).Tables.VoiceRowWords)
            {
                services.SetGlobal(word, 7);
            }

            UiRoot root = new(SharedSource.Source, services);
            ConfigScreen config = root.Push(new ConfigScreen());
            for (int tab = 0; tab < 3; tab++)
            {
                UiPreview.Run(root, 40);
                Assert.Equal(tab, config.CurrentTab);
                List<(Ctl Ctl, float X, float Y)> layout = config.Layout;
                Assert.True(layout.Count > (tab == 2 ? 3 : 8), $"{lang} tab {tab}: {layout.Count} controls");     // the Controls tab is a picture: the tabs and the default button
                Assert.All(layout, c => Assert.True(float.IsFinite(c.X) && float.IsFinite(c.Y)));
                Assert.Equal(CtlKind.Tab, layout[0].Ctl.Kind);
                Assert.Equal(19, config.VoiceRows.Count);
                Assert.True(DrawList(root).Length > 20);
                Assert.Empty(root.Context.Assets.Missing);
                root.Step(new InputFrame { Frame = root.Context.Frame + 1, Pressed = PadButtons.R1 });
            }
        }
    }
}

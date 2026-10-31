using Tsuki.Engine.Contracts;
using Tsuki.Tests.Data.Source;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Data.Ui;

/// <summary>
/// The gallery, the music player, the ending list and the flowchart on the user's own data (package K5b): the eboot tables load with the shapes
/// docs/formats/gallery.md gives, every picture and sheet they name exists, the unlock rules answer like the Python oracle (tools/tsuki/misc_gallery_extras_flowchart.py)
/// on seven fixed progress states, and every screen builds from the real UI sheets.
/// </summary>
public sealed class GalleryRealDataTests
{
    private static readonly Language[] Languages = [Language.Ja, Language.En, Language.Zc, Language.Zt];

    private static IUiGalleryTables Tables => SharedSource.Source.Eboot.Gallery ?? throw new InvalidOperationException("no gallery tables");

    private static GalleryModel Model => GalleryModel.For(Tables);

    // ---- the tables -------------------------------------------------------------------------------------------------------------------------------

    [GameDataFact]
    public void TheTablesLoadWithTheCountsOfTheDocument()
    {
        IUiGalleryTables t = Tables;
        Assert.Equal(458, t.CgGroups.Count);
        Assert.Equal(1972, t.CgGroups.Sum(g => g.Entries.Count));
        Assert.Equal(105, t.CgAliases.Count);
        Assert.Equal(11, t.CgLanguageStrips.Count);
        Assert.Equal(19, t.CgCategoryScenes.Count);
        Assert.Equal(95, t.Tracks.Count);
        Assert.Equal(25, t.Endings.Count);
        Assert.Equal(277, t.ChartColumns.Count);
        Assert.Equal(1286, t.ChartColumns.Sum(c => c.Nodes.Count));
        Assert.Equal(630, t.ChartEdges.Count);
        Assert.Equal(30, t.ChartTitleCells(Language.En).Count);
        Assert.Equal(30, t.ChartTitleCells(Language.Ja).Count);
        Assert.Equal(12, t.ChartLineCells.Count);

        // category scenes of gallery.md 4.4
        Assert.Equal([2], t.CgCategoryScenes[1]);
        Assert.Equal([275, 276], t.CgCategoryScenes[15]);
        Assert.Equal([309], t.CgCategoryScenes[19]);
    }

    [GameDataFact]
    public void EveryCgNamesAPictureAndAThumbnailChildThatExist()
    {
        IUiGalleryTables t = Tables;
        int children = SharedSource.Source.UiSheetChildCount(UiSet.GalleryPartsCgTh, Language.En);
        Assert.Equal(1979, children);
        var seen = new HashSet<int>();
        var assets = new UiAssets(SharedSource.Source, Language.En);
        foreach (GalleryCgRow cg in t.CgGroups.SelectMany(g => g.Entries))
        {
            Assert.NotNull(assets.Tables.FindPicture(cg.Name));
            Assert.InRange(cg.Thumbnail, 0, children - 1);
            Assert.True(seen.Add(cg.Thumbnail), cg.Name);
            Assert.True(cg.Width > 0 && cg.Height > 0, cg.Name);
        }
    }

    [GameDataFact]
    public void TheUnlockClassesOfTheCgsAreThoseOfTheDocument()
    {
        GalleryCg[] all = [.. Model.Groups.SelectMany(g => g)];
        Assert.Equal(1972, all.Length);

        // 952 pictures are in names_sprites and unlock when shown; 105 entries have an alias; 923 gallery-only pictures unlock only through scene_done(557)
        Assert.Equal(952, all.Count(c => Tables.PictureIndex(c.Row.Name) is >= 0 and < GalleryModel.SpriteNameCount));
        Assert.Equal(105, all.Count(c => c.AliasSeen.Count > 0));
        Assert.Equal(923, all.Count(c => c.DirectSeen < 0 && c.AliasSeen.Count == 0));
        Assert.Equal(11, all.Count(c => c.LanguageStrip));

        // the pictures that stack a strip per language are four strips tall
        var assets = new UiAssets(SharedSource.Source, Language.En);
        foreach (GalleryCg cg in all.Where(c => c.LanguageStrip))
        {
            (int archive, int entry) = assets.Tables.FindPicture(cg.Row.Name)!.Value;
            Picture pic = SharedSource.Source.Picture(archive, entry);
            Assert.True(pic.Height >= cg.Row.Y + (4 * cg.Row.Height), $"{cg.Row.Name}: {pic.Height} < 4 strips of {cg.Row.Height}");
        }
    }

    [GameDataFact]
    public void EveryFlowNodeHasAPreviewAndAMosaicCell()
    {
        int previews = SharedSource.Source.UiSheetChildCount(UiSet.GalleryPartsFcTh, Language.En);
        Assert.Equal(495, previews);
        ChartNodeRow[] scenes = [.. Tables.ChartColumns.SelectMany(c => c.Nodes).Where(n => n.Scene > 0)];
        Assert.Equal(495, scenes.Length);
        foreach (ChartNodeRow n in scenes)
        {
            Assert.Equal(n.Scene - 1, n.Preview);
            Assert.InRange(n.Mosaic, 0, 519);
            Assert.True(n.TitleUi > 0 && n.SynopsisUi > 0);
            Assert.InRange(n.ChapterLabel, 0, 27);
        }

        // the four previews that stack one band per language are 972 high
        var assets = new UiAssets(SharedSource.Source, Language.En);
        foreach (int child in new[] { 107, 114, 188, 481 })
        {
            Assert.Equal((430, 972), (assets.Geometry(GallerySheets.ChartPreview(child))!.Width, assets.Geometry(GallerySheets.ChartPreview(child))!.Height));
        }

        Assert.Equal((430, 243), (assets.Geometry(GallerySheets.ChartPreview(0))!.Width, assets.Geometry(GallerySheets.ChartPreview(0))!.Height));
    }

    [GameDataTheory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    [InlineData(Language.Zc)]
    [InlineData(Language.Zt)]
    public void EveryTitleSynopsisCreditAndPromptHasATextInEveryLanguage(Language lang)
    {
        var assets = new UiAssets(SharedSource.Source, lang);
        foreach (ChartNodeRow n in Tables.ChartColumns.SelectMany(c => c.Nodes).Where(n => n.Scene > 0))
        {
            Assert.False(string.IsNullOrWhiteSpace(assets.Text(n.TitleUi)), $"{lang} scene {n.Scene} title");
            Assert.False(string.IsNullOrWhiteSpace(assets.Text(n.SynopsisUi)), $"{lang} scene {n.Scene} synopsis");
        }

        foreach (GalleryTrackRow t in Tables.Tracks)
        {
            Assert.False(string.IsNullOrWhiteSpace(assets.Text(t.TitleUi)), $"{lang} track {t.Bgm}");
            Assert.True(t.LoopEnd > t.LoopStart, $"track {t.Bgm}");
            foreach (int c in new[] { t.VocalUi, t.LyricsUi, t.ComposerUi, t.ArrangerUi }.Where(c => c >= 0))
            {
                Assert.False(string.IsNullOrWhiteSpace(assets.Text(c)), $"{lang} track {t.Bgm} credit {c}");
            }
        }

        foreach (GalleryEndingRow e in Tables.Endings)
        {
            Assert.False(string.IsNullOrWhiteSpace(assets.Text(e.MessageUi)), $"{lang} ending {e.ScriptScene}");
        }

        foreach (int id in new[] { 245, 246, 247, 248, 249, 1497, 1498 })
        {
            Assert.False(string.IsNullOrWhiteSpace(assets.Text(id)), $"{lang} ui {id}");
        }
    }

    // ---- the sheets -------------------------------------------------------------------------------------------------------------------------------

    [GameDataTheory]
    [InlineData(Language.En)]
    [InlineData(Language.Ja)]
    public void TheGallerySheetsHaveTheSizesTheLayoutsAssume(Language lang)
    {
        var assets = new UiAssets(SharedSource.Source, lang);
        void Is(UiSheet s, int w, int h)
        {
            SheetGeometry? g = assets.Geometry(s);
            Assert.NotNull(g);
            Assert.Equal((w, h), (g.Width, g.Height));
        }

        Is(GallerySheets.MenuLine, 304, 56);
        Is(GallerySheets.MenuButtons, 800, 632);
        Is(GallerySheets.MenuWindow, 312, 1080);
        Is(GallerySheets.MenuLight, 528, 240);
        Is(GallerySheets.Underline, 1920, 16);
        Is(GallerySheets.Panel, 1560, 1080);
        Is(GallerySheets.CgCursor, 520, 160);
        Is(GallerySheets.CgThumb(0), 231, 130);
        Is(GallerySheets.MusicLine, 926, 16);
        Is(GallerySheets.MusicPanel, 448, 896);
        Is(GallerySheets.MusicButtons, 584, 272);
        Is(GallerySheets.MusicSlider, 400, 24);
        Is(GallerySheets.MusicList, 960, 1736);
        Is(GallerySheets.MusicKnob, 248, 88);
        Is(GallerySheets.MovieCards, 776, 464);
        Is(GallerySheets.EndingPlates, 1472, 584);
        Is(GallerySheets.EndingPlateButtons, 296, 104);
        Is(GallerySheets.EndingButtons, 368, 128);
        Is(GallerySheets.EndingCard(0), 432, 544);
        Is(GallerySheets.EndingCardLocked, 432, 544);
        Is(GallerySheets.ChartBackground, 1920, 1080);
        Is(GallerySheets.ChartCursor, 232, 80);
        Is(GallerySheets.ChartTag, 144, 32);
        Is(GallerySheets.ChartCard, 488, 496);
        Is(GallerySheets.Of(ChartSheet.LineGrey), 120, 304);
        Is(GallerySheets.Of(ChartSheet.LineLit), 120, 304);
        Is(GallerySheets.Of(ChartSheet.Mosaic), 1620, 1170);
        Is(GallerySheets.Of(ChartSheet.NodeFrame), 104, 64);
        Is(GallerySheets.Of(ChartSheet.BandMiddle), 1440, 1);
        Is(GallerySheets.Of(ChartSheet.BandUnder), 1440, 5);
        Is(GallerySheets.Of(ChartSheet.BandUpper), 1440, 5);
        Is(GallerySheets.Of(ChartSheet.Title), lang == Language.En ? 1392 : 632, 408);

        // the banners: the CG and music ones exist in English only, the movie and ending ones everywhere
        Assert.Equal(lang == Language.En, assets.Geometry(GallerySheets.CgTitle) is not null && GallerySheets.HasTitle(GalleryMode.Cg, lang));
        Is(GallerySheets.MovieTitle, 720, lang == Language.En ? 100 : 80);
        Is(GallerySheets.EndingTitle, 720, lang == Language.En ? 100 : 80);
    }

    [GameDataTheory]
    [InlineData(Language.En)]
    [InlineData(Language.Ja)]
    public void TheChartCellsLieInsideTheirSheets(Language lang)
    {
        var assets = new UiAssets(SharedSource.Source, lang);
        SheetGeometry titles = assets.Geometry(GallerySheets.Of(ChartSheet.Title))!;
        foreach (SheetCell c in Tables.ChartTitleCells(lang))
        {
            Assert.True(c.X0 >= 0 && c.Y0 >= 0 && c.X1 <= titles.Width && c.Y1 <= titles.Height, $"{c}");
        }

        SheetGeometry lines = assets.Geometry(GallerySheets.Of(ChartSheet.LineGrey))!;
        foreach (SheetCell c in Tables.ChartLineCells)
        {
            Assert.True(c.X0 >= 0 && c.Y0 >= 0 && c.X1 <= lines.Width && c.Y1 <= lines.Height, $"{c}");
            Assert.Equal(96, c.Width);
        }
    }

    // ---- the unlock rules against the Python oracle -----------------------------------------------------------------------------------------------

    public static TheoryData<string> OracleStates()
    {
        var data = new TheoryData<string>();
        foreach (string s in GalleryOracleStates.Names)
        {
            data.Add(s);
        }

        return data;
    }

    [GameDataTheory]
    [MemberData(nameof(OracleStates))]
    public void TheUnlockRulesEqualThePythonOracle(string state)
    {
        PreviewProgress p = GalleryOracleStates.Build(state);
        GalleryModel model = Model;

        // CG: the flat index of an entry is its position in group order
        int[][] unlocked = model.Unlocked(p);
        var cg = new List<int>();
        int offset = 0;
        for (int g = 0; g < model.Groups.Count; g++)
        {
            cg.AddRange(unlocked[g].Select(k => offset + k));
            offset += model.Groups[g].Count;
        }

        Assert.Equal(GalleryOracleExpected.Cg(state), cg);
        Assert.Equal(GalleryOracleExpected.Tiles(state), Enumerable.Range(0, unlocked.Length).Where(g => unlocked[g].Length > 0));
        Assert.Equal(GalleryOracleExpected.Music(state), Enumerable.Range(0, Tables.Tracks.Count).Where(i => model.TrackUnlocked(i, p)));
        Assert.Equal(GalleryOracleExpected.Extras(state), Enumerable.Range(0, Tables.Endings.Count).Where(i => model.EndingUnlocked(i, p)));
        Assert.Equal(GalleryOracleExpected.Movies(state), Enumerable.Range(0, 3).Where(i => GalleryModel.MovieUnlocked(i, p)));

        // the flowchart's column filter equals the engine's loop (emulated in the oracle)
        Assert.Equal(GalleryOracleExpected.Kept(state), FlowchartChart.KeptColumns(Tables.ChartColumns, p));
    }

    [GameDataFact]
    public void TheColumnFilterKeepsTheCountsTheDocumentGives()
    {
        Assert.Equal(277, FlowchartChart.KeptColumns(Tables.ChartColumns, GalleryOracleStates.Build("everything")).Count);
        Assert.Equal(276, FlowchartChart.KeptColumns(Tables.ChartColumns, GalleryOracleStates.Build("edges_only")).Count);
        Assert.Equal(71, FlowchartChart.KeptColumns(Tables.ChartColumns, GalleryOracleStates.Build("seen_prefix")).Count);
        Assert.Empty(FlowchartChart.KeptColumns(Tables.ChartColumns, GalleryOracleStates.Build("empty")));
    }

    // ---- the chart --------------------------------------------------------------------------------------------------------------------------------

    [GameDataTheory]
    [InlineData("everything", true)]
    [InlineData("everything", false)]
    [InlineData("sparse", true)]
    [InlineData("seen_prefix", true)]
    public void EveryCursorMoveOnTheRealChartLandsOnANode(string state, bool gallery)
    {
        PreviewProgress p = GalleryOracleStates.Build(state);
        foreach (int n in p.Done)
        {
            p.Route.Add(n);
        }

        FlowchartChart chart = FlowchartChart.Build(Tables, Language.En, p, gallery);
        Assert.True(chart.Rows > 0);
        for (int k = 0; k < chart.Rows; k++)
        {
            for (int j = 0; j < 9; j++)
            {
                var cell = new ChartCell(j, k);
                foreach (ChartCell? to in new[] { chart.MoveLeft(cell), chart.MoveRight(cell), chart.MoveUp(cell), chart.MoveDown(cell), chart.KeepInView(cell, _ => 500f) })
                {
                    if (to is { } c)
                    {
                        Assert.True(chart.NodeAt(c) >= 0, $"{state} {gallery}: ({j}, {k}) -> {c}");
                    }
                }
            }
        }
    }

    [GameDataFact]
    public void TheFullChartHoldsEverySceneNodeAndLaysOutInsideItsSprites()
    {
        PreviewProgress p = GalleryOracleStates.Build("everything");
        FlowchartChart chart = FlowchartChart.Build(Tables, Language.En, p, gallery: true);
        Assert.Equal(277, chart.Rows);
        Assert.Equal(495, chart.Nodes.Count(n => n.Node > 0));
        Assert.True(chart.ScrollMax > 10000f);
        Assert.Equal(chart.Limits.Count, chart.Titles.Count == 0 ? 0 : chart.Limits.Count);

        // sprites: sheets exist, sources lie inside them
        var assets = new UiAssets(SharedSource.Source, Language.En);
        foreach (IGrouping<ChartSheet, ChartSprite> group in chart.Sprites.GroupBy(s => s.Sheet))
        {
            SheetGeometry g = assets.Geometry(GallerySheets.Of(group.Key))!;
            foreach (ChartSprite s in group)
            {
                Assert.True(s.Src.X >= 0 && s.Src.Y >= 0 && s.Src.Right <= g.Width && s.Src.Bottom <= g.Height, $"{group.Key} {s.Src} in {g.Width}x{g.Height}");
            }
        }

        Assert.Empty(assets.Missing);
    }

    [GameDataFact]
    public void TheGalleryModelsOfAPlayerAQuarterThroughAreBelievable()
    {
        var services = new PreviewServices(Language.En);
        services.Believe(SharedSource.Source, "mid");
        int[][] unlocked = Model.Unlocked(services);
        int total = unlocked.Sum(g => g.Length);
        Assert.InRange(total, 150, 1600);
        Assert.True(services.Progress.Done.Count > 190);
        Assert.True(services.SceneDone(190));

        FlowchartChart chart = FlowchartChart.Build(Tables, Language.En, services, gallery: false);
        Assert.True(chart.Nodes.Count > 100);
        Assert.NotEmpty(chart.Titles.SelectMany(t => t));
        Assert.True(FlowchartScreen.Available(new UiRoot(SharedSource.Source, services).Context, gallery: false));

        var everything = new PreviewServices(Language.En);
        everything.Believe(SharedSource.Source, "full");
        Assert.Equal(1972, Model.Unlocked(everything).Sum(g => g.Length));
        Assert.All(Enumerable.Range(0, 25), i => Assert.True(Model.EndingUnlocked(i, everything)));
    }

    // ---- the screens ------------------------------------------------------------------------------------------------------------------------------

    private static DrawCmd[] DrawList(UiRoot root)
    {
        var list = new DisplayList(2048);
        list.Reset(root.Context.Frame);
        root.Draw(list);
        list.Seal();
        return list.Commands.ToArray();
    }

    [GameDataTheory]
    [InlineData("gallery-cg", UiSet.GalleryPartsCgTh)]
    [InlineData("gallery-cg-group", UiSet.GalleryPartsCgTh)]
    [InlineData("gallery-music", UiSet.GalleryPartsMusic)]
    [InlineData("gallery-movies", UiSet.GalleryPartsMovie)]
    [InlineData("gallery-endings", UiSet.GalleryPartsEnding)]
    [InlineData("flowchart-gallery", UiSet.GalleryPartsFcTh)]
    [InlineData("flowchart-game", UiSet.GalleryPartsFc)]
    public void EveryGalleryScreenDrawsItsOwnSheetsInEveryLanguage(string screen, UiSet set)
    {
        foreach (Language lang in Languages)
        {
            var services = new PreviewServices(lang);
            UiRoot root = UiPreview.Open(screen, SharedSource.Source, services);
            DrawCmd[] cmds = DrawList(root);
            Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile } t && t.A == (int)set);
            Assert.Empty(root.Context.Assets.Missing);
            Assert.Empty(root.Context.Assets.MissingPictures);
        }
    }

    [GameDataFact]
    public void TheCgViewerDrawsThePictureThroughItsSourceRectangle()
    {
        var services = new PreviewServices(Language.En);
        UiRoot root = UiPreview.Open("gallery-cg-view", SharedSource.Source, services);
        Assert.IsType<GalleryCgViewer>(root.Top);
        DrawCmd[] cmds = DrawList(root);
        DrawCmd[] tiles = [.. cmds.Where(c => c.Texture.Kind == TextureKind.PictureTile)];
        Assert.NotEmpty(tiles);

        // the picture is fitted to the screen height and centred
        Assert.InRange(tiles.Min(c => c.V0.X), 0f, 300f);
        Assert.InRange(tiles.Max(c => c.V3.X), 1620f, 1920f);
        Assert.Equal(1080f, tiles.Max(c => c.V3.Y), 1f);
        Assert.Empty(root.Context.Assets.MissingPictures);
    }

    [GameDataFact]
    public void TheTitleLeadsIntoTheGalleryAndBackOnRealData()
    {
        var services = new PreviewServices(Language.En);
        services.Believe(SharedSource.Source, "mid");
        var root = new UiRoot(SharedSource.Source, services);
        root.Context.TitleIntroShown = true;
        var title = root.Push(new TitleScreen());
        UiPreview.Run(root, 300);
        UiPreview.Press(root, PadButtons.Up);                             // wraps to EXIT (package K18) ...
        UiPreview.Press(root, PadButtons.Up);                             // ... and on to GALLERY
        UiPreview.Press(root, PadButtons.Decide);
        UiPreview.Run(root, 100);
        var gallery = Assert.IsType<GalleryScreen>(root.Top);
        Assert.Equal(GalleryMode.Cg, gallery.Mode);
        for (int i = 0; i < 5; i++)
        {
            UiPreview.Press(root, PadButtons.Down);
        }

        UiPreview.Press(root, PadButtons.Decide);
        UiPreview.Run(root, 60);
        Assert.Same(title, root.Top);
    }
}

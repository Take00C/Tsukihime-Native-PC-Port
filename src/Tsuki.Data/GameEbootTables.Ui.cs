using System.Buffers.Binary;
using Tsuki.Data.Eboot;
using Tsuki.Data.Eboot.Tables;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Data;

// The menu area of the eboot tables (package K5a): the UI cell tables, the config pages, the dialog messages and ui_text in the form Tsuki.Ui reads them.
public sealed partial class GameEbootTables
{
    private UiTablesView? _ui;
    private UiGalleryView? _galleryView;

    /// <inheritdoc/>
    IUiTables Tsuki.Engine.Contracts.IEbootTables.Ui => LazyInitializer.EnsureInitialized(ref _ui, () => new UiTablesView(this))!;

    /// <inheritdoc/>
    IUiGalleryTables? Tsuki.Engine.Contracts.IEbootTables.Gallery => LazyInitializer.EnsureInitialized(ref _galleryView, () => new UiGalleryView(this));

    /// <summary><see cref="IUiTables"/> over the readers of Tsuki.Data/Eboot/Tables; every table is read on first use.</summary>
    private sealed class UiTablesView : IUiTables
    {
        private readonly GameEbootTables _owner;
        private readonly EngineTables _engine;
        private readonly Lazy<IReadOnlyList<SheetCell>>[] _saveTitles;
        private readonly Lazy<IReadOnlyList<SheetCell>>[] _phaseTitles;
        private readonly Lazy<IReadOnlyList<SheetCell>>[] _guideWords;
        private readonly Lazy<IReadOnlyList<SheetCell>>[] _voiceNames;
        private readonly Lazy<IReadOnlyList<SheetCell>> _guideIcons;
        private readonly Lazy<IReadOnlyList<SheetCell>> _banners;
        private readonly Lazy<IReadOnlyList<(float X, float Y)>> _bannerKeys;
        private readonly Lazy<IReadOnlyList<SheetCell>> _faces;
        private readonly Lazy<IReadOnlyList<UiConfigPage>> _config;
        private readonly Lazy<IReadOnlyList<int>> _titleBackground;

        public UiTablesView(GameEbootTables owner)
        {
            _owner = owner;
            _engine = new EngineTables(owner.Reader);
            const LazyThreadSafetyMode Mode = LazyThreadSafetyMode.ExecutionAndPublication;
            EbootReader r = owner.Reader;
            _saveTitles = [new(() => Convert(UiCellTables.ReadSaveTitleCells(r, english: false)), Mode), new(() => Convert(UiCellTables.ReadSaveTitleCells(r, english: true)), Mode)];
            _phaseTitles =
            [
                new(() => Convert(UiCellTables.ReadPhaseTitleCells(r, PhaseTitleSet.Ja)), Mode),
                new(() => Convert(UiCellTables.ReadPhaseTitleCells(r, PhaseTitleSet.En)), Mode),
                new(() => Convert(UiCellTables.ReadPhaseTitleCells(r, PhaseTitleSet.Zh)), Mode),
            ];
            _guideWords = [.. Enumerable.Range(0, 5).Select(l => new Lazy<IReadOnlyList<SheetCell>>(() => Convert(UiCellTables.ReadGuideWordCells(r, l)), Mode))];
            _voiceNames = [new(() => Convert(UiCellTables.ReadVoiceNameCells(r, english: false)), Mode), new(() => Convert(UiCellTables.ReadVoiceNameCells(r, english: true)), Mode)];
            _guideIcons = new(() => Convert(UiCellTables.ReadGuideIconCells(r)), Mode);
            _banners = new(() => Convert(UiCellTables.ReadBannerCells(r)), Mode);
            _bannerKeys = new(() => UiCellTables.ReadBannerKeys(r), Mode);
            _faces = new(() => Convert(UiCellTables.ReadVoiceFaceCells(r)), Mode);
            _config = new(() => [.. ConfigPageTable.Read(r).Select(p => new UiConfigPage(
                p.FirstItem, p.LastItem,
                [.. p.Rows.Select(row => new UiConfigRow([.. row.Controls.Select(c => new UiConfigControl(c.Item, c.Kind, c.X, c.Y))], row.Current, row.Maximum))]))], Mode);
            _titleBackground = new(() =>
            {
                byte[] raw = r.Read(r.Layout.TitleBackground, 24).ToArray();
                return Enumerable.Range(0, 6).Select(k => BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4 * k))).ToArray();
            }, Mode);
        }

        public FontTables Fonts => _owner.FontTables;

        public string Text(Language language, int id)
        {
            var rows = _owner.UiText.Rows(Math.Min((int)language, 4));
            return id > 0 && id < rows.Count ? rows[id].Text ?? string.Empty : string.Empty;
        }

        public IReadOnlyList<SheetCell> SaveTitleCells(Language language) => _saveTitles[language == Language.En ? 1 : 0].Value;

        public IReadOnlyList<SheetCell> PhaseTitleCells(Language language) => _phaseTitles[language switch { Language.Ja => 0, Language.En => 1, _ => 2 }].Value;

        public IReadOnlyList<SheetCell> GuideIconCells => _guideIcons.Value;

        public IReadOnlyList<SheetCell> GuideWordCells(Language language) => _guideWords[Math.Min((int)language, 4)].Value;

        public IReadOnlyList<SheetCell> BannerCells => _banners.Value;

        public IReadOnlyList<(float X, float Y)> BannerKeys => _bannerKeys.Value;

        public IReadOnlyList<SheetCell> VoiceFaceCells => _faces.Value;

        public IReadOnlyList<SheetCell> VoiceNameCells(Language language) => _voiceNames[language == Language.En ? 1 : 0].Value;

        public IReadOnlyList<UiConfigPage> ConfigPages => _config.Value;

        public IReadOnlyList<int> DialogMessages => _engine.Dialogs.Ids.ToArray();

        public IReadOnlyList<int> VoiceRowWords => _engine.Voices.RowWords;

        public IReadOnlyList<string> TestSeNames => _engine.TestSes;

        public IReadOnlyList<int> TitleBackgroundTable => _titleBackground.Value;

        public (int Archive, int Entry)? FindPicture(string name)
        {
            var id = _owner.Catalog.NameIndex.Find(name);
            return id is { } v ? ((int)v.Archive, v.Index) : null;
        }

        private static IReadOnlyList<SheetCell> Convert(IReadOnlyList<UiCell> cells) => [.. cells.Select(c => new SheetCell(c.A, c.B, c.C, c.D))];
    }

    /// <summary><see cref="IUiGalleryTables"/> over <see cref="GalleryTables"/>, <see cref="FlowchartTables"/> and the flowchart cell tables (package K5b).</summary>
    private sealed class UiGalleryView : IUiGalleryTables
    {
        private readonly GameEbootTables _owner;
        private readonly Lazy<IReadOnlyList<GalleryGroupRow>> _groups;
        private readonly Lazy<IReadOnlyList<GalleryAliasRow>> _aliases;
        private readonly Lazy<IReadOnlyList<GalleryTrackRow>> _tracks;
        private readonly Lazy<IReadOnlyList<GalleryEndingRow>> _endings;
        private readonly Lazy<IReadOnlyList<ChartColumnRow>> _columns;
        private readonly Lazy<IReadOnlyList<ChartEdgeRow>> _edges;
        private readonly Lazy<IReadOnlyList<SheetCell>>[] _titleCells;
        private readonly Lazy<IReadOnlyList<SheetCell>> _lineCells;
        private readonly Lazy<Dictionary<string, int>> _pictureIndex;

        public UiGalleryView(GameEbootTables owner)
        {
            _owner = owner;
            const LazyThreadSafetyMode Mode = LazyThreadSafetyMode.ExecutionAndPublication;
            EbootReader r = owner.Reader;
            _groups = new(() => [.. Gallery.Groups.Select(g => new GalleryGroupRow(
                g.Category, [.. g.Entries.Select(e => new GalleryCgRow(e.Name, e.Thumbnail, e.X, e.Y, e.Width, e.Height, e.Flag))]))], Mode);
            _aliases = new(() => [.. Gallery.Aliases.Select(a => new GalleryAliasRow(a.Key, a.Aliases))], Mode);
            _tracks = new(() => [.. Gallery.Music.Select(m => new GalleryTrackRow(
                m.Bgm, m.TitleUi, m.VocalUi, m.LyricsUi, m.ComposerUi, m.ArrangerUi, m.LengthMs, m.LoopStart, m.LoopEnd, m.Variants))], Mode);
            _endings = new(() => [.. Gallery.Extras.Select(x => new GalleryEndingRow(x.ScriptScene, x.FlowScene, x.UnlockA, x.UnlockB, x.MessageUi))], Mode);
            _columns = new(() => ReadColumns(r, owner.GameEngineTables.Flowchart), Mode);
            _edges = new(() => [.. owner.GameEngineTables.Flowchart.Edges.Select(e => new ChartEdgeRow(e.From, e.To, e.GlobalIds, e.PlaythroughIds))], Mode);
            _titleCells =
            [
                new(() => FromRects(UiCellTables.ReadFlowTitleCells(r, english: false)), Mode),
                new(() => FromRects(UiCellTables.ReadFlowTitleCells(r, english: true)), Mode),
            ];
            _lineCells = new(() => FromRects(UiCellTables.ReadFlowLineCells(r)), Mode);
            _pictureIndex = new(() =>
            {
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                IReadOnlyList<string> names = owner.Catalog.PictureNames;
                for (int i = 0; i < names.Count; i++)
                {
                    map.TryAdd(names[i], i);
                }

                return map;
            }, Mode);
        }

        private GalleryTables Gallery => _owner.GameEngineTables.Gallery;

        public IReadOnlyList<GalleryGroupRow> CgGroups => _groups.Value;

        public IReadOnlyList<GalleryAliasRow> CgAliases => _aliases.Value;

        public IReadOnlyList<string> CgLanguageStrips => Gallery.Special;

        public IReadOnlyDictionary<int, IReadOnlyList<int>> CgCategoryScenes => Gallery.CategoryFlags;

        public IReadOnlyList<GalleryTrackRow> Tracks => _tracks.Value;

        public IReadOnlyList<GalleryEndingRow> Endings => _endings.Value;

        public IReadOnlyList<ChartColumnRow> ChartColumns => _columns.Value;

        public IReadOnlyList<ChartEdgeRow> ChartEdges => _edges.Value;

        public IReadOnlyList<SheetCell> ChartTitleCells(Language language) => _titleCells[language == Language.En ? 1 : 0].Value;

        public IReadOnlyList<SheetCell> ChartLineCells => _lineCells.Value;

        public int PictureIndex(string name)
        {
            ArgumentNullException.ThrowIfNull(name);

            // NameTable_Lookup (0x78410): up to 32 characters, ended by NUL or '.', compared upper-cased
            string key = name.Length > 32 ? name[..32] : name;
            int dot = key.IndexOf('.', StringComparison.Ordinal);
            if (dot >= 0)
            {
                key = key[..dot];
            }

            return _pictureIndex.Value.GetValueOrDefault(key, -1);
        }

        public int PictureSeenIndex(int pictureId) => ((Tsuki.Engine.Contracts.IEbootTables)_owner).GameFlow.PictureSeenIndex(pictureId);

        private static IReadOnlyList<SheetCell> Convert(IReadOnlyList<UiCell> cells) => [.. cells.Select(c => new SheetCell(c.A, c.B, c.C, c.D))];

        // the flowchart's label and connector tables hold (x, y, width, height), the other cell tables corners: both come out as corners
        private static IReadOnlyList<SheetCell> FromRects(IReadOnlyList<UiCell> cells) => [.. cells.Select(c => new SheetCell(c.A, c.B, c.A + c.C, c.B + c.D))];

        // each column keeps its node records and the two words of the record that follows them in memory (gallery.md 5.4: the engine's filter reads it)
        private static IReadOnlyList<ChartColumnRow> ReadColumns(EbootReader r, FlowchartTables flow)
        {
            var columns = new List<ChartColumnRow>(flow.Columns.Count);
            for (int c = 0; c < flow.Columns.Count; c++)
            {
                FlowColumn col = flow.Columns[c];
                long nodes = GalleryTables.Pointer(r, FlowchartTables.ColumnVa + (32L * c) + 16);
                long after = nodes + (64L * col.Nodes.Count);
                columns.Add(new ChartColumnRow(
                    col.Group,
                    [.. col.Nodes.Select(n => new ChartNodeRow(n.Scene, n.Connector, n.Link, n.ScriptScene, n.ChapterLabel, n.Order, n.Thumbnail, n.TitleUi, n.SynopsisUi))],
                    r.I32(after),
                    r.I32(after + 8)));
            }

            return columns;
        }
    }
}

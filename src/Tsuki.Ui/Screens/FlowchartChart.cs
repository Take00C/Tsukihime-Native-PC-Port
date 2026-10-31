// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/flow.rs (chart layout from cFlowchart, cursor movement)
using Tsuki.Engine.Contracts;

namespace Tsuki.Ui.Screens;

/// <summary>The sheets of the flowchart's chart (GALLERY_PARTS_FC children).</summary>
public enum ChartSheet
{
    /// <summary>Child 8: the one-pixel-high band fill (1440 x 1).</summary>
    BandMiddle,

    /// <summary>Child 9: the lower edge of a band (1440 x 5).</summary>
    BandUnder,

    /// <summary>Child 10: the upper edge of a band (1440 x 5).</summary>
    BandUpper,

    /// <summary>Child 4: the route lines, grey (120 x 304).</summary>
    LineGrey,

    /// <summary>Child 5: the route lines, lit (120 x 304).</summary>
    LineLit,

    /// <summary>Child 7: the scene thumbnail mosaic (1620 x 1170, 20 columns of 81 x 45).</summary>
    Mosaic,

    /// <summary>Child 11: the node frame without a thumbnail (104 x 64).</summary>
    NodeFrame,

    /// <summary>Child 6: the chapter labels.</summary>
    Title,
}

/// <summary>A textured piece of the chart in content coordinates (y not yet scrolled).</summary>
/// <param name="Sheet">The sheet.</param>
/// <param name="Dst">Where it lands.</param>
/// <param name="Src">The part of the sheet in texels.</param>
/// <param name="Prio">Drawing priority: farther pieces (higher) first.</param>
public readonly record struct ChartSprite(ChartSheet Sheet, RectF Dst, RectF Src, int Prio);

/// <summary>A node the cursor can select.</summary>
/// <param name="Node">The node id (its scene_done bit; negative for a marker, which is never selectable).</param>
/// <param name="X">Left edge in content coordinates.</param>
/// <param name="Y">Top edge in content coordinates.</param>
/// <param name="Preview">The GALLERY_PARTS_FC_TH child of the preview picture.</param>
/// <param name="TitleUi"><c>ui_text</c> id of the scene title.</param>
/// <param name="SynopsisUi"><c>ui_text</c> id of the synopsis.</param>
public readonly record struct ChartNode(int Node, float X, float Y, int Preview, int TitleUi, int SynopsisUi);

/// <summary>A cursor cell: the record index inside its row and the shown row.</summary>
/// <param name="Col">Record index in the row (cFlowchart + 56).</param>
/// <param name="Row">Shown row (cFlowchart + 60).</param>
public readonly record struct ChartCell(int Col, int Row);

/// <summary>The record under a cursor cell, as the action on it sees it (knife <c>record_at</c>).</summary>
/// <param name="Column">The static column index (the table row).</param>
/// <param name="Node">The node id (<see cref="ChartNodeRow.Scene"/>); 0 for a filler.</param>
/// <param name="ScriptScene">The scene id a jump or replay starts; -1 for a filler record.</param>
public readonly record struct ChartRecord(int Column, int Node, int ScriptScene);

/// <summary>
/// The flowchart's chart (knife <c>flow.rs</c>, itself the layout of cFlowchart): the columns of the eboot table that the progress opens, laid out on a 96 x 56
/// grid (node x = 438 + 96 j, row pitch 56) in sections per chapter band, with grey and lit connector pieces, plus the cursor movement over the record grid.
/// The data is the PS4's: the node records of <see cref="IUiGalleryTables.ChartColumns"/>, the cells of the PS4 sheets, and the column filter of the PS4 code
/// (which reads one record past the end of a column, gallery.md 5.4).
/// </summary>
public sealed class FlowchartChart
{
    /// <summary>Node frame width.</summary>
    public const float NodeW = 104f;

    /// <summary>Node frame height.</summary>
    public const float NodeH = 64f;

    /// <summary>The height of the part of the screen the chart scrolls in (above the guide bar).</summary>
    public const float ViewHeight = 1022f;

    /// <summary>The mosaic cell size: 20 columns of 81 x 45 with a one-pixel border.</summary>
    public const int MosaicCellW = 81;

    /// <summary>The mosaic cell height.</summary>
    public const int MosaicCellH = 45;

    /// <summary>Mosaic cells per row.</summary>
    public const int MosaicColumns = 20;

    private const uint Drawn = 1u << 31;
    private const uint Lit = 1u << 30;
    private const int EdgeBase = 564;

    private readonly List<GridRow> _grid;
    private readonly IUiProgress _progress;
    private readonly bool _gallery;

    private FlowchartChart(
        List<ChartSprite> sprites, List<ChartNode> nodes, List<List<ChartSprite>> titles, List<float> limits, List<float> rowY, float scrollMax, List<GridRow> grid,
        IUiProgress progress, bool gallery)
    {
        Sprites = sprites;
        Nodes = nodes;
        Titles = titles;
        Limits = limits;
        RowY = rowY;
        ScrollMax = scrollMax;
        _grid = grid;
        _progress = progress;
        _gallery = gallery;
    }

    /// <summary>Every textured piece, farther pieces first.</summary>
    public IReadOnlyList<ChartSprite> Sprites { get; }

    /// <summary>The selectable nodes.</summary>
    public IReadOnlyList<ChartNode> Nodes { get; }

    /// <summary>The phase titles by phase number: drawn pinned to the top of the view while their section scrolls past.</summary>
    public IReadOnlyList<IReadOnlyList<ChartSprite>> Titles { get; }

    /// <summary>The y of the upper divider of each section change, in order; the titles of phase p are pushed up by entry p.</summary>
    public IReadOnlyList<float> Limits { get; }

    /// <summary>The node y of each shown row.</summary>
    public IReadOnlyList<float> RowY { get; }

    /// <summary>The scroll range.</summary>
    public float ScrollMax { get; }

    /// <summary>The number of shown rows.</summary>
    public int Rows => _grid.Count;

    private sealed class Rec
    {
        public Rec(ChartNodeRow node) => N = node;

        public ChartNodeRow N { get; }

        public uint Flags { get; set; }
    }

    private sealed class Row
    {
        public Row(int index, int phase, List<Rec> recs)
        {
            Index = index;
            Phase = phase;
            Recs = recs;
        }

        public int Index { get; }

        public int Phase { get; }

        public List<Rec> Recs { get; }
    }

    private sealed class GridRow
    {
        public GridRow(int index, int phase, List<(ChartNodeRow N, int Node)> cells)
        {
            Index = index;
            Phase = phase;
            Cells = cells;
        }

        public int Index { get; }

        public int Phase { get; }

        public List<(ChartNodeRow N, int Node)> Cells { get; }
    }

    // ---- the column filter ------------------------------------------------------------------------------------------------------------------------

    private static (int Scene, int Link) NodeAt(IReadOnlyList<ChartColumnRow> columns, int c, int j)
    {
        ChartColumnRow col = columns[c];
        return j < col.Nodes.Count ? (col.Nodes[j].Scene, col.Nodes[j].Link) : (col.OverrunScene, col.OverrunLink);
    }

    /// <summary>
    /// The columns the progress opens (gallery.md 5.4, the loop of the cFlowchartScene build at 0x2149E; verified against the engine under emulation). A column is
    /// kept when it holds lit nodes that add something new compared with the columns around it.
    /// </summary>
    public static List<int> KeptColumns(IReadOnlyList<ChartColumnRow> columns, IUiProgress progress)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(progress);
        var kept = new List<int>();
        for (int c = 0; c < columns.Count; c++)
        {
            ChartColumnRow col = columns[c];
            int count = col.Nodes.Count;
            int rows = Math.Max(count, 1);
            int vis = 0;
            int diff = 0;
            for (int j = 0; j < rows; j++)
            {
                (int a, int link) = NodeAt(columns, c, j);
                if (!((a > 0 && progress.SceneDone(a)) || (link > 0 && progress.EdgeSeen(link))))
                {
                    continue;
                }

                vis++;
                if (a > 0)
                {
                    diff++;
                    continue;
                }

                if (a == 0)
                {
                    continue;
                }

                // a marker for scene -a: the previous static column is compared whether or not it was kept
                if (c == 0 || j > columns[c - 1].Nodes.Count)
                {
                    diff++;
                    continue;
                }

                int pa = NodeAt(columns, c - 1, j).Scene;
                if (progress.SceneDone(-a))
                {
                    if (Math.Abs(pa) != Math.Abs(a))
                    {
                        diff++;
                        continue;
                    }

                    if (c > 0x113)
                    {
                        continue;
                    }

                    for (int k = c + 1; k < columns.Count; k++)
                    {
                        if (j > columns[k].Nodes.Count)
                        {
                            diff++;
                            break;
                        }

                        if (columns[k].Group > col.Group)
                        {
                            break;
                        }

                        (int ka, int klink) = NodeAt(columns, k, j);
                        if (ka != a || klink != link)
                        {
                            diff++;
                            break;
                        }
                    }
                }
                else if (pa + a != 0)
                {
                    diff++;
                }
            }

            if ((vis >= 2 && diff != 0) || (vis == 1 && (diff == 1 || count == 1)))
            {
                kept.Add(c);
            }
        }

        return kept;
    }

    // ---- layout -----------------------------------------------------------------------------------------------------------------------------------

    private sealed class Builder
    {
        private readonly IUiProgress _fl;
        private readonly IReadOnlyList<SheetCell> _lines;
        private readonly List<Row> _rows;

        public Builder(IUiProgress progress, IReadOnlyList<SheetCell> lines, List<Row> rows)
        {
            _fl = progress;
            _lines = lines;
            _rows = rows;
        }

        public List<ChartSprite> Sprites { get; } = [];

        private bool Completed(int n) => n >= 1 && _fl.SceneDone(n);

        private bool Read(int n) => n >= 1 && _fl.SceneSeen(n);

        private bool Route(int n) => n >= 1 && _fl.RouteScene(n);

        private bool Edge(int f) => f >= 1 && _fl.EdgeSeen(f);

        private bool RouteEdge(int f) => f >= 1 && _fl.RouteEdge(f);

        private void Push(ChartSheet sheet, float x, float y, RectF src, int prio) => Sprites.Add(new ChartSprite(sheet, new RectF(x, y, src.W, src.H), src, prio));

        private RectF Cell(int i) => i >= 0 && i < _lines.Count ? new RectF(_lines[i].X0, _lines[i].Y0, _lines[i].Width, _lines[i].Height) : new RectF(0, 0, 4, 4);

        /// <summary>sub_13B54: section band from <paramref name="top"/> to <paramref name="bottom"/>; the next band starts 10 px lower.</summary>
        public float Band(float top, float bottom)
        {
            float h = bottom - top + 3f;
            Sprites.Add(new ChartSprite(ChartSheet.BandMiddle, new RectF(0, top, 1440, h), new RectF(0, 0, 1440, 1), 460));
            return bottom + 10f;
        }

        /// <summary>sub_13D04: section divider (the lower edge of the band above, the upper edge of the next).</summary>
        public void Divider(float y)
        {
            Push(ChartSheet.BandUnder, 0, y, new RectF(0, 0, 1440, 5), 450);
            Push(ChartSheet.BandUpper, 0, y + 8f, new RectF(0, 0, 1440, 5), 450);
        }

        private Rec? RecAt(int k, int j) => k >= 0 && k < _rows.Count && j >= 0 && j < _rows[k].Recs.Count ? _rows[k].Recs[j] : null;

        private bool GreyOn(int k, int j) => RecAt(k, j) is { } r && ((r.Flags & Drawn) != 0 || (r.N.Scene >= 1 && Completed(r.N.Scene)) || Edge(r.N.Link));

        private bool LitOn(int k, int j) => RecAt(k, j) is { } r && ((r.Flags & Lit) != 0 || (r.N.Scene >= 1 && Route(r.N.Scene)) || RouteEdge(r.N.Link));

        private bool UpWith(int k, int j, Func<int, bool> f) => k >= 1 && RecAt(k - 1, j) is { } p && p.N.Scene != 0 && f(Math.Abs(p.N.Scene));

        // sub_16078
        private bool SelfGrey(int k, int j)
        {
            if (RecAt(k, j) is not { } r)
            {
                return false;
            }

            int n = r.N.Scene;
            return n == 0 ? false : n < 0 ? Completed(-n) || k + 1 == _rows.Count : Completed(n) || Edge(r.N.Link);
        }

        private bool SelfLit(int k, int j) => RecAt(k, j) is { } r && r.N.Scene != 0 && Route(Math.Abs(r.N.Scene));

        /// <summary>Connector kind for the branch and merge kinds 5, 8, 10, 11 (sub_14D10 .. sub_15218 grey, sub_158B0 .. sub_15E80 lit), from the neighbours that are connected.</summary>
        private int Shape(int kind, int k, int j, bool lit)
        {
            bool On(int jj) => lit ? LitOn(k, jj) : GreyOn(k, jj);
            bool up = lit ? UpWith(k, j, Route) : UpWith(k, j, Completed);
            bool Me() => lit ? SelfLit(k, j) : SelfGrey(k, j);
            bool left = On(j - 1);
            bool right = On(j + 1);
            switch (kind)
            {
                case 5:
                    return !up ? 4 : right ? (Me() ? 5 : 2) : Me() ? 3 : 5;
                case 8:
                    if (!up)
                    {
                        return 7;
                    }

                    return left ? (right ? 8 : 6) : right ? 2 : 8;
                case 10:
                    return !up ? 9 : left ? (Me() ? 10 : 6) : Me() ? 3 : 10;
                case 11:
                    if (!Me())
                    {
                        return 7;
                    }

                    return left ? (right ? 11 : 9) : right ? 4 : 11;
                default:
                    return kind;
            }
        }

        /// <summary>sub_153B8: draws connector <paramref name="kind"/> above a node; <paramref name="first"/>: first row of a section.</summary>
        private void Line(int k, int j, bool first, bool lit, int kind, float x, float y)
        {
            int baseCell = 0;
            ChartSheet sheet = lit ? ChartSheet.LineLit : ChartSheet.LineGrey;
            int prio = lit ? 440 : 450;
            int cellKind = kind is >= 0 and <= 25 ? kind : 12;
            RectF c = Cell(cellKind + baseCell);
            Push(sheet, x, y, c, prio);
            int n = RecAt(k, j)?.N.Scene ?? 0;
            if (kind == 3)
            {
                if (first)
                {
                    Push(sheet, x, y - 13f, Cell(baseCell), prio);
                }
            }
            else if (kind <= 11 && ((0xE30 >> kind) & 1) != 0 && n < 0)
            {
                bool c2 = Read(-n);
                if (lit && !c2)
                {
                    c2 = Route(-n);
                }

                if (c2)
                {
                    Push(sheet, x, y + 14f, Cell(baseCell + 1), prio);
                }
            }
        }

        /// <summary>sub_14824: grey connector.</summary>
        public bool Grey(int k, int j, bool first, float x, float y)
        {
            if (RecAt(k, j) is not { } r)
            {
                return false;
            }

            if ((r.Flags & Drawn) == 0 && !Edge(r.N.Link))
            {
                return false;
            }

            int n = r.N.Scene;
            int kind = r.N.Connector;
            if (kind == 3 && n < 0)
            {
                if (k < 1 || RecAt(k - 1, j) is not { } p)
                {
                    return false;
                }

                bool ok = (p.Flags & Drawn) != 0 && (p.N.Scene == 0 ? false : p.N.Scene < 0 ? Read(-p.N.Scene) : Completed(p.N.Scene));
                if (!ok)
                {
                    return false;
                }
            }
            else if (kind is 5 or 8 or 10 or 11)
            {
                kind = Shape(kind, k, j, lit: false);
            }

            if (kind < 0)
            {
                return false;
            }

            Line(k, j, first, false, kind, x, y);
            return true;
        }

        /// <summary>sub_149B4: lit connector along the current playthrough.</summary>
        public bool LitLine(int k, int j, bool first, float x, float y)
        {
            if (RecAt(k, j) is not { } r)
            {
                return false;
            }

            if ((r.Flags & Lit) == 0 && !RouteEdge(r.N.Link))
            {
                return false;
            }

            int n = r.N.Scene;
            int kind = r.N.Connector;
            if (kind == 3 && n < 0)
            {
                bool ok = k >= 1 && RecAt(k - 1, j) is { } p && (p.Flags & Lit) != 0 && Math.Abs(p.N.Scene) >= 1 && Route(Math.Abs(p.N.Scene));
                if (!ok)
                {
                    return true;
                }
            }
            else if (kind is 5 or 8 or 10 or 11)
            {
                kind = Shape(kind, k, j, lit: true);
            }

            if (kind >= 0)
            {
                Line(k, j, first, true, kind, x, y);
            }

            return true;
        }

        /// <summary>
        /// sub_13FD4: the node picture. Returns whether the cursor may select it. The PS4 draws the mosaic cell of the node (field +0x14) into the frame's
        /// inner 102 x 62 area where the Switch has a thumbnail atlas cell.
        /// </summary>
        public bool Node(int k, int j, bool gallery, float x, float y)
        {
            int phase = _rows[k].Phase;
            Rec rec = _rows[k].Recs[j];
            int n = rec.N.Scene;
            int edge = rec.N.Link;
            int kind = rec.N.Connector;
            if (n == 0)
            {
                return false;
            }

            if (n < 0)
            {
                if (Completed(-n) || !Edge(edge) || Read(-n) || (!gallery && Route(-n)) || kind == 3)
                {
                    return false;
                }
            }
            else if (!Completed(n) && !Edge(edge))
            {
                return false;
            }

            bool thumb;
            bool select;
            if (gallery)
            {
                bool c = n >= 1 && Completed(n);
                thumb = c;
                select = c;
            }
            else if (n < 1)
            {
                thumb = false;
                select = false;
            }
            else
            {
                bool rt = Route(n);
                if (rt)
                {
                    rec.Flags |= Lit;
                }

                thumb = Completed(n);
                select = phase > 0 && rt;
            }

            int t = rec.N.Mosaic;
            if (thumb && t >= 0)
            {
                var src = new RectF((MosaicCellW * (t % MosaicColumns)) + 1f, (MosaicCellH * (t / MosaicColumns)) + 1f, MosaicCellW - 2f, MosaicCellH - 2f);
                Sprites.Add(new ChartSprite(ChartSheet.Mosaic, new RectF(x + 1f, y + 1f, 102f, 62f), src, 400));
            }
            else
            {
                Push(ChartSheet.NodeFrame, x, y, new RectF(0, 0, NodeW, NodeH), 400);
            }

            return select;
        }

        // the first loop of sub_FFFC, kept for the shown rows
        public static List<Row> VisibleRows(IReadOnlyList<ChartColumnRow> columns, IUiProgress progress)
        {
            var kept = new List<Row>();
            foreach (int i in KeptColumns(columns, progress))
            {
                kept.Add(new Row(i, columns[i].Group, [.. columns[i].Nodes.Select(n => new Rec(n))]));
            }

            return kept;
        }
    }

    /// <summary>
    /// Lays out the chart (knife <c>build</c>).
    /// </summary>
    /// <param name="tables">The gallery tables (columns, chapter label cells, connector cells).</param>
    /// <param name="language">The UI language: English has its own label cells, drawn at x 2 instead of 208.</param>
    /// <param name="progress">The progress bits the chart is drawn from.</param>
    /// <param name="gallery">Opened from the title's gallery: no current playthrough (every completed node is selectable, no lit connectors).</param>
    public static FlowchartChart Build(IUiGalleryTables tables, Language language, IUiProgress progress, bool gallery)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(progress);
        List<Row> rows = Builder.VisibleRows(tables.ChartColumns, progress);
        var b = new Builder(progress, tables.ChartLineCells, rows);
        var nodes = new List<ChartNode>();
        var cellNodes = rows.Select(r => Enumerable.Repeat(-1, r.Recs.Count).ToArray()).ToList();
        var titles = new List<List<ChartSprite>>();
        var limits = new List<float>();
        var rowY = new List<float>();
        float y = 0f;
        float titleY = 0f;
        float bandTop = 0f;
        int phase = 0;
        int count = 0;
        int lastTitle = -1;
        bool english = language == Language.En;
        IReadOnlyList<SheetCell> titleCells = tables.ChartTitleCells(language);
        float titleX = english ? 2f : 208f;
        for (int k = 0; k < rows.Count; k++)
        {
            bool first = rows[k].Phase != phase;
            if (first)
            {
                bandTop = b.Band(bandTop, y + 8f);
                b.Divider(y + 8f);
                limits.Add(y + 16f);
                y += 13f;
                phase = rows[k].Phase;
                titleY = y;
                count = 0;
                lastTitle = -1;
            }

            float nodeY = y + 4f;
            rowY.Add(nodeY);
            int slot = rows[k].Phase;
            while (titles.Count <= slot)
            {
                titles.Add([]);
            }

            for (int j = 0; j < rows[k].Recs.Count; j++)
            {
                ChartNodeRow f = rows[k].Recs[j].N;
                int title = f.ChapterLabel;
                bool edgeSeen = f.Link >= 1 && progress.EdgeSeen(f.Link);
                if (title >= 0 && count <= 2 && title > lastTitle && edgeSeen)
                {
                    titles[slot].Add(TitleSprite(titleCells, titleX, titleY + 8f, title));
                    titleY += 32f;
                    lastTitle = title;
                    if (title != 0)
                    {
                        count++;
                    }
                    else
                    {
                        // the first chapter also shows the "Prologue" label (the last non-empty cell of the table)
                        titles[slot].Add(TitleSprite(titleCells, titleX, titleY + 8f, PrologueCell(titleCells)));
                        titleY += 32f;
                        count += 2;
                    }
                }

                float x = (j * 96f) + 438f;
                if (b.Node(k, j, gallery, x, nodeY))
                {
                    cellNodes[k][j] = nodes.Count;
                    nodes.Add(new ChartNode(f.Scene, x, nodeY, f.Preview, f.TitleUi, f.SynopsisUi));
                }

                if (b.Grey(k, j, first, x, nodeY - 5f))
                {
                    rows[k].Recs[j].Flags |= Drawn;
                }

                if (!gallery && b.LitLine(k, j, first, x, nodeY - 5f))
                {
                    rows[k].Recs[j].Flags |= Lit;
                }
            }

            y += 56f;
        }

        float end = MathF.Max(y, titleY) + 8f;
        b.Band(bandTop, end);
        b.Sprites.Add(new ChartSprite(ChartSheet.BandUnder, new RectF(0, end, 1440, 5), new RectF(0, 0, 1440, 5), 450));

        // farther pieces first (stable: pieces of equal priority keep their order)
        List<ChartSprite> sprites = [.. b.Sprites.Select((s, i) => (s, i)).OrderByDescending(t => t.s.Prio).ThenBy(t => t.i).Select(t => t.s)];
        float scrollMax = end + 56f + 4f - 1022f;
        if (scrollMax - 28f <= 1022f)
        {
            scrollMax -= 28f;
        }

        if (rows.Count > 0 && rows[^1].Index == 276)
        {
            scrollMax -= 56f;
        }

        var grid = new List<GridRow>(rows.Count);
        for (int k = 0; k < rows.Count; k++)
        {
            var cells = new List<(ChartNodeRow N, int Node)>(rows[k].Recs.Count);
            for (int j = 0; j < rows[k].Recs.Count; j++)
            {
                cells.Add((rows[k].Recs[j].N, cellNodes[k][j]));
            }

            grid.Add(new GridRow(rows[k].Index, rows[k].Phase, cells));
        }

        return new FlowchartChart(sprites, nodes, titles, limits, rowY, MathF.Max(scrollMax, 0f), grid, progress, gallery);
    }

    private static int PrologueCell(IReadOnlyList<SheetCell> cells)
    {
        for (int i = cells.Count - 1; i >= 0; i--)
        {
            if (cells[i].Width > 0 && cells[i].Height > 0)
            {
                return i;
            }
        }

        return 0;
    }

    private static ChartSprite TitleSprite(IReadOnlyList<SheetCell> cells, float x, float y, int title)
    {
        SheetCell c = title >= 0 && title < cells.Count ? cells[title] : default;
        var src = new RectF(c.X0, c.Y0, c.Width, c.Height);
        return new ChartSprite(ChartSheet.Title, new RectF(x, y, src.W, src.H), src, 430);
    }

    // ---- the cursor -------------------------------------------------------------------------------------------------------------------------------

    private int Count(int k) => k >= 0 && k < _grid.Count ? _grid[k].Cells.Count : 0;

    private (ChartNodeRow N, int Node)? CellAt(ChartCell c) =>
        c.Row >= 0 && c.Row < _grid.Count && c.Col >= 0 && c.Col < _grid[c.Row].Cells.Count ? _grid[c.Row].Cells[c.Col] : null;

    /// <summary>The record under a cell as the action on it sees it, or null.</summary>
    public ChartRecord? RecordAt(ChartCell c) => CellAt(c) is { } cell ? new ChartRecord(_grid[c.Row].Index, cell.N.Scene, cell.N.ScriptScene) : null;

    /// <summary>The selectable node of a cell, or -1.</summary>
    public int NodeAt(ChartCell c) => CellAt(c) is { } cell ? cell.Node : -1;

    /// <summary>The cell of selectable node <paramref name="node"/> (an index into <see cref="Nodes"/>), or null.</summary>
    public ChartCell? CellOf(int node)
    {
        for (int k = 0; k < _grid.Count; k++)
        {
            for (int j = 0; j < _grid[k].Cells.Count; j++)
            {
                if (_grid[k].Cells[j].Node == node)
                {
                    return new ChartCell(j, k);
                }
            }
        }

        return null;
    }

    private bool HasSprite(ChartCell c) => NodeAt(c) >= 0;

    private bool Done(int n) => n >= 1 && _progress.SceneDone(n);

    private bool EdgeOk(ChartCell c) => CellAt(c) is { } r && r.N.Link >= 1 && _progress.EdgeSeen(r.N.Link);

    /// <summary>
    /// sub_F8FC: the cursor on open. (0, 0), then every shown row's first record with id <paramref name="id"/> (when id >= 0), the last one found winning; each
    /// match runs <paramref name="follow"/> (the scroll follow) at once.
    /// </summary>
    public ChartCell OpenCursor(int id, Action<ChartCell>? follow = null)
    {
        var cur = new ChartCell(0, 0);
        if (id >= 0)
        {
            for (int k = 0; k < _grid.Count; k++)
            {
                int j = _grid[k].Cells.FindIndex(c => c.N.Scene == id);
                if (j >= 0)
                {
                    cur = new ChartCell(j, k);
                    follow?.Invoke(cur);
                }
            }
        }

        return cur;
    }

    /// <summary>
    /// sub_1303C: keeps the column when its record has a sprite, else the nearest one with a sprite within 8 (right first). With <paramref name="wide"/> false the
    /// record's side of the chart is kept: columns 0..3 look only within 0..3 (3 away at most), 4+ only within 4+.
    /// </summary>
    private int? Snap(ChartCell cell, bool wide)
    {
        int n = Count(cell.Row);
        bool Ok(int c) => c >= 0 && c < n && HasSprite(new ChartCell(c, cell.Row));
        if (Ok(cell.Col))
        {
            return cell.Col;
        }

        (int reach, int lo, int hi) = wide ? (8, 0, n) : cell.Col >= 4 ? (8, 4, n) : (3, 0, 4);
        for (int d = 1; d <= reach; d++)
        {
            foreach (int c in new[] { cell.Col + d, cell.Col - d })
            {
                if (c >= lo && c < hi && Ok(c))
                {
                    return c;
                }
            }
        }

        return null;
    }

    private bool OkCell(ChartCell c) => c.Row >= 0 && c.Row < _grid.Count && HasSprite(c);

    /// <summary>sub_1262C: left along the row, else one or two columns left in the row below, then above.</summary>
    public ChartCell? MoveLeft(ChartCell cell)
    {
        (int j, int k) = (cell.Col, cell.Row);
        for (int c = j - 1; c >= 0; c--)
        {
            if (HasSprite(new ChartCell(c, k)))
            {
                return new ChartCell(c, k);
            }
        }

        int last = Rows - 1;
        if (k < last)
        {
            if (j < 1)
            {
                return null;
            }

            foreach (int c in new[] { j - 1, j - 2 })
            {
                if (c < 0)
                {
                    break;
                }

                if (OkCell(new ChartCell(c, k + 1)))
                {
                    return new ChartCell(c, k + 1);
                }

                if (k >= 1 && OkCell(new ChartCell(c, k - 1)))
                {
                    return new ChartCell(c, k - 1);
                }
            }

            return null;
        }

        if (k <= 0)
        {
            return null;
        }

        foreach (int c in new[] { j - 1, j - 2 })
        {
            if (c >= 0 && OkCell(new ChartCell(c, k - 1)))
            {
                return new ChartCell(c, k - 1);
            }
        }

        return null;
    }

    /// <summary>sub_1280C: right along the row, else one or two columns right (from columns 0..7 / 0..6) in the row below, then above.</summary>
    public ChartCell? MoveRight(ChartCell cell)
    {
        (int j, int k) = (cell.Col, cell.Row);
        for (int c = j + 1; c < Count(k); c++)
        {
            if (HasSprite(new ChartCell(c, k)))
            {
                return new ChartCell(c, k);
            }
        }

        int last = Rows - 1;
        if (k < last)
        {
            foreach ((int c, int max) in new[] { (j + 1, 7), (j + 2, 6) })
            {
                if (j > max)
                {
                    return null;
                }

                if (OkCell(new ChartCell(c, k + 1)))
                {
                    return new ChartCell(c, k + 1);
                }

                if (k >= 1 && OkCell(new ChartCell(c, k - 1)))
                {
                    return new ChartCell(c, k - 1);
                }
            }

            return null;
        }

        if (k <= 0)
        {
            return null;
        }

        foreach ((int c, int max) in new[] { (j + 1, 7), (j + 2, 6) })
        {
            if (j <= max && OkCell(new ChartCell(c, k - 1)))
            {
                return new ChartCell(c, k - 1);
            }
        }

        return null;
    }

    /// <summary>In game: the first node of this playthrough (RTST, with a sprite) in the nearest row above / below; up stops at the prologue (phase 0).</summary>
    private ChartCell? RouteStep(int k, int dy)
    {
        int r = k;
        while (true)
        {
            r += dy;
            if (r < 0 || r >= Rows || (dy < 0 && _grid[r].Phase == 0))
            {
                return null;
            }

            int j = _grid[r].Cells.FindIndex(c => c.N.Scene >= 1 && _progress.RouteScene(c.N.Scene) && c.Node >= 0);
            if (j >= 0)
            {
                return new ChartCell(j, r);
            }

            if ((dy < 0 && r < 1) || (dy > 0 && r >= Rows - 1))
            {
                return null;
            }
        }
    }

    /// <summary>
    /// sub_129F4: up. In the gallery the lines are followed up to the node they come from (sub_12EAC), else the cursor steps a row up and snaps (sub_1303C(0)).
    /// </summary>
    public ChartCell? MoveUp(ChartCell cell)
    {
        (int j, int k) = (cell.Col, cell.Row);
        if (k < 1)
        {
            return null;
        }

        if (!_gallery)
        {
            return RouteStep(k, -1);
        }

        (int col, int row) = (j, k);
        while (true)
        {
            if (TraceUp(col, row) is { } c)
            {
                return c;
            }

            row--;
            if (Snap(new ChartCell(col, row), false) is { } s)
            {
                return new ChartCell(s, row);
            }

            if (row <= 0)
            {
                return null;
            }

            col = j;
        }
    }

    /// <summary>
    /// sub_12B68: down. In the gallery the lines are followed down to the node they lead to (sub_136D0) or the next row snaps (sub_1303C(0)); failing every row,
    /// the rows below snap with any column (sub_1303C(1)).
    /// </summary>
    public ChartCell? MoveDown(ChartCell cell)
    {
        (int j, int k) = (cell.Col, cell.Row);
        int last = Rows - 1;
        if (!_gallery)
        {
            return k >= last ? null : RouteStep(k, 1);
        }

        int row = k;
        while (row < last)
        {
            row++;
            if (TraceDown(j, row) is { } c)
            {
                return c;
            }

            if (Snap(new ChartCell(j, row), false) is { } s)
            {
                return new ChartCell(s, row);
            }
        }

        row = k;
        while (row < last)
        {
            row++;
            if (Snap(new ChartCell(j, row), true) is { } s)
            {
                return new ChartCell(s, row);
            }
        }

        return null;
    }

    /// <summary>sub_12EAC: follow the line through cell (j, k) up to a finished node in the row above it.</summary>
    private ChartCell? TraceUp(int j, int k)
    {
        while (true)
        {
            if (k < 0 || j < 0 || j >= Count(k) || !EdgeOk(new ChartCell(j, k)))
            {
                return null;
            }

            if (k >= 1 && j < Count(k - 1) && CellAt(new ChartCell(j, k - 1)) is { } above && Done(above.N.Scene))
            {
                return new ChartCell(j, k - 1);
            }

            switch (CellAt(new ChartCell(j, k))!.Value.N.Connector)
            {
                case 2 or 5 or 8:
                    if (TraceUp(j + 1, k) is { } r1)
                    {
                        return r1;
                    }

                    k--;
                    break;
                case 3:
                    k--;
                    break;
                case 4 or 7:
                    j++;
                    break;
                case 6:
                    return TraceUp(j, k - 1) ?? TraceUpLeft(j - 1, k);
                case 9:
                    return TraceUpLeft(j - 1, k);
                case 10:
                    if (TraceUpLeft(j - 1, k) is { } r2)
                    {
                        return r2;
                    }

                    k--;
                    break;
                case 11:
                    return TraceUp(j + 1, k) ?? TraceUpLeft(j - 1, k);
                default:
                    return null;
            }
        }
    }

    /// <summary>sub_13854: follow a line leftwards along row k, then up (sub_12EAC).</summary>
    private ChartCell? TraceUpLeft(int j, int k)
    {
        while (true)
        {
            if (j < 0 || j >= Count(k) || !EdgeOk(new ChartCell(j, k)))
            {
                return null;
            }

            if (k > 0 && j < Count(k - 1) && CellAt(new ChartCell(j, k - 1)) is { } above && Done(above.N.Scene))
            {
                return new ChartCell(j, k - 1);
            }

            switch (CellAt(new ChartCell(j, k))!.Value.N.Connector)
            {
                case 2 or 3 or 5:
                    return TraceUp(j, k - 1);
                case 7 or 11:
                    j--;
                    break;
                case 8:
                    return TraceUpLeft(j - 1, k) ?? TraceUp(j, k - 1);
                default:
                    return null;
            }
        }
    }

    /// <summary>sub_136D0: follow the line through cell (j, k) down to a finished node.</summary>
    private ChartCell? TraceDown(int j, int k)
    {
        while (true)
        {
            if (k >= Rows || j < 0 || j >= Count(k))
            {
                return null;
            }

            (ChartNodeRow n, _) = CellAt(new ChartCell(j, k))!.Value;
            int id = n.Scene;
            if (Done(id))
            {
                return new ChartCell(j, k);
            }

            if (!EdgeOk(new ChartCell(j, k)))
            {
                return null;
            }

            switch (n.Connector)
            {
                case 2 or 7:
                    j++;
                    break;
                case 3 or 9:
                    k++;
                    break;
                case 4:
                    if (id < 0 && Done(-id))
                    {
                        j++;
                    }
                    else
                    {
                        k++;
                    }

                    break;
                case 5 or 11:
                    if (TraceDown(j + 1, k) is { } r)
                    {
                        return r;
                    }

                    k++;
                    break;
                case 6 or 8:
                    return TraceDownLeft(j - 1, k);
                case 10:
                    if (id < 0 && Done(-id))
                    {
                        k++;
                    }
                    else
                    {
                        return TraceDownLeft(j - 1, k);
                    }

                    break;
                default:
                    return null;
            }
        }
    }

    /// <summary>sub_139F4: follow a line leftwards, then down (sub_136D0).</summary>
    private ChartCell? TraceDownLeft(int j, int k)
    {
        while (true)
        {
            if (j < 0 || k >= Rows)
            {
                return null;
            }

            if (CellAt(new ChartCell(j, k)) is not { } cell)
            {
                return null;
            }

            int id = cell.N.Scene;
            if (Done(id))
            {
                return new ChartCell(j, k);
            }

            if (!EdgeOk(new ChartCell(j, k)))
            {
                return null;
            }

            switch (cell.N.Connector)
            {
                case 11:
                    if (TraceDownLeft(j - 1, k) is { } r)
                    {
                        return r;
                    }

                    if (id >= 0 || !Done(-id))
                    {
                        return TraceDown(j, k + 1);
                    }

                    k++;
                    break;
                case 3 or 4 or 5:
                    return TraceDown(j, k + 1);
                case 7 or 8:
                    j--;
                    break;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// sub_12D1C, after a manual scroll: the cursor walks down a row at a time while its node is above view y 4, then up while it is at view y 994 or below; each
    /// row snaps with sub_1303C(0), else (1). <paramref name="y"/> gives the view y of a node. Null when the row did not change.
    /// </summary>
    public ChartCell? KeepInView(ChartCell cell, Func<int, float> y)
    {
        ArgumentNullException.ThrowIfNull(y);
        int last = Rows - 1;
        int col = cell.Col;
        int row = cell.Row;

        bool Settle(ref int c, int r, bool below)
        {
            int? s = Snap(new ChartCell(c, r), false) ?? Snap(new ChartCell(c, r), true);
            if (s is not { } sc)
            {
                return false;
            }

            c = sc;
            int node = NodeAt(new ChartCell(sc, r));
            return node >= 0 && (below ? y(node) < 994f : y(node) >= 4f);
        }

        if (row < last)
        {
            while (!Settle(ref col, row, false))
            {
                row++;
                if (row >= last)
                {
                    break;
                }
            }
        }

        if (row >= 1)
        {
            while (!Settle(ref col, row, true))
            {
                row--;
                if (row <= 0)
                {
                    break;
                }
            }
        }

        return row != cell.Row ? new ChartCell(col, row) : null;
    }
}

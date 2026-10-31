namespace Tsuki.Data.Eboot.Tables;

/// <summary>One node of the flowchart (a 64-byte record; the first nine <c>i32</c> are static, the rest are run-time state and zero in the file).</summary>
/// <param name="Column">Static column index (0..276).</param>
/// <param name="Slot">Row inside the column.</param>
/// <param name="Scene">+0x00: positive = the RTST bit (scene_done unlocks the node), negative = a link marker to -Scene, 0 = empty.</param>
/// <param name="Connector">+0x04: connector kind, -1 none (index into the flow_line cells).</param>
/// <param name="Link">+0x08: choice edge flag id 564..1194 (edge_seen), -1 none.</param>
/// <param name="ScriptScene">+0x0C: the scene id the replay starts (the _JUMP target).</param>
/// <param name="ChapterLabel">+0x10: cell index into the flow_phasetitle tables.</param>
/// <param name="Order">+0x14: thumbnail/ordering index (no reader found in the PS4 code).</param>
/// <param name="Thumbnail">+0x18: GALLERY_PARTS_FC_TH child (scene - 1 for real nodes).</param>
/// <param name="TitleUi">+0x1C: ui_text id of the scene title, -1 none.</param>
/// <param name="SynopsisUi">+0x20: ui_text id of the synopsis, -1 none.</param>
public sealed record FlowNode(
    int Column, int Slot, int Scene, int Connector, int Link, int ScriptScene, int ChapterLabel, int Order, int Thumbnail, int TitleUi, int SynopsisUi);

/// <summary>A flowchart column.</summary>
/// <param name="Index">Column index (0..276).</param>
/// <param name="Group">+0x04: chapter band 0..15 (the look-ahead of the column filter stops at a higher group).</param>
/// <param name="Nodes">The nodes (the record's count at +0x08).</param>
public sealed record FlowColumn(int Index, int Group, IReadOnlyList<FlowNode> Nodes);

/// <summary>A scene-transition edge: the flags set when the story goes from node <c>From</c> to node <c>To</c>.</summary>
/// <param name="From">Source node.</param>
/// <param name="To">Target node.</param>
/// <param name="GlobalIds">Edge ids (564..1194) set in the global progress words (positive entries of fields 2..6).</param>
/// <param name="PlaythroughIds">Edge ids set in the playthrough words F600+ (positive entries of fields 7..11).</param>
public sealed record FlowEdge(int From, int To, IReadOnlyList<int> GlobalIds, IReadOnlyList<int> PlaythroughIds);

/// <summary>
/// The flowchart tables (docs/formats/gallery.md 5; knife's <c>flow_table.rs</c> <c>ROWS</c>, <c>EDGES</c>): 277 columns (RELRO 0x418770, 32 bytes) with
/// 1,286 nodes (text 0x18FC90..0x1A3E10, 64 bytes), 630 edges (text 0x2218D0, 0x30 bytes) and 83 choice edge flag triples (text 0x2214E0). The knife
/// cross-check (docs/research/knife_tables.md) found all 1,286 nodes equal in seven fields and in the two SYSMES fields after the +1 rule (<see cref="DialogMessageTable.Ps4SysmesId"/>),
/// and 628 of knife's 629 edges. Port of the flowchart half of <c>tsuki.misc_gallery_extras_flowchart.load</c>.
/// </summary>
public sealed class FlowchartTables
{
    /// <summary>RELRO VA of the 277 column records.</summary>
    public const long ColumnVa = 0x418770;

    /// <summary>Number of columns.</summary>
    public const int ColumnCount = 277;

    /// <summary>Text VA of the 630 edge records.</summary>
    public const long EdgeVa = 0x2218D0;

    /// <summary>Number of edge records.</summary>
    public const int EdgeCount = 630;

    /// <summary>Text VA of the choice edge flag triples.</summary>
    public const long ChoiceFlagVa = 0x2214E0;

    /// <summary>Number of triples; choice ids 1..82 are valid, entry 0 is unused.</summary>
    public const int ChoiceFlagCount = 83;

    private FlowchartTables(IReadOnlyList<FlowColumn> columns, IReadOnlyList<FlowEdge> edges, IReadOnlyList<(int A, int B, int C)> choiceFlags)
    {
        Columns = columns;
        Edges = edges;
        ChoiceFlags = choiceFlags;
    }

    /// <summary>The 277 columns.</summary>
    public IReadOnlyList<FlowColumn> Columns { get; }

    /// <summary>All 1,286 nodes in column order.</summary>
    public IEnumerable<FlowNode> Nodes => Columns.SelectMany(c => c.Nodes);

    /// <summary>The 630 edge records in table order (the engine uses the first match).</summary>
    public IReadOnlyList<FlowEdge> Edges { get; }

    /// <summary>The 83 choice edge flag triples by choice id (the three i32 of each record, as read).</summary>
    public IReadOnlyList<(int A, int B, int C)> ChoiceFlags { get; }

    /// <summary>Reads the tables.</summary>
    /// <exception cref="EbootFormatException">A node still holds run-time state, or a pointer is not file-backed.</exception>
    public static FlowchartTables Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        EbootLayout layout = eboot.Layout;
        List<FlowColumn> columns = [];
        for (int c = 0; c < layout.FlowColumnCount; c++)
        {
            long va = layout.FlowColumns + (32L * c);
            int group = eboot.I32(va + 4);
            int count = eboot.I32(va + 8);
            long nodesVa = GalleryTables.Pointer(eboot, va + 16);
            List<FlowNode> nodes = [];
            for (int j = 0; j < count; j++)
            {
                long nva = nodesVa + (64L * j);
                int[] f = new int[16];
                for (int k = 0; k < 16; k++)
                {
                    f[k] = eboot.I32(nva + (4L * k));
                }

                if (f[9..].Any(v => v != 0))
                {
                    throw new EbootFormatException($"flow node {c}/{j}: run-time fields are not zero");
                }

                nodes.Add(new FlowNode(c, j, f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8]));
            }

            columns.Add(new FlowColumn(c, group, nodes));
        }

        List<FlowEdge> edges = [];
        for (int i = 0; i < layout.FlowEdgeCount; i++)
        {
            long va = layout.FlowEdges + (0x30L * i);
            int[] f = new int[12];
            for (int k = 0; k < 12; k++)
            {
                f[k] = eboot.I32(va + (4L * k));
            }

            edges.Add(new FlowEdge(f[0], f[1], [.. f[2..7].Where(v => v > 0)], [.. f[7..12].Where(v => v > 0)]));
        }

        List<(int, int, int)> choice = [];
        for (int i = 0; i < layout.ChoiceFlagCount; i++)
        {
            long va = layout.ChoiceFlags + (12L * i);
            choice.Add((eboot.I32(va), eboot.I32(va + 4), eboot.I32(va + 8)));
        }

        return new FlowchartTables(columns, edges, choice);
    }
}

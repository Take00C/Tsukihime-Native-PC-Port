using Tsuki.Engine.Text;

namespace Tsuki.Engine.Contracts;

/// <summary>The eboot tables of the game interpreter (package K3; docs/research/knife_tables.md, docs/formats/gallery.md 2, 5).</summary>
public partial interface IEbootTables
{
    /// <summary>The glyph lookup and rectangle tables the text layout reads (docs/formats/fonts.md 5).</summary>
    FontTables GameFonts { get; }

    /// <summary>The scene table and the flowchart data the interpreter needs for <c>_JUMP</c> and the flowchart marks.</summary>
    IGameFlowTables GameFlow { get; }
}

/// <summary>A scene-transition edge of the flowchart: the edge flags a story step from node <c>From</c> to node <c>To</c> sets.</summary>
/// <param name="From">Source node.</param>
/// <param name="To">Target node.</param>
/// <param name="GlobalIds">Edge ids set in the global progress words (G400+).</param>
/// <param name="PlaythroughIds">Edge ids set in the playthrough words (F600+).</param>
public sealed record FlowEdgeRow(int From, int To, IReadOnlyList<int> GlobalIds, IReadOnlyList<int> PlaythroughIds);

/// <summary>A node of the flowchart (a 64-byte record of text VA 0x18FC90).</summary>
/// <param name="Column">The flowchart column (0..276).</param>
/// <param name="Scene">The RTST bit of the node (positive), a link marker (negative) or 0.</param>
/// <param name="ScriptScene">The scene id the replay of the node starts (the <c>_JUMP</c> target).</param>
/// <param name="Link">The choice edge flag id 564..1194 of the node, -1 none.</param>
/// <param name="Group">The chapter band of the node's column (0 for the first chapter's columns).</param>
public sealed record FlowNodeRow(int Column, int Scene, int ScriptScene, int Link = -1, int Group = 0);

/// <summary>
/// The scene and flowchart tables of the game interpreter. Everything is read once from the user's eboot and immutable
/// (K1b's readers; docs/research/knife_tables.md).
/// </summary>
public interface IGameFlowTables
{
    /// <summary>Number of scripts (549).</summary>
    int SceneCount { get; }

    /// <summary>The scene id of script <paramref name="script"/> (<c>_JUMP</c> argument 0).</summary>
    int SceneId(int script);

    /// <summary>The script number of a scene id, or -1 when the table has none (script.md 6.1).</summary>
    int ScriptOfSceneId(int sceneId);

    /// <summary>The flowchart node of script <paramref name="script"/> (the RTST bit), -1 when it has none.</summary>
    int NodeOfScript(int script);

    /// <summary>
    /// The picture-seen bit of picture <paramref name="pictureId"/> (gallery.md 2.2: a <c>_HH_VV</c> variant counts as its base picture, ids from 0xA8D go
    /// through the seen map, bits 0..3199), or -1.
    /// </summary>
    int PictureSeenIndex(int pictureId);

    /// <summary>The 630 edges of the flowchart.</summary>
    IReadOnlyList<FlowEdgeRow> Edges { get; }

    /// <summary>The flowchart nodes (1,286).</summary>
    IReadOnlyList<FlowNodeRow> Nodes { get; }

    /// <summary>F words a flowchart jump keeps whole.</summary>
    IReadOnlyList<int> JumpKeepWords { get; }

    /// <summary>F words a flowchart jump keeps as a signed byte.</summary>
    IReadOnlyList<int> JumpKeepBytes { get; }

    /// <summary>F words a flowchart jump keeps as a flag.</summary>
    IReadOnlyList<int> JumpKeepBits { get; }

    /// <summary>(last column, F word) pairs: a jump to a column at or before the last column zeroes the word.</summary>
    IReadOnlyList<(int LastColumn, int Word)> JumpClears { get; }
}

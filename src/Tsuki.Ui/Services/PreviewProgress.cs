using Tsuki.Engine.Contracts;
using Tsuki.Ui.Screens;

namespace Tsuki.Ui;

/// <summary>
/// The unlock bits of a <see cref="PreviewServices"/> game: plain sets, with helpers that build a believable player from the eboot tables (a quarter of the
/// story read, some of its CGs and music seen) for the screenshot tool and the tests.
/// </summary>
public sealed class PreviewProgress : IUiProgress
{
    /// <summary>scene_seen bits.</summary>
    public HashSet<int> Seen { get; } = [];

    /// <summary>scene_done bits.</summary>
    public HashSet<int> Done { get; } = [];

    /// <summary>edge_seen ids (564..1267).</summary>
    public HashSet<int> Edges { get; } = [];

    /// <summary>The nodes of this playthrough (RTST).</summary>
    public HashSet<int> Route { get; } = [];

    /// <summary>The edges of this playthrough.</summary>
    public HashSet<int> RouteEdges { get; } = [];

    /// <summary>movie_seen bits.</summary>
    public HashSet<int> Movies { get; } = [];

    /// <summary>picture_seen bits (0..3199).</summary>
    public HashSet<int> Pictures { get; } = [];

    /// <summary>bgm_heard tracks.</summary>
    public HashSet<int> Bgm { get; } = [];

    /// <inheritdoc/>
    public bool SceneSeen(int n) => Seen.Contains(n);

    /// <inheritdoc/>
    public bool SceneDone(int n) => Done.Contains(n);

    /// <inheritdoc/>
    public bool EdgeSeen(int id) => Edges.Contains(id);

    /// <inheritdoc/>
    public bool MovieSeen(int bit) => Movies.Contains(bit);

    /// <inheritdoc/>
    public bool PictureSeen(int seenIndex) => Pictures.Contains(seenIndex);

    /// <inheritdoc/>
    public bool BgmHeard(int track) => Bgm.Contains(track);

    /// <inheritdoc/>
    public bool RouteScene(int n) => Route.Contains(n);

    /// <inheritdoc/>
    public bool RouteEdge(int id) => RouteEdges.Contains(id);

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        Seen.Clear();
        Done.Clear();
        Edges.Clear();
        Route.Clear();
        RouteEdges.Clear();
        Movies.Clear();
        Pictures.Clear();
        Bgm.Clear();
    }

    /// <summary>
    /// A player who has read the flowchart scenes 1 to <paramref name="lastScene"/> in one playthrough: those scenes are started, finished and entered, the edges
    /// between them taken, the CGs of the first chapters and a share of the music seen, both openings watched.
    /// </summary>
    /// <param name="tables">The gallery tables.</param>
    /// <param name="lastScene">The last scene read (the table has scenes 1 to 495).</param>
    /// <param name="share">The share of the reachable CGs and tracks that were seen, 0..1.</param>
    public void ReadTo(IUiGalleryTables tables, int lastScene, float share = 0.6f)
    {
        ArgumentNullException.ThrowIfNull(tables);
        Clear();
        // the prologue scenes 485 and 486 come first, then scenes 1 to lastScene
        foreach (int n in Enumerable.Range(1, lastScene).Concat([485, 486]))
        {
            Seen.Add(n);
            Done.Add(n);
            Route.Add(n);
        }

        // the edges between the scenes read; the hidden nodes (497 and up) are crossed on the way, so an edge into one is taken and the hidden node counts as passed
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (ChartEdgeRow e in tables.ChartEdges)
            {
                bool from = Done.Contains(e.From);
                bool to = Done.Contains(e.To) || (e.To >= 497 && from);
                if (from && to)
                {
                    foreach (int id in e.GlobalIds)
                    {
                        Edges.Add(id);
                    }

                    foreach (int id in e.PlaythroughIds)
                    {
                        RouteEdges.Add(id);
                    }

                    if (e.To >= 497)
                    {
                        Done.Add(e.To);
                        Seen.Add(e.To);
                        Route.Add(e.To);
                    }
                }
            }
        }

        Movies.Add(0);
        Movies.Add(1);
        var rng = new UiRandom(11);
        GalleryModel model = GalleryModel.For(tables);
        foreach (IReadOnlyList<GalleryCg> group in model.Groups)
        {
            foreach (GalleryCg cg in group)
            {
                if (model.CategoryOpen(cg.Category, this) && cg.DirectSeen >= 0 && rng.Next() < share)
                {
                    Pictures.Add(cg.DirectSeen);
                }
            }
        }

        for (int i = 0; i < tables.Tracks.Count; i++)
        {
            if (rng.Next() < share)
            {
                Bgm.Add(tables.Tracks[i].Bgm);
            }
        }
    }

    /// <summary>A player who has finished everything: every scene, edge, movie, picture and track.</summary>
    public void Everything(IUiGalleryTables tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ReadTo(tables, 560, 1f);
        for (int n = 1; n <= 0x23F; n++)
        {
            Seen.Add(n);
            Done.Add(n);
        }

        for (int id = 564; id <= 1267; id++)
        {
            Edges.Add(id);
        }
    }
}

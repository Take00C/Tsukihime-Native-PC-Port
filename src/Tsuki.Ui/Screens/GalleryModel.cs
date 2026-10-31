using System.Runtime.CompilerServices;
using Tsuki.Engine.Contracts;

namespace Tsuki.Ui.Screens;

/// <summary>A CG of the gallery with the picture-seen bits that unlock it resolved once (docs/formats/gallery.md 4.3).</summary>
/// <param name="Group">Group index (0..457).</param>
/// <param name="Index">Position inside the group.</param>
/// <param name="Row">The table record (name, thumbnail child, source rectangle).</param>
/// <param name="Category">The group's unlock category.</param>
/// <param name="DirectSeen">The seen bit of the picture itself (names_sprites lookup), or -1 for a gallery-only picture.</param>
/// <param name="AliasSeen">The seen bits of the alias pictures that unlock it (the first resolvable alias decides, earlier ones are tried with every size letter).</param>
/// <param name="LanguageStrip">The picture stacks one strip per language: the source rectangle moves down by its height times the language.</param>
public sealed record GalleryCg(int Group, int Index, GalleryCgRow Row, int Category, int DirectSeen, IReadOnlyList<int> AliasSeen, bool LanguageStrip);

/// <summary>
/// The unlock rules of the gallery, the music player, the movie list and the ending list (docs/formats/gallery.md 2.3, 4.3, 6.2, 7, 8) over the eboot tables
/// and an <see cref="IUiProgress"/>. Name lookups are resolved once when the model is created; every query afterwards is bit tests only.
/// </summary>
public sealed class GalleryModel
{
    /// <summary>names_sprites is the first 9,958 picture names (gallery.md 2.2): a CG whose picture is beyond them is gallery-only.</summary>
    public const int SpriteNameCount = 9958;

    /// <summary>The scene whose completion (QA_25) unlocks every CG (gallery.md 4.3).</summary>
    public const int AllCgScene = 557;

    /// <summary>The movie gallery items: the movie table index and the movie_seen bit that unlocks it (null: always), gallery.md 7.</summary>
    public static readonly IReadOnlyList<(int Movie, int? Bit)> Movies = [(4, null), (5, 0), (6, 1)];

    private static readonly ConditionalWeakTable<IUiGalleryTables, GalleryModel> Cache = [];

    private GalleryModel(IUiGalleryTables tables)
    {
        Tables = tables;
        var groups = new List<IReadOnlyList<GalleryCg>>(tables.CgGroups.Count);
        var aliases = new Dictionary<string, GalleryAliasRow>(StringComparer.Ordinal);
        foreach (GalleryAliasRow a in tables.CgAliases)
        {
            aliases[a.Key] = a;
        }

        var strips = new HashSet<string>(tables.CgLanguageStrips.Select(Stem), StringComparer.OrdinalIgnoreCase);
        for (int g = 0; g < tables.CgGroups.Count; g++)
        {
            GalleryGroupRow group = tables.CgGroups[g];
            var list = new List<GalleryCg>(group.Entries.Count);
            for (int k = 0; k < group.Entries.Count; k++)
            {
                GalleryCgRow row = group.Entries[k];
                int direct = Seen(SpriteIndex(row.Name));
                IReadOnlyList<int> alias = aliases.TryGetValue(row.Name, out GalleryAliasRow? a) ? AliasPlan(a) : [];
                list.Add(new GalleryCg(g, k, row, group.Category, direct, alias, strips.Contains(Stem(row.Name))));
            }

            groups.Add(list);
        }

        Groups = groups;
    }

    /// <summary>The tables the model reads.</summary>
    public IUiGalleryTables Tables { get; }

    /// <summary>The CGs by group (458 groups, 1,972 CGs).</summary>
    public IReadOnlyList<IReadOnlyList<GalleryCg>> Groups { get; }

    /// <summary>The model of <paramref name="tables"/> (built once per table set).</summary>
    public static GalleryModel For(IUiGalleryTables tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return Cache.GetValue(tables, t => new GalleryModel(t));
    }

    private static string Stem(string name)
    {
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot >= 0 ? name[..dot] : name;
    }

    private int SpriteIndex(string name)
    {
        int i = Tables.PictureIndex(name);
        return i is >= 0 and < SpriteNameCount ? i : -1;
    }

    private int Seen(int pictureId) => pictureId < 0 ? -1 : Tables.PictureSeenIndex(pictureId);

    // gallery.md 4.3 alias_seen: aliases are tried in order; the first one that resolves (or is shorter than 6 characters) decides alone, the ones before it are
    // tried with each size letter at index 4
    private List<int> AliasPlan(GalleryAliasRow alias)
    {
        var plan = new List<int>();
        foreach (string al in alias.Aliases)
        {
            int i = SpriteIndex(al);
            if (i >= 0 || al.Length < 6)
            {
                plan.Add(Seen(i));
                break;
            }

            foreach (char letter in "ALMS")
            {
                plan.Add(Seen(Tables.PictureIndex(al[..4] + letter + al[5..])));
            }
        }

        plan.RemoveAll(x => x < 0);
        return plan;
    }

    // ---- CG --------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The category's gate (gallery.md 4.4): category 0 is always open, the others when any of their scenes was started.</summary>
    public bool CategoryOpen(int category, IUiProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return !Tables.CgCategoryScenes.TryGetValue(category, out IReadOnlyList<int>? scenes) || scenes.Any(progress.SceneSeen);
    }

    /// <summary>The CG is unlocked (0x13630): its category is open and the picture or an alias was shown, or QA_25 was finished.</summary>
    public bool IsUnlocked(GalleryCg cg, IUiProgress progress)
    {
        ArgumentNullException.ThrowIfNull(cg);
        ArgumentNullException.ThrowIfNull(progress);
        return CategoryOpen(cg.Category, progress) && (cg.AliasSeen.Any(progress.PictureSeen) || (cg.DirectSeen >= 0 && progress.PictureSeen(cg.DirectSeen)) || progress.SceneDone(AllCgScene));
    }

    /// <summary>The unlocked entry indices of every group (an empty list for a locked group).</summary>
    public int[][] Unlocked(IUiProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var result = new int[Groups.Count][];
        for (int g = 0; g < Groups.Count; g++)
        {
            result[g] = [.. Groups[g].Where(cg => IsUnlocked(cg, progress)).Select(cg => cg.Index)];
        }

        return result;
    }

    // ---- music -----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The track is unlocked (0x2CBA0): its BGM or one of its variants was heard, or a special rule holds.</summary>
    public bool TrackUnlocked(int index, IUiProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        GalleryTrackRow t = Tables.Tracks[index];
        if (progress.BgmHeard(t.Bgm) || t.Variants.Any(progress.BgmHeard))
        {
            return true;
        }

        return t.Bgm switch
        {
            80 => progress.MovieSeen(0),
            82 => progress.SceneSeen(560),
            124 => progress.MovieSeen(1),
            126 => progress.SceneSeen(562),
            _ => false,
        };
    }

    /// <summary>
    /// The unlock state of every track. A track that a variant or a special rule opened also gets its own heard bit (<see cref="IUiGalleryServices.MarkBgmHeard"/>
    /// when <paramref name="mark"/> is given, 0x87630).
    /// </summary>
    public bool[] TracksUnlocked(IUiProgress progress, IUiGalleryServices? mark = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var ok = new bool[Tables.Tracks.Count];
        for (int i = 0; i < ok.Length; i++)
        {
            ok[i] = TrackUnlocked(i, progress);
            if (ok[i] && mark is not null && !progress.BgmHeard(Tables.Tracks[i].Bgm))
            {
                mark.MarkBgmHeard(Tables.Tracks[i].Bgm);
            }
        }

        return ok;
    }

    // ---- movies ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Movie gallery item <paramref name="item"/> (0 prologue, 1 and 2 the openings) can be played (0x2B610).</summary>
    public static bool MovieUnlocked(int item, IUiProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return item is >= 0 and < 3 && (Movies[item].Bit is not { } bit || progress.MovieSeen(bit));
    }

    // ---- endings ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The number of ending list entries; the last one (QA_25) is the congratulation entry.</summary>
    public int EndingCount => Tables.Endings.Count;

    /// <summary>
    /// Ending entry <paramref name="index"/> is reached (gallery.md 8; knife <c>ending_ok</c>): its unlock scene was finished; the last entry needs every other
    /// one reached.
    /// </summary>
    public bool EndingUnlocked(int index, IUiProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (index == EndingCount - 1)
        {
            return AllEndings(progress);
        }

        GalleryEndingRow e = Tables.Endings[index];
        return progress.SceneDone(e.UnlockA) || (e.UnlockB >= 0 && progress.SceneDone(e.UnlockB));
    }

    /// <summary>Every ending but the last is reached (the last entry's own test).</summary>
    public bool AllEndings(IUiProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        for (int i = 0; i < EndingCount - 1; i++)
        {
            if (!EndingUnlocked(i, progress))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when <paramref name="node"/> is the flowchart node of an ending list scene (533..557): the in-game chart does not open there.</summary>
    public bool IsEndingNode(int node) => Tables.Endings.Any(e => e.FlowScene == node);
}

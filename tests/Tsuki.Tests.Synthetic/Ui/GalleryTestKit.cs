using System.Reflection;
using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>
/// Small gallery tables with the shapes of the real ones (groups with categories, aliases, tracks with variants and special rules, 25 endings, a dozen flowchart
/// columns), numbered so the unlock rules can be checked without game data.
/// </summary>
internal sealed class FakeGalleryTables : IUiGalleryTables
{
    // picture ids: IMG0001..IMG0005 are 1..5, the alias pictures 20 (AAA_S_01) and 21 (BBB_M_01), the gallery-only pictures are beyond 9958
    private static readonly Dictionary<string, int> Pictures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IMG0001"] = 1,
        ["IMG0002"] = 2,
        ["IMG0003"] = 3,
        ["IMG0004"] = 4,
        ["IMG0005"] = 5,
        ["AAA_S_01"] = 20,
        ["BBB_M_01"] = 21,
        ["IMG3000"] = 10000,
        ["IMG3001"] = 10001,
        ["IMG3002"] = 10002,
    };

    private static GalleryCgRow Cg(string name, int th) => new(name, th, 0, 0, 1920, 1080, 0);

    public IReadOnlyList<GalleryGroupRow> CgGroups { get; } =
    [
        new(0, [Cg("IMG0001.MZP", 0)]),
        new(0, [Cg("IMG0002.MZP", 1), Cg("IMG0003.MZP", 2), Cg("IMG0004.MZP", 3)]),
        new(1, [Cg("IMG0005.MZP", 4)]),
        new(0, [Cg("IMG3000.MZP", 5)]),
        new(0, [Cg("IMG3001.MZP", 6)]),
        new(2, [Cg("IMG3002.MZP", 7)]),
    ];

    public IReadOnlyList<GalleryAliasRow> CgAliases { get; } =
    [
        new("IMG3001.MZP", ["AAA_L_01.MZP", "BBB_M_01.MZP", "NEVER_SEEN.MZP"]),
        new("IMG3002.MZP", ["SHORT"]),
    ];

    public IReadOnlyList<string> CgLanguageStrips { get; } = ["IMG0005"];

    public IReadOnlyDictionary<int, IReadOnlyList<int>> CgCategoryScenes { get; } =
        new Dictionary<int, IReadOnlyList<int>> { [1] = [2], [2] = [3, 4] };

    public IReadOnlyList<GalleryTrackRow> Tracks { get; } =
    [
        new(1, 300, 400, -1, 401, -1, 10000, 48000, 480000, []),
        new(2, 301, -1, -1, 401, 402, 10000, 48000, 480000, [50]),
        new(80, 302, -1, -1, 401, -1, 0, 48000, 480000, []),
        new(82, 303, -1, -1, 401, -1, 10000, 48000, 480000, []),
        new(124, 304, -1, -1, 401, -1, 10000, 48000, 480000, []),
        new(126, 305, -1, -1, 401, -1, 10000, 48000, 480000, []),
    ];

    public IReadOnlyList<GalleryEndingRow> Endings { get; } =
    [
        .. Enumerable.Range(0, 25).Select(i => new GalleryEndingRow(1000 + i, 533 + i, 100 + i, i == 5 ? 300 : -1, 1000 + i)),
    ];

    public IReadOnlyList<ChartColumnRow> ChartColumns { get; } = BuildColumns();

    public IReadOnlyList<ChartEdgeRow> ChartEdges { get; } = [new(1, 2, [565], [565]), new(2, 3, [566], [566]), new(2, 4, [567], [567])];

    public IReadOnlyList<SheetCell> ChartLineCells { get; } = [.. Enumerable.Range(0, 12).Select(i => new SheetCell(9, 11 + (i * 20), 105, 24 + (i * 20)))];

    public IReadOnlyList<SheetCell> ChartTitleCells(Language language) => [.. Enumerable.Range(0, 30).Select(i => i == 29 ? default : new SheetCell(25, i * 30, 465, (i * 30) + 30))];

    public int PictureIndex(string name)
    {
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        return Pictures.GetValueOrDefault(dot >= 0 ? name[..dot] : name, -1);
    }

    public int PictureSeenIndex(int pictureId) => pictureId is >= 0 and < 3200 ? pictureId : -1;

    private static ChartNodeRow Node(int scene, int conn, int link, int label = 0) => new(scene, conn, link, 300 + scene, label, scene, scene, 400 + scene, 500 + scene);

    // the chart of the fake data: a prologue column, a chain, a two-way branch whose second branch ends in a marker, and a last section
    private static List<ChartColumnRow> BuildColumns() =>
    [
        new(0, [Node(1, -1, -1)], 0, 0),
        new(0, [Node(2, 3, 565)], 0, 0),
        new(1, [Node(3, 5, 566, 1), Node(4, 9, 567)], 0, 0),
        new(1, [Node(5, 3, 568), Node(-4, 3, 569)], 0, 0),
        new(2, [Node(6, 3, 570, 2)], 0, 0),
        new(2, [Node(7, 3, 571)], 0, 0),
    ];
}

/// <summary>Builds roots over data that has the fake gallery tables.</summary>
internal static class GalleryKit
{
    public static IGameData Data() => DispatchProxy.Create<IGameData, GalleryDataProxy>();

    public static PreviewServices Services(Language language = Language.En) => new(language, usedSlots: 2);

    public static UiRoot Root(PreviewServices services) => new(Data(), services);

    /// <summary>A root with the gallery open (the title is not under it) and its fade-in done.</summary>
    public static (UiRoot Root, PreviewServices Services, GalleryScreen Gallery) OpenGallery(Action<PreviewServices>? progress = null)
    {
        PreviewServices s = Services();
        progress?.Invoke(s);
        UiRoot root = Root(s);
        var gallery = root.Push(new GalleryScreen());
        UiTestKit.Run(root, 40);
        return (root, s, gallery);
    }

    /// <summary>Marks scenes as started and finished.</summary>
    public static void Read(PreviewServices s, params int[] scenes)
    {
        foreach (int n in scenes)
        {
            s.SetSceneBit(n, true);
            s.Progress.Seen.Add(n);
            s.Progress.Done.Add(n);
        }
    }

    /// <summary>A progress with the first chain of the fake chart read: scenes 1 to 3 and the edges between them.</summary>
    public static void ReadChain(PreviewServices s)
    {
        Read(s, 1, 2, 3, 4, 5);
        foreach (int id in new[] { 565, 566, 567, 568 })
        {
            s.Progress.Edges.Add(id);
            s.Progress.RouteEdges.Add(id);
        }

        foreach (int n in new[] { 1, 2, 3, 4, 5 })
        {
            s.Progress.Route.Add(n);
        }

        s.CurrentNode = 3;
    }

    public class GalleryDataProxy : DispatchProxy
    {
        private static readonly IEbootTables Tables = DispatchProxy.Create<IEbootTables, GalleryTablesProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case "get_Eboot":
                    return Tables;
                case "UiSheet":
                case "UiSheetLayout":          // a default member of IGameData that the menus call to place a sheet: the proxy must answer it too
                case "Picture":
                    return UiTestKit.Data().UiSheet(UiSet.GalleryPartsCg, Language.En, 0);
                default:
                    return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void) ? Activator.CreateInstance(targetMethod.ReturnType) : null;
            }
        }
    }

    public class GalleryTablesProxy : DispatchProxy
    {
        private static readonly FakeUiTables Ui = new();
        private static readonly FakeGalleryTables Gallery = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            "get_Ui" => Ui,
            "get_Gallery" => Gallery,
            _ => targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void) ? Activator.CreateInstance(targetMethod.ReturnType) : null,
        };
    }
}

using System.Reflection;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;
using Tsuki.Tests.Synthetic.Text;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>
/// A game data source for the screen tests that needs no game folder: every UI sheet is one big transparent tile, the eboot tables are numbered stand-ins with
/// the real shapes (38 phase cells, 55 dialog ids, two config pages with 16 and 26 rows). The two contract interfaces are built with
/// <see cref="DispatchProxy"/>, so members that other packages add later do not break these tests.
/// </summary>
internal static class UiTestKit
{
    private static readonly Picture Sheet = new(4000, 4000, [new PictureTile(0, TileFlags.MayBeTransparent, 4000, 4000, new PixelRect(0, 0, 4000, 4000), 0, 0, ReadOnlyMemory<byte>.Empty)]);

    public static IGameData Data(Picture? sheet = null)
    {
        IGameData data = DispatchProxy.Create<IGameData, DataProxy>();
        ((DataProxy)(object)data).Served = sheet ?? Sheet;
        return data;
    }

    public static UiRoot Root(PreviewServices services, IUiPictureSource? parts = null) => new(Data(), services, parts);

    /// <summary>Steps <paramref name="frames"/> frames with no input.</summary>
    public static void Run(UiRoot root, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            root.Step(new InputFrame { Frame = root.Context.Frame + 1 });
        }
    }

    /// <summary>One frame in which <paramref name="buttons"/> go down (pressed, repeated and held), then a frame with nothing held.</summary>
    public static void Press(UiRoot root, PadButtons buttons)
    {
        root.Step(new InputFrame { Frame = root.Context.Frame + 1, Pressed = buttons, Repeated = buttons, Held = buttons });
        root.Step(new InputFrame { Frame = root.Context.Frame + 1 });
    }

    /// <summary>Holds <paramref name="buttons"/> for <paramref name="frames"/> frames; the first frame is the press, the others held repeats of the d-pad.</summary>
    public static void Hold(UiRoot root, PadButtons buttons, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            root.Step(new InputFrame
            {
                Frame = root.Context.Frame + 1,
                Pressed = i == 0 ? buttons : PadButtons.None,
                Repeated = i == 0 ? buttons : PadButtons.None,
                Held = buttons,
            });
        }

        root.Step(new InputFrame { Frame = root.Context.Frame + 1 });
    }

    /// <summary>A frame with the pointer at canvas pixel (x, y); with <paramref name="click"/> the left button goes down in it.</summary>
    public static void Point(UiRoot root, float x, float y, bool click = false)
    {
        InputFrame f = UiInput.PointerAt(x, y, click, root.Context.Frame + 1);
        root.Step(f);
        if (click)
        {
            root.Step(new InputFrame { Frame = root.Context.Frame + 1, Pointer = f.Pointer with { Held = PointerButtons.None, Pressed = PointerButtons.None } });
        }
    }

    /// <summary>The left button goes down at canvas pixel (x, y) in one step (and stays down).</summary>
    public static void MouseDown(UiRoot root, float x, float y) =>
        root.Step(UiInput.PointerFrame(x, y, PointerButtons.Left, PointerButtons.Left, 0, root.Context.Frame + 1));

    /// <summary>The pointer moves to (x, y) with the left button held.</summary>
    public static void MouseMove(UiRoot root, float x, float y) =>
        root.Step(UiInput.PointerFrame(x, y, PointerButtons.Left, PointerButtons.None, 0, root.Context.Frame + 1));

    /// <summary>The left button comes up at (x, y).</summary>
    public static void MouseUp(UiRoot root, float x, float y) =>
        root.Step(UiInput.PointerFrame(x, y, PointerButtons.None, PointerButtons.None, 0, root.Context.Frame + 1));

    /// <summary>The pointer moves to (x, y) with no button.</summary>
    public static void MouseHover(UiRoot root, float x, float y) =>
        root.Step(UiInput.PointerFrame(x, y, PointerButtons.None, PointerButtons.None, 0, root.Context.Frame + 1));

    /// <summary>A wheel turn of <paramref name="steps"/> at (x, y) (positive: up).</summary>
    public static void MouseWheel(UiRoot root, float x, float y, int steps) =>
        root.Step(UiInput.PointerFrame(x, y, PointerButtons.None, PointerButtons.None, steps, root.Context.Frame + 1));

    /// <summary>Press at (x0, y0), run to (x1, y1) in <paramref name="steps"/> steps with the button down, release there.</summary>
    public static void Drag(UiRoot root, float x0, float y0, float x1, float y1, int steps = 8)
    {
        MouseDown(root, x0, y0);
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            MouseMove(root, x0 + ((x1 - x0) * t), y0 + ((y1 - y0) * t));
        }

        MouseUp(root, x1, y1);
    }

    /// <summary>A click at (x, y) that goes down and comes up two steps later without moving.</summary>
    public static void Click(UiRoot root, float x, float y)
    {
        MouseDown(root, x, y);
        MouseUp(root, x, y);
    }

    public static DisplayList Draw(UiRoot root)
    {
        var list = new DisplayList(512);
        list.Reset(root.Context.Frame);
        root.Draw(list);
        list.Seal();
        return list;
    }

    public class DataProxy : DispatchProxy
    {
        private static readonly IEbootTables Tables = DispatchProxy.Create<IEbootTables, TablesProxy>();

        public Picture Served { get; set; } = Sheet;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case "get_Eboot":
                    return Tables;
                case "UiSheet":
                case "UiSheetLayout":          // the menus place sheets from their layout (a default member of IGameData: the proxy must answer it too)
                case "Picture":
                    return Served;
                default:
                    return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void) ? Activator.CreateInstance(targetMethod.ReturnType) : null;
            }
        }
    }

    public class TablesProxy : DispatchProxy
    {
        private static readonly FakeUiTables Ui = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == "get_Ui"
                ? Ui
                : targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void) ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }
}

/// <summary>Numbered stand-ins for the eboot UI tables, with the shapes of the real ones.</summary>
internal sealed class FakeUiTables : IUiTables
{
    private readonly FontTables _fonts = SyntheticFont.Create();

    public FontTables Fonts => _fonts;

    public string Text(Language language, int id) => id > 0 ? $"text {id}" : string.Empty;

    public IReadOnlyList<SheetCell> SaveTitleCells(Language language) => Cells(38, 256, 40);

    public IReadOnlyList<SheetCell> PhaseTitleCells(Language language) => Cells(38, 568, 80);

    public IReadOnlyList<SheetCell> GuideIconCells => Cells(23, 40, 40);

    public IReadOnlyList<SheetCell> GuideWordCells(Language language) => Cells(27, 100, 28);

    public IReadOnlyList<SheetCell> BannerCells => Cells(2, 416, 56);

    public IReadOnlyList<(float X, float Y)> BannerKeys { get; } = [.. Enumerable.Range(0, 10).Select(i => (1f, 1f))];

    public IReadOnlyList<SheetCell> VoiceFaceCells => Cells(20, 304, 80);

    public IReadOnlyList<SheetCell> VoiceNameCells(Language language) => Cells(20, 136, 48);

    public IReadOnlyList<UiConfigPage> ConfigPages { get; } = BuildPages();

    public IReadOnlyList<int> DialogMessages { get; } = [.. Enumerable.Range(0, 55).Select(i => 1000 + i)];

    public IReadOnlyList<int> VoiceRowWords { get; } = [.. Enumerable.Range(100, 19)];

    public IReadOnlyList<string> TestSeNames { get; } = [.. Enumerable.Range(0, 14).Select(i => $"se{i}")];

    public IReadOnlyList<int> TitleBackgroundTable { get; } = [1, 2, 3, 3, 2, 4];

    public (int Archive, int Entry)? FindPicture(string name) => (4, 1);

    private static IReadOnlyList<SheetCell> Cells(int n, int w, int h) => [.. Enumerable.Range(0, n).Select(i => new SheetCell(0, i * h, w, (i * h) + h))];

    // the shape of the two eboot pages (docs/formats/adv_state.md 9.1): minus / slider / plus / check rows, radio pairs, the language buttons; the sound page ends in 19 voice rows
    private static List<UiConfigPage> BuildPages()
    {
        UiConfigRow Row(params UiConfigControl[] c) => new(c, 0, 100);
        var p0 = new List<UiConfigRow>();
        for (int i = 0; i < 4; i++)
        {
            var c = new List<UiConfigControl>
            {
                new(52 + (4 * i), 3, 245f, 100f + (70f * i)),
                new(55 + (4 * i), 4, 120f, 56f + (70f * i)),
                new(53 + (4 * i), 5, 452f, 100f + (70f * i)),
            };
            if (i < 2)
            {
                c.Add(new UiConfigControl(54 + (4 * i), 2, 475f, 94f + (70f * i)));
            }

            p0.Add(Row([.. c]));
        }

        p0.Add(Row(new UiConfigControl(66, 6, 243f, 358f), new UiConfigControl(68, 2, 290f, 360f)));
        p0.Add(Row(new UiConfigControl(67, 6, 243f, 378f)));
        for (int k = 0; k < 3; k++)
        {
            p0.Add(Row(new UiConfigControl(69 + (2 * k), 7 + k, 243f, 421f + (63f * k))));
            p0.Add(Row(new UiConfigControl(70 + (2 * k), 7 + k, 243f, 441f + (63f * k))));
        }

        for (int k = 0; k < 4; k++)
        {
            p0.Add(Row(new UiConfigControl(75 + k, 10, 243f, 616f + (20f * k))));
        }

        var p1 = new List<UiConfigRow>();
        for (int i = 0; i < 3; i++)
        {
            var c = new List<UiConfigControl>
            {
                new(79 + (4 * i), 3, 245f, 105f + (28f * i)),
                new(82 + (4 * i), 4, 121f, 60f + (28f * i)),
                new(80 + (4 * i), 5, 452f, 105f + (28f * i)),
                new(81 + (4 * i), 2, 476f, 100f + (28f * i)),
            };
            if (i == 1)
            {
                c.Add(new UiConfigControl(86, 1, 560f, 128f));
            }

            p1.Add(Row([.. c]));
        }

        p1.Add(Row(new UiConfigControl(92, 6, 242f, 229f)));
        p1.Add(Row(new UiConfigControl(93, 6, 242f, 252f)));
        p1.Add(Row(new UiConfigControl(94, 7, 242f, 290f)));
        p1.Add(Row(new UiConfigControl(95, 7, 242f, 312f)));
        for (int r = 0; r < 19; r++)
        {
            p1.Add(Row(
                new UiConfigControl(96 + (5 * r), 3, -9999f, -9999f), new UiConfigControl(100 + (5 * r), 4, -9999f, -9999f),
                new UiConfigControl(97 + (5 * r), 5, -9999f, -9999f), new UiConfigControl(98 + (5 * r), 2, -9999f, -9999f), new UiConfigControl(99 + (5 * r), 1, -9999f, -9999f)));
        }

        return [new UiConfigPage(52, 78, p0), new UiConfigPage(79, 190, p1)];
    }
}

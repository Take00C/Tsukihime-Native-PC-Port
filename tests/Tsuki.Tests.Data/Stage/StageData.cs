using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;
using Tsuki.Engine.Vm;
using Tsuki.Tests.Data.Catalog;

namespace Tsuki.Tests.Data.Stage;

/// <summary>The stage's eboot tables for the tests, through the contract the game host uses (<see cref="IEbootTables"/> of <see cref="GameEbootTables"/>).</summary>
internal static class StageData
{
    private static readonly Lazy<IEbootTables> TablesLazy = new(() => new GameEbootTables(SharedEboot.Reader, SharedEboot.Catalog));

    // opened once for the process (the archives stay mapped)
    private static readonly Lazy<GamePictureLayouts> LayoutsLazy = new(() => new GamePictureLayouts(GameFolderSource.Open(GameFolder.Require())));

    /// <summary>The picture catalog and name map.</summary>
    public static IPictureCatalog Catalog => TablesLazy.Value.Pictures;

    /// <summary>The motion tables.</summary>
    public static IMotionTables Motions => TablesLazy.Value.Motions;

    /// <summary>The picture layouts of the game folder (the layers produce quads, so a scene draw can be compared with the engine's).</summary>
    public static IPictureLayoutSource Layouts => LayoutsLazy.Value;

    /// <summary>The STRA haze table.</summary>
    public static IHazeTable Haze => TablesLazy.Value.Haze;

    /// <summary>A statement from its script text, such as <c>_STCH(7,img0597,240,136);</c>.</summary>
    public static Statement Parse(string text)
    {
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes(text);
        ScriptSegment seg = ScriptSegmenter.Read(bytes, 0);
        if (!seg.HasName)
        {
            throw new ArgumentException($"no statement name in {text}", nameof(text));
        }

        return new Statement(0, seg.Start, seg.End, CommandName.FromBytes(bytes.AsSpan(seg.NameStart, 4)), bytes.AsMemory(seg.ArgsStart, seg.End - seg.ArgsStart));
    }
}

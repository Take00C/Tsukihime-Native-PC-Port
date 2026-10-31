using System.Buffers.Binary;
using Tsuki.Data.Catalog;
using Tsuki.Data.Eboot.Tables;
using Tsuki.Data.Scripts;
using Tsuki.Engine.Stage;

namespace Tsuki.Data;

// The stage area of the eboot tables (package K2): the motion tables, the picture catalog and the haze table in the form Tsuki.Engine/Stage reads them.
public sealed partial class GameEbootTables
{
    private EngineTables? _stageEngineTables;
    private IMotionTables? _stageMotions;
    private IPictureCatalog? _stagePictures;

    private EngineTables StageEngineTables => LazyInitializer.EnsureInitialized(ref _stageEngineTables, () => new EngineTables(Reader))!;

    /// <inheritdoc/>
    IMotionTables Tsuki.Engine.Contracts.IEbootTables.Motions => LazyInitializer.EnsureInitialized(ref _stageMotions, BuildStageMotions)!;

    /// <inheritdoc/>
    IPictureCatalog Tsuki.Engine.Contracts.IEbootTables.Pictures => LazyInitializer.EnsureInitialized(ref _stagePictures, () => new GamePictureCatalog(Catalog))!;

    /// <inheritdoc/>
    IHazeTable Tsuki.Engine.Contracts.IEbootTables.Haze => new GameHazeTable(StageEngineTables.Haze);

    /// <inheritdoc/>
    IReadOnlyList<float>? Tsuki.Engine.Contracts.IEbootTables.ClipRadii => StageEngineTables.ClipRadii;

    private IMotionTables BuildStageMotions()
    {
        MotionTables t = Motions;
        int n = MotionTables.MotionCount;
        var duration = new long[n];
        var end = new int[n];
        var masks = new ulong[n];
        var loops = new int[n];
        for (int id = 1; id <= n; id++)
        {
            duration[id - 1] = t.Duration(id);
            end[id - 1] = t.KeyRange(id).End;
            masks[id - 1] = StageEngineTables.MotionMasks.MaskOf(id);
            loops[id - 1] = StageEngineTables.MotionLoops.LoopPoint(id);
        }

        int keys = t.KeyframeCount;
        var times = new long[keys];
        var fields = new long[keys * MotionTable.FieldCount];
        for (int i = 0; i < keys; i++)
        {
            MotionKeyframe k = t.Keyframe(i);
            times[i] = k.Time;
            k.Fields.Span.CopyTo(fields.AsSpan(i * MotionTable.FieldCount, MotionTable.FieldCount));
        }

        return new ArrayMotionTables(duration, end, times, fields, masks, loops);
    }

    /// <summary><see cref="IPictureCatalog"/> over the image catalog and the picture-name map (STCH lookup, VA 0x78410).</summary>
    private sealed class GamePictureCatalog(GameCatalog catalog) : IPictureCatalog
    {
        private readonly PictureInfo[] _info = [.. catalog.Images.Entries.Select(e => new PictureInfo(e.AnchorX, e.AnchorY, e.ScaleClass, e.DrawFlags, e.Name1 is not null))];

        public int Count => _info.Length;

        public int Lookup(string name) => catalog.Resolver.IndexOf(name);

        public string Name(int pic) => pic >= 0 && pic < _info.Length ? catalog.PictureNames[pic] : string.Empty;

        public PictureInfo Info(int pic) => _info[pic];
    }

    private sealed class GameHazeTable(HazeTable table) : IHazeTable
    {
        public bool TryGetEntry(int straId, out float[] entry)
        {
            if (table.TryGetEntry(straId, out ReadOnlySpan<float> e))
            {
                entry = e.ToArray();
                return true;
            }

            entry = [];
            return false;
        }
    }
}

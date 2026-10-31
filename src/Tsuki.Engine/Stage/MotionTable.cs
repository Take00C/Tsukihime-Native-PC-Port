namespace Tsuki.Engine.Stage;

/// <summary>
/// The motion tables of the eboot as the stage reads them (docs/formats/layers.md 8.1): the duration and end index of each of 24,192
/// motions, 71,905 keyframes of 27 fields, the field-enable word of each motion and the loop point of each motion. The data layer
/// implements it over <c>MotionTables</c> (Tsuki.Data) plus the flag words (text VA 0x245130) and the loop switch (VA 0xC1C70, package K1b).
/// Implementations are immutable; <see cref="MotionTable"/> adds the session edits of <c>_SGMA</c>.
/// </summary>
public interface IMotionTables
{
    /// <summary>Number of motions (ids 1..MotionCount); 24,192.</summary>
    int MotionCount { get; }

    /// <summary>Number of keyframes (the end index of the last motion); 71,905.</summary>
    int KeyCount { get; }

    /// <summary>Duration of motion <paramref name="id"/> (1..MotionCount) in milliseconds.</summary>
    long Duration(int id);

    /// <summary>Exclusive end index of the keyframes of motion <paramref name="id"/> (1..MotionCount); motion id uses <c>[KeyEnd(id - 1), KeyEnd(id))</c>.</summary>
    int KeyEnd(int id);

    /// <summary>Time in milliseconds of keyframe <paramref name="index"/>.</summary>
    long KeyTime(int index);

    /// <summary>Field <paramref name="field"/> (1..27) of keyframe <paramref name="index"/>; -32767 (-100001 for field 8) = not animated.</summary>
    long KeyField(int index, int field);

    /// <summary>The field-enable word of motion <paramref name="id"/> (table 0x245130, indexed by id).</summary>
    ulong FieldMask(int id);

    /// <summary>The loop point of motion <paramref name="id"/> (VA 0xC1C70): 0 no loop, 1 loop the whole motion, n greater than 1 loop from n ms.</summary>
    int LoopPoint(int id);
}

/// <summary>An <see cref="IMotionTables"/> over plain arrays (tests, golden data, adapters).</summary>
public sealed class ArrayMotionTables : IMotionTables
{
    private readonly long[] _duration;
    private readonly int[] _end;
    private readonly long[] _times;
    private readonly long[] _fields;
    private readonly ulong[] _masks;
    private readonly int[] _loops;

    /// <summary>Creates the tables.</summary>
    /// <param name="duration">Duration of motion id at index id - 1.</param>
    /// <param name="end">Exclusive end key of motion id at index id - 1.</param>
    /// <param name="times">Time of each key.</param>
    /// <param name="fields">27 fields per key, flat (field f of key i at <c>27 * i + f - 1</c>).</param>
    /// <param name="masks">Field-enable word of motion id at index id - 1.</param>
    /// <param name="loops">Loop point of motion id at index id - 1.</param>
    public ArrayMotionTables(long[] duration, int[] end, long[] times, long[] fields, ulong[] masks, int[] loops)
    {
        ArgumentNullException.ThrowIfNull(duration);
        ArgumentNullException.ThrowIfNull(end);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(masks);
        ArgumentNullException.ThrowIfNull(loops);
        if (end.Length != duration.Length || masks.Length != duration.Length || loops.Length != duration.Length)
        {
            throw new ArgumentException("duration, end, masks and loops need one entry per motion");
        }

        if (fields.Length != times.Length * MotionTable.FieldCount)
        {
            throw new ArgumentException("27 fields per key are required");
        }

        _duration = duration;
        _end = end;
        _times = times;
        _fields = fields;
        _masks = masks;
        _loops = loops;
    }

    /// <inheritdoc/>
    public int MotionCount => _duration.Length;

    /// <inheritdoc/>
    public int KeyCount => _times.Length;

    /// <inheritdoc/>
    public long Duration(int id) => _duration[id - 1];

    /// <inheritdoc/>
    public int KeyEnd(int id) => id < 1 ? 0 : _end[id - 1];

    /// <inheritdoc/>
    public long KeyTime(int index) => _times[index];

    /// <inheritdoc/>
    public long KeyField(int index, int field) => _fields[(MotionTable.FieldCount * index) + field - 1];

    /// <inheritdoc/>
    public ulong FieldMask(int id) => _masks[id - 1];

    /// <inheritdoc/>
    public int LoopPoint(int id) => _loops[id - 1];
}

/// <summary>One motion: keyframe records <c>[Start, End)</c> of the global table.</summary>
/// <param name="Id">Motion id.</param>
/// <param name="Duration">Duration in milliseconds.</param>
/// <param name="Start">First key.</param>
/// <param name="End">One past the last key.</param>
public readonly record struct MotionRef(int Id, long Duration, int Start, int End);

/// <summary>
/// The motion tables of one game session: the engine's in-memory keyframe table, which <c>_SGMA</c> edits for the rest of the session
/// (layers.md 4.3), over immutable eboot data. Port of <c>MotionTable</c> in the oracle: <see cref="Val"/> is the accessor VA 0xC2190
/// (field 0 = time), <see cref="Flags"/> VA 0xC1C40 and <see cref="Get"/> the duration / end accessors 0xC2150 / 0xC2170.
/// </summary>
public sealed class MotionTable
{
    /// <summary>Fields per keyframe after the time (1..27).</summary>
    public const int FieldCount = 27;

    /// <summary>The i16 "not animated" marker.</summary>
    public const long Unset = -32767;

    /// <summary>The i16 "hold the previous value" marker.</summary>
    public const long Hold = -32766;

    /// <summary>The "not animated" marker of the i64 rotation field 8.</summary>
    public const long Unset8 = -100001;

    /// <summary>The "hold" marker of field 8.</summary>
    public const long Hold8 = -100000;

    private readonly IMotionTables _source;
    private readonly Dictionary<(int Index, int Field), long> _edits = [];

    /// <summary>Creates a session table over <paramref name="source"/>.</summary>
    public MotionTable(IMotionTables source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <summary>Number of keyframes.</summary>
    public int KeyCount => _source.KeyCount;

    /// <summary>Number of motions.</summary>
    public int MotionCount => _source.MotionCount;

    /// <summary>Field <paramref name="f"/> of keyframe <paramref name="i"/> (0 = time); -32767 outside the table (VA 0xC2190).</summary>
    public long Val(int i, int f)
    {
        if ((uint)i >= (uint)_source.KeyCount || (uint)f > FieldCount)
        {
            return Unset;
        }

        if (f == 0)
        {
            return _source.KeyTime(i);
        }

        return _edits.Count != 0 && _edits.TryGetValue((i, f), out long v) ? v : _source.KeyField(i, f);
    }

    /// <summary>Time of keyframe <paramref name="i"/>.</summary>
    public long Time(int i) => Val(i, 0);

    /// <summary>Changes field <paramref name="f"/> (1..27) of keyframe <paramref name="i"/> as <c>_SGMA</c> does.</summary>
    public void SetVal(int i, int f, long value) => _edits[(i, f)] = value;

    /// <summary>The field-enable word of motion <paramref name="id"/>; 0 outside 1..24192 (VA 0xC1C40).</summary>
    public ulong Flags(int id) => id >= 1 && id <= _source.MotionCount ? _source.FieldMask(id) : 0;

    /// <summary>The loop point of motion <paramref name="id"/> (VA 0xC1C70).</summary>
    public int Loop(int id) => id >= 1 && id <= _source.MotionCount ? _source.LoopPoint(id) : 0;

    /// <summary>The motion <paramref name="id"/>, or null for ids outside 1..24192 (no motion).</summary>
    public MotionRef? Get(int id)
    {
        if (id < 1 || id > _source.MotionCount)
        {
            return null;
        }

        return new MotionRef(id, _source.Duration(id), _source.KeyEnd(id - 1), _source.KeyEnd(id));
    }
}

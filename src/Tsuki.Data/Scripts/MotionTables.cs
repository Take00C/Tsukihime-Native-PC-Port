using System.Buffers.Binary;
using Tsuki.Data.Eboot;

namespace Tsuki.Data.Scripts;

/// <summary>One keyframe of a layer motion (docs/formats/layers.md; <c>script_tables.Keyframe</c>).</summary>
/// <param name="Time">Field 0: milliseconds from the motion start (an i64 at +0).</param>
/// <param name="Fields">
/// Fields 1..27: 7 i16 at +8..+0x14, the i64 at +0x18, then 19 i16 at +0x20..+0x44; <see cref="MotionTables.NotAnimated"/> (-32767) marks a field that is not animated.
/// </param>
public readonly record struct MotionKeyframe(long Time, ReadOnlyMemory<long> Fields);

/// <summary>
/// The layer motions <c>_STMA</c> / <c>_SGMA</c> play, compiled into the eboot as three tables (accessors at VA 0xC2150, 0xC2170 and
/// 0xC2190; consumer VA 0xBE4C0): the duration of each of 24,192 motions (i64[24192], text VA 0x274538), the exclusive end index of each
/// motion's keyframes (i64[24192], text VA 0x2A3938) and 71,905 keyframes of 72 bytes (data VA 0x873FF0). Motion <c>id</c> (1-based, as the
/// script writes it) owns the keyframes from the end index of motion <c>id - 1</c> (0 for id 1) up to its own end index. A port of
/// <c>script_tables.read_motions</c>; the loop points (a compiled switch at VA 0xC1C70) and the field-mask words are not part of it.
/// </summary>
public sealed class MotionTables
{
    /// <summary>Motions: ids 1..24192 (the accessor compares <c>id - 1</c> with 0x5E7F).</summary>
    public const int MotionCount = 0x5E80;

    /// <summary>Text VA of the duration table.</summary>
    public const long DurationVa = 0x274538;

    /// <summary>Text VA of the end-index table.</summary>
    public const long EndVa = 0x2A3938;

    /// <summary>Data VA of the first keyframe.</summary>
    public const long KeysVa = 0x873FF0;

    /// <summary>Bytes of a keyframe.</summary>
    public const int KeySize = 72;

    /// <summary>The highest valid keyframe index (VA 0xC2197).</summary>
    public const int MaxKey = 0x118E0;

    /// <summary>Number of fields after the time (1..27).</summary>
    public const int FieldCount = 27;

    /// <summary>The i16 marker of a field that is not animated.</summary>
    public const short NotAnimated = -32767;

    private readonly long[] _duration;
    private readonly long[] _end;
    private readonly long[] _times;
    private readonly long[] _fields;

    private MotionTables(long[] duration, long[] end, long[] times, long[] fields)
    {
        _duration = duration;
        _end = end;
        _times = times;
        _fields = fields;
    }

    /// <summary>Number of keyframes (the end index of the last motion).</summary>
    public int KeyframeCount => _times.Length;

    /// <summary>Reads the three tables from the eboot.</summary>
    /// <exception cref="EbootFormatException">A table is not file-backed, or the end indices do not rise to at most <see cref="MaxKey"/> + 1.</exception>
    public static MotionTables Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        EbootLayout layout = eboot.Layout;
        int motions = layout.MotionCount;
        int maxKeys = layout.KeyframeCount;
        long[] duration = ReadInt64s(eboot, layout.MotionDurations, motions);
        long[] end = ReadInt64s(eboot, layout.MotionEnds, motions);
        long previous = 0;
        for (int k = 0; k < motions; k++)
        {
            if (end[k] < previous || end[k] > maxKeys)
            {
                throw new EbootFormatException($"motion {k + 1}: end index {end[k]} (previous {previous}, at most {maxKeys})");
            }

            previous = end[k];
        }

        int keys = checked((int)end[^1]);
        long[] times = new long[keys];
        long[] fields = new long[keys * FieldCount];
        ReadOnlySpan<byte> raw = eboot.Read(layout.MotionKeys, KeySize * keys);
        for (int i = 0; i < keys; i++)
        {
            ReadOnlySpan<byte> r = raw.Slice(KeySize * i, KeySize);
            times[i] = BinaryPrimitives.ReadInt64LittleEndian(r);
            Span<long> f = fields.AsSpan(FieldCount * i, FieldCount);
            for (int j = 0; j < 7; j++)
            {
                f[j] = BinaryPrimitives.ReadInt16LittleEndian(r[(8 + (2 * j))..]);
            }

            f[7] = BinaryPrimitives.ReadInt64LittleEndian(r[0x18..]);
            for (int j = 0; j < 19; j++)
            {
                f[8 + j] = BinaryPrimitives.ReadInt16LittleEndian(r[(0x20 + (2 * j))..]);
            }
        }

        return new MotionTables(duration, end, times, fields);
    }

    /// <summary>The duration in milliseconds of motion <paramref name="id"/> (1-based).</summary>
    public long Duration(int id) => _duration[CheckId(id)];

    /// <summary>The keyframes of motion <paramref name="id"/> (1-based) as the half-open range of keyframe indices.</summary>
    public (int First, int End) KeyRange(int id)
    {
        int k = CheckId(id);
        int first = k == 0 ? 0 : (int)_end[k - 1];
        return (first, (int)_end[k]);
    }

    /// <summary>The number of keyframes of motion <paramref name="id"/>.</summary>
    public int KeyframeCountOf(int id)
    {
        (int first, int end) = KeyRange(id);
        return Math.Max(end - first, 0);
    }

    /// <summary>Keyframe <paramref name="index"/> (0-based, over all motions).</summary>
    public MotionKeyframe Keyframe(int index)
    {
        if ((uint)index >= (uint)_times.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"keyframe index out of range 0..{_times.Length - 1}");
        }

        return new MotionKeyframe(_times[index], _fields.AsMemory(FieldCount * index, FieldCount));
    }

    /// <summary>The keyframes of motion <paramref name="id"/> in order.</summary>
    public IEnumerable<MotionKeyframe> Keyframes(int id)
    {
        (int first, int end) = KeyRange(id);
        for (int i = first; i < end; i++)
        {
            yield return Keyframe(i);
        }
    }

    /// <summary>Motions in this table (24,192 in the verified build).</summary>
    public int Count => _duration.Length;

    private int CheckId(int id)
    {
        if (id < 1 || id > _duration.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, $"motion id out of range 1..{_duration.Length}");
        }

        return id - 1;
    }

    private static long[] ReadInt64s(EbootReader eboot, long textVa, int count)
    {
        ReadOnlySpan<byte> raw = eboot.Read(textVa, 8 * count);
        long[] values = new long[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadInt64LittleEndian(raw[(8 * i)..]);
        }

        return values;
    }
}

using System.Buffers.Binary;

namespace Tsuki.Data.Eboot.Tables;

/// <summary>One timed key of a focus-line (<c>FCLN</c>) effect; <see cref="TimeMs"/> -1 marks an unused key. Positions are in 1280x720 units.</summary>
/// <param name="TimeMs">Key time in ms, -1 = unused.</param>
/// <param name="CentreX">Centre x.</param>
/// <param name="CentreY">Centre y.</param>
/// <param name="RadiusMin">Inner radius of the lines.</param>
/// <param name="RadiusMax">Outer radius of the lines.</param>
/// <param name="CountMin">Fewest lines.</param>
/// <param name="CountMax">Most lines.</param>
/// <param name="Alpha">0..255.</param>
/// <param name="RerollMs">Interval at which the lines are rolled again.</param>
public readonly record struct FclnKey(int TimeMs, int CentreX, int CentreY, int RadiusMin, int RadiusMax, int CountMin, int CountMax, int Alpha, int RerollMs);

/// <summary>
/// The focus-line effects of <c>_FCLN</c> (knife <c>game/focus.rs</c> <c>LINES</c>, dword_35153C): 30 effects x 4 keys x 9 <c>i32</c> at text VA 0x216BC0.
/// 118 of the 120 keys equal knife's. Port of <c>tsuki.engine_tables.read_fcln_lines</c>.
/// </summary>
public sealed class FclnLineTable
{
    /// <summary>Text VA.</summary>
    public const long Va = 0x216BC0;

    /// <summary>Number of effects.</summary>
    public const int Effects = 30;

    /// <summary>Keys per effect.</summary>
    public const int KeysPerEffect = 4;

    private readonly FclnKey[] _keys;

    private FclnLineTable(FclnKey[] keys)
    {
        _keys = keys;
    }

    /// <summary>Reads the table.</summary>
    /// <exception cref="EbootFormatException">The table is not file-backed.</exception>
    public static FclnLineTable Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        ReadOnlySpan<byte> raw = eboot.Read(eboot.Layout.FclnLines, 36 * Effects * KeysPerEffect);
        FclnKey[] keys = new FclnKey[Effects * KeysPerEffect];
        for (int k = 0; k < keys.Length; k++)
        {
            keys[k] = new FclnKey(
                At(raw, k, 0), At(raw, k, 1), At(raw, k, 2), At(raw, k, 3), At(raw, k, 4), At(raw, k, 5), At(raw, k, 6), At(raw, k, 7), At(raw, k, 8));
        }

        return new FclnLineTable(keys);
    }

    private static int At(ReadOnlySpan<byte> raw, int key, int field) => BinaryPrimitives.ReadInt32LittleEndian(raw[((36 * key) + (4 * field))..]);

    /// <summary>Key <paramref name="key"/> (0..3) of effect <paramref name="effect"/> (0..29; effect 0 is FCLN effect 4).</summary>
    public FclnKey Get(int effect, int key)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(effect);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(effect, Effects);
        ArgumentOutOfRangeException.ThrowIfNegative(key);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(key, KeysPerEffect);
        return _keys[(effect * KeysPerEffect) + key];
    }
}

/// <summary>One falling-note kind of the <c>FCLN</c> effects 1..3 (knife <c>game/focus.rs</c> <c>NOTES</c>, <c>NOTE_V</c>, <c>NOTE_JITTER</c>, <c>NOTE_FLOOR</c>).</summary>
/// <param name="Image">Image number (57, 58, 59).</param>
/// <param name="Frames">Animation frames (24, 20, 16).</param>
/// <param name="Sequence">The frame order of the animation (<paramref name="Frames"/> entries).</param>
/// <param name="Width">Frame width.</param>
/// <param name="Height">Frame height.</param>
/// <param name="Step">Ticks per animation frame.</param>
/// <param name="Vx">Initial x velocity (-0.4).</param>
/// <param name="Vy">Initial y velocity (4.0).</param>
/// <param name="JitterX">Per-frame x jitter (0.005).</param>
/// <param name="JitterY">Per-frame y jitter (0.005).</param>
/// <param name="Drift">A small term (about 1e-5) that knife does not use.</param>
/// <param name="Accel">Acceleration (0.001, 0.0002, 0.0015).</param>
/// <param name="Floor">The y at which a note ends (450).</param>
public sealed record FclnNote(
    int Image, int Frames, IReadOnlyList<int> Sequence, int Width, int Height, int Step, float Vx, float Vy, float JitterX, float JitterY, float Drift, float Accel, int Floor);

/// <summary>
/// The three falling-note records of <c>FCLN</c> effects 1..3: 0x58 bytes each at data VA 0x8734A0. Port of <c>tsuki.engine_tables.read_fcln_notes</c>.
/// </summary>
public static class FclnNoteTable
{
    /// <summary>Data VA of the first record.</summary>
    public const long Va = 0x8734A0;

    /// <summary>Bytes per record.</summary>
    public const int Stride = 0x58;

    /// <summary>Number of records.</summary>
    public const int Count = 3;

    /// <summary>Reads the three records.</summary>
    /// <exception cref="EbootFormatException">A record has an impossible frame count.</exception>
    public static IReadOnlyList<FclnNote> Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        FclnNote[] notes = new FclnNote[Count];
        for (int k = 0; k < Count; k++)
        {
            ReadOnlySpan<byte> b = eboot.Read(eboot.Layout.FclnNotes + (Stride * (long)k), Stride);
            int frames = BinaryPrimitives.ReadUInt16LittleEndian(b[2..]);
            if (frames is < 1 or > 24)
            {
                throw new EbootFormatException($"falling note {k}: {frames} frames");
            }

            int[] sequence = new int[frames];
            for (int f = 0; f < frames; f++)
            {
                sequence[f] = BinaryPrimitives.ReadInt16LittleEndian(b[(4 + (2 * f))..]);
            }

            notes[k] = new FclnNote(
                BinaryPrimitives.ReadUInt16LittleEndian(b), frames, sequence,
                BinaryPrimitives.ReadInt16LittleEndian(b[0x36..]), BinaryPrimitives.ReadInt16LittleEndian(b[0x38..]), BinaryPrimitives.ReadInt16LittleEndian(b[0x3A..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[0x3C..]), BinaryPrimitives.ReadSingleLittleEndian(b[0x40..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[0x44..]), BinaryPrimitives.ReadSingleLittleEndian(b[0x48..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[0x4C..]), BinaryPrimitives.ReadSingleLittleEndian(b[0x50..]),
                BinaryPrimitives.ReadInt32LittleEndian(b[0x54..]));
        }

        return notes;
    }
}

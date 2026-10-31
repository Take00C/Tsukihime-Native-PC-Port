// The PS4 evaluated colour-grade tables of _STEC (docs/formats/compositing.md 9; tools/tsuki/misc_compositing_transitions_shaders.py
// read_stec_tables): 81 modes, each with a flag word (what the recolour does), three 256-byte LUTs (bytes B, G, R) and two luma weights.
// The Switch build stores shader parameters instead (knife grade.rs); the PS4 stores the evaluated LUTs, so the PS4 tables are read from the
// user's eboot (knife_port.md 1 rule 4) and knife's TABLE is not used.
using System.Buffers.Binary;
using Tsuki.Data.Eboot;

namespace Tsuki.Render.Grade;

/// <summary>The 81 colour-grade modes of <c>_STEC</c> as the eboot holds them.</summary>
public sealed class StecTables
{
    /// <summary>Number of modes (ids 1..81).</summary>
    public const int Count = 81;

    /// <summary>Text VA of the flag words, <c>i32[81]</c>: 0x100 per-channel LUTs, 0x200 luma into the LUTs, 0x400 solid colour.</summary>
    public const long FlagsVa = 0x2D5A78;

    /// <summary>Relocated pointer arrays to the 256-byte LUT of byte 0 (blue), byte 1 (green), byte 2 (red), <c>u8*[81]</c> each.</summary>
    public static readonly long[] LutPointerVa = [0x42C590, 0x42C308, 0x42C080];

    /// <summary>Text VA of the luma weight of byte 0 (blue), <c>i32[81]</c>.</summary>
    public const long WeightBlueVa = 0x2D5D00;

    /// <summary>Text VA of the luma weight of byte 2 (red), <c>i32[81]</c> (the weight of byte 1 is 0x224).</summary>
    public const long WeightRedVa = 0x2D5BBC;

    /// <summary>Text VA of the two luma constants, <c>f32[2]</c> = (382.5, 765.0).</summary>
    public const long LumaConstantsVa = 0x2D4008;

    private readonly int[] _flags;
    private readonly int[] _weightBlue;
    private readonly int[] _weightRed;
    private readonly byte[] _luts;

    /// <summary>Creates tables from raw arrays (tests; the reader is <see cref="Read"/>).</summary>
    /// <param name="flags">81 flag words.</param>
    /// <param name="weightBlue">81 weights of byte 0.</param>
    /// <param name="weightRed">81 weights of byte 2.</param>
    /// <param name="luts">81 x 3 x 256 bytes, mode-major, then byte 0, 1, 2.</param>
    /// <param name="lumaOffset">The constant added to the weighted sum (382.5).</param>
    /// <param name="lumaDivisor">The divisor (765.0).</param>
    public StecTables(int[] flags, int[] weightBlue, int[] weightRed, byte[] luts, float lumaOffset = 382.5f, float lumaDivisor = 765.0f)
    {
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(weightBlue);
        ArgumentNullException.ThrowIfNull(weightRed);
        ArgumentNullException.ThrowIfNull(luts);
        if (flags.Length != Count || weightBlue.Length != Count || weightRed.Length != Count || luts.Length != Count * 3 * 256)
        {
            throw new ArgumentException("a STEC table set has 81 modes");
        }

        _flags = flags;
        _weightBlue = weightBlue;
        _weightRed = weightRed;
        _luts = luts;
        LumaOffset = lumaOffset;
        LumaDivisor = lumaDivisor;
    }

    /// <summary>The constant added to the weighted sum before the division (382.5).</summary>
    public float LumaOffset { get; }

    /// <summary>The divisor of the luma (765.0).</summary>
    public float LumaDivisor { get; }

    /// <summary>The flag word of mode <paramref name="id"/> (1..81).</summary>
    public int Flags(int id) => _flags[id - 1];

    /// <summary>The luma weight of byte 0 (blue) of mode <paramref name="id"/>.</summary>
    public int WeightBlue(int id) => _weightBlue[id - 1];

    /// <summary>The luma weight of byte 2 (red) of mode <paramref name="id"/>.</summary>
    public int WeightRed(int id) => _weightRed[id - 1];

    /// <summary>The 256-byte LUT of mode <paramref name="id"/> for byte <paramref name="channel"/> (0 blue, 1 green, 2 red).</summary>
    public ReadOnlySpan<byte> Lut(int id, int channel) => _luts.AsSpan((((id - 1) * 3) + channel) * 256, 256);

    /// <summary>Reads the tables from the eboot (<c>read_stec_tables</c>).</summary>
    /// <exception cref="EbootFormatException">A table is not file-backed.</exception>
    public static StecTables Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        var relocated = new Dictionary<ulong, long>();
        foreach (Relocation r in eboot.Image.Rela())
        {
            if (r.Type == SelfImage.RX86Relative)
            {
                relocated[r.Offset] = r.Addend;
            }
        }

        int[] flags = ReadInts(eboot, FlagsVa);
        int[] wb = ReadInts(eboot, WeightBlueVa);
        int[] wr = ReadInts(eboot, WeightRedVa);
        ReadOnlySpan<byte> k = eboot.Read(LumaConstantsVa, 8);
        float offset = BinaryPrimitives.ReadSingleLittleEndian(k);
        float divisor = BinaryPrimitives.ReadSingleLittleEndian(k[4..]);
        byte[] luts = new byte[Count * 3 * 256];
        for (int channel = 0; channel < 3; channel++)
        {
            for (int mode = 0; mode < Count; mode++)
            {
                long slot = LutPointerVa[channel] + (8L * mode);
                long ptr = relocated.TryGetValue((ulong)slot, out long addend) ? addend : (long)eboot.U64(slot);
                if (ptr != 0)
                {
                    eboot.Read(ptr, 256).CopyTo(luts.AsSpan(((mode * 3) + channel) * 256, 256));
                }
            }
        }

        return new StecTables(flags, wb, wr, luts, offset, divisor);
    }

    private static int[] ReadInts(EbootReader eboot, long va)
    {
        ReadOnlySpan<byte> raw = eboot.Read(va, Count * 4);
        int[] values = new int[Count];
        for (int i = 0; i < Count; i++)
        {
            values[i] = BinaryPrimitives.ReadInt32LittleEndian(raw[(4 * i)..]);
        }

        return values;
    }
}

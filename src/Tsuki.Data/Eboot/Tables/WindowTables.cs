using System.Buffers.Binary;

namespace Tsuki.Data.Eboot.Tables;

/// <summary>One message window type: the PS4 image slot and its origin in 1280x720 units.</summary>
/// <param name="Slot">PS4 image slot (knife's slot numbers belong to another texture list and differ).</param>
/// <param name="X">Origin x.</param>
/// <param name="Y">Origin y.</param>
public readonly record struct WindowType(uint Slot, float X, float Y);

/// <summary>
/// The message window placement tables (knife <c>game.rs</c> <c>WINDOWS</c> / <c>EN_WINDOWS</c>, <c>WINDOW_DX</c>, <c>WINDOW_DY</c>; docs/formats/textbox.md 7): the 56 window types at
/// text VA 0x2006C0 (<c>{u32 slot; f32 x; f32 y}</c>; the origins equal knife's overseas table 56 of 56), the x offsets chosen by F1106 (27 floats at
/// 0x2005B0) and the y offsets chosen by F1107 (37 floats at 0x200620). Port of <c>tsuki.engine_tables.read_window_types</c> and
/// <c>read_window_offsets</c>.
/// </summary>
public sealed class MessageWindowTable
{
    /// <summary>Text VA of the window types.</summary>
    public const long TypesVa = 0x2006C0;

    /// <summary>Number of window types.</summary>
    public const int TypeCount = 56;

    /// <summary>Text VA of the x offsets.</summary>
    public const long DxVa = 0x2005B0;

    /// <summary>Number of x offsets.</summary>
    public const int DxCount = 27;

    /// <summary>Text VA of the y offsets.</summary>
    public const long DyVa = 0x200620;

    /// <summary>Number of y offsets.</summary>
    public const int DyCount = 37;

    private MessageWindowTable(WindowType[] types, float[] dx, float[] dy)
    {
        Types = types;
        Dx = dx;
        Dy = dy;
    }

    /// <summary>The 56 window types.</summary>
    public IReadOnlyList<WindowType> Types { get; }

    /// <summary>X offsets by F1106.</summary>
    public IReadOnlyList<float> Dx { get; }

    /// <summary>Y offsets by F1107.</summary>
    public IReadOnlyList<float> Dy { get; }

    /// <summary>Reads the three tables.</summary>
    /// <exception cref="EbootFormatException">A table is not file-backed.</exception>
    public static MessageWindowTable Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        int count = eboot.Layout.WindowTypeCount;
        ReadOnlySpan<byte> raw = eboot.Read(eboot.Layout.WindowTypes, 12 * count);
        WindowType[] types = new WindowType[count];
        for (int k = 0; k < count; k++)
        {
            ReadOnlySpan<byte> r = raw[(12 * k)..];
            types[k] = new WindowType(BinaryPrimitives.ReadUInt32LittleEndian(r), BinaryPrimitives.ReadSingleLittleEndian(r[4..]), BinaryPrimitives.ReadSingleLittleEndian(r[8..]));
        }

        return new MessageWindowTable(types, ReadFloats(eboot, eboot.Layout.WindowDx, DxCount), ReadFloats(eboot, eboot.Layout.WindowDy, DyCount));
    }

    internal static float[] ReadFloats(EbootReader eboot, long va, int count)
    {
        ReadOnlySpan<byte> raw = eboot.Read(va, 4 * count);
        float[] values = new float[count];
        for (int k = 0; k < count; k++)
        {
            values[k] = BinaryPrimitives.ReadSingleLittleEndian(raw[(4 * k)..]);
        }

        return values;
    }
}

/// <summary>
/// The six soft-clip radii (knife <c>stage.rs</c> <c>RADII</c>, dword_34D81C): <c>f32[6]</c> = 10, 32, 40, 64, 128, 256 at text VA 0x200590, the texel
/// sizes of the <c>alphagradation</c> corner textures. Port of <c>tsuki.engine_tables.read_soft_clip_radii</c>.
/// </summary>
public static class SoftClipRadii
{
    /// <summary>Text VA.</summary>
    public const long Va = 0x200590;

    /// <summary>Reads the six radii.</summary>
    /// <exception cref="EbootFormatException">The table is not file-backed.</exception>
    public static IReadOnlyList<float> Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        return MessageWindowTable.ReadFloats(eboot, eboot.Layout.ClipRadii, 6);
    }
}

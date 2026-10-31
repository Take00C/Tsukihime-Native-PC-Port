using System.Buffers.Binary;

namespace Tsuki.Data.Eboot.Tables;

/// <summary>One control of a config row: its item id, type code and position in 640x448 units (-9999 where the Sound tab places it at run time).</summary>
/// <param name="Item">Item id (grid +0x280).</param>
/// <param name="Kind">Type code (grid +0x78): 1 test button, 2 check box, 3 / 4 / 5 the left / middle / right control of a slider row (knife's minus button, slider, plus button [H]), 6..10 radio groups.</param>
/// <param name="X">Position x (+0x488).</param>
/// <param name="Y">Position y.</param>
public readonly record struct ConfigControl(int Item, int Kind, float X, float Y);

/// <summary>One config row.</summary>
/// <param name="Controls">The controls of the row, left to right.</param>
/// <param name="Current">Current value (+0x900).</param>
/// <param name="Maximum">Slider maximum (+0x968).</param>
public sealed record ConfigRow(IReadOnlyList<ConfigControl> Controls, int Current, int Maximum);

/// <summary>One config tab's data-driven page.</summary>
/// <param name="FirstItem">First item id.</param>
/// <param name="LastItem">Last item id.</param>
/// <param name="Rows">The rows.</param>
public sealed record ConfigPage(int FirstItem, int LastItem, IReadOnlyList<ConfigRow> Rows);

/// <summary>
/// The config screen's layout (knife <c>ui.rs</c> <c>conf_layout_at</c>, dword_46CDB0 + 628 * tab): the PS4 keeps two pages of 0x9D0 bytes (628 <c>i32</c>) at data VA 0x66E890
/// (System tab, Sound tab) with the item and type grids, the control positions (the floats knife inlines), current values and slider maxima
/// (docs/formats/adv_state.md 9.1). Port of <c>tsuki.engine_tables.read_config_pages_layout</c>.
/// </summary>
public static class ConfigPageTable
{
    /// <summary>Data VA of page 0.</summary>
    public const long Va = 0x66E890;

    /// <summary>Bytes per page.</summary>
    public const int Stride = 0x9D0;

    /// <summary>Number of pages with content.</summary>
    public const int PageCount = 2;

    /// <summary>The y/x value of a control that is placed at run time.</summary>
    public const float UnusedPosition = -9999f;

    private const int RowLimit = 26;

    /// <summary>Reads the two pages.</summary>
    /// <exception cref="EbootFormatException">A page has an impossible row or control count.</exception>
    public static IReadOnlyList<ConfigPage> Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        ConfigPage[] pages = new ConfigPage[PageCount];
        for (int p = 0; p < PageCount; p++)
        {
            ReadOnlySpan<byte> b = eboot.Read(eboot.Layout.ConfigPages + (Stride * (long)p), Stride);
            int first = BinaryPrimitives.ReadInt32LittleEndian(b);
            int last = BinaryPrimitives.ReadInt32LittleEndian(b[4..]);
            int rowCount = BinaryPrimitives.ReadInt32LittleEndian(b[12..]);
            if (rowCount is < 0 or > RowLimit)
            {
                throw new EbootFormatException($"config page {p}: {rowCount} rows");
            }

            ConfigRow[] rows = new ConfigRow[rowCount];
            for (int r = 0; r < rowCount; r++)
            {
                int k = BinaryPrimitives.ReadInt32LittleEndian(b[(0x10 + (4 * r))..]);
                if (k is < 0 or > 5)
                {
                    throw new EbootFormatException($"config page {p} row {r}: {k} controls");
                }

                ConfigControl[] controls = new ConfigControl[k];
                for (int c = 0; c < k; c++)
                {
                    controls[c] = new ConfigControl(
                        BinaryPrimitives.ReadInt32LittleEndian(b[(0x280 + (20 * r) + (4 * c))..]),
                        BinaryPrimitives.ReadInt32LittleEndian(b[(0x78 + (20 * r) + (4 * c))..]),
                        BinaryPrimitives.ReadSingleLittleEndian(b[(0x488 + (40 * r) + (8 * c))..]),
                        BinaryPrimitives.ReadSingleLittleEndian(b[(0x488 + (40 * r) + (8 * c) + 4)..]));
                }

                rows[r] = new ConfigRow(controls, BinaryPrimitives.ReadInt32LittleEndian(b[(0x900 + (4 * r))..]), BinaryPrimitives.ReadInt32LittleEndian(b[(0x968 + (4 * r))..]));
            }

            pages[p] = new ConfigPage(first, last, rows);
        }

        return pages;
    }
}

/// <summary>
/// The STMA field-enable masks (knife <c>exe.rs</c> <c>motion_masks</c>): <c>u64[24193]</c> at text VA 0x245130, indexed by motion id (entry 0 unused; the engine's accessor at
/// 0xC1C40 returns 0 outside 1..24192). The Python side reads the same table in <c>misc_layer_state_and_motions.MotionTable</c>. Port of
/// <c>tsuki.engine_tables.read_motion_field_masks</c>.
/// </summary>
public sealed class MotionFieldMasks
{
    /// <summary>Text VA.</summary>
    public const long Va = 0x245130;

    /// <summary>Number of entries.</summary>
    public const int Count = 24193;

    private readonly ulong[] _masks;

    private MotionFieldMasks(ulong[] masks)
    {
        _masks = masks;
    }

    /// <summary>The mask of every motion id (index = id).</summary>
    public ReadOnlySpan<ulong> Masks => _masks;

    /// <summary>Reads the table.</summary>
    /// <exception cref="EbootFormatException">The table is not file-backed.</exception>
    public static MotionFieldMasks Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        int count = eboot.Layout.MotionCount + 1;
        ReadOnlySpan<byte> raw = eboot.Read(eboot.Layout.MotionMasks, 8 * count);
        ulong[] masks = new ulong[count];
        for (int k = 0; k < count; k++)
        {
            masks[k] = BinaryPrimitives.ReadUInt64LittleEndian(raw[(8 * k)..]);
        }

        return new MotionFieldMasks(masks);
    }

    /// <summary>The mask of a motion id; 0 for ids outside 1..24192.</summary>
    public ulong MaskOf(int motionId) => motionId >= 1 && motionId < _masks.Length ? _masks[motionId] : 0;
}

using System.Buffers.Binary;

namespace Tsuki.Data.Eboot.Tables;

/// <summary>A cell of a UI sheet in texels, four <c>i16</c>: (x0, y0, x1, y1) unless the table says (x, y, w, h).</summary>
/// <param name="A">First value (x0 or x).</param>
/// <param name="B">Second value (y0 or y).</param>
/// <param name="C">Third value (x1 or w).</param>
/// <param name="D">Fourth value (y1 or h).</param>
public readonly record struct UiCell(int A, int B, int C, int D);

/// <summary>The UI language selection of the cell tables that exist in three variants.</summary>
public enum PhaseTitleSet
{
    /// <summary>Japanese (and the fallback).</summary>
    Ja,

    /// <summary>English.</summary>
    En,

    /// <summary>Chinese and Korean (the PS4 code uses this set for every language but ja and en).</summary>
    Zh,
}

/// <summary>
/// The UI texture cell tables that knife copies from the Switch executable (<c>ui.rs</c>, <c>flow_table.rs</c>), found in the PS4 eboot as arrays of four
/// <c>i16</c>. Each reader returns the cells of one table; the sheet each belongs to is named in the member's summary. The language rules are those of
/// the PS4 code: savetitle and flow titles use the English table when the language is English (1), phase titles the English table for English, the
/// Japanese one for Japanese (0) and the third table for every other language. Port of the <c>tsuki.engine_tables.read_*_cells</c> functions.
/// </summary>
public static class UiCellTables
{
    /// <summary>Data VAs of the two savetitle tables (38 phases): other languages, English.</summary>
    public static IReadOnlyList<long> SaveTitleVa { get; } = [0x7A85C0, 0x7A86F0];

    /// <summary>Data VAs of the three system_phasetitle tables (38 phases): ja, en, zh.</summary>
    public static IReadOnlyList<long> PhaseTitleVa { get; } = [0x7A6E70, 0x7A6FA0, 0x7A70D0];

    /// <summary>Text VAs of the two flow_phasetitle tables (30 chapter labels, x y w h): other languages, English.</summary>
    public static IReadOnlyList<long> FlowTitleVa { get; } = [0x1A3EE0, 0x1A3FD0];

    /// <summary>Text VA of the 12 flow_line_merge cells (x y w h) by connector kind 0..11.</summary>
    public const long FlowLineVa = 0x1A40C0;

    /// <summary>Data VA of the 20 conf_faceicon cells (x0 y0 x1 y1), one per config voice row.</summary>
    public const long VoiceFaceVa = 0x66E6B0;

    /// <summary>Data VAs of the 20 conf_name cells: other languages, English.</summary>
    public static IReadOnlyList<long> VoiceNameVa { get; } = [0x66E750, 0x66E7F0];

    /// <summary>Data VA of the 23 help_key icon cells.</summary>
    public const long GuideIconVa = 0x7A6190;

    /// <summary>Data VA of the first language block of the help_txt word cells (27 cells per language, 0xD8 bytes).</summary>
    public const long GuideWordVa = 0x7A6250;

    /// <summary>Data VAs of the two mode banner cells.</summary>
    public static IReadOnlyList<long> BannerRectVa { get; } = [0x781B10, 0x781B20];

    /// <summary>Data VA of the 10 banner scale keys (f32 x, f32 y).</summary>
    public const long BannerKeyVa = 0x781AC0;

    /// <summary>Savetitle cells by phase F023 (38), (x0, y0, x1, y1). Phases 16 and 18 of the non-English table are 571 wide (knife's Japanese table: 560).</summary>
    public static IReadOnlyList<UiCell> ReadSaveTitleCells(EbootReader eboot, bool english) => ReadCells(eboot, eboot.Layout.SaveTitleCells[english ? 1 : 0], 38);

    /// <summary>system_phasetitle cells by phase index (38), (x0, y0, x1, y1).</summary>
    public static IReadOnlyList<UiCell> ReadPhaseTitleCells(EbootReader eboot, PhaseTitleSet set) => ReadCells(eboot, eboot.Layout.PhaseTitleCells[(int)set], 38);

    /// <summary>Flowchart chapter-label cells (x, y, w, h) by chapter_label index (30). Index 29 is zero in the PS4 table.</summary>
    public static IReadOnlyList<UiCell> ReadFlowTitleCells(EbootReader eboot, bool english) => ReadCells(eboot, eboot.Layout.FlowTitleCells[english ? 1 : 0], 30);

    /// <summary>Flowchart connector cells (x, y, w, h) by connector kind 0..11 (the grey half; knife's lit half is not in the PS4 table).</summary>
    public static IReadOnlyList<UiCell> ReadFlowLineCells(EbootReader eboot) => ReadCells(eboot, eboot.Layout.FlowLineCells, 12);

    /// <summary>conf_faceicon cells per config voice row (20).</summary>
    public static IReadOnlyList<UiCell> ReadVoiceFaceCells(EbootReader eboot) => ReadCells(eboot, eboot.Layout.VoiceFaceCells, 20);

    /// <summary>conf_name cells per config voice row (20).</summary>
    public static IReadOnlyList<UiCell> ReadVoiceNameCells(EbootReader eboot, bool english) => ReadCells(eboot, eboot.Layout.VoiceNameCells[english ? 1 : 0], 20);

    /// <summary>help_key icon cells (23): 0..6 pad directions, 7 A, 8 Y, 9 X, 10 B ... (knife's order; the PS4 icons differ from index 13).</summary>
    public static IReadOnlyList<UiCell> ReadGuideIconCells(EbootReader eboot) => ReadCells(eboot, eboot.Layout.GuideIconCells, 23);

    /// <summary>help_txt word cells of one language (0 ja, 1 en, 2 zc, 3 zt, 4 ko): 27 cells (back, select, move, ... scroll).</summary>
    public static IReadOnlyList<UiCell> ReadGuideWordCells(EbootReader eboot, int language)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(language);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(language, 4);
        return ReadCells(eboot, eboot.Layout.GuideWordCells + (27 * 8L * language), 27);
    }

    /// <summary>The two mode banner cells of the PS4 build (x0, y0, x1, y1).</summary>
    public static IReadOnlyList<UiCell> ReadBannerCells(EbootReader eboot) => [.. eboot.Layout.BannerRects.Select(va => ReadCells(eboot, va, 1)[0])];

    /// <summary>The banner pop-in scale keys (x scale, y scale), sampled over t * 9.</summary>
    public static IReadOnlyList<(float X, float Y)> ReadBannerKeys(EbootReader eboot)
    {
        float[] f = MessageWindowTable.ReadFloats(eboot, eboot.Layout.BannerKeys, 20);
        return [.. Enumerable.Range(0, 10).Select(k => (f[2 * k], f[(2 * k) + 1]))];
    }

    /// <summary>Reads <paramref name="count"/> cells at a VA.</summary>
    public static IReadOnlyList<UiCell> ReadCells(EbootReader eboot, long va, int count)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        ReadOnlySpan<byte> raw = eboot.Read(va, 8 * count);
        UiCell[] cells = new UiCell[count];
        for (int k = 0; k < count; k++)
        {
            ReadOnlySpan<byte> r = raw[(8 * k)..];
            cells[k] = new UiCell(
                BinaryPrimitives.ReadInt16LittleEndian(r), BinaryPrimitives.ReadInt16LittleEndian(r[2..]),
                BinaryPrimitives.ReadInt16LittleEndian(r[4..]), BinaryPrimitives.ReadInt16LittleEndian(r[6..]));
        }

        return cells;
    }
}

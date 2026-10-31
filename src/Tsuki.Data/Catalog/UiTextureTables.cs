using System.Buffers.Binary;
using Tsuki.Data.Eboot;

namespace Tsuki.Data.Catalog;

/// <summary>
/// One UI texture registration record (0x18 bytes; docs/formats/archives_ui_movies.md 7.3): which child of which
/// allpac3 archive goes into which engine texture slot.
/// </summary>
/// <param name="Va">VA of the record.</param>
/// <param name="State">+0x00, 0 in the file.</param>
/// <param name="TexSlot">+0x04, the engine texture slot; a negative slot ends the table.</param>
/// <param name="Name">+0x08, the base name such as <c>conf_parts.mrg</c>; the localising loader inserts <c>_&lt;lang&gt;</c>.</param>
/// <param name="Child">+0x10, the section index inside the archive (clamped to the last child by VA 0xEB350).</param>
/// <param name="Child2">+0x14, -1 in every live record.</param>
public sealed record UiTexRecord(long Va, int State, int TexSlot, string Name, int Child, int Child2);

/// <summary>A header-less array of <see cref="UiTexRecord"/> and the record that ended it.</summary>
/// <param name="Va">VA of the first record.</param>
/// <param name="Records">The records before the terminator.</param>
/// <param name="Terminator">The 24 bytes of the terminating record.</param>
public sealed record UiTexTable(long Va, IReadOnlyList<UiTexRecord> Records, byte[] Terminator);

/// <summary>
/// The UI texture registration tables of eboot.bin (21 tables, 220 records) and the engine rules that turn their names
/// into allpac3 archives. A port of <c>tsuki.catalog_eboot.Eboot.ui_table(s)</c> and the loaders of <c>tsuki.catalog</c>
/// (<c>localize_parts_name</c>, <c>ui_child_index</c>, <c>lang_index_from_system</c>).
/// </summary>
public static class UiTextureTables
{
    /// <summary>The asset language suffixes in engine order.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["ja", "en", "zc", "zt", "ko"];

    /// <summary>Reads the table at <paramref name="va"/> up to its terminator (a negative slot or a name pointer outside the text segment).</summary>
    public static UiTexTable Read(EbootReader eb, long va)
    {
        var records = new List<UiTexRecord>();
        for (int k = 0; ; k++)
        {
            long r = va + ((long)EbootAddresses.UiRecordSize * k);
            var head = eb.Read(r, EbootAddresses.UiRecordSize);
            int state = BinaryPrimitives.ReadInt32LittleEndian(head);
            int tex = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
            ulong p = BinaryPrimitives.ReadUInt64LittleEndian(head[8..]);
            if (tex < 0 || !eb.IsTextPointer(p))
            {
                return new UiTexTable(va, records, head.ToArray());
            }

            records.Add(new UiTexRecord(
                r, state, tex, eb.CString((long)p, 64), BinaryPrimitives.ReadInt32LittleEndian(head[16..]),
                BinaryPrimitives.ReadInt32LittleEndian(head[20..])));
        }
    }

    /// <summary>All 21 tables, in the order of <see cref="EbootAddresses.UiTables"/>.</summary>
    public static IReadOnlyList<UiTexTable> ReadAll(EbootReader eb)
    {
        ArgumentNullException.ThrowIfNull(eb);
        return [.. eb.Layout.UiTables.Select(va => Read(eb, va))];
    }

    /// <summary>
    /// PS4 system language to asset language (VA 0x3ED90 / 0x407F0): 0 (Japanese) is ja, 11 (Simplified Chinese) zc, 10
    /// (Traditional Chinese) zt, anything else en. The in-game option overrides this when it is below 4; 4 (ko) is never selected.
    /// </summary>
    public static int LangIndexFromSystem(int systemLanguage) => systemLanguage switch { 0 => 0, 11 => 2, 10 => 3, _ => 1 };

    /// <summary>
    /// The UI loader's name rule (VA 0xC2AF0): strip a trailing <c>.mrg</c> or <c>.MRG</c> (only if the name has at least 5
    /// characters) and append <c>_&lt;suffix&gt;.mrg</c>. A <paramref name="lang"/> outside 0..4 is replaced by the language of
    /// <paramref name="systemLanguage"/> (default 1 = English), as VA 0xC2C56 does.
    /// </summary>
    public static string LocalizePartsName(string name, int lang, int systemLanguage = 1)
    {
        if (lang is < 0 or >= 5)
        {
            lang = LangIndexFromSystem(systemLanguage);
        }

        string stem = name.Length >= 5 && (name.EndsWith(".mrg", StringComparison.Ordinal) || name.EndsWith(".MRG", StringComparison.Ordinal))
            ? name[..^4]
            : name;
        return $"{stem}_{Languages[lang]}.mrg";
    }

    /// <summary>
    /// The child that is actually loaded for a registration record (VA 0xEB350): an index past the end selects the last child
    /// (a signed compare), which is how the selecttxt record (child 89 of 83) gets its digit column.
    /// </summary>
    public static int ChildIndex(int child, int childCount) => childCount > child ? child : childCount - 1;
}

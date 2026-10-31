using System.Buffers.Binary;
using System.Globalization;
using Tsuki.Data.Eboot;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Catalog;

/// <summary>The image archives by the engine's archive id (docs/formats/archives_ui_movies.md 2.1).</summary>
public enum ImageArchive : byte
{
    /// <summary>allpac0: id 0, searched last.</summary>
    Allpac0 = 0,

    /// <summary>allpac1: id 1.</summary>
    Allpac1 = 1,

    /// <summary>allpac1_2: id 2, the continuation of allpac1.</summary>
    Allpac1_2 = 2,

    /// <summary>allpac2: id 3, the 1/12.8 copies; never searched by name.</summary>
    Allpac2 = 3,

    /// <summary>allpac3: id 4, UI sheets and rules.</summary>
    Allpac3 = 4,

    /// <summary>allpac4: id 5, late additions, searched first.</summary>
    Allpac4 = 5,
}

/// <summary>A picture file: its archive and NAM index. The engine packs it as <c>archive &lt;&lt; 16 | index</c>.</summary>
public readonly record struct ImageId(ImageArchive Archive, int Index)
{
    /// <summary>The packed id of the engine (VA 0xC3270).</summary>
    public int Packed => ((int)Archive << 16) | Index;

    /// <summary>The archive's base name (<c>allpac1_2</c>).</summary>
    public string ArchiveName => Archive switch
    {
        ImageArchive.Allpac0 => "allpac0",
        ImageArchive.Allpac1 => "allpac1",
        ImageArchive.Allpac1_2 => "allpac1_2",
        ImageArchive.Allpac2 => "allpac2",
        ImageArchive.Allpac3 => "allpac3",
        _ => "allpac4",
    };

    /// <summary>Splits a packed id; the archive id is the 4 bits at bit 16.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The archive id is not 0..5.</exception>
    public static ImageId Unpack(int packed)
    {
        int arc = (packed >> 16) & 0xF;
        return arc > 5
            ? throw new ArgumentOutOfRangeException(nameof(packed), $"archive id {arc} of 0x{packed:x} is not 0..5")
            : new ImageId((ImageArchive)arc, packed & 0xFFFF);
    }

    /// <inheritdoc />
    public override string ToString() => ArchiveName + "#" + Index.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One record of the engine's image table (data VA 0x671900 + 0x48 * k; docs/formats/images.md 8,
/// archives_ui_movies.md 4). Record k describes allpac2 entry k. Merges <c>tsuki.image_catalog.CatalogEntry</c> and
/// <c>tsuki.catalog_eboot.ImageRecord</c>.
/// </summary>
/// <param name="Index">Record number = picture id = allpac2 entry.</param>
/// <param name="FileOffset">File offset of the record in eboot.bin.</param>
/// <param name="Name0">Base or full image MZP name (text VA +0x08), mixed case, with extension.</param>
/// <param name="Name1">Face layer drawn over <paramref name="Name0"/> (+0x10), or null.</param>
/// <param name="LayerX">Position of the face relative to the base (+0x18); 0 in every record.</param>
/// <param name="LayerY">See <paramref name="LayerX"/> (+0x1C).</param>
/// <param name="W3">floor(width / 3) (+0x2C).</param>
/// <param name="H3">floor(height * 362 / 1080) (+0x2E).</param>
/// <param name="Width">Logical width; one language band for pictures with bands (+0x30).</param>
/// <param name="Height">Logical height (+0x32).</param>
/// <param name="AnchorX">Anchor in the w3 / h3 scale (+0x34).</param>
/// <param name="AnchorY">Anchor y (+0x38); -1 in empty slots.</param>
/// <param name="Kind">+0x3C, 0x0B in every record.</param>
/// <param name="DrawFlags">+0x3D, 3 only for the seven solid colours.</param>
/// <param name="DrawParam">+0x3E, 0 in every record.</param>
/// <param name="PicFlags">+0x3F: bit 0 = an <c>_HH_VV</c> variant, bit 1 = four language bands.</param>
/// <param name="CharId">+0x40, the character id of a sprite record, else 0.</param>
/// <param name="ScaleClass">+0x42, the sprite size letter A / L / M / S as 0..3.</param>
public sealed record CatalogEntry(
    int Index, long FileOffset, string Name0, string? Name1, int LayerX, int LayerY, int W3, int H3, int Width, int Height,
    int AnchorX, int AnchorY, int Kind, int DrawFlags, int DrawParam, int PicFlags, int CharId, int ScaleClass)
{
    /// <summary>Row distance between language bands is rounded up to a multiple of this (VA 0xC26A1).</summary>
    public const int BandAlign = 254;

    /// <summary>The thumbnail scale 1/12.8 (VA 0xC2730).</summary>
    public const double ThumbScale = 0.078125;

    /// <summary>VA of the record in the verified build (in another build's located table the record sits at <see cref="FileOffset"/> instead).</summary>
    public long Va => EbootAddresses.ImageTable + ((long)EbootAddresses.ImageRecordSize * Index);

    /// <summary>The script-visible name: the face when there is one, else the file. Equals the allpac2 NAM name.</summary>
    public string Name => Name1 ?? Name0;

    /// <summary>The allpac2 NAM name, upper case.</summary>
    public string ThumbName => Name.ToUpperInvariant();

    /// <summary>The layers to draw, upper case: <see cref="Name0"/>, then <see cref="Name1"/> if any.</summary>
    public IReadOnlyList<string> Layers => Name1 is null ? [Name0.ToUpperInvariant()] : [Name0.ToUpperInvariant(), Name1.ToUpperInvariant()];

    /// <summary>True when the record draws a body (<see cref="Name0"/>) plus a face (<see cref="Name1"/>).</summary>
    public bool Paired => Name1 is not null;

    /// <summary>pic_flags bit 0: the name ends in <c>_HH_VV</c>, a variant of another picture.</summary>
    public bool IsVariant => (PicFlags & 1) != 0;

    /// <summary>pic_flags bit 1: the picture stores the ja / en / zc / zt versions stacked vertically.</summary>
    public bool HasLanguageBands => (PicFlags & 2) != 0;

    /// <summary>Row distance between language bands in the full-size picture; 0 without bands.</summary>
    public int BandStride => HasLanguageBands ? (Height + BandAlign - 1) / BandAlign * BandAlign : 0;

    /// <summary>
    /// The part of the full-size picture the engine draws (displayed pixels): the logical size from the top-left, or
    /// for a picture with language bands the band of <paramref name="language"/> (0 ja, 1 en, 2 zc, 3 zt).
    /// </summary>
    public PixelRect SourceRect(int language)
    {
        return HasLanguageBands
            ? new PixelRect(0, ImageCatalog.LanguageIndex(language) * BandStride, Width, Height)
            : new PixelRect(0, 0, Width, Height);
    }

    /// <summary>The part of the allpac2 thumbnail the engine draws (VA 0xC2730).</summary>
    public PixelRect ThumbRect(int language)
    {
        int tw = (int)((Width * ThumbScale) + 0.5);
        int th = (int)((Height * ThumbScale) + 0.5);
        return HasLanguageBands ? new PixelRect(0, ImageCatalog.LanguageIndex(language) * th, tw, th) : new PixelRect(0, 0, tw, th);
    }
}

/// <summary>
/// The engine's image catalog: 14,744 records and 31 empty slots (14,775 = the engine's bound) in the data segment of
/// eboot.bin. A port of <c>tsuki.image_catalog</c> (the table) and <c>tsuki.catalog_eboot.Eboot.image_table</c>.
/// </summary>
public sealed class ImageCatalog
{
    private const int RecordSize = EbootAddresses.ImageRecordSize;

    private ImageCatalog(CatalogEntry[] entries, int emptySlots, long fileOffset)
    {
        Entries = entries;
        EmptySlots = emptySlots;
        FileOffset = fileOffset;
    }

    /// <summary>The used records, index = record number.</summary>
    public IReadOnlyList<CatalogEntry> Entries { get; }

    /// <summary>Empty slots after the used records (31 in v01.01).</summary>
    public int EmptySlots { get; }

    /// <summary>File offset of record 0 (0x3D78A0 in v01.01).</summary>
    public long FileOffset { get; }

    /// <summary>Number of used records.</summary>
    public int Count => Entries.Count;

    /// <summary>The record <paramref name="k"/>.</summary>
    public CatalogEntry this[int k] => Entries[k];

    /// <summary>The game language index (0 ja, 1 en, 2 zc, 3 zt) of a band language.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not 0..3.</exception>
    public static int LanguageIndex(int language) =>
        language is >= 0 and <= 3 ? language : throw new ArgumentOutOfRangeException(nameof(language), $"language index {language} not in 0..3");

    /// <summary>The language index of <c>ja en zc zt</c> (case-insensitive).</summary>
    /// <exception cref="ArgumentException">Not one of the four.</exception>
    public static int LanguageIndex(string language) =>
        Array.FindIndex(["ja", "en", "zc", "zt"], l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase)) is var i and >= 0
            ? i
            : throw new ArgumentException($"unknown language \"{language}\" (expected ja, en, zc or zt)", nameof(language));

    /// <summary>
    /// Reads the table where <see cref="EbootReader.Layout"/> puts it (VA 0x671900 in the verified build): every slot up to the bound, the used records contiguous
    /// from 0, the rest empty. Checks the pointers, the file names and the count: the layout's by default (-1), <paramref name="expectedRecords"/> when given, none for null.
    /// </summary>
    /// <exception cref="EbootFormatException">A record or the count differs from the analysed build.</exception>
    public static ImageCatalog Read(EbootReader eb, int? expectedRecords = -1)
    {
        ArgumentNullException.ThrowIfNull(eb);
        EbootLayout layout = eb.Layout;
        if (expectedRecords == -1)
        {
            expectedRecords = layout.ImageRecords;     // the layout's count; null means no count check
        }

        var list = new List<CatalogEntry>(layout.ImageRecords);
        int empty = 0;
        long dataFile = (eb.FileOffset(layout.ImageTable) ?? throw new EbootFormatException($"image table VA 0x{layout.ImageTable:x} is not file-backed")) - layout.ImageTable;
        for (int k = 0; k < layout.ImageSlots; k++)
        {
            long va = layout.ImageTable + ((long)RecordSize * k);
            var raw = eb.Read(va, RecordSize);
            ulong pname = BinaryPrimitives.ReadUInt64LittleEndian(raw);
            ulong pfile = BinaryPrimitives.ReadUInt64LittleEndian(raw[8..]);
            ulong pov = BinaryPrimitives.ReadUInt64LittleEndian(raw[16..]);
            if (pfile == 0)
            {
                if (pov != 0 || BinaryPrimitives.ReadInt32LittleEndian(raw[0x20..]) != -1)
                {
                    throw new EbootFormatException($"image slot {k} is neither a record nor empty");
                }

                empty++;
                continue;
            }

            if (empty > 0)
            {
                throw new EbootFormatException($"image slot {k} is used after an empty slot");
            }

            if (pname != 0)
            {
                throw new EbootFormatException($"image slot {k}: name pointer is not 0 (different eboot version?)");
            }

            if (!eb.IsTextPointer(pfile) || (pov != 0 && !eb.IsTextPointer(pov)))
            {
                throw new EbootFormatException($"image slot {k}: pointer outside the text segment");
            }

            string file = eb.CString((long)pfile, 48);
            string? overlay = pov != 0 ? eb.CString((long)pov, 48) : null;
            foreach (string? n in new[] { file, overlay })
            {
                if (n is not null && !IsFileName(n))
                {
                    throw new EbootFormatException($"image slot {k}: unexpected file name \"{n}\"");
                }
            }

            list.Add(FromRecord(k, va + dataFile, file, overlay, raw));
        }

        if (list.Count == 0 || !string.Equals(list[0].Name0, "BLACK.mzp", StringComparison.OrdinalIgnoreCase))
        {
            throw new EbootFormatException($"image table not found at 0x{layout.ImageTable:x}");
        }

        if (expectedRecords is { } want && list.Count != want)
        {
            throw new EbootFormatException($"image table has {list.Count} records, expected {want}");
        }

        return new ImageCatalog([.. list], empty, list[0].FileOffset);
    }

    /// <summary>
    /// Locates the table by content in the whole file (record 0 names <c>BLACK.mzp</c>, record 1 <c>BLUE.mzp</c>) and returns
    /// the file offset of record 0, the used records and the empty slots after them (port of <c>find_catalog</c>).
    /// </summary>
    /// <exception cref="EbootFormatException">No such pair, or the table is malformed.</exception>
    public static (long FileOffset, int Used, int Empty) Locate(ReadOnlySpan<byte> data)
    {
        const long textStart = EbootAddresses.TextFileOffset;
        const long textEnd = EbootAddresses.TextFileOffset + EbootAddresses.TextSize;
        ReadOnlySpan<byte> black = "BLACK.mzp\0"u8;
        long start = -1;
        int searchEnd = (int)Math.Min(textEnd, data.Length);
        int s = IndexOfIn(data, black, (int)textStart, searchEnd);
        Span<byte> ptr = stackalloc byte[8];
        while (s >= 0 && start < 0)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(ptr, (ulong)(s - textStart));
            int pos = IndexOfIn(data, ptr, (int)textEnd, data.Length);
            while (pos >= 0)
            {
                long rec = pos - 8;
                if (IsRecordStart(data, rec) && IsRecordStart(data, rec + RecordSize))
                {
                    ulong n1 = BinaryPrimitives.ReadUInt64LittleEndian(data[(int)(rec + RecordSize + 8)..]);
                    if (string.Equals(TextCString(data, n1), "BLUE.MZP", StringComparison.OrdinalIgnoreCase))
                    {
                        start = rec;
                        break;
                    }
                }

                pos = IndexOfIn(data, ptr, pos + 1, data.Length);
            }

            s = IndexOfIn(data, black, s + 1, searchEnd);
        }

        if (start < 0)
        {
            throw new EbootFormatException("image catalog not found (no BLACK.mzp/BLUE.mzp record pair)");
        }

        int n = 0;
        while (true)
        {
            long off = start + ((long)n * RecordSize);
            if (off + RecordSize > data.Length)
            {
                throw new EbootFormatException("image catalog runs past the end of the file");
            }

            if (BinaryPrimitives.ReadUInt64LittleEndian(data[(int)(off + 8)..]) == 0)
            {
                break;
            }

            if (!IsRecordStart(data, off))
            {
                throw new EbootFormatException($"malformed catalog record {n} at file 0x{off:x}");
            }

            n++;
        }

        int empty = 0;
        while (true)
        {
            long off = start + ((long)(n + empty) * RecordSize);
            if (off + RecordSize > data.Length)
            {
                break;
            }

            var f = data.Slice((int)off, RecordSize);
            if (BinaryPrimitives.ReadUInt64LittleEndian(f[8..]) != 0 || BinaryPrimitives.ReadUInt64LittleEndian(f[16..]) != 0
                || BinaryPrimitives.ReadInt32LittleEndian(f[0x20..]) != -1)
            {
                break;
            }

            empty++;
        }

        return empty == 0
            ? throw new EbootFormatException($"no empty terminator slot after catalog record {n - 1}")
            : (start, n, empty);
    }

    /// <summary>Reads the table found by <see cref="Locate"/> (port of <c>load_catalog</c>); names are decoded as ASCII.</summary>
    public static ImageCatalog ReadByContent(ReadOnlySpan<byte> data)
    {
        var (start, n, empty) = Locate(data);
        var entries = new CatalogEntry[n];
        for (int i = 0; i < n; i++)
        {
            long off = start + ((long)i * RecordSize);
            var raw = data.Slice((int)off, RecordSize);
            ulong p0 = BinaryPrimitives.ReadUInt64LittleEndian(raw[8..]);
            ulong p1 = BinaryPrimitives.ReadUInt64LittleEndian(raw[16..]);
            entries[i] = FromRecord(i, off, TextCString(data, p0), p1 != 0 ? TextCString(data, p1) : null, raw);
        }

        return new ImageCatalog(entries, empty, start);
    }

    private static CatalogEntry FromRecord(int index, long fileOffset, string name0, string? name1, ReadOnlySpan<byte> raw) =>
        new(
            index, fileOffset, name0, name1, BinaryPrimitives.ReadInt32LittleEndian(raw[0x18..]), BinaryPrimitives.ReadInt32LittleEndian(raw[0x1C..]),
            BinaryPrimitives.ReadUInt16LittleEndian(raw[0x2C..]), BinaryPrimitives.ReadUInt16LittleEndian(raw[0x2E..]),
            BinaryPrimitives.ReadUInt16LittleEndian(raw[0x30..]), BinaryPrimitives.ReadUInt16LittleEndian(raw[0x32..]),
            BinaryPrimitives.ReadInt32LittleEndian(raw[0x34..]), BinaryPrimitives.ReadInt32LittleEndian(raw[0x38..]),
            raw[0x3C], raw[0x3D], raw[0x3E], raw[0x3F], BinaryPrimitives.ReadUInt16LittleEndian(raw[0x40..]),
            BinaryPrimitives.ReadInt16LittleEndian(raw[0x42..]));

    /// <summary><c>[A-Za-z0-9_]+\.(mzp|MZP|mrg|MRG)</c>: the names the engine table holds.</summary>
    public static bool IsFileName(string n)
    {
        int dot = n.LastIndexOf('.');
        if (dot < 1)
        {
            return false;
        }

        for (int i = 0; i < dot; i++)
        {
            char c = n[i];
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
            {
                return false;
            }
        }

        return n[(dot + 1)..] is "mzp" or "MZP" or "mrg" or "MRG";
    }

    private static int IndexOfIn(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern, int start, int end)
    {
        if (start >= end || start < 0)
        {
            return -1;
        }

        int i = data[start..end].IndexOf(pattern);
        return i < 0 ? -1 : start + i;
    }

    private static string TextCString(ReadOnlySpan<byte> data, ulong va)
    {
        if (va == 0 || va >= EbootAddresses.TextSize)
        {
            throw new EbootFormatException($"name pointer 0x{va:x} outside the text segment");
        }

        int o = (int)(EbootAddresses.TextFileOffset + (long)va);
        var window = data.Slice(o, Math.Min(96, data.Length - o));
        int nul = window.IndexOf((byte)0);
        if (nul < 0)
        {
            throw new EbootFormatException($"unterminated name at text VA 0x{va:x}");
        }

        foreach (byte b in window[..nul])
        {
            if (b >= 0x80)
            {
                throw new EbootFormatException($"non-ASCII name at text VA 0x{va:x}");
            }
        }

        return System.Text.Encoding.ASCII.GetString(window[..nul]);
    }

    private static bool IsRecordStart(ReadOnlySpan<byte> data, long off)
    {
        if (off < 0 || off + RecordSize > data.Length)
        {
            return false;
        }

        var f = data.Slice((int)off, RecordSize);
        if (BinaryPrimitives.ReadUInt64LittleEndian(f) != 0 || BinaryPrimitives.ReadInt32LittleEndian(f[0x20..]) != -1
            || BinaryPrimitives.ReadInt32LittleEndian(f[0x24..]) != -1)
        {
            return false;
        }

        ulong p0 = BinaryPrimitives.ReadUInt64LittleEndian(f[8..]);
        if (p0 == 0)
        {
            return false;
        }

        try
        {
            return TextCString(data, p0).EndsWith(".MZP", StringComparison.OrdinalIgnoreCase);
        }
        catch (EbootFormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// The embedded picture-name lists (32-byte records: NUL-padded upper-case name without extension, bytes 30..31 CR LF,
/// a 0xFF byte after the last record). Name k is the name of image-table record k (archives_ui_movies.md 2.4).
/// </summary>
public static class PictureNameLists
{
    /// <summary>Names in the list at data VA 0x7F6800 including the empty record 14,744 (14,745).</summary>
    public const int Records = EbootAddresses.ImageTableRecords + 1;

    /// <summary>
    /// Every record before the 0xFF terminator, including the final empty one, so index k is the engine index k
    /// (port of <c>Eboot.name_list</c>).
    /// </summary>
    /// <exception cref="EbootFormatException">No terminator within <paramref name="limit"/> records.</exception>
    public static IReadOnlyList<string> ReadList(EbootReader eb, long va, int limit = 0x8000)
    {
        var names = new List<string>();
        for (int k = 0; k < limit; k++)
        {
            var rec = eb.Read(va + ((long)EbootAddresses.NameRecordSize * k), EbootAddresses.NameRecordSize);
            if (rec[0] == 0xFF)
            {
                return names;
            }

            int nul = rec.IndexOf((byte)0);
            names.Add(EbootReader.Latin1(nul >= 0 ? rec[..nul] : rec));
        }

        throw new EbootFormatException($"name list at 0x{va:x} has no 0xFF terminator within {limit} records");
    }

    /// <summary>The script-name list at data VA 0x7F6800 (14,745 records; the last is empty); entry 0 must be <c>BLACK</c>.</summary>
    public static IReadOnlyList<string> Read(EbootReader eb)
    {
        ArgumentNullException.ThrowIfNull(eb);
        var names = ReadList(eb, eb.Layout.ImageNames);
        return names.Count == 0 || names[0] != "BLACK"
            ? throw new EbootFormatException($"image name list not found at 0x{eb.Layout.ImageNames:x}")
            : names;
    }

    /// <summary>The old list at data VA 0x7A8B10: the first 9,958 names and an empty record; empty when the eboot has no such list.</summary>
    public static IReadOnlyList<string> ReadOld(EbootReader eb)
    {
        ArgumentNullException.ThrowIfNull(eb);
        return eb.Layout.OldImageNames == 0 ? [] : ReadList(eb, eb.Layout.OldImageNames);
    }
}

using System.Buffers.Binary;

namespace Tsuki.Data.Eboot;

/// <summary>
/// The addresses of the analysed eboot.bin (CUSA47999 v01.01) that the table readers use: text VA to file offset is
/// <c>VA + 0x2280</c>, RELRO <c>VA - 0x400000 + 0x32C210</c>, data <c>VA - 0x600000 + 0x365FA0</c>
/// (docs/formats/engine.md 1.2, RR 3.2). They are valid only after the build check accepted the file.
/// </summary>
public static class EbootAddresses
{
    /// <summary>File offset of text VA 0 (docs/formats/engine.md 1.2).</summary>
    public const long TextFileOffset = 0x2280;

    /// <summary>Size of the text segment in the file (also the first byte after it, as a VA).</summary>
    public const long TextSize = 0x329DC8;

    /// <summary>RELRO segment: VA, file offset and size.</summary>
    public const long RelroVa = 0x400000, RelroFileOffset = 0x32C210, RelroSize = 0x35ED0;

    /// <summary>Data segment (file-backed part): VA, file offset and size.</summary>
    public const long DataVa = 0x600000, DataFileOffset = 0x365FA0, DataSize = 0x7D66E0;

    /// <summary>The image table, 0x48-byte records (docs/formats/images.md 8).</summary>
    public const long ImageTable = 0x671900;

    /// <summary>Size of an image table record.</summary>
    public const int ImageRecordSize = 0x48;

    /// <summary>Slots of the image table: the engine's bound <c>id &lt;= 0x39B6</c>.</summary>
    public const int ImageTableSlots = 0x39B6 + 1;

    /// <summary>Used image table records in v01.01 (allpac2 entries minus DUMMY.MRG).</summary>
    public const int ImageTableRecords = 14_744;

    /// <summary>The picture-name list (32-byte records ending in CR LF, then 0xFF).</summary>
    public const long ImageNames = 0x7F6800;

    /// <summary>The old name list: the first 9,958 names of the list above (archives_ui_movies.md 2.4).</summary>
    public const long OldImageNames = 0x7A8B10;

    /// <summary>Size of a name-list record.</summary>
    public const int NameRecordSize = 0x20;

    /// <summary>The movie table, 0x30-byte records.</summary>
    public const long MovieTable = 0xDC8C70;

    /// <summary>Size of a movie record.</summary>
    public const int MovieRecordSize = 0x30;

    /// <summary>The terminator record of the movie table (id -1).</summary>
    public const long MovieTerminator = 0xDCA590;

    /// <summary>Five <c>char*</c> to the language suffixes <c>ja en zc zt ko</c>.</summary>
    public const long LanguageSuffixes = 0x42C030;

    /// <summary>Four u32 movie ids of the prologue per language.</summary>
    public const long PrologueTable = 0x200F70;

    /// <summary>The scene-id table: 549 i16 (docs/formats/script.md 2.5).</summary>
    public const long SceneIds = 0x216130;

    /// <summary><c>char *ui_text[5][1499]</c> in RELRO.</summary>
    public const long UiText = 0x41D5F0;

    /// <summary>The string "Hunex Consumer Library".</summary>
    public const long HunexString = 0x2DA510;

    /// <summary>Size of a UI texture registration record.</summary>
    public const int UiRecordSize = 0x18;

    /// <summary>The 21 UI texture registration tables: header-less arrays ended by a record with a negative texture slot.</summary>
    public static IReadOnlyList<long> UiTables { get; } =
    [
        0x66AD10, 0x66BA80, 0x66BBC0, 0x66C9A0, 0x780C90, 0x781B70,
        0x782700, 0x784290, 0x785E20, 0x7879B0, 0x789540, 0x78B2C0,
        0x7A6090, 0x7A67F0, 0x7A6B20, 0x7A7680, 0x7A8300, 0x8730F0,
        0x8731A0, 0x873660, 0xDB9320,
    ];

    /// <summary>The five <c>ed1_parts</c> tables that no code references.</summary>
    public static IReadOnlyList<long> UiTablesUnreferenced { get; } = [0x782700, 0x784290, 0x785E20, 0x7879B0, 0x789540];
}

/// <summary>
/// Reads the file-backed bytes of eboot.bin by virtual address, through the program headers of a
/// <see cref="SelfImage"/>: a port of the read helpers of <c>tsuki.catalog_eboot.Eboot</c>. A read must lie inside one
/// loaded segment's file image (.bss and unmapped addresses are errors here, unlike <see cref="SelfImage.ReadVa"/>).
/// </summary>
public sealed class EbootReader
{
    private readonly (ulong Start, ulong End, long FileOffset)[] _segments;

    /// <summary>Creates a reader over a parsed image with the addresses of the verified build.</summary>
    public EbootReader(SelfImage image)
        : this(image, EbootLayout.Analysed)
    {
    }

    /// <summary>Creates a reader over a parsed image whose tables are where <paramref name="layout"/> says (package K25).</summary>
    public EbootReader(SelfImage image, EbootLayout layout)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(layout);
        Image = image;
        Layout = layout;
        var list = new List<(ulong, ulong, long)>();
        foreach (var ph in image.LoadSegments())
        {
            if (image.PhdrFileOffset(ph) is { } fo && ph.FileSize != 0)
            {
                list.Add((ph.VAddr, ph.VAddr + ph.FileSize, fo));
            }
        }

        _segments = [.. list];
        var text = image.Text;
        TextEnd = text.VAddr + text.FileSize;
    }

    /// <summary>The parsed SELF / ELF.</summary>
    public SelfImage Image { get; }

    /// <summary>
    /// Where the tables are in this eboot: <see cref="EbootLayout.Analysed"/> for the verified build, else what <see cref="EbootLocator"/> found by content
    /// (package K25). Every table reader takes its addresses and counts from here.
    /// </summary>
    public EbootLayout Layout { get; }

    /// <summary>First VA after the text segment: pointers to names must be below it.</summary>
    public ulong TextEnd { get; }

    /// <summary>Parses <paramref name="data"/> and wraps it with the verified build's addresses.</summary>
    public static EbootReader Parse(ReadOnlyMemory<byte> data) => new(SelfImage.Parse(data));

    /// <summary>The same image with another <see cref="Layout"/>.</summary>
    public EbootReader WithLayout(EbootLayout layout) => new(Image, layout);

    /// <summary>The loaded segments as (first VA, VA after the file image, file offset of the first byte), in program-header order.</summary>
    public IReadOnlyList<(ulong Start, ulong End, long FileOffset)> Segments => _segments;

    /// <summary>True when <paramref name="p"/> points into the text segment (<c>0 &lt; p &lt; TextEnd</c>).</summary>
    public bool IsTextPointer(ulong p) => p > 0 && p < TextEnd;

    /// <summary>The file offset of a VA, or null when it is not file-backed.</summary>
    public long? FileOffset(long va)
    {
        foreach (var (start, end, fo) in _segments)
        {
            if ((ulong)va >= start && (ulong)va < end)
            {
                return fo + (long)((ulong)va - start);
            }
        }

        return null;
    }

    /// <summary>The <paramref name="n"/> bytes at a VA.</summary>
    /// <exception cref="EbootFormatException">The range is not inside one segment's file image.</exception>
    public ReadOnlySpan<byte> Read(long va, int n)
    {
        foreach (var (start, end, fo) in _segments)
        {
            if (va >= 0 && (ulong)va >= start && (ulong)va < end)
            {
                if ((ulong)va + (ulong)n > end)
                {
                    break;
                }

                return Image.Data.Span.Slice(checked((int)(fo + (long)((ulong)va - start))), n);
            }
        }

        throw new EbootFormatException($"VA 0x{va:x} (+{n}) is not file-backed");
    }

    /// <summary>A little-endian u32.</summary>
    public uint U32(long va) => BinaryPrimitives.ReadUInt32LittleEndian(Read(va, 4));

    /// <summary>A little-endian i32.</summary>
    public int I32(long va) => BinaryPrimitives.ReadInt32LittleEndian(Read(va, 4));

    /// <summary>A little-endian u64.</summary>
    public ulong U64(long va) => BinaryPrimitives.ReadUInt64LittleEndian(Read(va, 8));

    /// <summary>A little-endian i16.</summary>
    public short I16(long va) => BinaryPrimitives.ReadInt16LittleEndian(Read(va, 2));

    /// <summary>
    /// The NUL-terminated string at a VA, at most <paramref name="limit"/> bytes, each byte as one character (Latin-1,
    /// like the Python tools).
    /// </summary>
    public string CString(long va, int limit = 256)
    {
        int n = limit;
        foreach (var (start, end, _) in _segments)
        {
            if (va >= 0 && (ulong)va >= start && (ulong)va < end)
            {
                n = (int)Math.Min((ulong)limit, end - (ulong)va);
                break;
            }
        }

        var window = Read(va, n);
        int nul = window.IndexOf((byte)0);
        return Latin1(nul >= 0 ? window[..nul] : window);
    }

    /// <summary>Each byte as one character.</summary>
    public static string Latin1(ReadOnlySpan<byte> bytes)
    {
        return string.Create(bytes.Length, bytes.ToArray(), static (chars, b) =>
        {
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)b[i];
            }
        });
    }
}

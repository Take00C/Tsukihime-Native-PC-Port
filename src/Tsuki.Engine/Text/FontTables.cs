using System.Buffers.Binary;
using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Text;

/// <summary>
/// One glyph record of the eboot: <c>{u16 texture slot, u16 x, u16 y, s16 w, u16 h}</c>, 10 bytes
/// (docs/formats/fonts.md 5; tools/tsuki/font_eboot.py <c>Rect</c>).
/// </summary>
/// <param name="Tex">Texture slot (font page = slot - 0x21).</param>
/// <param name="X">Left edge in the atlas page.</param>
/// <param name="Y">Top edge in the atlas page.</param>
/// <param name="W">Width, signed like the engine's s16.</param>
/// <param name="H">Height.</param>
public readonly record struct GlyphRect(int Tex, int X, int Y, int W, int H);

/// <summary>
/// A sorted <c>{u64 key, s16 glyph index, u16 0, u32 0}</c> lookup table (eboot VA 0x10810): the key
/// is the character's UTF-8 bytes packed big-endian. A found key whose index is negative counts as
/// missing, like the engine (fonts.md 5.1).
/// </summary>
public sealed class GlyphLookupTable
{
    private readonly Dictionary<ulong, int> _index;

    /// <summary>Creates a table from its entries.</summary>
    /// <param name="name">Table name, for example <c>narrow</c> or <c>ja_wide</c>.</param>
    /// <param name="keys">The packed UTF-8 keys.</param>
    /// <param name="indices">The glyph index of each key (same length).</param>
    public GlyphLookupTable(string name, IReadOnlyList<ulong> keys, IReadOnlyList<int> indices)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(indices);
        if (keys.Count != indices.Count)
        {
            throw new ArgumentException("keys and indices differ in length", nameof(indices));
        }

        Name = name;
        _index = new Dictionary<ulong, int>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            _index[keys[i]] = indices[i];
        }
    }

    /// <summary>Table name.</summary>
    public string Name { get; }

    /// <summary>Number of entries.</summary>
    public int Count => _index.Count;

    /// <summary>Glyph index of <paramref name="key"/>, or -1 when the key is missing or its index is negative.</summary>
    public int Get(ulong key) => _index.TryGetValue(key, out int idx) && idx >= 0 ? idx : -1;
}

/// <summary>
/// An array of 10-byte glyph records the engine indexes without a bounds check. The table holds the
/// bytes from its first record to the end of the executable image segment, so an index past
/// <see cref="Count"/> reads the neighbouring data exactly like the game (fonts.md 5.2; tools/tsuki
/// <c>RecordTable</c>).
/// </summary>
public sealed class GlyphRecordTable
{
    private readonly ReadOnlyMemory<byte> _image;

    /// <summary>Creates a record table.</summary>
    /// <param name="name">Table name, for example <c>ja_s0</c>.</param>
    /// <param name="count">Records that belong to the table.</param>
    /// <param name="image">Bytes from the first record on; at least <c>10 * count</c>, longer to cover reads past the end.</param>
    public GlyphRecordTable(string name, int count, ReadOnlyMemory<byte> image)
    {
        if (image.Length < 10L * count)
        {
            throw new ArgumentException($"{name}: {image.Length} bytes hold fewer than {count} records", nameof(image));
        }

        Name = name;
        Count = count;
        _image = image;
    }

    /// <summary>Table name.</summary>
    public string Name { get; }

    /// <summary>Records that belong to the table.</summary>
    public int Count { get; }

    /// <summary>True when <paramref name="index"/> is inside the table proper.</summary>
    public bool InRange(int index) => index >= 0 && index < Count;

    /// <summary>Reads record <paramref name="index"/>, past the end of the table too, as far as the image reaches.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The record lies beyond the supplied image bytes.</exception>
    public GlyphRect Get(int index)
    {
        long offset = 10L * index;
        if (offset < 0 || offset + 10 > _image.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"{Name}: record {index} lies beyond the supplied image bytes ({_image.Length})");
        }

        var s = _image.Span.Slice((int)offset, 10);
        return new GlyphRect(
            BinaryPrimitives.ReadUInt16LittleEndian(s),
            BinaryPrimitives.ReadUInt16LittleEndian(s[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(s[4..]),
            BinaryPrimitives.ReadInt16LittleEndian(s[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(s[8..]));
    }

    /// <summary>Packs records into the 10-byte form (the tail past the table is left to the caller).</summary>
    public static byte[] Pack(IReadOnlyList<GlyphRect> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var bytes = new byte[10 * records.Count];
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var s = bytes.AsSpan(10 * i, 10);
            BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)r.Tex);
            BinaryPrimitives.WriteUInt16LittleEndian(s[2..], (ushort)r.X);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], (ushort)r.Y);
            BinaryPrimitives.WriteInt16LittleEndian(s[6..], (short)r.W);
            BinaryPrimitives.WriteUInt16LittleEndian(s[8..], (ushort)r.H);
        }

        return bytes;
    }
}

/// <summary>
/// All glyph-map data the text layout needs, from eboot.bin (fonts.md 5; tools/tsuki/font_eboot.py
/// <c>EbootFontTables</c>): the 16 lookup tables, the 17 record tables, the first texture slot of the
/// wide grid per language (the 39 <c>[tag]</c> names are constants, <see cref="StandardTagNames"/>).
/// </summary>
public sealed class FontTables
{
    /// <summary>Number of <c>[tag]</c> glyph names (fonts.md 5.5).</summary>
    public const int TagCount = 39;

    /// <summary>
    /// The 39 <c>[tag]</c> glyph names in table order (VA 0x187C80): seven symbols, <c>[ber00]</c>..<c>[ber30]</c>
    /// and <c>[zap00]</c>. They are constants of this build; <c>EbootFontTables.verify</c> checks the eboot against them.
    /// </summary>
    public static IReadOnlyList<string> StandardTagNames { get; } =
        [.. new[] { "[heart]", "[heart2]", "[star]", "[star2]", "[edia]", "[dummy]", "[onpu]" }
            .Concat(Enumerable.Range(0, 31).Select(i => $"[ber{i:00}]"))
            .Append("[zap00]")];

    internal static IReadOnlyList<byte[]> StandardTagBytes { get; } =
        [.. StandardTagNames.Select(t => System.Text.Encoding.ASCII.GetBytes(t))];

    /// <summary>Language names of the table families, by engine language index.</summary>
    internal static readonly string[] LangNames = ["ja", "en", "zc", "zt", "ko"];

    private readonly Dictionary<string, GlyphLookupTable> _lookups;
    private readonly Dictionary<string, GlyphRecordTable> _records;
    private readonly LanguageGlyphs?[] _perLanguage = new LanguageGlyphs?[5];

    /// <summary>Creates the table set.</summary>
    /// <param name="lookups">Lookup tables by name (<c>narrow</c>, <c>ja_s1</c>, <c>ja_wide</c> ...).</param>
    /// <param name="records">Record tables by name (<c>ja_s0</c>, <c>en_s3</c> ...).</param>
    /// <param name="wideBase">First texture slot of the wide grid for ja, en, zc, zt, ko.</param>
    public FontTables(
        IEnumerable<GlyphLookupTable> lookups, IEnumerable<GlyphRecordTable> records, IReadOnlyList<int> wideBase)
    {
        ArgumentNullException.ThrowIfNull(lookups);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(wideBase);
        if (wideBase.Count != 5)
        {
            throw new ArgumentException("wideBase needs 5 entries", nameof(wideBase));
        }

        _lookups = lookups.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _records = records.ToDictionary(t => t.Name, StringComparer.Ordinal);
        WideBase = [.. wideBase];
    }

    /// <summary>First texture slot of the wide grid per language (VA 0x187E2E).</summary>
    public IReadOnlyList<int> WideBase { get; }

    /// <summary>The <c>[tag]</c> names as ASCII bytes, in table order.</summary>
    internal IReadOnlyList<byte[]> Tags => StandardTagBytes;

    /// <summary>A lookup table by name.</summary>
    public GlyphLookupTable Lookup(string name) =>
        _lookups.TryGetValue(name, out var t) ? t : throw new KeyNotFoundException($"lookup table {name}");

    /// <summary>A record table by name.</summary>
    public GlyphRecordTable Records(string name) =>
        _records.TryGetValue(name, out var t) ? t : throw new KeyNotFoundException($"record table {name}");

    /// <summary>The glyph resolver of one language (cached).</summary>
    public LanguageGlyphs For(Language lang)
    {
        int i = (int)lang;
        if ((uint)i >= 5)
        {
            throw new ArgumentOutOfRangeException(nameof(lang));
        }

        return _perLanguage[i] ??= new LanguageGlyphs(this, i);
    }

    internal GlyphLookupTable? TryLookup(string name) => _lookups.GetValueOrDefault(name);

    internal GlyphRecordTable? TryRecords(string name) => _records.GetValueOrDefault(name);
}

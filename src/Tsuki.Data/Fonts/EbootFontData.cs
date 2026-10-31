using System.Buffers.Binary;
using System.Text;
using Tsuki.Data.Eboot;
using Tsuki.Engine.Text;

namespace Tsuki.Data.Fonts;

/// <summary>Where one 16-byte lookup table lives in the analysed eboot (<c>font_eboot.LOOKUP_SPECS</c>; docs/formats/fonts.md 5.1).</summary>
/// <param name="Name">Table name.</param>
/// <param name="Va">Data VA of the first record.</param>
/// <param name="Count">Records (the binary search's high index + 1 in VA 0x10810).</param>
/// <param name="LeaSite">Text VA of a <c>lea r64, [rip+disp32]</c> that loads the table.</param>
public sealed record FontLookupSpec(string Name, long Va, int Count, long LeaSite);

/// <summary>Where one 10-byte glyph record table lives in the analysed eboot (<c>font_eboot.RECORD_SPECS</c>; docs/formats/fonts.md 5.2).</summary>
/// <param name="Name">Table name.</param>
/// <param name="Va">Text VA of the first record.</param>
/// <param name="Count">Records that belong to the table.</param>
/// <param name="LeaSite">Text VA of the 7-byte <c>lea rcx, [rip+disp32]</c> that loads the table.</param>
public sealed record FontRecordSpec(string Name, long Va, int Count, long LeaSite);

/// <summary>A lookup table as stored: <c>{u64 key, s16 glyph index, u16 0, u32 0}</c> sorted by key.</summary>
/// <param name="Spec">Where it comes from.</param>
/// <param name="Keys">The packed UTF-8 keys in table order.</param>
/// <param name="Indices">The glyph index of each key.</param>
public sealed record FontLookupData(FontLookupSpec Spec, ulong[] Keys, short[] Indices);

/// <summary>
/// The glyph-map tables in <c>eboot.bin</c> (docs/formats/fonts.md 5; a port of <c>tsuki.font_eboot.EbootFontTables</c>): 16 sorted lookup
/// arrays (17,423 entries together), 17 arrays of 10-byte glyph records, the first texture slot of the wide grid per language and the 39
/// <c>[tag]</c> names. <see cref="ToEngineTables"/> hands them to the text layout; <see cref="Verify"/> is the self-check of RR 3.3 item 2.
/// </summary>
public sealed class EbootFontData
{
    /// <summary>Text VA of the wide-grid base slots, a u16 per language (ja, en, zc, zt, ko).</summary>
    public const long WideBaseVa = 0x187E2E;

    /// <summary>Text VA of the <c>lea rdx, [rip+...]</c> that loads <see cref="WideBaseVa"/>.</summary>
    public const long WideBaseSite = 0x111F7;

    /// <summary>Text VA of the 39 tag names, 11 bytes each.</summary>
    public const long TagsVa = 0x187C80;

    /// <summary>Text VA of the <c>lea r15, [rip+...]</c> that loads <see cref="TagsVa"/>.</summary>
    public const long TagsSite = 0x117B1;

    /// <summary>Bytes per tag record.</summary>
    public const int TagStride = 11;

    /// <summary>Number of tags.</summary>
    public const int TagCount = 39;

    /// <summary>The wide-grid base slots of the analysed build.</summary>
    public static IReadOnlyList<int> ExpectedWideBase { get; } = [59, 59, 59, 57, 59];

    /// <summary>The 16 lookup tables. Counts are the immediate high index + 1 from the binary-search setup in VA 0x10810.</summary>
    public static IReadOnlyList<FontLookupSpec> LookupSpecs { get; } =
    [
        new("narrow", 0x626C10, 109, 0x108C8),
        new("ja_s1", 0x6272E0, 701, 0x10C91),
        new("en_s1", 0x629EB0, 111, 0x10D9E),
        new("zc_s1", 0x62A5A0, 769, 0x10CEE),
        new("zt_s1", 0x62D5B0, 1631, 0x10D49),
        new("ko_s1", 0x633BA0, 1, 0x10C29),
        new("ja_s2", 0x633BB0, 1174, 0x10848),
        new("en_s2", 0x638510, 82, 0x10DFE),
        new("zc_s2", 0x638A30, 1434, 0x10930),
        new("zt_s2", 0x63E3D0, 1631, 0x109A1),
        new("ko_s2", 0x6449C0, 1, 0x10A0C),
        new("ja_wide", 0x6449D0, 2795, 0x10F7E),
        new("en_wide", 0x64F880, 6, 0x110F7),
        new("zc_wide", 0x64F8E0, 3403, 0x10FDE),
        new("zt_wide", 0x65CD90, 3570, 0x1103E),
        new("ko_wide", 0x66ACB0, 5, 0x1109E),
    ];

    /// <summary>The 17 glyph record tables, selected in <c>get_glyph</c> (VA 0x11150).</summary>
    public static IReadOnlyList<FontRecordSpec> RecordSpecs { get; } =
    [
        new("ja_s0", 0x177370, 104, 0x1146E),
        new("ja_s2", 0x177780, 1174, 0x11315),
        new("ja_s1", 0x17A560, 701, 0x114A3),
        new("en_s0", 0x17C0D0, 109, 0x11479),
        new("en_s2", 0x17C520, 82, 0x113B5),
        new("en_s3", 0x17C860, 111, 0x114CB),
        new("en_s4", 0x17CCC0, 111, 0x114D6),
        new("en_s5", 0x17D120, 111, 0x114E1),
        new("en_s1", 0x17D580, 111, 0x114C0),
        new("zc_s0", 0x17D9E0, 109, 0x11484),
        new("zc_s2", 0x17DE30, 1434, 0x113D8),
        new("zc_s1", 0x181640, 769, 0x114AC),
        new("zt_s0", 0x183450, 109, 0x1148F),
        new("zt_s1", 0x1838A0, 1631, 0x113F1),
        new("ko_s0", 0x187860, 103, 0x1149A),
        new("ko_s2", 0x187C66, 1, 0x11414),
        new("ko_s1", 0x187C70, 1, 0x114B5),
    ];

    private readonly EbootReader _eboot;

    private EbootFontData(
        EbootReader eboot, IReadOnlyList<FontLookupData> lookups, IReadOnlyList<ReadOnlyMemory<byte>> recordImages,
        IReadOnlyList<int> wideBase, IReadOnlyList<string> tags)
    {
        _eboot = eboot;
        Lookups = lookups;
        RecordImages = recordImages;
        WideBase = wideBase;
        Tags = tags;
    }

    /// <summary>The lookup tables in <see cref="LookupSpecs"/> order.</summary>
    public IReadOnlyList<FontLookupData> Lookups { get; }

    /// <summary>
    /// For each record table in <see cref="RecordSpecs"/> order: the bytes from its first record to the end of the file-backed text segment, so
    /// that a record index past the table reads the neighbouring data like the engine does (fonts.md 5.2).
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> RecordImages { get; }

    /// <summary>First texture slot of the wide grid for ja, en, zc, zt, ko.</summary>
    public IReadOnlyList<int> WideBase { get; }

    /// <summary>The 39 <c>[tag]</c> names.</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>Total lookup entries: 17,423 in the analysed build.</summary>
    public int LookupEntryCount => Lookups.Sum(l => l.Keys.Length);

    /// <summary>Reads every table from a parsed eboot.</summary>
    /// <exception cref="EbootFormatException">A table is not file-backed.</exception>
    public static EbootFontData Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        EbootLayout layout = eboot.Layout;
        var lookups = new List<FontLookupData>(layout.FontLookups.Count);
        foreach (FontLookupSpec spec in layout.FontLookups)
        {
            ReadOnlySpan<byte> raw = eboot.Read(spec.Va, 16 * spec.Count);
            ulong[] keys = new ulong[spec.Count];
            short[] indices = new short[spec.Count];
            for (int i = 0; i < spec.Count; i++)
            {
                keys[i] = BinaryPrimitives.ReadUInt64LittleEndian(raw[(16 * i)..]);
                indices[i] = BinaryPrimitives.ReadInt16LittleEndian(raw[((16 * i) + 8)..]);
            }

            lookups.Add(new FontLookupData(spec, keys, indices));
        }

        var images = new List<ReadOnlyMemory<byte>>(layout.FontRecords.Count);
        foreach (FontRecordSpec spec in layout.FontRecords)
        {
            _ = eboot.Read(spec.Va, 10 * spec.Count);
            long file = eboot.FileOffset(spec.Va) ?? throw new EbootFormatException($"record table {spec.Name}: VA 0x{spec.Va:x} is not file-backed");
            var segment = eboot.Image.SegmentOf((ulong)spec.Va) ?? throw new EbootFormatException($"record table {spec.Name}: VA 0x{spec.Va:x} is not mapped");
            long remaining = (long)(segment.VAddr + segment.FileSize) - spec.Va;
            images.Add(eboot.Image.Data.Slice(checked((int)file), checked((int)remaining)));
        }

        int[] wide = new int[5];
        ReadOnlySpan<byte> wideRaw = eboot.Read(layout.FontWideBase, 10);
        for (int i = 0; i < 5; i++)
        {
            wide[i] = BinaryPrimitives.ReadUInt16LittleEndian(wideRaw[(2 * i)..]);
        }

        var tags = new string[TagCount];
        for (int i = 0; i < TagCount; i++)
        {
            ReadOnlySpan<byte> record = eboot.Read(layout.FontTags + (TagStride * i), TagStride);
            int nul = record.IndexOf((byte)0);
            tags[i] = Encoding.ASCII.GetString(nul >= 0 ? record[..nul] : record);
        }

        return new EbootFontData(eboot, lookups, images, wide, tags);
    }

    /// <summary>The tables in the form the text layout reads (<see cref="FontTables"/>).</summary>
    public FontTables ToEngineTables()
    {
        var lookups = Lookups.Select(l => new GlyphLookupTable(l.Spec.Name, l.Keys, [.. l.Indices.Select(i => (int)i)]));
        var records = _eboot.Layout.FontRecords.Select((s, i) => new GlyphRecordTable(s.Name, s.Count, RecordImages[i]));
        return new FontTables(lookups, records, WideBase);
    }

    /// <summary>
    /// The self-check of RR 3.3 item 2: the code still loads every table from the expected address, the lookup keys are strictly sorted and their
    /// indices a permutation, the tag names and the wide-grid bases are the documented ones (<c>EbootFontTables.verify</c>; fonts.md 10.7).
    /// An empty list means this eboot matches the analysed layout; another build fails loudly instead of rendering junk.
    /// </summary>
    public IReadOnlyList<string> Verify()
    {
        var issues = new List<string>();
        EbootLayout layout = _eboot.Layout;
        for (int i = 0; i < layout.FontLookups.Count; i++)
        {
            FontLookupSpec spec = layout.FontLookups[i];
            CheckLea(issues, "lookup", spec.Name, spec.LeaSite, spec.Va);
            FontLookupData data = Lookups[i];
            bool sorted = true;
            for (int k = 1; k < data.Keys.Length; k++)
            {
                if (data.Keys[k - 1] >= data.Keys[k])
                {
                    sorted = false;
                    break;
                }
            }

            if (!sorted)
            {
                issues.Add($"lookup {spec.Name}: keys not strictly sorted");
            }

            // Python checks the dict built from the table: duplicates collapse, so a duplicate key also breaks the permutation.
            var byKey = new Dictionary<ulong, short>();
            for (int k = 0; k < data.Keys.Length; k++)
            {
                byKey[data.Keys[k]] = data.Indices[k];
            }

            short[] values = [.. byKey.Values.OrderBy(v => v)];
            if (values.Length != spec.Count || values.Where((v, k) => v != k).Any())
            {
                issues.Add($"lookup {spec.Name}: indices are not a permutation of 0..{spec.Count - 1}");
            }
        }

        foreach (FontRecordSpec spec in layout.FontRecords)
        {
            CheckLea(issues, "records", spec.Name, spec.LeaSite, spec.Va);
        }

        if (layout.IsAnalysed && RipTarget(WideBaseSite) != WideBaseVa)
        {
            issues.Add("wide base table reference moved");
        }

        if (layout.IsAnalysed && RipTarget(TagsSite) != TagsVa)
        {
            issues.Add("tag table reference moved");
        }

        if (!Tags.SequenceEqual(StandardTags))
        {
            issues.Add($"tag names differ: ({string.Join(", ", Tags.Select(t => "'" + t + "'"))})");
        }

        if (!WideBase.SequenceEqual(ExpectedWideBase))
        {
            issues.Add($"unexpected wide texture bases ({string.Join(", ", WideBase)})");
        }

        return issues;
    }

    private static IReadOnlyList<string> StandardTags { get; } = FontTables.StandardTagNames;

    // a located table (package K25) has no lea site of its own: the locator proved the reference, so there is nothing to check here
    private void CheckLea(List<string> issues, string kind, string name, long site, long expected)
    {
        if (site == 0)
        {
            return;
        }

        long target = RipTarget(site);
        if (target != expected)
        {
            issues.Add($"{kind} {name}: lea at 0x{site:x} -> 0x{target:x}, expected 0x{expected:x}");
        }
    }

    /// <summary>The target of a 7-byte <c>lea reg, [rip+disp32]</c> at a text VA.</summary>
    private long RipTarget(long insnVa) => insnVa + 7 + _eboot.I32(insnVa + 3);
}

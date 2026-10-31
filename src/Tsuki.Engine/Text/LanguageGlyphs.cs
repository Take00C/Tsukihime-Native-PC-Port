namespace Tsuki.Engine.Text;

/// <summary>
/// The glyph chosen for one character or <c>[tag]</c> (tools/tsuki/font.py <c>Glyph</c>, reduced to
/// what the layout reads).
/// </summary>
/// <param name="Consumed">UTF-8 bytes consumed; 0 for an invalid lead byte (the engine consumes nothing).</param>
/// <param name="Tex">Texture slot (font page = slot - 0x21).</param>
/// <param name="X">Atlas rectangle left.</param>
/// <param name="Y">Atlas rectangle top.</param>
/// <param name="W">Atlas rectangle width (signed, like the engine's s16).</param>
/// <param name="H">Atlas rectangle height.</param>
/// <param name="Adj">Pen/draw x adjustment, already multiplied by the scale (a double, narrowed to float by the caller).</param>
public readonly record struct Glyph(int Consumed, int Tex, int X, int Y, int W, int H, double Adj);

/// <summary>
/// Glyph lookup of one language: a character (or <c>[tag]</c>) at a byte offset plus the style to its
/// rectangle in a font page, and the joined-dash pieces. Mirrors eboot VA 0x10810 / 0x11150 / 0x11750 /
/// 0x11B80 / 0x11ED0 as ported in tools/tsuki/font.py <c>Font</c> (docs/formats/fonts.md 5, 6).
/// </summary>
public sealed class LanguageGlyphs
{
    /// <summary>Glyph cell size of the fixed grids.</summary>
    public const int Cell = 73;

    private const int GridCols = 14;
    private const int PerPage = GridCols * 7;
    private const int FallbackSlot = 0x22;

    private readonly FontTables _tables;
    private readonly int _lang;
    private readonly GlyphLookupTable _narrow;
    private readonly GlyphLookupTable _wide;
    private readonly GlyphLookupTable _s1;
    private readonly GlyphLookupTable _s2;
    private readonly GlyphRecordTable?[] _recordsByStyle = new GlyphRecordTable?[6];

    internal LanguageGlyphs(FontTables tables, int lang)
    {
        _tables = tables;
        _lang = lang;
        string name = FontTables.LangNames[lang];
        _narrow = tables.Lookup("narrow");
        _wide = tables.Lookup(name + "_wide");
        _s1 = tables.Lookup(name + "_s1");
        _s2 = tables.Lookup(name + "_s2");
        for (int style = 0; style < 6; style++)
        {
            _recordsByStyle[style] = tables.Records(RecordTableName(lang, style));
        }
    }

    /// <summary>Name of the 10-byte record table for (language, style) (VA 0x112E7 jump table).</summary>
    public static string RecordTableName(int lang, int style)
    {
        string name = FontTables.LangNames[lang];
        switch (lang)
        {
            case 0:
                return style == 0 ? "ja_s0" : style == 2 ? "ja_s2" : "ja_s1";
            case 1:
                return style switch { 0 => "en_s0", 2 => "en_s2", 3 => "en_s3", 4 => "en_s4", 5 => "en_s5", _ => "en_s1" };
            case 3:
                return style == 0 ? "zt_s0" : "zt_s1";
            default:
                return style == 0 ? name + "_s0" : style == 2 ? name + "_s2" : name + "_s1";
        }
    }

    /// <summary>
    /// The glyph of the character or <c>[tag]</c> at <paramref name="b"/>[<paramref name="pos"/>]
    /// (VA 0x11750). <c>Consumed == 0</c> marks a NUL byte, the end of <paramref name="b"/> or an invalid
    /// lead byte; callers must handle it.
    /// </summary>
    public Glyph CharGlyph(ReadOnlySpan<byte> b, int pos, int style, int kind, double scale)
    {
        if (pos >= b.Length || b[pos] == 0)
        {
            return new Glyph(0, 0xFFFF, 0, 0, Cell, Cell, 0.0);
        }

        if (b[pos] == 0x5B)
        {
            var tags = _tables.Tags;
            for (int k = 0; k < tags.Count; k++)
            {
                if (b[pos..].StartsWith(tags[k]))
                {
                    return TagGlyph(k, tags[k].Length, kind, scale);
                }
            }
        }

        (ulong key, int n) = DecodeKey(b, pos);
        if (n == 0)
        {
            return new Glyph(0, 0xFFFF, 0, 0, 0, 0, 0.0);
        }

        if (TryGetGlyph(key, style, scale, n, out var g))
        {
            return g;
        }

        return new Glyph(n, FallbackSlot, 0, 0, n <= 2 ? 42 : Cell, Cell, 0.0);
    }

    /// <summary>
    /// Joined-dash piece for the character at <paramref name="pos"/> (VA 0x11B80): 0..8, or -1 to draw the
    /// normal glyph. <paramref name="prevPos"/> is the start of the previous parse element, or -1.
    /// </summary>
    public static int DashVariantAt(ReadOnlySpan<byte> b, int pos, int prevPos, bool lineStart, bool lineEnd, bool flagB)
    {
        ulong key = Key3At(b, pos);
        int baseVariant = key switch { 0xE296A0 => 0, 0xE28095 => 3, 0xE29480 => 6, _ => -1 };
        if (baseVariant < 0)
        {
            return -1;
        }

        bool hasPrev = Key3At(b, prevPos) == key && !lineStart;
        bool hasNext = Key3At(b, pos + 3) == key && !lineEnd;
        if (hasPrev && hasNext)
        {
            return baseVariant + 1;
        }

        if (hasPrev)
        {
            return baseVariant + (flagB ? 0 : 2);
        }

        if (hasNext)
        {
            return baseVariant + (flagB ? 2 : 0);
        }

        return -1;
    }

    /// <summary>Rectangle of a joined-dash piece on page 61 (VA 0x11ED0).</summary>
    public Glyph DashGlyph(int variant, int style, int kind, double scale)
    {
        int y = _lang switch { 0 => style == 2 ? 146 : 73, 2 => 219, 3 => 292, _ => 73 };
        int xb = (_lang == 1 || _lang == 4) && style is 3 or 4 or 5 ? 511 : 0;
        int part = variant % 3;
        int x = xb + variant / 3 * 146 + part switch { 0 => 0, 1 => 41, _ => 78 };
        int w = part == 0 ? 73 : 68;
        double adj = part == 0 ? 0.0 : scale * (kind < 2 ? 5.0 : -0.5);
        return new Glyph(3, 0x5E, x, y, w, Cell, adj);
    }

    /// <summary>Engine UTF-8 decode (VA 0x11B00 / 0x11750): the packed key and its length; length 0 = invalid lead.</summary>
    internal static (ulong Key, int Length) DecodeKey(ReadOnlySpan<byte> b, int pos)
    {
        int c = b[pos];
        int n;
        if (c < 0x80)
        {
            return ((ulong)c, 1);
        }

        if ((c & 0xE0) == 0xC0)
        {
            n = 2;
        }
        else if ((c & 0xF0) == 0xE0)
        {
            n = 3;
        }
        else if ((c & 0xF8) == 0xF0)
        {
            n = 4;
        }
        else
        {
            return ((ulong)c, 0);
        }

        ulong key = 0;
        for (int i = 0; i < n; i++)
        {
            key = (key << 8) | (pos + i < b.Length ? b[pos + i] : (byte)0);
        }

        return (key, n);
    }

    private static ulong Key3At(ReadOnlySpan<byte> b, int pos)
    {
        if (pos < 0 || pos >= b.Length || (b[pos] & 0xF0) != 0xE0)
        {
            return 0;
        }

        ulong key = 0;
        for (int i = 0; i < 3; i++)
        {
            key = (key << 8) | (pos + i < b.Length ? b[pos + i] : (byte)0);
        }

        return key;
    }

    private Glyph TagGlyph(int k, int length, int kind, double scale)
    {
        double adjFar = scale * (kind < 2 ? 5.0 : -0.5);
        if (k >= 38)
        {
            return new Glyph(length, 0x61, 0, 219, 68, 73, adjFar);
        }

        if (k >= 7)
        {
            int j = k - 7;
            return new Glyph(length, 0x5F, j % 14 * 68, j / 14 * 73, 68, 73, adjFar);
        }

        if (k == 4)
        {
            return new Glyph(length, 0x5E, 307, 0, 46, 73, 0.0);
        }

        return new Glyph(length, 0x5E, 73 * k, 0, 73, 73, 0.0);
    }

    private GlyphLookupTable LookupTableFor(ulong key, int style) => style switch
    {
        0 => key >= 0x10000 ? _wide : _narrow,
        2 => _s2,
        _ => _s1,
    };

    private bool TryGetGlyph(ulong key, int style, double scale, int consumed, out Glyph glyph)
    {
        int idx = LookupTableFor(key, style).Get(key);
        if (style != 0 && idx < 0)
        {
            idx = LookupTableFor(key, 0).Get(key);
            style = 0;
        }

        if (idx < 0)
        {
            glyph = default;
            return false;
        }

        if (key >= 0x10000 && style == 0)
        {
            int tex = _tables.WideBase[_lang] + idx / PerPage;
            int x = idx % GridCols * Cell;
            int y = idx % PerPage / GridCols * Cell;
            int w = Cell;
            double wideAdj = 0.0;
            if (_lang == 0 && key >= 0xEFBDA1 && key <= 0xEFBE9F)
            {
                x += 15;
                w = 42;
                if ((key & ~1UL) == 0xEFBE9E)
                {
                    wideAdj = -2.0 * scale;
                }
            }

            glyph = new Glyph(consumed, tex, x, y, w, Cell, wideAdj);
            return true;
        }

        var r = (_recordsByStyle[style] ?? throw new InvalidOperationException("no record table for style " + style)).Get(idx);
        double adj = 0.0;
        if (_lang == 1 && style is 3 or 4 or 5)
        {
            // italic: keep the upright advance (VA 0x11531)
            int? w2 = null;
            if (style == 3)
            {
                int i2 = _tables.Lookup(key >= 0x10000 ? "en_wide" : "narrow").Get(key);
                if (i2 >= 0)
                {
                    w2 = key > 0xFFFF ? Cell : _tables.Records("en_s0").Get(i2).W;
                }
            }
            else if (style == 4)
            {
                int i2 = _tables.Lookup("en_s1").Get(key);
                if (i2 >= 0)
                {
                    w2 = _tables.Records("en_s1").Get(i2).W;
                }
            }
            else
            {
                int i2 = _tables.Lookup("en_s2").Get(key);
                if (i2 >= 0)
                {
                    w2 = _tables.Records("en_s2").Get(i2).W;
                }
            }

            if (w2 is { } upright)
            {
                adj = (double)(upright - r.W) * scale;
            }
        }

        glyph = new Glyph(consumed, r.Tex, r.X, r.Y, r.W, r.H, adj);
        return true;
    }
}

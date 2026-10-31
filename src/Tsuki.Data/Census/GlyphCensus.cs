using System.Collections.Concurrent;
using Tsuki.Data.Containers;
using Tsuki.Data.Fonts;
using Tsuki.Data.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Data.Census;

/// <summary>
/// The fonts part of the census: for each of the four selectable languages, every character of every text line the scripts reference (and of
/// the lines those reference with <c>$</c>) must have an entry in the glyph lookup tables of the eboot (<c>narrow</c> below U+10000 in UTF-8
/// key order, else <c>&lt;lang&gt;_wide</c>; docs/formats/fonts.md 5, 6), and the glyph that entry selects must lie on a page of the language's
/// <c>ft_&lt;lang&gt;.mrg</c> (65 pages), inside the page. The glyph is found as the text layout finds it (<see cref="LanguageGlyphs.CharGlyph"/>).
/// </summary>
internal static class GlyphCensus
{
    private const int FirstSlot = FontArchive.FirstSlot;

    public static IReadOnlyList<GlyphCensusLanguage> Run(
        GameFolderSource source, IReadOnlyList<int> ids, ParallelOptions parallel, List<CensusFinding> findings)
    {
        FontTables tables = source.Tables.FontTables;
        var results = new List<GlyphCensusLanguage>();
        foreach (Language lang in TextRules.SelectableLanguages)
        {
            results.Add(RunLanguage(source, tables, lang, ids, parallel, findings));
        }

        return results;
    }

    private static GlyphCensusLanguage RunLanguage(
        GameFolderSource source, FontTables tables, Language lang, IReadOnlyList<int> ids, ParallelOptions parallel, List<CensusFinding> findings)
    {
        string code = TextRules.Code(lang);
        TextTable table = source.Text.Table(lang);
        GlyphLookupTable narrow = tables.Lookup("narrow");
        GlyphLookupTable wide = tables.Lookup(code + "_wide");

        // 1. the characters of the lines (followed through $ references)
        var seen = new HashSet<int>(ids);
        var queue = new Queue<int>(ids);
        var chars = new Dictionary<ulong, int>();
        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            foreach (MarkupToken token in Markup.Flatten(Markup.Tokenize(table.LineBytes(id).Span, lang)))
            {
                switch (token)
                {
                    case TextToken text:
                        Count(text.Text.AsSpan(), chars);
                        break;
                    case RefToken reference when reference.LineId >= 0 && reference.LineId < table.Count && seen.Add(reference.LineId):
                        queue.Enqueue(reference.LineId);
                        break;
                }
            }
        }

        // 2. each distinct character: lookup entry, glyph, page
        LanguageGlyphs glyphs = tables.For(lang);
        FontArchive font = source.FontFile(lang);
        var pageSize = new ConcurrentDictionary<int, (int W, int H)>();
        var missing = new List<(string, int)>();
        var pages = new HashSet<int>();
        var rects = new HashSet<(int, int, int, int, int)>();
        int outOfPage = 0;
        foreach ((ulong key, int count) in chars.OrderBy(c => c.Key))
        {
            GlyphLookupTable lookup = key >= 0x10000 ? wide : narrow;
            if (lookup.Get(key) < 0)
            {
                missing.Add((key.ToString("x6", System.Globalization.CultureInfo.InvariantCulture), count));
                continue;
            }

            byte[] utf8 = Utf8Of(key);
            Glyph g = glyphs.CharGlyph(utf8, 0, style: 0, kind: 0, scale: 1.0);
            int page = g.Tex - FirstSlot;
            if (g.Consumed != utf8.Length || page < 0 || page >= FontArchive.PageCount)
            {
                findings.Add(new CensusFinding("glyph", $"{code} U+{key:x}: glyph slot 0x{g.Tex:x} is not a page of ft_{code}.mrg", null));
                continue;
            }

            pages.Add(page);
            if (!rects.Add((g.Tex, g.X, g.Y, g.W, g.H)))
            {
                continue;
            }

            (int w, int h) = pageSize.GetOrAdd(page, p =>
            {
                FontPageData data = font.ReadPageData(p);
                return (data.Width, data.Height);
            });
            if (g.X < 0 || g.Y < 0 || g.W < 0 || g.X + g.W > w || g.Y + g.H > h)
            {
                outOfPage++;
                findings.Add(new CensusFinding("glyph", $"{code} U+{key:x}: rectangle {g.X},{g.Y} {g.W}x{g.H} is outside page {page} ({w}x{h})", null));
            }
        }

        foreach ((string key, int count) in missing)
        {
            findings.Add(new CensusFinding(
                "glyph", $"{code}: no glyph lookup entry for U+{key} ({count} uses)", CensusAllowList.GlyphFallbackReason(code, key, count)));
        }

        _ = parallel;
        return new GlyphCensusLanguage(code, seen.Count, chars.Count, chars.Values.Sum(v => (long)v), missing, pages.Count, outOfPage);
    }

    /// <summary>Counts the characters of UTF-8 text by their engine key (the UTF-8 bytes as a big-endian integer).</summary>
    private static void Count(ReadOnlySpan<byte> text, Dictionary<ulong, int> chars)
    {
        int i = 0;
        while (i < text.Length)
        {
            int n = text[i] switch { < 0x80 => 1, >= 0xC0 and < 0xE0 => 2, >= 0xE0 and < 0xF0 => 3, >= 0xF0 and < 0xF8 => 4, _ => 1 };
            n = Math.Min(n, text.Length - i);
            ulong key = 0;
            for (int j = 0; j < n; j++)
            {
                key = (key << 8) | text[i + j];
            }

            chars[key] = chars.GetValueOrDefault(key) + 1;
            i += n;
        }
    }

    private static byte[] Utf8Of(ulong key)
    {
        int n = key switch { < 0x100 => 1, < 0x10000 => 2, < 0x1000000 => 3, _ => 4 };
        byte[] bytes = new byte[n];
        for (int j = n - 1; j >= 0; j--)
        {
            bytes[j] = (byte)key;
            key >>= 8;
        }

        return bytes;
    }
}

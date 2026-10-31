using Tsuki.Engine.Text;

namespace Tsuki.Tests.Synthetic.Text;

/// <summary>
/// A glyph-table set that holds exactly the entries the Python oracle read for the unit cases
/// (<see cref="SyntheticLayoutCases"/>), so the text layout runs without game data.
/// </summary>
internal static class SyntheticFont
{
    private static readonly string[] LookupNames =
    [
        "narrow", "ja_s1", "en_s1", "zc_s1", "zt_s1", "ko_s1", "ja_s2", "en_s2", "zc_s2", "zt_s2", "ko_s2",
        "ja_wide", "en_wide", "zc_wide", "zt_wide", "ko_wide",
    ];

    private static readonly string[] RecordNames =
    [
        "ja_s0", "ja_s2", "ja_s1", "en_s0", "en_s2", "en_s3", "en_s4", "en_s5", "en_s1", "zc_s0", "zc_s2", "zc_s1",
        "zt_s0", "zt_s1", "ko_s0", "ko_s2", "ko_s1",
    ];

    public static FontTables Create()
    {
        var lookups = LookupNames.Select(name =>
        {
            var entries = SyntheticLayoutCases.Lookups.Where(e => e.Table == name).ToArray();
            return new GlyphLookupTable(name, entries.Select(e => e.Key).ToArray(), entries.Select(e => e.Index).ToArray());
        }).ToArray();
        var records = RecordNames.Select(name =>
        {
            var entries = SyntheticLayoutCases.Records.Where(e => e.Table == name).ToArray();
            int count = entries.Length == 0 ? 0 : entries.Max(e => e.Index) + 1;
            var rects = new GlyphRect[count];
            foreach (var e in entries)
            {
                rects[e.Index] = new GlyphRect(e.Tex, e.X, e.Y, e.W, e.H);
            }

            return new GlyphRecordTable(name, count, GlyphRecordTable.Pack(rects));
        }).ToArray();
        return new FontTables(lookups, records, [59, 59, 59, 57, 59]);
    }
}

using System.Buffers.Binary;
using System.Text.Json;
using Tsuki.Data.Containers;
using Tsuki.Engine.Text;

namespace Tsuki.Tests.Data.Text.Layout;

/// <summary>
/// Inputs of the text layout differential tests: the glyph tables of the golden eboot export, the
/// script_text lines of the user's game folder and the golden files of <c>tools/oracle</c>.
/// </summary>
internal static class LayoutData
{
    public const int LanguageCount = 4;

    public const string TextboxCommand = "$env:PYTHONPATH='D:\\TsukihimePort\\tools'; .venv\\Scripts\\python.exe -m oracle.textbox";
    public const string SliceCommand = "$env:PYTHONPATH='D:\\TsukihimePort\\tools'; .venv\\Scripts\\python.exe -m oracle.run slice";

    private static readonly Lazy<FontTables> Fonts = new(LoadFontTables);
    private static readonly Lazy<byte[][][]> Lines = new(LoadLines);

    /// <summary>The glyph tables (lookup and record tables) of <c>samples/golden/slice/eboot/font_tables.json</c>.</summary>
    public static FontTables FontTables => Fonts.Value;

    /// <summary>Script lines of ja, en, zc, zt without their CR LF (docs/formats/text.md 2).</summary>
    public static byte[][][] ScriptLines => Lines.Value;

    public static string Repo(params string[] parts) => Path.Combine([GoldenData.RepoRoot, .. parts]);

    /// <summary>A file of <c>samples/golden/</c>; fails with the command that creates it when missing.</summary>
    public static string GoldenFile(string area, string relative, string command)
    {
        string p = Repo("samples", "golden", area, relative);
        if (!File.Exists(p))
        {
            throw new FileNotFoundException($"golden file {p} is missing; create it with: {command}");
        }

        return p;
    }

    private static FontTables LoadFontTables()
    {
        string path = GoldenFile("slice", Path.Combine("eboot", "font_tables.json"), SliceCommand);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var lookups = new List<GlyphLookupTable>();
        foreach (var t in root.GetProperty("lookups").EnumerateArray())
        {
            var keys = new List<ulong>();
            var idx = new List<int>();
            foreach (var e in t.GetProperty("entries").EnumerateArray())
            {
                keys.Add(e[0].GetUInt64());
                idx.Add(e[1].GetInt32());
            }

            lookups.Add(new GlyphLookupTable(t.GetProperty("name").GetString()!, keys, idx));
        }

        // The engine indexes the record arrays without a bounds check (fonts.md 5.2): the records some
        // synthetic messages read past the end of a table are exported next to the lines.
        string overrunPath = GoldenFile("textbox", Path.Combine("fonts", "font_overrun.json"), TextboxCommand);
        using var overrunDoc = JsonDocument.Parse(File.ReadAllBytes(overrunPath));
        var overrun = overrunDoc.RootElement.GetProperty("hits").EnumerateArray()
            .Select(h => (Table: h[0].GetString()!, Index: h[1].GetInt32(),
                Rect: new GlyphRect(h[2].GetInt32(), h[3].GetInt32(), h[4].GetInt32(), h[5].GetInt32(), h[6].GetInt32())))
            .ToList();

        var records = new List<GlyphRecordTable>();
        foreach (var t in root.GetProperty("records").EnumerateArray())
        {
            string name = t.GetProperty("name").GetString()!;
            var rects = new List<GlyphRect>();
            foreach (var e in t.GetProperty("entries").EnumerateArray())
            {
                rects.Add(new GlyphRect(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32(), e[4].GetInt32()));
            }

            int count = rects.Count;
            foreach (var hit in overrun.Where(h => h.Table == name))
            {
                while (rects.Count <= hit.Index)
                {
                    rects.Add(default);
                }

                rects[hit.Index] = hit.Rect;
            }

            records.Add(new GlyphRecordTable(name, count, GlyphRecordTable.Pack(rects)));
        }

        var wideBase = root.GetProperty("wide_base").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var tags = root.GetProperty("tags").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(FontTables.StandardTagNames, tags);
        return new FontTables(lookups, records, wideBase);
    }

    private static byte[][][] LoadLines()
    {
        using var mrg = StandaloneMrg.Open(GameImage.Path("script_text.mrg"));
        var c = mrg.Container;
        Assert.Equal(10, c.Count);
        var result = new byte[LanguageCount][][];
        for (int lg = 0; lg < LanguageCount; lg++)
        {
            ReadOnlySpan<byte> table = c.Data.Span.Slice((int)c.Sections[2 * lg].Offset, c.Sections[2 * lg].Size);
            ReadOnlySpan<byte> blob = c.Data.Span.Slice((int)c.Sections[2 * lg + 1].Offset, c.Sections[2 * lg + 1].Size);
            var offsets = new List<int>();
            for (int i = 0; i + 4 <= table.Length; i += 4)
            {
                uint v = BinaryPrimitives.ReadUInt32BigEndian(table[i..]);
                if (v == 0xFFFFFFFF)
                {
                    break;
                }

                offsets.Add((int)v);
            }

            int count = offsets.Count - 2;                     // two trailing blob_size guard entries
            var lines = new byte[count][];
            for (int k = 0; k < count; k++)
            {
                var raw = blob[offsets[k]..offsets[k + 1]];
                if (raw.Length >= 2 && raw[^2] == 0x0D && raw[^1] == 0x0A)
                {
                    raw = raw[..^2];
                }

                lines[k] = raw.ToArray();
            }

            result[lg] = lines;
        }

        return result;
    }
}

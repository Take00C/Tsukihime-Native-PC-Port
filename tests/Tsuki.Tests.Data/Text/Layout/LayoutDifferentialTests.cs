using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Tests.Data.Text.Layout;

/// <summary>
/// The C# <c>Text_Layout</c> against the engine and the Python oracle on the real data (PACE 7.2 #3):
/// the 601 frozen engine layouts first, then every script line of ja, en, zc and zt (175,876) in two
/// call configurations by SHA-1 of the canonical block.
/// </summary>
public sealed class LayoutDifferentialTests
{
    private static readonly (string Name, LayoutParams Params)[] Configs =
    [
        ("main", new LayoutParams(Flags: 0, Window: 0)),
        ("bubble", new LayoutParams(Flags: 1 << 20, Window: 5)),
    ];

    private static readonly string[] LanguageNames = ["ja", "en", "zc", "zt"];

    [GameDataFact]
    public void TheScaleAndWindowConstantsEqualTheEbootFloat32Tables()
    {
        // followups.md: SCALE_TABLE[8], [9] (VA 0x2D3730) were 1 ulp off in tsuki.textbox.
        string path = LayoutData.GoldenFile("slice", Path.Combine("eboot", "textbox_tables.json"), LayoutData.SliceCommand);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        byte[] raw = Convert.FromHexString(doc.RootElement.GetProperty("scales").GetProperty("raw").GetString()!);
        Assert.Equal(40, raw.Length);
        for (int i = 0; i < 10; i++)
        {
            uint bits = BitConverter.ToUInt32(raw, 4 * i);
            Assert.True(bits == BitConverter.SingleToUInt32Bits(TextLayout.Scales[i]),
                $"scale {i}: eboot 0x{bits:x8}, port 0x{BitConverter.SingleToUInt32Bits(TextLayout.Scales[i]):x8}");
        }

        byte[] metrics = Convert.FromHexString(doc.RootElement.GetProperty("font_metrics").GetProperty("raw").GetString()!);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(BitConverter.ToInt32(metrics, 4 * i), TextLayout.FontMetrics[i]);
        }

        byte[] colours = Convert.FromHexString(doc.RootElement.GetProperty("colours").GetProperty("raw").GetString()!);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(BitConverter.ToUInt16(colours, 2 * i), TextLayout.Colors[i]);
        }
    }

    [GameDataFact]
    public void TheGoldenEngineLayoutsAreEqualRecordForRecord()
    {
        string path = LayoutData.Repo("samples", "textbox", "engine_layout_golden.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var cases = doc.RootElement.GetProperty("cases");
        var lines = LayoutData.ScriptLines;
        var m = new Mismatches();
        int count = 0;
        foreach (var c in cases.EnumerateArray())
        {
            count++;
            int lang = c.GetProperty("lang").GetInt32();
            int id = c.GetProperty("id").GetInt32();
            int flags = c.GetProperty("flags").GetInt32();
            int window = c.GetProperty("window").GetInt32();
            string tag = $"{LanguageNames[lang]}#{id} flags {flags:x} window {window}";
            var b = TextLayout.Layout(lines[lang][id], (Language)lang, LayoutData.FontTables, new LayoutParams(Flags: flags, Window: window));
            m.Equal($"{tag} n", c.GetProperty("n").GetInt32(), b.N);
            m.Equal($"{tag} nlines", c.GetProperty("nlines").GetInt32(), b.NLines);
            CompareBits(m, $"{tag} line_height", c.GetProperty("line_height"), b.LineHeight);
            CompareBits(m, $"{tag} line_width", c.GetProperty("line_width"), b.LineWidth);
            var recs = c.GetProperty("records");
            m.Equal($"{tag} record count", recs.GetArrayLength(), b.Records.Length);
            int k = 0;
            foreach (var r in recs.EnumerateArray())
            {
                if (k >= b.Records.Length)
                {
                    break;
                }

                var g = b.Records[k];
                int[] want = [.. r.EnumerateArray().Select(e => (int)e.GetInt64())];
                int[] got =
                [
                    g.Index, g.Flags, g.Color, g.Ext, g.Line, g.Tex, g.X, g.Y, g.X2, g.Y2, g.Ctr,
                    (int)BitConverter.SingleToUInt32Bits(g.Dx), (int)BitConverter.SingleToUInt32Bits(g.Dy),
                    (int)BitConverter.SingleToUInt32Bits(g.Sx), (int)BitConverter.SingleToUInt32Bits(g.Sy),
                ];
                if (!want.AsSpan().SequenceEqual(got))
                {
                    m.Add($"{tag} record {k}: expected [{string.Join(",", want)}], got [{string.Join(",", got)}]");
                }

                k++;
            }
        }

        Assert.Equal(601, count);
        m.AssertNone("golden engine layouts (601 cases)");
    }

    private static void CompareBits(Mismatches m, string what, JsonElement expected, float[] actual)
    {
        int k = 0;
        foreach (var e in expected.EnumerateArray())
        {
            uint want = unchecked((uint)e.GetInt64());
            uint got = BitConverter.SingleToUInt32Bits(actual[k]);
            if (want != got)
            {
                m.Add($"{what}[{k}]: expected 0x{want:x8}, got 0x{got:x8}");
            }

            k++;
        }
    }

    [GameDataFact]
    public void EveryScriptLineHasTheOracleLayoutInTheOrdinaryMessageWindow() => CompareAllLines("main");

    [GameDataFact]
    public void EveryScriptLineHasTheOracleLayoutInASpeechBubble() => CompareAllLines("bubble");

    [GameDataFact]
    public void TheUnicornVerifiedSyntheticEdgeCasesEqualTheOracle() => CompareCases("synthetic", 2772);

    [GameDataFact]
    public void TheSeededRandomMessagesEqualTheOracle() => CompareCases("fuzz", 6000);

    private static void CompareCases(string part, int expectedCount)
    {
        string path = LayoutData.GoldenFile("textbox", Path.Combine(part, "cases.json"), LayoutData.TextboxCommand);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var cases = doc.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(expectedCount, cases.Length);
        var fonts = LayoutData.FontTables;
        var m = new Mismatches();
        Parallel.For(0, cases.Length, i =>
        {
            var c = cases[i];
            var lang = (Language)c.GetProperty("lang").GetInt32();
            byte[] text = Convert.FromHexString(c.GetProperty("text").GetString()!);
            var d = new LayoutParams();
            int I(string name, int fallback) => c.TryGetProperty(name, out var e) ? e.GetInt32() : fallback;
            double D(string name, double fallback) => c.TryGetProperty(name, out var e) ? e.GetDouble() : fallback;
            var p = new LayoutParams(
                Width: I("width", d.Width), Height: I("height", d.Height), Spacing: I("spacing", d.Spacing),
                Leading: I("leading", d.Leading), Color: I("color", d.Color), Flags: I("flags", d.Flags),
                Sx: D("sx", d.Sx), Sy: D("sy", d.Sy), Block: I("block", d.Block), Window: I("window", d.Window),
                SavedX: D("saved_x", d.SavedX));
            var block = TextLayout.Layout(text, lang, fonts, p);
            string got = Convert.ToHexStringLower(SHA1.HashData(LayoutCanonical.Serialize(block)));
            string want = c.GetProperty("sha1").GetString()!;
            if (got != want)
            {
                m.Add($"{part} case {i} ({lang}, flags 0x{p.Flags:x}, window {p.Window}, n={c.GetProperty("n").GetInt32()}, "
                    + $"nlines {c.GetProperty("nlines").GetInt32()} vs {block.NLines}): hash differs; text {Convert.ToHexString(text)[..Math.Min(120, text.Length * 2)]}");
            }
        });
        m.AssertNone($"{part} cases ({cases.Length})");
    }

    private static void CompareAllLines(string config)
    {
        var p = Configs.Single(c => c.Name == config).Params;
        var lines = LayoutData.ScriptLines;
        var fonts = LayoutData.FontTables;
        var m = new Mismatches();
        var watch = Stopwatch.StartNew();
        long layouts = 0;
        for (int lg = 0; lg < LayoutData.LanguageCount; lg++)
        {
            string file = LayoutData.GoldenFile("textbox", Path.Combine("lines", $"layout_{config}_{LanguageNames[lg]}.sha1"), LayoutData.TextboxCommand);
            byte[] expected = File.ReadAllBytes(file);
            int count = lines[lg].Length;
            Assert.Equal(43969, count);
            Assert.Equal(20 * count, expected.Length);
            var language = (Language)lg;
            int lang = lg;
            const int chunk = 256;
            Parallel.For(0, (count + chunk - 1) / chunk, ci =>
            {
                Span<byte> digest = stackalloc byte[20];
                for (int id = ci * chunk; id < Math.Min(count, (ci + 1) * chunk); id++)
                {
                    var block = TextLayout.Layout(lines[lang][id], language, fonts, p);
                    SHA1.HashData(LayoutCanonical.Serialize(block), digest);
                    if (!digest.SequenceEqual(expected.AsSpan(20 * id, 20)))
                    {
                        m.Add($"{LanguageNames[lang]}#{id} ({config}): hash differs; compare with `python -m oracle.textbox dump {LanguageNames[lang]} {id} --config {config}`");
                    }
                }
            });
            layouts += count;
        }

        watch.Stop();
        Assert.Equal(4 * 43969, layouts);
        m.AssertNone($"{layouts:N0} layouts, {config} ({watch.Elapsed.TotalSeconds:F1} s)");
    }
}

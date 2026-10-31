using System.Security.Cryptography;
using System.Text.Json;
using Tsuki.Data.Containers;
using Tsuki.Data.Fonts;
using Tsuki.Data.IO;
using Tsuki.Data.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Tests.Data.Scripts;
using Tsuki.Tests.Data.Text;

namespace Tsuki.Tests.Data.Fonts;

/// <summary>
/// The glyph pages of <c>ft_&lt;lang&gt;.mrg</c> against the oracle (<c>tsuki.font.FontPages</c>, run by <c>font_oracle.py pages</c>): 5 x 65
/// pages, header, palette, indices, alpha plane and RGBA, and against the exporter's page hashes of the slice (<c>samples/golden/slice/fonts</c>).
/// </summary>
public sealed class FontPageTests
{
    private const string Oracle = "tests/Tsuki.Tests.Data/Fonts/font_oracle.py";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(6);

    private static string Sha1(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(SHA1.HashData(b));

    [GameDataFact]
    public void All325PagesOfTheFiveFontFilesEqualTheOracle()
    {
        var files = new DirectoryGameFiles(GameFolder.Require());
        var archives = new Dictionary<string, FontArchive>();
        foreach (Language lang in Enum.GetValues<Language>())
        {
            archives[TextRules.Code(lang)] = FontArchive.Open(files, lang);
        }

        var mismatches = new Mismatches();
        int pages = 0;
        int withGlyphs = 0;
        foreach (JsonDocument doc in OracleRun.Records(Oracle, "pages", "page", Timeout))
        {
            using (doc)
            {
                JsonElement e = doc.RootElement;
                string lang = e.GetProperty("lang").GetString()!;
                int section = e.GetProperty("section").GetInt32();
                FontPageData page = archives[lang].ReadPageData(section);
                string label = $"ft_{lang} page {section}";
                mismatches.Equal(label + " width", e.GetProperty("w").GetInt32(), page.Width);
                mismatches.Equal(label + " height", e.GetProperty("h").GetInt32(), page.Height);
                mismatches.Equal(label + " header", string.Join(",", e.GetProperty("header").EnumerateArray().Select(x => x.GetInt32())), string.Join(",", page.Header));
                mismatches.Equal(label + " flag", e.GetProperty("flag").GetInt32(), page.Flag);
                mismatches.Equal(label + " slot", FontArchive.FirstSlot + section, page.Slot);
                mismatches.Equal(label + " indices", e.GetProperty("indices_sha1").GetString(), Sha1(page.Indices));
                mismatches.Equal(label + " palette", e.GetProperty("palette_sha1").GetString(), Sha1(page.Palette));
                mismatches.Equal(label + " alpha", e.GetProperty("alpha_sha1").GetString(), Sha1(page.ToCoverage()));
                mismatches.Equal(label + " rgba", e.GetProperty("rgba_sha1").GetString(), Sha1(page.ToRgba()));
                pages++;
                if (page.Flag == 1)
                {
                    withGlyphs++;
                }
            }
        }

        mismatches.AssertNone("font pages");
        Assert.Equal(325, pages);
        Assert.True(withGlyphs > 100, $"only {withGlyphs} pages hold glyphs");
    }

    [GameDataFact]
    public void ThePagesOfJaAndEnEqualTheExportersHashesAndTheContractPageIsTheSameRgba()
    {
        var files = new DirectoryGameFiles(GameFolder.Require());
        int compared = 0;
        foreach (Language lang in new[] { Language.Ja, Language.En })
        {
            string code = TextRules.Code(lang);
            string path = Path.Combine(GoldenData.RepoRoot, "samples", "golden", "slice", "fonts", $"ft_{code}.json");
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
            FontArchive archive = FontArchive.Open(files, lang);
            foreach (JsonElement p in doc.RootElement.GetProperty("pages").EnumerateArray())
            {
                int section = p.GetProperty("section").GetInt32();
                FontPageData data = archive.ReadPageData(section);
                Assert.Equal(p.GetProperty("width").GetInt32(), data.Width);
                Assert.Equal(p.GetProperty("height").GetInt32(), data.Height);
                Assert.Equal(p.GetProperty("flag").GetInt32(), data.Flag);
                Assert.Equal(p.GetProperty("slot").GetInt32(), data.Slot);
                Assert.Equal(p.GetProperty("header").EnumerateArray().Select(x => x.GetInt32()).ToArray(), data.Header.Select(x => (int)x).ToArray());
                Assert.Equal(p.GetProperty("indices_sha1").GetString(), Sha1(data.Indices));
                Assert.Equal(p.GetProperty("alpha_sha1").GetString(), Sha1(data.ToCoverage()));
                Assert.Equal(p.GetProperty("rgba_sha1").GetString(), Sha1(data.ToRgba()));
                if (section is 0 or 1 or 64)
                {
                    FontPage page = archive.ReadPage(section);
                    Assert.Equal(lang, page.Language);
                    Assert.Equal(section, page.Page);
                    Assert.Equal((data.Width, data.Height), (page.Width, page.Height));
                    Assert.Equal(p.GetProperty("rgba_sha1").GetString(), Sha1(page.Rgba.Span));
                }

                compared++;
            }
        }

        Assert.Equal(130, compared);
    }

    [GameDataFact]
    public void PageZeroIsABlank256SquareAndTheOthersAre1024Square()
    {
        var files = new DirectoryGameFiles(GameFolder.Require());
        FontArchive archive = FontArchive.Open(files, Language.Ja);
        FontPageData blank = archive.ReadPageData(0);
        Assert.Equal((256, 256, 0), (blank.Width, blank.Height, blank.Flag));
        FontPageData glyphs = archive.ReadPageData(1);
        Assert.Equal((1024, 1024, 1), (glyphs.Width, glyphs.Height, glyphs.Flag));
        Assert.Equal(0x22, glyphs.Slot);
        Assert.Throws<ArgumentOutOfRangeException>(() => archive.ReadPageData(65));
        Assert.Throws<ArgumentOutOfRangeException>(() => archive.ReadPageData(-1));
    }

    [GameDataFact]
    public void OpeningAFontFileReadsOnlyItsSectionTable()
    {
        var files = new CountingFiles(new DirectoryGameFiles(GameFolder.Require()));
        FontArchive archive = FontArchive.Open(files, Language.Zt);
        Assert.True(files.BytesRead < 4096, $"opening read {files.BytesRead} bytes");
        long before = files.BytesRead;
        _ = archive.ReadPageData(3);
        Assert.True(files.BytesRead - before < files.Length("image/ft_zt.mrg") / 20);
    }

    // -- no game data ---------------------------------------------------------------------------------------------------

    private static byte[] PageContainer(int w, int h, byte[] indices, byte[] palette, ushort[]? header = null, int flag = 1, bool shortHeader = false)
    {
        header ??= [(ushort)w, (ushort)h, (ushort)w, (ushort)h, 1, 1, 1, 1];
        var hdr = new List<byte>();
        foreach (ushort v in header)
        {
            hdr.AddRange(BitConverter.GetBytes(v));
        }

        hdr.AddRange(palette);
        if (!shortHeader)
        {
            hdr.Add((byte)flag);
        }
        else
        {
            hdr.RemoveRange(hdr.Count - 10, 10);
        }

        return MiniMzp.Build([[.. hdr], MiniMzx.Literal(indices, indices.Length)]);
    }

    private static byte[] Palette(Func<int, (byte R, byte G, byte B, byte A)> entry)
    {
        byte[] p = new byte[1024];
        for (int i = 0; i < 256; i++)
        {
            (byte r, byte g, byte b, byte a) = entry(i);
            p[(4 * i) + 0] = r;
            p[(4 * i) + 1] = g;
            p[(4 * i) + 2] = b;
            p[(4 * i) + 3] = a;
        }

        return p;
    }

    private static FontArchive Archive(byte[] page)
    {
        var sections = new List<byte[]>();
        for (int i = 0; i < FontArchive.PageCount; i++)
        {
            sections.Add(page);
        }

        return FontArchive.FromMemory(Language.Ja, MiniMzp.Build(sections));
    }

    [Fact]
    public void TheCsm1SwizzleSwapsBitsThreeAndFourOfThePaletteIndex()
    {
        Assert.Equal(0, FontArchive.UnswizzleCsm1(0));
        Assert.Equal(0x10, FontArchive.UnswizzleCsm1(0x08));
        Assert.Equal(0x08, FontArchive.UnswizzleCsm1(0x10));
        Assert.Equal(0x18, FontArchive.UnswizzleCsm1(0x18));
        Assert.Equal(0xFF, FontArchive.UnswizzleCsm1(0xFF));
        Assert.Equal(256, Enumerable.Range(0, 256).Select(FontArchive.UnswizzleCsm1).Distinct().Count());
        Assert.All(Enumerable.Range(0, 256), i => Assert.Equal(i, FontArchive.UnswizzleCsm1(FontArchive.UnswizzleCsm1(i))));
    }

    [Fact]
    public void ThePaletteAlphaConversionIsTheEnginesInPlaceRule()
    {
        Assert.Equal(0xFF, FontPageData.Alpha8(0x80));
        Assert.Equal(0xFE, FontPageData.Alpha8(0x7F));
        Assert.Equal(0x00, FontPageData.Alpha8(0x00));
        Assert.Equal(0x02, FontPageData.Alpha8(0x01));
        Assert.Equal(0x02, FontPageData.Alpha8(0x81));    // (2 * 0x81) & 0xFF
        Assert.Equal(0x7E, FontPageData.Alpha8(0xBF));
    }

    [Fact]
    public void ASyntheticPageDecodesWithItsPaletteUnswizzledAndItsAlphaConverted()
    {
        // stored palette entry s holds (s, 100 + s, 200, s): after the swizzle is undone, pixel index i reads stored entry CSM1(i)
        byte[] stored = Palette(s => ((byte)s, (byte)(100 + (s % 100)), 200, (byte)(s % 0x81)));
        byte[] indices = [0, 1, 8, 16, 24, 255, 0x80, 7];
        FontArchive archive = Archive(PageContainer(4, 2, indices, stored));
        FontPageData page = archive.ReadPageData(5);
        Assert.Equal((4, 2, 1, 0x26), (page.Width, page.Height, page.Flag, page.Slot));
        Assert.Equal(indices, page.Indices);
        byte[] rgba = page.ToRgba();
        Assert.Equal(32, rgba.Length);
        for (int px = 0; px < indices.Length; px++)
        {
            int entry = FontArchive.UnswizzleCsm1(indices[px]);
            Assert.Equal((byte)entry, rgba[4 * px]);
            Assert.Equal((byte)(100 + (entry % 100)), rgba[(4 * px) + 1]);
            Assert.Equal(200, rgba[(4 * px) + 2]);
            Assert.Equal(FontPageData.Alpha8((byte)(entry % 0x81)), rgba[(4 * px) + 3]);
        }

        Assert.Equal(indices.Select(i => FontPageData.Alpha8((byte)(FontArchive.UnswizzleCsm1(i) % 0x81))).ToArray(), page.ToCoverage());
        FontPage contract = archive.ReadPage(5);
        Assert.Equal(rgba, contract.Rgba.ToArray());
        Assert.Equal((Language.Ja, 5, 4, 2), (contract.Language, contract.Page, contract.Width, contract.Height));
    }

    [Fact]
    public void AMalformedPageIsRefusedWithTheRuleThatFailed()
    {
        byte[] pal = new byte[1024];
        byte[] ok = [0, 1, 2, 3];
        Assert.Contains("unexpected tiling", Assert.Throws<ContainerFormatException>(() =>
            Archive(PageContainer(2, 2, ok, pal, header: [2, 2, 2, 2, 2, 1, 1, 1])).ReadPageData(0)).Message, StringComparison.Ordinal);
        Assert.Contains("unexpected tiling", Assert.Throws<ContainerFormatException>(() =>
            Archive(PageContainer(2, 2, ok, pal, header: [2, 2, 1, 2, 1, 1, 1, 1])).ReadPageData(0)).Message, StringComparison.Ordinal);
        Assert.Contains("pixel bytes", Assert.Throws<ContainerFormatException>(() =>
            Archive(PageContainer(4, 2, ok, pal)).ReadPageData(0)).Message, StringComparison.Ordinal);
        Assert.Contains("header too short", Assert.Throws<ContainerFormatException>(() =>
            Archive(PageContainer(2, 2, ok, pal, shortHeader: true)).ReadPageData(0)).Message, StringComparison.Ordinal);
        Assert.Contains("expected 2", Assert.Throws<ContainerFormatException>(() =>
            FontArchive.FromMemory(Language.En, MiniMzp.Build(Enumerable.Repeat(MiniMzp.Build([new byte[8]]), 65).ToArray())).ReadPageData(0)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithTheWrongNumberOfSectionsIsRefused()
    {
        byte[] file = MiniMzp.Build(Enumerable.Repeat(new byte[8], 64).ToArray());
        ContainerFormatException e = Assert.Throws<ContainerFormatException>(() => FontArchive.FromMemory(Language.Ja, file));
        Assert.Contains("64 sections, expected 65", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankPageHeaderWithoutAFlagByteReadsAsMinusOne()
    {
        // a header of exactly 1040 bytes: the flag byte is missing and Python answers -1
        byte[] pal = new byte[1024];
        ushort[] words = [1, 2, 1, 2, 1, 1, 1, 1];
        byte[] header = [.. words.SelectMany(BitConverter.GetBytes), .. pal];
        byte[] noFlag = MiniMzp.Build([header, MiniMzx.Literal([0, 0], 2)]);
        FontPageData data = Archive(noFlag).ReadPageData(0);
        Assert.Equal(-1, data.Flag);
        Assert.Equal((1, 2), (data.Width, data.Height));
    }
}

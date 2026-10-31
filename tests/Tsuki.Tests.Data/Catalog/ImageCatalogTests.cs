using System.Globalization;
using Tsuki.Data.Catalog;
using Tsuki.Data.Containers;
using Tsuki.Data.Eboot;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Data.Catalog;

/// <summary>
/// The image catalog (data VA 0x671900, 14,744 records + 31 empty slots) and the picture-name lists
/// (<c>tsuki.image_catalog</c>, <c>tsuki.catalog_eboot</c>): every slot equals the oracle's TSV, both ways of locating the table
/// agree, the figures of docs/formats/images.md 8 are reproduced, and damaged tables are errors.
/// </summary>
public sealed class ImageCatalogTests
{
    // ---- synthetic: the rectangle rules ------------------------------------------------------------------------

    private static CatalogEntry Entry(int width, int height, int picFlags = 0) =>
        new(7, 0, "A.mzp", null, 0, 0, width / 3, height * 362 / 1080, width, height, 0, 0, 0x0B, 0, 0, picFlags, 0, 0);

    [Theory]
    [InlineData(1, 254)]
    [InlineData(253, 254)]
    [InlineData(254, 254)]
    [InlineData(255, 508)]
    [InlineData(2560, 2794)] // IMG2431: 11 x 254
    [InlineData(1080, 1270)]
    public void ABandIsStoredAtTheHeightRoundedUpToWholeRowsOf254(int height, int stride)
    {
        Assert.Equal(stride, Entry(100, height, 2).BandStride);
        Assert.Equal(0, Entry(100, height).BandStride);
    }

    [Fact]
    public void ThePictureWithBandsDrawsTheBandOfTheLanguageAndOthersTheTopLeftPart()
    {
        var band = Entry(2838, 2560, 2);
        Assert.Equal(new PixelRect(0, 0, 2838, 2560), band.SourceRect(0));
        Assert.Equal(new PixelRect(0, 2794, 2838, 2560), band.SourceRect(1));
        Assert.Equal(new PixelRect(0, 5588, 2838, 2560), band.SourceRect(2));
        Assert.Equal(new PixelRect(0, 8382, 2838, 2560), band.SourceRect(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => band.SourceRect(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => band.SourceRect(-1));
        var plain = Entry(717, 1279);
        Assert.Equal(new PixelRect(0, 0, 717, 1279), plain.SourceRect(0));
        Assert.Equal(new PixelRect(0, 0, 717, 1279), plain.SourceRect(3));
        Assert.Equal(new PixelRect(0, 0, 717, 1279), plain.SourceRect(9)); // the engine does not look at the language without bands
    }

    [Fact]
    public void TheThumbnailRectangleRoundsTheScaledSizeHalfUpAndStacksTheBands()
    {
        var full = Entry(1920, 1080);
        Assert.Equal(new PixelRect(0, 0, 150, 84), full.ThumbRect(1)); // 150.0 and 84.375
        var band = Entry(2838, 2560, 2); // 221.7 -> 222, 200.0 -> 200
        Assert.Equal(new PixelRect(0, 0, 222, 200), band.ThumbRect(0));
        Assert.Equal(new PixelRect(0, 600, 222, 200), band.ThumbRect(3));
        Assert.Equal(new PixelRect(0, 0, 1, 1), Entry(7, 7).ThumbRect(0)); // 0.547 -> 1
        Assert.Equal(new PixelRect(0, 0, 0, 0), Entry(6, 6).ThumbRect(0)); // 0.469 -> 0
        Assert.Throws<ArgumentOutOfRangeException>(() => band.ThumbRect(7));
    }

    [Fact]
    public void FlagsNamesAndLayersFollowTheRecord()
    {
        var e = new CatalogEntry(11473, 0, "ARU_L_81_00_N00_B.MZP", "ARU_L_81_00_N00.MZP", 0, 0, 239, 428, 717, 1279, 124, 219, 0x0B, 0, 0, 0, 2, 1);
        Assert.True(e.Paired);
        Assert.Equal("ARU_L_81_00_N00.MZP", e.Name);
        Assert.Equal("ARU_L_81_00_N00.MZP", e.ThumbName);
        Assert.Equal(["ARU_L_81_00_N00_B.MZP", "ARU_L_81_00_N00.MZP"], e.Layers);
        Assert.Equal(0x671900 + (0x48 * 11473), e.Va);
        Assert.False(e.IsVariant);
        Assert.False(e.HasLanguageBands);
        var single = new CatalogEntry(2438, 0, "img2431.MZP", null, 0, 0, 946, 858, 2838, 2560, 473, 429, 0x0B, 0, 0, 2, 0, 0);
        Assert.False(single.Paired);
        Assert.Equal("IMG2431.MZP", single.ThumbName);
        Assert.Equal(["IMG2431.MZP"], single.Layers);
        Assert.True(single.HasLanguageBands);
        Assert.True(Entry(1, 1, 3).IsVariant);
        Assert.True(Entry(1, 1, 3).HasLanguageBands);
    }

    [Fact]
    public void LanguageNamesMapToTheEngineIndexAndOthersAreRejected()
    {
        Assert.Equal([0, 1, 2, 3], new[] { "ja", "EN", "Zc", "zt" }.Select(ImageCatalog.LanguageIndex));
        Assert.Throws<ArgumentException>(() => ImageCatalog.LanguageIndex("ko"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageCatalog.LanguageIndex(4));
    }

    [Theory]
    [InlineData("BLACK.mzp", true)]
    [InlineData("AKI_A_00_00_N00_B.MZP", true)]
    [InlineData("conf_parts_en.mrg", true)]
    [InlineData("X.MRG", true)]
    [InlineData("a.Mzp", false)]
    [InlineData(".mzp", false)]
    [InlineData("a-b.mzp", false)]
    [InlineData("abc", false)]
    [InlineData("a.mzp ", false)]
    [InlineData("a.png", false)]
    public void TheEngineTableHoldsOnlyPlainFileNames(string name, bool expected) =>
        Assert.Equal(expected, ImageCatalog.IsFileName(name));

    [Fact]
    public void WithoutThePairOfRecordsTheTableIsNotFound()
    {
        var e = Assert.Throws<EbootFormatException>(() => ImageCatalog.Locate(new byte[0x400000]));
        Assert.Contains("not found", e.Message, StringComparison.Ordinal);
        Assert.Throws<EbootFormatException>(() => ImageCatalog.Locate([]));
    }

    // ---- real data ----------------------------------------------------------------------------------------------

    private sealed record Slot(int Index, bool Used, string[] Cells);

    private static (Dictionary<string, int> Col, List<string[]> Rows) GoldenRows()
    {
        var rows = CatalogGolden.Tsv("catalog/image_catalog.tsv", out string[] header);
        return (header.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => x.i), rows);
    }

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Rect(PixelRect r) => $"{r.X},{r.Y},{r.X + r.Width},{r.Y + r.Height}";

    [GameDataFact]
    public void EverySlotEqualsTheOracleOnBothReaders()
    {
        var (col, rows) = GoldenRows();
        var fixedVa = ImageCatalog.Read(SharedEboot.Reader);
        var byContent = ImageCatalog.ReadByContent(SharedEboot.Data);
        Assert.Equal(14_775, rows.Count);
        Assert.Equal(14_744, fixedVa.Count);
        Assert.Equal(31, fixedVa.EmptySlots);
        Assert.Equal(14_744, byContent.Count);
        Assert.Equal(31, byContent.EmptySlots);
        Assert.Equal(0x3D78A0, fixedVa.FileOffset);
        Assert.Equal(fixedVa.FileOffset, byContent.FileOffset);

        var langs = new[] { "ja", "en", "zc", "zt" };
        var bad = new Mismatches();
        for (int k = 0; k < 14_744; k++)
        {
            var g = rows[k];
            string C(string name) => g[col[name]];
            var e = fixedVa[k];
            bad.Equal($"{k} record equality of the two readers", e, byContent[k]);
            bad.Equal($"{k} slot", I(k), C("slot"));
            bad.Equal($"{k} used", "1", C("used"));
            bad.Equal($"{k} va", "0x" + e.Va.ToString("x", CultureInfo.InvariantCulture), C("va"));
            bad.Equal($"{k} file offset", "0x" + e.FileOffset.ToString("x", CultureInfo.InvariantCulture), C("file_offset"));
            bad.Equal($"{k} name0", e.Name0, C("name0"));
            bad.Equal($"{k} name1", e.Name1 ?? "-", C("name1"));
            bad.Equal($"{k} layer", $"{e.LayerX},{e.LayerY}", $"{C("layer_x")},{C("layer_y")}");
            bad.Equal($"{k} w3 h3", $"{e.W3},{e.H3}", $"{C("w3")},{C("h3")}");
            bad.Equal($"{k} size", $"{e.Width}x{e.Height}", $"{C("width")}x{C("height")}");
            bad.Equal($"{k} anchor", $"{e.AnchorX},{e.AnchorY}", $"{C("anchor_x")},{C("anchor_y")}");
            bad.Equal($"{k} kind/flags", $"{e.Kind},{e.DrawFlags},{e.DrawParam},{e.PicFlags}", $"{C("kind")},{C("draw_flags")},{C("draw_param")},{C("pic_flags")}");
            bad.Equal($"{k} char/scale", $"{e.CharId},{e.ScaleClass}", $"{C("char_id")},{C("scale_class")}");
            bad.Equal($"{k} band stride", I(e.BandStride), C("band_stride"));
            bad.Equal($"{k} logical name", e.Name, C("logical_name"));
            bad.Equal($"{k} paired", e.Paired ? "1" : "0", C("paired"));
            for (int l = 0; l < 4; l++)
            {
                bad.Equal($"{k} source rect {langs[l]}", Rect(e.SourceRect(l)), C("src_" + langs[l]));
                bad.Equal($"{k} thumb rect {langs[l]}", Rect(e.ThumbRect(l)), C("thumb_" + langs[l]));
            }

            bad.Equal($"{k} cache fields", "-1,-1", $"{C("cache0")},{C("cache1")}");
        }

        bad.AssertNone("image table against the oracle");
    }

    [GameDataFact]
    public void TheThirtyOneEmptySlotsAreEmptyAsDocumented()
    {
        var (col, rows) = GoldenRows();
        var empties = rows.Skip(14_744).ToList();
        Assert.Equal(31, empties.Count);
        Assert.All(empties, g =>
        {
            Assert.Equal("0", g[col["used"]]);
            Assert.Equal("-", g[col["name0"]]);
            Assert.Equal("-", g[col["name1"]]);
            Assert.Equal("-1", g[col["cache0"]]);
            Assert.Equal("-1", g[col["cache1"]]);
            Assert.Equal("-1", g[col["anchor_y"]]);
        });
        var reader = SharedEboot.Reader;
        for (int k = 14_744; k < 14_775; k++)
        {
            var raw = reader.Read(EbootAddresses.ImageTable + (0x48L * k), 0x48);
            Assert.Equal(0UL, BitConverter.ToUInt64(raw[8..]));
            Assert.Equal(0UL, BitConverter.ToUInt64(raw[16..]));
            Assert.Equal(-1, BitConverter.ToInt32(raw[0x20..]));
        }
    }

    [GameDataFact]
    public void TheFiguresOfImagesMd8AreReproduced()
    {
        var cat = SharedEboot.Catalog.Images;
        Assert.Equal(14_744, cat.Count);
        Assert.Equal("BLACK.mzp", cat[0].Name0);
        Assert.Equal("BLUE.MZP", cat[1].Name0.ToUpperInvariant());
        Assert.Equal(3_529, cat.Entries.Count(e => e.Paired));
        Assert.Equal(166, cat.Entries.Count(e => e.HasLanguageBands));
        Assert.Equal(7_288, cat.Entries.Count(e => e.IsVariant));
        Assert.Equal(
            new SortedDictionary<int, int> { [0] = 7_316, [1] = 7_262, [2] = 140, [3] = 26 },
            new SortedDictionary<int, int>(cat.Entries.GroupBy(e => e.PicFlags).ToDictionary(g => g.Key, g => g.Count())));
        Assert.All(cat.Entries, e => Assert.Equal(0x0B, e.Kind));
        Assert.All(cat.Entries, e => Assert.Equal(0, e.DrawParam));
        Assert.All(cat.Entries, e => Assert.Equal((0, 0), (e.LayerX, e.LayerY)));
        Assert.All(cat.Entries, e => Assert.Equal(e.Width / 3, e.W3));
        Assert.All(cat.Entries, e => Assert.Equal(e.Height * 362 / 1080, e.H3));
        Assert.Equal(
            new[] { "BLACK.MZP", "BLUE.MZP", "GREEN.MZP", "ORANGE.MZP", "RED.MZP", "WHITE.MZP", "YELLOW.MZP" },
            cat.Entries.Where(e => e.DrawFlags == 3).Select(e => e.Name0.ToUpperInvariant()).Order());
        Assert.Equal(Enumerable.Range(0, 7), cat.Entries.Where(e => e.DrawFlags == 3).Select(e => e.Index));
        Assert.All(cat.Entries.Where(e => e.DrawFlags != 3), e => Assert.Equal(0, e.DrawFlags));
        // all 4,750 sprite records carry the character id and size class of their name; one scene (img5028) has them too
        var sprites = cat.Entries.Where(e => NameGrammar.Classify(e.Name).Category == NameCategory.Sprite).ToList();
        Assert.Equal(4_750, sprites.Count);
        foreach (var e in sprites)
        {
            var info = NameGrammar.Classify(e.Name);
            Assert.Equal(NameGrammar.CharCodes[info.CharCode].CharId, e.CharId);
            Assert.Equal(NameGrammar.SizeClasses[info.SizeClass], e.ScaleClass);
        }

        // 1,221 standalone sprites, no overlay on anything but a sprite
        Assert.Equal(1_221, sprites.Count(e => !e.Paired));
        Assert.All(sprites.Where(e => !e.Paired), e => Assert.Equal(e.Name, e.Name0));
        Assert.All(cat.Entries.Where(e => e.Paired), e => Assert.EndsWith("_B.MZP", e.Name0, StringComparison.OrdinalIgnoreCase));
        Assert.All(cat.Entries.Where(e => e.Paired), e => Assert.Equal(NameCategory.Sprite, NameGrammar.Classify(e.Name1!).Category));
    }

    [GameDataFact]
    public void TheWorkedExamplesOfImagesMd8AreRecord2438And11473()
    {
        var cat = SharedEboot.Catalog.Images;
        var board = cat[2438];
        Assert.Equal(0x402650, board.FileOffset);
        Assert.Equal("img2431.MZP", board.Name0);
        Assert.Null(board.Name1);
        Assert.Equal((2838, 2560, 946, 858), (board.Width, board.Height, board.W3, board.H3));
        Assert.Equal((473, 429), (board.AnchorX, board.AnchorY));
        Assert.Equal(2, board.PicFlags);
        Assert.Equal(2794, board.BandStride);
        Assert.Equal(new PixelRect(0, 2794, 2838, 2560), board.SourceRect(1)); // IMG2431 en band = rows 2794..5353
        Assert.Equal(2794 + 2560 - 1, board.SourceRect(1).Y + board.SourceRect(1).Height - 1);

        var neco = cat[11473];
        Assert.Equal(0x4A1368, neco.FileOffset);
        Assert.Equal("ARU_L_81_00_N00_B.MZP", neco.Name0);
        Assert.Equal("ARU_L_81_00_N00.MZP", neco.Name1);
        Assert.Equal((717, 1279, 239, 428), (neco.Width, neco.Height, neco.W3, neco.H3));
        Assert.Equal((124, 219), (neco.AnchorX, neco.AnchorY));
        Assert.Equal((2, 1), (neco.CharId, neco.ScaleClass));
        Assert.Equal(0, neco.PicFlags);

        // the three bases that exist nowhere in the data (images.md 8)
        Assert.Equal(
            [11574, 11667, 12536],
            cat.Entries.Where(e => e.Name0.ToUpperInvariant() is "ARU_M_03_35_N00_B.MZP" or "ARU_S_03_35_N00_B.MZP" or "NOE_L_60_03_N00_B.MZP").Select(e => e.Index));
    }

    [GameDataFact]
    public void ThePictureIdSpaceIsTheAllpac2IndexSpace()
    {
        // record k is allpac2 entry k: the allpac2 NAM name equals name1 if present, else name0, for 14,744 / 14,744 records
        var allpac2 = NamTable.Parse(File.ReadAllBytes(GameImage.Path("allpac2.nam")));
        Assert.Equal(14_745, allpac2.Count);
        Assert.Equal("DUMMY.MRG", allpac2[14_744]);
        var cat = SharedEboot.Catalog;
        foreach (var e in cat.Images.Entries)
        {
            if (!string.Equals(e.Name, allpac2[e.Index], StringComparison.OrdinalIgnoreCase))
            {
                Assert.Fail($"record {e.Index} is {e.Name}, allpac2 entry {e.Index} is {allpac2[e.Index]}");
            }
        }
    }

    [GameDataFact]
    public void BothWaysToLocateTheTableAgree()
    {
        var (offset, used, empty) = ImageCatalog.Locate(SharedEboot.Data);
        Assert.Equal((0x3D78A0L, 14_744, 31), (offset, used, empty));
        Assert.Equal(offset, SharedEboot.Reader.FileOffset(EbootAddresses.ImageTable));
    }

    [GameDataFact]
    public void ThePictureNameListsEqualTheOracleAndTheRecords()
    {
        var names = SharedEboot.Catalog.PictureNames;
        var old = SharedEboot.Catalog.OldPictureNames;
        var golden = CatalogGolden.Tsv("catalog/image_names.tsv", out _);
        Assert.Equal(14_745, names.Count);
        Assert.Equal(golden.Count, names.Count);
        for (int k = 0; k < golden.Count; k++)
        {
            Assert.Equal(k.ToString(CultureInfo.InvariantCulture), golden[k][0]);
            Assert.Equal(names[k], golden[k][1]);
        }

        Assert.Equal("BLACK", names[0]);
        Assert.Equal(string.Empty, names[^1]);
        Assert.Equal(9_959, old.Count);
        Assert.Equal(names.Take(9_958), old.Take(9_958));
        Assert.Equal(string.Empty, old[^1]);
        var goldenOld = CatalogGolden.Tsv("catalog/old_image_names.tsv", out _);
        Assert.Equal(old, goldenOld.Select(r => r[1]));

        // name k is the name of record k without the extension, upper case
        foreach (var e in SharedEboot.Catalog.Images.Entries)
        {
            string expected = e.Name[..e.Name.LastIndexOf('.')].ToUpperInvariant();
            if (names[e.Index] != expected)
            {
                Assert.Fail($"name {e.Index} is {names[e.Index]}, record says {expected}");
            }
        }

        // the 0xFF terminator and the CR LF of every record
        var reader = SharedEboot.Reader;
        Assert.Equal(0xFF, reader.Read(EbootAddresses.ImageNames + (32L * names.Count), 1)[0]);
        for (int k = 0; k < names.Count - 1; k++)
        {
            var rec = reader.Read(EbootAddresses.ImageNames + (32L * k) + 30, 2);
            Assert.True(rec[0] == '\r' && rec[1] == '\n', $"record {k}");
        }
    }

    [GameDataFact]
    public void DamagedTablesAreErrorsAtTheRecordThatBreaksTheRule()
    {
        byte[] data = (byte[])SharedEboot.Data.Clone();
        long rec5 = SharedEboot.DataFile(EbootAddresses.ImageTable) + (0x48 * 5);

        void Expect(string part, Action change, Action undo)
        {
            change();
            var e = Assert.Throws<EbootFormatException>(() => ImageCatalog.Read(EbootReader.Parse(data)));
            Assert.Contains(part, e.Message, StringComparison.Ordinal);
            undo();
        }

        // a non-zero name pointer (field +0x00 is 0 in all 14,775 slots)
        Expect("image slot 5: name pointer is not 0", () => data[rec5] = 1, () => data[rec5] = 0);
        // a file pointer outside the text segment
        byte save = data[rec5 + 11];
        Expect("image slot 5: pointer outside the text segment", () => data[rec5 + 11] = 0x7F, () => data[rec5 + 11] = save);
        // a file name with a character the engine never uses
        long name5 = SharedEboot.TextFile((long)BitConverter.ToUInt64(data, (int)rec5 + 8));
        byte c = data[name5];
        Expect("unexpected file name", () => data[name5] = (byte)'-', () => data[name5] = c);
        // a used record after an empty slot (slot 14744 stays empty, slot 14745 gets a name)
        long slot14745 = SharedEboot.DataFile(EbootAddresses.ImageTable) + (0x48L * 14_745);
        Array.Copy(data, rec5 + 8, data, slot14745 + 8, 8);
        var late = Assert.Throws<EbootFormatException>(() => ImageCatalog.Read(EbootReader.Parse(data)));
        Assert.Contains("image slot 14745 is used after an empty slot", late.Message, StringComparison.Ordinal);
        Array.Clear(data, (int)slot14745 + 8, 8);

        // one record fewer: the count differs
        long slot14743 = SharedEboot.DataFile(EbootAddresses.ImageTable) + (0x48L * 14_743);
        byte[] saved = data.AsSpan((int)slot14743 + 8, 16).ToArray();
        Array.Clear(data, (int)slot14743 + 8, 16);
        var count = Assert.Throws<EbootFormatException>(() => ImageCatalog.Read(EbootReader.Parse(data)));
        Assert.Contains("14743 records, expected 14744", count.Message, StringComparison.Ordinal);
        Assert.Equal(14_743, ImageCatalog.Read(EbootReader.Parse(data), null).Count);
        saved.CopyTo(data, (int)slot14743 + 8);
        Assert.Equal(14_744, ImageCatalog.Read(EbootReader.Parse(data)).Count);
    }
}

using System.Globalization;
using System.Text;
using Tsuki.Data.Catalog;
using Tsuki.Data.Containers;

namespace Tsuki.Tests.Data.Catalog;

/// <summary>Builds NAM32 name tables for the synthetic tests.</summary>
public static class SyntheticNam
{
    /// <summary>A NAM32 table: each name NUL-padded to 30 bytes and CR LF, then the all-zero terminator record.</summary>
    public static NamTable Table(params string[] names)
    {
        var bytes = new List<byte>();
        foreach (string n in names)
        {
            byte[] rec = new byte[32];
            Encoding.ASCII.GetBytes(n).CopyTo(rec, 0);
            rec[30] = (byte)'\r';
            rec[31] = (byte)'\n';
            bytes.AddRange(rec);
        }

        bytes.AddRange(new byte[32]);
        return NamTable.ParseNam32(bytes.ToArray());
    }

    /// <summary>The five searched archives from name lists.</summary>
    public static Dictionary<ImageArchive, NamTable> Archives(
        string[] a0, string[] a1, string[] a12, string[] a3, string[] a4) => new()
        {
            [ImageArchive.Allpac0] = Table(a0),
            [ImageArchive.Allpac1] = Table(a1),
            [ImageArchive.Allpac1_2] = Table(a12),
            [ImageArchive.Allpac3] = Table(a3),
            [ImageArchive.Allpac4] = Table(a4),
        };
}

/// <summary>
/// The NAM file-name search (VA 0xC3270, archives_ui_movies.md 2.3): the key rules and the search order on synthetic tables, and
/// the 63,332 queries of the oracle on the real name tables.
/// </summary>
public sealed class NamSearchTests
{
    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    [Fact]
    public void TheKeyStartsAfterTheLastBackslashAndIsUpperCased()
    {
        Assert.Equal(B("IMG0001.MZP"), ImageNameIndex.Key("dir\\sub\\img0001.mzp"));
        Assert.Equal(B("CAUTION_EN.MZP"), ImageNameIndex.Key("Caution_En.mzp"));
        Assert.Equal(B(new string('A', 28)), ImageNameIndex.Key(new string('a', 40)));
        Assert.Equal(B("AB"), ImageNameIndex.Key(B("ab\0cd")));
        Assert.Empty(ImageNameIndex.Key(string.Empty));
        Assert.Empty(ImageNameIndex.Key("\\"));
        Assert.Empty(ImageNameIndex.Key("a\\"));
        Assert.Equal(B("A"), ImageNameIndex.Key("\\a"));
        Assert.Equal(B("DEF"), ImageNameIndex.Key("abc\\def"));
    }

    [Fact]
    public void ALetterAfterAByteOfAtLeast0x80IsNotUpperCasedBecauseItIsAShiftJisTrailByte()
    {
        Assert.Equal([0x82, (byte)'a', (byte)'B', (byte)'.', (byte)'M', (byte)'Z', (byte)'P'], ImageNameIndex.Key([0x82, (byte)'a', (byte)'b', (byte)'.', (byte)'m', (byte)'z', (byte)'p']));
        Assert.Equal([(byte)'X', 0x81, (byte)'b', (byte)'C'], ImageNameIndex.Key([(byte)'x', 0x81, (byte)'b', (byte)'c']));
    }

    [Fact]
    public void TheBackslashScanOnlyLooksAtTheFirst256Bytes()
    {
        byte[] name = [.. B(new string('q', 300)), (byte)'\\', .. B("abc")];
        Assert.Equal(B(new string('Q', 28)), ImageNameIndex.Key(name));
        byte[] early = [.. B(new string('q', 255)), (byte)'\\', .. B("abc")];
        Assert.Equal(B("ABC"), ImageNameIndex.Key(early));
    }

    [Fact]
    public void NonAsciiTextMustBePassedAsBytes()
    {
        Assert.Throws<ArgumentException>(() => ImageNameIndex.Key("café"));
        Assert.Throws<ArgumentException>(() => new ImageNameIndex(SyntheticNam.Archives([], [], [], [], [])).Find("あ.mzp"));
    }

    private static ImageNameIndex Index() => new(SyntheticNam.Archives(
        ["BLACK.MZP", "IMG0001.MZP", "DUMMY.MRG"],
        ["IMG0002.MZP", "IMG0001.MZP"],
        ["IMG0002.MZP"],
        ["DUMMY.MRG"],
        ["IMG9999.MZP"]));

    [Theory]
    [InlineData("img0001.mzp", ImageArchive.Allpac1, 1)]
    [InlineData("IMG0002.MZP", ImageArchive.Allpac1_2, 0)]
    [InlineData("dummy.mrg", ImageArchive.Allpac3, 0)]
    [InlineData("IMG9999.MZP", ImageArchive.Allpac4, 0)]
    [InlineData("black.mzp", ImageArchive.Allpac0, 0)]
    public void TheSearchOrderIsAllpac4Allpac3Allpac1_2Allpac1Allpac0(string name, ImageArchive archive, int index)
    {
        var idx = Index();
        Assert.Equal(new ImageId(archive, index), idx.Find(name));
        Assert.Equal(idx.Find(name), idx.FindLinear(ImageNameIndex.Key(name)));
        Assert.Equal(idx.Find(name), idx.FindLinear(Encoding.ASCII.GetBytes(name)));
    }

    [Fact]
    public void AMissIsNullAndAScriptIdWithoutAnExtensionNeverMatches()
    {
        var idx = Index();
        Assert.Null(idx.Find("nope.mzp"));
        Assert.Null(idx.Find("IMG0001")); // the NUL takes part in the compare
        Assert.Null(idx.FindLinear("IMG0001"u8));
        Assert.Null(idx.Find(string.Empty));
    }

    [Fact]
    public void WithoutThePlayGoChunksAllpac1AndAllpac1_2AreSkipped()
    {
        var idx = Index();
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 1), idx.Find("IMG0001.MZP", allpac1Available: false));
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 1), idx.FindLinear("IMG0001.MZP"u8, allpac1Available: false));
        Assert.Null(idx.Find("IMG0002.MZP", allpac1Available: false));
        Assert.Equal(new ImageId(ImageArchive.Allpac1_2, 0), idx.Find("IMG0002.MZP", allpac1Available: true));
    }

    [Fact]
    public void OnlyThe28ByteKeyIsComparedSoALongerNameMatchesItsTruncation()
    {
        string stored = new string('N', 26) + ".M"; // 28 characters
        string longer = stored + "ZP";
        var idx = new ImageNameIndex(SyntheticNam.Archives([stored], [], [], [], []));
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 0), idx.Find(stored));
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 0), idx.Find(longer));
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 0), idx.FindLinear(Encoding.ASCII.GetBytes(longer)));
        Assert.Null(idx.Find(stored[..27]));
        // a stored name of 29 or 30 characters is compared by its first 28 as well
        var thirty = new ImageNameIndex(SyntheticNam.Archives([stored + "ZP"], [], [], [], []));
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 0), thirty.Find(stored));
    }

    [Fact]
    public void AnEmptyRecordEndsTheScanOfItsTableAndTheFirstEqualNameWins()
    {
        var idx = new ImageNameIndex(SyntheticNam.Archives(["A.MZP", string.Empty, "B.MZP"], [], [], [], []));
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 0), idx.Find("a.mzp"));
        Assert.Null(idx.Find("b.mzp"));
        Assert.Null(idx.FindLinear("b.mzp"u8));
        var twice = new ImageNameIndex(SyntheticNam.Archives(["A.MZP", "A.MZP"], [], [], [], []));
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 0), twice.Find("A.MZP"));
    }

    [Fact]
    public void AMissingArchiveIsSkipped()
    {
        var idx = new ImageNameIndex(new Dictionary<ImageArchive, NamTable> { [ImageArchive.Allpac0] = SyntheticNam.Table("X.MZP") });
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 0), idx.Find("x.mzp"));
        Assert.Null(idx.Find("y.mzp"));
    }

    [Fact]
    public void PackedIdsSplitIntoArchiveAndIndex()
    {
        var id = new ImageId(ImageArchive.Allpac1, 2157);
        Assert.Equal(0x1086D, id.Packed);
        Assert.Equal(id, ImageId.Unpack(0x1086D));
        Assert.Equal(new ImageId(ImageArchive.Allpac2, 7), ImageId.Unpack(0x30007));
        Assert.Equal("allpac1_2", new ImageId(ImageArchive.Allpac1_2, 0).ArchiveName);
        Assert.Equal("allpac4#3", new ImageId(ImageArchive.Allpac4, 3).ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageId.Unpack(0x60000));
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 5), ImageId.Unpack(5));
    }

    // ---- real data ----------------------------------------------------------------------------------------------

    [GameDataFact]
    public void TheQueriesOfTheOracleGiveTheSameFileOnTheRealNameTables()
    {
        var index = SharedEboot.Catalog.NameIndex;
        var rows = CatalogGolden.Tsv("lookup/nam_queries.tsv", out string[] header);
        Assert.Equal(["query_hex", "allpac1_open", "packed_id"], header);
        Assert.True(rows.Count > 60_000, rows.Count.ToString(CultureInfo.InvariantCulture));
        var bad = new Mismatches();
        int hits = 0;
        foreach (var r in rows)
        {
            byte[] q = CatalogGolden.FromHex(r[0]);
            bool open = r[1] == "1";
            int expected = int.Parse(r[2], CultureInfo.InvariantCulture);
            var got = index.Find(q, open);
            hits += got is null ? 0 : 1;
            bad.Equal($"query {r[0]} (allpac1 {r[1]})", expected, got?.Packed ?? -1);
        }

        bad.AssertNone("NAM search against the oracle");
        Assert.True(hits > 50_000, $"{hits} hits");
    }

    [GameDataFact]
    public void TheLinearScanOfTheEngineEqualsTheDictionaryOnASampleOfTheQueries()
    {
        var index = SharedEboot.Catalog.NameIndex;
        var rows = CatalogGolden.Tsv("lookup/nam_queries.tsv", out _);
        var bad = new Mismatches();
        for (int i = 0; i < rows.Count; i += 53)
        {
            byte[] q = CatalogGolden.FromHex(rows[i][0]);
            bool open = rows[i][1] == "1";
            bad.Equal($"query {rows[i][0]}", index.Find(q, open), index.FindLinear(q, open));
        }

        bad.AssertNone("FindLinear against Find");
    }

    [GameDataFact]
    public void EveryNameOfTheSearchedArchivesFindsItselfExceptTheThreeShadowedDummies()
    {
        var cat = SharedEboot.Catalog;
        var shadowed = new List<string>();
        foreach (var arc in ImageNameIndex.SearchOrder)
        {
            var nam = cat.Nams[arc];
            for (int i = 0; i < nam.Count; i++)
            {
                var found = cat.NameIndex.Find(nam[i]);
                if (found != new ImageId(arc, i))
                {
                    shadowed.Add($"{arc}:{nam[i]}");
                }
            }
        }

        Assert.Equal(["Allpac3:DUMMY.MRG", "Allpac1_2:DUMMY.MRG", "Allpac0:DUMMY.MRG"], shadowed);
        Assert.Equal(new ImageId(ImageArchive.Allpac4, 36), cat.NameIndex.Find("dummy.mrg"));
        Assert.Equal(new ImageId(ImageArchive.Allpac1, 2157), cat.NameIndex.Find("IMG2511.MZP"));
        Assert.Equal(0x1086D, cat.NameIndex.Find("IMG2511.MZP")!.Value.Packed);
        Assert.Null(cat.NameIndex.Find("img2511"));
    }

    [GameDataFact]
    public void EveryFileNameOfTheImageTableResolvesExceptTheThreeBodiesThatAreNotOnDisk()
    {
        var cat = SharedEboot.Catalog;
        var missing = cat.Images.Entries.SelectMany(e => new[] { e.Name0, e.Name1 }).Where(n => n is not null)
            .Select(n => n!).Where(n => cat.NameIndex.Find(n) is null).Distinct().Order().ToArray();
        Assert.Equal(["ARU_M_03_35_N00_B.MZP", "ARU_S_03_35_N00_B.MZP", "NOE_L_60_03_N00_B.MZP"], missing);
        // allpac2 is never searched by name, so its own names resolve elsewhere or nowhere
        Assert.NotNull(cat.NameIndex.Find("AKI_A_00_04_N01.MZP"));
    }
}

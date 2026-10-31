using System.Globalization;
using System.Text;
using Tsuki.Data.Catalog;

namespace Tsuki.Tests.Data.Catalog;

/// <summary>
/// Script image ids to image-table records to archive entries (VA 0x78410, 0x81760, 0x4A060 / 0x4A110;
/// archives_ui_movies.md 2.4): the key and suffix rules, a synthetic table, the lookups of RR item 2 on the real data
/// (<c>img2511</c> to record 2518 to allpac1 #2157, <c>img1696</c> with a9 = a10 = 11) and the oracle's 23,855 queries.
/// </summary>
public sealed class ScriptImageTests
{
    [Fact]
    public void TheKeyStopsAtTheFirstDotAndTheNulAndHoldsAtMost32Bytes()
    {
        Assert.Equal("IMG2511", ScriptImageNames.Key("img2511"));
        Assert.Equal("BOY_A_00_00_N01", ScriptImageNames.Key("boy_a_00_00_n01.mzp"));
        Assert.Equal(new string('X', 32), ScriptImageNames.Key(new string('x', 40)));
        Assert.Equal(string.Empty, ScriptImageNames.Key(string.Empty));
        Assert.Equal(string.Empty, ScriptImageNames.Key("."));
        Assert.Equal("A", ScriptImageNames.Key("a.b"));
        Assert.Equal("A", ScriptImageNames.Key("a\0b"u8));
        Assert.Equal(new string('A', 31) + "B", ScriptImageNames.Key(new string('a', 31) + "b"));
        Assert.Equal(new string('A', 32), ScriptImageNames.Key(new string('a', 32) + "b"));
        Assert.Equal(new string('A', 31), ScriptImageNames.Key(new string('a', 31) + ".b"));
        // bytes of at least 0x80 pass through, and map to one character each
        Assert.Equal("\u0082 ABC", ScriptImageNames.Key([0x82, 0xA0, (byte)'a', (byte)'b', (byte)'c']));
    }

    [Fact]
    public void ThePictureVariantSuffixIsAppendedWhenEitherArgumentIsNonZero()
    {
        Assert.Equal("img1696_11_11", ScriptImageNames.StchName("img1696", 11, 11));
        Assert.Equal("img2277_00_20", ScriptImageNames.StchName("img2277", 0, 20));
        Assert.Equal("img0342", ScriptImageNames.StchName("img0342", 0, 0));
        Assert.Equal("img0001_23_07", ScriptImageNames.StchName("img0001", 123, 7));
        Assert.Equal("img0001_00_04", ScriptImageNames.StchName("img0001", -3, 4));
        Assert.Equal("img0001_00_00", ScriptImageNames.StchName("img0001", 100, 100));
        Assert.Equal("img0001_00_99", ScriptImageNames.StchName("img0001", 0, 99));
        Assert.Equal("img0001_01_00", ScriptImageNames.StchName("img0001", 1, -1));
        Assert.Equal("_05_05", ScriptImageNames.StchName(string.Empty, 5, 5));
        Assert.Equal("img0001_09_10"u8.ToArray(), ScriptImageNames.StchName("img0001"u8, 9, 10));
        Assert.Equal("img0001"u8.ToArray(), ScriptImageNames.StchName("img0001"u8, 0, 0));
    }

    private static ScriptImageResolver Synthetic(out IReadOnlyList<CatalogEntry> table)
    {
        // a body + face sprite, a plain scene, a variant of the scene, a sprite with a body that is not on disk
        string[] names = ["IMG0001", "IMG0001_03_04", "BOY_A_00_00_N01", "NOE_L_60_03_N01", "WHITE", string.Empty];
        CatalogEntry E(int k, string f, string? o) =>
            new(k, 0, f, o, 0, 0, 1, 1, 1, 1, 0, 0, 0x0B, 0, 0, 0, 0, 0);
        table =
        [
            E(0, "img0001.MZP", null), E(1, "img0001_03_04.MZP", null),
            E(2, "BOY_A_00_00_N00_B.MZP", "BOY_A_00_00_N01.MZP"),
            E(3, "NOE_L_60_03_N00_B.MZP", "NOE_L_60_03_N01.MZP"), E(4, "WHITE.mzp", null),
        ];
        var index = new ImageNameIndex(SyntheticNam.Archives(
            ["WHITE.MZP", "BOY_A_00_00_N00_B.MZP", "BOY_A_00_00_N01.MZP", "IMG0001.MZP"],
            ["IMG0001_03_04.MZP"], ["NOE_L_60_03_N01.MZP"], [], []));
        return new ScriptImageResolver(names, table, index);
    }

    [Fact]
    public void AnIdResolvesToItsRecordAndTheFilesOfTheRecord()
    {
        var res = Synthetic(out _);
        var scene = res.Resolve("img0001");
        Assert.True(scene.Found);
        Assert.Equal((0, "img0001.MZP", null), (scene.Index, scene.File, scene.Overlay));
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 3), scene.FileId);
        Assert.Null(scene.OverlayId);
        Assert.Equal(new ImageId(ImageArchive.Allpac2, 0), scene.ReducedId);
        Assert.Equal("IMG0001", scene.Key);

        var variant = res.Resolve("img0001", 3, 4);
        Assert.Equal((1, "IMG0001_03_04"), (variant.Index, variant.Key));
        Assert.Equal(new ImageId(ImageArchive.Allpac1, 0), variant.FileId);
        Assert.Equal(new ImageId(ImageArchive.Allpac2, 1), variant.ReducedId);

        var sprite = res.Resolve("boy_a_00_00_n01");
        Assert.Equal((2, "BOY_A_00_00_N00_B.MZP", "BOY_A_00_00_N01.MZP"), (sprite.Index, sprite.File, sprite.Overlay));
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 1), sprite.FileId);
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 2), sprite.OverlayId);
    }

    [Fact]
    public void AMissingBodyHasNoFileIdButItsFaceIsStillFound()
    {
        var res = Synthetic(out _);
        var r = res.Resolve("NOE_L_60_03_N01");
        Assert.Equal(3, r.Index);
        Assert.Equal("NOE_L_60_03_N00_B.MZP", r.File);
        Assert.Null(r.FileId);
        Assert.Equal(new ImageId(ImageArchive.Allpac1_2, 0), r.OverlayId);
    }

    [Fact]
    public void UnknownIdsAndTheEmptyRecordResolveToMinusOne()
    {
        var res = Synthetic(out _);
        Assert.False(res.Resolve("no_such_image").Found);
        Assert.Equal(-1, res.Resolve("no_such_image").Index);
        Assert.Equal("NO_SUCH_IMAGE", res.Resolve("no_such_image").Key);
        // the empty name list record 5 exists, but there is no table record 5
        Assert.Equal(-1, res.Resolve(string.Empty).Index);
        Assert.Equal(5, res.IndexOf(string.Empty));
        Assert.Equal(0, res.IndexOf("IMG0001.mzp")); // cut at the dot
        Assert.Equal(-1, res.IndexOf("img0001_03_05"));
    }

    [Fact]
    public void TheFirstOfTwoEqualNamesWins()
    {
        CatalogEntry E(int k, string f) => new(k, 0, f, null, 0, 0, 1, 1, 1, 1, 0, 0, 0x0B, 0, 0, 0, 0, 0);
        var res = new ScriptImageResolver(["TWICE", "TWICE"], [E(0, "a.mzp"), E(1, "b.mzp")], new ImageNameIndex(SyntheticNam.Archives([], [], [], [], [])));
        Assert.Equal(0, res.Resolve("twice").Index);
        Assert.Equal("a.mzp", res.Resolve("twice").File);
    }

    // ---- real data ----------------------------------------------------------------------------------------------

    [GameDataFact]
    public void TheLookupsOfRuntimeRequirementsItem2ResolveAsDocumented()
    {
        var res = SharedEboot.Catalog.Resolver;

        var plain = res.Resolve("img2511");
        Assert.Equal((2518, "img2511.MZP", null), (plain.Index, plain.File, plain.Overlay));
        Assert.Equal(new ImageId(ImageArchive.Allpac1, 2157), plain.FileId); // allpac1 #2157
        Assert.Equal(new ImageId(ImageArchive.Allpac2, 2518), plain.ReducedId);
        Assert.Equal(0x30000 | 2518, plain.ReducedId!.Value.Packed);

        var white = res.Resolve("WHITE");
        Assert.Equal(5, white.Index);
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 5), white.FileId);

        var boy = res.Resolve("boy_a_00_00_n01");
        Assert.Equal(11680, boy.Index);
        Assert.Equal(("BOY_A_00_00_N00_B.MZP", "BOY_A_00_00_N01.MZP"), (boy.File, boy.Overlay));
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 1838), boy.FileId);
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 1840), boy.OverlayId);

        // img1696 with a9 = a10 = 11 shows IMG1696_11_11 instead of IMG1696: record 6218, allpac0 #1121
        var blur = res.Resolve("img1696", 11, 11);
        Assert.Equal("IMG1696_11_11", blur.Key);
        Assert.Equal((6218, "img1696_11_11.MZP"), (blur.Index, blur.File));
        Assert.Equal(new ImageId(ImageArchive.Allpac0, 1121), blur.FileId);
        Assert.NotEqual(res.Resolve("img1696").Index, blur.Index);

        var standalone = res.Resolve("AKI_A_00_04_N01");
        Assert.Equal((9981, "AKI_A_00_04_N01.MZP", null), (standalone.Index, standalone.File, standalone.Overlay));
        Assert.Equal(new ImageId(ImageArchive.Allpac1, 8424), standalone.FileId);

        var dangling = res.Resolve("NOE_L_60_03_N01");
        Assert.Equal(12536, dangling.Index);
        Assert.Equal("NOE_L_60_03_N00_B.MZP", dangling.File);
        Assert.Null(dangling.FileId);
        Assert.Equal(new ImageId(ImageArchive.Allpac1_2, 1082), dangling.OverlayId);

        Assert.False(res.Resolve("no_such_image").Found);
        Assert.Equal("IMG0342", res.Resolve("img0342").Key);
        Assert.Equal("IMG0342", res.Resolve("img0342", 0, 0).Key);
    }

    [GameDataFact]
    public void TheRegisterNamesAreCaseInsensitiveAndTheExtensionIsIgnored()
    {
        var res = SharedEboot.Catalog.Resolver;
        Assert.Equal(2518, res.Resolve("IMG2511").Index);
        Assert.Equal(2518, res.Resolve("Img2511").Index);
        Assert.Equal(2518, res.Resolve("img2511.mzp").Index);
        Assert.Equal(2518, res.Resolve("img2511.anything").Index);
        Assert.Equal(-1, res.Resolve("img2511x").Index);
        Assert.Equal(-1, res.Resolve("img3000").Index); // IMG3000..IMG4193 are not in the name list: scripts cannot show them
        Assert.Equal(-1, res.Resolve("img4193").Index);
        Assert.Equal(-1, res.Resolve("BG_ERROR").Index);
    }

    [GameDataFact]
    public void TheQueriesOfTheOracleResolveIdentically()
    {
        var res = SharedEboot.Catalog.Resolver;
        var rows = CatalogGolden.Tsv("lookup/script_queries.tsv", out string[] header);
        Assert.Equal(["query_hex", "a9", "a10", "key_hex", "index", "file", "overlay", "file_id", "overlay_id", "reduced_id"], header);
        Assert.True(rows.Count > 23_000, rows.Count.ToString(CultureInfo.InvariantCulture));
        var bad = new Mismatches();
        int found = 0;
        foreach (var r in rows)
        {
            byte[] q = CatalogGolden.FromHex(r[0]);
            int aa = int.Parse(r[1], CultureInfo.InvariantCulture);
            int bb = int.Parse(r[2], CultureInfo.InvariantCulture);
            var got = res.Resolve(q, aa, bb);
            string ctx = $"query {r[0]} a9={aa} a10={bb}";
            bad.Equal(ctx + " key", r[3], got.Key.Length == 0 ? "-" : Convert.ToHexStringLower(got.Key.Select(c => (byte)c).ToArray()));
            bad.Equal(ctx + " index", int.Parse(r[4], CultureInfo.InvariantCulture), got.Index);
            bad.Equal(ctx + " file", r[5], got.File ?? "-");
            bad.Equal(ctx + " overlay", r[6], got.Overlay ?? "-");
            bad.Equal(ctx + " file_id", r[7], Packed(got.FileId));
            bad.Equal(ctx + " overlay_id", r[8], Packed(got.OverlayId));
            bad.Equal(ctx + " reduced_id", r[9], Packed(got.ReducedId));
            found += got.Found ? 1 : 0;
        }

        bad.AssertNone("script image resolution against the oracle");
        Assert.True(found > 20_000, $"{found} resolved");
    }

    [GameDataFact]
    public void EveryNameOfTheListResolvesToItsOwnRecordAndTheCensusFiguresHold()
    {
        var cat = SharedEboot.Catalog;
        var res = cat.Resolver;
        for (int k = 0; k < cat.Images.Count; k++)
        {
            if (res.IndexOf(cat.PictureNames[k]) != k)
            {
                Assert.Fail($"name {k} ({cat.PictureNames[k]}) resolves to {res.IndexOf(cat.PictureNames[k])}");
            }
        }

        // a variant is its base plus a9 / a10 (archives_ui_movies.md 2.4: 7,288 records are _HH_VV variants)
        int variants = 0;
        foreach (var e in cat.Images.Entries.Where(e => e.IsVariant))
        {
            string name = cat.PictureNames[e.Index];
            int aa = int.Parse(name[^5..^3], CultureInfo.InvariantCulture);
            int bb = int.Parse(name[^2..], CultureInfo.InvariantCulture);
            if (aa == 0 && bb == 0)
            {
                continue; // _00_00 adds no suffix: the plain name is the base record
            }

            Assert.Equal(e.Index, res.Resolve(name[..^6], aa, bb).Index);
            variants++;
        }

        Assert.True(variants > 7_000, variants.ToString(CultureInfo.InvariantCulture));
    }

    private static string Packed(ImageId? id) => id is { } v ? v.Packed.ToString(CultureInfo.InvariantCulture) : "-";
}

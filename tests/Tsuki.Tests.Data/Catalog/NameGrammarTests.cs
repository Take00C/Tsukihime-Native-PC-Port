using Tsuki.Data.Catalog;

namespace Tsuki.Tests.Data.Catalog;

/// <summary>The name grammar of the picture archives (<c>tsuki.catalog.classify_name</c>, archives_ui_movies.md 3).</summary>
public sealed class NameGrammarTests
{
    [Theory]
    [InlineData("BLACK.MZP", NameCategory.SolidColor, "", "")]
    [InlineData("IMG0042.MZP", NameCategory.Scene, "", "IMG0042")]
    [InlineData("IMG0042_03_03.MZP", NameCategory.SceneLayer, "", "IMG0042")]
    [InlineData("IMG3864.MZP", NameCategory.GalleryImage, "", "IMG3864")]
    [InlineData("img2511", NameCategory.Scene, "", "IMG2511")]
    [InlineData("AKI_A_00_00_N01.MZP", NameCategory.Sprite, "", "AKI_A_00_00_N01")]
    [InlineData("AKI_A_00_00_N00_B.MZP", NameCategory.SpriteBody, "", "AKI_A_00_00_N00")]
    [InlineData("SIE_A_09_01_B00_B.MZP", NameCategory.SpriteBody, "", "SIE_A_09_01_B00")]
    [InlineData("ARU_A_18_05_C01_32_32.MZP", NameCategory.SpriteLayer, "", "ARU_A_18_05_C01")]
    [InlineData("Z20_S_63_04_N00.MZP", NameCategory.Sprite, "", "Z20_S_63_04_N00")]
    [InlineData("RULE_07.MZP", NameCategory.TransitionRule, "", "")]
    [InlineData("CAUTION1_ZT.MZP", NameCategory.CautionScreen, "zt", "")]
    [InlineData("CAUTION2.MZP", NameCategory.CautionScreen, "ja", "")]
    [InlineData("CAUTION_JA", NameCategory.CautionScreen, "ja", "")]
    [InlineData("CONF_PARTS_EN.MRG", NameCategory.UiParts, "en", "CONF_PARTS")]
    [InlineData("GALLERY_PARTS_COMMON_KO.MRG", NameCategory.UiParts, "ko", "GALLERY_PARTS_COMMON")]
    [InlineData("GALLERY_PARTS_FC_TH.MRG", NameCategory.UiThumbnails, "", "GALLERY_PARTS_FC_TH")]
    [InlineData("BORDER_PARTS.MRG", NameCategory.UiParts, "", "BORDER_PARTS")]
    [InlineData("DUMMY.MRG", NameCategory.Placeholder, "", "")]
    [InlineData("CGMODE_TOP.MRG", NameCategory.Placeholder, "", "")]
    [InlineData("SELMAP_TOP.MZP", NameCategory.Placeholder, "", "")]
    [InlineData("whatever.bin", NameCategory.Unknown, "", "")]
    [InlineData("", NameCategory.Unknown, "", "")]
    [InlineData(".", NameCategory.Unknown, "", "")]
    [InlineData("IMG004", NameCategory.Unknown, "", "")]
    [InlineData("IMG12345", NameCategory.Unknown, "", "")]
    [InlineData("aki_a_00_00_n01", NameCategory.Sprite, "", "AKI_A_00_00_N01")]
    [InlineData("Y00_L_01_02_A03_B", NameCategory.SpriteBody, "", "Y00_L_01_02_A03")]
    public void ANameIsClassifiedByItsGrammar(string name, NameCategory category, string lang, string baseName)
    {
        var info = NameGrammar.Classify(name);
        Assert.Equal(category, info.Category);
        Assert.Equal(lang, info.Lang);
        Assert.Equal(baseName, info.Base);
        Assert.Equal(name, info.Name);
    }

    [Fact]
    public void ASpriteCarriesItsCharacterAndSizeClass()
    {
        var info = NameGrammar.Classify("AKI_A_00_00_N01.MZP");
        Assert.Equal(("AKI", 8, "Akiha Tohno", "A"), (info.CharCode, info.CharId, info.CharName, info.SizeClass));
        var z = NameGrammar.Classify("Z20_S_63_04_N00.MZP");
        Assert.Equal((29, "S"), (z.CharId, z.SizeClass));
        var variant = NameGrammar.Classify("ARU_A_18_05_C01_32_32.MZP");
        Assert.Equal("32_32", variant.Layer);
        Assert.Equal(2, variant.CharId);
        Assert.Equal("03_03", NameGrammar.Classify("IMG0042_03_03.MZP").Layer);
        var unknownCode = NameGrammar.Classify("ZZZ_A_00_00_N00");
        Assert.Equal((NameCategory.Sprite, 0, "unknown"), (unknownCode.Category, unknownCode.CharId, unknownCode.CharName));
        Assert.Equal(27, NameGrammar.CharCodes.Count);
    }

    [Fact]
    public void TheCautionKindsAreNumbered()
    {
        Assert.Equal("fiction/violence disclaimer", NameGrammar.Classify("CAUTION_EN.MZP").Detail);
        Assert.Equal("recording/streaming notice", NameGrammar.Classify("CAUTION1.MZP").Detail);
        Assert.Equal("disclaimer + recording notice", NameGrammar.Classify("CAUTION2_ZC.MZP").Detail);
        Assert.Equal("?", NameGrammar.Classify("CAUTION3.MZP").Detail);
        Assert.Equal("fiction/violence disclaimer", NameGrammar.Classify("CAUTIONX").Detail); // the reference reads "no match" as kind ""
    }

    [Fact]
    public void ASetThatLacksAThumbnailVariantIsUnknownWhereThePythonReferenceRaises()
    {
        Assert.Equal(NameCategory.Unknown, NameGrammar.Classify("TITLE_PARTS_TH").Category);
    }

    [Theory]
    [InlineData(NameCategory.SolidColor, "solid_color")]
    [InlineData(NameCategory.SceneLayer, "scene_layer")]
    [InlineData(NameCategory.GalleryImage, "gallery_image")]
    [InlineData(NameCategory.SpriteBody, "sprite_body")]
    [InlineData(NameCategory.UiThumbnails, "ui_thumbnails")]
    [InlineData(NameCategory.TransitionRule, "transition_rule")]
    [InlineData(NameCategory.CautionScreen, "caution_screen")]
    [InlineData(NameCategory.Unknown, "unknown")]
    public void TheWireNamesAreThoseOfTheReference(NameCategory category, string wire) =>
        Assert.Equal(wire, NameGrammar.Wire(category));

    [GameDataFact]
    public void EveryNameOfTheOracleIsClassifiedIdentically()
    {
        var rows = CatalogGolden.Tsv("lookup/classify.tsv", out string[] header);
        Assert.Equal(["name", "category", "lang", "base", "char_code", "char_id", "char_name", "size_class", "layer", "detail"], header);
        Assert.True(rows.Count > 16_000, rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var bad = new Mismatches();
        foreach (var r in rows)
        {
            string name = r[0] == "-" ? string.Empty : r[0];
            var info = NameGrammar.Classify(name);
            bad.Equal($"{name} category", r[1], NameGrammar.Wire(info.Category));
            bad.Equal($"{name} lang", r[2], Dash(info.Lang));
            bad.Equal($"{name} base", r[3], Dash(info.Base));
            bad.Equal($"{name} char_code", r[4], Dash(info.CharCode));
            bad.Equal($"{name} char_id", r[5], info.CharId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            bad.Equal($"{name} char_name", r[6], Dash(info.CharName));
            bad.Equal($"{name} size_class", r[7], Dash(info.SizeClass));
            bad.Equal($"{name} layer", r[8], Dash(info.Layer));
            bad.Equal($"{name} detail", r[9], Dash(info.Detail));
        }

        bad.AssertNone("name grammar against the oracle");
    }

    [GameDataFact]
    public void TheCategoryCountsOfArchivesUiMoviesMd3AreReproduced()
    {
        var cat = SharedEboot.Catalog;
        var counts = new Dictionary<NameCategory, int>();
        foreach (var arc in new[] { ImageArchive.Allpac0, ImageArchive.Allpac1, ImageArchive.Allpac1_2, ImageArchive.Allpac3, ImageArchive.Allpac4 })
        {
            foreach (string name in cat.Nams[arc].Names)
            {
                var c = NameGrammar.Classify(name).Category;
                counts[c] = counts.GetValueOrDefault(c) + 1;
            }
        }

        Assert.Equal(0, counts.GetValueOrDefault(NameCategory.Unknown));
        Assert.Equal(1_024, counts[NameCategory.GalleryImage]);
        Assert.Equal(998, counts[NameCategory.SpriteBody]);
        Assert.Equal(49, counts[NameCategory.TransitionRule]);
        Assert.Equal(12, counts[NameCategory.CautionScreen]);
        Assert.Equal(69, counts[NameCategory.UiParts]);
        Assert.Equal(2, counts[NameCategory.UiThumbnails]);
    }

    private static string Dash(string s) => s.Length == 0 ? "-" : s;
}

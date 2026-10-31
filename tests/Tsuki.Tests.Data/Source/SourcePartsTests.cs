using Tsuki.Data;
using Tsuki.Data.Audio;
using Tsuki.Data.Census;
using Tsuki.Data.Catalog;
using Tsuki.Data.Containers;
using Tsuki.Engine.Contracts;
using Tsuki.Tests.Data.Images;

namespace Tsuki.Tests.Data.Source;

/// <summary>The parts of the source and the census that need no game folder (synthetic input), and the sound-effect names on the real file.</summary>
public sealed class SourcePartsTests
{
    // -- se_nam.mrg --------------------------------------------------------------------------------------------------

    /// <summary>Four NAM32 sections in an mrgd00 container: a record is 28 name bytes, a 2-byte tag, CR LF.</summary>
    private static byte[] SeNamFile(string[] s0, string[] s1, string[] s2, string[] s3) =>
        SyntheticImages.Container([.. new[] { s0, s1, s2, s3 }.Select(Nam32)]);

    private static byte[] Nam32(string[] names)
    {
        byte[] data = new byte[names.Length * 32];
        for (int i = 0; i < names.Length; i++)
        {
            System.Text.Encoding.ASCII.GetBytes(names[i], data.AsSpan(i * 32, 28));
            data[(i * 32) + 30] = (byte)'\r';
            data[(i * 32) + 31] = (byte)'\n';
        }

        return data;
    }

    [Fact]
    public void TheLookupKeyIsCutAfterTheLastBackslashAtTheDotAndAt28CharactersAndUpperCased()
    {
        Assert.Equal("MOON_SE00013", SeNames.Key("moon_se00013"));
        Assert.Equal("MOON_SE00013", SeNames.Key(@"se\moon_se00013.at9"));
        Assert.Equal("ABCDEFGHIJKLMNOPQRSTUVWXYZ12", SeNames.Key("abcdefghijklmnopqrstuvwxyz123456789"));
        Assert.Equal(string.Empty, SeNames.Key(@"dir\"));
        Assert.Equal("ÀB", SeNames.Key("Àb")); // only a..z are upper-cased
    }

    [Fact]
    public void ALookupSearchesSectionZeroThenThreeThenTwoAndTheFlagsSkipTheFirstTwo()
    {
        byte[] file = SeNamFile(["S00", "DUP"], ["RANKING"], ["DUMMY", "DUP", "FATE_SE001", "DUMMY"], ["SE_A0018", "DUP"]);
        SeNames names = SeNames.FromMemory(file);
        Assert.Equal(4, names.Sections.Count);
        Assert.Equal(4, names.VseCount);
        Assert.Equal(new SeLookup(0, 0), names.Lookup("s00"));
        Assert.Equal(new SeLookup(0, 1), names.Lookup("dup"));
        Assert.Equal(new SeLookup(2, 10_001), names.Lookup("dup", SeNames.SkipSectionZero));
        Assert.Equal(new SeLookup(2, 1), names.Lookup("dup", SeNames.SkipSectionZero | SeNames.SkipSectionThree));
        Assert.Equal(new SeLookup(2, 2), names.Lookup("Fate_SE001"));
        Assert.Equal(0, names.VseIndex("dummy")); // the engine scan stops at the first of two equal names
        Assert.Equal(-1, names.VseIndex("se_a0018")); // section 3 is not a vse entry
        Assert.True(names.Lookup("fate_se001")!.Value.IsVseEntry);
        Assert.False(names.Lookup("se_a0018")!.Value.IsVseEntry);
        Assert.Null(names.Lookup("no_such_sound"));
        Assert.Equal(-1, names.VseIndex("no_such_sound"));
    }

    [Fact]
    public void AFileWithoutFourSectionsIsRefused()
    {
        Assert.Equal(4, SeNames.FromMemory(SeNamFile(["A"], ["B"], ["C"], ["D"])).Sections.Count);
        byte[] three = SyntheticImages.Container([Nam32(["A"]), Nam32(["B"]), Nam32(["C"])]);
        ContainerFormatException e = Assert.Throws<ContainerFormatException>(() => SeNames.FromMemory(three));
        Assert.Contains("3 sections", e.Message, StringComparison.Ordinal);
        Assert.Throws<ContainerFormatException>(() => SeNames.FromMemory(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
    }

    // -- UI sheet names ----------------------------------------------------------------------------------------------

    [Fact]
    public void SheetNamesFollowTheLoadersRule()
    {
        Assert.Equal("conf_parts_en.mrg", UiSheetNames.FileName(UiSet.ConfParts, Language.En));
        Assert.Equal("conf_parts_ko.mrg", UiSheetNames.FileName(UiSet.ConfParts, Language.Ko));
        Assert.Equal("gallery_parts_cg_zt.mrg", UiSheetNames.FileName(UiSet.GalleryPartsCg, Language.Zt));
        Assert.Equal("gallery_parts_cg_th.mrg", UiSheetNames.FileName(UiSet.GalleryPartsCgTh, Language.Ja));
        Assert.Equal("select_parts.mrg", UiSheetNames.FileName(UiSet.SelectParts, Language.Zc));
        Assert.Equal("selecttxt_parts_ja.mrg", UiSheetNames.FileName(UiSet.SelectTxtParts, Language.Ja));
        Assert.All(Enum.GetValues<UiSet>(), set => Assert.EndsWith(".mrg", UiSheetNames.BaseName(set), StringComparison.Ordinal));
        Assert.Equal(6, Enum.GetValues<UiSet>().Count(s => !UiSheetNames.HasLanguageVariants(s)));
        Assert.Equal(19, Enum.GetValues<UiSet>().Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => UiSheetNames.BaseName((UiSet)200));
        Assert.Throws<ArgumentOutOfRangeException>(() => UiSheetNames.HasLanguageVariants((UiSet)200));
    }

    // -- the allow-list ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheAllowListExcusesOnlyTheDocumentedPictures()
    {
        foreach (string key in CensusAllowList.PictureScriptBugs.Keys)
        {
            Assert.NotNull(CensusAllowList.ImageReason(new ResolvedImage(key, -1)));
        }

        Assert.Null(CensusAllowList.ImageReason(new ResolvedImage("IMG9999_00_01", -1)));
        Assert.Null(CensusAllowList.ImageReason(new ResolvedImage("IMG5007_02_02_X", -1)));

        // a found record whose body file is one of the three missing ones is excused; any other missing file is not
        var noe = new ResolvedImage("NOE_L_60_03_N01", 12536, "NOE_L_60_03_N00_B.MZP", "NOE_L_60_03_N01.MZP", null, new ImageId(ImageArchive.Allpac1_2, 1082));
        Assert.NotNull(CensusAllowList.ImageReason(noe));
        var other = new ResolvedImage("AKI_A_00_00_N00", 5, "AKI_A_00_00_N00_B.MZP", "AKI_A_00_00_N00.MZP", null, new ImageId(ImageArchive.Allpac0, 3));
        Assert.Null(CensusAllowList.ImageReason(other));
        var both = new ResolvedImage("X", 5, "NOE_L_60_03_N00_B.MZP", "ARU_M_03_35_N00_B.MZP", null, null);
        Assert.NotNull(CensusAllowList.ImageReason(both));
        var oneOk = new ResolvedImage("X", 5, "NOE_L_60_03_N00_B.MZP", "SOMETHING_ELSE.MZP", null, null);
        Assert.Null(CensusAllowList.ImageReason(oneOk));
    }

    [Fact]
    public void TheAllowListExcusesOnlyTheDocumentedMotionsAndGlyphs()
    {
        CensusReference Motion(string key, string state) => new(ReferenceKind.Motion, 0, 0, "STMA", key, state, false);
        Assert.NotNull(CensusAllowList.Reason(Motion("0", "zero")));
        Assert.NotNull(CensusAllowList.Reason(Motion("101960", "out")));
        Assert.Null(CensusAllowList.Reason(Motion("101961", "out")));
        Assert.Null(CensusAllowList.Reason(Motion("-", "none")));
        Assert.Null(CensusAllowList.Reason(new CensusReference(ReferenceKind.Voice, 0, 0, "VPLY", "A", "-|0", false)));

        Assert.NotNull(CensusAllowList.GlyphFallbackReason("zc", "efbd82", 3));
        Assert.Null(CensusAllowList.GlyphFallbackReason("zc", "efbd82", 4)); // used more often than the documents say
        Assert.Null(CensusAllowList.GlyphFallbackReason("ja", "efbd82", 3));
        Assert.Null(CensusAllowList.GlyphFallbackReason("zt", "e9b2bd", 1));
        Assert.Equal(4, CensusAllowList.GlyphFallbacks.Count);
        Assert.Equal(3, CensusAllowList.MissingSpriteBodies.Count);
        Assert.Equal(5, CensusAllowList.PictureScriptBugs.Count);
    }

    [Fact]
    public void AReferenceLineHasTheSixColumnsOfTheOracle()
    {
        var r = new CensusReference(ReferenceKind.Image, 12, 345, "STCH", "IMG1696_11_11", "allpac0#1121;m=0", true);
        Assert.Equal("image\t12\t345\tSTCH\tIMG1696_11_11\tallpac0#1121;m=0", r.Line);
        Assert.Equal("scene\t0\t0\tJUMP\t270\t3", new CensusReference(ReferenceKind.Scene, 0, 0, "JUMP", "270", "3", true).Line);
    }

    // -- sound-effect names on the real file -------------------------------------------------------------------------

    [GameDataFact]
    public void TheRealSoundEffectNamesResolveAsAudioMdSays()
    {
        SeNames names = SharedSource.Source.SoundEffectNames;
        Assert.Equal(new[] { 8, 2, 1_327, 67 }, names.Sections.Select(s => s.Count).ToArray());
        Assert.Equal("S00", names.Sections[0][0]);
        Assert.Equal("RANKING", names.Sections[1][0]);
        Assert.Equal("DUMMY", names.Sections[2][0]);
        Assert.Equal("SE_A0018", names.Sections[3][0]);
        Assert.Equal(new SeLookup(2, 297), names.Lookup("moon_se00013", SeNames.SkipSectionZero)); // audio.md 4.2: UI code plays vse #297
        Assert.Equal(new SeLookup(0, 3), names.Lookup("S03"));
        Assert.Equal(new SeLookup(2, 10_000), names.Lookup("se_a0018"));
        Assert.Equal(0, names.VseIndex("dummy"));
        Assert.Equal(names.VseCount, SharedSource.Source.AudioCount(AudioBank.Se));
    }
}

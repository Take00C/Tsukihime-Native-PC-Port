// Where every table the data layer reads lives in one eboot.bin (package K25). The analysed build's addresses are the constants of EbootAddresses and the table classes;
// another eboot gets a layout that EbootLocator found by content. Every reader takes its addresses from EbootReader.Layout, so no reader knows a fixed address any more.
using Tsuki.Data.Catalog;
using Tsuki.Data.Eboot.Tables;
using Tsuki.Data.Fonts;
using Tsuki.Data.Scripts;

namespace Tsuki.Data.Eboot;

/// <summary>
/// The addresses and counts of every table <c>Tsuki.Data</c> reads from an eboot.bin. <see cref="Analysed"/> is the layout of the verified build
/// (CUSA47999 01.01, the constants the documents cite); <see cref="EbootLocator"/> builds one for any other eboot from its content, and
/// <see cref="EbootReader.Layout"/> hands it to the readers. Virtual addresses throughout; a count is the number of records a reader may read.
/// </summary>
public sealed record EbootLayout
{
    /// <summary>True when this is the layout of the verified build, taken from the constants rather than located.</summary>
    public bool IsAnalysed { get; init; }

    // -- catalog --------------------------------------------------------------------------------------------------------

    /// <summary>The image table (0x48-byte records).</summary>
    public long ImageTable { get; init; }

    /// <summary>Used records of the image table.</summary>
    public int ImageRecords { get; init; }

    /// <summary>Slots of the image table: the used records and the empty ones after them (the engine's bound).</summary>
    public int ImageSlots { get; init; }

    /// <summary>The picture-name list (32-byte records, one per image record plus one empty, then 0xFF).</summary>
    public long ImageNames { get; init; }

    /// <summary>The older, shorter name list; 0 when the eboot has none.</summary>
    public long OldImageNames { get; init; }

    /// <summary>The movie table (0x30-byte records).</summary>
    public long MovieTable { get; init; }

    /// <summary>Records of the movie table before its terminator.</summary>
    public int MovieRecords { get; init; }

    /// <summary>The terminator record of the movie table (id -1).</summary>
    public long MovieTerminator => MovieTable + ((long)EbootAddresses.MovieRecordSize * MovieRecords);

    /// <summary>The <c>char*</c> table of the language suffixes <c>ja en zc zt ko</c>.</summary>
    public long LanguageSuffixes { get; init; }

    /// <summary>Entries of <see cref="LanguageSuffixes"/> (5).</summary>
    public int LanguageCount { get; init; }

    /// <summary>The four prologue movie ids per language.</summary>
    public long PrologueTable { get; init; }

    /// <summary><c>char *ui_text[languages][UiTextCount]</c>.</summary>
    public long UiText { get; init; }

    /// <summary>Strings per language in <see cref="UiText"/> (1,499).</summary>
    public int UiTextCount { get; init; }

    /// <summary>The string "Hunex Consumer Library".</summary>
    public long HunexString { get; init; }

    /// <summary>The UI texture registration tables, in address order.</summary>
    public IReadOnlyList<long> UiTables { get; init; } = [];

    // -- fonts ----------------------------------------------------------------------------------------------------------

    /// <summary>The 16 glyph lookup tables (fonts.md 5.1), in engine order.</summary>
    public IReadOnlyList<FontLookupSpec> FontLookups { get; init; } = [];

    /// <summary>The 17 glyph record tables (fonts.md 5.3), in engine order.</summary>
    public IReadOnlyList<FontRecordSpec> FontRecords { get; init; } = [];

    /// <summary>The wide-grid base slots, a u16 per language.</summary>
    public long FontWideBase { get; init; }

    /// <summary>The 39 <c>[tag]</c> names, 11 bytes each.</summary>
    public long FontTags { get; init; }

    // -- scripts and motions --------------------------------------------------------------------------------------------

    /// <summary>The scene-id table (an i16 per script, -1 terminated).</summary>
    public long SceneIds { get; init; }

    /// <summary>Scripts in <see cref="SceneIds"/>.</summary>
    public int SceneCount { get; init; }

    /// <summary>The STMA field-enable masks, <c>u64[MotionCount + 1]</c>.</summary>
    public long MotionMasks { get; init; }

    /// <summary>The motion durations, <c>i64[MotionCount]</c>.</summary>
    public long MotionDurations { get; init; }

    /// <summary>The exclusive keyframe end index of each motion, <c>i64[MotionCount]</c>.</summary>
    public long MotionEnds { get; init; }

    /// <summary>The 72-byte keyframes.</summary>
    public long MotionKeys { get; init; }

    /// <summary>Motions (24,192).</summary>
    public int MotionCount { get; init; }

    /// <summary>Keyframes (71,905): the end index of the last motion.</summary>
    public int KeyframeCount { get; init; }

    // -- engine tables (docs/research/knife_tables.md) ------------------------------------------------------------------

    /// <summary>The STRA haze parameters (10 floats per entry, entry 0 the filler).</summary>
    public long Haze { get; init; }

    /// <summary>Entries of <see cref="Haze"/> including entry 0 (558).</summary>
    public int HazeEntries { get; init; }

    /// <summary>The flowchart node of each scene, <c>i32[SceneNodeCount]</c>.</summary>
    public long SceneNodes { get; init; }

    /// <summary>Entries of <see cref="SceneNodes"/> (527).</summary>
    public int SceneNodeCount { get; init; }

    /// <summary>The compiled STMA loop switch, <c>int loop_point(int id)</c>.</summary>
    public long MotionLoopFunction { get; init; }

    /// <summary>The compiled flowchart-jump clears, <c>void clear(int column)</c>.</summary>
    public long JumpClearFunction { get; init; }

    /// <summary>The F word array the clears write into.</summary>
    public long FWordsBase { get; init; }

    /// <summary>The flow-jump copy of the keep lists: words, bytes, bits.</summary>
    public IReadOnlyList<long> FlowJumpKeepLists { get; init; } = [];

    /// <summary>The history-record copy of the keep lists: words, bytes, bits.</summary>
    public IReadOnlyList<long> HistoryKeepLists { get; init; } = [];

    /// <summary>The 30 x 4 focus-line keys.</summary>
    public long FclnLines { get; init; }

    /// <summary>The three falling-note records.</summary>
    public long FclnNotes { get; init; }

    /// <summary>The voice speaker records.</summary>
    public long VoiceSpeakers { get; init; }

    /// <summary>Records of <see cref="VoiceSpeakers"/> (36).</summary>
    public int VoiceSpeakerRecords { get; init; }

    /// <summary>Speaker to config voice row bit, <c>i32[24]</c>.</summary>
    public long VoiceRowBits { get; init; }

    /// <summary>Config voice row to progress word, <c>i32[19]</c>.</summary>
    public long VoiceRowWords { get; init; }

    /// <summary>The 14 config SE test names.</summary>
    public long TestSeNames { get; init; }

    /// <summary>The confirm-dialog and help message ids, <c>i32[55]</c>.</summary>
    public long DialogMessages { get; init; }

    /// <summary>The message window types.</summary>
    public long WindowTypes { get; init; }

    /// <summary>Records of <see cref="WindowTypes"/> (56).</summary>
    public int WindowTypeCount { get; init; }

    /// <summary>The 27 window x offsets.</summary>
    public long WindowDx { get; init; }

    /// <summary>The 37 window y offsets.</summary>
    public long WindowDy { get; init; }

    /// <summary>The six soft-clip radii.</summary>
    public long ClipRadii { get; init; }

    /// <summary>The <c>@c1..@c5</c> fill colours.</summary>
    public long TextColours { get; init; }

    /// <summary>The two config pages.</summary>
    public long ConfigPages { get; init; }

    /// <summary>The CG gallery groups.</summary>
    public long GalleryGroups { get; init; }

    /// <summary>Records of <see cref="GalleryGroups"/> (458).</summary>
    public int GalleryGroupCount { get; init; }

    /// <summary>The CG alias records.</summary>
    public long GalleryAliases { get; init; }

    /// <summary>Records of <see cref="GalleryAliases"/> (105).</summary>
    public int GalleryAliasCount { get; init; }

    /// <summary>The special (four-language-band) CG names.</summary>
    public long GallerySpecial { get; init; }

    /// <summary>Entries of <see cref="GallerySpecial"/> (11).</summary>
    public int GallerySpecialCount { get; init; }

    /// <summary>The picture seen-index map.</summary>
    public long SeenMap { get; init; }

    /// <summary>The music room records.</summary>
    public long Music { get; init; }

    /// <summary>Records of <see cref="Music"/> (95).</summary>
    public int MusicCount { get; init; }

    /// <summary>The ending-list records.</summary>
    public long Extras { get; init; }

    /// <summary>Records of <see cref="Extras"/> (25).</summary>
    public int ExtraCount { get; init; }

    /// <summary>The trophy scene records.</summary>
    public long TrophyScenes { get; init; }

    /// <summary>Records of <see cref="TrophyScenes"/> (27).</summary>
    public int TrophySceneCount { get; init; }

    /// <summary>The jump table of the CG category switch.</summary>
    public long CategoryJumpTable { get; init; }

    /// <summary>The common join of the category switch's cases.</summary>
    public long CategoryJoin { get; init; }

    /// <summary>Unlock categories with a rule (19).</summary>
    public int CategoryCount { get; init; }

    /// <summary>The flowchart column records.</summary>
    public long FlowColumns { get; init; }

    /// <summary>Records of <see cref="FlowColumns"/> (277).</summary>
    public int FlowColumnCount { get; init; }

    /// <summary>The flowchart edge records.</summary>
    public long FlowEdges { get; init; }

    /// <summary>Records of <see cref="FlowEdges"/> (630).</summary>
    public int FlowEdgeCount { get; init; }

    /// <summary>The choice edge flag triples.</summary>
    public long ChoiceFlags { get; init; }

    /// <summary>Records of <see cref="ChoiceFlags"/> (83).</summary>
    public int ChoiceFlagCount { get; init; }

    /// <summary>The savetitle cell tables: other languages, English.</summary>
    public IReadOnlyList<long> SaveTitleCells { get; init; } = [];

    /// <summary>The system_phasetitle cell tables: ja, en, zh.</summary>
    public IReadOnlyList<long> PhaseTitleCells { get; init; } = [];

    /// <summary>The flow_phasetitle cell tables: other languages, English.</summary>
    public IReadOnlyList<long> FlowTitleCells { get; init; } = [];

    /// <summary>The flow_line_merge connector cells.</summary>
    public long FlowLineCells { get; init; }

    /// <summary>The conf_faceicon cells.</summary>
    public long VoiceFaceCells { get; init; }

    /// <summary>The conf_name cell tables: other languages, English.</summary>
    public IReadOnlyList<long> VoiceNameCells { get; init; } = [];

    /// <summary>The help_key icon cells.</summary>
    public long GuideIconCells { get; init; }

    /// <summary>The help_txt word cells (five language blocks).</summary>
    public long GuideWordCells { get; init; }

    /// <summary>The two mode banner cells.</summary>
    public IReadOnlyList<long> BannerRects { get; init; } = [];

    /// <summary>The banner pop-in scale keys.</summary>
    public long BannerKeys { get; init; }

    /// <summary>The six title background indices.</summary>
    public long TitleBackground { get; init; }

    /// <summary>The layout of the verified build: every constant the documents cite.</summary>
    public static EbootLayout Analysed { get; } = new()
    {
        IsAnalysed = true,
        ImageTable = EbootAddresses.ImageTable,
        ImageRecords = EbootAddresses.ImageTableRecords,
        ImageSlots = EbootAddresses.ImageTableSlots,
        ImageNames = EbootAddresses.ImageNames,
        OldImageNames = EbootAddresses.OldImageNames,
        MovieTable = EbootAddresses.MovieTable,
        MovieRecords = (int)((EbootAddresses.MovieTerminator - EbootAddresses.MovieTable) / EbootAddresses.MovieRecordSize),
        LanguageSuffixes = EbootAddresses.LanguageSuffixes,
        LanguageCount = 5,
        PrologueTable = EbootAddresses.PrologueTable,
        UiText = EbootAddresses.UiText,
        UiTextCount = UiTextTable.Count,
        HunexString = EbootAddresses.HunexString,
        UiTables = EbootAddresses.UiTables,
        FontLookups = EbootFontData.LookupSpecs,
        FontRecords = EbootFontData.RecordSpecs,
        FontWideBase = EbootFontData.WideBaseVa,
        FontTags = EbootFontData.TagsVa,
        SceneIds = EbootAddresses.SceneIds,
        SceneCount = SceneTable.RetailCount,
        MotionMasks = MotionFieldMasks.Va,
        MotionDurations = MotionTables.DurationVa,
        MotionEnds = MotionTables.EndVa,
        MotionKeys = MotionTables.KeysVa,
        MotionCount = MotionTables.MotionCount,
        KeyframeCount = MotionTables.MaxKey + 1,
        Haze = HazeTable.Va,
        HazeEntries = HazeTable.MaxId + 1,
        SceneNodes = SceneNodeTable.Va,
        SceneNodeCount = SceneNodeTable.Count,
        MotionLoopFunction = MotionLoopTable.FunctionVa,
        JumpClearFunction = JumpClearTable.FunctionVa,
        FWordsBase = JumpClearTable.FWordsBase,
        FlowJumpKeepLists = FlowKeepLists.FlowJumpVa,
        HistoryKeepLists = FlowKeepLists.HistoryVa,
        FclnLines = FclnLineTable.Va,
        FclnNotes = FclnNoteTable.Va,
        VoiceSpeakers = VoiceSpeakerTable.Va,
        VoiceSpeakerRecords = VoiceSpeakerTable.RecordCount,
        VoiceRowBits = VoiceSpeakerTable.RowBitVa,
        VoiceRowWords = VoiceSpeakerTable.RowWordVa,
        TestSeNames = Tables.TestSeNames.Va,
        DialogMessages = DialogMessageTable.Va,
        WindowTypes = MessageWindowTable.TypesVa,
        WindowTypeCount = MessageWindowTable.TypeCount,
        WindowDx = MessageWindowTable.DxVa,
        WindowDy = MessageWindowTable.DyVa,
        ClipRadii = SoftClipRadii.Va,
        TextColours = TextColourTable.Va,
        ConfigPages = ConfigPageTable.Va,
        GalleryGroups = GalleryTables.GroupVa,
        GalleryGroupCount = GalleryTables.GroupCount,
        GalleryAliases = GalleryTables.AliasVa,
        GalleryAliasCount = GalleryTables.AliasCount,
        GallerySpecial = GalleryTables.SpecialVa,
        GallerySpecialCount = GalleryTables.SpecialCount,
        SeenMap = GalleryTables.SeenMapVa,
        Music = GalleryTables.MusicVa,
        MusicCount = GalleryTables.MusicCount,
        Extras = GalleryTables.ExtraVa,
        ExtraCount = GalleryTables.ExtraCount,
        TrophyScenes = GalleryTables.TrophySceneVa,
        TrophySceneCount = GalleryTables.TrophySceneCount,
        CategoryJumpTable = GalleryTables.CategoryJumpTableVa,
        CategoryJoin = GalleryTables.CategoryJoin,
        CategoryCount = GalleryTables.CategoryCount,
        FlowColumns = FlowchartTables.ColumnVa,
        FlowColumnCount = FlowchartTables.ColumnCount,
        FlowEdges = FlowchartTables.EdgeVa,
        FlowEdgeCount = FlowchartTables.EdgeCount,
        ChoiceFlags = FlowchartTables.ChoiceFlagVa,
        ChoiceFlagCount = FlowchartTables.ChoiceFlagCount,
        SaveTitleCells = UiCellTables.SaveTitleVa,
        PhaseTitleCells = UiCellTables.PhaseTitleVa,
        FlowTitleCells = UiCellTables.FlowTitleVa,
        FlowLineCells = UiCellTables.FlowLineVa,
        VoiceFaceCells = UiCellTables.VoiceFaceVa,
        VoiceNameCells = UiCellTables.VoiceNameVa,
        GuideIconCells = UiCellTables.GuideIconVa,
        GuideWordCells = UiCellTables.GuideWordVa,
        BannerRects = UiCellTables.BannerRectVa,
        BannerKeys = UiCellTables.BannerKeyVa,
        TitleBackground = TitleBackgroundVa,
    };

    /// <summary>Text VA of the six title background indices of the verified build (read by the title screen; package K5a).</summary>
    public const long TitleBackgroundVa = 0x218A40;

    /// <summary>
    /// The named addresses of this layout, in a fixed order, for reports and for the equality test of the locator: <c>(name, address)</c> pairs where a table
    /// family lists one line per member.
    /// </summary>
    public IEnumerable<(string Name, long Va)> Addresses()
    {
        yield return ("ImageTable", ImageTable);
        yield return ("ImageNames", ImageNames);
        yield return ("OldImageNames", OldImageNames);
        yield return ("MovieTable", MovieTable);
        yield return ("MovieTerminator", MovieTerminator);
        yield return ("LanguageSuffixes", LanguageSuffixes);
        yield return ("PrologueTable", PrologueTable);
        yield return ("UiText", UiText);
        yield return ("HunexString", HunexString);
        for (int i = 0; i < UiTables.Count; i++)
        {
            yield return ($"UiTables[{i}]", UiTables[i]);
        }

        foreach (FontLookupSpec l in FontLookups)
        {
            yield return ($"FontLookup {l.Name}", l.Va);
        }

        foreach (FontRecordSpec r in FontRecords)
        {
            yield return ($"FontRecords {r.Name}", r.Va);
        }

        yield return ("FontWideBase", FontWideBase);
        yield return ("FontTags", FontTags);
        yield return ("SceneIds", SceneIds);
        yield return ("MotionMasks", MotionMasks);
        yield return ("MotionDurations", MotionDurations);
        yield return ("MotionEnds", MotionEnds);
        yield return ("MotionKeys", MotionKeys);
        yield return ("Haze", Haze);
        yield return ("SceneNodes", SceneNodes);
        yield return ("MotionLoopFunction", MotionLoopFunction);
        yield return ("JumpClearFunction", JumpClearFunction);
        yield return ("FWordsBase", FWordsBase);
        for (int i = 0; i < FlowJumpKeepLists.Count; i++)
        {
            yield return ($"FlowJumpKeepLists[{i}]", FlowJumpKeepLists[i]);
        }

        for (int i = 0; i < HistoryKeepLists.Count; i++)
        {
            yield return ($"HistoryKeepLists[{i}]", HistoryKeepLists[i]);
        }

        yield return ("FclnLines", FclnLines);
        yield return ("FclnNotes", FclnNotes);
        yield return ("VoiceSpeakers", VoiceSpeakers);
        yield return ("VoiceRowBits", VoiceRowBits);
        yield return ("VoiceRowWords", VoiceRowWords);
        yield return ("TestSeNames", TestSeNames);
        yield return ("DialogMessages", DialogMessages);
        yield return ("WindowTypes", WindowTypes);
        yield return ("WindowDx", WindowDx);
        yield return ("WindowDy", WindowDy);
        yield return ("ClipRadii", ClipRadii);
        yield return ("TextColours", TextColours);
        yield return ("ConfigPages", ConfigPages);
        yield return ("GalleryGroups", GalleryGroups);
        yield return ("GalleryAliases", GalleryAliases);
        yield return ("GallerySpecial", GallerySpecial);
        yield return ("SeenMap", SeenMap);
        yield return ("Music", Music);
        yield return ("Extras", Extras);
        yield return ("TrophyScenes", TrophyScenes);
        yield return ("CategoryJumpTable", CategoryJumpTable);
        yield return ("CategoryJoin", CategoryJoin);
        yield return ("FlowColumns", FlowColumns);
        yield return ("FlowEdges", FlowEdges);
        yield return ("ChoiceFlags", ChoiceFlags);
        for (int i = 0; i < SaveTitleCells.Count; i++)
        {
            yield return ($"SaveTitleCells[{i}]", SaveTitleCells[i]);
        }

        for (int i = 0; i < PhaseTitleCells.Count; i++)
        {
            yield return ($"PhaseTitleCells[{i}]", PhaseTitleCells[i]);
        }

        for (int i = 0; i < FlowTitleCells.Count; i++)
        {
            yield return ($"FlowTitleCells[{i}]", FlowTitleCells[i]);
        }

        yield return ("FlowLineCells", FlowLineCells);
        yield return ("VoiceFaceCells", VoiceFaceCells);
        for (int i = 0; i < VoiceNameCells.Count; i++)
        {
            yield return ($"VoiceNameCells[{i}]", VoiceNameCells[i]);
        }

        yield return ("GuideIconCells", GuideIconCells);
        yield return ("GuideWordCells", GuideWordCells);
        for (int i = 0; i < BannerRects.Count; i++)
        {
            yield return ($"BannerRects[{i}]", BannerRects[i]);
        }

        yield return ("BannerKeys", BannerKeys);
        yield return ("TitleBackground", TitleBackground);
    }

    /// <summary>The counts of this layout as <c>(name, count)</c> pairs, in a fixed order.</summary>
    public IEnumerable<(string Name, int Count)> Counts()
    {
        yield return ("ImageRecords", ImageRecords);
        yield return ("ImageSlots", ImageSlots);
        yield return ("MovieRecords", MovieRecords);
        yield return ("LanguageCount", LanguageCount);
        yield return ("UiTextCount", UiTextCount);
        yield return ("UiTables", UiTables.Count);
        foreach (FontLookupSpec l in FontLookups)
        {
            yield return ($"FontLookup {l.Name}", l.Count);
        }

        foreach (FontRecordSpec r in FontRecords)
        {
            yield return ($"FontRecords {r.Name}", r.Count);
        }

        yield return ("SceneCount", SceneCount);
        yield return ("MotionCount", MotionCount);
        yield return ("KeyframeCount", KeyframeCount);
        yield return ("HazeEntries", HazeEntries);
        yield return ("SceneNodeCount", SceneNodeCount);
        yield return ("VoiceSpeakerRecords", VoiceSpeakerRecords);
        yield return ("WindowTypeCount", WindowTypeCount);
        yield return ("GalleryGroupCount", GalleryGroupCount);
        yield return ("GalleryAliasCount", GalleryAliasCount);
        yield return ("GallerySpecialCount", GallerySpecialCount);
        yield return ("MusicCount", MusicCount);
        yield return ("ExtraCount", ExtraCount);
        yield return ("TrophySceneCount", TrophySceneCount);
        yield return ("CategoryCount", CategoryCount);
        yield return ("FlowColumnCount", FlowColumnCount);
        yield return ("FlowEdgeCount", FlowEdgeCount);
        yield return ("ChoiceFlagCount", ChoiceFlagCount);
    }
}

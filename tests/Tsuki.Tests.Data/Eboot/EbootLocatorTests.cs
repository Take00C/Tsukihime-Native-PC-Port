using System.Buffers.Binary;
using Tsuki.Data.Eboot;
using Tsuki.Data.Verification;
using Tsuki.Tests.Data.Catalog;

namespace Tsuki.Tests.Data.Eboot;

/// <summary>
/// Package K25: the content locator on the real eboot. On the verified eboot it must find every table of <see cref="EbootLayout.Analysed"/> at the documented
/// address with the documented count; a damaged copy is refused by name; the second eboot of this machine (the 5.05 backport in the update folder beside the game,
/// when it is there) has every table at the same place with the same bytes.
/// </summary>
public sealed class EbootLocatorTests(ITestOutputHelper output)
{
    private static LocateResult Located => LocatedLazy.Value;

    private static readonly Lazy<LocateResult> LocatedLazy = new(() => EbootLocator.Locate(SharedEboot.Reader));

    [GameDataFact]
    public void EveryTableOfTheVerifiedEbootIsFoundAtTheDocumentedAddress()
    {
        LocateResult result = Located;
        output.WriteLine(result.Summary());
        output.WriteLine(result.Report());
        Assert.True(result.Complete, string.Join("; ", result.Missing.Select(m => $"{m.Name}: {m.Problem}")));
        var mismatches = new Mismatches();
        var expected = EbootLayout.Analysed.Addresses().ToList();
        var actual = result.Layout!.Addresses().ToList();
        mismatches.Equal("address count", expected.Count, actual.Count);
        foreach (((string name, long want), (string _, long got)) in expected.Zip(actual))
        {
            mismatches.Equal(name, $"0x{want:x}", $"0x{got:x}");
        }

        foreach (((string name, int want), (string _, int got)) in EbootLayout.Analysed.Counts().Zip(result.Layout.Counts()))
        {
            mismatches.Equal("count " + name, want, got);
        }

        mismatches.AssertNone("located layout against the documented one");
        Assert.False(result.Layout.IsAnalysed);
        Assert.True(result.Found.Count >= 60, $"{result.Found.Count} tables");
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(20), $"{result.Elapsed}");
    }

    [GameDataFact]
    public void EveryTableReadThroughTheLocatedLayoutValidates()
    {
        LocateResult result = Located;
        Assert.True(result.Complete);
        IReadOnlyList<string> failed = EbootLocator.Validate(SharedEboot.Reader, result.Layout!);
        Assert.Empty(failed);
        Assert.Empty(EbootLocator.Validate(SharedEboot.Reader, EbootLayout.Analysed));
    }

    [GameDataFact]
    public void TheLocatedLayoutReadsTheSameTablesAsTheDocumentedOne()
    {
        EbootReader located = SharedEboot.Reader.WithLayout(Located.Layout!);
        EbootReader documented = SharedEboot.Reader;
        Assert.Equal(Tsuki.Data.Catalog.ImageCatalog.Read(documented).Count, Tsuki.Data.Catalog.ImageCatalog.Read(located).Count);
        Assert.Equal(Tsuki.Data.Catalog.MovieTable.Read(documented).Select(m => m.Path), Tsuki.Data.Catalog.MovieTable.Read(located).Select(m => m.Path));
        Assert.Equal(Tsuki.Data.Scripts.SceneTable.Read(documented).Ids.ToArray(), Tsuki.Data.Scripts.SceneTable.Read(located).Ids.ToArray());
        var a = new Tsuki.Data.Eboot.Tables.EngineTables(documented);
        var b = new Tsuki.Data.Eboot.Tables.EngineTables(located);
        Assert.Equal(a.MotionLoops.Loops, b.MotionLoops.Loops);
        Assert.Equal(a.JumpClears.Pairs, b.JumpClears.Pairs);
        Assert.Equal(a.Gallery.Music.Count, b.Gallery.Music.Count);
        Assert.Equal(a.Flowchart.Nodes.Count(), b.Flowchart.Nodes.Count());
        Assert.Equal(a.Dialogs.Ids.ToArray(), b.Dialogs.Ids.ToArray());
        Assert.Equal(Tsuki.Data.Fonts.EbootFontData.Read(documented).LookupEntryCount, Tsuki.Data.Fonts.EbootFontData.Read(located).LookupEntryCount);
        Assert.Equal(Tsuki.Data.Fonts.EbootFontData.Read(documented).Tags, Tsuki.Data.Fonts.EbootFontData.Read(located).Tags);
    }

    private static byte[] Copy() => (byte[])SharedEboot.Data.Clone();

    private static long File(long va) => SharedEboot.Reader.FileOffset(va) ?? throw new InvalidOperationException($"0x{va:x} is not file-backed");

    [GameDataFact]
    public void ADamagedTableIsReportedByNameAndNothingThrows()
    {
        // the haze head (its signature): the table is missing, the rest is found
        byte[] haze = Copy();
        haze.AsSpan((int)File(EbootLayout.Analysed.Haze + 40), 40).Clear();
        LocateResult r1 = EbootLocator.Locate(EbootReader.Parse(haze));
        Assert.False(r1.Complete);
        Assert.Contains(r1.Missing, m => m.Name == "Haze");
        Assert.DoesNotContain(r1.Missing, m => m.Name == "ImageTable");
        output.WriteLine("haze damaged: " + r1.Summary());

        // the picture-name string BLACK.mzp: the image table and the name lists go
        byte[] black = Copy();
        black[(int)File(0x1A8250)] = (byte)'X';
        LocateResult r2 = EbootLocator.Locate(EbootReader.Parse(black));
        Assert.Contains(r2.Missing, m => m.Name == "ImageTable");
        Assert.Contains(r2.Missing, m => m.Name == "ImageNames");
        output.WriteLine("BLACK damaged: " + r2.Summary());

        // one glyph lookup key out of order: the lookups fail validation
        byte[] lookups = Copy();
        BinaryPrimitives.WriteUInt64LittleEndian(lookups.AsSpan((int)File(EbootLayout.Analysed.FontLookups[1].Va + 16)), 1);
        LocateResult r3 = EbootLocator.Locate(EbootReader.Parse(lookups));
        Assert.Contains(r3.Missing, m => m.Name == "FontLookups");
        output.WriteLine("lookup damaged: " + r3.Summary());

        // the jump-clear function's first instruction: the code pattern is gone
        byte[] clear = Copy();
        clear[(int)File(EbootLayout.Analysed.JumpClearFunction)] = 0x90;
        LocateResult r4 = EbootLocator.Locate(EbootReader.Parse(clear));
        Assert.Contains(r4.Missing, m => m.Name == "JumpClearFunction");
        Assert.Contains("occurs 0 times", r4.Missing.First(m => m.Name == "JumpClearFunction").Problem, StringComparison.Ordinal);

        // and the message a player would see
        var identity = BuildIdentity.Identify(new Tsuki.Data.IO.DirectoryGameFiles(GameFolder.Require()), clear);
        var e = new UnsupportedBuildException(identity, r4);
        Assert.Contains("JumpClearFunction", e.Message, StringComparison.Ordinal);
        Assert.Contains("report it with the log", e.Message, StringComparison.Ordinal);
        Assert.Contains("CUSA47999", e.Message, StringComparison.Ordinal);
        Assert.Equal(BuildMatch.KnownRelease, identity.Match);
    }

    [GameDataFact]
    public void TheIdentityOfTheVerifiedFolderIsTheVerifiedBuildWithFourLanguages()
    {
        var identity = BuildIdentity.Identify(new Tsuki.Data.IO.DirectoryGameFiles(GameFolder.Require()), SharedEboot.Data);
        output.WriteLine(identity.Report());
        Assert.Equal(BuildMatch.Verified, identity.Match);
        Assert.Same(KnownBuilds.Verified, identity.Known);
        Assert.Equal("CUSA47999", identity.TitleId);
        Assert.Equal(new[] { Tsuki.Engine.Contracts.Language.Ja, Tsuki.Engine.Contracts.Language.En, Tsuki.Engine.Contracts.Language.Zc, Tsuki.Engine.Contracts.Language.Zt }, identity.TextLanguages);
        Assert.Equal(identity.TextLanguages, identity.FontLanguages);
        Assert.Equal(identity.TextLanguages, identity.MovieLanguages);
        Assert.Equal(identity.TextLanguages, identity.AvailableLanguages);
        Assert.Contains(Tsuki.Engine.Contracts.Language.Ko, identity.SheetLanguages);           // the Korean UI sheets exist, the text and glyphs do not: ko is not selectable
        Assert.DoesNotContain(Tsuki.Engine.Contracts.Language.Ko, identity.AvailableLanguages);
        Assert.StartsWith("known build: CUSA47999 01.01", identity.Describe(), StringComparison.Ordinal);
    }

    /// <summary>The update folder beside the game folder on this machine, when there is one with an eboot of its own.</summary>
    public static string? SecondEboot()
    {
        string? patch = Tsuki.Data.IO.GameLayoutResolver.FindPatchBeside(GameFolder.Require());
        return patch is not null && System.IO.File.Exists(Path.Combine(patch, "eboot.bin")) ? Path.Combine(patch, "eboot.bin") : null;
    }

    [GameDataFact]
    public void TheSecondEbootOfThisMachineHasEveryTableAtTheSamePlaceWithTheSameBytes()
    {
        string? second = SecondEboot();
        if (second is null)
        {
            output.WriteLine("no update folder with an eboot beside the game folder: only the verified eboot is checked here");
            Assert.True(Located.Complete);
            return;
        }

        byte[] other = System.IO.File.ReadAllBytes(second);
        EbootReader reader = EbootReader.Parse(other);
        LocateResult result = EbootLocator.Locate(reader);
        output.WriteLine($"{second}: {result.Summary()}");
        Assert.True(result.Complete, string.Join("; ", result.Missing.Select(m => $"{m.Name}: {m.Problem}")));
        var mismatches = new Mismatches();
        foreach (((string name, long want), (string _, long got)) in EbootLayout.Analysed.Addresses().Zip(result.Layout!.Addresses()))
        {
            mismatches.Equal(name, $"0x{want:x}", $"0x{got:x}");
        }

        foreach (((string name, int want), (string _, int got)) in EbootLayout.Analysed.Counts().Zip(result.Layout.Counts()))
        {
            mismatches.Equal("count " + name, want, got);
        }

        mismatches.AssertNone("the second eboot's layout against the documented one");

        // every table's bytes are identical to the verified eboot's (the differing bytes are code and version words)
        byte[] mine = SharedEboot.Data;
        int differing = 0;
        foreach ((string name, long va, int length) in TableRanges(result.Layout))
        {
            long fo = reader.FileOffset(va) ?? throw new InvalidOperationException(name);
            if (!mine.AsSpan((int)fo, length).SequenceEqual(other.AsSpan((int)fo, length)))
            {
                differing++;
                output.WriteLine($"table {name} differs");
            }
        }

        Assert.Equal(0, differing);
        int differentBytes = 0;
        for (int i = 0; i < mine.Length; i++)
        {
            if (mine[i] != other[i])
            {
                differentBytes++;
            }
        }

        output.WriteLine($"{differentBytes} bytes differ between the two eboots");
        Assert.True(differentBytes < 1000, $"{differentBytes} differing bytes");

        var identity = BuildIdentity.Identify(new Tsuki.Data.IO.DirectoryGameFiles(Path.GetDirectoryName(second)!), other);
        Assert.Equal(BuildMatch.Verified, identity.Match);
        Assert.NotNull(identity.Known);
        Assert.NotSame(KnownBuilds.Verified, identity.Known);
        Assert.Empty(EbootLocator.Validate(reader, result.Layout));
    }

    /// <summary>The byte ranges the readers read for the tables that have a stride and a count.</summary>
    private static IEnumerable<(string Name, long Va, int Length)> TableRanges(EbootLayout l)
    {
        yield return ("ImageTable", l.ImageTable, 0x48 * l.ImageSlots);
        yield return ("ImageNames", l.ImageNames, 32 * (l.ImageRecords + 1));
        yield return ("MovieTable", l.MovieTable, 0x30 * (l.MovieRecords + 1));
        yield return ("LanguageSuffixes", l.LanguageSuffixes, 8 * l.LanguageCount);
        yield return ("PrologueTable", l.PrologueTable, 16);
        yield return ("UiText", l.UiText, 8 * l.UiTextCount * l.LanguageCount);
        foreach (var lk in l.FontLookups)
        {
            yield return ("lookup " + lk.Name, lk.Va, 16 * lk.Count);
        }

        foreach (var rc in l.FontRecords)
        {
            yield return ("records " + rc.Name, rc.Va, 10 * rc.Count);
        }

        yield return ("FontTags", l.FontTags, 11 * 39);
        yield return ("FontWideBase", l.FontWideBase, 10);
        yield return ("SceneIds", l.SceneIds, 2 * (l.SceneCount + 1));
        yield return ("MotionMasks", l.MotionMasks, 8 * (l.MotionCount + 1));
        yield return ("MotionDurations", l.MotionDurations, 8 * l.MotionCount);
        yield return ("MotionEnds", l.MotionEnds, 8 * l.MotionCount);
        yield return ("MotionKeys", l.MotionKeys, 72 * l.KeyframeCount);
        yield return ("Haze", l.Haze, 40 * l.HazeEntries);
        yield return ("SceneNodes", l.SceneNodes, 4 * l.SceneNodeCount);
        yield return ("MotionLoopFunction", l.MotionLoopFunction, 0x600);
        yield return ("JumpClearFunction", l.JumpClearFunction, 0xF8);
        yield return ("FclnLines", l.FclnLines, 36 * 120);
        yield return ("FclnNotes", l.FclnNotes, 3 * 0x58);
        yield return ("VoiceSpeakers", l.VoiceSpeakers, 16 * l.VoiceSpeakerRecords);
        yield return ("VoiceRowBits", l.VoiceRowBits, 4 * 24);
        yield return ("VoiceRowWords", l.VoiceRowWords, 4 * 19);
        yield return ("TestSeNames", l.TestSeNames, 13 * 14);
        yield return ("DialogMessages", l.DialogMessages, 4 * 55);
        yield return ("WindowTypes", l.WindowTypes, 12 * l.WindowTypeCount);
        yield return ("WindowDx", l.WindowDx, 4 * 27);
        yield return ("WindowDy", l.WindowDy, 4 * 37);
        yield return ("ClipRadii", l.ClipRadii, 24);
        yield return ("TextColours", l.TextColours, 12);
        yield return ("ConfigPages", l.ConfigPages, 2 * 0x9D0);
        yield return ("GalleryGroups", l.GalleryGroups, 32 * l.GalleryGroupCount);
        yield return ("GalleryAliases", l.GalleryAliases, 0x28 * l.GalleryAliasCount);
        yield return ("GallerySpecial", l.GallerySpecial, 8 * (l.GallerySpecialCount + 1));
        yield return ("Music", l.Music, 0x38 * l.MusicCount);
        yield return ("Extras", l.Extras, 20 * l.ExtraCount);
        yield return ("TrophyScenes", l.TrophyScenes, 8 * l.TrophySceneCount);
        yield return ("CategoryJumpTable", l.CategoryJumpTable, 4 * l.CategoryCount);
        yield return ("FlowColumns", l.FlowColumns, 32 * l.FlowColumnCount);
        yield return ("FlowEdges", l.FlowEdges, 48 * l.FlowEdgeCount);
        yield return ("ChoiceFlags", l.ChoiceFlags, 12 * l.ChoiceFlagCount);
        foreach (long va in l.SaveTitleCells.Concat(l.PhaseTitleCells))
        {
            yield return ("title cells", va, 8 * 38);
        }

        foreach (long va in l.FlowTitleCells)
        {
            yield return ("flow title cells", va, 8 * 30);
        }

        yield return ("FlowLineCells", l.FlowLineCells, 8 * 12);
        yield return ("VoiceFaceCells", l.VoiceFaceCells, 8 * 20);
        foreach (long va in l.VoiceNameCells)
        {
            yield return ("voice name cells", va, 8 * 20);
        }

        yield return ("GuideIconCells", l.GuideIconCells, 8 * 23);
        yield return ("GuideWordCells", l.GuideWordCells, 8 * 27 * 5);
        yield return ("BannerKeys", l.BannerKeys, 80 + 32);
        yield return ("TitleBackground", l.TitleBackground, 24);
    }
}

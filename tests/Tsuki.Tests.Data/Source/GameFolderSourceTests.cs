using System.Security.Cryptography;
using Tsuki.Data;
using Tsuki.Data.Catalog;
using Tsuki.Data.Eboot;
using Tsuki.Data.Images;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Data.Source;

/// <summary>
/// <see cref="GameFolderSource"/> on the user's folder: what it opens and when, how it refuses arguments the game does not have,
/// the UI sheets of allpac3, and calls from several threads. Agreement with the golden slice is in
/// <c>GameFolderSourceAgreesWithGoldenTests</c>; every reference of every script is in the census tests.
/// </summary>
public sealed class GameFolderSourceTests(ITestOutputHelper output)
{
    private static GameFolderSource Folder => SharedSource.Source;

    private static readonly UiSet[] AllSets = Enum.GetValues<UiSet>();

    private static readonly Language[] SelectableLanguages = [Language.Ja, Language.En, Language.Zc, Language.Zt];

    [GameDataFact]
    public void OpeningNeedsOnlyTheEbootAndTheFiveNamTables()
    {
        // a folder with no archive at all: eboot.bin and the five image .nam files copied next to each other
        string dir = Path.Combine(Path.GetTempPath(), "tsuki-k1a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "image"));
        try
        {
            File.Copy(Path.Combine(GameFolder.Require(), "eboot.bin"), Path.Combine(dir, "eboot.bin"));
            foreach (string name in new[] { "allpac0", "allpac1", "allpac1_2", "allpac3", "allpac4" })
            {
                File.Copy(Path.Combine(GameFolder.Require(), "image", name + ".nam"), Path.Combine(dir, "image", name + ".nam"));
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using GameFolderSource source = GameFolderSource.Open(dir);
            watch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            output.WriteLine($"Open: {watch.Elapsed.TotalMilliseconds:F1} ms, {allocated:N0} bytes allocated (eboot.bin 12.7 MB, 5 .nam tables, SHA-256 of the eboot)");
            Assert.True(source.Build.IsAnalysedBuild);
            Assert.Equal(14_744, source.Catalog.Images.Count);
            Assert.Equal(134, source.Tables.Movies.Count);
            Assert.True(allocated < 128L * 1024 * 1024, $"opening allocated {allocated:N0} bytes");

            // the first thing that needs an archive says which file is missing
            FileNotFoundException scripts = Assert.Throws<FileNotFoundException>(() => source.ScriptCount);
            Assert.Contains("allscr.mrg", scripts.Message, StringComparison.OrdinalIgnoreCase);
            FileNotFoundException picture = Assert.Throws<FileNotFoundException>(() => source.Picture(0, 0));
            Assert.Contains("allpac0.hed", picture.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Throws<FileNotFoundException>(() => source.TextLineCount);
            Assert.Throws<FileNotFoundException>(() => source.FontPage(Language.En, 0));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [GameDataFact]
    public void AMissingFolderOrEbootIsAnError()
    {
        Assert.Throws<DirectoryNotFoundException>(() => GameFolderSource.Open(Path.Combine(Path.GetTempPath(), "tsuki-k1a-no-such-folder")));
        string dir = Path.Combine(Path.GetTempPath(), "tsuki-k1a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Throws<FileNotFoundException>(() => GameFolderSource.Open(dir));
            File.WriteAllBytes(Path.Combine(dir, "eboot.bin"), new byte[64]);
            Assert.ThrowsAny<Exception>(() => GameFolderSource.Open(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [GameDataFact]
    public void ArgumentsTheGameDoesNotHaveAreRefusedWithTheRange()
    {
        GameFolderSource s = Folder;
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Script(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Script(549));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.TextLine(Language.Ja, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.TextLine(Language.Ja, 43_969));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.TextLine((Language)9, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.FontPage(Language.Ja, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.FontPage(Language.Ja, 65));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.FontPage((Language)9, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Picture(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Picture(6, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Picture(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Picture(0, s.Archive("allpac0").Count));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Audio((AudioBank)9, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Audio(AudioBank.Bgm, 132));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Audio(AudioBank.Bgm, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Movie(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Movie(134));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.UiSheet(UiSet.ConfParts, Language.En, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.UiSheet(UiSet.ConfParts, Language.En, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.UiSheet((UiSet)99, Language.En, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.UiSheet(UiSet.ConfParts, (Language)9, 0));
    }

    [GameDataFact]
    public void KoreanTextAndFontsAreReadEvenThoughTheGameNeverSelectsThem()
    {
        // text.md 3: ko is placeholder data; the data layer still serves it
        GameFolderSource s = Folder;
        Assert.Equal(43_969, s.Text.Table(Language.Ko).Count);
        Assert.Contains(Enumerable.Range(0, 400), id => s.TextLine(Language.Ko, id).Length > 0);
        Assert.Equal(65, Enumerable.Range(0, 65).Count(p => s.FontPage(Language.Ko, p).Width > 0));
    }

    [GameDataFact]
    public void EveryTextLineOfEveryLanguageIsTheEnginesCopyOfItsBlobBytes()
    {
        // text.md 4 (VA 0x75F80): nothing when the stored string is shorter than 3 bytes, else its first min(len, 0x301) bytes less 2
        GameFolderSource s = Folder;
        var bad = new Mismatches();
        long lines = 0;
        long capped = 0;
        long empty = 0;
        foreach (Language lang in Enum.GetValues<Language>())
        {
            var table = s.Text.Table(lang);
            for (int id = 0; id < s.TextLineCount; id++)
            {
                ReadOnlySpan<byte> raw = table.Raw(id).Span;
                ReadOnlySpan<byte> expected = raw.Length < 3 ? default : raw[..(Math.Min(raw.Length, 0x301) - 2)];
                if (!expected.SequenceEqual(s.TextLine(lang, id).Span))
                {
                    bad.Add($"{lang} line {id}");
                }

                lines++;
                capped += raw.Length > 0x301 ? 1 : 0;
                empty += raw.Length < 3 ? 1 : 0;
            }
        }

        output.WriteLine($"{lines} lines compared; {capped} are cut at 0x2FF bytes, {empty} are empty");
        bad.AssertNone("TextLine against the blob bytes");
        Assert.Equal(5L * 43_969, lines);

        // no retail line reaches the 0x301-byte cap, so the cap itself is checked on synthetic data (ScriptTextTests); 9 stored strings are under 3 bytes
        Assert.Equal(0, capped);
        Assert.Equal(9, empty);
    }

    [GameDataFact]
    public void AudioNeedsADecoderButTheRiffBytesDoNot()
    {
        using GameFolderSource s = GameFolderSource.Open(GameFolder.Require());
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => s.Audio(AudioBank.Bgm, 33));
        Assert.Contains("IAudioDecoder", e.Message, StringComparison.Ordinal);
        Assert.True(s.AudioPayload(AudioBank.Bgm, 33).Span[..4].SequenceEqual("RIFF"u8));
        Assert.True(s.AudioPayload(AudioBank.Se, 598).Span[..4].SequenceEqual("RIFF"u8));
        Assert.True(s.AudioPayload(AudioBank.Voice, 15_616).Span[..4].SequenceEqual("RIFF"u8), "a voice entry is unwrapped from its LV chunk and MZX0 stream");
        Assert.True(s.AudioPayload(AudioBank.Vag, 0).Span[..4].SequenceEqual("VAGp"u8));
        Assert.Equal(132, s.AudioCount(AudioBank.Bgm));
        Assert.Equal(1_327, s.AudioCount(AudioBank.Se));
        Assert.Equal(16_464, s.AudioCount(AudioBank.Voice));
        Assert.Equal(2, s.AudioCount(AudioBank.Voice2));
        Assert.Equal(19, s.AudioCount(AudioBank.Vag));
        Assert.Equal(1_327, s.SoundEffectNames.VseCount);
    }

    [GameDataFact]
    public void EveryUiSheetOfEveryLanguageIsFoundAndEveryChildIsAnImage()
    {
        // archives_ui_movies.md 5.1-5.2: 71 sheet archives (13 sets x 5 languages + 6 language-free sets), 3,699 children, all images
        GameFolderSource s = Folder;
        var files = new HashSet<string>(StringComparer.Ordinal);
        long children = 0;
        foreach (UiSet set in AllSets)
        {
            foreach (Language lang in Enum.GetValues<Language>())
            {
                string file = UiSheetNames.FileName(set, lang);
                int count = s.UiSheetChildCount(set, lang);
                Assert.True(count > 0, $"{file} has no children");
                if (!files.Add(file))
                {
                    Assert.False(UiSheetNames.HasLanguageVariants(set), $"{file} is named twice by a set with language variants");
                    continue;
                }

                children += count;
                for (int c = 0; c < count; c++)
                {
                    ImageMzp image = s.OpenUiSheetImage(set, lang, c);
                    Assert.True(image.Header.DisplayWidth > 0 && image.Header.DisplayHeight > 0, $"{file} child {c}");
                }
            }
        }

        output.WriteLine($"{files.Count} sheet archives, {children} children");
        Assert.Equal(71, files.Count);
        Assert.Equal(3_699, children);
    }

    [GameDataFact]
    public void ASheetChildIsTheSameImageAsADirectDecodeOfThatSection()
    {
        GameFolderSource s = Folder;
        ImageId id = s.Catalog.NameIndex.Find("conf_parts_en.mrg")!.Value;
        Assert.Equal(ImageArchive.Allpac3, id.Archive);
        Assert.Equal(1, id.Index); // CONF_PARTS_EN.MRG is allpac3 #1 (archives_ui_movies.md 5.1)
        var direct = Tsuki.Data.Containers.MzpContainer.Parse(s.Archive("allpac3").Memory(id.Index));
        Assert.Equal(20, direct.Count);
        foreach (int child in new[] { 0, 4, 9, 19 })
        {
            Picture want = ImageMzp.Open(direct.Child(child)).ToPicture();
            Picture got = s.UiSheet(UiSet.ConfParts, Language.En, child);
            Assert.Equal((want.Width, want.Height), (got.Width, got.Height));
            Assert.Equal(want.Tiles.Length, got.Tiles.Length);
            for (int t = 0; t < want.Tiles.Length; t++)
            {
                Assert.True(want.Tiles[t].Rgba.Span.SequenceEqual(got.Tiles[t].Rgba.Span), $"child {child} tile {t}");
            }
        }

        // the sizes of archives_ui_movies.md 5.3: child 0 is 608x272 in Japanese and English, child 4 is 760x696 in Japanese and 1336x696 in English
        Assert.Equal((608, 272), SizeOf(s, Language.Ja, 0));
        Assert.Equal((608, 272), SizeOf(s, Language.En, 0));
        Assert.Equal((760, 696), SizeOf(s, Language.Ja, 4));
        Assert.Equal((1336, 696), SizeOf(s, Language.En, 4));
        Assert.Equal(19, s.UiSheetChildCount(UiSet.ConfParts, Language.Ko));
        Assert.Equal(20, s.UiSheetChildCount(UiSet.ConfParts, Language.Ja));
    }

    private static (int, int) SizeOf(GameFolderSource s, Language lang, int child)
    {
        ImageHeader h = s.OpenUiSheetImage(UiSet.ConfParts, lang, child).Header;
        return (h.DisplayWidth, h.DisplayHeight);
    }

    [GameDataFact]
    public void EveryRegistrationRecordOfTheEbootNamesASheetThatExistsAndAChildThatIs()
    {
        // 21 tables, 220 records (archives_ui_movies.md 7.2, 7.3). The documented exceptions: extra_parts.mrg (15 records, referenced by
        // the menu state machine, not on disk: find returns -1), the five dead ed1_parts.mrg tables (1 record each), and the selecttxt
        // record whose child the engine rewrites at run time (child -1 in the file).
        GameFolderSource s = Folder;
        Assert.Equal(21, s.Catalog.UiTables.Count);
        int records = 0;
        int missingExtra = 0;
        int missingEd1 = 0;
        int rewritten = 0;
        int clamped = 0;
        int localised = 0;
        int plain = 0;
        foreach (UiTexTable table in s.Catalog.UiTables)
        {
            foreach (UiTexRecord rec in table.Records)
            {
                records++;
                if (rec.Name == "extra_parts.mrg" || rec.Name == "ed1_parts.mrg")
                {
                    foreach (Language lang in SelectableLanguages)
                    {
                        Assert.Null(s.Catalog.NameIndex.Find(UiTextureTables.LocalizePartsName(rec.Name, (int)lang)));
                    }

                    Assert.Null(s.Catalog.NameIndex.Find(rec.Name));
                    if (rec.Name == "extra_parts.mrg")
                    {
                        missingExtra++;
                    }
                    else
                    {
                        missingEd1++;
                    }

                    continue;
                }

                if (rec.Child < 0)
                {
                    Assert.Equal("selecttxt_parts.mrg", rec.Name);
                    rewritten++;
                    continue;
                }

                bool isLocalised = false;
                foreach (Language lang in SelectableLanguages)
                {
                    string file = UiTextureTables.LocalizePartsName(rec.Name, (int)lang);
                    ImageId? id = s.Catalog.NameIndex.Find(file);
                    if (id is null)
                    {
                        id = s.Catalog.NameIndex.Find(rec.Name);
                        Assert.True(id is not null, $"{rec.Name} ({file}) is in no image archive");
                    }
                    else if (lang == Language.Ja)
                    {
                        isLocalised = true;
                    }

                    Assert.Equal(ImageArchive.Allpac3, id.Value.Archive);
                    int count = Tsuki.Data.Containers.MzpContainer.Parse(s.Archive("allpac3").Memory(id.Value.Index)).Count;
                    int child = UiTextureTables.ChildIndex(rec.Child, count);
                    Assert.InRange(child, 0, count - 1);
                    if (child != rec.Child && lang == Language.Ja)
                    {
                        clamped++;
                    }
                }

                if (isLocalised)
                {
                    localised++;
                }
                else
                {
                    plain++;
                }
            }
        }

        output.WriteLine(
            $"{records} records: {localised} localised, {plain} neutral, {clamped} clamped, {rewritten} rewritten at run time, {missingExtra} extra_parts and {missingEd1} ed1_parts without an archive");
        Assert.Equal(220, records);
        Assert.Equal(15, missingExtra);
        Assert.Equal(5, missingEd1);
        Assert.Equal(1, rewritten);
        Assert.Equal(1, clamped); // selecttxt record 0: child 89 of 83
        Assert.Equal(records - 15 - 5 - 1, localised + plain);
    }

    [GameDataFact]
    public void SeveralThreadsGetTheSameAnswersAsOne()
    {
        GameFolderSource s = Folder;
        string Digest(int seed)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var rng = new Random(seed);
            for (int n = 0; n < 24; n++)
            {
                int k = rng.Next(549);
                sha.AppendData(s.Script(k).Bytes.Span);
                sha.AppendData(s.TextLine(SelectableLanguages[rng.Next(4)], rng.Next(43_969)).Span);
                sha.AppendData(s.FontPage(SelectableLanguages[rng.Next(4)], rng.Next(65)).Rgba.Span[..4096]);
                Picture p = s.UiSheet(UiSet.TitleParts, SelectableLanguages[rng.Next(4)], rng.Next(15));
                sha.AppendData(BitConverter.GetBytes(p.Width * 65536L + p.Height));
                sha.AppendData(p.Tiles[0].Rgba.Span[..Math.Min(4096, p.Tiles[0].Rgba.Length)]);
                sha.AppendData(s.AudioPayload(AudioBank.Voice, rng.Next(16_464)).Span[..4]);
                AudioClip clip = s.Audio(AudioBank.Se, rng.Next(1_327));
                sha.AppendData(BitConverter.GetBytes(clip.FrameCount));
            }

            return Convert.ToHexStringLower(sha.GetHashAndReset());
        }

        int[] seeds = [.. Enumerable.Range(1, 12)];
        string[] one = [.. seeds.Select(Digest)];
        string[] many = new string[seeds.Length];
        Parallel.For(0, seeds.Length, new ParallelOptions { MaxDegreeOfParallelism = 12 }, i => many[i] = Digest(seeds[i]));
        Assert.Equal(one, many);
    }

    [GameDataFact]
    public void AFreshSourceCanBeDisposedAndRefusesFurtherUse()
    {
        GameFolderSource s = GameFolderSource.Open(GameFolder.Require());
        Assert.Equal(549, s.ScriptCount);
        s.OpenImage(0, 0);
        s.Dispose();
        s.Dispose();
        Assert.Throws<ObjectDisposedException>(() => s.Script(0));
        Assert.Throws<ObjectDisposedException>(() => s.Picture(0, 0));
        Assert.Throws<ObjectDisposedException>(() => s.TextLine(Language.Ja, 0));
    }
}

/// <summary>The memory a source needs while it reads the 24 GB archive; alone in the process like <see cref="GiantArchiveTests"/>.</summary>
[Collection(SerialMemoryCollection.Name)]
public sealed class GameFolderSourceMemoryTests
{
    [GameDataFact]
    public void ReadingFromTheGiantArchiveThroughTheSourceLoadsOnlyTheEntries()
    {
        using GameFolderSource s = GameFolderSource.Open(GameFolder.Require());
        GC.Collect();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long privateBefore = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
        int total = s.Archive("allpac1").Count;
        long pixels = 0;
        for (int i = 0; i < total; i += 41)
        {
            pixels += s.OpenImage(1, i).Header.Width;
        }

        // decode a few mid-sized ones completely: the cost is the picture, not the archive
        int decoded = 0;
        for (int i = 3; i < total && decoded < 6; i += 997)
        {
            Picture p = s.Picture(1, i);
            pixels += p.Width;
            decoded++;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long privateGrowth = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 - privateBefore;
        Assert.True(pixels > 0);
        Assert.True(allocated < 1024L * 1024 * 1024, $"managed allocation: {allocated:N0} bytes");
        Assert.True(privateGrowth < 1536L * 1024 * 1024, $"private memory growth: {privateGrowth:N0} bytes");
    }
}

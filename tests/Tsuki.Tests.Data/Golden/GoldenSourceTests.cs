#if TSUKI_DEV
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using Tsuki.Data.Golden;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Data.Golden;

/// <summary>
/// <see cref="GoldenSource"/> through <see cref="IGameData"/> (the inherited consumer tests), plus what only the golden source
/// promises: what the slice holds, the exceptions for everything it does not hold, the typed eboot tables, thread safety and
/// whole-file verification.
/// </summary>
public sealed class GoldenSourceTests : SliceConsumerTests
{
    private readonly ITestOutputHelper _output;

    public GoldenSourceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    protected override IGameData Source => SliceExpect.Source;

    [GameDataFact]
    public void TheSourceDescribesTheSliceFolderAndTheExportedBuild()
    {
        GoldenSource source = SliceExpect.Source;
        Assert.Equal(SliceExpect.Dir, source.Build.Source);
        Assert.Equal(SliceExpect.Dir, source.Slice.Directory);
        Assert.Same(source.Tables, source.Eboot);
        Assert.NotNull(source.GameDirectory);
        Assert.Equal(GameFolder.Path, source.GameDirectory);
    }

    [GameDataFact]
    public void ContentsListWhatTheManifestHolds()
    {
        GoldenSliceContents c = SliceExpect.Source.Contents;
        // Everything is compared with the manifest's own file list and counters, so a regenerated or extended slice needs no edit here;
        // what the other tests rely on by name (scripts 2 and 3, the ja and en text and fonts, the pictures and clips below) is pinned
        // as "contains".
        Assert.Equal(SliceExpect.ScriptIndices(), c.Scripts.ToArray());
        Assert.Contains(2, c.Scripts);
        Assert.Contains(3, c.Scripts);
        Assert.Equal(SliceExpect.FontLanguages(), c.FontLanguages.Order().ToArray());
        Assert.Contains(Language.Ja, c.FontLanguages);
        Assert.Contains(Language.En, c.FontLanguages);

        using (JsonDocument text = SliceExpect.Json("text/lines.json"))
        {
            JsonProperty[] languages = [.. text.RootElement.GetProperty("languages").EnumerateObject()];
            Assert.Equal(languages.Select(l => SliceExpect.LanguageOf(l.Name)).Order().ToArray(), SliceExpect.Source.TextLanguages.ToArray());
            Assert.Contains(Language.Ja, SliceExpect.Source.TextLanguages);
            Assert.Contains(Language.En, SliceExpect.Source.TextLanguages);
            foreach (JsonProperty l in languages)
            {
                int[] ids = [.. l.Value.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("id").GetInt32()).Order()];
                Assert.Equal(SliceExpect.Count("text.messages"), ids.Length);
                Assert.Equal(ids, SliceExpect.Source.TextIds.Order().ToArray());
            }
        }

        foreach (int id in Enumerable.Range(0, 8))
        {
            Assert.Contains(id, SliceExpect.Source.TextIds);   // lines 0..7 are what text.md 2.3 and the layouts quote
        }

        var fromManifest = SliceExpect.PictureFiles().Select(p => new GoldenPictureKey(p.Archive, p.Entry)).ToArray();
        Assert.Equal(fromManifest, c.Pictures);
        Assert.Equal(SliceExpect.Count("pictures.pictures"), c.Pictures.Length);
        foreach ((int archive, int entry) in new[] { (0, 166), (0, 270), (0, 358), (0, 920), (4, 71), (4, 72), (4, 73), (4, 74) })
        {
            Assert.Contains(new GoldenPictureKey(archive, entry), c.Pictures);
        }

        Assert.Contains(new GoldenAudioKey(AudioBank.Bgm, 33), c.Audio);
        Assert.Contains(new GoldenAudioKey(AudioBank.Se, 598), c.Audio);
        for (int voice = 0x3D00; voice <= 0x3D08; voice++)
        {
            Assert.Contains(new GoldenAudioKey(AudioBank.Voice, voice), c.Audio);
        }

        Assert.Equal(SliceExpect.Count("audio.voices"), c.Audio.Count(a => a.Bank == AudioBank.Voice));
        Assert.Equal(SliceExpect.Count("audio.ses"), c.Audio.Count(a => a.Bank == AudioBank.Se));
        Assert.Equal(SliceExpect.Count("audio.bgm"), c.Audio.Count(a => a.Bank == AudioBank.Bgm));
        Assert.Equal(SliceExpect.Count("audio.ses") + SliceExpect.Count("audio.bgm") + SliceExpect.Count("audio.voices"), c.Audio.Length);
    }

    [GameDataFact]
    public void RawTextLinesAreTheBlobBytesWithTheirCrLf()
    {
        using JsonDocument doc = SliceExpect.Json("text/lines.json");
        var mismatches = new Mismatches();
        foreach (JsonProperty language in doc.RootElement.GetProperty("languages").EnumerateObject())
        {
            Language lang = language.Name == "ja" ? Language.Ja : Language.En;
            foreach (JsonElement line in language.Value.GetProperty("lines").EnumerateArray())
            {
                int id = line.GetProperty("id").GetInt32();
                ReadOnlyMemory<byte> raw = SliceExpect.Source.RawTextLine(lang, id);
                mismatches.Equal($"{language.Name} line {id} raw SHA-1", line.GetProperty("raw_sha1").GetString()!, SliceExpect.Sha1(raw.Span));
                mismatches.Equal($"{language.Name} line {id} raw length", line.GetProperty("next_offset").GetInt32() - line.GetProperty("offset").GetInt32(), raw.Length);
                mismatches.Equal($"{language.Name} line {id} ends in CR LF", true, raw.Span.EndsWith("\r\n"u8));
                ReadOnlySpan<byte> engine = SliceExpect.Source.TextLine(lang, id).Span;
                mismatches.Equal($"{language.Name} line {id} engine copy is the raw line without CR LF", true, raw.Span[..^2].SequenceEqual(engine));
            }
        }

        mismatches.AssertNone("raw text lines");
    }

    [GameDataFact]
    public void WhatTheSliceDoesNotHoldThrowsGoldenSliceMissing()
    {
        GoldenSource s = SliceExpect.Source;
        Assert.IsAssignableFrom<KeyNotFoundException>(Assert.Throws<GoldenSliceMissingException>(() => s.Script(0)));
        Assert.Throws<GoldenSliceMissingException>(() => s.Script(1));
        Assert.Throws<GoldenSliceMissingException>(() => s.Script(548));
        Assert.Throws<GoldenSliceMissingException>(() => s.TextLine(Language.Zc, 0));
        Assert.Throws<GoldenSliceMissingException>(() => s.TextLine(Language.Ko, 0));
        Assert.Throws<GoldenSliceMissingException>(() => s.TextLine(Language.Ja, 8));
        Assert.Throws<GoldenSliceMissingException>(() => s.TextLine(Language.En, 43_968));
        Assert.Throws<GoldenSliceMissingException>(() => s.RawTextLine(Language.Ja, 100));
        Assert.Throws<GoldenSliceMissingException>(() => s.FontPage(Language.Zc, 1));
        Assert.Throws<GoldenSliceMissingException>(() => s.Picture(0, 1));
        Assert.Throws<GoldenSliceMissingException>(() => s.Picture(5, 0));
        Assert.Throws<GoldenSliceMissingException>(() => s.Audio(AudioBank.Bgm, 34));
        Assert.Throws<GoldenSliceMissingException>(() => s.Audio(AudioBank.Vag, 0));
        Assert.Throws<GoldenSliceMissingException>(() => s.Audio(AudioBank.Voice, 0x3D09));
        var e = Assert.Throws<GoldenSliceMissingException>(() => s.UiSheet(UiSet.TitleParts, Language.Ja, 0));
        Assert.Contains("TitleParts", e.What, StringComparison.Ordinal);
        Assert.Throws<GoldenSliceMissingException>(() => s.Tables.Table("nonexistent"));
    }

    [GameDataFact]
    public void ArgumentsOutsideTheGamesRangesAreArgumentErrors()
    {
        GoldenSource s = SliceExpect.Source;
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Script(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Script(549));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.TextLine(Language.Ja, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.TextLine(Language.Ja, 43_969));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.TextLine((Language)9, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.FontPage(Language.Ja, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.FontPage(Language.Ja, 65));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Picture(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Picture(6, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Picture(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Audio((AudioBank)9, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Audio(AudioBank.Bgm, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Movie(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Movie(134));
    }

    [GameDataFact]
    public void ReturnedBuffersAreFreshSoACallerMayNotCorruptTheSource()
    {
        GoldenSource s = SliceExpect.Source;
        Picture a = s.Picture(0, 358);
        Picture b = s.Picture(0, 358);
        Assert.NotSame(a, b);
        Assert.False(a.Tiles[0].Rgba.Equals(b.Tiles[0].Rgba), "two reads share a tile buffer");
        Assert.True(a.Tiles[0].Rgba.Span.SequenceEqual(b.Tiles[0].Rgba.Span));

        FontPage p = s.FontPage(Language.En, 1);
        FontPage q = s.FontPage(Language.En, 1);
        Assert.False(p.Rgba.Equals(q.Rgba), "two reads share a font page buffer");

        // the script bytes and text lines are cached: they are the same object each time and read-only by type
        Assert.Same(s.Script(2), s.Script(2));
        Assert.True(s.TextLine(Language.Ja, 0).Equals(s.TextLine(Language.Ja, 0)));
    }

    [GameDataFact]
    public async Task SeveralWorkersReadTheSourceAtOnce()
    {
        // ARCHITECTURE 3.4: decode workers call IGameData from several threads.
        GoldenSource s = SliceExpect.Source;
        string expectedPicture = SliceExpect.Sha1(s.Picture(0, 358).Tiles[0].Rgba.Span);
        string expectedVoice = SliceExpect.Sha1(s.Audio(AudioBank.Voice, 0x3D00).Samples.Span);
        string expectedPage = SliceExpect.Sha1(s.FontPage(Language.Ja, 1).Rgba.Span);
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Task[] tasks = Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 6; i++)
            {
                switch ((worker + i) % 6)
                {
                    case 0 when SliceExpect.Sha1(s.Picture(0, 358).Tiles[0].Rgba.Span) != expectedPicture:
                        failures.Add($"worker {worker}: picture differs");
                        break;
                    case 1 when SliceExpect.Sha1(s.Audio(AudioBank.Voice, 0x3D00).Samples.Span) != expectedVoice:
                        failures.Add($"worker {worker}: voice differs");
                        break;
                    case 2 when SliceExpect.Sha1(s.FontPage(Language.Ja, 1).Rgba.Span) != expectedPage:
                        failures.Add($"worker {worker}: font page differs");
                        break;
                    case 3 when s.Script(3).SegmentStarts.Length != 2130:
                        failures.Add($"worker {worker}: script 3 differs");
                        break;
                    case 4 when s.TextLine(Language.En, 3).Length == 0:
                        failures.Add($"worker {worker}: text line empty");
                        break;
                    case 5 when s.Tables.SceneIds.Length != 549:
                        failures.Add($"worker {worker}: scene ids differ");
                        break;
                    default:
                        break;
                }
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.Empty(failures);
    }

    [GameDataFact]
    public void WholeFileVerificationAcceptsTheFilesTheSourceReads()
    {
        using GoldenSource verified = GoldenSource.Open(SliceExpect.Dir, GameFolder.Path, GoldenVerifyMode.Sha1);
        Assert.Equal(2130, verified.Script(3).SegmentStarts.Length);
        Assert.Equal(SliceExpect.Count("text.messages"), verified.TextIds.Length);
        Assert.NotEmpty(verified.TextLine(Language.Ja, 0).ToArray());
        Assert.Equal(1024, verified.FontPage(Language.En, 1).Width);
        Assert.Equal(512, verified.Picture(0, 358).Tiles[0].StoredWidth);
        Assert.Equal(105_536, verified.Audio(AudioBank.Voice, 0x3D00).FrameCount);
        Assert.Equal(549, verified.Tables.SceneIds.Length);
    }

    [GameDataFact]
    public void ADisposedSourceRefusesFurtherCalls()
    {
        GoldenSource s = GoldenSource.Open(SliceExpect.Dir, GameFolder.Path);
        Assert.Equal(2, s.Script(2).Index);
        s.Dispose();
        s.Dispose();
        Assert.Throws<ObjectDisposedException>(() => s.Script(2));
        Assert.Throws<ObjectDisposedException>(() => s.Picture(0, 358));
        Assert.Throws<ObjectDisposedException>(() => s.TextLine(Language.Ja, 0));
        Assert.Throws<ObjectDisposedException>(() => s.Audio(AudioBank.Voice, 0x3D00));
    }

    [GameDataFact]
    public void TheSliceIsFoundAboveTheTestBinaryOrThroughTheGoldenRoot()
    {
        string slice = SliceExpect.Dir;
        string root = Path.GetDirectoryName(slice)!;
        Assert.Equal(slice, GoldenSlice.FindDirectory());                            // walks up from the binary to samples/golden/slice
        Assert.Equal(slice, GoldenSlice.FindDirectory(null, root));                  // %TSUKI_GOLDEN_DIR% = the folder that holds slice
        Assert.Equal(slice, GoldenSlice.FindDirectory(slice, goldenRoot: null));
        Assert.Null(GoldenSlice.FindDirectory(null, Path.Combine(root, "no-such-area")));
        Assert.Null(GoldenSlice.FindDirectory(Path.Combine(root, "no-such-area"), root));   // a hint without a manifest does not fall through
        if (File.Exists(Path.Combine(root, "image", "manifest.json")))
        {
            // another area is found by its manifest, and Open refuses it by the area name inside
            var wrongArea = Assert.Throws<GoldenFormatException>(() => GoldenSlice.Open(Path.Combine(root, "image")));
            Assert.Contains("area \"image\"", wrongArea.Message, StringComparison.Ordinal);
        }

        using GoldenSource found = GoldenSource.Open();
        Assert.Equal(slice, found.Slice.Directory);
        Assert.Equal(GoldenData.RepoRoot, found.Slice.RepositoryRoot);
    }

    [GameDataFact]
    public void TypedEbootRecordsStayValidAfterTheSourceIsDisposed()
    {
        using GoldenSource s = GoldenSource.Open(SliceExpect.Dir, GameFolder.Path);
        GoldenCatalogRecord catalog = s.Tables.CatalogRecords[0];
        GoldenTextboxTable scales = s.Tables.Textbox["scales"];
        s.Dispose();
        Assert.True(catalog.Record.GetProperty("width").GetInt32() > 0);
        Assert.Equal(10, scales.Values!.Value.GetArrayLength());
    }

    [GameDataFact]
    public void ReportsWhatTheMainCallsCost()
    {
        // numbers for the consumers (Debug build of Tsuki.Data, warm file cache): printed, not asserted, except for the sanity bounds
        var sw = new System.Diagnostics.Stopwatch();
        double Time(Action action)
        {
            sw.Restart();
            action();
            return sw.Elapsed.TotalMilliseconds;
        }

        GoldenSource? s = null;
        double open = Time(() => s = GoldenSource.Open(SliceExpect.Dir, GameFolder.Path));
        using GoldenSource source = s!;
        double script = Time(() => source.Script(3));
        double text = Time(() => source.TextLine(Language.En, 3));
        double scenes = Time(() => _ = source.Tables.SceneIds);
        double fonts = Time(() => _ = source.Tables.FontTables);
        double page0 = Time(() => source.FontPage(Language.Ja, 1));
        double page = Time(() =>
        {
            for (int i = 0; i < 10; i++)
            {
                source.FontPage(Language.Ja, 2 + i);
            }
        }) / 10;
        double small = Time(() => source.Picture(0, 358));
        double big = Time(() => source.Picture(0, 270));
        double biggest = Time(() => source.Picture(0, 920));
        double voice = Time(() => source.Audio(AudioBank.Voice, 0x3D00));
        double bgm = Time(() => source.Audio(AudioBank.Bgm, 33));
        _output.WriteLine($"Open (manifest of 87 files): {open:F1} ms");
        _output.WriteLine($"Script(3) first call (2,130 statements): {script:F1} ms; TextLine first call: {text:F2} ms; scene ids: {scenes:F1} ms; glyph tables (1 MB JSON): {fonts:F1} ms");
        _output.WriteLine($"FontPage 1024 x 1024: {page0:F1} ms first, {page:F1} ms mean of the next 10");
        _output.WriteLine($"Picture 4 tiles ({4 * 512 * 256 * 4 / 1024} KiB): {small:F1} ms; 25 tiles (12.5 MiB): {big:F1} ms; 105 tiles (52.5 MiB): {biggest:F1} ms");
        _output.WriteLine($"Audio voice 105,536 frames: {voice:F1} ms; bgm 33, 8,258,048 stereo frames (63 MiB): {bgm:F1} ms");
        Assert.True(open < 5000 && biggest < 5000 && bgm < 5000, "a call took over five seconds");
    }

    [GameDataFact]
    public void AMissingSliceFolderNamesTheCommandThatCreatesIt()
    {
        string nowhere = Path.Combine(Path.GetTempPath(), "tsuki-no-such-golden-slice-" + Guid.NewGuid().ToString("N"));
        var e = Assert.Throws<DirectoryNotFoundException>(() => GoldenSource.Open(nowhere));
        Assert.Contains("oracle.run slice", e.Message, StringComparison.Ordinal);
    }

    [GameDataFact]
    public void MoviePathsNeedAGameFolder()
    {
        string noGame = Path.Combine(Path.GetTempPath(), "tsuki-no-such-game-" + Guid.NewGuid().ToString("N"));
        using var withoutGame = new GoldenSource(GoldenSlice.Open(SliceExpect.Dir), noGame, ownsSlice: true);
        // an explicit folder without eboot.bin is ignored; the fallbacks (TSUKI_GAME_DIR, the exporter's folder) may still find one
        Assert.NotEqual(Path.GetFullPath(noGame), withoutGame.GameDirectory);

        // the table path itself never needs the folder
        Assert.Equal("/app0/image/movie/prologue.mp4", withoutGame.Tables.Movie(4).Path);
    }

    // -- the eboot tables ---------------------------------------------------------------------------------------------

    [GameDataFact]
    public void EbootTableNamesAreTheExportedFiles()
    {
        GoldenEbootTables t = SliceExpect.Source.Tables;
        Assert.Equal(["build", "catalog", "compositing", "config", "font_tables", "motions", "movies", "scenes", "textbox_tables"], t.TableNames);
        foreach (string name in t.TableNames)
        {
            using JsonDocument expected = SliceExpect.Json($"eboot/{name}.json");
            Assert.Equal(expected.RootElement.GetRawText(), t.Table(name).GetRawText());
        }
    }

    [GameDataFact]
    public void SceneIdsMatchTheEbootAndTheScriptsOfTheSlice()
    {
        ImmutableArray<int> ids = SliceExpect.Source.Tables.SceneIds;
        Assert.Equal(549, ids.Length);
        Assert.Equal([110, 240, 260, 270], ids.Take(4).ToArray());   // RR 3.3 probe scene_ids_start
        foreach (int k in new[] { 2, 3 })
        {
            using JsonDocument doc = SliceExpect.Json($"scripts/script_{k:D4}.json");
            Assert.Equal(doc.RootElement.GetProperty("scene_id").GetInt32(), ids[k]);
        }

        using JsonDocument names = SliceExpect.Json("scripts/allscr_names.json");
        Assert.Equal(names.RootElement.GetProperty("scene_ids").EnumerateArray().Select(e => e.GetInt32()).ToArray(), ids.ToArray());
    }

    [GameDataFact]
    public void TheMovieTableHas134RecordsAndTheFourPrologueIds()
    {
        GoldenEbootTables t = SliceExpect.Source.Tables;
        Assert.Equal(134, t.Movies.Length);
        Assert.Equal([4, 46, 47, 106], t.PrologueMovieIds);
        Assert.Equal("/app0/image/movie/titlebg1.mp4", t.Movie(0).Path);
        Assert.Equal("/app0/image/movie/prologue.mp4", t.Movie(4).Path);
        Assert.EndsWith("prologue_en.mp4", t.Movie(46).Path, StringComparison.Ordinal);
        Assert.EndsWith("prologue_zc.mp4", t.Movie(47).Path, StringComparison.Ordinal);
        Assert.EndsWith("prologue_zt.mp4", t.Movie(106).Path, StringComparison.Ordinal);
        for (int i = 0; i < t.Movies.Length; i++)
        {
            Assert.Equal(i, t.Movies[i].Index);
            Assert.Equal(8, t.Movies[i].Raw.Length);
        }

        // 134 files exist (counts: movie_files_present) and the slice records the four prologue files
        using JsonDocument doc = SliceExpect.Json("eboot/movies.json");
        foreach (JsonElement f in doc.RootElement.GetProperty("files").EnumerateArray())
        {
            Assert.Equal(f.GetProperty("path").GetString(), t.Movie(f.GetProperty("index").GetInt32()).Path);
        }
    }

    [GameDataFact]
    public void CatalogRecordsLeadToTheArchiveEntriesThePicturesWereExportedFrom()
    {
        GoldenSource s = SliceExpect.Source;
        ImmutableArray<GoldenCatalogRecord> records = s.Tables.CatalogRecords;
        Assert.Equal(SliceExpect.Count("eboot.catalog_records_exported"), records.Length);

        // every record names an archive entry that the slice exports as a picture, and that picture's header names the same file
        foreach (GoldenCatalogRecord r in records)
        {
            Assert.NotNull(r.FileId);
            int archive = Array.IndexOf(SliceExpect.ArchiveNames, r.FileId.Archive);
            Assert.True(archive >= 0, $"{r.Key}: unknown archive {r.FileId.Archive}");
            Assert.True(s.Contents.HasPicture(archive, r.FileId.Index), $"{r.Key}: {r.FileId.Archive}#{r.FileId.Index} is not in the slice");
            using JsonDocument pic = SliceExpect.Json($"pictures/{r.FileId.Archive}_{r.FileId.Index:D5}.json");
            Assert.Equal(pic.RootElement.GetProperty("name").GetString(), r.File, ignoreCase: true);
            Assert.Equal(r.Name, r.Key);
            Assert.Equal(r.NameIndex, r.Record.GetProperty("index").GetInt32());
        }

        // RR 7.3: STCH names img1685 and img0700_06_06 resolve to catalog records 1692 and 5330
        Assert.Equal(1692, s.Tables.CatalogRecord("IMG1685")!.Record.GetProperty("index").GetInt32());
        Assert.Equal(5330, s.Tables.CatalogRecord("IMG0700_06_06")!.Record.GetProperty("index").GetInt32());
        Assert.Equal(270, s.Tables.CatalogRecord("IMG1685")!.FileId!.Index);
        Assert.Null(s.Tables.CatalogRecord("IMG9999"));

        // the 17 pictures of 00_02 plus BLACK of 00_01
        var scripts = SliceExpect.Json("scripts/script_0003.json");
        using (scripts)
        {
            var keys = scripts.RootElement.GetProperty("assets").GetProperty("pictures").EnumerateArray()
                .Select(p => p.GetProperty("key").GetString()!).ToHashSet(StringComparer.Ordinal);
            keys.Add("BLACK");
            Assert.Equal(keys.Order(StringComparer.Ordinal).ToArray(), records.Select(r => r.Key).Order(StringComparer.Ordinal).ToArray());
        }
    }

    [GameDataFact]
    public void MotionsAreTheFourTheSliceStarts()
    {
        ImmutableArray<GoldenMotion> motions = SliceExpect.Source.Tables.Motions;
        Assert.Equal([22523, 22524, 22525, 22526], motions.Select(m => m.Id).ToArray());
        Assert.Equal([1000, 3500, 14000, 70000], motions.Select(m => m.DurationMs).ToArray());
        Assert.Equal([2, 2, 3, 2], motions.Select(m => m.Keys.Length).ToArray());
        foreach (GoldenMotion m in motions)
        {
            Assert.Equal(m.EndKey - m.FirstKey, m.Keys.Length);
            Assert.All(m.Keys, k => Assert.Equal(27, k.Fields.Length));
            Assert.Equal(m.DurationMs, m.Keys[^1].Time);
            Assert.Equal(m.Keys.Select(k => k.Index).ToArray(), Enumerable.Range(m.FirstKey, m.Keys.Length).ToArray());
        }

        Assert.Same(motions[1], SliceExpect.Source.Tables.Motion(22524));
        Assert.Null(SliceExpect.Source.Tables.Motion(1));
    }

    [GameDataFact]
    public void GlyphTablesHold17423LookupEntriesAndSeventeenRectangleTables()
    {
        GoldenFontTables f = SliceExpect.Source.Tables.FontTables;
        Assert.Equal(16, f.Lookups.Length);
        Assert.Equal(17, f.Records.Length);
        Assert.Equal(17_423, f.Lookups.Sum(l => l.Keys.Length));   // counts: eboot.font_lookup_entries
        Assert.Equal(39, f.Tags.Length);
        Assert.Equal([59, 59, 59, 57, 59], f.WideBase);
        GoldenFontLookup narrow = f.Lookup("narrow");
        Assert.Equal(109, narrow.Keys.Length);
        Assert.Equal(32UL, narrow.Keys[0]);        // the space
        Assert.Equal(0, narrow.Indices[0]);
        Assert.Equal(narrow.Keys.OrderBy(k => k).ToArray(), narrow.Keys.ToArray());   // sorted, for the engine's binary search
        GoldenGlyphRecords ja = f.Record("ja_s0");
        Assert.Equal(104, ja.Entries.Length);
        Assert.Equal(new GoldenGlyphRect(34, 0, 0, 32, 73), ja.Entries[0]);
        Assert.Throws<KeyNotFoundException>(() => f.Lookup("nope"));

        // the same numbers the textbox stream's own loader reads from the file
        using JsonDocument doc = SliceExpect.Json("eboot/font_tables.json");
        Assert.Equal(doc.RootElement.GetProperty("lookups").GetArrayLength(), f.Lookups.Length);
        Assert.Equal(doc.RootElement.GetProperty("records").EnumerateArray().Sum(t => t.GetProperty("entries").GetArrayLength()), f.Records.Sum(r => r.Entries.Length));
    }

    [GameDataFact]
    public void ConfigDefaultsAre320BytesWhoseFieldsMatchTheirRawBytes()
    {
        GoldenConfigDefaults c = SliceExpect.Source.Tables.Config;
        Assert.Equal(0x140, c.Raw.Length);
        Assert.Equal(54, c.Fields.Length);
        int expectedOffset = 0;
        foreach (GoldenConfigField f in c.Fields)
        {
            Assert.Equal(expectedOffset, f.Offset);
            expectedOffset += f.Size;
            if (f.Value is { } value)
            {
                ReadOnlySpan<byte> raw = c.Raw.Span.Slice(f.Offset, f.Size);
                long actual = f.Size switch
                {
                    1 => raw[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(raw),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(raw),
                    8 => BinaryPrimitives.ReadInt64LittleEndian(raw),
                    _ => throw new InvalidOperationException($"field {f.Name} has size {f.Size} and a value"),
                };
                Assert.True(value == actual || (f.Size == 2 && (short)actual == value) || (f.Size == 4 && (int)actual == value), $"{f.Name}: value {value}, bytes {Convert.ToHexString(raw)}");
            }
        }

        Assert.Equal(0x140, expectedOffset);
        // M2 acceptance: the music volume default is 40 and "Play All Voices" is 100 (misc_adv_system_state.md)
        Assert.Equal(40, c.Fields.Single(f => f.Offset == 20).Value);
        Assert.Equal(100, c.Fields.Single(f => f.Offset == 22).Value);
    }

    [GameDataFact]
    public void TextboxTablesDecodeToTheirExportedValues()
    {
        ImmutableSortedDictionary<string, GoldenTextboxTable> tables = SliceExpect.Source.Tables.Textbox;
        Assert.Equal(11, tables.Count);
        int decoded = 0;
        foreach (GoldenTextboxTable t in tables.Values)
        {
            if (t.Format is null || t.Values is null)
            {
                Assert.Equal("layout_region", t.Name);
                continue;
            }

            var expected = new List<double>();
            Flatten(t.Values.Value, expected);
            var actual = new List<double>();
            int pos = 0;
            foreach ((int repeat, char kind) in ParseFormat(t.Format))
            {
                for (int i = 0; i < repeat; i++)
                {
                    ReadOnlySpan<byte> raw = t.Raw.Span;
                    switch (kind)
                    {
                        case 'f':
                            actual.Add(BinaryPrimitives.ReadSingleLittleEndian(raw[pos..]));
                            pos += 4;
                            break;
                        case 'i':
                            actual.Add(BinaryPrimitives.ReadInt32LittleEndian(raw[pos..]));
                            pos += 4;
                            break;
                        case 'H':
                            actual.Add(BinaryPrimitives.ReadUInt16LittleEndian(raw[pos..]));
                            pos += 2;
                            break;
                        case 'Q':
                            actual.Add(BinaryPrimitives.ReadUInt64LittleEndian(raw[pos..]));
                            pos += 8;
                            break;
                        default:
                            throw new InvalidOperationException($"{t.Name}: format character {kind}");
                    }
                }
            }

            Assert.Equal(t.Raw.Length, pos);
            if (t.Name == "window_sizes")
            {
                // "23f23f": two parallel arrays (widths, then heights), exported as 23 (width, height) pairs
                actual = [.. Enumerable.Range(0, 23).SelectMany(i => new[] { actual[i], actual[23 + i] })];
            }

            Assert.Equal(expected, actual);
            decoded++;
        }

        Assert.Equal(10, decoded);
        // Package B corrected tsuki.textbox's 1-ulp scale and window-size constants to the eboot
        // float32 values (followups.md), so every exported table now matches tsuki.textbox.
        Assert.Empty(tables.Values.Where(t => t.MatchesTsukiTextbox == false).Select(t => t.Name));
        float[] scales = tables["scales"].Floats();
        Assert.Equal(10, scales.Length);
        Assert.Equal(0.34375f, scales[0]);
        Assert.Equal(46, tables["window_sizes"].Floats().Length);
    }

    private static void Flatten(JsonElement e, List<double> into)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement x in e.EnumerateArray())
            {
                Flatten(x, into);
            }
        }
        else
        {
            into.Add(e.GetDouble());
        }
    }

    private static IEnumerable<(int Repeat, char Kind)> ParseFormat(string format)
    {
        int i = 0;
        while (i < format.Length)
        {
            int repeat = 0;
            bool any = false;
            while (i < format.Length && char.IsAsciiDigit(format[i]))
            {
                repeat = (repeat * 10) + (format[i] - '0');
                any = true;
                i++;
            }

            yield return (any ? repeat : 1, format[i++]);
        }
    }
}
#endif

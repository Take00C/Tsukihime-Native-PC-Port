#if TSUKI_DEV
using Tsuki.Data;
using Tsuki.Data.Golden;
using Tsuki.Engine.Contracts;
using Tsuki.Tests.Data.Golden;

namespace Tsuki.Tests.Data.Source;

/// <summary>
/// ARCHITECTURE 3.3: <c>GameFolderSource</c> (the C# decoders on the user's folder) and <c>GoldenSource</c> (the Python oracle's export)
/// "must agree". Every test here asks both through <see cref="IGameData"/> (or through the typed tables of each) for everything the golden
/// slice holds, and compares the answers value for value: script bytes and segment starts, text lines, font pages, picture tiles, audio
/// samples and loops, movie paths, and the eboot tables.
/// </summary>
public sealed class GameFolderSourceAgreesWithGoldenTests
{
    private static GoldenSource Golden => SliceExpect.Source;

    private static GameFolderSource Folder => SharedSource.Source;

    [GameDataFact]
    public void TheBuildIsTheAnalysedOneAndEqualsTheGoldenExportsIdentity()
    {
        Assert.True(Folder.Build.IsAnalysedBuild, "the user's eboot.bin is not the analysed build");
        Assert.Equal(Golden.Build.EbootSize, Folder.Build.EbootSize);
        Assert.Equal(Golden.Build.EbootSha256, Folder.Build.EbootSha256);
        Assert.Equal(Path.GetFullPath(GameFolder.Require()), Folder.Build.Source);
    }

    [GameDataFact]
    public void ScriptsAndTheirSegmentStartsAgree()
    {
        Assert.Equal(Golden.ScriptCount, Folder.ScriptCount);
        Assert.Equal(549, Folder.ScriptCount);
        Assert.NotEmpty(Golden.Contents.Scripts);
        foreach (int k in Golden.Contents.Scripts)
        {
            ScriptImage want = Golden.Script(k);
            ScriptImage got = Folder.Script(k);
            Assert.Equal(want.Index, got.Index);
            Assert.True(want.Bytes.Span.SequenceEqual(got.Bytes.Span), $"script {k}: the decoded bytes differ");
            Assert.Equal(want.SegmentStarts.ToArray(), got.SegmentStarts.ToArray());
        }
    }

    [GameDataFact]
    public void TextLinesAgreeInEveryLanguageAndIdTheSliceHolds()
    {
        Assert.Equal(Golden.TextLineCount, Folder.TextLineCount);
        Assert.Equal(43_969, Folder.TextLineCount);
        var bad = new Mismatches();
        foreach (Language lang in Golden.TextLanguages)
        {
            foreach (int id in Golden.TextIds)
            {
                if (!Golden.TextLine(lang, id).Span.SequenceEqual(Folder.TextLine(lang, id).Span))
                {
                    bad.Add($"{lang} line {id}");
                }
            }
        }

        bad.AssertNone("text lines");
        Assert.NotEmpty(Golden.TextLanguages);
    }

    [GameDataFact]
    public void FontPagesAgreeOnEveryPageTheSliceHolds()
    {
        var bad = new Mismatches();
        int pages = 0;
        foreach (Language lang in Golden.Contents.FontLanguages)
        {
            for (int p = 0; p < 65; p++)
            {
                FontPage want = Golden.FontPage(lang, p);
                FontPage got = Folder.FontPage(lang, p);
                bad.Equal($"{lang} page {p} language", want.Language, got.Language);
                bad.Equal($"{lang} page {p} page", want.Page, got.Page);
                bad.Equal($"{lang} page {p} size", (want.Width, want.Height), (got.Width, got.Height));
                if (!want.Rgba.Span.SequenceEqual(got.Rgba.Span))
                {
                    bad.Add($"{lang} page {p}: the RGBA differs");
                }

                pages++;
            }
        }

        bad.AssertNone("font pages");
        Assert.Equal(130, pages);
    }

    [GameDataFact]
    public void PicturesAgreeTileForTile()
    {
        var bad = new Mismatches();
        Assert.NotEmpty(Golden.Contents.Pictures);
        foreach (GoldenPictureKey key in Golden.Contents.Pictures)
        {
            Picture want = Golden.Picture(key.Archive, key.Entry);
            Picture got = Folder.Picture(key.Archive, key.Entry);
            string label = key.ToString();
            bad.Equal($"{label} size", (want.Width, want.Height), (got.Width, got.Height));
            bad.Equal($"{label} tile count", want.Tiles.Length, got.Tiles.Length);
            for (int t = 0; t < Math.Min(want.Tiles.Length, got.Tiles.Length); t++)
            {
                PictureTile w = want.Tiles[t];
                PictureTile g = got.Tiles[t];
                bad.Equal($"{label} tile {t} index", w.Index, g.Index);
                bad.Equal($"{label} tile {t} flags", w.Flags, g.Flags);
                bad.Equal($"{label} tile {t} stored size", (w.StoredWidth, w.StoredHeight), (g.StoredWidth, g.StoredHeight));
                bad.Equal($"{label} tile {t} source", w.Source, g.Source);
                bad.Equal($"{label} tile {t} position", (w.X, w.Y), (g.X, g.Y));
                if (!w.Rgba.Span.SequenceEqual(g.Rgba.Span))
                {
                    bad.Add($"{label} tile {t}: the RGBA differs");
                }
            }
        }

        bad.AssertNone("pictures");
    }

    [GameDataFact]
    public void AudioClipsAgreeSampleForSample()
    {
        var bad = new Mismatches();
        Assert.NotEmpty(Golden.Contents.Audio);
        foreach (GoldenAudioKey key in Golden.Contents.Audio)
        {
            AudioClip want = Golden.Audio(key.Bank, key.Index);
            AudioClip got = Folder.Audio(key.Bank, key.Index);
            string label = key.ToString();
            bad.Equal($"{label} bank", want.Bank, got.Bank);
            bad.Equal($"{label} index", want.Index, got.Index);
            bad.Equal($"{label} channels", want.Channels, got.Channels);
            bad.Equal($"{label} rate", want.SampleRate, got.SampleRate);
            bad.Equal($"{label} frames", want.FrameCount, got.FrameCount);
            bad.Equal($"{label} loop", want.Loop, got.Loop);
            if (!want.Samples.Span.SequenceEqual(got.Samples.Span))
            {
                bad.Add($"{label}: the samples differ");
            }
        }

        bad.AssertNone("audio");
    }

    [GameDataFact]
    public void MoviePathsAgreeForEveryRecordOfTheTable()
    {
        GoldenEbootTables golden = Golden.Tables;
        Assert.Equal(134, golden.Movies.Length);
        Assert.Equal(134, Folder.Tables.Movies.Count);
        for (int i = 0; i < golden.Movies.Length; i++)
        {
            string want = Path.GetFullPath(Golden.Movie(i));
            string got = Folder.Movie(i);
            Assert.True(string.Equals(want, got, StringComparison.OrdinalIgnoreCase), $"movie {i}: {want} vs {got}");
            Assert.True(File.Exists(got), $"movie {i}: {got} is not a file");
            Assert.Equal(golden.Movies[i].Path, Folder.Tables.Movies[i].Path);
            Assert.Equal(golden.Movies[i].Va, (int)Folder.Tables.Movies[i].Va);
            Assert.Equal(golden.Movies[i].Raw.Select(v => unchecked((uint)v)).ToArray(), Folder.Tables.Movies[i].RawFields.ToArray());
        }

        Assert.Equal(golden.PrologueMovieIds.ToArray(), Folder.Catalog.PrologueIds.ToArray());
    }

    [GameDataFact]
    public void TheEbootTablesAgreeWithTheGoldenTables()
    {
        GoldenEbootTables golden = Golden.Tables;
        GameEbootTables ours = Folder.Tables;

        // scene ids (script.md 2.5): all 549
        Assert.Equal(golden.SceneIds.ToArray(), ours.SceneIds.Ids.ToArray().Select(i => (int)i).ToArray());

        // the catalog records of the pictures the slice displays (archives_ui_movies.md 2.4)
        Assert.NotEmpty(golden.CatalogRecords);
        foreach (GoldenCatalogRecord g in golden.CatalogRecords)
        {
            var r = ours.Catalog.Resolver.Resolve(g.Key);
            Assert.True(r.Found, g.Key);
            Assert.Equal(g.NameIndex, r.Index);
            Assert.Equal(g.Name, ours.Catalog.PictureNames[r.Index]);
            Assert.Equal(g.FileId?.Packed, r.FileId?.Packed);
            Assert.Equal(g.OverlayId?.Packed, r.OverlayId?.Packed);
            Assert.Equal(g.ReducedId?.Packed, r.ReducedId?.Packed);
        }

        // the motions the slice starts, with their keyframes (layers.md)
        Assert.NotEmpty(golden.Motions);
        foreach (GoldenMotion g in golden.Motions)
        {
            Assert.Equal(g.DurationMs, ours.Motions.Duration(g.Id));
            Assert.Equal((g.FirstKey, g.EndKey), ours.Motions.KeyRange(g.Id));
            foreach (GoldenMotionKey key in g.Keys)
            {
                MotionKeyframeView k = new(ours.Motions.Keyframe(key.Index));
                Assert.Equal(key.Time, k.Time);
                Assert.Equal(key.Fields.ToArray(), k.Fields);
            }
        }

        // the glyph tables (fonts.md 5): 16 lookup arrays and 17 record arrays, every entry
        GoldenFontTables g2 = golden.FontTables;
        Assert.Equal(g2.Lookups.Length, ours.FontData.Lookups.Count);
        Assert.Equal(g2.Records.Length, ours.FontData.RecordImages.Count);
        for (int i = 0; i < g2.Lookups.Length; i++)
        {
            Assert.Equal(g2.Lookups[i].Name, ours.FontData.Lookups[i].Spec.Name);
            Assert.Equal(g2.Lookups[i].Keys.ToArray(), ours.FontData.Lookups[i].Keys);
            Assert.Equal(g2.Lookups[i].Indices.ToArray(), ours.FontData.Lookups[i].Indices.Select(x => (int)x).ToArray());
        }

        Assert.Equal(g2.WideBase.ToArray(), ours.FontData.WideBase.ToArray());
        Assert.Equal(g2.Tags.ToArray(), ours.FontData.Tags.ToArray());
        Assert.Equal(17_423, ours.FontData.LookupEntryCount);
    }

    [GameDataFact]
    public void EverythingTheSliceDoesNotHoldIsStillAnsweredByTheFolder()
    {
        // the slice holds 2 of 549 scripts, 8 of 43,969 lines in 2 of 5 languages, 130 of 325 pages ...: the folder holds all of it
        Assert.Throws<GoldenSliceMissingException>(() => Golden.Script(0));
        Assert.Equal(0, Folder.Script(0).Index);
        Assert.Throws<GoldenSliceMissingException>(() => Golden.TextLine(Language.Zc, 0));
        Assert.Contains(Enumerable.Range(0, 200), id => Folder.TextLine(Language.Zc, id).Length > 0);
        Assert.Throws<GoldenSliceMissingException>(() => Golden.FontPage(Language.Zt, 0));
        Assert.Equal(256, Folder.FontPage(Language.Zt, 0).Width);
        Assert.Throws<GoldenSliceMissingException>(() => Golden.UiSheet(UiSet.ConfParts, Language.En, 0));
        Assert.Equal(608, Folder.UiSheet(UiSet.ConfParts, Language.En, 0).Width);
    }

    private readonly record struct MotionKeyframeView(Tsuki.Data.Scripts.MotionKeyframe Frame)
    {
        public long Time => Frame.Time;

        public long[] Fields => Frame.Fields.ToArray();
    }
}
#endif

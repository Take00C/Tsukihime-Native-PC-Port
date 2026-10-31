using System.Globalization;
using System.Text.Json;
using Tsuki.Data.Catalog;
using Tsuki.Data.Containers;
using Tsuki.Data.Eboot;
using Tsuki.Data.IO;

namespace Tsuki.Tests.Data.Catalog;

/// <summary>
/// The movie table and its language remap (VA 0x5E280), the UI texture registration tables and the loader rules
/// (VA 0xC2AF0, 0xEB350, 0x407F0), the language suffix table and the prologue table: the oracle's golden files, the document's
/// counts, and the rules on synthetic inputs.
/// </summary>
public sealed class MovieAndUiTableTests
{
    // ---- synthetic rules ------------------------------------------------------------------------------------------

    [Fact]
    public void ThePrologueMovieFollowsTheLanguageAndOthersFallBackToEnglish()
    {
        Assert.Equal(["prologue.mp4", "prologue_en.mp4", "prologue_zc.mp4", "prologue_zt.mp4"], Enumerable.Range(0, 4).Select(l => MovieRules.FileForLanguage(4, l)));
        Assert.Equal(46, MovieRules.IndexForLanguage(4, 4));
        Assert.Equal(46, MovieRules.IndexForLanguage(4, -1));
        Assert.Equal([4, 46, 47, 106], MovieRules.PrologueIds);
    }

    [Fact]
    public void TheOpeningsMapTraditionalChineseToTheSimplifiedFile()
    {
        Assert.Equal(["op1_arc.mp4", "op1_arc_en.mp4", "op1_arc_zc.mp4", "op1_arc_zc.mp4"], Enumerable.Range(0, 4).Select(l => MovieRules.FileForLanguage(5, l)));
        Assert.Equal("op2_ciel_zc.mp4", MovieRules.FileForLanguage(6, 3));
        Assert.Equal("op2_ciel_en.mp4", MovieRules.FileForLanguage(6, 1));
    }

    [Fact]
    public void EveryChapterCardHasItsOwnLanguageFile()
    {
        for (int id = 8; id <= 34; id++)
        {
            string name = MovieRules.Names[id];
            Assert.StartsWith("phasetitle_", name, StringComparison.Ordinal);
            string[] suffix = ["", "_en", "_zc", "_zt"];
            for (int l = 0; l < 4; l++)
            {
                Assert.Equal(name + suffix[l], MovieRules.Names[MovieRules.IndexForLanguage(id, l)]);
            }

            Assert.Equal(id + 40, MovieRules.IndexForLanguage(id, 4)); // out-of-range language behaves like English
        }
    }

    [Fact]
    public void LanguageNeutralIdsAreUnchanged()
    {
        foreach (int id in new[] { 0, 1, 2, 3, 7, 35, 36, 37, 38, 39, 45, 133 })
        {
            Assert.All(Enumerable.Range(0, 5), l => Assert.Equal(id, MovieRules.IndexForLanguage(id, l)));
        }
    }

    [Fact]
    public void TheMovieNamesAre134InTheOrderOfTheTable()
    {
        Assert.Equal(134, MovieRules.Names.Count);
        Assert.Equal("titlebg1", MovieRules.Names[0]);
        Assert.Equal("prologue", MovieRules.Names[4]);
        Assert.Equal("phasetitle_01_00", MovieRules.Names[8]);
        Assert.Equal("phasetitle_15_01b", MovieRules.Names[34]);
        Assert.Equal("TM_CI", MovieRules.Names[38]);
        Assert.Equal("credit00", MovieRules.Names[39]);
        Assert.Equal("prologue_en", MovieRules.Names[46]);
        Assert.Equal("prologue_zc", MovieRules.Names[47]);
        Assert.Equal("op1_arc_en", MovieRules.Names[102]);
        Assert.Equal("prologue_zt", MovieRules.Names[106]);
        Assert.Equal("phasetitle_15_01b_zt", MovieRules.Names[133]);
        Assert.Equal(134, MovieRules.Names.Distinct().Count());
    }

    [Fact]
    public void AMovieKnowsItsGroupLanguageAndBase()
    {
        var info = MovieRules.Info(46);
        Assert.Equal(("prologue_en", "prologue_en.mp4", "prologue", 4, "en", "prologue"), (info.Name, info.File, info.Base, info.BaseId, info.Lang, info.Group));
        Assert.Equal(["prologue.mp4", "prologue_en.mp4", "prologue_zc.mp4", "prologue_zt.mp4"], info.Variants);
        Assert.Equal("neutral", MovieRules.Info(0).Lang);
        Assert.Equal("title background loop", MovieRules.Info(2).Group);
        Assert.Equal("staff credits", MovieRules.Info(40).Group);
        Assert.Equal("TYPE-MOON logo", MovieRules.Info(38).Group);
        Assert.Equal("ending", MovieRules.Info(35).Group);
        Assert.Equal("chapter end", MovieRules.Info(7).Group);
        Assert.Equal("opening", MovieRules.Info(105).Group);
    }

    [Fact]
    public void TheSystemLanguageSelectsTheAssetLanguage()
    {
        Assert.Equal([0, 1, 3, 2, 1, 1], new[] { 0, 1, 10, 11, 9, 2 }.Select(UiTextureTables.LangIndexFromSystem));
    }

    [Fact]
    public void TheLoaderStripsTheExtensionAndInsertsTheLanguage()
    {
        Assert.Equal("conf_parts_en.mrg", UiTextureTables.LocalizePartsName("conf_parts.mrg", 1));
        Assert.Equal("TITLE_PARTS_ja.mrg", UiTextureTables.LocalizePartsName("TITLE_PARTS.MRG", 0));
        Assert.Equal("a_zt.mrg", UiTextureTables.LocalizePartsName("a.mrg", 3)); // length 5: stripped
        Assert.Equal(".mrg_zc.mrg", UiTextureTables.LocalizePartsName(".mrg", 2)); // length 4: kept
        Assert.Equal("x.mzp_ko.mrg", UiTextureTables.LocalizePartsName("x.mzp", 4));
        // a language outside 0..4 is replaced by the system language (VA 0xC2C56 calls 0x407F0)
        Assert.Equal("conf_parts_en.mrg", UiTextureTables.LocalizePartsName("conf_parts.mrg", 7));
        Assert.Equal("conf_parts_zt.mrg", UiTextureTables.LocalizePartsName("conf_parts.mrg", -1, systemLanguage: 10));
        Assert.Equal("conf_parts_ja.mrg", UiTextureTables.LocalizePartsName("conf_parts.mrg", 9, systemLanguage: 0));
        Assert.Equal("_ja.mrg", UiTextureTables.LocalizePartsName(string.Empty, 0));
    }

    [Fact]
    public void AChildPastTheEndSelectsTheLastChild()
    {
        Assert.Equal(82, UiTextureTables.ChildIndex(89, 83));
        Assert.Equal(82, UiTextureTables.ChildIndex(82, 83));
        Assert.Equal(0, UiTextureTables.ChildIndex(0, 83));
        Assert.Equal(4, UiTextureTables.ChildIndex(5, 5));
        Assert.Equal(-1, UiTextureTables.ChildIndex(0, 0));
    }

    // ---- real data ----------------------------------------------------------------------------------------------

    [GameDataFact]
    public void TheMovieTableEqualsTheOracleAndTheFilesOnDisk()
    {
        var movies = SharedEboot.Catalog.Movies;
        var rows = CatalogGolden.Tsv("catalog/movies.tsv", out string[] header);
        Assert.Equal(["movie_id", "va", "path", "f0", "f1", "f2", "f3", "f4", "f5", "f6", "f7"], header);
        Assert.Equal(134, movies.Count);
        Assert.Equal(rows.Count, movies.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var m = movies[i];
            var r = rows[i];
            Assert.True(r[0] == m.MovieId.ToString(CultureInfo.InvariantCulture), $"movie {i} id");
            Assert.True(r[1] == "0x" + m.Va.ToString("x", CultureInfo.InvariantCulture), $"movie {i} va");
            Assert.True(r[2] == m.Path, $"movie {i} path");
            for (int f = 0; f < 8; f++)
            {
                Assert.True(r[3 + f] == m.RawFields[f].ToString(CultureInfo.InvariantCulture), $"movie {i} field {f}");
            }
        }

        Assert.Equal(Enumerable.Range(0, 134), movies.Select(m => m.MovieId));
        Assert.Equal("/app0/image/movie/titlebg1.mp4", movies[0].Path);
        Assert.Equal(-1, SharedEboot.Reader.I32(EbootAddresses.MovieTerminator));
        Assert.Equal(EbootAddresses.MovieTable + (134L * EbootAddresses.MovieRecordSize), EbootAddresses.MovieTerminator);
        Assert.Equal(MovieRules.Names, movies.Select(m => m.FileName[..^4]));
        // the runtime defaults of the player object: 960 x 544 and unit scale
        Assert.All(movies, m => Assert.Equal<uint>([0, 960, 544, 0, 0, 0x3F800000, 0x3F800000, 0], m.RawFields));

        var onDisk = new DirectoryGameFiles(GameFolder.Require()).ListFiles("image/movie").ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(movies.Select(m => m.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase), onDisk);
    }

    [GameDataFact]
    public void TheLanguageSuffixTableAndThePrologueTableEqualTheOracle()
    {
        var cat = SharedEboot.Catalog;
        using var doc = CatalogGolden.Json("catalog/tables.json");
        var root = doc.RootElement;
        Assert.Equal(root.GetProperty("lang_suffixes").EnumerateArray().Select(e => e.GetString()), cat.LanguageSuffixes);
        Assert.Equal(["ja", "en", "zc", "zt", "ko"], cat.LanguageSuffixes);
        Assert.Equal(root.GetProperty("prologue_ids").EnumerateArray().Select(e => e.GetInt32()), cat.PrologueIds);
        Assert.Equal(MovieRules.PrologueIds, cat.PrologueIds);
        var found = root.GetProperty("find_catalog");
        Assert.Equal(found.GetProperty("file_offset").GetInt64(), cat.Images.FileOffset);
        Assert.Equal(found.GetProperty("used").GetInt32(), cat.Images.Count);
        Assert.Equal(found.GetProperty("empty").GetInt32(), cat.Images.EmptySlots);
        Assert.Equal(root.GetProperty("ui_table_vas").EnumerateArray().Select(e => e.GetInt64()), EbootAddresses.UiTables);
        Assert.Equal(root.GetProperty("ui_tables_unreferenced").EnumerateArray().Select(e => e.GetInt64()), EbootAddresses.UiTablesUnreferenced);
        Assert.Equal(EbootAddresses.MovieTerminator, root.GetProperty("movie_terminator_va").GetInt64());
        var segments = root.GetProperty("segments").EnumerateArray().Select(s => (s[0].GetInt64(), s[1].GetInt64(), s[2].GetInt64())).ToArray();
        Assert.Equal(
            [(0L, EbootAddresses.TextFileOffset, EbootAddresses.TextSize), (EbootAddresses.RelroVa, EbootAddresses.RelroFileOffset, EbootAddresses.RelroSize),
                (EbootAddresses.DataVa, EbootAddresses.DataFileOffset, EbootAddresses.DataSize)],
            segments);
    }

    [GameDataFact]
    public void TheRemapOfTheOracleHoldsForEveryIdAndLanguage()
    {
        using var doc = CatalogGolden.Json("lookup/rules.json");
        var root = doc.RootElement;
        Assert.Equal(root.GetProperty("movie_names").EnumerateArray().Select(e => e.GetString()), MovieRules.Names);
        Assert.Equal(root.GetProperty("prologue_ids").EnumerateArray().Select(e => e.GetInt32()), MovieRules.PrologueIds);
        int n = 0;
        foreach (var r in root.GetProperty("movie_remap").EnumerateArray())
        {
            int id = r[0].GetInt32();
            int lang = r[1].GetInt32();
            Assert.True(r[2].GetInt32() == MovieRules.IndexForLanguage(id, lang), $"remap id {id} language {lang}");
            if (r[3].ValueKind == JsonValueKind.String)
            {
                Assert.Equal(r[3].GetString(), MovieRules.FileForLanguage(id, lang));
            }
            else
            {
                Assert.True(MovieRules.IndexForLanguage(id, lang) >= MovieRules.Names.Count);
            }

            n++;
        }

        Assert.Equal(141 * 8, n);
        var infos = root.GetProperty("movie_info").EnumerateArray().ToArray();
        Assert.Equal(134, infos.Length);
        for (int i = 0; i < infos.Length; i++)
        {
            var g = infos[i];
            var info = MovieRules.Info(i);
            Assert.Equal(g.GetProperty("index").GetInt32(), info.Index);
            Assert.Equal(g.GetProperty("name").GetString(), info.Name);
            Assert.Equal(g.GetProperty("file").GetString(), info.File);
            Assert.Equal(g.GetProperty("base").GetString(), info.Base);
            Assert.Equal(g.GetProperty("base_id").GetInt32(), info.BaseId);
            Assert.Equal(g.GetProperty("lang").GetString(), info.Lang);
            Assert.Equal(g.GetProperty("group").GetString(), info.Group);
            var variants = g.GetProperty("variants");
            Assert.Equal(new[] { "ja", "en", "zc", "zt" }.Select(l => variants.GetProperty(l).GetString()), info.Variants);
        }
    }

    [GameDataFact]
    public void TheUiTexturePlacementRulesEqualTheOracle()
    {
        using var doc = CatalogGolden.Json("lookup/rules.json");
        var root = doc.RootElement;
        foreach (var r in root.GetProperty("lang_index_from_system").EnumerateArray())
        {
            Assert.Equal(r[1].GetInt32(), UiTextureTables.LangIndexFromSystem(r[0].GetInt32()));
        }

        int n = 0;
        foreach (var r in root.GetProperty("localize_parts_name").EnumerateArray())
        {
            Assert.True(
                r[3].GetString() == UiTextureTables.LocalizePartsName(r[0].GetString()!, r[1].GetInt32(), r[2].GetInt32()),
                $"{r[0].GetString()} language {r[1].GetInt32()} system {r[2].GetInt32()}");
            n++;
        }

        Assert.True(n > 1_000, n.ToString(CultureInfo.InvariantCulture));
        foreach (var r in root.GetProperty("ui_child_index").EnumerateArray())
        {
            Assert.Equal(r[2].GetInt32(), UiTextureTables.ChildIndex(r[0].GetInt32(), r[1].GetInt32()));
        }
    }

    [GameDataFact]
    public void TheTwentyOneRegistrationTablesEqualTheOracle()
    {
        var tables = SharedEboot.Catalog.UiTables;
        using var doc = CatalogGolden.Json("catalog/ui_tables.json");
        var golden = doc.RootElement.GetProperty("tables").EnumerateArray().ToArray();
        Assert.Equal(21, tables.Count);
        Assert.Equal(golden.Length, tables.Count);
        int records = 0;
        for (int t = 0; t < golden.Length; t++)
        {
            var g = golden[t];
            var table = tables[t];
            Assert.Equal(g.GetProperty("va").GetInt64(), table.Va);
            Assert.Equal(g.GetProperty("terminator").GetString(), Convert.ToHexStringLower(table.Terminator));
            var recs = g.GetProperty("records").EnumerateArray().ToArray();
            Assert.Equal(recs.Length, table.Records.Count);
            for (int i = 0; i < recs.Length; i++)
            {
                var rec = table.Records[i];
                Assert.Equal(recs[i][0].GetInt64(), rec.Va);
                Assert.Equal(recs[i][1].GetInt32(), rec.State);
                Assert.Equal(recs[i][2].GetInt32(), rec.TexSlot);
                Assert.Equal(recs[i][3].GetString(), rec.Name);
                Assert.Equal(recs[i][4].GetInt32(), rec.Child);
                Assert.Equal(recs[i][5].GetInt32(), rec.Child2);
            }

            records += recs.Length;
        }

        Assert.Equal(220, records);
        var conf = tables.Single(t => t.Va == 0x66C9A0).Records;
        Assert.Equal(Enumerable.Range(361, 20), conf.Select(r => r.TexSlot));
        Assert.Equal(Enumerable.Range(0, 20), conf.Select(r => r.Child));
        Assert.All(conf, r => Assert.Equal("conf_parts.mrg", r.Name));
    }

    [GameDataFact]
    public void The707TableLookupsResolveAsDirectClampedAndRewrittenAtRunTime()
    {
        // tools/tests/test_catalog.py::test_ui_texture_tables_resolve: 699 direct, 4 clamped, 4 rewritten, 8 in tables whose archives are not on disk
        var cat = SharedEboot.Catalog;
        long[] neutral = [0x781B70, 0x8730F0];
        long[] absent = [0x78B2C0, .. EbootAddresses.UiTablesUnreferenced];
        using var allpac3 = MrgArchive.Open(GameImage.Path("allpac3"));
        var children = Enumerable.Range(0, allpac3.Count).ToDictionary(i => i, i => allpac3.OpenContainer(i).Count);

        foreach (long va in absent)
        {
            foreach (var r in cat.UiTables.Single(t => t.Va == va).Records)
            {
                for (int l = 0; l < 5; l++)
                {
                    Assert.Null(cat.NameIndex.Find(UiTextureTables.LocalizePartsName(r.Name, l)));
                }
            }
        }

        Assert.Equal([-1], cat.UiTables.Where(t => !absent.Contains(t.Va)).SelectMany(t => t.Records).Select(r => r.Child2).Distinct());
        int direct = 0;
        int clamped = 0;
        int runtime = 0;
        foreach (var table in cat.UiTables.Where(t => !absent.Contains(t.Va)))
        {
            foreach (var r in table.Records)
            {
                string[] names = neutral.Contains(table.Va) ? [r.Name] : Enumerable.Range(0, 4).Select(l => UiTextureTables.LocalizePartsName(r.Name, l)).ToArray();
                foreach (string name in names)
                {
                    var id = cat.NameIndex.Find(name) ?? throw new InvalidOperationException(name);
                    Assert.Equal(ImageArchive.Allpac3, id.Archive);
                    int count = children[id.Index];
                    if (r.Child >= 0 && r.Child < count)
                    {
                        direct++;
                    }
                    else if (table.Va == 0x8731A0 && r.Child == 89)
                    {
                        // selecttxt record 0: child 89 of 83 loads the last child
                        Assert.Equal((83, 82), (count, UiTextureTables.ChildIndex(r.Child, count)));
                        clamped++;
                    }
                    else
                    {
                        // selecttxt record 1: child -1 in the file, rewritten at run time (VA 0xA5FB2..0xA5FC1)
                        Assert.Equal((0x8731A0L, -1, 98), (table.Va, r.Child, r.TexSlot));
                        runtime++;
                    }
                }
            }
        }

        Assert.Equal((699, 4, 4), (direct, clamped, runtime));
    }

    [GameDataFact]
    public void TheNameGrammarTablesEqualTheOracle()
    {
        using var doc = CatalogGolden.Json("lookup/rules.json");
        var root = doc.RootElement;
        var codes = root.GetProperty("char_codes").EnumerateObject().ToDictionary(p => p.Name, p => (p.Value[0].GetInt32(), p.Value[1].GetString()!));
        Assert.Equal(codes.Count, NameGrammar.CharCodes.Count);
        foreach (var (k, v) in codes)
        {
            Assert.Equal(v, NameGrammar.CharCodes[k]);
        }

        Assert.Equal(
            root.GetProperty("size_classes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()),
            NameGrammar.SizeClasses.ToDictionary(p => p.Key, p => p.Value));
        var sets = root.GetProperty("ui_parts_sets").EnumerateObject().ToDictionary(p => p.Name, p => (p.Value[0].GetString()!, p.Value[1].GetString()!));
        Assert.Equal(sets.Count, NameGrammar.UiPartsSets.Count);
        foreach (var (k, v) in sets)
        {
            Assert.Equal(v, NameGrammar.UiPartsSets[k]);
        }

        Assert.Equal(["allpac4", "allpac3", "allpac1_2", "allpac1", "allpac0"], root.GetProperty("image_search_order").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(
            ["allpac4", "allpac3", "allpac1_2", "allpac1", "allpac0"],
            ImageNameIndex.SearchOrder.Select(a => new ImageId(a, 0).ArchiveName));
        foreach (var p in root.GetProperty("archive_ids").EnumerateObject())
        {
            Assert.Equal(p.Value.GetString(), new ImageId((ImageArchive)int.Parse(p.Name, CultureInfo.InvariantCulture), 0).ArchiveName);
        }

        Assert.Equal(ImageNameIndex.KeyLength, root.GetProperty("nam_key_len").GetInt32());
        Assert.Equal(ImageNameIndex.ScanLimit, root.GetProperty("nam_scan_limit").GetInt32());
        Assert.Equal(ScriptImageNames.KeyLength, root.GetProperty("script_key_len").GetInt32());
        foreach (var c in root.GetProperty("nam_key").EnumerateArray())
        {
            Assert.Equal(c[1].GetString(), Hex(ImageNameIndex.Key(CatalogGolden.FromHex(c[0].GetString()!))));
        }

        foreach (var c in root.GetProperty("script_image_key").EnumerateArray())
        {
            Assert.Equal(c[1].GetString(), ScriptImageNames.Key(CatalogGolden.FromHex(c[0].GetString()!)));
        }

        foreach (var c in root.GetProperty("stch_image_name").EnumerateArray())
        {
            Assert.Equal(c[3].GetString(), ScriptImageNames.StchName(c[0].GetString()!, c[1].GetInt32(), c[2].GetInt32()));
        }
    }

    private static string Hex(byte[] b) => b.Length == 0 ? "-" : Convert.ToHexStringLower(b);
}

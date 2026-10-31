using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tsuki.Data.Containers;
using Tsuki.Data.IO;
using Tsuki.Data.Text;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Data.Text;

/// <summary>
/// script_text.mrg against the oracle (<c>tsuki.text</c>, run by <c>text_oracle.py lines</c>): 5 x 43,969 lines, every table and every line.
/// </summary>
public sealed class ScriptTextTests
{
    private const string Oracle = "tests/Tsuki.Tests.Data/Text/text_oracle.py";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(8);
    private static readonly string[] Codes = ["ja", "en", "zc", "zt", "ko"];

    // -- real data ------------------------------------------------------------------------------------------------------

    [GameDataFact]
    public void TheFileHasFiveTimes43969LinesAndNoDeviationFromTheRetailLayout()
    {
        ScriptText text = SharedText.Value;
        Assert.Equal(TextRules.RetailLineCount, text.Count);
        Assert.Equal(TextRules.RetailLineCount, 43_969);
        foreach (Language lang in Enum.GetValues<Language>())
        {
            Assert.Equal(43_969, text.Table(lang).Count);
            Assert.Equal(1, text.EndMarks[(int)lang]);
        }

        Assert.Empty(text.Validate());
        using JsonDocument container = OracleRun.One(Oracle, "lines", "container", Timeout);
        Assert.Equal(43_969, container.RootElement.GetProperty("count").GetInt32());
        Assert.Empty(container.RootElement.GetProperty("issues").EnumerateArray());
        Assert.Empty(container.RootElement.GetProperty("validate").EnumerateArray());
    }

    [GameDataFact]
    public void EveryTableEqualsTheOraclesOffsetsAndBlobByteForByte()
    {
        int seen = 0;
        foreach (JsonDocument doc in OracleRun.Records(Oracle, "lines", "table", Timeout))
        {
            using (doc)
            {
                JsonElement t = doc.RootElement;
                Language lang = TextRules.Parse(t.GetProperty("lang").GetString()!);
                TextTable table = SharedText.Value.Table(lang);
                Assert.Equal(t.GetProperty("count").GetInt32(), table.Count);
                Assert.Equal(t.GetProperty("offsets").GetInt32(), table.Offsets.Length);
                Assert.Equal(t.GetProperty("blob_len").GetInt32(), table.Blob.Length);
                Assert.Equal(t.GetProperty("blob_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(table.Blob.Span)));
                byte[] be = new byte[4 * table.Offsets.Length];
                for (int i = 0; i < table.Offsets.Length; i++)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(be.AsSpan(4 * i), table.Offsets[i]);
                }

                Assert.Equal(t.GetProperty("offsets_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(be)));
                Assert.Equal(t.GetProperty("end_marks").GetInt32(), SharedText.Value.EndMarks[(int)lang]);
                Assert.Equal(
                    t.GetProperty("validate").EnumerateArray().Select(e => e.GetString()).ToArray(),
                    table.Validate().ToArray());
                seen++;
            }
        }

        Assert.Equal(5, seen);
    }

    [GameDataTheory]
    [InlineData("ja")]
    [InlineData("en")]
    [InlineData("zc")]
    [InlineData("zt")]
    [InlineData("ko")]
    public void EveryEngineLineEqualsTheOracle(string code) => CompareCategory(code, "engine");

    [GameDataTheory]
    [InlineData("ja")]
    [InlineData("en")]
    [InlineData("zc")]
    [InlineData("zt")]
    [InlineData("ko")]
    public void EveryLineTokenizesLikeTheOracleInLineAndScriptMode(string code)
    {
        CompareCategory(code, "tok");
        CompareCategory(code, "tokscript");
    }

    [GameDataTheory]
    [InlineData("ja")]
    [InlineData("en")]
    [InlineData("zc")]
    [InlineData("zt")]
    [InlineData("ko")]
    public void EveryLinesStyledRunsAndPlainTextEqualTheOracle(string code)
    {
        CompareCategory(code, "runs");
        CompareCategory(code, "plain");
    }

    [GameDataFact]
    public void TheMarkupCensusOfEveryLanguageEqualsTheOracle()
    {
        int seen = 0;
        foreach (JsonDocument doc in OracleRun.Records(Oracle, "lines", "census", Timeout))
        {
            using (doc)
            {
                Language lang = TextRules.Parse(doc.RootElement.GetProperty("lang").GetString()!);
                TextTable table = SharedText.Value.Table(lang);
                SortedDictionary<string, Markup.CensusEntry> ours =
                    Markup.Census(Enumerable.Range(0, table.Count).Select(table.LineBytes), lang);
                var theirs = doc.RootElement.GetProperty("entries").EnumerateArray().ToList();
                Assert.Equal(theirs.Count, ours.Count);
                foreach (JsonElement e in theirs)
                {
                    string key = Encoding.UTF8.GetString(Convert.FromHexString(e[0].GetString()!));
                    Assert.True(ours.TryGetValue(key, out Markup.CensusEntry? entry), $"{Codes[(int)lang]}: census key {key} missing");
                    Assert.Equal(e[1].GetInt32(), entry!.Count);
                    Assert.Equal(e[2].GetInt32(), entry.Lines);
                    Assert.Equal(e[3].EnumerateArray().Select(x => x.GetInt32()).ToArray(), entry.FirstIds.ToArray());
                }

                seen++;
            }
        }

        Assert.Equal(5, seen);
    }

    [GameDataFact]
    public void TheGuardEntriesAndTheFirstIdBeyondThemAnswerLikeTheOracle()
    {
        foreach (JsonDocument doc in OracleRun.Records(Oracle, "lines", "guards", Timeout))
        {
            using (doc)
            {
                Language lang = TextRules.Parse(doc.RootElement.GetProperty("lang").GetString()!);
                TextTable table = SharedText.Value.Table(lang);
                foreach (JsonElement row in doc.RootElement.GetProperty("lines").EnumerateArray())
                {
                    Assert.Equal(row[1].GetString(), Convert.ToHexStringLower(table.EngineLine(row[0].GetInt32()).Span));
                }

                Assert.Equal("IndexError", doc.RootElement.GetProperty("beyond").GetString());
                Assert.Throws<ArgumentOutOfRangeException>(() => table.EngineLine(table.Count + 2));
                Assert.Throws<ArgumentOutOfRangeException>(() => table.Raw(table.Count));
                Assert.Throws<ArgumentOutOfRangeException>(() => table.Raw(-1));
            }
        }
    }

    [GameDataFact]
    public void TheSliceExportAndTheDataExportAgreeWithTheReader()
    {
        // samples/golden_data/scripttext.tsv (gen_golden_data.py): counts and sizes per language
        string[] tsv = File.ReadAllLines(Path.Combine(GoldenData.RepoRoot, "samples", "golden", "data", "scripttext.tsv"));
        string[] header = tsv[0].Split('	');
        foreach (TsvRow row in tsv.Skip(1).Select(l => new TsvRow(header, l.Split('	'))))
        {
            Language lang = TextRules.Parse(row["lang"]);
            TextTable table = SharedText.Value.Table(lang);
            Assert.Equal(int.Parse(row["count"], System.Globalization.CultureInfo.InvariantCulture), table.Count);
            Assert.Equal(int.Parse(row["blob_bytes"], System.Globalization.CultureInfo.InvariantCulture), table.Blob.Length);
            Assert.Equal(int.Parse(row["table_entries"], System.Globalization.CultureInfo.InvariantCulture), table.Offsets.Length + SharedText.Value.EndMarks[(int)lang]);
            Assert.Equal(int.Parse(row["table_bytes"], System.Globalization.CultureInfo.InvariantCulture), 4 * (table.Offsets.Length + SharedText.Value.EndMarks[(int)lang]));
        }

        // samples/golden/slice/text/lines.json: 8 lines per language with their raw and engine bytes
        string path = Path.Combine(GoldenData.RepoRoot, "samples", "golden", "slice", "text", "lines.json");
        using JsonDocument slice = JsonDocument.Parse(File.ReadAllBytes(path));
        int lines = 0;
        foreach (JsonProperty language in slice.RootElement.GetProperty("languages").EnumerateObject())
        {
            TextTable table = SharedText.Value.Table(TextRules.Parse(language.Name));
            foreach (JsonElement line in language.Value.GetProperty("lines").EnumerateArray())
            {
                int id = line.GetProperty("id").GetInt32();
                Assert.Equal(line.GetProperty("raw_hex").GetString(), Convert.ToHexStringLower(table.Raw(id).Span));   // the blob bytes, CR LF included
                Assert.Equal(line.GetProperty("engine_line_hex").GetString(), Convert.ToHexStringLower(table.EngineLine(id).Span));
                Assert.Equal(line.GetProperty("raw_sha1").GetString(), Convert.ToHexStringLower(SHA1.HashData(table.Raw(id).Span)));
                Assert.Equal(line.GetProperty("offset").GetInt32(), (int)table.Offsets[id]);
                Assert.Equal(line.GetProperty("next_offset").GetInt32(), (int)table.Offsets[id + 1]);
                lines++;
            }
        }

        Assert.True(lines >= 16, $"only {lines} slice lines were compared");
    }

    [GameDataFact]
    public void OpeningTheFileReadsTheTablesAndNoBlobUntilALineIsAsked()
    {
        var files = new CountingFiles(SharedText.Files);
        ScriptText text = ScriptText.Open(files);
        long fileLength = files.Length("image/script_text.mrg");
        Assert.True(files.BytesRead < 1_000_000, $"opening read {files.BytesRead} bytes");
        Assert.True(files.BytesRead < fileLength / 10);
        _ = text.EngineLine(Language.En, 3);
        Assert.True(files.BytesRead > 2_700_000, "the English blob was read");
        Assert.True(files.BytesRead < 4_000_000, $"only the English pair should be read, not {files.BytesRead}");
        Assert.Equal(["image/script_text.mrg"], files.OpenedFiles);
    }

    // -- no game data ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ASyntheticFileReadsBackLineForLine()
    {
        string[][] langs =
        [
            ["こんにちは", "second", "x", ""],
            ["hello", "w", "e", ""],
            ["a", "b", "c", "d"],
            ["1", "2", "3", "4"],
            ["　", "　", "　", "　"],
        ];
        byte[] file = MiniMzp.ScriptText(langs);
        ScriptText text = ScriptText.FromMemory(file);
        Assert.Equal(4, text.Count);
        Assert.Equal("こんにちは", text.Line(Language.Ja, 0));
        Assert.Equal("hello", text.Line(Language.En, 0));
        Assert.Equal(Encoding.UTF8.GetBytes("こんにちは\r\n"), text.Raw(Language.Ja, 0).ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes("こんにちは"), text.EngineLine(Language.Ja, 0).ToArray());
        Assert.Empty(text.EngineLine(Language.Ja, 3).ToArray());   // an empty line is CR LF only: shorter than 3 bytes
        Assert.Equal("x"u8.ToArray(), text.EngineLine(Language.Ja, 2).ToArray());   // "x" + CR LF is exactly 3 bytes: min(3, 0x301) - 2 = 1
        Assert.Empty(text.Validate());
    }

    [Fact]
    public void AThreeByteEntryCopiesOneByte()
    {
        ScriptText text = ScriptText.FromMemory(MiniMzp.ScriptText([["x"], ["x"], ["x"], ["x"], ["x"]]));
        Assert.Equal("x"u8.ToArray(), text.EngineLine(Language.En, 0).ToArray());
    }

    [Fact]
    public void LanguagesWithDifferentLineCountsAreRefused()
    {
        byte[] file = MiniMzp.ScriptText([["a", "b"], ["a", "b"], ["a", "b"], ["a", "b", "c"], ["a", "b"]]);
        TextFormatException e = Assert.Throws<TextFormatException>(() => ScriptText.FromMemory(file));
        Assert.Contains("different line counts", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AContainerWithTheWrongSectionCountIsRefused()
    {
        byte[] file = MiniMzp.Build([new byte[8], new byte[8]]);
        TextFormatException e = Assert.Throws<TextFormatException>(() => ScriptText.FromMemory(file));
        Assert.Contains("expected 10 sections", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreeEndMarksAreAcceptedAndReportedByValidate()
    {
        string[][] langs = [["a"], ["b"], ["c"], ["d"], ["e"]];
        ScriptText text = ScriptText.FromMemory(MiniMzp.ScriptText(langs, endMarks: 3));
        Assert.All(text.EndMarks, n => Assert.Equal(3, n));
        Assert.Equal(5, text.Validate().Count(m => m.Contains("end marks (retail has 1)", StringComparison.Ordinal)));
        Assert.Equal("a", text.Line(Language.Ja, 0));
    }

    [Fact]
    public void OffsetTablesFollowTheRetailRulesAndTheMangetsuRepack()
    {
        byte[] Table(params uint[] v)
        {
            byte[] b = new byte[4 * v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4 * i), v[i]);
            }

            return b;
        }

        uint[] parsed = TextTable.ParseOffsets(Table(0, 5, 9, 9, 0xFFFFFFFF), strict: true, out int marks);
        Assert.Equal([0u, 5, 9, 9], parsed);
        Assert.Equal(1, marks);
        parsed = TextTable.ParseOffsets(Table(0, 5, 5, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF), strict: false, out marks);
        Assert.Equal(3, marks);
        Assert.Equal(3, parsed.Length);
        Assert.Throws<TextFormatException>(() => TextTable.ParseOffsets(Table(0, 5, 5, 0xFFFFFFFF, 0xFFFFFFFF), strict: true, out _));
        Assert.Throws<TextFormatException>(() => TextTable.ParseOffsets(Table(0, 5, 5), strict: false, out _));
        Assert.Throws<TextFormatException>(() => TextTable.ParseOffsets(Table(0, 5, 0xFFFFFFFF, 7), strict: false, out _));
        Assert.Throws<TextFormatException>(() => TextTable.ParseOffsets(Table(0, 0xFFFFFFFF), strict: false, out _));
        Assert.Throws<TextFormatException>(() => TextTable.ParseOffsets(new byte[6], strict: false, out _));
    }

    [Fact]
    public void ADamagedTableNeverThrowsFromAReadAndValidateNamesTheDamage()
    {
        // offsets: line 0 = [0,4) ok, line 1 = [4,2) decreasing, line 2 starts past the blob end
        var table = new TextTable(Language.En, [0, 4, 2, 100, 10, 10], "ab\r\ncd"u8.ToArray());
        Assert.Equal("ab\r\n"u8.ToArray(), table.Raw(0).ToArray());
        Assert.Empty(table.Raw(1).ToArray());
        Assert.Equal("\r\ncd"u8.ToArray(), table.Raw(2).ToArray());          // Python slicing: clamped to the blob
        Assert.Empty(table.EngineLine(1).ToArray());                           // negative length
        Assert.Equal("\r\ncd"u8.ToArray(), table.EngineLine(2).ToArray());
        IReadOnlyList<string> issues = table.Validate();
        Assert.Contains(issues, m => m.Contains("offsets decrease at 1", StringComparison.Ordinal));
        Assert.Contains(issues, m => m.Contains("does not end with blob_size twice", StringComparison.Ordinal));
        Assert.Contains(issues, m => m.Contains("strings without CRLF", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEngineLineIsCutAtThe767ByteCapWithoutChecksOnItsEnd()
    {
        byte[] blob = new byte[2000];
        Array.Fill(blob, (byte)'x');
        var table = new TextTable(Language.En, [0, 1000, 1500, 2000, 2000], blob);
        Assert.Equal(0x2FF, table.EngineLine(0).Length);            // 1000 bytes: min(1000, 0x301) - 2
        Assert.Equal(498, table.EngineLine(1).Length);              // 500 bytes: 498, whatever the last two bytes are
        Assert.Equal(498, table.EngineLine(2).Length);
        Assert.Equal(1000, table.Raw(0).Length);
    }

    [Fact]
    public void TheLanguageRulesFollowTheEngine()
    {
        Assert.Equal(["ja", "en", "zc", "zt", "ko"], TextRules.LanguageCodes);
        Assert.Equal(Language.Zt, TextRules.Parse("zt"));
        Assert.Throws<ArgumentException>(() => TextRules.Parse("fr"));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextRules.Code((Language)5));
        Assert.False(TextRules.RubyEnabled(Language.En));
        Assert.True(TextRules.RubyEnabled(Language.Ja));
        Assert.Equal(0, TextRules.SystemLanguageToText(0));
        Assert.Equal(2, TextRules.SystemLanguageToText(11));
        Assert.Equal(3, TextRules.SystemLanguageToText(10));
        Assert.Equal(1, TextRules.SystemLanguageToText(null));
        Assert.Equal(1, TextRules.SystemLanguageToText(7));
        Assert.Equal((3, 3), TextRules.EffectiveLanguages(3, 0));
        Assert.Equal((4, 0), TextRules.EffectiveLanguages(4, 0));       // $ expansion takes the blank ko slot, the layout falls back
        Assert.Equal((2, 2), TextRules.EffectiveLanguages(TextRules.LanguageOptionUnset, 11));
        Assert.Equal(43960, TextRules.ChoiceNumberId(0));
        Assert.Equal(43968, TextRules.ChoiceNumberId(8));
        Assert.Equal(43959, TextRules.ChoiceNumberId(9));
        Assert.Equal("$043960あ"u8.ToArray(), TextRules.ChoiceSlotText(0, "あ"u8));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextRules.ChoiceNumberId(-1));
    }

    [GameDataFact]
    public void TheEngineConstantsEqualTheOraclesAndTheEbootTables()
    {
        using JsonDocument rules = OracleRun.One(Oracle, "rules", "consts", Timeout);
        JsonElement r = rules.RootElement;
        Assert.Equal(r.GetProperty("icons").EnumerateArray().Select(e => e.GetString()).ToArray(), TextRules.IconNames.ToArray());
        Assert.Equal(39, TextRules.IconNames.Count);
        Assert.Equal(TextRules.EngineLineMax, r.GetProperty("engine_line_max").GetInt32());
        Assert.Equal(TextRules.EngineExpandMax, r.GetProperty("expand_max").GetInt32());
        Assert.Equal(TextRules.MessageArgMax, r.GetProperty("arg_max").GetInt32());
        Assert.Equal(TextRules.ChoiceNumberBase, r.GetProperty("number_base").GetInt32());
        Assert.Equal(TextRules.ChoiceNumberOverflow, r.GetProperty("number_overflow").GetInt32());
        Assert.Equal(TextRules.ChoiceSlotSize, r.GetProperty("slot_size").GetInt32());
        Assert.Equal(TextRules.LanguageOptionUnset, r.GetProperty("unset").GetInt32());
        foreach (JsonElement c in r.GetProperty("colors").EnumerateArray())
        {
            Assert.Equal(c[1].GetInt32(), TextRules.ColorRgb565[c[0].GetInt32()]);
        }

        string[] scale = r.GetProperty("scale").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(scale, TextRules.ScaleTable.Select(d => BitConverter.DoubleToInt64Bits(d).ToString("x16", System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        foreach (JsonElement row in r.GetProperty("ruby").EnumerateArray())
        {
            Assert.Equal(row[1].GetBoolean(), TextRules.RubyEnabled(TextRules.Parse(row[0].GetString()!)));
        }

        // the float32 tables in the eboot: scale table at text VA 0x2D3730, colour words at 0x2D34C0, icon names at 0x2D3580 (39 x 11)
        var eboot = Tsuki.Data.Eboot.EbootReader.Parse(File.ReadAllBytes(Path.Combine(GameFolder.Require(), "eboot.bin")));
        for (int i = 0; i < 10; i++)
        {
            float f = BitConverter.Int32BitsToSingle(eboot.I32(0x2D3730 + (4 * i)));
            Assert.Equal((double)f, TextRules.ScaleTable[i]);
        }

        for (int i = 1; i <= 5; i++)
        {
            Assert.Equal(TextRules.ColorRgb565[i], (int)(ushort)eboot.I16(0x2D34C0 + (2 * i)));
        }

        for (int i = 0; i < 39; i++)
        {
            Assert.Equal("[" + TextRules.IconNames[i] + "]", eboot.CString(0x2D3580 + (11 * i), 11));
        }
    }

    [GameDataFact]
    public void TheLanguageGridsAndChoiceSlotsEqualTheOracle()
    {
        using (JsonDocument sys = OracleRun.One(Oracle, "rules", "sys", Timeout))
        {
            foreach (JsonElement row in sys.RootElement.GetProperty("entries").EnumerateArray())
            {
                int? input = row[0].ValueKind == JsonValueKind.Null ? null : row[0].GetInt32();
                Assert.Equal(row[1].GetInt32(), TextRules.SystemLanguageToText(input));
            }
        }

        using (JsonDocument eff = OracleRun.One(Oracle, "rules", "eff", Timeout))
        {
            int n = 0;
            foreach (JsonElement row in eff.RootElement.GetProperty("entries").EnumerateArray())
            {
                int? sys = row[1].ValueKind == JsonValueKind.Null ? null : row[1].GetInt32();
                (int expansion, int layout) = TextRules.EffectiveLanguages(row[0].GetInt32(), sys);
                Assert.Equal(row[2][0].GetInt32(), expansion);
                Assert.Equal(row[2][1].GetInt32(), layout);
                n++;
            }

            Assert.True(n > 100);
        }

        using JsonDocument choice = OracleRun.One(Oracle, "rules", "choice", Timeout);
        foreach (JsonElement row in choice.RootElement.GetProperty("entries").EnumerateArray())
        {
            int slot = row[0].GetInt32();
            Assert.Equal(row[1].GetInt32(), TextRules.ChoiceNumberId(slot));
            Assert.Equal(row[2].GetString(), Convert.ToHexStringLower(TextRules.ChoiceSlotText(slot, "あ"u8)));
        }
    }

    private static void CompareCategory(string code, string category)
    {
        Language lang = TextRules.Parse(code);
        string? oracle = null;
        foreach (JsonDocument doc in OracleRun.Records(Oracle, "lines", "lines", Timeout))
        {
            using (doc)
            {
                JsonElement e = doc.RootElement;
                if (e.GetProperty("lang").GetString() == code && e.GetProperty("cat").GetString() == category)
                {
                    oracle = e.GetProperty("h").GetString();
                    break;
                }
            }
        }

        Assert.NotNull(oracle);
        TextHashes.AssertEqual($"{code} {category}", oracle!, TextHashes.Get(lang, category));
    }
}

/// <summary>An <see cref="IGameFiles"/> that counts the bytes read and remembers which files were opened.</summary>
internal sealed class CountingFiles(IGameFiles inner) : IGameFiles
{
    private long _bytes;
    private readonly HashSet<string> _opened = new(StringComparer.OrdinalIgnoreCase);

    public long BytesRead => Interlocked.Read(ref _bytes);

    public IReadOnlyCollection<string> OpenedFiles => _opened.OrderBy(s => s, StringComparer.Ordinal).ToArray();

    public string Description => inner.Description;

    public bool Exists(string relativePath) => inner.Exists(relativePath);

    public long Length(string relativePath) => inner.Length(relativePath);

    public int ReadAt(string relativePath, long offset, Span<byte> buffer)
    {
        int n = inner.ReadAt(relativePath, offset, buffer);
        Interlocked.Add(ref _bytes, n);
        lock (_opened)
        {
            _opened.Add(relativePath);
        }

        return n;
    }

    public IReadOnlyList<string> ListFiles(string relativeDirectory) => inner.ListFiles(relativeDirectory);
}

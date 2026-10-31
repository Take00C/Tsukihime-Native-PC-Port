using System.Text;
using Tsuki.Data.Eboot;

namespace Tsuki.Tests.Data.Catalog;

/// <summary>
/// <c>ui_text[5][1499]</c> (RELRO VA 0x41D5F0, <c>tools/eboot/uitext.py</c>): 5 x 1,499 strings equal the oracle's TSV files
/// (and the research export <c>samples/engine/ui_text_&lt;lang&gt;.tsv</c> byte for byte, RR 1 item 3); the getter's range rule;
/// a damaged pointer or string is an error, not a wrong text.
/// </summary>
public sealed class UiTextTests
{
    private static readonly UTF8Encoding Strict = new(false, true);

    private static UiTextTable Table() => UiTextTable.Read(SharedEboot.Reader.Image);

    [GameDataTheory]
    [InlineData(0, "ja")]
    [InlineData(1, "en")]
    [InlineData(2, "zc")]
    [InlineData(3, "zt")]
    [InlineData(4, "ko")]
    public void EveryLanguageEqualsTheOraclesTsvByteForByte(int index, string lang)
    {
        var table = Table();
        Assert.Equal(UiTextTable.Count, table.Rows(index).Count);
        Assert.Equal(lang, UiTextTable.Languages[index]);
        string expected = File.ReadAllText(CatalogGolden.Locate($"eboot/ui_text_{lang}.tsv"), Strict);
        string actual = table.ToTsv(index);
        Assert.True(expected == actual, FirstDifference(expected, actual));

        // the research export of docs/formats/engine.md (samples/engine) is the same file; it is only compared when this checkout has it
        string research = Path.Combine(GoldenData.RepoRoot, "samples", "engine", $"ui_text_{lang}.tsv");
        if (File.Exists(research))
        {
            Assert.Equal(File.ReadAllBytes(research), Strict.GetBytes(actual));
        }
    }

    [GameDataFact]
    public void TheTableHas7495EntriesAndPlaceholdersAtIdZero()
    {
        var table = Table();
        int total = 0;
        for (int l = 0; l < 5; l++)
        {
            var rows = table.Rows(l);
            total += rows.Count;
            Assert.Equal(Enumerable.Range(0, 1499), rows.Select(r => r.Id));
            Assert.All(rows, r => Assert.Equal(UiTextTable.Languages[l], r.Lang));
            Assert.All(rows, r => Assert.True(r.Va > 0));
        }

        Assert.Equal(7495, total);
        Assert.Equal("NULL", table.Rows(0)[0].Text);
        Assert.Equal("NULL", table.Rows(1)[0].Text);
        Assert.All(new[] { 2, 3, 4 }, l => Assert.StartsWith("NULL", table.Rows(l)[0].Text, StringComparison.Ordinal));
        // the strings of each language sit in their own block, in id order
        for (int l = 0; l < 5; l++)
        {
            var vas = table.Rows(l).Skip(1).Select(r => r.Va).ToArray();
            Assert.Equal(vas.OrderBy(v => v), vas);
        }

        // each language block starts with its id-0 placeholder (uitext.py: JA 0xD63F38, EN 0xD7BFCE, ZC 0xD8DF2C, ZT 0xDA0AAA, KO 0xDB3CE7)
        Assert.Equal([0xD63F38L, 0xD7BFCEL, 0xD8DF2CL, 0xDA0AAAL, 0xDB3CE7L], Enumerable.Range(0, 5).Select(l => table.Rows(l)[0].Va));
        Assert.All(Enumerable.Range(0, 5), l => Assert.Equal(1499, table.Rows(l).Select(r => r.Va).Distinct().Count()));
    }

    [GameDataFact]
    public void TheGetterAppliesTheRangeRuleOfTheEngine()
    {
        var table = Table();
        Assert.Null(table.Get(0, 0));
        Assert.Null(table.Get(0, -1));
        Assert.Null(table.Get(0, 0x5DB));
        Assert.Null(table.Get(0, 1499));
        Assert.Null(table.Get(5, 1));
        Assert.Null(table.Get(-1, 1));
        Assert.Equal(table.Rows(0)[1].Text, table.Get(0, 1));
        Assert.Equal(table.Rows(0)[1498].Text, table.Get(0, 1498));
        Assert.Equal(table.Rows(1)[7].Text, table.Get(1, 7));
        Assert.Equal(table.Rows(4)[1498].Text, table.Get(4, 1498));
    }

    [Fact]
    public void TheEscapesOfThePythonExportAreTabLineFeedCarriageReturnAndBackslash()
    {
        Assert.Equal("a\\\\b\\tc\\nd\\re", UiTextTable.Escape("a\\b\tc\nd\re"));
        Assert.Equal(string.Empty, UiTextTable.Escape(string.Empty));
        Assert.Equal("^line^break", UiTextTable.Escape("^line^break"));
    }

    [GameDataFact]
    public void ADamagedPointerIsAnErrorBecauseItDisagreesWithItsRelocation()
    {
        byte[] data = (byte[])SharedEboot.Data.Clone();
        data[SharedEboot.RelroFile(EbootAddresses.UiText + (8 * 5))]++;
        var e = Assert.Throws<EbootFormatException>(() => UiTextTable.Read(SelfImage.Parse(data)));
        Assert.Contains("ui_text[ja][5]", e.Message, StringComparison.Ordinal);
        Assert.Contains("relocation", e.Message, StringComparison.Ordinal);
    }

    [GameDataFact]
    public void AStringThatIsNotUtf8IsAnError()
    {
        byte[] data = (byte[])SharedEboot.Data.Clone();
        long va = Table().Rows(1)[10].Va;
        data[SharedEboot.Reader.FileOffset(va)!.Value] = 0xFF;
        var e = Assert.Throws<EbootFormatException>(() => UiTextTable.Read(SelfImage.Parse(data)));
        Assert.Contains("ui_text[en][10]", e.Message, StringComparison.Ordinal);
    }

    private static string FirstDifference(string expected, string actual)
    {
        if (expected == actual)
        {
            return string.Empty;
        }

        string[] a = expected.Split('\n');
        string[] b = actual.Split('\n');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
            {
                return $"line {i + 1} differs: expected \"{a[i]}\", got \"{b[i]}\"";
            }
        }

        return $"{a.Length} lines expected, {b.Length} found";
    }
}

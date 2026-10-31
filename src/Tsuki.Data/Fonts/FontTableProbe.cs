using System.Globalization;
using Tsuki.Data.Containers;
using Tsuki.Data.Eboot;
using Tsuki.Data.Verification;

namespace Tsuki.Data.Fonts;

/// <summary>
/// The font-table probe of RR 3.3 item 2 (<c>EbootFontTables.verify()</c>, docs/formats/fonts.md 10.7): the code still loads every glyph
/// table from the expected address, the lookup arrays are sorted permutations, the tag names and wide-grid bases are the documented ones.
/// It is the eighth structural probe next to <see cref="EbootProbes"/> and is not run by <see cref="BuildCheck"/> yet.
/// </summary>
public static class FontTableProbe
{
    /// <summary>The id of the probe's <see cref="CheckResult"/>.</summary>
    public const string Id = "eboot.probe.fonts";

    private const string Title = "font tables: 16 lookup arrays (17,423 entries), 17 record arrays, tags, wide-grid bases";

    /// <summary>Runs the probe on the bytes of eboot.bin. Never throws for malformed content.</summary>
    public static CheckResult Run(ReadOnlyMemory<byte> eboot)
    {
        try
        {
            EbootFontData data = EbootFontData.Read(EbootReader.Parse(eboot));
            IReadOnlyList<string> issues = data.Verify();
            string actual = issues.Count == 0
                ? $"{data.Lookups.Count} lookup arrays with {data.LookupEntryCount.ToString("N0", CultureInfo.InvariantCulture)} entries"
                : $"{issues.Count} problem(s)";
            return issues.Count == 0 && data.LookupEntryCount == 17_423
                ? new CheckResult(Id, Title, CheckStatus.Pass, BuildFailure.None, "no problems", actual, string.Empty)
                : Fail(actual, issues.Count == 0 ? "the lookup arrays hold " + data.LookupEntryCount + " entries, not 17,423" : string.Join("; ", issues.Take(3)));
        }
        catch (Exception e) when (e is ContainerFormatException or ArgumentException or IndexOutOfRangeException or InvalidOperationException
                                      or OverflowException or KeyNotFoundException or InvalidCastException)
        {
            return Fail("unreadable", e.Message);
        }
    }

    private static CheckResult Fail(string actual, string detail) =>
        new(Id, Title, CheckStatus.Fail, BuildFailure.EbootProbeFailed, "no problems", actual,
            $"{Title}: {detail}. The font tables are not where the analysed build has them.");
}

using System.Globalization;
using Tsuki.Cli;

namespace Tsuki.Tests.Data;

/// <summary>
/// <c>Tsuki.Cli decode-all</c>: every MZX0 stream of the smaller archives is decoded in-process and the totals are
/// the ones of containers.md 5.5 (streams, compressed bytes, decompressed bytes, largest overrun), with every stream
/// ending exactly at the end of its section. The three giant archives (allpac0, allpac1, allpac1_2: 1,041,633 streams)
/// are covered by the command itself, about 30 s: <c>Tsuki.Cli decode-all</c>.
/// </summary>
public sealed class DecodeAllTests
{
    private sealed record Row(string Source, long Streams, long Compressed, long Decompressed, int MaxOvershoot, long Failures);

    // containers.md 5.5
    private static readonly Row[] Expected =
    [
        new("allpac2", 15_777, 361_818_805, 1_384_505_472, 2, 0),
        new("allpac3", 25_099, 347_692_514, 2_026_909_440, 4, 0),
        new("allpac4", 2_738, 56_054_300, 395_779_808, 120, 0),
        new("parts", 2_174, 776_552, 35_602_560, 0, 0),
        new("voice", 16_464, 1_303_554_952, 1_545_322_624, 126, 0),
        new("voice2", 2, 906, 3_720, 0, 0),
        new("allscr.mrg", 550, 9_433_876, 19_633_657, 127, 0), // 549 scripts + 1 tile
        new("ft_en.mrg", 65, 1_140_985, 67_174_400, 0, 0),
        new("ft_ja.mrg", 65, 8_943_421, 67_174_400, 0, 0),
        new("ft_ko.mrg", 65, 525_330, 67_174_400, 0, 0),
        new("ft_zc.mrg", 65, 10_727_471, 67_174_400, 0, 0),
        new("ft_zt.mrg", 65, 10_049_550, 67_174_400, 0, 0),
    ];

    [GameDataFact]
    public void TheSmallerArchivesDecodeToTheDocumentedTotals()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = CliApp.Run(["decode-all", "--game", GameFolder.Require(), "--only", "allpac2,allpac3,allpac4,parts,voice,voice2,allscr,ft_en,ft_ja,ft_ko,ft_zc,ft_zt"], output, error);
        Assert.True(exit == ExitCode.Ok, output + Environment.NewLine + error);

        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (string line in output.ToString().Split('\n'))
        {
            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 8 && f[0] != "source" && f[0] != "total")
            {
                long N(int i) => long.Parse(f[i].Replace(",", string.Empty), CultureInfo.InvariantCulture);
                rows[f[0]] = new Row(f[0], N(1), N(2), N(3), (int)N(4), N(5));
            }
        }

        var m = new Mismatches();
        foreach (var want in Expected)
        {
            if (!rows.TryGetValue(want.Source, out var got))
            {
                m.Add($"{want.Source}: no row in the decode-all output");
                continue;
            }

            m.Equal(want.Source + " streams", want.Streams, got.Streams);
            m.Equal(want.Source + " compressed bytes", want.Compressed, got.Compressed);
            m.Equal(want.Source + " decompressed bytes", want.Decompressed, got.Decompressed);
            m.Equal(want.Source + " max overrun", want.MaxOvershoot, got.MaxOvershoot);
            m.Equal(want.Source + " failures", want.Failures, got.Failures);
        }

        m.AssertNone("decode-all");
        Assert.Equal(Expected.Length, rows.Count);
    }

    [Fact]
    public void DecodeAllRejectsAnUnknownOption()
    {
        var error = new StringWriter();
        Assert.Equal(ExitCode.Usage, CliApp.Run(["decode-all", "--bogus"], new StringWriter(), error));
        Assert.Contains("unexpected argument", error.ToString());
        Assert.Equal(ExitCode.Usage, CliApp.Run(["no-such-command"], new StringWriter(), error));
    }
}

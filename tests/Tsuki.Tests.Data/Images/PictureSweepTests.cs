using System.Globalization;
using Tsuki.Cli;

namespace Tsuki.Tests.Data.Images;

/// <summary>
/// <c>Tsuki.Cli decode-all --pictures</c> in-process: the smaller sources are swept against the oracle's sample, and a changed SHA-1 is
/// reported with its label and a failing exit code. The full run (35,630 pictures) is the nightly command in docs/status/wave2/A1.md.
/// </summary>
public sealed class PictureSweepTests
{
    private static int Run(out string output, out string error, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int exit = CliApp.Run(args, o, e);
        output = o.ToString();
        error = e.ToString();
        return exit;
    }

    private static long Equal(string output)
    {
        string line = output.Split('\n').Single(l => l.StartsWith("oracle:", StringComparison.Ordinal));
        string tail = line[(line.IndexOf("equal ", StringComparison.Ordinal) + "equal ".Length)..];
        return long.Parse(tail[..tail.IndexOf(',', StringComparison.Ordinal)].Replace(",", string.Empty), CultureInfo.InvariantCulture);
    }

    [GameDataFact]
    public void TheSmallerSourcesSweepEqualToTheOracleSample()
    {
        string tsv = ImageGolden.Locate("sha1/pictures.tsv");
        var rows = ImageGolden.Rows("sha1").Where(r => r.Source is "allpac4" or "parts" or "allscr.mrg" or "ft_en.mrg").ToList();
        Assert.True(rows.Count >= 30, rows.Count.ToString(CultureInfo.InvariantCulture));
        int exit = Run(out string output, out string error, "decode-all", "--pictures", "--game", GameFolder.Require(), "--only", "allpac4,parts,allscr,ft_en", "--expect", tsv);
        Assert.True(exit == ExitCode.Ok, output + error);
        Assert.Equal(rows.Count, Equal(output));
        Assert.Contains("different 0, listed but not decoded 0", output);
    }

    [GameDataFact]
    public void AChangedDigestIsReportedWithItsLabel()
    {
        var row = ImageGolden.Rows("sha1").First(r => r.Source == "parts");
        string temp = Path.Combine(Path.GetTempPath(), "tsuki-a1-" + Guid.NewGuid().ToString("N") + ".tsv");
        try
        {
            // flip the first digit of one digest in a copy of the oracle file
            string[] lines = File.ReadAllLines(ImageGolden.Locate("sha1/pictures.tsv"));
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(row.Label + "\t", StringComparison.Ordinal))
                {
                    lines[i] = lines[i].Replace(row.Sha1, (row.Sha1[0] == '0' ? '1' : '0') + row.Sha1[1..], StringComparison.Ordinal);
                }
            }

            File.WriteAllLines(temp, lines);
            int exit = Run(out string output, out _, "decode-all", "--pictures", "--game", GameFolder.Require(), "--only", "parts", "--expect", temp);
            Assert.Equal(ExitCode.Failed, exit);
            Assert.Contains(row.Label + ": SHA-1 ", output);
            Assert.Contains("different 1,", output);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void ThePictureOptionsNeedPictures()
    {
        Assert.Equal(ExitCode.Usage, Run(out _, out string error, "decode-all", "--game", GameFolder.Path ?? ".", "--expect", "x.tsv"));
        Assert.Contains("need --pictures", error);
        Assert.Equal(ExitCode.Usage, Run(out _, out string error2, "decode-all", "--bogus"));
        Assert.Contains("unexpected argument", error2);
    }
}

using Tsuki.Cli;

namespace Tsuki.Tests.Data.Census;

/// <summary><c>Tsuki.Cli census</c>: the command that prints the counts of census T0 and exits non-zero on a failure.</summary>
public sealed class CensusCommandTests(ITestOutputHelper output)
{
    [GameDataFact]
    public void TheCommandPrintsTheCountsAndExitsZero()
    {
        string refs = Path.Combine(Path.GetTempPath(), "tsuki-k1a-refs-" + Guid.NewGuid().ToString("N") + ".tsv");
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exit = CliApp.Run(["census", "--game", GameFolder.Require(), "--refs", refs, "--sample", "8"], stdout, stderr);
            output.WriteLine(stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.Equal(ExitCode.Ok, exit);
            string text = stdout.ToString();
            Assert.Contains("census T0: 549 scripts", text, StringComparison.Ordinal);
            Assert.Contains("every reference resolves except the allow-list", text, StringComparison.Ordinal);
            Assert.Contains("allow-listed: 103", text, StringComparison.Ordinal);
            Assert.Contains("failures: 0", text, StringComparison.Ordinal);
            Assert.Equal(166_437, File.ReadLines(refs).Count());
        }
        finally
        {
            File.Delete(refs);
        }
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.Equal(ExitCode.Usage, CliApp.Run(["census", "--bogus"], stdout, stderr));
        Assert.Contains("unexpected argument", stderr.ToString(), StringComparison.Ordinal);

        using var stderr2 = new StringWriter();
        string none = Path.Combine(Path.GetTempPath(), "tsuki-no-such-folder-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(ExitCode.Usage, CliApp.Run(["census", "--game", none], stdout, stderr2));
        Assert.Contains("no game folder", stderr2.ToString(), StringComparison.Ordinal);
    }
}

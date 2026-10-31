using System.Diagnostics;
using System.Security.Cryptography;
using Tsuki.Cli;
using Tsuki.Data.IO;
using Tsuki.Data.Verification;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Data;

/// <summary>
/// The build check and <c>Tsuki.Cli verify</c> (RR 3.3, ROADMAP M0 acceptance 2): accepts the user's folder by exact
/// eboot size and SHA-256 and the FORMATS 2 counts; rejects a flipped byte, a missing archive and a corrupted
/// copy in a temp folder, each with its own message.
/// </summary>
public sealed class BuildCheckTests
{
    private static DirectoryGameFiles Real => new(GameFolder.Require());

    [GameDataFact]
    public void TheUsersFolderIsAccepted()
    {
        var report = BuildCheck.Run(Real);
        Assert.True(report.Accepted, BuildReportFormatter.Format(report, verbose: true));
        Assert.Equal(BuildFailure.None, report.PrimaryFailure);
        Assert.NotNull(report.Info);
        Assert.Equal(12_767_802, report.Info.EbootSize);
        Assert.Equal("c2843415fdfda5ebbc84f4d55c6cca044a7476436cf0cfdfccac83116beeb918", report.Info.EbootSha256);
        Assert.True(report.Info.IsAnalysedBuild);
    }

    [GameDataFact]
    public void TheCountsAreTheOnesOfFormatsSection2AndEqualTheOracle()
    {
        var report = BuildCheck.Run(Real);
        var c = report.Counts;
        Assert.Equal(12, c.TripletEntries.Count);
        Assert.Equal(49_620, c.TripletTotal);
        Assert.Equal(8, c.StandaloneSections.Count);
        Assert.Equal(891, c.StandaloneTotal);
        Assert.Equal(552, c.StandaloneSections["allscr.mrg"]);
        Assert.Equal(549, c.Scripts);
        Assert.All(new[] { "ja", "en", "zc", "zt", "ko" }, l => Assert.Equal(43_969, c.TextLines[l]));
        Assert.Equal(16_464, c.Voices);
        Assert.Equal(134, c.MovieFiles);
        Assert.Equal(1_327, c.VseNames);

        using var doc = GoldenData.Json("buildcheck.json");
        var root = doc.RootElement;
        Assert.Equal(root.GetProperty("eboot").GetProperty("size").GetInt64(), report.Info!.EbootSize);
        Assert.Equal(root.GetProperty("eboot").GetProperty("sha256").GetString(), report.Info.EbootSha256);
        Assert.Equal(root.GetProperty("triplet_total").GetInt32(), c.TripletTotal);
        Assert.Equal(root.GetProperty("standalone_total").GetInt32(), c.StandaloneTotal);
        Assert.Equal(root.GetProperty("scripts").GetInt32(), c.Scripts);
        Assert.Equal(root.GetProperty("voices").GetInt32(), c.Voices);
        Assert.Equal(root.GetProperty("movie_mp4").GetInt32(), c.MovieFiles);
        foreach (var t in root.GetProperty("triplets").EnumerateObject())
        {
            Assert.Equal(t.Value.GetProperty("entries").GetInt32(), c.TripletEntries[t.Name]);
        }

        foreach (var s in root.GetProperty("standalone").EnumerateObject())
        {
            Assert.Equal(s.Value.GetInt32(), c.StandaloneSections[s.Name]);
        }

        foreach (var l in root.GetProperty("text_lines").EnumerateObject())
        {
            Assert.Equal(l.Value.GetInt32(), c.TextLines[l.Name]);
        }
    }

    [GameDataFact]
    public void TheBuildCheckRunsInWellUnderASecond()
    {
        BuildCheck.Run(Real); // warm-up: JIT
        var sw = Stopwatch.StartNew();
        var report = BuildCheck.Run(Real);
        sw.Stop();
        Assert.True(report.Accepted);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"{sw.ElapsedMilliseconds} ms");
    }

    [GameDataFact]
    public void AnEbootWithOneFlippedByteIsRejectedAsAHashMismatch()
    {
        var view = new OverlayGameFiles(Real).FlipByte("eboot.bin", 0x123456);
        var report = BuildCheck.Run(view);
        Assert.False(report.Accepted);
        Assert.Equal(BuildFailure.EbootHashMismatch, report.PrimaryFailure);
        var failure = Assert.Single(report.Failures);
        Assert.Equal("eboot.sha256", failure.Id);
        Assert.Contains("size matches but its content differs", failure.Message);
        // the size and the probes still pass: only the identity hash tells the copies apart
        Assert.Contains(report.Checks, c => c.Id == "eboot.size" && c.Passed);
        Assert.Contains(report.Checks, c => c.Id == "eboot.probe.self" && c.Passed);
        Assert.False(report.Info!.IsAnalysedBuild);
    }

    [GameDataFact]
    public void ATruncatedEbootIsRejectedAsASizeMismatch()
    {
        byte[] eboot = Real.ReadAll("eboot.bin");
        var view = new OverlayGameFiles(Real).Replace("eboot.bin", eboot[..^1]);
        var report = BuildCheck.Run(view);
        Assert.Equal(BuildFailure.EbootSizeMismatch, report.PrimaryFailure);
        Assert.Contains(report.Failures, f => f.Id == "eboot.size" && f.Message.Contains("12,767,801"));
        Assert.Contains(report.Failures, f => f.Id == "eboot.sha256" && f.Message.Contains("another build"));
    }

    [GameDataFact]
    public void AFolderWithOneArchiveMissingIsRejectedAsAMissingFile()
    {
        var view = new OverlayGameFiles(Real).Hide("image/bgm.hed");
        var report = BuildCheck.Run(view);
        Assert.False(report.Accepted);
        Assert.Equal(BuildFailure.FileMissing, report.PrimaryFailure);
        var failure = report.Failures.First();
        Assert.Equal("data.bgm.files", failure.Id);
        Assert.Contains("bgm.hed", failure.Message);
        Assert.Contains("missing", failure.Message);
        // the eboot is fine: the two rejections are told apart by their kind and their text
        Assert.Contains(report.Checks, c => c.Id == "eboot.sha256" && c.Passed);
        var flipped = BuildCheck.Run(new OverlayGameFiles(Real).FlipByte("eboot.bin", 5));
        Assert.NotEqual(flipped.PrimaryFailure, report.PrimaryFailure);
        Assert.NotEqual(flipped.Failures.First().Message, failure.Message);
    }

    [GameDataFact]
    public void AnArchiveWithAnotherEntryCountIsRejectedAsACountMismatch()
    {
        // drop the last HED8 entry of parts.hed (the 16-byte terminator stays): 23 entries instead of 24
        byte[] hed = Real.ReadAll("image/parts.hed");
        byte[] cut = [.. hed[..(8 * 23)], .. hed[(8 * 24)..]];
        var report = BuildCheck.Run(new OverlayGameFiles(Real).Replace("image/parts.hed", cut));
        Assert.False(report.Accepted);
        Assert.Contains(report.Failures, f => f.Id == "data.parts.count" && f.Failure == BuildFailure.CountMismatch);
        Assert.Contains(report.Failures, f => f.Id == "data.triplets.total");
    }

    [GameDataFact]
    public void ACorruptedCopyInATempFolderIsRejected()
    {
        // ROADMAP M0 acceptance 2: "an eboot with one flipped byte". Here as a real copy on disk, in a temp
        // folder of its own (the game folder is never written).
        string temp = Path.Combine(Path.GetTempPath(), "tsuki-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            byte[] eboot = Real.ReadAll("eboot.bin");
            eboot[eboot.Length / 2] ^= 0x80;
            File.WriteAllBytes(Path.Combine(temp, "eboot.bin"), eboot);

            var report = BuildCheck.Run(temp);
            Assert.False(report.Accepted);
            Assert.Equal(BuildFailure.EbootHashMismatch, report.PrimaryFailure);
            Assert.Contains(report.Failures, f => f.Id == "eboot.sha256");
            Assert.Contains(report.Failures, f => f.Failure == BuildFailure.FileMissing && f.Id == "data.allpac0.files");
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(eboot)), report.Info!.EbootSha256);

            var output = new StringWriter();
            var error = new StringWriter();
            int exit = CliApp.Run(["verify", "--game", temp], output, error);
            Assert.Equal(ExitCode.Failed, exit);
            Assert.Contains("RESULT: REJECTED (EbootHashMismatch)", output.ToString());
            Assert.Contains("SHA-256", output.ToString());
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [GameDataFact]
    public void VerifyAcceptsTheUsersFolderAndPrintsTheCounts()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = CliApp.Run(["verify", "--game", GameFolder.Require()], output, error);
        string text = output.ToString();
        Assert.Equal(ExitCode.Ok, exit);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("RESULT: ACCEPTED", text);
        Assert.Contains("c2843415fdfda5ebbc84f4d55c6cca044a7476436cf0cfdfccac83116beeb918", text);
        Assert.Contains("49,620", text);
        Assert.Contains("891", text);
        Assert.Contains("552", text);
        Assert.Contains("43,969", text);
        Assert.Contains("16,464", text);
        Assert.Contains("134", text);
    }

    [GameDataFact]
    public void VerifyRejectsAMissingFolderAsAUsageError()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = CliApp.Run(["verify", "--game", Path.Combine(Path.GetTempPath(), "tsuki-no-such-folder-" + Guid.NewGuid().ToString("N"))], output, error);
        Assert.Equal(ExitCode.Usage, exit);
        Assert.Contains("does not exist", error.ToString());
    }
}

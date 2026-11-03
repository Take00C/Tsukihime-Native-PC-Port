using System.Formats.Tar;
using System.IO.Compression;
using Tsuki.Cli;
using Tsuki.Cli.Commands;
using Tsuki.Data.Icons;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Data.Cli;

/// <summary>
/// Package K40: <c>Tsuki.Cli release-sums</c> writes the SHA256SUMS.txt that the launcher's update button checks the release zip against, and refuses a zip the
/// update would refuse. No game data is needed: the zips are made of copies of the Tsukihime.exe and Tsukihime.dll built next to these tests. The file it writes is
/// read back with the launcher's own reader (src/Tsuki.Launcher/Core/ReleaseSums.cs, compiled into these tests).
/// </summary>
public sealed class ReleaseSumsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k40-sums-" + Guid.NewGuid().ToString("N"));
    private readonly string _version;

    public ReleaseSumsTests()
    {
        Directory.CreateDirectory(_root);
        string info = PeIcons.Marks(Path.Combine(AppContext.BaseDirectory, "Tsukihime.dll"))!.InformationalVersion!;
        _version = info.Split('+')[0];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A publish-like folder: both programs and TsukihimeLauncher.dll (a copy of Tsukihime.dll, so it carries this build's version), zipped by contents.</summary>
    private string ReleaseZip(string name, Action<string>? change = null, bool wrapped = false)
    {
        string folder = Path.Combine(_root, Path.GetFileNameWithoutExtension(name) + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(folder, "licenses"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Tsukihime.exe"), Path.Combine(folder, "Tsukihime.exe"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Tsukihime.exe"), Path.Combine(folder, "TsukihimeLauncher.exe"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Tsukihime.dll"), Path.Combine(folder, "TsukihimeLauncher.dll"));
        File.WriteAllText(Path.Combine(folder, "licenses", "LICENSE.txt"), "licence");
        change?.Invoke(folder);
        string zip = Path.Combine(_root, "z-" + Guid.NewGuid().ToString("N")[..8], name);
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Fastest, includeBaseDirectory: wrapped);
        return zip;
    }

    /// <summary>The port's crescent .ico (assets/icon/tsukihime.ico), which both programs in a release zip must carry.</summary>
    private static byte[] Crescent { get; } = File.ReadAllBytes(Path.Combine(RepoRoot(), "assets", "icon", "tsukihime.ico"));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Tsuki.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exit = CliApp.Run(["release-sums", .. args], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void TheChecksumFileListsTheZipAndTheLauncherAcceptsIt()
    {
        string zip = ReleaseZip($"Tsukihime-v{_version}-win-x64.zip");
        string extra = Path.Combine(_root, "ffmpeg-n9.0.1-source.tar.xz");
        File.WriteAllText(extra, "a source archive");
        (int exit, string output, string error) = Run(zip, extra);
        Assert.Equal(ExitCode.Ok, exit);
        Assert.Equal(string.Empty, error);
        string sums = Path.Combine(Path.GetDirectoryName(zip)!, ReleaseSumsCommand.SumsFileName);
        Assert.Contains("RESULT: wrote " + sums, output, StringComparison.Ordinal);
        string text = File.ReadAllText(sums);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.Equal($"{ReleaseSums.Sha256Hex(zip)}  {Path.GetFileName(zip)}\n{ReleaseSums.Sha256Hex(extra)}  {Path.GetFileName(extra)}\n", text);

        // the launcher's reader agrees: the zip matches, a changed zip does not
        Assert.Equal(SumCheck.Match, ReleaseSums.Check(text, Path.GetFileName(zip), ReleaseSums.Sha256Hex(zip)));
        File.AppendAllText(zip, "x");
        Assert.Equal(SumCheck.Mismatch, ReleaseSums.Check(text, Path.GetFileName(zip), ReleaseSums.Sha256Hex(zip)));

        // --out puts it elsewhere
        string other = Path.Combine(_root, "out", "SHA256SUMS.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        Assert.Equal(ExitCode.Ok, Run(extra, "--out", other).Exit);
        Assert.StartsWith(ReleaseSums.Sha256Hex(extra), File.ReadAllText(other), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheReadmesLogoPassesInAZipWithOrWithoutItsOneFolder(bool wrapped)
    {
        // K46: the release steps zip the publish folder itself (Compress-Archive -Path folder), so the logo is Tsukihime-vX.Y.Z-win-x64/assets/branding/...
        string zip = ReleaseZip($"Tsukihime-v{_version}-win-x64.zip", f =>
        {
            Directory.CreateDirectory(Path.Combine(f, "assets", "branding"));
            File.WriteAllBytes(Path.Combine(f, "assets", "branding", "tsukihime-logo.png"), [0x89, 0x50, 0x4E, 0x47]);
        }, wrapped);
        Assert.Empty(ReleaseSumsCommand.CheckReleaseZip(zip, Crescent));
    }

    [Fact]
    public void TheChecksumFileListsTheWindowsZipAndTheLinuxArchiveAndTheLauncherReadsOnlyItsZipsLine()
    {
        // package K46: one SHA256SUMS.txt for both assets; the Windows launcher's update looks up its zip by name and never takes the archive's line for it
        string zip = ReleaseZip($"Tsukihime-v{_version}-win-x64.zip");
        string tar = LinuxRelease.Tar(LinuxRelease.Folder(_root, "linux-" + Guid.NewGuid().ToString("N")[..6]), Path.Combine(Path.GetDirectoryName(zip)!, $"Tsukihime-v{_version}-linux-x64.tar.gz"));
        Assert.Empty(ReleaseSumsCommand.CheckReleaseTar(tar, Crescent));
        (int exit, string output, string error) = Run(zip, tar);
        Assert.True(exit == ExitCode.Ok, output + error);
        string text = File.ReadAllText(Path.Combine(Path.GetDirectoryName(zip)!, ReleaseSumsCommand.SumsFileName));
        string zipHex = ReleaseSums.Sha256Hex(zip);
        string tarHex = ReleaseSums.Sha256Hex(tar);
        Assert.Equal($"{zipHex}  {Path.GetFileName(zip)}\n{tarHex}  {Path.GetFileName(tar)}\n", text);
        Assert.DoesNotContain("no Tsukihime-vX.Y.Z-win-x64.zip", output, StringComparison.Ordinal);

        IReadOnlyDictionary<string, string> map = ReleaseSums.Parse(text);
        Assert.Equal(2, map.Count);
        Assert.True(ReleaseVersion.TryParse(_version, out ReleaseVersion parsed));
        Assert.Equal(SumCheck.Match, ReleaseSums.Check(text, UpdateSource.ZipAssetName(parsed), zipHex));          // the name the launcher looks up
        Assert.Equal(SumCheck.Mismatch, ReleaseSums.Check(text, Path.GetFileName(zip), tarHex));        // the archive's checksum never passes for the zip
        Assert.Equal(SumCheck.Match, ReleaseSums.Check(text, Path.GetFileName(tar), tarHex));

        // the list in the other order reads the same
        string reversed = $"{tarHex}  {Path.GetFileName(tar)}\n{zipHex}  {Path.GetFileName(zip)}\n";
        Assert.Equal(SumCheck.Match, ReleaseSums.Check(reversed, Path.GetFileName(zip), zipHex));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("mode")]
    [InlineData("two folders")]
    [InlineData("link")]
    [InlineData("picture")]
    [InlineData("gpl")]
    [InlineData("no library")]
    [InlineData("framework-dependent")]
    [InlineData("version")]
    [InlineData("personal")]
    public void ALinuxArchiveThatIsNotARightReleaseGetsNoChecksumFile(string what)
    {
        string folder = LinuxRelease.Folder(_root, "linux-" + Guid.NewGuid().ToString("N")[..6]);
        string dir = Path.Combine(_root, "t-" + Guid.NewGuid().ToString("N")[..8]);
        string name = $"Tsukihime-v{_version}-linux-x64.tar.gz";
        string tar;
        string expected;
        switch (what)
        {
            case "name":
                tar = LinuxRelease.Tar(folder, Path.Combine(dir, $"Tsukihime-v{_version}-linux.tar.gz"));
                expected = "a Linux release archive must be named Tsukihime-vX.Y.Z-linux-x64.tar.gz";
                break;
            case "mode":
                tar = LinuxRelease.Tar(folder, Path.Combine(dir, name), (rel, m) => rel == "libavcodec.so.63" ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead : m);
                expected = "libavcodec.so.63 has the mode 644, not 755";
                break;
            case "two folders":
                tar = LinuxRelease.Tar(folder, Path.Combine(dir, name), extra: (w, _) => w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "elsewhere/README.md") { DataStream = new MemoryStream([1]) }));
                expected = "the entry elsewhere/README.md is not inside the one folder";
                break;
            case "link":
                tar = LinuxRelease.Tar(folder, Path.Combine(dir, name), extra: (w, root) => w.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, root + "/libavcodec.so") { LinkName = "libavcodec.so.63" }));
                expected = "Tsukihime-v";
                break;
            case "picture":
                Directory.CreateDirectory(Path.Combine(folder, "assets", "branding"));
                File.WriteAllBytes(Path.Combine(folder, "assets", "branding", "tsukihime-cg.png"), [0x89, 0x50, 0x4E, 0x47]);
                tar = LinuxRelease.Tar(folder, Path.Combine(dir, name));
                expected = "assets/branding/tsukihime-cg.png is a picture";
                break;
            case "gpl":
                File.WriteAllBytes(Path.Combine(folder, "libavcodec.so.63"), LinuxRelease.Ffmpeg("libavcodec.so.63", LinuxRelease.LgplConfiguration + " --enable-gpl"));
                tar = LinuxRelease.Tar(folder, Path.Combine(dir, name));
                expected = "libavcodec.so.63 was built with --enable-gpl or --enable-nonfree";
                break;
            case "no library":
                File.Delete(Path.Combine(folder, "libswresample.so.7"));
                tar = LinuxRelease.Tar(folder, Path.Combine(dir, name));
                expected = "no libswresample.so.7 in";
                break;
            case "framework-dependent":
                File.Delete(Path.Combine(folder, "libhostfxr.so"));
                tar = LinuxRelease.Tar(folder, Path.Combine(dir, name));
                expected = "no libhostfxr.so in";
                break;
            case "version":
                tar = LinuxRelease.Tar(folder, Path.Combine(dir, "Tsukihime-v9.9.9-linux-x64.tar.gz"));
                expected = $"Tsukihime.dll says version {_version}";
                break;
            default:
                File.WriteAllText(Path.Combine(folder, ReleaseCheck.PersonalNote), "PERSONAL");
                tar = LinuxRelease.Tar(folder, Path.Combine(dir, name));
                expected = "holds PERSONAL_BUILD.txt";
                break;
        }

        IReadOnlyList<string> problems = ReleaseSumsCommand.CheckReleaseTar(tar, Crescent);
        Assert.Contains(problems, p => p.Contains(expected, StringComparison.Ordinal));
        if (what == "link")
        {
            Assert.Contains(problems, p => p.Contains("is a SymbolicLink entry", StringComparison.Ordinal));
        }

        (int exit, string output, _) = Run(tar);
        Assert.Equal(ExitCode.Failed, exit);
        Assert.Contains("RESULT: SHA256SUMS.txt was NOT written", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(dir, ReleaseSumsCommand.SumsFileName)));
    }

    [Fact]
    public void AZipAndAnArchiveOfDifferentVersionsOrAZipWithAPictureGetNoChecksumFile()
    {
        string zip = ReleaseZip($"Tsukihime-v{_version}-win-x64.zip");
        string linux = LinuxRelease.Folder(_root, "linux-" + Guid.NewGuid().ToString("N")[..6]);
        string tar = LinuxRelease.Tar(linux, Path.Combine(_root, "other", "Tsukihime-v0.0.1-linux-x64.tar.gz"));
        (int exit, string output, _) = Run(zip, tar);
        Assert.Equal(ExitCode.Failed, exit);
        Assert.Contains($"the release files name different versions (0.0.1, {_version})", output, StringComparison.Ordinal);

        string picture = ReleaseZip($"Tsukihime-v{_version}-win-x64.zip", f =>
        {
            Directory.CreateDirectory(Path.Combine(f, "assets", "branding"));
            File.WriteAllBytes(Path.Combine(f, "assets", "branding", "tsukihime-cg.png"), [0x89, 0x50, 0x4E, 0x47]);
        });
        (exit, output, _) = Run(picture);
        Assert.Equal(ExitCode.Failed, exit);
        Assert.Contains("assets/branding/tsukihime-cg.png is a picture", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AZipTheUpdateWouldRefuseGetsNoChecksumFile()
    {
        string badName = ReleaseZip("Tsukihime-PC-port-v0.1.4-win-x64.zip");
        (int exit, string output, _) = Run(badName);
        Assert.Equal(ExitCode.Failed, exit);
        Assert.Contains("must be named Tsukihime-vX.Y.Z-win-x64.zip", output, StringComparison.Ordinal);
        Assert.Contains("RESULT: SHA256SUMS.txt was NOT written", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(badName)!, ReleaseSumsCommand.SumsFileName)));

        (exit, output, _) = Run(ReleaseZip("Tsukihime-v9.9.9-win-x64.zip"));
        Assert.Equal(ExitCode.Failed, exit);
        Assert.Contains($"TsukihimeLauncher.dll says version {_version}", output, StringComparison.Ordinal);
        Assert.Contains("<Version>9.9.9</Version>", output, StringComparison.Ordinal);

        (exit, output, _) = Run(ReleaseZip($"Tsukihime-v{_version}-win-x64.zip", f => File.WriteAllText(Path.Combine(f, "PERSONAL_BUILD.txt"), "PERSONAL")));
        Assert.Equal(ExitCode.Failed, exit);
        Assert.Contains("a personal build is never released", output, StringComparison.Ordinal);

        (exit, output, _) = Run(ReleaseZip($"Tsukihime-v{_version}-win-x64.zip", f => File.Delete(Path.Combine(f, "TsukihimeLauncher.exe"))));
        Assert.Equal(ExitCode.Failed, exit);
        Assert.Contains("no TsukihimeLauncher.exe at the top of the zip", output, StringComparison.Ordinal);

        string slip = ReleaseZip($"Tsukihime-v{_version}-win-x64.zip");
        using (ZipArchive z = ZipFile.Open(slip, ZipArchiveMode.Update))
        {
            using var w = new StreamWriter(z.CreateEntry("../evil.txt").Open());
            w.Write("x");
        }

        (exit, output, _) = Run(slip);
        Assert.Equal(ExitCode.Failed, exit);
        Assert.Contains("would land outside the folder", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(slip)!, ReleaseSumsCommand.SumsFileName)));

        Assert.Equal(ExitCode.Usage, Run().Exit);
        Assert.Equal(ExitCode.Usage, Run(Path.Combine(_root, "missing.zip")).Exit);
        Assert.Equal(ExitCode.Usage, Run("--nonsense").Exit);
    }

    [Fact]
    public void AZipWhoseFilesSitInOneFolderIsAcceptedAsTheLauncherAcceptsIt()
    {
        string folder = Path.Combine(_root, "wrapped");
        string inner = Path.Combine(folder, $"Tsukihime-v{_version}-win-x64");
        Directory.CreateDirectory(inner);
        foreach ((string from, string to) in new[] { ("Tsukihime.exe", "Tsukihime.exe"), ("Tsukihime.exe", "TsukihimeLauncher.exe"), ("Tsukihime.dll", "TsukihimeLauncher.dll") })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, from), Path.Combine(inner, to));
        }

        string zip = Path.Combine(_root, "wrapped-zip", $"Tsukihime-v{_version}-win-x64.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
        Assert.Empty(ReleaseSumsCommand.CheckReleaseZip(zip, Crescent));
    }

    [Fact]
    public void AZipMadeFromAFolderTheLauncherStampedGetsNoChecksumFile()
    {
        // package K43, review: a folder whose launcher has run carries Arcueid in its programs and none of the personal marks; release-sums sees the zip itself
        var face = new IconChoice("a made-up face", IcoFile.Read(FaceIcon.EncodeIco([.. FaceIcon.Sizes.Select(size => new IconFrame(size, [.. Enumerable.Range(0, size * size * 4).Select(i => (byte)(i * 13))]))]))!);
        var crescent = new IconChoice("the port's crescent", IcoFile.Read(Crescent)!);
        string stamped = ReleaseZip($"Tsukihime-v{_version}-win-x64.zip", f => Assert.Equal(StampOutcome.Stamped, ExeIconStamp.StampExe(Path.Combine(f, "TsukihimeLauncher.exe"), face, [crescent, face]).Outcome));
        (int exit, string output, _) = Run(stamped);
        Assert.Equal(ExitCode.Failed, exit);
        Assert.Contains("TsukihimeLauncher.exe carries an icon that is not the port's crescent (7 frames", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Tsukihime.exe carries", output, StringComparison.Ordinal);
        Assert.Contains("RESULT: SHA256SUMS.txt was NOT written", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(stamped)!, ReleaseSumsCommand.SumsFileName)));

        // a copy the stamp leaves beside the programs means the same, even when the programs carry the crescent again
        string leftover = ReleaseZip($"Tsukihime-v{_version}-win-x64.zip", f => File.Copy(Path.Combine(f, "TsukihimeLauncher.exe"), Path.Combine(f, "TsukihimeLauncher.exe" + ExeIconStamp.OldSuffix)));
        (exit, output, _) = Run(leftover);
        Assert.Equal(ExitCode.Failed, exit);
        Assert.Contains("holds TsukihimeLauncher.exe.icon-old, a copy the launcher leaves when it writes the program icons", output, StringComparison.Ordinal);

        // the crescent is compared with the file --icon names: another icon there refuses the plain build
        string plain = ReleaseZip($"Tsukihime-v{_version}-win-x64.zip");
        string other = Path.Combine(_root, "other.ico");
        File.WriteAllBytes(other, FaceIcon.EncodeIco([new IconFrame(16, new byte[16 * 16 * 4])]));
        Assert.Equal(ExitCode.Failed, Run(plain, "--icon", other).Exit);
        Assert.Equal(ExitCode.Ok, Run(plain, "--icon", Path.Combine(RepoRoot(), "assets", "icon", "tsukihime.ico")).Exit);
        Assert.Empty(ReleaseSumsCommand.CheckReleaseZip(plain, Crescent));
    }
}

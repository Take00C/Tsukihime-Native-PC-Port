using System.Security.Cryptography;
using System.Text;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>Package K40 (issue #6): the addresses an update may come from, the checksum file, the zip's entries (zip-slip) and the check of an unpacked release.</summary>
public sealed class UpdateSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-k40-safety-" + Guid.NewGuid().ToString("N"));

    public UpdateSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // -- hosts --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://api.github.com/repos/Take00C/Tsukihime-Native-PC-Port/releases?per_page=20")]
    [InlineData("https://github.com/Take00C/Tsukihime-Native-PC-Port/releases/download/v0.1.4/Tsukihime-v0.1.4-win-x64.zip")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/1/2?X-Amz-Algorithm=AWS4")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2")]
    [InlineData("HTTPS://GitHub.COM/x")]
    public void GitHubAndItsFileHostsAreAllowedOverHttps(string url)
    {
        Assert.Null(HostPolicy.GitHub.Problem(new Uri(url)));
    }

    [Theory]
    [InlineData("http://github.com/x", "only HTTPS")]
    [InlineData("http://objects.githubusercontent.com/x", "only HTTPS")]
    [InlineData("https://github.com.evil.example/x", "not a GitHub address")]
    [InlineData("https://evilgithub.com/x", "not a GitHub address")]
    [InlineData("https://githubusercontent.com/x", "not a GitHub address")]
    [InlineData("https://objects.githubusercontent.com.evil.example/x", "not a GitHub address")]
    [InlineData("https://gist.github.com/x", "not a GitHub address")]
    [InlineData("https://github.com:8443/x", "default HTTPS port")]
    [InlineData("https://user:pass@github.com/x", "user name")]
    [InlineData("https://127.0.0.1/x", "not a GitHub address")]
    [InlineData("ftp://github.com/x", "only HTTPS")]
    [InlineData("file:///C:/Windows/notepad.exe", "only HTTPS")]
    public void EverythingElseIsRefused(string url, string why)
    {
        Assert.Contains(why, HostPolicy.GitHub.Problem(new Uri(url)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLoopbackPolicyOfTheTestsAdmitsTheLoopbackAddressOnly()
    {
        Assert.True(HostPolicy.Loopback.Allows(new Uri("http://127.0.0.1:5000/repos/x")));
        Assert.True(HostPolicy.Loopback.Allows(new Uri("http://localhost:5000/")));
        Assert.True(HostPolicy.Loopback.Allows(new Uri("http://[::1]:5000/")));
        Assert.False(HostPolicy.Loopback.Allows(new Uri("https://github.com/x")));
        Assert.False(HostPolicy.Loopback.Allows(new Uri("http://10.0.0.5/x")));
        Assert.False(HostPolicy.Loopback.Allows(new Uri("http://127.0.0.1.evil.example/x")));
        Assert.Throws<ArgumentException>(() => UpdateSource.ForLoopback(new Uri("https://api.github.com/")));

        UpdateSource loop = UpdateSource.ForLoopback(new Uri("http://127.0.0.1:5000"));
        Assert.Equal("http://127.0.0.1:5000/repos/Take00C/Tsukihime-Native-PC-Port/releases?per_page=20", loop.ReleasesUri.AbsoluteUri);
        Assert.Equal("https://api.github.com/repos/Take00C/Tsukihime-Native-PC-Port/releases?per_page=20", UpdateSource.GitHub.ReleasesUri.AbsoluteUri);
        Assert.Equal("https://github.com/Take00C/Tsukihime-Native-PC-Port/releases", UpdateSource.GitHub.ReleasesPage);
        Assert.Same(HostPolicy.GitHub, UpdateSource.GitHub.Policy);
    }

    // -- SHA256SUMS.txt -----------------------------------------------------------------------------------------------

    private const string Abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"; // SHA-256 of "abc"

    [Fact]
    public void TheChecksumFileIsReadInSha256sumsFormats()
    {
        string text = "\uFEFF# checksums of v0.1.4\r\n" +
            $"{Abc.ToUpperInvariant()}  Tsukihime-v0.1.4-win-x64.zip\r\n" +
            $"{Abc} *ffmpeg-source.tar.xz\n" +
            $"{Abc}  ./dot-slash.zip\n" +
            "not a line\n" +
            $"{Abc[..63]}  short.zip\n" +
            $"{Abc} one-space.zip\n";
        IReadOnlyDictionary<string, string> map = ReleaseSums.Parse(text);
        Assert.Equal(3, map.Count);
        Assert.Equal(Abc, map["Tsukihime-v0.1.4-win-x64.zip"]);
        Assert.Equal(Abc, map["ffmpeg-source.tar.xz"]);
        Assert.Equal(Abc, map["dot-slash.zip"]);
        Assert.Equal($"{Abc}  x.zip", ReleaseSums.FormatLine(Abc.ToUpperInvariant(), "x.zip"));
    }

    [Fact]
    public async Task ADownloadMatchesMismatchesOrIsMissing()
    {
        string file = Path.Combine(_dir, "Tsukihime-v0.1.4-win-x64.zip");
        File.WriteAllBytes(file, Encoding.ASCII.GetBytes("abc"));
        string hex = ReleaseSums.Sha256Hex(file);
        Assert.Equal(Abc, hex);
        Assert.Equal(Abc, await ReleaseSums.Sha256HexAsync(file, TestContext.Current.CancellationToken));
        Assert.Equal(SumCheck.Match, ReleaseSums.Check($"{Abc}  Tsukihime-v0.1.4-win-x64.zip\n", "Tsukihime-v0.1.4-win-x64.zip", hex));
        Assert.Equal(SumCheck.Match, ReleaseSums.Check($"{Abc.ToUpperInvariant()}  Tsukihime-v0.1.4-win-x64.zip\n", "Tsukihime-v0.1.4-win-x64.zip", hex));
        string other = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("abd")));
        Assert.Equal(SumCheck.Mismatch, ReleaseSums.Check($"{other}  Tsukihime-v0.1.4-win-x64.zip\n", "Tsukihime-v0.1.4-win-x64.zip", hex));
        Assert.Equal(SumCheck.Missing, ReleaseSums.Check($"{Abc}  Tsukihime-v0.1.3-win-x64.zip\n", "Tsukihime-v0.1.4-win-x64.zip", hex));
        Assert.Equal(SumCheck.Missing, ReleaseSums.Check(string.Empty, "Tsukihime-v0.1.4-win-x64.zip", hex));
        Assert.Equal(SumCheck.Missing, ReleaseSums.Check("<html>Not Found</html>", "Tsukihime-v0.1.4-win-x64.zip", hex));
    }

    // -- zip entries --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("licenses/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("a\\..\\..\\evil.txt")]
    [InlineData("/etc/evil")]
    [InlineData("\\Windows\\evil.dll")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("C:evil.dll")]
    [InlineData("Tsukihime.exe:stream")]
    [InlineData("CON")]
    [InlineData("licenses/nul.txt")]
    [InlineData("TsukihimeLauncher.exe.")]
    [InlineData("TsukihimeLauncher.exe ")]
    [InlineData("a//b.txt")]
    [InlineData("./x.txt")]
    [InlineData("")]
    public void EntriesThatCouldLeaveTheFolderAreRefused(string name)
    {
        Assert.NotNull(UpdateInstaller.EntryProblem(name));
    }

    [Theory]
    [InlineData("TsukihimeLauncher.exe")]
    [InlineData("licenses/FFmpeg-LGPL.txt")]
    [InlineData("licenses\\THIRD_PARTY.md")]
    [InlineData("runtimes/win-x64/native/SDL3.dll")]
    [InlineData("licenses/")]
    [InlineData("Tsukihime-v0.1.4/Tsukihime.exe")]
    [InlineData("console.dll")]
    public void OrdinaryEntriesAreFine(string name)
    {
        Assert.Null(UpdateInstaller.EntryProblem(name));
    }

    [Fact]
    public void AZipSlipZipIsRefusedWholeAndNothingLandsOutsideTheStagingFolder()
    {
        string staging = Path.Combine(_dir, "app", "update-staging", "files");
        string zip = ReleaseFiles.RawZip(Path.Combine(_dir, "slip.zip"), ("TsukihimeLauncher.exe", "x"), ("../../evil.txt", "owned"), ("licenses/ok.txt", "y"));
        var e = Assert.Throws<UpdateRefusedException>(() => UpdateInstaller.Extract(zip, staging, TestContext.Current.CancellationToken));
        Assert.Contains("../../evil.txt", e.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_dir, "app", "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.False(Directory.Exists(staging), "the half-unpacked staging folder is removed");

        string twice = ReleaseFiles.RawZip(Path.Combine(_dir, "twice.zip"), ("a.txt", "1"), ("A.TXT", "2"));
        Assert.Contains("twice", Assert.Throws<UpdateRefusedException>(() => UpdateInstaller.Extract(twice, staging, TestContext.Current.CancellationToken)).Message, StringComparison.Ordinal);

        string broken = Path.Combine(_dir, "broken.zip");
        File.WriteAllText(broken, "PK not really a zip");
        Assert.Contains("cannot be read", Assert.Throws<UpdateRefusedException>(() => UpdateInstaller.Extract(broken, staging, TestContext.Current.CancellationToken)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AGoodZipIsUnpackedWithItsFolders()
    {
        string release = ReleaseFiles.Folder(_dir, "release", "new");
        string zip = ReleaseFiles.Zip(release, Path.Combine(_dir, "Tsukihime-v0.1.4-win-x64.zip"));
        string staging = Path.Combine(_dir, "staging");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "stale.txt"), "from an earlier try");
        IReadOnlyList<string> files = UpdateInstaller.Extract(zip, staging, TestContext.Current.CancellationToken);
        Assert.Equal(["Tsukihime.exe", "TsukihimeLauncher.dll", "TsukihimeLauncher.exe", Path.Combine("licenses", "LICENSE.txt"), "marker.txt"], files.Order(StringComparer.Ordinal));
        Assert.Equal("licence new", File.ReadAllText(Path.Combine(staging, "licenses", "LICENSE.txt")));
        Assert.False(File.Exists(Path.Combine(staging, "stale.txt")), "the staging folder is emptied first");
    }

    // -- the unpacked release -----------------------------------------------------------------------------------------

    [Fact]
    public void AnUnpackedReleaseMustBeThePortAtTheVersionOfItsTag()
    {
        ReleaseVersion version = ReleaseFiles.BuildVersion();
        string good = ReleaseFiles.Folder(_dir, "good", "new");
        StagedRelease staged = UpdateInstaller.Check(good, version);
        Assert.Equal(Path.GetFullPath(good), staged.Root);
        Assert.Contains("TsukihimeLauncher.exe", staged.Files);
        Assert.StartsWith(version.ToString(), staged.LauncherVersion, StringComparison.Ordinal);

        // the release is labelled with another version than its launcher carries: refused (it would offer itself again after the restart)
        var other = new ReleaseVersion(version.Major, version.Minor, version.Patch + 1);
        Assert.Contains($"labelled {other.Tag}", Assert.Throws<UpdateRefusedException>(() => UpdateInstaller.Check(good, other)).Message, StringComparison.Ordinal);

        string noGame = ReleaseFiles.Folder(_dir, "nogame", "x");
        File.Delete(Path.Combine(noGame, "Tsukihime.exe"));
        Assert.Contains("no Tsukihime.exe", Assert.Throws<UpdateRefusedException>(() => UpdateInstaller.Check(noGame, version)).Message, StringComparison.Ordinal);

        string noLauncher = ReleaseFiles.Folder(_dir, "nolauncher", "x");
        File.Delete(Path.Combine(noLauncher, "TsukihimeLauncher.exe"));
        Assert.Contains("no TsukihimeLauncher.exe", Assert.Throws<UpdateRefusedException>(() => UpdateInstaller.Check(noLauncher, version)).Message, StringComparison.Ordinal);

        string personal = ReleaseFiles.Folder(_dir, "personal", "x");
        File.WriteAllText(Path.Combine(personal, "licenses", "PERSONAL_BUILD.txt"), "PERSONAL BUILD - DO NOT SHARE");
        Assert.Contains("personal build", Assert.Throws<UpdateRefusedException>(() => UpdateInstaller.Check(personal, version)).Message, StringComparison.Ordinal);

        string notManaged = ReleaseFiles.Folder(_dir, "notmanaged", "x");
        File.WriteAllText(Path.Combine(notManaged, "TsukihimeLauncher.dll"), "not a PE image");
        Assert.Contains("readable", Assert.Throws<UpdateRefusedException>(() => UpdateInstaller.Check(notManaged, version)).Message, StringComparison.Ordinal);

        // review K41: a damaged launcher library (cut off at several points, so its PE headers or its metadata are broken) is refused like an unreadable one,
        // never with another exception, which the update overlay would not catch
        byte[] real = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Tsukihime.dll"));
        foreach (int cut in new[] { 64, 512, 4096, real.Length / 2, real.Length - 64 })
        {
            string damaged = ReleaseFiles.Folder(_dir, "damaged" + cut.ToString(System.Globalization.CultureInfo.InvariantCulture), "x");
            File.WriteAllBytes(Path.Combine(damaged, "TsukihimeLauncher.dll"), real[..cut]);
            Exception? thrown = Record.Exception(() => UpdateInstaller.Check(damaged, version));
            Assert.True(thrown is null or UpdateRefusedException, $"cut at {cut}: {thrown}");
            if (cut <= 4096)
            {
                Assert.Contains("readable", thrown?.Message, StringComparison.Ordinal);       // no metadata left at all
            }
        }

        // a zip whose files sit in one folder: that folder is the release
        string wrapped = Path.Combine(_dir, "wrapped");
        Directory.CreateDirectory(wrapped);
        Directory.Move(ReleaseFiles.Folder(_dir, "inner", "w"), Path.Combine(wrapped, "Tsukihime-v0.1.4-win-x64"));
        Assert.Equal(Path.Combine(wrapped, "Tsukihime-v0.1.4-win-x64"), UpdateInstaller.Check(wrapped, version).Root);
    }

    [Fact]
    public void AFolderTheUserCannotWriteIsSaidInWords()
    {
        Assert.Null(UpdateInstaller.ProbeWritable(_dir));
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
        Assert.NotNull(UpdateInstaller.ProbeWritable(Path.Combine(_dir, "missing", "folder")));
        string message = UpdateInstaller.NotWritableMessage(@"C:\Program Files\Tsukihime", new ReleaseVersion(0, 1, 4));
        Assert.Contains(@"C:\Program Files\Tsukihime", message, StringComparison.Ordinal);
        Assert.Contains("Tsukihime-v0.1.4-win-x64.zip", message, StringComparison.Ordinal);
        Assert.Contains("Releases page", message, StringComparison.Ordinal);

        // Program Files itself, as an ordinary account sees it (an elevated test run could write there, so it is skipped then; nothing is created either way)
        if (OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess)
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            Assert.NotNull(UpdateInstaller.ProbeWritable(programFiles));
        }
    }

    [Fact]
    public void TheSpaceCheckAsksForFourTimesTheZip()
    {
        Assert.Null(Updater.SpaceProblem(_dir, 1024));
        Assert.Null(Updater.SpaceProblem(_dir, 0));
        string? tooBig = Updater.SpaceProblem(_dir, long.MaxValue / 8);
        Assert.NotNull(tooBig);
        Assert.Contains("not enough room", tooBig, StringComparison.Ordinal);
    }
}

using System.Diagnostics;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>
/// Package K40 (issue #6), end to end: the real TsukihimeLauncher.exe of this build, copied into a temporary folder, runs its update in the verification mode
/// (<c>--update-selftest</c>, no window) against a loopback stand-in of GitHub (<see cref="FakeGitHub"/>: the release list, the zip, SHA256SUMS.txt, the redirect to
/// the file host), replaces its own files while it runs, and restarts into the new launcher, whose <c>--selftest</c> proves it runs and that the .old files and
/// the staging folder are gone. The launcher's build output is found beside this test's own output (a solution build makes both); without it the test is skipped.
/// </summary>
public sealed class UpdateEndToEndTests : IDisposable
{
    private const string Repo = UpdateSource.DefaultRepository;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-k40-e2e-" + Guid.NewGuid().ToString("N"));

    public UpdateEndToEndTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        KillLaunchersIn(_dir);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a launcher that has not quite ended; the temp folder is the system's to clean
        }
    }

    /// <summary>The build output of src/Tsuki.Launcher next to this test's output (artifacts layout or the plain bin layout), or null.</summary>
    internal static string? LauncherOutput()
    {
        string here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string[] candidates =
        [
            here.Replace($"{Path.DirectorySeparatorChar}Tsuki.Tests.Synthetic{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}Tsuki.Launcher{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase),
            here.Replace($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}Tsuki.Tests.Synthetic{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}Tsuki.Launcher{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                + "-windows",
        ];
        return candidates.FirstOrDefault(c => !string.Equals(c, here, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(c, GameLauncher.LauncherFileName)));
    }

    /// <summary>
    /// A copy of the launcher's output that runs: the launcher with its libraries and Tsukihime.exe / Tsukihime.dll (the update checks they are there; nothing starts
    /// the game). The FFmpeg and SDL libraries and the debug files stay out (the launcher does not load them).
    /// </summary>
    internal static string CopyInstall(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith("av", StringComparison.OrdinalIgnoreCase) || name.StartsWith("swresample", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.StartsWith("SDL3", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(to, name));
        }

        return to;
    }

    private static void KillLaunchersIn(string folder)
    {
        foreach (Process p in Process.GetProcessesByName("TsukihimeLauncher"))
        {
            using (p)
            {
                try
                {
                    if (p.MainModule?.FileName is { } path && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!p.WaitForExit(30_000))
                        {
                            p.Kill();
                        }
                    }
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // gone already
                }
            }
        }
    }

    /// <summary>Runs the installed launcher's update self-test and returns both reports (the old launcher's and the one it restarted into).</summary>
    internal static (string Before, string After, int Exit) RunUpdate(string install, string profile, Uri api, string reportDir, params string[] extra)
    {
        string report = Path.Combine(reportDir, "update.txt");
        var info = new ProcessStartInfo(Path.Combine(install, GameLauncher.LauncherFileName)) { UseShellExecute = false, WorkingDirectory = install };
        foreach (string a in new[] { "--update-selftest", report, "--data", profile, "--update-api", api.AbsoluteUri }.Concat(extra))
        {
            info.ArgumentList.Add(a);
        }

        // the launcher's own check of the game folder is not what this test is about: no game folder for it (and nothing of the player's profile: --data)
        info.Environment.Remove("TSUKI_GAME_DIR");
        using Process first = Process.Start(info)!;
        Assert.True(first.WaitForExit(120_000), "the old launcher ended");
        string before = File.Exists(report) ? File.ReadAllText(report) : "(no report)";
        string afterPath = report + ".after.txt";
        var clock = Stopwatch.StartNew();
        while (!File.Exists(afterPath) && clock.Elapsed < TimeSpan.FromSeconds(120) && first.ExitCode == 0)
        {
            Thread.Sleep(200);
        }

        KillLaunchersIn(install);
        string after = File.Exists(afterPath) ? File.ReadAllText(afterPath) : "(no report of the new launcher)";
        Keep(reportDir, profile, report, afterPath);
        return (before, after, first.ExitCode);
    }

    /// <summary>With TSUKI_K40_KEEP set to a folder, the reports and the launcher's log of a run are copied there (the evidence of a verification run).</summary>
    private static void Keep(string reportDir, string profile, params string[] reports)
    {
        if (Environment.GetEnvironmentVariable("TSUKI_K40_KEEP") is not { Length: > 0 } keep)
        {
            return;
        }

        string target = Path.Combine(keep, new DirectoryInfo(reportDir).Name + "-" + DateTime.Now.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(target);
        foreach (string file in reports.Append(Path.Combine(profile, LauncherLog.FileName)).Where(File.Exists))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }

    /// <summary>
    /// The real thing, when two published folders are named (TSUKI_K40_FROM: an installed release, TSUKI_K40_TO: the next one, both made by `dotnet publish` of the
    /// launcher, self-contained, so the running launcher's own runtime files are among the files replaced): the older one, copied, updates itself to the newer one
    /// served as Tsukihime-vX.Y.Z-win-x64.zip with its SHA256SUMS.txt, and the launcher it restarts into reports the newer version. Skipped without the two folders.
    /// </summary>
    [Fact]
    public void APublishedReleaseUpdatesItselfToTheNextPublishedRelease()
    {
        string? from = Environment.GetEnvironmentVariable("TSUKI_K40_FROM");
        string? to = Environment.GetEnvironmentVariable("TSUKI_K40_TO");
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || !File.Exists(Path.Combine(from, GameLauncher.LauncherFileName)) || !File.Exists(Path.Combine(to, GameLauncher.LauncherFileName)))
        {
            Assert.Skip("TSUKI_K40_FROM and TSUKI_K40_TO do not name two published folders");
        }

        static ReleaseVersion VersionOf(string folder)
        {
            string? info = Tsuki.Data.Icons.PeIcons.Marks(Path.Combine(folder, "TsukihimeLauncher.dll"))?.InformationalVersion;
            Assert.True(ReleaseVersion.TryParseInformational(info, out ReleaseVersion v), info);
            return v;
        }

        ReleaseVersion oldVersion = VersionOf(from);
        ReleaseVersion newVersion = VersionOf(to);
        Assert.True(newVersion > oldVersion, $"{to} ({newVersion}) is newer than {from} ({oldVersion})");

        string install = Path.Combine(_dir, "Tsukihime");
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(install, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        // TSUKI_K40_TO_ZIP: the release zip as the release steps make it (Compress-Archive of the publish folder's contents), instead of zipping TSUKI_K40_TO here
        string zipName = UpdateSource.ZipAssetName(newVersion);
        string zip = Path.Combine(_dir, zipName);
        if (Environment.GetEnvironmentVariable("TSUKI_K40_TO_ZIP") is { Length: > 0 } given)
        {
            File.Copy(given, zip);
        }
        else
        {
            ReleaseFiles.Zip(to, zip);
        }

        string sums = ReleaseFiles.Sums(Path.Combine(_dir, UpdateSource.SumsAssetName), zip);
        using var server = new FakeGitHub();
        server.List(
            Repo,
            server.Release(Repo, newVersion.Tag, [(zipName, zip), (UpdateSource.SumsAssetName, sums)], body: "## Fixed\n\n- The published-folder run of package K40."),
            server.Release(Repo, oldVersion.Tag, [(zipName, zip), (UpdateSource.SumsAssetName, sums)]));
        (string before, string after, int exit) = RunUpdate(install, Path.Combine(_dir, "profile"), server.Base, _dir);

        Assert.True(exit == 0, before);
        Assert.DoesNotContain("FAIL", before, StringComparison.Ordinal);
        Assert.Contains($"launcher {oldVersion}", before, StringComparison.Ordinal);
        Assert.Contains($"the check found an update ({newVersion.Tag}", before, StringComparison.Ordinal);
        Assert.DoesNotContain("FAIL", after, StringComparison.Ordinal);
        Assert.Contains($"launcher {newVersion}", after, StringComparison.Ordinal);
        Assert.Contains($"this launcher ({newVersion.Tag}) is newer than the one it replaced ({oldVersion.Tag})", after, StringComparison.Ordinal);
        Assert.Contains($"after the update from {oldVersion.Tag}: 0 file(s) left by the cleanup, 0 .old file(s) in the folder, staging folder gone", after, StringComparison.Ordinal);
        foreach (string file in Directory.GetFiles(to, "*", SearchOption.AllDirectories))
        {
            string installed = Path.Combine(install, Path.GetRelativePath(to, file));
            Assert.True(File.Exists(installed), installed);
            Assert.Equal(ReleaseSums.Sha256Hex(file), ReleaseSums.Sha256Hex(installed));
        }
    }

    [Fact]
    public void TheLauncherUpdatesItsOwnFolderFromTheLoopbackGitHubAndRestartsIntoTheNewFiles()
    {
        string? output = LauncherOutput();
        if (output is null)
        {
            Assert.Skip("the launcher's build output is not beside the tests (build src/Tsuki.slnx)");
        }

        ReleaseVersion version = ReleaseFiles.BuildVersion();
        Assert.True(version.Patch > 0, "the build's version has a patch number to pretend an older one");
        var older = new ReleaseVersion(version.Major, version.Minor, version.Patch - 1);

        // the installed copy, and the release: the same build plus a marker file and a changed licence text, so the new files can be told from the old ones
        string install = CopyInstall(output, Path.Combine(_dir, "Tsukihime"));
        File.WriteAllText(Path.Combine(install, "README.md"), "old readme");
        string release = CopyInstall(output, Path.Combine(_dir, "release"));
        File.WriteAllText(Path.Combine(release, "README.md"), "new readme");
        File.WriteAllText(Path.Combine(release, "K40_UPDATE_MARK.txt"), "installed by the update");
        string zipName = UpdateSource.ZipAssetName(version);
        string zip = ReleaseFiles.Zip(release, Path.Combine(_dir, zipName));
        string sums = ReleaseFiles.Sums(Path.Combine(_dir, UpdateSource.SumsAssetName), zip);
        string profile = Path.Combine(_dir, "profile");

        using var server = new FakeGitHub();
        server.List(Repo, server.Release(Repo, version.Tag, [(zipName, zip), (UpdateSource.SumsAssetName, sums)], body: "## Fixed\n\n- **The end-to-end test** of package K40."));
        (string before, string after, int exit) = RunUpdate(install, profile, server.Base, _dir, "--update-current", older.ToString());

        Assert.True(exit == 0, before);
        Assert.DoesNotContain("FAIL", before, StringComparison.Ordinal);
        Assert.Contains($"the check found an update ({version.Tag}", before, StringComparison.Ordinal);
        Assert.Contains("the running TsukihimeLauncher.exe was renamed to .old and the new one moved in", before, StringComparison.Ordinal);
        Assert.Contains("the new launcher was started", before, StringComparison.Ordinal);

        // the new launcher ran from the same folder, cleaned up behind the old one and passed its own self-test
        Assert.DoesNotContain("FAIL", after, StringComparison.Ordinal);
        Assert.Contains($"launcher {version}", after, StringComparison.Ordinal);
        Assert.Contains($"runs from {install}", after, StringComparison.Ordinal);
        Assert.Contains($"after the update from {older.Tag}: 0 file(s) left by the cleanup, 0 .old file(s) in the folder, staging folder gone", after, StringComparison.Ordinal);
        Assert.Equal("new readme", File.ReadAllText(Path.Combine(install, "README.md")));
        Assert.Equal("installed by the update", File.ReadAllText(Path.Combine(install, "K40_UPDATE_MARK.txt")));
        Assert.Empty(Directory.GetFiles(install, "*.old", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(install, UpdateStaging.FolderName)));
        foreach (string file in Directory.GetFiles(release))
        {
            Assert.Equal(ReleaseSums.Sha256Hex(file), ReleaseSums.Sha256Hex(Path.Combine(install, Path.GetFileName(file))));
        }

        // what the launcher logged, in the test's profile (never the player's)
        string log = File.ReadAllText(Path.Combine(profile, LauncherLog.FileName));
        Assert.Contains($"update check (Check now): {server.Base}repos/{Repo}/releases?per_page=20 for {older.Tag}", log, StringComparison.Ordinal);
        Assert.Contains($"update: {zipName} matches {UpdateSource.SumsAssetName}", log, StringComparison.Ordinal);
        Assert.Contains("update cleanup: the .old files and the staging folder are gone", log, StringComparison.Ordinal);
        Assert.Contains($"/assets/{version.Tag}/{zipName}", server.Requests.Select(r => r.Path));
    }

    /// <summary>
    /// A process named Tsukihime.exe started from <paramref name="folder"/> that stays up for a minute without a window: a copy of Windows' own ping.exe, pinging the
    /// loopback address (the game itself would open a window). Killed by the caller.
    /// </summary>
    private static Process StartFakeGame(string folder)
    {
        string ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
        Assert.True(File.Exists(ping), ping);
        string exe = Path.Combine(folder, GameLauncher.AppFileName);
        File.Copy(ping, exe, overwrite: true);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("-n");
        info.ArgumentList.Add("60");
        info.ArgumentList.Add("127.0.0.1");
        return Process.Start(info)!;
    }

    [Fact]
    public void TheGameOfThisFolderCountsAsRunningAndAnotherFoldersGameDoesNot()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
        }

        string here = Path.Combine(_dir, "here");
        string there = Path.Combine(_dir, "there");
        Directory.CreateDirectory(here);
        Directory.CreateDirectory(there);
        Assert.False(GameLauncher.IsGameRunningFrom(here));
        using Process game = StartFakeGame(here);
        try
        {
            Assert.True(GameLauncher.IsGameRunningFrom(here));
            Assert.False(GameLauncher.IsGameRunningFrom(there));
        }
        finally
        {
            game.Kill();
            game.WaitForExit(10_000);
        }

        Assert.False(GameLauncher.IsGameRunningFrom(here));
    }

    [Fact]
    public void TheUpdateWaitsWhileTheGameOfItsFolderRuns()
    {
        string? output = LauncherOutput();
        if (output is null)
        {
            Assert.Skip("the launcher's build output is not beside the tests (build src/Tsuki.slnx)");
        }

        ReleaseVersion version = ReleaseFiles.BuildVersion();
        var older = new ReleaseVersion(version.Major, version.Minor, version.Patch - 1);
        string install = CopyInstall(output, Path.Combine(_dir, "Tsukihime"));
        string zipName = UpdateSource.ZipAssetName(version);
        string zip = ReleaseFiles.Zip(CopyInstall(output, Path.Combine(_dir, "release")), Path.Combine(_dir, zipName));
        string sums = ReleaseFiles.Sums(Path.Combine(_dir, UpdateSource.SumsAssetName), zip);
        using var server = new FakeGitHub();
        server.List(Repo, server.Release(Repo, version.Tag, [(zipName, zip), (UpdateSource.SumsAssetName, sums)]));
        using Process game = StartFakeGame(install);
        try
        {
            string[] before = Directory.GetFiles(install).Order(StringComparer.Ordinal).Select(f => f + ":" + ReleaseSums.Sha256Hex(f)).ToArray();
            (string report, string after, int exit) = RunUpdate(install, Path.Combine(_dir, "profile"), server.Base, _dir, "--update-current", older.ToString());
            Assert.Equal(1, exit);
            Assert.Contains("FAIL  the update was installed and the restart was asked for (stage Offer; Close the game first: Tsukihime.exe of this folder is running", report, StringComparison.Ordinal);
            Assert.Equal("(no report of the new launcher)", after);
            Assert.DoesNotContain(server.Requests, r => r.Path.EndsWith(".zip", StringComparison.Ordinal));     // nothing was downloaded
            Assert.Equal(before, Directory.GetFiles(install).Order(StringComparer.Ordinal).Select(f => f + ":" + ReleaseSums.Sha256Hex(f)).ToArray());
            Assert.False(Directory.Exists(Path.Combine(install, UpdateStaging.FolderName)));
        }
        finally
        {
            game.Kill();
            game.WaitForExit(10_000);
        }
    }

    /// <summary>
    /// Review K41, an adversarial release through the real launcher: a zip whose SHA256SUMS.txt matches (so it passes the checksum and is unpacked) but which carries,
    /// after the good files, entries that climb out of the folder ("../", "..\..\..\..\" up to above the test's folder, "licenses/../../../"). The launcher must refuse it whole: exit 1, no new launcher started, nothing
    /// written beside or above the install, every file of the install unchanged, no staging folder and no .old file left.
    /// </summary>
    [Fact]
    public void AZipSlipReleaseWithAMatchingChecksumIsRefusedByTheRealLauncher()
    {
        string? output = LauncherOutput();
        if (output is null)
        {
            Assert.Skip("the launcher's build output is not beside the tests (build src/Tsuki.slnx)");
        }

        ReleaseVersion version = ReleaseFiles.BuildVersion();
        var older = new ReleaseVersion(version.Major, version.Minor, version.Patch - 1);
        string install = CopyInstall(output, Path.Combine(_dir, "Tsukihime"));
        string zipName = UpdateSource.ZipAssetName(version);
        string zip = ReleaseFiles.Zip(CopyInstall(output, Path.Combine(_dir, "release")), Path.Combine(_dir, zipName));
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Update))
        {
            foreach (string evil in new[] { "../K41_ESCAPED.txt", "..\\..\\..\\..\\K41_ESCAPED_TOO.txt", "licenses/../../../K41_ESCAPED_THREE.txt" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(evil).Open());
                writer.Write("this file must never be written");
            }
        }

        string sums = ReleaseFiles.Sums(Path.Combine(_dir, UpdateSource.SumsAssetName), zip);
        Assert.Equal(SumCheck.Match, ReleaseSums.Check(File.ReadAllText(sums), zipName, ReleaseSums.Sha256Hex(zip)));
        using var server = new FakeGitHub();
        server.List(Repo, server.Release(Repo, version.Tag, [(zipName, zip), (UpdateSource.SumsAssetName, sums)]));
        string[] before = Directory.GetFiles(install, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(f => f + ":" + ReleaseSums.Sha256Hex(f)).ToArray();

        (string report, string after, int exit) = RunUpdate(install, Path.Combine(_dir, "profile"), server.Base, _dir, "--update-current", older.ToString());

        Assert.Equal(1, exit);
        Assert.Contains($"the check found an update ({version.Tag}", report, StringComparison.Ordinal);
        Assert.Contains("FAIL  the update was installed and the restart was asked for (stage Failed; The update did not work: the zip was refused: ", report, StringComparison.Ordinal);
        Assert.DoesNotContain("the new launcher was started", report, StringComparison.Ordinal);
        Assert.Equal("(no report of the new launcher)", after);
        Assert.Contains($"/assets/{version.Tag}/{zipName}", server.Requests.Select(r => r.Path));      // it got past the checksum: the zip check refused it
        foreach (string name in new[] { "K41_ESCAPED.txt", "K41_ESCAPED_TOO.txt", "K41_ESCAPED_THREE.txt" })
        {
            Assert.Empty(Directory.GetFiles(_dir, name, SearchOption.AllDirectories));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, name)), name);
        }

        Assert.Equal(before, Directory.GetFiles(install, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(f => f + ":" + ReleaseSums.Sha256Hex(f)).ToArray());
        Assert.False(Directory.Exists(Path.Combine(install, UpdateStaging.FolderName)));
        Assert.Empty(Directory.GetFiles(install, "*.old", SearchOption.AllDirectories));
    }

    [Fact]
    public void APersonalBuildNeitherChecksNorUpdates()
    {
        string? output = LauncherOutput();
        if (output is null)
        {
            Assert.Skip("the launcher's build output is not beside the tests (build src/Tsuki.slnx)");
        }

        ReleaseVersion version = ReleaseFiles.BuildVersion();
        string install = CopyInstall(output, Path.Combine(_dir, "Tsukihime"));
        File.WriteAllText(Path.Combine(install, UpdateInstaller.PersonalNote), "PERSONAL BUILD OF THE TSUKIHIME PC PORT - DO NOT SHARE");
        string zipName = UpdateSource.ZipAssetName(version);
        string zip = ReleaseFiles.Zip(CopyInstall(output, Path.Combine(_dir, "release")), Path.Combine(_dir, zipName));
        string sums = ReleaseFiles.Sums(Path.Combine(_dir, UpdateSource.SumsAssetName), zip);
        using var server = new FakeGitHub();
        server.List(Repo, server.Release(Repo, version.Tag, [(zipName, zip), (UpdateSource.SumsAssetName, sums)]));
        string[] before = Directory.GetFiles(install).Select(f => ReleaseSums.Sha256Hex(f)).ToArray();

        (string report, string after, int exit) = RunUpdate(install, Path.Combine(_dir, "profile"), server.Base, _dir, "--update-current", "0.0.1");
        Assert.Equal(1, exit);
        Assert.Contains("FAIL  the check found an update (none; Personal build: updates are off.)", report, StringComparison.Ordinal);
        Assert.Equal("(no report of the new launcher)", after);
        Assert.Empty(server.Requests);                                                   // not even the release list was asked for
        Assert.Equal(before, Directory.GetFiles(install).Select(f => ReleaseSums.Sha256Hex(f)).ToArray());
    }
}

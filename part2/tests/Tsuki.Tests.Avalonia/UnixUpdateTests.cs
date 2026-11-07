using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tsuki.Cli.Commands;
using Tsuki.Launcher.Avalonia;
using Tsuki.Launcher.Core;
using Tsuki.Tests.Data.Cli;
using Tsuki.Tests.Synthetic.Launcher;

namespace Tsuki.Tests.Avalonia;

public sealed class UnixUpdateTests : IDisposable
{
    private const string Repo = UpdateSource.DefaultRepository;
    private readonly string _root = UnixUpdateTestKit.TempRoot("tsuki-k48-update-");
    private static ReleaseVersion Version => ReleaseFiles.BuildVersion();

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (string folder in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories))
            {
                Chmod(folder, ReleaseRules.Mode755);
            }
        }

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("win", Architecture.X64, "Tsukihime-v0.1.6-win-x64.zip")]
    [InlineData("linux", Architecture.X64, "Tsukihime-v0.1.6-linux-x64.tar.gz")]
    [InlineData("macos", Architecture.X64, "Tsukihime-v0.1.6-macos-x64.zip")]
    [InlineData("macos", Architecture.Arm64, "Tsukihime-v0.1.6-macos-arm64.zip")]
    public void EachPlatformSelectsOnlyItsOwnAssetAndChecksumLine(string platform, Architecture architecture, string expected)
    {
        var version = new ReleaseVersion(0, 1, 6);
        string[] names = ["Tsukihime-v0.1.6-win-x64.zip", "Tsukihime-v0.1.6-linux-x64.tar.gz", "Tsukihime-v0.1.6-macos-arm64.zip", "Tsukihime-v0.1.6-macos-x64.zip"];
        Assert.Equal(expected, PlatformUpdate.AssetNameFor(version, platform, architecture));
        var assets = names.Select(name => new { name, browser_download_url = "https://github.com/" + Repo + "/releases/download/v0.1.6/" + name, size = 1, state = "uploaded" })
            .Append(new { name = UpdateSource.SumsAssetName, browser_download_url = "https://github.com/" + Repo + "/releases/download/v0.1.6/" + UpdateSource.SumsAssetName, size = 1, state = "uploaded" }).ToArray();
        string json = JsonSerializer.Serialize(new[] { new { tag_name = "v0.1.6", name = "test", body = "", assets } });
        FeedEntry entry = Assert.Single(UpdateFeed.Parse(json, v => PlatformUpdate.AssetNameFor(v, platform, architecture)));
        ReleaseInfo release = Assert.IsType<ReleaseInfo>(entry.Release);
        Assert.Equal(expected, release.Zip.Name);
        string sums = string.Join("\n", names.Select((name, index) => new string((char)('a' + index), 64) + "  " + name)) + "\n";
        string expectedHash = new((char)('a' + Array.IndexOf(names, expected)), 64);
        Assert.Equal(SumCheck.Match, ReleaseSums.Check(sums, expected, expectedHash));
        Assert.Equal(SumCheck.Mismatch, ReleaseSums.Check(sums, names.First(name => name != expected), expectedHash));
        Assert.Equal("Tsukihime-v0.1.6-win-x64.zip", Assert.IsType<ReleaseInfo>(Assert.Single(UpdateFeed.Parse(json)).Release).Zip.Name);
        Assert.Equal("Tsukihime-v0.1.6-win-x64.zip", UpdateSource.ZipAssetName(version));
    }

    [Fact]
    public void ThisHostAsksForItsOwnPlatformsAssetAndALinuxWithoutAReleaseForItsArchitectureForNone()
    {
        var version = new ReleaseVersion(0, 1, 7);
        string expected = OperatingSystem.IsWindows() ? "Tsukihime-v0.1.7-win-x64.zip"
            : OperatingSystem.IsLinux() ? "Tsukihime-v0.1.7-linux-x64.tar.gz"
            : $"Tsukihime-v0.1.7-macos-{(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64")}.zip";
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            Assert.Equal(expected, PlatformUpdate.AssetName(version));
        }

        // review K48 -15: a self-built arm64 Linux launcher must not download and install the x64 release
        Assert.Contains("Linux architecture has no release", Assert.Throws<UpdateRefusedException>(() => PlatformUpdate.AssetNameFor(version, "linux", Architecture.Arm64)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxDownloadsItsTarAppliesItAndKeepsTheProfileUntouched()
    {
        string install = LinuxRelease.Folder(_root, "install");
        string release = LinuxRelease.Folder(_root, "release");
        File.WriteAllText(Path.Combine(install, "README.md"), "old");
        File.WriteAllText(Path.Combine(release, "README.md"), "new");
        File.WriteAllText(Path.Combine(release, "installed-by-update.txt"), "new file");
        string profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.ini"), "language=en\n");
        File.WriteAllText(Path.Combine(profile, "save.dat"), "synthetic save");
        string name = PlatformUpdate.AssetNameFor(Version, "linux", Architecture.X64);
        string archive = LinuxRelease.Tar(release, Path.Combine(_root, name));
        string sums = ReleaseFiles.Sums(Path.Combine(_root, UpdateSource.SumsAssetName), archive);
        using var server = new FakeGitHub();
        server.List(Repo, server.Release(Repo, Version.Tag, [(name, archive), (UpdateSource.SumsAssetName, sums)]));
        var model = Model(install, profile, server.Base);
        var measured = new List<string>();
        model.FreeSpace = path =>
        {
            measured.Add(path);
            return long.MaxValue;
        };
        int restarts = 0;
        string[]? journal = null;
        bool downloadGone = false;
        model.RestartRequested = (exe, version) =>
        {
            Assert.Equal(Version, version);
            Assert.True(File.Exists(exe));
            journal = File.ReadAllLines(new UpdateStaging(install).Journal);
            downloadGone = !Directory.Exists(new UpdateStaging(install).DownloadDirectory);
            restarts++;
        };
        await model.CheckForUpdatesAsync(manual: true);
        Assert.Equal(name, model.AvailableUpdate?.Zip.Name);
        await model.RunUpdateAsync();
        Assert.Equal(UnixUpdateStage.Restarting, model.UpdateStage);
        Assert.Equal(1, restarts);
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "README.md")));
        Assert.Equal("new file", File.ReadAllText(Path.Combine(install, "installed-by-update.txt")));
        Assert.Equal("language=en\n", File.ReadAllText(Path.Combine(profile, "settings.ini")));
        Assert.Equal("synthetic save", File.ReadAllText(Path.Combine(profile, "save.dat")));

        // the launcher goes in last, and the 100 MB download is gone before the restart (review K48 -13)
        Assert.NotNull(journal);
        Assert.Equal("done", journal[^1]);
        Assert.Equal("new\t" + Path.Combine(Path.GetFullPath(install), "TsukihimeLauncher"), journal[^2]);
        Assert.True(downloadGone);

        Assert.Equal(0, UpdateInstaller.Cleanup(install));
        Assert.False(Directory.Exists(Path.Combine(install, UpdateStaging.FolderName)));

        // review ci-tests-docs-3 (79b1d28): the free space of the launcher's own folder (a Steam Deck's /home is a mount of its own), never of "/"
        Assert.Equal([Path.GetFullPath(install)], measured);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(ReleaseRules.Mode755, File.GetUnixFileMode(Path.Combine(install, "TsukihimeLauncher")));
            Assert.Equal(ReleaseRules.Mode755, File.GetUnixFileMode(Path.Combine(install, "Tsukihime")));
        }
    }

    [Fact]
    public async Task ABadChecksumDoesNotChangeAnyInstalledFile()
    {
        string install = LinuxRelease.Folder(_root, "bad-install");
        string release = LinuxRelease.Folder(_root, "bad-release");
        string profile = Path.Combine(_root, "bad-profile");
        File.WriteAllText(Path.Combine(install, "README.md"), "old");
        string name = PlatformUpdate.AssetNameFor(Version, "linux", Architecture.X64);
        string archive = LinuxRelease.Tar(release, Path.Combine(_root, name));
        string sums = Path.Combine(_root, UpdateSource.SumsAssetName);
        File.WriteAllText(sums, new string('0', 64) + "  " + name + "\n");
        using var server = new FakeGitHub();
        server.List(Repo, server.Release(Repo, Version.Tag, [(name, archive), (UpdateSource.SumsAssetName, sums)]));
        var model = Model(install, profile, server.Base);
        bool restarted = false;
        model.RestartRequested = (_, _) => restarted = true;
        string[] before = Snapshot(install);
        await model.CheckForUpdatesAsync(manual: true);
        await model.RunUpdateAsync();
        Assert.Equal(UnixUpdateStage.Failed, model.UpdateStage);
        Assert.Contains("does not match", model.UpdateMessage, StringComparison.Ordinal);
        Assert.True(model.UpdateMessageVisible);
        Assert.NotEmpty(model.Toast);
        string tag = Regex.Escape(Version.Tag);
        Assert.Matches($@"^{tag} is available \(checked at \d\d:\d\d\)\. Press ""Update to {tag}"" at the bottom of the window\.$", model.UpdateStatus);
        Assert.False(restarted);
        Assert.Equal(before, Snapshot(install));
        Assert.False(Directory.Exists(Path.Combine(install, UpdateStaging.FolderName)));
    }

    [Fact]
    public async Task AChecksumFileWithoutTheArchivesLineStopsBeforeTheDownload()
    {
        string install = LinuxRelease.Folder(_root, "noline-install");
        string name = PlatformUpdate.AssetNameFor(Version, "linux", Architecture.X64);
        string archive = LinuxRelease.Tar(LinuxRelease.Folder(_root, "noline-release"), Path.Combine(_root, name));
        string sums = Path.Combine(_root, UpdateSource.SumsAssetName);
        File.WriteAllText(sums, ReleaseSums.Sha256Hex(archive) + "  " + UpdateSource.ZipAssetName(Version) + "\n");
        using var server = new FakeGitHub();
        server.List(Repo, server.Release(Repo, Version.Tag, [(name, archive), (UpdateSource.SumsAssetName, sums)]));
        var model = Model(install, Path.Combine(_root, "noline-profile"), server.Base);
        model.RestartRequested = (_, _) => { };
        await model.CheckForUpdatesAsync(manual: true);
        await model.RunUpdateAsync();
        Assert.Equal(UnixUpdateStage.Failed, model.UpdateStage);
        Assert.Contains("has no checksum for " + name, model.UpdateMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(server.Requests, request => request.Path.EndsWith(name, StringComparison.Ordinal) && request.Path.StartsWith("/assets/", StringComparison.Ordinal));
    }

    [Fact]
    public void LinuxRollsBackWhenTheNewProcessCannotStart()
    {
        string install = LinuxRelease.Folder(_root, "rollback-install");
        string release = LinuxRelease.Folder(_root, "rollback-release");
        File.WriteAllText(Path.Combine(install, "README.md"), "old");
        File.WriteAllText(Path.Combine(release, "README.md"), "new");
        File.WriteAllText(Path.Combine(release, "new-file.txt"), "must be rolled back");
        string archive = LinuxRelease.Tar(release, Path.Combine(_root, "rollback.tar.gz"));
        var staging = new UpdateStaging(install);
        StagedRelease flat = UnixReleaseArchive.ExtractLinux(archive, staging.FilesDirectory, Version, CancellationToken.None);
        var staged = new UnixStagedUpdate(Info(archive), staging, flat, null);
        var error = Assert.Throws<UpdateRefusedException>(() => PlatformUpdate.ApplyAndRestart(staged, (_, _) => throw new IOException("no start")));
        Assert.Contains("The previous version was restored.", error.Message, StringComparison.Ordinal);
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "README.md")));
        Assert.False(File.Exists(Path.Combine(install, "new-file.txt")));
        Assert.Empty(Directory.GetFiles(install, "*.old", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(staging.Root));
    }

    [Fact]
    public void AnApplyThatFailsHalfWayRestoresTheOldVersionDeletesTheStagingAndNeverRestarts()
    {
        // review K48 -02 (g) and -13: a file that cannot be replaced (Windows: held without delete sharing; Unix: its folder is read-only) ends the apply
        RequireEnforcedPermissions();
        string install = LinuxRelease.Folder(_root, "apply-install");
        string release = LinuxRelease.Folder(_root, "apply-release");
        File.WriteAllText(Path.Combine(install, "licenses", "LICENSE.txt"), "old licence");
        File.WriteAllText(Path.Combine(release, "licenses", "LICENSE.txt"), "new licence");
        File.WriteAllText(Path.Combine(release, "README.md"), "new");
        string archive = LinuxRelease.Tar(release, Path.Combine(_root, "apply.tar.gz"));
        string[] before = Snapshot(install);
        var staging = new UpdateStaging(install);
        var staged = new UnixStagedUpdate(Info(archive), staging, UnixReleaseArchive.ExtractLinux(archive, staging.FilesDirectory, Version, CancellationToken.None), null);
        bool restarted = false;
        using (Block(Path.Combine(install, "licenses"), "LICENSE.txt"))
        {
            string message = Assert.Throws<UpdateRefusedException>(() => PlatformUpdate.ApplyAndRestart(staged, (_, _) => restarted = true)).Message;
            Assert.EndsWith("The previous version was restored.", message, StringComparison.Ordinal);
        }

        Assert.False(restarted);
        Assert.Equal(before, Snapshot(install));
        Assert.False(Directory.Exists(staging.Root));
    }

    [Fact]
    public void ARestartRollbackThatCannotFinishIsRepairedAtTheNextStartInsteadOfLosingTheOldCopies()
    {
        // review K48 -04: Rollback writes its outcome after "done"; the next start repairs instead of deleting the .old files
        RequireEnforcedPermissions();
        string install = LinuxRelease.Folder(_root, "restart-install");
        string release = LinuxRelease.Folder(_root, "restart-release");
        File.WriteAllText(Path.Combine(install, "docs", "SETUP_GUIDE.md"), "old guide");
        File.WriteAllText(Path.Combine(release, "docs", "SETUP_GUIDE.md"), "new guide");
        string archive = LinuxRelease.Tar(release, Path.Combine(_root, "restart.tar.gz"));
        var staging = new UpdateStaging(install);
        var staged = new UnixStagedUpdate(Info(archive), staging, UnixReleaseArchive.ExtractLinux(archive, staging.FilesDirectory, Version, CancellationToken.None), null);
        IDisposable? block = null;
        try
        {
            string message = Assert.Throws<UpdateRefusedException>(() => PlatformUpdate.ApplyAndRestart(staged, (_, _) =>
            {
                block = Block(Path.Combine(install, "docs"), "SETUP_GUIDE.md");
                throw new IOException("no start");
            })).Message;
            Assert.Contains("not completely restored", message, StringComparison.Ordinal);
            Assert.Equal("rollback incomplete", File.ReadAllLines(staging.Journal)[^1]);
            Assert.Equal("old guide", File.ReadAllText(Path.Combine(install, "docs", "SETUP_GUIDE.md.old")));
            Assert.True(UpdateInstaller.Cleanup(install, PlatformUpdate.LauncherProgram) > 0, "a start while the file is still blocked keeps the old copy");
        }
        finally
        {
            block?.Dispose();
        }

        PlatformUpdate.CleanupPrevious(install, macOs: false);
        Assert.Equal("old guide", File.ReadAllText(Path.Combine(install, "docs", "SETUP_GUIDE.md")));
        Assert.Empty(Directory.GetFiles(install, "*.old", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(staging.Root));
    }

    [Fact]
    public void ALinuxApplyCutOffAfterTheLauncherMovedCountsAsDone()
    {
        // review K48 -16: the Linux launcher has no .exe; the next start finishes such an update, as on Windows
        string install = LinuxRelease.Folder(_root, "cut-install");
        var staging = new UpdateStaging(install);
        string launcher = Path.Combine(Path.GetFullPath(install), "TsukihimeLauncher");
        File.Move(launcher, launcher + UpdateInstaller.OldSuffix);
        File.WriteAllBytes(launcher, LinuxRelease.Elf(2, "new launcher"));
        Directory.CreateDirectory(staging.Root);
        File.WriteAllLines(staging.Journal, ["old\t" + launcher, "new\t" + launcher]);
        PlatformUpdate.CleanupPrevious(install, macOs: false);
        Assert.Equal(LinuxRelease.Elf(2, "new launcher"), File.ReadAllBytes(launcher));
        Assert.False(File.Exists(launcher + UpdateInstaller.OldSuffix));
        Assert.False(Directory.Exists(staging.Root));
    }

    [Fact]
    public void TarTraversalIsRefusedBeforeItCanEscape()
    {
        string release = LinuxRelease.Folder(_root, "unsafe-release");
        string archive = LinuxRelease.Tar(release, Path.Combine(_root, "unsafe.tar.gz"), extra: (writer, root) =>
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, root + "/../../escaped.txt")
            {
                DataStream = new MemoryStream([1]),
                Mode = ReleaseRules.Mode644,
            });
        });
        Assert.Throws<UnixUpdateRefusedException>(() => UnixReleaseArchive.ExtractLinux(archive, Path.Combine(_root, "unsafe-out"), Version, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    [Fact]
    public void MacZipIsCheckedAndItsLauncherModeIsPreserved()
    {
        string next = MacRelease.Folder(_root, name: "next-mac");
        string archive = MacRelease.Zip(next, Path.Combine(_root, $"Tsukihime-v{Version}-macos-arm64.zip"));
        string app = UnixReleaseArchive.ExtractMac(archive, Path.Combine(_root, "mac-out"), Info(archive), CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(app, "Contents", "MacOS", "TsukihimeLauncher")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(ReleaseRules.Mode755, File.GetUnixFileMode(Path.Combine(app, "Contents", "MacOS", "TsukihimeLauncher")));
            Assert.Equal(ReleaseRules.Mode644, File.GetUnixFileMode(Path.Combine(app, "Contents", "MacOS", "TsukihimeLauncher.dll")));
        }
    }

    [Fact]
    public void TheMacUpdateReplacesTheRunningBundleWhateverItIsCalledAndNoOtherCopy()
    {
        // review K48 -01: Archive Utility names a second download "Tsukihime 2.app"; the update is for the copy that runs
        string applications = Path.Combine(_root, "Applications");
        string running = Bundle(applications, "Tsukihime 2.app", "running");
        string other = Bundle(applications, "Tsukihime.app", "other");
        string launcherDirectory = Path.Combine(running, "Contents", "MacOS") + Path.DirectorySeparatorChar;
        Assert.Equal(running, PlatformUpdate.MacBundle(launcherDirectory));
        UpdateStaging staging = PlatformUpdate.MacStaging(PlatformUpdate.MacBundle(launcherDirectory));
        Assert.Equal(Path.Combine(applications, ".Tsukihime 2.app.update"), staging.AppDirectory);
        Assert.Equal(running, PlatformUpdate.BundleOf(staging));
        StageMacBundle(staging, "new");
        string? restarted = null;
        PlatformUpdate.ApplyAndRestart(new UnixStagedUpdate(Info(MacAsset()), staging, null, running), (exe, _) => restarted = exe, Exchange);
        Assert.Equal(Path.Combine(running, "Contents", "MacOS", "TsukihimeLauncher"), restarted);
        Assert.Equal("new", VersionText(running));
        Assert.Equal("other", VersionText(other));
        Assert.Equal("running", VersionText(Path.Combine(staging.FilesDirectory, "Tsukihime.app")));
        Assert.False(Directory.Exists(staging.DownloadDirectory));

        PlatformUpdate.CleanupPrevious(launcherDirectory, macOs: true);
        Assert.False(Directory.Exists(staging.AppDirectory));
        Assert.Equal(["Tsukihime 2.app", "Tsukihime.app"], Directory.GetFileSystemEntries(applications).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheMacUpdateStagesBesideTheBundleInItsOwnMarkedFolderAndLeavesEveryOtherFolderAlone()
    {
        // review K48 -06: generic names such as update-staging and Tsukihime.old in a shared folder are someone else's
        string downloads = Path.Combine(_root, "Downloads");
        string running = Bundle(downloads, "Tsukihime.app", "running");
        foreach (string foreign in new[] { "update-staging", "Tsukihime.old" })
        {
            Directory.CreateDirectory(Path.Combine(downloads, foreign));
            File.WriteAllText(Path.Combine(downloads, foreign, "someone-elses-file.txt"), "keep");
        }

        string archive = MacRelease.Zip(MacRelease.Folder(_root, name: "mac-release"), Path.Combine(_root, "mac-release-zip", MacAsset()));
        using var server = new FakeGitHub();
        ReleaseInfo release = await Release(server, archive);
        UpdateStaging staging = PlatformUpdate.MacStaging(running);
        using (var client = new UpdateClient(UpdateSource.ForLoopback(server.Base), "test"))
        {
            // stand-ins for ditto, codesign and renameatx_np (reviews mac-update-1, mac-ci-5): the real ones refuse a synthetic, unsigned app on a Mac
            UnixStagedUpdate staged = await PlatformUpdate.PrepareAsync(client, release, staging, CancellationToken.None, null, macOs: true, macTools: UnixUpdateTestKit.MacTools(), exchange: Exchange);
            Assert.Equal(running, staged.AppBundle);
            Assert.True(File.Exists(Path.Combine(staging.AppDirectory, PlatformUpdate.MacMarker)));
            PlatformUpdate.ApplyAndRestart(staged, (_, _) => { }, Exchange);
        }

        PlatformUpdate.CleanupPrevious(Path.Combine(running, "Contents", "MacOS"), macOs: true);
        Assert.False(Directory.Exists(staging.AppDirectory));
        foreach (string foreign in new[] { "update-staging", "Tsukihime.old" })
        {
            Assert.Equal("keep", File.ReadAllText(Path.Combine(downloads, foreign, "someone-elses-file.txt")));
        }
    }

    [Fact]
    public async Task AFolderOfTheStagingsNameWithoutTheMarkerIsNeverDeleted()
    {
        string downloads = Path.Combine(_root, "Unmarked");
        string running = Bundle(downloads, "Tsukihime.app", "running");
        UpdateStaging staging = PlatformUpdate.MacStaging(running);
        Directory.CreateDirectory(staging.AppDirectory);
        File.WriteAllText(Path.Combine(staging.AppDirectory, "mine.txt"), "not the launcher's");
        string archive = MacRelease.Zip(MacRelease.Folder(_root, name: "unmarked-release"), Path.Combine(_root, "unmarked-zip", MacAsset()));
        using var server = new FakeGitHub();
        ReleaseInfo release = await Release(server, archive);
        using var client = new UpdateClient(UpdateSource.ForLoopback(server.Base), "test");
        string message = (await Assert.ThrowsAsync<UnixUpdateRefusedException>(() => PlatformUpdate.PrepareAsync(client, release, staging, CancellationToken.None, null, macOs: true, macTools: UnixUpdateTestKit.MacTools(), exchange: Exchange))).Message;
        Assert.Contains("is in the way and is not the launcher's", message, StringComparison.Ordinal);
        // review mac-update-6: the folder's name starts with a dot, so the sentence says how Finder shows it
        Assert.Contains("Finder hides it (Cmd+Shift+. shows hidden files)", message, StringComparison.Ordinal);
        PlatformUpdate.CleanupPrevious(Path.Combine(running, "Contents", "MacOS"), macOs: true);
        Assert.Equal("not the launcher's", File.ReadAllText(Path.Combine(staging.AppDirectory, "mine.txt")));
        Assert.Equal("running", VersionText(running));
        Assert.DoesNotContain(server.Requests, request => request.Path.StartsWith("/assets/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALeftoverStagingThatCannotBeRemovedIsRefusedBeforeTheDownloadAndTheAppIsUntouched()
    {
        // review K48 -19: a leftover folder that cannot be deleted, not a plain file
        if (OperatingSystem.IsWindows()) { Assert.Skip("a read-only folder is a Unix permission"); }
        string downloads = Path.Combine(_root, "Locked");
        string running = Bundle(downloads, "Tsukihime.app", "running");
        UpdateStaging staging = PlatformUpdate.MacStaging(running);
        string locked = Path.Combine(staging.FilesDirectory, "Tsukihime.app", "Contents");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(staging.AppDirectory, PlatformUpdate.MacMarker), "marker");
        File.WriteAllText(Path.Combine(locked, "Info.plist"), "the previous update's old bundle");
        RequireEnforcedPermissions();
        Chmod(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        string archive = MacRelease.Zip(MacRelease.Folder(_root, name: "locked-release"), Path.Combine(_root, "locked-zip", MacAsset()));
        using var server = new FakeGitHub();
        ReleaseInfo release = await Release(server, archive);
        using var client = new UpdateClient(UpdateSource.ForLoopback(server.Base), "test");
        string message = (await Assert.ThrowsAsync<UnixUpdateRefusedException>(() => PlatformUpdate.PrepareAsync(client, release, staging, CancellationToken.None, null, macOs: true, macTools: UnixUpdateTestKit.MacTools(), exchange: Exchange))).Message;
        Assert.Contains("could not be removed. Finder hides it", message, StringComparison.Ordinal);
        Assert.EndsWith("Your installed app was left as it was.", message, StringComparison.Ordinal);
        Assert.Equal("running", VersionText(running));
        Assert.True(File.Exists(Path.Combine(staging.AppDirectory, PlatformUpdate.MacMarker)), "a folder that could not be emptied stays the launcher's");
        Assert.DoesNotContain(server.Requests, request => request.Path.StartsWith("/assets/", StringComparison.Ordinal));
    }

    [Fact]
    public void TheMacUpdatePutsTheOldBundleBackWhenTheNewOneCannotStart()
    {
        string running = Bundle(Path.Combine(_root, "restore"), "Tsukihime.app", "old");
        UpdateStaging staging = PlatformUpdate.MacStaging(running);
        StageMacBundle(staging, "new");
        var error = Assert.Throws<UpdateRefusedException>(() => PlatformUpdate.ApplyAndRestart(new UnixStagedUpdate(Info(MacAsset()), staging, null, running), (_, _) => throw new IOException("no start"), Exchange));
        Assert.Contains("no start The previous version was restored.", error.Message, StringComparison.Ordinal);
        Assert.Equal("old", VersionText(running));
        Assert.Equal("new", VersionText(Path.Combine(staging.FilesDirectory, "Tsukihime.app")));
    }

    [Fact]
    public void WhenTheSwapBackFailsTheNewBundleIsNeverDeletedBeforeTheOldOneIsBack()
    {
        // review K48 -17: the fallback deleted the app before the old copy was back; a half-deleted bundle was left
        RequireEnforcedPermissions();
        string running = Bundle(Path.Combine(_root, "swapback"), "Tsukihime.app", "old");
        UpdateStaging staging = PlatformUpdate.MacStaging(running);
        StageMacBundle(staging, "new");
        int exchanges = 0;
        bool OnceOnly(string source, string target) => ++exchanges == 1 && Exchange(source, target);
        IDisposable? block = null;
        string message;
        try
        {
            message = Assert.Throws<UpdateRefusedException>(() => PlatformUpdate.ApplyAndRestart(new UnixStagedUpdate(Info(MacAsset()), staging, null, running), (_, _) =>
            {
                // something in the new bundle cannot be deleted (Windows: a file held open; Unix: a read-only folder)
                block = Block(Path.Combine(running, "Contents", "Resources"), "version.txt");
                throw new IOException("no start");
            }, OnceOnly)).Message;
        }
        finally
        {
            block?.Dispose();
        }

        string aside = Path.Combine(staging.FilesDirectory, "Tsukihime-failed.app");
        Assert.Equal(2, exchanges);
        if (OperatingSystem.IsWindows())
        {
            // the open file keeps the new bundle where it is: both stay whole
            Assert.Contains("The previous version could not be put back", message, StringComparison.Ordinal);
            Assert.Equal("new", VersionText(running));
            Assert.Equal("old", VersionText(Path.Combine(staging.FilesDirectory, "Tsukihime.app")));
        }
        else
        {
            // the bundle itself moves: the new one aside, whole, and the old one back
            Assert.Contains("The previous version was restored.", message, StringComparison.Ordinal);
            Assert.Equal("old", VersionText(running));
            Assert.Equal("new", VersionText(aside));
        }

        Assert.True(File.Exists(Path.Combine(running, "Contents", "Info.plist")), "the app's path holds a whole bundle");
    }

    [Fact]
    public void TheNativeSwapPassesRenameSwapAndReportsTheErrno()
    {
        // R5-2b and review K48 -12: the five arguments renameatx_np gets, without a Mac
        var calls = new List<(int, string, int, string, uint)>();
        Assert.True(PlatformUpdate.ExchangeBundles("/a/new.app", "/a/old.app", (fromFd, from, toFd, to, flags) => { calls.Add((fromFd, from, toFd, to, flags)); return 0; }));
        Assert.Equal(new[] { (-2, "/a/new.app", -2, "/a/old.app", 2u) }, calls);
        IOException error = Assert.Throws<IOException>(() => PlatformUpdate.ExchangeBundles("/a/new.app", "/a/old.app", (_, _, _, _, _) =>
        {
            Marshal.SetLastPInvokeError(45);
            return -1;
        }));
        Assert.Contains("(errno 45)", error.Message, StringComparison.Ordinal);
        Assert.Contains(Marshal.GetPInvokeErrorMessage(45), error.Message, StringComparison.Ordinal);
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Throws<PlatformNotSupportedException>(() => PlatformUpdate.ExchangeBundles("/a/new.app", "/a/old.app"));
        }

        var method = typeof(PlatformUpdate).GetMethod("RenameExchange", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.Equal([typeof(int), typeof(string), typeof(int), typeof(string), typeof(uint)], method.GetParameters().Select(parameter => parameter.ParameterType));
        DllImportAttribute import = Assert.IsType<DllImportAttribute>(Attribute.GetCustomAttribute(method, typeof(DllImportAttribute)));
        Assert.Equal(("libc", "renameatx_np", true, CharSet.Ansi), (import.Value, import.EntryPoint, import.SetLastError, import.CharSet));
    }

    [Fact]
    public void MacNativeSwapWorks()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Skip("the real renameatx_np call runs on macOS"); }
        string source = Path.Combine(_root, "native-source");
        string target = Path.Combine(_root, "native-target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "new.txt"), "new");
        File.WriteAllText(Path.Combine(target, "old.txt"), "old");
        Assert.True(PlatformUpdate.ExchangeBundles(source, target));
        Assert.True(File.Exists(Path.Combine(target, "new.txt")));
        Assert.True(File.Exists(Path.Combine(source, "old.txt")));
        Assert.Contains("errno", Assert.Throws<IOException>(() => PlatformUpdate.ExchangeBundles(Path.Combine(_root, "missing"), target)).Message, StringComparison.Ordinal);

        // review mac-update-5: the update swaps <folder>/Tsukihime.app with <folder>/.Tsukihime.app.update/update-staging/files/Tsukihime.app, across parents,
        // while the running launcher holds files of the old bundle open
        string running = Bundle(Path.Combine(_root, "native-parent"), "Tsukihime.app", "old");
        UpdateStaging staging = PlatformUpdate.MacStaging(running);
        StageMacBundle(staging, "new");
        string staged = Path.Combine(staging.FilesDirectory, "Tsukihime.app");
        using (var open = new FileStream(Path.Combine(running, "Contents", "Resources", "version.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(PlatformUpdate.ExchangeBundles(staged, running));
            Assert.Equal("new", VersionText(running));
            Assert.Equal("old", VersionText(staged));
            using var reader = new StreamReader(open);
            Assert.Equal("old", reader.ReadToEnd());
        }
    }

    [Fact]
    public async Task AFreshMacBundleUpdatesThroughTheModelWritingOnlyBesideItself()
    {
        // reviews ci-tests-docs-2/-3, macos-1: the model's Mac path on any host. A bundle with no .Tsukihime.app.update yet reaches the download (79b1d28: probing
        // that folder refused every Mac update), the space check measures the bundle's own folder, and the bundles are swapped and the launcher restarted
        string applications = Path.Combine(_root, "MacApps");
        string running = Bundle(applications, "Tsukihime.app", "running");
        string launcher = Path.Combine(running, "Contents", "MacOS") + Path.DirectorySeparatorChar;
        UpdateStaging staging = PlatformUpdate.MacStaging(running);
        Assert.False(Directory.Exists(staging.AppDirectory));
        using var server = new FakeGitHub();
        ServeMac(server, "model-mac-release", "new");
        UnixLauncherModel model = MacModel(launcher, Path.Combine(_root, "model-mac-profile"), server.Base);
        var measured = new List<string>();
        model.FreeSpace = path =>
        {
            measured.Add(path);
            return long.MaxValue;
        };
        string? restarted = null;
        model.RestartRequested = (exe, version) =>
        {
            Assert.Equal(Version, version);
            restarted = exe;
        };
        await model.CheckForUpdatesAsync(manual: true);
        Assert.Equal(MacAsset(), model.AvailableUpdate?.Zip.Name);
        await model.RunUpdateAsync();
        Assert.True(model.UpdateStage == UnixUpdateStage.Restarting, model.UpdateMessage);
        Assert.Equal(Path.Combine(running, "Contents", "MacOS", "TsukihimeLauncher"), restarted);
        Assert.Contains(server.Requests, request => request.Path.StartsWith("/assets/", StringComparison.Ordinal) && request.Path.EndsWith("/" + MacAsset(), StringComparison.Ordinal));
        Assert.Equal([Path.GetFullPath(applications)], measured);
        Assert.Equal("new", VersionText(running));
        Assert.Equal("running", VersionText(Path.Combine(staging.FilesDirectory, "Tsukihime.app")));

        PlatformUpdate.CleanupPrevious(launcher, macOs: true);
        Assert.False(Directory.Exists(staging.AppDirectory));
        Assert.Equal(["Tsukihime.app"], Directory.GetFileSystemEntries(applications).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ACancelDuringTheMacUnpackingInstallsNothingAndIsNotAFailure()
    {
        // review mac-update-3: Cancel during "Unpacking" was ignored; the bundle was swapped and the launcher restarted
        string running = Bundle(Path.Combine(_root, "CancelApps"), "Tsukihime.app", "running");
        using var server = new FakeGitHub();
        ServeMac(server, "cancel-mac-release", "new");
        UnixLauncherModel model = MacModel(Path.Combine(running, "Contents", "MacOS"), Path.Combine(_root, "cancel-mac-profile"), server.Base);
        bool restarted = false;
        model.RestartRequested = (_, _) => restarted = true;
        foreach (string when in new[] { "ditto", "codesign" })
        {
            // pressed while ditto unpacks, then while codesign verifies
            model.MacTools = new MacTools((zip, folder) =>
            {
                UnixUpdateTestKit.Ditto(zip, folder);
                if (when == "ditto") { model.CancelUpdate(); }
                return null;
            }, _ =>
            {
                if (when == "codesign") { model.CancelUpdate(); }
                return null;
            });
            await model.CheckForUpdatesAsync(manual: true);
            await model.RunUpdateAsync();
            Assert.Equal(UnixUpdateStage.Offer, model.UpdateStage);
            Assert.Equal(LauncherTexts.UpdateCancelled, model.UpdateMessage);
            Assert.False(restarted, when);
            Assert.Equal("running", VersionText(running));
            Assert.False(Directory.Exists(PlatformUpdate.MacStaging(running).AppDirectory), when);
        }
    }

    [Fact]
    public async Task ACancelWhileCodesignRunsEndsThePreparedMacUpdate()
    {
        // review mac-update-3: the check after the unpacking, on its own
        string running = Bundle(Path.Combine(_root, "CancelPrepare"), "Tsukihime.app", "running");
        string archive = MacRelease.Zip(MacRelease.Folder(_root, name: "cancel-prepare-release"), Path.Combine(_root, "cancel-prepare-zip", MacAsset()));
        using var server = new FakeGitHub();
        ReleaseInfo release = await Release(server, archive);
        UpdateStaging staging = PlatformUpdate.MacStaging(running);
        using var cancel = new CancellationTokenSource();
        var tools = new MacTools((zip, folder) =>
        {
            UnixUpdateTestKit.Ditto(zip, folder);
            return null;
        }, _ =>
        {
            cancel.Cancel();
            return null;
        });
        using var client = new UpdateClient(UpdateSource.ForLoopback(server.Base), "test");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlatformUpdate.PrepareAsync(client, release, staging, cancel.Token, null, macOs: true, macTools: tools, exchange: Exchange));
        Assert.False(Directory.Exists(staging.AppDirectory));
        Assert.Equal("running", VersionText(running));
    }

    [Fact]
    public async Task ADriveWhereMacOSCannotSwapTheAppIsRefusedBeforeTheDownload()
    {
        // review mac-update-2: HFS+, exFAT, FAT and network shares have no RENAME_SWAP; every update downloaded, unpacked and then failed at the swap
        string running = Bundle(Path.Combine(_root, "Volumes", "Games"), "Tsukihime.app", "running");
        string archive = MacRelease.Zip(MacRelease.Folder(_root, name: "noswap-release"), Path.Combine(_root, "noswap-zip", MacAsset()));
        using var server = new FakeGitHub();
        ReleaseInfo release = await Release(server, archive);
        UpdateStaging staging = PlatformUpdate.MacStaging(running);
        var probed = new List<string>();
        bool NoSwap(string source, string target)
        {
            probed.Add(source);
            probed.Add(target);
            throw new IOException("macOS could not exchange the old and new app bundles: Operation not supported (errno 45)");
        }

        using var client = new UpdateClient(UpdateSource.ForLoopback(server.Base), "test");
        UnixUpdateRefusedException refused = await Assert.ThrowsAsync<UnixUpdateRefusedException>(() =>
            PlatformUpdate.PrepareAsync(client, release, staging, CancellationToken.None, null, macOs: true, macTools: UnixUpdateTestKit.MacTools(), exchange: NoSwap));
        Assert.StartsWith("The update button needs Tsukihime.app on an APFS drive, such as your Mac's own;", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith($"or download {MacAsset()} from the Releases page. Your installed app was left as it was.", refused.Message, StringComparison.Ordinal);
        Assert.Contains(refused.Problems, problem => problem.Contains("(errno 45)", StringComparison.Ordinal));
        Assert.Equal(2, probed.Count);
        Assert.All(probed, folder => Assert.Equal(staging.AppDirectory, Path.GetDirectoryName(folder)));
        Assert.DoesNotContain(server.Requests, request => request.Path.StartsWith("/assets/", StringComparison.Ordinal));
        Assert.False(Directory.Exists(staging.AppDirectory));
        Assert.Equal("running", VersionText(running));
    }

    [Fact]
    public void WhenMacOSCannotExchangeTheBundlesTheAppStaysAndTheStagingGoes()
    {
        // review mac-update-2: renameatx_np swaps both or neither, so nothing was changed; the staged copy is not left for the next start
        string running = Bundle(Path.Combine(_root, "noexchange"), "Tsukihime.app", "old");
        UpdateStaging staging = PlatformUpdate.MacStaging(running);
        StageMacBundle(staging, "new");
        bool restarted = false;
        UnixUpdateRefusedException refused = Assert.Throws<UnixUpdateRefusedException>(() => PlatformUpdate.ApplyAndRestart(new UnixStagedUpdate(Info(MacAsset()), staging, null, running),
            (_, _) => restarted = true, (_, _) => throw new IOException("macOS could not exchange the old and new app bundles: Operation not supported (errno 45)")));
        Assert.Equal("The update did not work: macOS could not exchange the old and new app bundles: Operation not supported (errno 45). Your installed app was left as it was.", refused.Message);
        Assert.False(restarted);
        Assert.Equal("old", VersionText(running));
        Assert.False(Directory.Exists(staging.AppDirectory));
    }

    [Fact]
    public async Task AnEmptyStagingFolderWithoutItsMarkerIsRemovedAndTheUpdateGoesOn()
    {
        // review mac-update-6: a launcher stopped between making the folder and writing its marker left an empty folder that refused every later update
        string running = Bundle(Path.Combine(_root, "Empty"), "Tsukihime.app", "running");
        UpdateStaging staging = PlatformUpdate.MacStaging(running);
        Directory.CreateDirectory(staging.AppDirectory);
        string archive = MacRelease.Zip(MacRelease.Folder(_root, name: "empty-release"), Path.Combine(_root, "empty-zip", MacAsset()));
        using var server = new FakeGitHub();
        ReleaseInfo release = await Release(server, archive);
        using var client = new UpdateClient(UpdateSource.ForLoopback(server.Base), "test");
        UnixStagedUpdate staged = await PlatformUpdate.PrepareAsync(client, release, staging, CancellationToken.None, null, macOs: true, macTools: UnixUpdateTestKit.MacTools(), exchange: Exchange);
        Assert.Equal(running, staged.AppBundle);
        Assert.True(File.Exists(Path.Combine(staging.AppDirectory, PlatformUpdate.MacMarker)));
        Assert.True(File.Exists(Path.Combine(staging.FilesDirectory, "Tsukihime.app", "Contents", "Info.plist")));
    }

    [Fact]
    public async Task ThePreUpdateCheckCountsALinuxApplyCutOffAfterTheLauncherMovedAsDone()
    {
        // review linux-deck-5: the check before an update read the journal with the Windows launcher's name and put the old launcher back over the new one
        string install = LinuxRelease.Folder(_root, "precheck-install");
        var staging = new UpdateStaging(install);
        string launcher = Path.Combine(Path.GetFullPath(install), "TsukihimeLauncher");
        File.Move(launcher, launcher + UpdateInstaller.OldSuffix);
        File.WriteAllBytes(launcher, LinuxRelease.Elf(2, "new launcher"));
        Directory.CreateDirectory(staging.Root);
        File.WriteAllLines(staging.Journal, ["old\t" + launcher, "new\t" + launcher]);
        // the release's checksum file lacks its line, so the update ends right after that check
        string name = PlatformUpdate.AssetNameFor(Version, "linux", Architecture.X64);
        string archive = LinuxRelease.Tar(LinuxRelease.Folder(_root, "precheck-release"), Path.Combine(_root, "precheck-assets", name));
        string sums = Path.Combine(_root, "precheck-assets", UpdateSource.SumsAssetName);
        File.WriteAllText(sums, ReleaseSums.Sha256Hex(archive) + "  " + UpdateSource.ZipAssetName(Version) + "\n");
        using var server = new FakeGitHub();
        server.List(Repo, server.Release(Repo, Version.Tag, [(name, archive), (UpdateSource.SumsAssetName, sums)]));
        var model = Model(install, Path.Combine(_root, "precheck-profile"), server.Base);
        model.RestartRequested = (_, _) => throw new InvalidOperationException("never");
        await model.CheckForUpdatesAsync(manual: true);
        await model.RunUpdateAsync();
        Assert.Contains("has no checksum for " + name, model.UpdateMessage, StringComparison.Ordinal);
        Assert.Equal(LinuxRelease.Elf(2, "new launcher"), File.ReadAllBytes(launcher));
        Assert.False(File.Exists(launcher + UpdateInstaller.OldSuffix));
    }

    [Fact]
    public async Task ALaterLinuxReleaseWithOtherFilesInstallsThroughTheModel()
    {
        // decision D1 of v0.1.7 (review linux-deck-2): one licence fewer, the next FFmpeg and a new 0755 file; release-sums alone holds a release to the lists
        string install = LinuxRelease.Folder(_root, "later-install");
        string release = LinuxRelease.Folder(_root, "later-release");
        File.Delete(Path.Combine(release, "licenses", "Tmds-DBus-MIT.txt"));
        File.Move(Path.Combine(release, "libavcodec.so.63"), Path.Combine(release, "libavcodec.so.64"));
        File.WriteAllText(Path.Combine(release, "tsukihime-steam.sh"), "#!/bin/sh\n");
        string name = PlatformUpdate.AssetNameFor(Version, "linux", Architecture.X64);
        string archive = LinuxRelease.Tar(release, Path.Combine(_root, "later-assets", name), (relative, mode) => relative == "tsukihime-steam.sh" ? ReleaseRules.Mode755 : mode);
        string sums = ReleaseFiles.Sums(Path.Combine(_root, "later-assets", UpdateSource.SumsAssetName), archive);
        using var server = new FakeGitHub();
        server.List(Repo, server.Release(Repo, Version.Tag, [(name, archive), (UpdateSource.SumsAssetName, sums)]));
        var model = Model(install, Path.Combine(_root, "later-profile"), server.Base);
        int restarts = 0;
        model.RestartRequested = (_, _) => restarts++;
        await model.CheckForUpdatesAsync(manual: true);
        await model.RunUpdateAsync();
        Assert.True(model.UpdateStage == UnixUpdateStage.Restarting, model.UpdateMessage);
        Assert.Equal(1, restarts);
        Assert.True(File.Exists(Path.Combine(install, "libavcodec.so.64")));
        Assert.True(File.Exists(Path.Combine(install, "tsukihime-steam.sh")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(ReleaseRules.Mode755, File.GetUnixFileMode(Path.Combine(install, "tsukihime-steam.sh")));
        }
    }

    [Fact]
    public async Task AReleaseTheLauncherWillNotInstallSaysWhatToDoAndLogsEveryReason()
    {
        // review linux-deck-4: the player was told to set <Version> and publish again; launcher.log gets the reasons, the overlay the way out
        string install = LinuxRelease.Folder(_root, "refused-install");
        string release = LinuxRelease.Folder(_root, "refused-release");
        Directory.CreateDirectory(Path.Combine(release, "assets"));
        File.WriteAllBytes(Path.Combine(release, "assets", "screenshot.png"), [1]);
        File.WriteAllText(Path.Combine(release, ReleaseCheck.PersonalNote), "personal");
        string name = PlatformUpdate.AssetNameFor(Version, "linux", Architecture.X64);
        string archive = LinuxRelease.Tar(release, Path.Combine(_root, "refused-assets", name));
        string sums = ReleaseFiles.Sums(Path.Combine(_root, "refused-assets", UpdateSource.SumsAssetName), archive);
        using var server = new FakeGitHub();
        server.List(Repo, server.Release(Repo, Version.Tag, [(name, archive), (UpdateSource.SumsAssetName, sums)]));
        var model = Model(install, Path.Combine(_root, "refused-profile"), server.Base);
        model.RestartRequested = (_, _) => throw new InvalidOperationException("never");
        string[] before = Snapshot(install);
        await model.CheckForUpdatesAsync(manual: true);
        await model.RunUpdateAsync();
        Assert.Equal(UnixUpdateStage.Failed, model.UpdateStage);
        Assert.Equal($"The update did not work: this launcher cannot install {name} (launcher.log says why). Nothing was changed. Download {name} from the Releases page and replace the old folder by hand; your saves and settings stay where they are.", model.UpdateMessage);
        Assert.Equal(model.UpdateMessage, model.Toast);
        IReadOnlyList<string> log = model.Log.Lines();
        Assert.Contains(log, line => line.EndsWith($"update: refused {name}: 2 problem(s)", StringComparison.Ordinal));
        Assert.Contains(log, line => line.EndsWith("  assets/screenshot.png is a picture; a release carries none but the README's logo (assets/branding/tsukihime-logo.png)", StringComparison.Ordinal));
        Assert.Contains(log, line => line.EndsWith($"  holds {ReleaseCheck.PersonalNote}: a personal build is never released", StringComparison.Ordinal));
        Assert.Equal(before, Snapshot(install));
        Assert.False(Directory.Exists(Path.Combine(install, UpdateStaging.FolderName)));
    }

    [Fact]
    public async Task TheHelpPageSaysCheckingForUpdatesWhileACheckRuns()
    {
        // review launcher-ui-6: the Windows launcher says so; the Help page kept its old line until the answer came
        string install = LinuxRelease.Folder(_root, "checking-install");
        using var server = new FakeGitHub();
        server.List(Repo);
        var model = Model(install, Path.Combine(_root, "checking-profile"), server.Base);
        var shown = new List<string>();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(model.UpdateStatus)) { shown.Add(model.UpdateStatus); }
        };
        await model.CheckForUpdatesAsync(manual: false);
        Assert.Contains(LauncherTexts.Checking, shown);
        Assert.StartsWith("You have the newest version, v0.0.1 (checked at ", model.UpdateStatus, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Applications", true)]
    [InlineData("/Applications/Games", true)]
    [InlineData("/Applications/Tsukihime.app/Contents/MacOS", false)]
    [InlineData("/Users/ann/Applications", false)]
    [InlineData("/Volumes/Games", false)]
    public void AStandardAccountInApplicationsIsSentToItsOwnApplicationsFolder(string folder, bool applications)
    {
        // reviews macos-4, mac-update-4, mac-journey-9: a standard account was told to move the app "into Applications", where it already was
        string message = UnixUpdateChecks.Message(folder, mac: true, translocated: false, "Tsukihime-v0.1.7-macos-arm64.zip");
        Assert.Equal(applications, message.StartsWith($"Only an administrator account can change {folder}. ", StringComparison.Ordinal));
        Assert.Equal(applications, message.Contains("~/Applications", StringComparison.Ordinal));
        Assert.Equal(!applications, message.Contains("Move Tsukihime.app into Applications (or another folder of your own)", StringComparison.Ordinal));
        Assert.EndsWith("download Tsukihime-v0.1.7-macos-arm64.zip from the Releases page. Your saves and settings stay where they are.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMacSentenceNamesTheRunningBundleAndItsFolderWithoutATrailingSeparator()
    {
        // review K48 -01 and launcher-ui-8: Archive Utility names a second download "Tsukihime 2.app"; the folder is named as Finder shows it
        string folder = Path.Combine(_root, "not-there");
        string launcher = Path.Combine(folder, "Tsukihime 2.app", "Contents", "MacOS") + Path.DirectorySeparatorChar;
        UnixUpdateChecks.Problem problem = Assert.IsType<UnixUpdateChecks.Problem>(UnixUpdateChecks.NotWritable(launcher, UnixUpdateChecks.UpdateFolder(launcher, mac: true), mac: true, MacAsset()));
        Assert.Equal(Path.GetFullPath(folder), problem.Folder);
        Assert.StartsWith($"The folder of Tsukihime 2.app cannot be changed by your account: {Path.GetFullPath(folder)}. Move Tsukihime 2.app into Applications", problem.Message, StringComparison.Ordinal);
        Directory.CreateDirectory(folder);
        problem = Assert.IsType<UnixUpdateChecks.Problem>(UnixUpdateChecks.NotWritable(launcher, folder, mac: true, MacAsset()));
        Assert.Equal(Path.TrimEndingDirectorySeparator(launcher), problem.Folder);
        Assert.Contains($"by your account: {Path.TrimEndingDirectorySeparator(launcher)}. ", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALauncherOutsideABundleCannotUpdateOnAMac()
    {
        string folder = Path.Combine(_root, "bin", "Debug");
        Directory.CreateDirectory(folder);
        Assert.Contains("does not run from an app bundle", Assert.Throws<UpdateRefusedException>(() => PlatformUpdate.BundleOf(PlatformUpdate.MacStaging(PlatformUpdate.MacBundle(folder)))).Message, StringComparison.Ordinal);
        string unplisted = Path.Combine(_root, "NoPlist.app");
        Directory.CreateDirectory(Path.Combine(unplisted, "Contents", "MacOS"));
        Assert.Throws<UpdateRefusedException>(() => PlatformUpdate.BundleOf(PlatformUpdate.MacStaging(unplisted)));
    }

    private static string Bundle(string parent, string name, string version)
    {
        string app = MacRelease.Folder(parent, name: "bundle-" + version + "-" + Guid.NewGuid().ToString("N")[..6]);
        string target = Path.Combine(parent, name);
        Directory.Move(app, target);
        Directory.Delete(Path.GetDirectoryName(app)!);
        File.WriteAllText(Path.Combine(target, "Contents", "Resources", "version.txt"), version);
        return target;
    }

    private static void StageMacBundle(UpdateStaging staging, string version)
    {
        Directory.CreateDirectory(staging.DownloadDirectory);
        File.WriteAllText(Path.Combine(staging.AppDirectory, PlatformUpdate.MacMarker), "marker");
        string app = MacRelease.Folder(staging.FilesDirectory, name: string.Empty);
        File.WriteAllText(Path.Combine(app, "Contents", "Resources", "version.txt"), version);
    }

    private static string VersionText(string app) => File.ReadAllText(Path.Combine(app, "Contents", "Resources", "version.txt"));

    private static string MacAsset() => PlatformUpdate.AssetNameFor(Version, "macos", Architecture.Arm64);

    private async Task<ReleaseInfo> Release(FakeGitHub server, string archive)
    {
        string sums = ReleaseFiles.Sums(Path.Combine(Path.GetDirectoryName(archive)!, UpdateSource.SumsAssetName), archive);
        server.List(Repo, server.Release(Repo, Version.Tag, [(Path.GetFileName(archive), archive), (UpdateSource.SumsAssetName, sums)]));
        using var client = new UpdateClient(UpdateSource.ForLoopback(server.Base), "test");
        UpdateCheckResult result = await client.CheckAsync(new ReleaseVersion(0, 0, 1), CancellationToken.None, MacAssetName);
        return result.Update ?? throw new InvalidOperationException(result.Error);
    }

    private static string MacAssetName(ReleaseVersion version) => PlatformUpdate.AssetNameFor(version, "macos", Architecture.Arm64);

    /// <summary>Makes <paramref name="file"/> in <paramref name="folder"/> impossible to rename or delete until disposed: held open on Windows, a read-only folder on Unix.</summary>
    private static IDisposable Block(string folder, string file)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(Path.Combine(folder, file), FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        Chmod(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new Restore(() => Chmod(folder, ReleaseRules.Mode755));
    }

    /// <summary>Skips a test whose failure is made with a read-only folder when this account ignores folder permissions (root).</summary>
    private void RequireEnforcedPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string folder = Path.Combine(_root, "permission-probe");
        Directory.CreateDirectory(folder);
        Chmod(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        bool ignored = CanWriteIn(folder);
        Chmod(folder, ReleaseRules.Mode755);
        if (ignored)
        {
            Assert.Skip("this account ignores folder permissions (root)");
        }
    }

    private static void Chmod(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, mode); }
    }

    private static bool CanWriteIn(string folder)
    {
        string probe = Path.Combine(folder, ".probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private static bool Exchange(string source, string target) => UnixUpdateTestKit.Exchange(source, target);

    /// <summary>A launcher of the Linux layout, on any host (a Mac's would take the bundle path: reviews macos-1, mac-ci-1).</summary>
    private static UnixLauncherModel Model(string install, string profile, Uri api) => new(new LauncherPaths(profile), install, Path.Combine(install, "no-game"))
    {
        UpdateSource = UpdateSource.ForLoopback(api),
        AssetName = version => PlatformUpdate.AssetNameFor(version, "linux", Architecture.X64),
        CurrentVersionOverride = new ReleaseVersion(0, 0, 1),
        MacBundleUpdate = false,
    };

    /// <summary>A launcher of the Mac layout (BUNDLE/Contents/MacOS), on any host: stand-ins for ditto, codesign and renameatx_np.</summary>
    private static UnixLauncherModel MacModel(string launcherDirectory, string profile, Uri api) => new(new LauncherPaths(profile), launcherDirectory, Path.Combine(profile, "no-game"))
    {
        UpdateSource = UpdateSource.ForLoopback(api),
        AssetName = MacAssetName,
        CurrentVersionOverride = new ReleaseVersion(0, 0, 1),
        MacBundleUpdate = true,
        MacTools = UnixUpdateTestKit.MacTools(),
        MacExchange = Exchange,
    };

    /// <summary>Serves the Mac ZIP of the app <paramref name="name"/> (Contents/Resources/version.txt says <paramref name="version"/>) as this build's release.</summary>
    private string ServeMac(FakeGitHub server, string name, string version)
    {
        string next = MacRelease.Folder(_root, name: name);
        File.WriteAllText(Path.Combine(next, "Contents", "Resources", "version.txt"), version);
        string archive = MacRelease.Zip(next, Path.Combine(_root, name + "-zip", MacAsset()));
        string sums = ReleaseFiles.Sums(Path.Combine(Path.GetDirectoryName(archive)!, UpdateSource.SumsAssetName), archive);
        server.List(Repo, server.Release(Repo, Version.Tag, [(MacAsset(), archive), (UpdateSource.SumsAssetName, sums)]));
        return archive;
    }

    private static ReleaseInfo Info(string archive) => new(Version, Version.Tag, "test", "notes", null, false,
        new ReleaseAsset(Path.GetFileName(archive), new Uri("http://127.0.0.1/asset"), File.Exists(archive) ? new FileInfo(archive).Length : 0),
        new ReleaseAsset(UpdateSource.SumsAssetName, new Uri("http://127.0.0.1/sums"), 1));

    private static string[] Snapshot(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(folder, file) + ":" + ReleaseSums.Sha256Hex(file)).ToArray();
}

using System.Diagnostics;
using Tsuki.Cli.Commands;
using Tsuki.Launcher.Core;
using Tsuki.Tests.Data.Cli;
using Tsuki.Tests.Synthetic.Launcher;

namespace Tsuki.Tests.Avalonia;

public sealed class LinuxUpdateEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k48-linux-e2e-" + Guid.NewGuid().ToString("N"));

    public LinuxUpdateEndToEndTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (Process process in Process.GetProcessesByName("TsukihimeLauncher"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName.StartsWith(_root, StringComparison.Ordinal) == true && !process.HasExited) { process.Kill(); }
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task TheRealLinuxLauncherUpdatesRestartsAndCleansUp()
    {
        string publish = Publish();
        string install = CopyInstall(publish, Path.Combine(_root, "install"));
        string release = CopyInstall(publish, Path.Combine(_root, "release"));
        File.WriteAllText(Path.Combine(install, "README.md"), "old");
        File.WriteAllText(Path.Combine(release, "README.md"), "new");
        File.WriteAllText(Path.Combine(release, "K48_UPDATE_MARK.txt"), "installed by the update");
        AddSyntheticFfmpeg(release);
        ReleaseVersion version = ReleaseFiles.BuildVersion();
        string archiveName = $"Tsukihime-{version.Tag}-linux-x64.tar.gz";
        string archive = LinuxRelease.Tar(release, Path.Combine(_root, archiveName));
        string sums = AllSums(archive, version);
        using var server = new FakeGitHub();
        server.List(UpdateSource.DefaultRepository, server.Release(UpdateSource.DefaultRepository, version.Tag,
            [(archiveName, archive), (UpdateSource.SumsAssetName, sums)]));
        string profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.ini"), "language=en\n");
        File.WriteAllText(Path.Combine(profile, "save.dat"), "synthetic save");
        (string before, string after, int exit) = await Run(install, profile, server.Base);
        Assert.True(exit == 0, before + "\n" + after);
        Assert.Contains("UPDATE SELFTEST PASS", before, StringComparison.Ordinal);
        Assert.Contains("SELFTEST PASS: 7 pages", after, StringComparison.Ordinal);
        Assert.Contains("launcher " + version.Tag, after, StringComparison.Ordinal);
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "README.md")));
        Assert.True(File.Exists(Path.Combine(install, "K48_UPDATE_MARK.txt")));
        Assert.Empty(Directory.GetFiles(install, "*.old", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(install, UpdateStaging.FolderName)));
        Assert.Equal("language=en\n", File.ReadAllText(Path.Combine(profile, "settings.ini")));
        Assert.Equal("synthetic save", File.ReadAllText(Path.Combine(profile, "save.dat")));
    }

    [Fact]
    public async Task TheRealLinuxLauncherRefusesABadChecksumWithoutChangingFiles()
    {
        string install = CopyInstall(Publish(), Path.Combine(_root, "bad-install"));
        string release = CopyInstall(Publish(), Path.Combine(_root, "bad-release"));
        AddSyntheticFfmpeg(release);
        ReleaseVersion version = ReleaseFiles.BuildVersion();
        string archiveName = $"Tsukihime-{version.Tag}-linux-x64.tar.gz";
        string archive = LinuxRelease.Tar(release, Path.Combine(_root, archiveName));
        string sums = Path.Combine(_root, UpdateSource.SumsAssetName);
        File.WriteAllText(sums, new string('0', 64) + "  " + archiveName + "\n");
        using var server = new FakeGitHub();
        server.List(UpdateSource.DefaultRepository, server.Release(UpdateSource.DefaultRepository, version.Tag,
            [(archiveName, archive), (UpdateSource.SumsAssetName, sums)]));
        string[] beforeFiles = Snapshot(install);
        (string before, string after, int exit) = await Run(install, Path.Combine(_root, "bad-profile"), server.Base);
        Assert.True(exit == 1, before);
        Assert.Contains("does not match", before, StringComparison.Ordinal);
        Assert.Equal("(no new launcher report)", after);
        Assert.Equal(beforeFiles, Snapshot(install));
        Assert.False(Directory.Exists(Path.Combine(install, UpdateStaging.FolderName)));
    }

    private string Publish()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("the real process test runs in WSL/Linux"); }
        string? publish = Environment.GetEnvironmentVariable("TSUKI_K48_LINUX_PUBLISH");
        if (string.IsNullOrEmpty(publish) || !File.Exists(Path.Combine(publish, "TsukihimeLauncher")))
        {
            Assert.Skip("TSUKI_K48_LINUX_PUBLISH does not name a self-contained Linux launcher publish");
        }

        return publish;
    }

    private static string CopyInstall(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            if (!OperatingSystem.IsWindows()) { new FileInfo(target).UnixFileMode = new FileInfo(file).UnixFileMode; }
        }

        return to;
    }

    private static void AddSyntheticFfmpeg(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "licenses"));
        foreach (string lib in ReleaseRules.LinuxFfmpeg)
        {
            string path = Path.Combine(folder, lib);
            if (!File.Exists(path)) { File.WriteAllBytes(path, LinuxRelease.Ffmpeg(lib)); }
        }

        string notice = Path.Combine(folder, "licenses", "FFmpeg-SOURCE.txt");
        if (!File.Exists(notice))
        {
            File.WriteAllText(notice, "Synthetic test stand-in for BtbN's linux64-lgpl-shared-9.0 notice\n" + string.Join(" ", ReleaseRules.LinuxFfmpeg) + "\n");
        }
    }

    /// <summary>The four-platform checksum file, each line with its own asset's hash (review K48 -19: one hash on every line hid a wrong line).</summary>
    private static string AllSums(string archive, ReleaseVersion version)
    {
        string folder = Path.GetDirectoryName(archive)!;
        var lines = new List<string>();
        foreach (string name in new[] { UpdateSource.ZipAssetName(version), Path.GetFileName(archive), $"Tsukihime-{version.Tag}-macos-arm64.zip", $"Tsukihime-{version.Tag}-macos-x64.zip" })
        {
            string asset = Path.Combine(folder, name);
            if (!File.Exists(asset)) { File.WriteAllText(asset, "the other platform's asset: " + name); }
            lines.Add(ReleaseSums.Sha256Hex(asset) + "  " + name);
        }

        string path = Path.Combine(folder, UpdateSource.SumsAssetName);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        Assert.Equal(4, ReleaseSums.Parse(File.ReadAllText(path)).Values.Distinct(StringComparer.Ordinal).Count());
        return path;
    }

    private static async Task<(string Before, string After, int Exit)> Run(string install, string profile, Uri api)
    {
        string report = Path.Combine(install, "..", "update-" + Guid.NewGuid().ToString("N") + ".txt");
        var start = new ProcessStartInfo(Path.Combine(install, "TsukihimeLauncher")) { UseShellExecute = false, WorkingDirectory = install, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--update-selftest", report, "--data", profile, "--update-api", api.AbsoluteUri, "--update-current", "0.0.1" }) { start.ArgumentList.Add(argument); }
        start.Environment.Remove("TSUKI_GAME_DIR");
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!await Task.Run(() => process.WaitForExit(120_000)))
        {
            process.Kill();
            throw new TimeoutException(await output + "\n" + await error);
        }

        string before = File.Exists(report) ? File.ReadAllText(report) : await output + "\n" + await error;
        var clock = Stopwatch.StartNew();
        while (!File.Exists(report + ".after.txt") && clock.Elapsed < TimeSpan.FromSeconds(30) && process.ExitCode == 0) { await Task.Delay(100); }
        string after = File.Exists(report + ".after.txt") ? File.ReadAllText(report + ".after.txt") : "(no new launcher report)";
        return (before + "\n" + await error, after, process.ExitCode);
    }

    private static string[] Snapshot(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(folder, file) + ":" + ReleaseSums.Sha256Hex(file)).ToArray();
}

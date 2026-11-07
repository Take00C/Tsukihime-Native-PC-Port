using System.Diagnostics;
using System.Text.RegularExpressions;
using Tsuki.Launcher.Core;
using Tsuki.Tests.Synthetic.Launcher;

namespace Tsuki.Tests.Avalonia;

/// <summary>
/// The real Mac update (review mac-update-5), the twin of <see cref="LinuxUpdateEndToEndTests"/>: the launcher of a real, signed Tsukihime-vX.Y.Z-macos-ARCH.zip
/// (TSUKI_MAC_RELEASE_ZIP: the workflow's own ZIP) installs that same ZIP from a loopback stand-in of GitHub with the system's ditto, codesign and renameatx_np,
/// restarts into the new bundle, which cleans up; the swapped bundle still passes codesign. It runs only on a Mac with that variable set.
/// </summary>
public sealed class MacUpdateEndToEndTests : IDisposable
{
    private readonly string _root = UnixUpdateTestKit.TempRoot("tsuki-mac-e2e-");

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
    public async Task TheRealMacLauncherSwapsItsBundleRestartsAndCleansUp()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Skip("the real Mac update runs on macOS"); }
        string? zip = Environment.GetEnvironmentVariable("TSUKI_MAC_RELEASE_ZIP");
        if (string.IsNullOrEmpty(zip) || !File.Exists(zip)) { Assert.Skip("TSUKI_MAC_RELEASE_ZIP does not name a signed Tsukihime-vX.Y.Z-macos-ARCH.zip"); }
        string name = Path.GetFileName(zip);
        Match release = Regex.Match(name, @"^Tsukihime-(v\d+\.\d+\.\d+)-macos-(arm64|x64)\.zip$", RegexOptions.CultureInvariant);
        Assert.True(release.Success, name);
        string tag = release.Groups[1].Value;

        string installed = Path.Combine(_root, "old");
        Assert.Equal(string.Empty, Run("/usr/bin/ditto", ["-x", "-k", zip, installed]));
        string app = Path.Combine(installed, "Tsukihime.app");
        string assets = Path.Combine(_root, "assets");
        Directory.CreateDirectory(assets);
        string sums = ReleaseFiles.Sums(Path.Combine(assets, UpdateSource.SumsAssetName), zip);
        using var server = new FakeGitHub();
        server.List(UpdateSource.DefaultRepository, server.Release(UpdateSource.DefaultRepository, tag, [(name, zip), (UpdateSource.SumsAssetName, sums)]));
        string profile = Path.Combine(_root, "profile");
        string report = Path.Combine(_root, "update.txt");

        var start = new ProcessStartInfo(Path.Combine(app, "Contents", "MacOS", "TsukihimeLauncher")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--update-selftest", report, "--data", profile, "--update-api", server.Base.AbsoluteUri, "--update-current", "0.0.1" }) { start.ArgumentList.Add(argument); }
        start.Environment.Remove("TSUKI_GAME_DIR");
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        if (!await Task.Run(() => process.WaitForExit(240_000), TestContext.Current.CancellationToken))
        {
            process.Kill();
            throw new TimeoutException(await output + "\n" + await error);
        }

        string before = (File.Exists(report) ? File.ReadAllText(report) : await output) + "\n" + await error;
        Assert.True(process.ExitCode == 0, before);
        Assert.Contains("UPDATE SELFTEST PASS", before, StringComparison.Ordinal);

        // the new launcher waits for this one to end, removes the staging folder that holds the old bundle, then writes its report
        var clock = Stopwatch.StartNew();
        while (!File.Exists(report + ".after.txt") && clock.Elapsed < TimeSpan.FromSeconds(60)) { await Task.Delay(200, TestContext.Current.CancellationToken); }
        string after = File.Exists(report + ".after.txt") ? File.ReadAllText(report + ".after.txt") : "(no new launcher report)";
        Assert.Contains("SELFTEST PASS", after, StringComparison.Ordinal);
        Assert.Contains($"Updated from v0.0.1 to {tag}.", after, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(installed, ".Tsukihime.app.update")), after);
        Assert.Equal(["Tsukihime.app"], Directory.GetFileSystemEntries(installed).Select(Path.GetFileName));
        Assert.Equal(string.Empty, Run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app]));
    }

    /// <summary>Runs a program; empty when it exits with 0, else what it said.</summary>
    private static string Run(string program, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        if (!process.WaitForExit(120_000))
        {
            process.Kill();
            return program + " did not finish within 120 seconds";
        }

        return process.ExitCode == 0 ? string.Empty : $"{program} exited with {process.ExitCode}: {error.GetAwaiter().GetResult()} {output.GetAwaiter().GetResult()}";
    }
}

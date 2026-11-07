using System.Diagnostics;
using Tsuki.Cli.Commands;
using Tsuki.Data.Icons;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher.Avalonia;

/// <summary>
/// The macOS programs the Mac update runs. <paramref name="Unpack"/> unpacks the checked ZIP into a folder (ditto -x -k: it gives the non-Mach-O files of
/// Contents/MacOS their code-signature attributes back from ditto's metadata entries); <paramref name="Verify"/> checks the unpacked app (codesign --verify --deep
/// --strict). Each returns why it failed, or null. The tests pass stand-ins.
/// </summary>
public sealed record MacTools(Func<string, string, string?> Unpack, Func<string, string?> Verify)
{
    /// <summary>The seconds either program may take.</summary>
    public const int TimeoutSeconds = 120;

    /// <summary>ditto and codesign on a Mac; null on other hosts (the tests'), where .NET unpacks the ZIP without its metadata and no signature is verified.</summary>
    public static MacTools? ForHost() => OperatingSystem.IsMacOS()
        ? new MacTools(
            (zip, folder) => Run("/usr/bin/ditto", ["-x", "-k", zip, folder], "ditto could not unpack the downloaded app"),
            app => Run("/usr/bin/codesign", ["--verify", "--deep", "--strict", "--verbose=2", app], "the downloaded app fails codesign verification"))
        : null;

    /// <summary>Runs <paramref name="program"/>; null when it exits with 0, else <paramref name="failure"/> and what it wrote.</summary>
    internal static string? Run(string program, IReadOnlyList<string> arguments, string failure)
    {
        string name = Path.GetFileName(program);
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(start);
            if (process is null)
            {
                return $"{name} could not be started, so the downloaded app was not installed";
            }

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeoutSeconds * 1000))
            {
                process.Kill();
                process.WaitForExit();
                return $"{name} did not finish within {TimeoutSeconds} seconds, so the downloaded app was not installed";
            }

            return process.ExitCode == 0 ? null : $"{failure}: {error.GetAwaiter().GetResult().Trim()} {output.GetAwaiter().GetResult().Trim()}".TrimEnd();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"{name} could not run ({e.Message}), so the downloaded app was not installed";
        }
    }
}

/// <summary>
/// An update the macOS or Linux launcher refuses with a sentence for the player (<see cref="Exception.Message"/>, shown as it is), and the reasons for launcher.log
/// (<paramref name="problems"/>): a release this launcher will not install, or a place it cannot install into.
/// </summary>
public sealed class UnixUpdateRefusedException(string message, IReadOnlyList<string> problems) : Exception(message)
{
    /// <summary>Why, for launcher.log.</summary>
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// Unpacks and checks a downloaded macOS or Linux release with the updaters' rules (<see cref="ReleaseRuleSet.Update"/>): every entry is checked before anything is
/// written (safe names, folders and plain files, no set-id bit, nothing others may write, nothing forbidden, no personal note, the limits), then the unpacked
/// release must hold both programs of the right architecture with what they need to start, and both assemblies must carry the release's version and no personal
/// mark. The lists of libraries, licences and notices of this version are release-sums' (<see cref="ReleaseRuleSet.Complete"/>), not the updater's: an installed
/// launcher must take later releases whose files change. A refusal says to download the release by hand; launcher.log gets the reasons.
/// </summary>
internal static class UnixReleaseArchive
{
    /// <summary>The overlay's sentence when the downloaded <paramref name="asset"/> is not a release this launcher installs (the reasons go to launcher.log).</summary>
    public static string RefusedSentence(string asset, bool mac) =>
        $"The update did not work: this launcher cannot install {asset} (launcher.log says why). Nothing was changed. Download {asset} from the Releases page and replace the old {(mac ? "app" : "folder")} by hand; your saves and settings stay where they are.";

    public static StagedRelease ExtractLinux(string archivePath, string destination, ReleaseVersion expected, CancellationToken cancel, ReleaseLimits? limits = null)
    {
        var problems = new List<string>();
        string root = ReleaseRules.UnpackLinuxTar(archivePath, destination, problems, limits, cancel, ReleaseRuleSet.Update);
        if (problems.Count == 0)
        {
            ReleaseRules.CheckLinuxUpdate(root, expected.ToString(), problems);
        }

        if (problems.Count > 0)
        {
            throw new UnixUpdateRefusedException(RefusedSentence(Path.GetFileName(archivePath), mac: false), problems);
        }

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        return new StagedRelease(root, files, PeIcons.Marks(Path.Combine(root, "TsukihimeLauncher.dll"))?.InformationalVersion);
    }

    /// <summary>
    /// Unpacks the Mac ZIP into destination/Tsukihime.app and checks it. <paramref name="tools"/> (a Mac) unpacks with ditto and verifies the code signature
    /// (codesign --verify --deep --strict, the last check); without them .NET unpacks and the signature's files are only checked to be there.
    /// </summary>
    public static string ExtractMac(string archivePath, string destination, ReleaseInfo release, CancellationToken cancel, MacTools? tools = null, ReleaseLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        string arch = release.Zip.Name == PlatformUpdate.AssetNameFor(release.Version, "macos", System.Runtime.InteropServices.Architecture.X64) ? "x64"
            : release.Zip.Name == PlatformUpdate.AssetNameFor(release.Version, "macos", System.Runtime.InteropServices.Architecture.Arm64) ? "arm64"
            : throw new UpdateRefusedException($"{release.Zip.Name} is not a Mac release of {release.Tag}");
        var problems = new List<string>();
        string app = ReleaseRules.UnpackMacZip(archivePath, destination, problems, limits, tools?.Unpack, cancel, ReleaseRuleSet.Update);
        if (problems.Count == 0)
        {
            ReleaseRules.CheckMacUpdate(app, arch, release.Version.ToString(), problems);
            if (!OperatingSystem.IsWindows())
            {
                // ditto, not .NET, gave the files their modes: both programs must have come out runnable and safe
                foreach (string name in ReleaseRules.MacHosts.Where(name => File.Exists(Path.Combine(app, "Contents", "MacOS", name))))
                {
                    UnixFileMode mode = File.GetUnixFileMode(Path.Combine(app, "Contents", "MacOS", name));
                    if (ReleaseRules.ModePolicyProblem((int)mode, directory: false, program: true) is { } why)
                    {
                        problems.Add($"Contents/MacOS/{name} was unpacked with the mode {Convert.ToString((int)mode, 8)}: {why}");
                    }
                }
            }
        }

        // a Cancel pressed while ditto ran ends the update before codesign
        cancel.ThrowIfCancellationRequested();
        if (problems.Count == 0 && tools?.Verify(app) is { } signature)
        {
            problems.Add(signature);
        }

        if (problems.Count > 0)
        {
            throw new UnixUpdateRefusedException(RefusedSentence(release.Zip.Name, mac: true), problems);
        }

        return app;
    }
}

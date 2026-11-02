// The steps of an update between the player's "Update now" and the restart (package K40): download SHA256SUMS.txt and the zip, check the zip's SHA-256 against the
// list, unpack it into the staging folder, check what was unpacked. Nothing in the launcher's folder changes before UpdateInstaller.Apply; a failure or a Cancel up
// to there only removes the staging folder. Also: which version is running, and whether this is a personal build (package K28), which never updates.
using System.Globalization;
using System.Net.Http;
using System.Reflection;

namespace Tsuki.Launcher.Core;

/// <summary>The step an update is at.</summary>
public enum UpdateStep
{
    /// <summary>Downloading the checksum file and the zip.</summary>
    Downloading,

    /// <summary>Computing the zip's SHA-256.</summary>
    Verifying,

    /// <summary>Unpacking into the staging folder.</summary>
    Unpacking,

    /// <summary>Replacing the files.</summary>
    Installing,
}

/// <summary>Where an update is.</summary>
/// <param name="Step">The step.</param>
/// <param name="Download">The bytes of the zip so far (the download step).</param>
public readonly record struct UpdateProgress(UpdateStep Step, DownloadProgress Download);

/// <summary>The running launcher: its version and whether it may update.</summary>
/// <param name="InformationalVersion">The assembly's informational version ("0.1.3+&lt;commit&gt;").</param>
/// <param name="Version">Its X.Y.Z (0.0.0 when it has none).</param>
/// <param name="IsPersonal">A personal build (package K28): the TsukiPersonalBuild metadata, a "-personal" version or PERSONAL_BUILD.txt beside the program. It never checks or updates.</param>
public sealed record LauncherBuild(string InformationalVersion, ReleaseVersion Version, bool IsPersonal)
{
    /// <summary>The metadata key of a personal build (src/Tsuki.PersonalIcon.targets).</summary>
    public const string PersonalKey = "TsukiPersonalBuild";

    /// <summary>Reads the build of <paramref name="assembly"/> installed in <paramref name="appDirectory"/>.</summary>
    public static LauncherBuild Of(Assembly assembly, string appDirectory)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        ReleaseVersion version = ReleaseVersion.TryParseInformational(info, out ReleaseVersion v) ? v : default;
        bool personal = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(m => m.Key == PersonalKey && string.Equals(m.Value, "true", StringComparison.OrdinalIgnoreCase))
            || info.Contains("-personal", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(appDirectory, UpdateInstaller.PersonalNote));
        return new LauncherBuild(info, version, personal);
    }

    /// <summary>The User-Agent of the update check: "TsukihimeLauncher/0.1.3 (+https://github.com/owner/name)".</summary>
    public string UserAgent(UpdateSource source) => $"TsukihimeLauncher/{Version} (+https://github.com/{source?.Repository ?? UpdateSource.DefaultRepository})";
}

/// <summary>Download, verify, unpack, check.</summary>
public static class Updater
{
    /// <summary>The free disk space an update needs, as a multiple of the zip's size (the zip, its unpacked files and the .old copies until the restart).</summary>
    public const int SpaceFactor = 4;

    /// <summary>
    /// Null when the drive of <paramref name="appDirectory"/> has room for an update of <paramref name="zipSize"/> bytes (or the free space cannot be read), else a
    /// sentence for the overlay.
    /// </summary>
    public static string? SpaceProblem(string appDirectory, long zipSize)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(appDirectory));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || zipSize <= 0)
            {
                return null;
            }

            long free = new DriveInfo(root).AvailableFreeSpace;
            long need = (zipSize * SpaceFactor) + (64L * 1024 * 1024);
            return free >= need ? null
                : $"There is not enough room on {root} for the update: it needs about {GameFolderCheck.FormatSize(need)}, {GameFolderCheck.FormatSize(free)} are free.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads and checks <paramref name="release"/> into <paramref name="staging"/> (emptied first): SHA256SUMS.txt (it must list the zip), the zip (its SHA-256 must
    /// be the listed one), the unpacked files (<see cref="UpdateInstaller.Check"/>). Throws <see cref="UpdateRefusedException"/> for a release that must not be installed,
    /// <see cref="HttpRequestException"/> / <see cref="IOException"/> for trouble on the way, <see cref="OperationCanceledException"/> for the player's Cancel; in each
    /// case the staging folder is removed again.
    /// </summary>
    public static async Task<StagedRelease> PrepareAsync(UpdateClient client, ReleaseInfo release, UpdateStaging staging, IProgress<UpdateProgress>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(staging);
        UpdateInstaller.DeleteTree(staging.Root);
        if (Directory.Exists(staging.Root))
        {
            throw new IOException($"the staging folder {staging.Root} is still in use; restart the launcher and try again");
        }

        try
        {
            Directory.CreateDirectory(staging.DownloadDirectory);
            progress?.Report(new UpdateProgress(UpdateStep.Downloading, new DownloadProgress(0, release.Zip.Size)));
            string sums = await client.DownloadTextAsync(release.Sums.Url, ReleaseSums.MaxBytes, cancel).ConfigureAwait(false);
            if (!ReleaseSums.Parse(sums).ContainsKey(release.Zip.Name))
            {
                throw new UpdateRefusedException($"{UpdateSource.SumsAssetName} of {release.Tag} has no checksum for {release.Zip.Name}, so the download could not be checked");
            }

            string zip = Path.Combine(staging.DownloadDirectory, release.Zip.Name);
            await client.DownloadFileAsync(release.Zip.Url, zip, release.Zip.Size, progress is null ? null : new Relay(progress), cancel).ConfigureAwait(false);

            progress?.Report(new UpdateProgress(UpdateStep.Verifying, default));
            string hex = await ReleaseSums.Sha256HexAsync(zip, cancel).ConfigureAwait(false);
            switch (ReleaseSums.Check(sums, release.Zip.Name, hex))
            {
                case SumCheck.Mismatch:
                    throw new UpdateRefusedException($"the downloaded {release.Zip.Name} does not match its checksum in {UpdateSource.SumsAssetName} (SHA-256 {hex[..12]}...), so it was not installed");
                case SumCheck.Missing:
                    throw new UpdateRefusedException($"{UpdateSource.SumsAssetName} has no checksum for {release.Zip.Name}");
            }

            progress?.Report(new UpdateProgress(UpdateStep.Unpacking, default));
            return await Task.Run(
                () =>
                {
                    UpdateInstaller.Extract(zip, staging.FilesDirectory, cancel);
                    return UpdateInstaller.Check(staging.FilesDirectory, release.Version);
                },
                cancel).ConfigureAwait(false);
        }
        catch
        {
            UpdateInstaller.DeleteTree(staging.Root);
            throw;
        }
    }

    /// <summary>The words of a step for the overlay.</summary>
    public static string Describe(UpdateProgress p) => p.Step switch
    {
        UpdateStep.Downloading when p.Download.Total > 0 => string.Create(
            CultureInfo.InvariantCulture,
            $"Downloading: {GameFolderCheck.FormatSize(p.Download.Received)} of {GameFolderCheck.FormatSize(p.Download.Total)} ({(int)(p.Download.Fraction * 100)} %)"),
        UpdateStep.Downloading => $"Downloading: {GameFolderCheck.FormatSize(p.Download.Received)}",
        UpdateStep.Verifying => "Checking the download against SHA256SUMS.txt...",
        UpdateStep.Unpacking => "Unpacking the new version...",
        _ => "Replacing the files...",
    };

    /// <summary>Passes the zip's bytes on as the download step (no SynchronizationContext hop of its own: the caller's Progress does that).</summary>
    private sealed class Relay(IProgress<UpdateProgress> target) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => target.Report(new UpdateProgress(UpdateStep.Downloading, value));
    }
}

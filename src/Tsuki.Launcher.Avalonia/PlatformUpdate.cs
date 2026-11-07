using System.Runtime.InteropServices;
using Tsuki.Cli.Commands;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher.Avalonia;

/// <summary>A checked release, unpacked and ready to go in.</summary>
/// <param name="Info">The release.</param>
/// <param name="Staging">Where it was unpacked.</param>
/// <param name="Flat">Linux: the unpacked folder, applied file by file.</param>
/// <param name="AppBundle">macOS: the running bundle, which the unpacked Tsukihime.app replaces as a whole.</param>
public sealed record UnixStagedUpdate(ReleaseInfo Info, UpdateStaging Staging, StagedRelease? Flat, string? AppBundle);

public static partial class PlatformUpdate
{
    /// <summary>The marker of the Mac staging folder: a folder beside the app is deleted only when it carries this file.</summary>
    public const string MacMarker = "TSUKIHIME_UPDATE.txt";

    /// <summary>The launcher's program (Linux: the last file an update replaces; macOS: Contents/MacOS/TsukihimeLauncher).</summary>
    public const string LauncherProgram = "TsukihimeLauncher";

    /// <summary>The ending of the Mac staging folder's name: ".NAME.app.update" beside NAME.app.</summary>
    private const string MacStagingSuffix = ".update";

    /// <summary>renameatx_np's flag that exchanges the two paths (RENAME_SWAP of sys/stdio.h).</summary>
    internal const uint RenameSwap = 0x2;

    /// <summary>AT_FDCWD on macOS: the paths are relative to the working folder (they are absolute here).</summary>
    internal const int AtFdcwd = -2;

    public static string AssetName(ReleaseVersion version) => AssetNameFor(
        version,
        OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "win",
        RuntimeInformation.ProcessArchitecture);

    internal static string AssetNameFor(ReleaseVersion version, string platform, Architecture architecture)
    {
        if (platform == "macos")
        {
            string arch = architecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => throw new UpdateRefusedException("this Mac architecture has no release") };
            return $"Tsukihime-{version.Tag}-macos-{arch}.zip";
        }

        return platform switch
        {
            "linux" when architecture == Architecture.X64 => $"Tsukihime-{version.Tag}-linux-x64.tar.gz",
            "linux" => throw new UpdateRefusedException("this Linux architecture has no release (the Linux release is x86-64)"),
            "win" => UpdateSource.ZipAssetName(version),
            _ => throw new ArgumentOutOfRangeException(nameof(platform)),
        };
    }

    /// <summary>
    /// Where an update is staged. Linux: update-staging in the launcher's folder. macOS: the hidden folder ".NAME.app.update" beside the running bundle NAME.app
    /// (<see cref="MacStaging"/>; a rename stays on one volume), never a folder of a generic name in a shared folder such as Applications or Downloads.
    /// </summary>
    public static UpdateStaging StagingDirectory(string launcherDirectory, bool? macOs = null) =>
        (macOs ?? OperatingSystem.IsMacOS()) ? MacStaging(MacBundle(launcherDirectory)) : new UpdateStaging(launcherDirectory);

    /// <summary>The bundle a Mac launcher runs from: its folder is BUNDLE/Contents/MacOS, whatever the bundle is called ("Tsukihime 2.app" too).</summary>
    public static string MacBundle(string launcherDirectory) => Path.GetFullPath(Path.Combine(launcherDirectory, "..", ".."));

    /// <summary>The staging of the Mac bundle <paramref name="bundle"/>: its AppDirectory is the hidden folder ".NAME.app.update" beside the bundle.</summary>
    public static UpdateStaging MacStaging(string bundle)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundle));
        return new UpdateStaging(Path.Combine(Path.GetDirectoryName(full)!, "." + Path.GetFileName(full) + MacStagingSuffix));
    }

    /// <summary>The bundle a Mac staging belongs to (the reverse of <see cref="MacStaging"/>), when it is one: NAME.app with Contents/Info.plist.</summary>
    internal static string BundleOf(UpdateStaging staging)
    {
        string hidden = Path.TrimEndingDirectorySeparator(Path.GetFullPath(staging.AppDirectory));
        string name = Path.GetFileName(hidden);
        string? bundle = name.StartsWith('.') && name.EndsWith(".app" + MacStagingSuffix, StringComparison.Ordinal)
            ? Path.Combine(Path.GetDirectoryName(hidden)!, name[1..^MacStagingSuffix.Length])
            : null;
        return bundle is not null && File.Exists(Path.Combine(bundle, "Contents", "Info.plist"))
            ? bundle
            : throw new UpdateRefusedException("the launcher does not run from an app bundle (NAME.app with Contents/Info.plist), so it cannot replace itself; download the release instead");
    }

    public static IProgress<UpdateProgress> Progress(UnixLauncherModel model) => new Progress<UpdateProgress>(model.Report);

    /// <summary>
    /// Downloads the release's checksum file and archive into <paramref name="staging"/>, checks the archive's SHA-256 and unpacks and checks it
    /// (<see cref="UnixReleaseArchive"/>). On macOS the staging folder is made fresh with its <see cref="MacMarker"/> (one that is in the way without the marker is
    /// never deleted: the update is refused), and before anything is downloaded two empty folders in it are exchanged the way the bundles will be, so that a drive
    /// that cannot do that (not APFS) is refused there. A Cancel during the unpacking ends it too. <paramref name="macOs"/>, <paramref name="macTools"/>,
    /// <paramref name="limits"/> and <paramref name="exchange"/> (renameatx_np) are for the tests.
    /// </summary>
    public static async Task<UnixStagedUpdate> PrepareAsync(UpdateClient client, ReleaseInfo release, UpdateStaging staging, CancellationToken cancel, IProgress<UpdateProgress>? progress,
        bool? macOs = null, MacTools? macTools = null, ReleaseLimits? limits = null, Func<string, string, bool>? exchange = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(staging);
        bool mac = macOs ?? OperatingSystem.IsMacOS();
        string? bundle = mac ? BundleOf(staging) : null;
        if (mac)
        {
            OpenMacStaging(staging.AppDirectory);
            ProbeMacSwap(staging.AppDirectory, bundle!, release.Zip.Name, exchange ?? ((source, target) => ExchangeBundles(source, target)));
        }
        else
        {
            UpdateInstaller.DeleteTree(staging.Root);
        }

        try
        {
            Directory.CreateDirectory(staging.DownloadDirectory);
            progress?.Report(new UpdateProgress(UpdateStep.Downloading, new DownloadProgress(0, release.Zip.Size)));
            string sums = await client.DownloadTextAsync(release.Sums.Url, ReleaseSums.MaxBytes, cancel).ConfigureAwait(false);
            if (!ReleaseSums.Parse(sums).ContainsKey(release.Zip.Name))
            {
                throw new UpdateRefusedException($"{UpdateSource.SumsAssetName} has no checksum for {release.Zip.Name}");
            }

            string archive = Path.Combine(staging.DownloadDirectory, release.Zip.Name);
            await client.DownloadFileAsync(release.Zip.Url, archive, release.Zip.Size, progress is null ? null : new Relay(progress), cancel).ConfigureAwait(false);
            progress?.Report(new UpdateProgress(UpdateStep.Verifying, default));
            string hex = await ReleaseSums.Sha256HexAsync(archive, cancel).ConfigureAwait(false);
            if (ReleaseSums.Check(sums, release.Zip.Name, hex) is not SumCheck.Match)
            {
                throw new UpdateRefusedException($"the downloaded {release.Zip.Name} does not match {UpdateSource.SumsAssetName} (SHA-256 {hex[..12]}...), so it was not installed");
            }

            progress?.Report(new UpdateProgress(UpdateStep.Unpacking, default));
            if (mac)
            {
                MacTools? tools = macTools ?? MacTools.ForHost();
                await Task.Run(() => UnixReleaseArchive.ExtractMac(archive, staging.FilesDirectory, release, cancel, tools, limits), cancel).ConfigureAwait(false);
                // a Cancel pressed while ditto or codesign ran ends the update here, before anything is swapped
                cancel.ThrowIfCancellationRequested();
                return new UnixStagedUpdate(release, staging, null, bundle);
            }

            StagedRelease flat = await Task.Run(() => UnixReleaseArchive.ExtractLinux(archive, staging.FilesDirectory, release.Version, cancel, limits), cancel).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            return new UnixStagedUpdate(release, staging, flat, null);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            Discard();
            throw new UnixUpdateRefusedException(UnixReleaseArchive.RefusedSentence(release.Zip.Name, mac), ["the release archive cannot be read: " + e.Message]);
        }
        catch
        {
            Discard();
            throw;
        }

        void Discard()
        {
            if (mac)
            {
                DeleteMarked(staging.AppDirectory);
            }
            else
            {
                UpdateInstaller.DeleteTree(staging.Root);
            }
        }
    }

    /// <summary>Removes an update that was staged but does not go in (the game was started meanwhile, or Cancel): its marked folder on a Mac, update-staging on Linux.</summary>
    public static void Discard(UnixStagedUpdate staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        if (staged.Flat is null)
        {
            DeleteMarked(staged.Staging.AppDirectory);
        }
        else
        {
            UpdateInstaller.DeleteTree(staged.Staging.Root);
        }
    }

    /// <summary>
    /// Puts a staged update in place and asks for the restart into it; when that restart fails, the old version is put back. Linux: file by file
    /// (<see cref="UpdateInstaller.Apply"/>, the launcher last). macOS: the running bundle and the staged one are exchanged in one step; the old bundle stays in
    /// the staging folder, which the next start deletes; when the exchange fails nothing was changed and the staging folder goes. <paramref name="exchange"/>
    /// replaces renameatx_np in the tests.
    /// </summary>
    public static void ApplyAndRestart(UnixStagedUpdate staged, Action<string, ReleaseVersion> restartRequested, Func<string, string, bool>? exchange = null)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(restartRequested);
        if (staged.Flat is not null)
        {
            string appDirectory = staged.Staging.AppDirectory;
            ApplyResult result = UpdateInstaller.Apply(appDirectory, staged.Flat, staged.Staging.Journal, launcherFileName: LauncherProgram);
            if (!result.Applied)
            {
                if (result.RolledBack)
                {
                    UpdateInstaller.DeleteTree(staged.Staging.Root);
                }

                throw new UpdateRefusedException((result.Problem ?? "the update could not be applied") + (result.RolledBack ? " The previous version was restored." : " Rollback was incomplete; .old files were kept for recovery."));
            }

            UpdateInstaller.DeleteTree(staged.Staging.DownloadDirectory);
            try
            {
                restartRequested(Path.Combine(appDirectory, LauncherProgram), staged.Info.Version);
            }
            catch (Exception e)
            {
                bool restored = UpdateInstaller.Rollback(staged.Staging.Journal);
                if (restored)
                {
                    UpdateInstaller.DeleteTree(staged.Staging.Root);
                }

                throw new UpdateRefusedException($"the new launcher could not be started: {e.Message} The previous version was {(restored ? "restored" : "not completely restored")}.");
            }

            return;
        }

        string app = staged.AppBundle ?? throw new UpdateRefusedException("the update has no app bundle to replace");
        string stagedApp = Path.Combine(staged.Staging.FilesDirectory, "Tsukihime.app");
        if (!Directory.Exists(app) || !Directory.Exists(stagedApp))
        {
            throw new UpdateRefusedException("the installed or staged app bundle is missing");
        }

        Func<string, string, bool> swap = exchange ?? ((source, target) => ExchangeBundles(source, target));
        string? failed;
        try
        {
            failed = swap(stagedApp, app) ? null : "macOS could not exchange the old and new app bundles";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            failed = e.Message;
        }

        if (failed is not null)
        {
            // renameatx_np swaps both or neither: the installed app is as it was
            DeleteMarked(staged.Staging.AppDirectory);
            throw new UnixUpdateRefusedException($"The update did not work: {failed}. Your installed app was left as it was.", ["exchanging the bundles failed: " + failed]);
        }

        try
        {
            UpdateInstaller.DeleteTree(staged.Staging.DownloadDirectory);
            restartRequested(Path.Combine(app, "Contents", "MacOS", LauncherProgram), staged.Info.Version);
        }
        catch (Exception e)
        {
            string? left = SwapBack(stagedApp, app, swap);
            throw new UpdateRefusedException($"the new launcher could not be started: {e.Message} "
                + (left is null ? "The previous version was restored." : $"The previous version could not be put back ({left}); it is in {stagedApp}."));
        }
    }

    /// <summary>
    /// Puts the old bundle (at <paramref name="stagedApp"/> since the exchange) back at <paramref name="app"/>; null when it is back, else why not. When the second
    /// exchange fails, the new bundle is moved aside before the old one is moved in, and nothing is deleted: the app's path always holds one whole bundle.
    /// </summary>
    private static string? SwapBack(string stagedApp, string app, Func<string, string, bool> swap)
    {
        string why;
        try
        {
            if (swap(stagedApp, app))
            {
                return null;
            }

            why = "the bundles could not be exchanged back";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            why = e.Message;
        }

        string aside = Path.Combine(Path.GetDirectoryName(stagedApp)!, "Tsukihime-failed.app");
        try
        {
            Directory.Move(app, aside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{why}; {e.Message}";
        }

        try
        {
            Directory.Move(stagedApp, app);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                Directory.Move(aside, app);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                return $"{why}; {e.Message}; {again.Message}, so the new version is in {aside}";
            }

            return $"{why}; {e.Message}";
        }
    }

    /// <summary>renameatx_np, the native exchange; tests pass a stand-in (<see cref="ExchangeBundles"/>).</summary>
    internal delegate int RenameFunction(int fromFd, string from, int toFd, string to, uint flags);

    /// <summary>Exchanges two folders in one step (renameatx_np with <see cref="RenameSwap"/>); throws with the errno when macOS refuses.</summary>
    internal static bool ExchangeBundles(string source, string target, RenameFunction? rename = null)
    {
        if (rename is null && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException();
        }

        RenameFunction call = rename ?? RenameExchange;
        if (call(AtFdcwd, source, AtFdcwd, target, RenameSwap) == 0)
        {
            return true;
        }

        int error = Marshal.GetLastPInvokeError();
        throw new IOException($"macOS could not exchange the old and new app bundles: {Marshal.GetPInvokeErrorMessage(error)} (errno {error})");
    }

    [DllImport("libc", EntryPoint = "renameatx_np", SetLastError = true, CharSet = CharSet.Ansi)]
    internal static extern int RenameExchange(int fromFd, string from, int toFd, string to, uint flags);

    /// <summary>What the previous update left. Linux: <see cref="UpdateInstaller.Cleanup"/>. macOS: the staging folder beside the bundle, when it carries the marker.</summary>
    public static void CleanupPrevious(string launcherDirectory, bool? macOs = null)
    {
        if (macOs ?? OperatingSystem.IsMacOS())
        {
            DeleteMarked(MacStaging(MacBundle(launcherDirectory)).AppDirectory);
        }
        else
        {
            UpdateInstaller.Cleanup(launcherDirectory, LauncherProgram);
        }
    }

    /// <summary>
    /// Makes the Mac staging folder fresh, with its marker; refuses when a folder of that name is not the launcher's or cannot be emptied. An empty folder of that
    /// name without the marker is what a launcher stopped between making the folder and writing the marker leaves: it is removed (only an empty folder can be).
    /// </summary>
    private static void OpenMacStaging(string folder)
    {
        string marker = Path.Combine(folder, MacMarker);
        if (Directory.Exists(folder) && !File.Exists(marker) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            TryDeleteEmpty(folder);
        }

        if (File.Exists(folder) || (Directory.Exists(folder) && !File.Exists(marker)))
        {
            throw new UnixUpdateRefusedException(
                $"The update did not work: {folder} is in the way and is not the launcher's. Finder hides it (Cmd+Shift+. shows hidden files); move it elsewhere and try again. Your installed app was left as it was.",
                [$"{folder} is in the way: it has no {MacMarker}"]);
        }

        DeleteMarked(folder);
        if (Directory.Exists(folder))
        {
            throw new UnixUpdateRefusedException(
                $"The update did not work: the previous update's {folder} could not be removed. Finder hides it (Cmd+Shift+. shows hidden files); move it to the Trash and try again. Your installed app was left as it was.",
                [$"the previous update's {folder} could not be removed"]);
        }

        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(marker, "The Tsukihime launcher's update folder. The launcher deletes it at its next start.\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDeleteEmpty(folder);
            throw;
        }
    }

    /// <summary>
    /// Refuses before the download when the drive of the bundle cannot exchange two folders in one step (renameatx_np's RENAME_SWAP: APFS can; HFS+, exFAT, FAT
    /// and network shares cannot): two empty folders inside the marked staging <paramref name="folder"/>, on that drive, are exchanged and removed again.
    /// </summary>
    private static void ProbeMacSwap(string folder, string bundle, string asset, Func<string, string, bool> swap)
    {
        string first = Path.Combine(folder, ".swap-probe-1");
        string second = Path.Combine(folder, ".swap-probe-2");
        string? why;
        try
        {
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            why = swap(first, second) ? null : "the folders were not exchanged";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            why = e.Message;
        }
        finally
        {
            TryDeleteEmpty(first);
            TryDeleteEmpty(second);
        }

        if (why is null)
        {
            return;
        }

        DeleteMarked(folder);
        string name = Path.GetFileName(bundle);
        string? format = DriveFormat(Path.GetDirectoryName(bundle)!);
        throw new UnixUpdateRefusedException(
            $"The update button needs {name} on an APFS drive, such as your Mac's own; macOS cannot swap the app in one step on the drive it is on now{(format is null ? string.Empty : $" ({format})")}. "
            + $"Move {name} to your Mac's own drive with Finder (for example into ~/Applications) and update from there, or download {asset} from the Releases page. Your installed app was left as it was.",
            [$"exchanging two folders in {folder} failed: {why}"]);
    }

    private static string? DriveFormat(string folder)
    {
        try
        {
            return new DriveInfo(folder).DriveFormat;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Deletes <paramref name="folder"/> when it is an empty folder; anything in it keeps it.</summary>
    private static void TryDeleteEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // not empty, or not ours to delete: it stays
        }
    }

    /// <summary>Deletes <paramref name="folder"/> when it carries <see cref="MacMarker"/>; the marker goes last, so a folder that could not be emptied stays the launcher's.</summary>
    private static void DeleteMarked(string folder)
    {
        string marker = Path.Combine(folder, MacMarker);
        if (!File.Exists(marker))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(folder).Where(entry => !string.Equals(entry, marker, StringComparison.Ordinal)).ToList())
        {
            try
            {
                if (Directory.Exists(entry) && new DirectoryInfo(entry).LinkTarget is null)
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // what is left goes at the next start
            }
        }

        if (Directory.EnumerateFileSystemEntries(folder).Any(entry => !string.Equals(entry, marker, StringComparison.Ordinal)))
        {
            return;
        }

        UpdateInstaller.DeleteTree(folder);
    }

    private sealed class Relay(IProgress<UpdateProgress> target) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => target.Report(new UpdateProgress(UpdateStep.Downloading, value));
    }
}

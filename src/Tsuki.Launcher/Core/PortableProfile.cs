using System.Globalization;
using Tsuki.App.Services;

namespace Tsuki.Launcher.Core;

/// <summary>The result of a portable-profile switch.</summary>
public sealed record PortableProfileResult(bool Changed, bool RestartRequired, string Message);

/// <summary>Copy-first switching between the current profile and the portable folder beside the programs.</summary>
public static class PortableProfile
{
    /// <summary>True when the selected profile is the portable folder. An explicit --data folder therefore still wins.</summary>
    public static bool InUse(LauncherPaths paths, string programDirectory)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return SameFolder(paths.DataDirectory, ProfileDirectory.PortableFolder(programDirectory));
    }

    /// <summary>Backs up, copies and verifies the current profile in the requested direction. The launcher must reopen before it starts the game.</summary>
    public static PortableProfileResult Set(
        bool enable,
        LauncherPaths current,
        string programDirectory,
        DateTime? now = null,
        Func<bool>? gameRunning = null,
        Func<string, string?>? writableProbe = null,
        string? perUserDirectory = null,
        Action<string>? beforeCopy = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(programDirectory);
        if (gameRunning?.Invoke() == true)
        {
            return new(false, false, "Close the game before copying the profile: it writes save.dat when it quits.");
        }

        bool active = InUse(current, programDirectory);
        if (enable == active)
        {
            return new(false, false, enable ? "The portable profile is already in use." : "The per-user profile is already in use.");
        }

        if ((writableProbe ?? UpdateInstaller.ProbeWritable)(programDirectory) is { } problem)
        {
            return new(false, false, "The program folder cannot be written by your account. Move the programs to a folder of your own before using portable saves. " + problem);
        }

        string portable = Path.GetFullPath(ProfileDirectory.PortableFolder(programDirectory));
        string source = Path.GetFullPath(current.DataDirectory);
        string target = enable ? portable : Path.GetFullPath(perUserDirectory ?? ProfileDirectory.PerUser());
        if (Overlaps(source, target))
        {
            return new(false, false, "The current --data folder and the portable folder overlap. Choose a separate --data folder first; nothing was copied.");
        }

        DateTime time = now ?? DateTime.Now;
        string stamp = time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        bool targetExisted = Directory.Exists(target);
        string copyTarget = target;
        string? replacedBackup = null;      // where the destination's own save is kept before the copy replaces it
        try
        {
            BackupInfo? sourceBackup = SaveTools.Backup(current, time);
            if (targetExisted)
            {
                BackupInfo? targetBackup = SaveTools.Backup(new LauncherPaths(target), time);
                replacedBackup = targetBackup?.Path;
                if (targetBackup is not null && Directory.Exists(Path.Combine(source, "backups", targetBackup.Name)))
                {
                    string preserved = targetBackup.Path + "-destination";
                    for (int n = 2; Directory.Exists(preserved) || File.Exists(preserved); n++)
                    {
                        preserved = targetBackup.Path + "-destination-" + n.ToString(CultureInfo.InvariantCulture);
                    }

                    Directory.Move(targetBackup.Path, preserved);      // the recursive copy must not replace the old destination's backup with the source's
                    replacedBackup = preserved;
                }
            }
            else
            {
                copyTarget = target + "-incomplete-" + stamp;
                for (int n = 2; Directory.Exists(copyTarget) || File.Exists(copyTarget); n++)
                {
                    copyTarget = target + "-incomplete-" + stamp + "-" + n.ToString(CultureInfo.InvariantCulture);
                }
            }

            CopyVerified(source, copyTarget, beforeCopy);
            if (copyTarget != target)
            {
                Directory.Move(copyTarget, target);                    // save becomes the active marker only after its first copy is complete and verified
            }
            if (enable)
            {
                return new(true, true, "Copied and verified the profile in save. Close and reopen the launcher before PLAY. The previous profile is still where it was" + BackupSuffix(sourceBackup) + ".");
            }

            string parked = portable + "-backup-" + time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            for (int n = 2; Directory.Exists(parked) || File.Exists(parked); n++)
            {
                parked = portable + "-backup-" + time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + n.ToString(CultureInfo.InvariantCulture);
            }

            Directory.Move(portable, parked);
            // the portable folder's own backup moved with it into the parked folder: the message names the backup of the save the copy replaced
            string replaced = replacedBackup is null ? string.Empty : "; the save it replaced is backed up in " + replacedBackup;
            return new(true, true, "Copied and verified the profile back to " + target + ". The portable copy is preserved as " + parked + ". Close and reopen the launcher before PLAY" + replaced + ".");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new(false, false, "The profile was not switched: " + e.Message + " Existing profiles and their backups were not deleted.");
        }
    }

    private static string BackupSuffix(BackupInfo? backup) => backup is null ? string.Empty : "; the previous save is backed up in " + backup.Path;

    // the run locks in the profile folder: the Linux / macOS launcher holds launcher.lock with FileShare.None while its window runs (UnixLauncherInstance),
    // which no other open can share, and the game holds game.lock for its run (CrashLog). They belong to the running programs, not to the profile, and each
    // program makes its own in whichever profile it opens next
    private static readonly string[] RunLocks = ["launcher.lock", "game.lock"];

    private static bool IsRunLock(string source, string file) =>
        Path.GetDirectoryName(Path.GetRelativePath(source, file)) is "" && RunLocks.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);

    private static void CopyVerified(string source, string target, Action<string>? beforeCopy)
    {
        string[] files = Directory.Exists(source)
            ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(f => !IsRunLock(source, f)).Order(StringComparer.Ordinal).ToArray()
            : [];
        Directory.CreateDirectory(target);
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(source, file);
            string copy = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            beforeCopy?.Invoke(file);
            File.Copy(file, copy, overwrite: true);
        }

        foreach (string file in files)
        {
            string copy = Path.Combine(target, Path.GetRelativePath(source, file));
            using var expected = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var actual = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (expected.Length != actual.Length)
            {
                throw new IOException("the copied file has a different length: " + Path.GetRelativePath(source, file));
            }

            var a = new byte[1 << 16];
            var b = new byte[a.Length];
            int read;
            while ((read = expected.Read(a)) > 0)
            {
                actual.ReadExactly(b.AsSpan(0, read));
                if (!a.AsSpan(0, read).SequenceEqual(b.AsSpan(0, read)))
                {
                    throw new IOException("the copied file does not match: " + Path.GetRelativePath(source, file));
                }
            }
        }
    }

    private static bool Overlaps(string a, string b)
    {
        string left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        string right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        return SameFolder(left, right) || left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

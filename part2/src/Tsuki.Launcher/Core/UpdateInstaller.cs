// Installing a downloaded release over the running one (package K40). The zip is unpacked into a staging folder inside the launcher's folder (update-staging\files),
// with every entry checked so that nothing lands outside it (zip-slip: "..", rooted paths, drive letters, alternate data streams, names Windows would alter). The
// staged folder must look like a release of the port: TsukihimeLauncher.exe and Tsukihime.exe at its top, no PERSONAL_BUILD.txt (package K28), and a launcher
// whose own version is the release's (so that a mislabelled zip cannot bring back the version it replaces and offer itself again).
//
// Applying: for every staged file, the file it replaces is renamed to "<name>.old" first (Windows lets a running program and its loaded libraries be renamed, not
// overwritten), then the new file is moved in. Every step is written to update-staging\journal.txt before it is taken; when one step fails, the steps already taken
// are undone in reverse (the new file removed, the .old file renamed back), so the folder is the old version again. The next start of the launcher deletes the .old
// files of the journal and the staging folder (Cleanup). Files of the old version that the release no longer has are left alone, and nothing outside the launcher's
// folder is touched: the saves and settings (%APPDATA%\Tsuki) and the game folder never are.
using System.Globalization;
using System.IO.Compression;
using Tsuki.Data.Icons;

namespace Tsuki.Launcher.Core;

/// <summary>The staging folder of an update inside the launcher's folder.</summary>
/// <param name="AppDirectory">The folder of TsukihimeLauncher.exe.</param>
public sealed record UpdateStaging(string AppDirectory)
{
    /// <summary>The staging folder's name.</summary>
    public const string FolderName = "update-staging";

    /// <summary>The staging folder.</summary>
    public string Root => Path.Combine(AppDirectory, FolderName);

    /// <summary>The downloaded zip and checksum file.</summary>
    public string DownloadDirectory => Path.Combine(Root, "download");

    /// <summary>The unpacked release.</summary>
    public string FilesDirectory => Path.Combine(Root, "files");

    /// <summary>The log of the apply step (what was renamed and moved), read by the rollback and the cleanup.</summary>
    public string Journal => Path.Combine(Root, "journal.txt");
}

/// <summary>An unpacked release, checked.</summary>
/// <param name="Root">The folder that holds TsukihimeLauncher.exe (the staging folder, or its one sub-folder when the zip had one).</param>
/// <param name="Files">Every file under <paramref name="Root"/>, relative, with backslashes.</param>
/// <param name="LauncherVersion">The informational version of the staged TsukihimeLauncher.dll.</param>
public sealed record StagedRelease(string Root, IReadOnlyList<string> Files, string? LauncherVersion);

/// <summary>What happened to the folder when an apply step failed.</summary>
/// <param name="Applied">True when the new version is in place.</param>
/// <param name="Problem">Why it is not, or null.</param>
/// <param name="RolledBack">True when every step taken was undone (the old version is whole again).</param>
/// <param name="Replaced">How many files were replaced (renamed to .old).</param>
/// <param name="Added">How many files were new.</param>
public sealed record ApplyResult(bool Applied, string? Problem, bool RolledBack, int Replaced, int Added);

/// <summary>Unpacks, checks, applies and cleans up an update.</summary>
public static class UpdateInstaller
{
    /// <summary>The suffix of a replaced file until the next start deletes it.</summary>
    public const string OldSuffix = ".old";

    /// <summary>The note a personal build carries beside its programs (src/Tsuki.PersonalIcon.targets, package K28).</summary>
    public const string PersonalNote = "PERSONAL_BUILD.txt";

    /// <summary>At most this many entries in a release zip (the release has about 280).</summary>
    public const int MaxEntries = 20_000;

    /// <summary>At most this many bytes unpacked (the release unpacks to about 250 MB).</summary>
    public const long MaxUnpackedBytes = 4L * 1024 * 1024 * 1024;

    private static readonly string[] DeviceNames = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    // -- writable folder ----------------------------------------------------------------------------------------------

    /// <summary>Null when the launcher's folder can take the update (a file can be created and removed in it), else the reason.</summary>
    public static string? ProbeWritable(string appDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        string probe = Path.Combine(appDirectory, ".update-write-test-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return e.Message;
        }
    }

    /// <summary>The sentence the overlay shows for a folder that cannot be written (Program Files and the like).</summary>
    public static string NotWritableMessage(string appDirectory, ReleaseVersion version) =>
        $"The launcher's folder cannot be changed by your account: {appDirectory}. Windows protects folders such as Program Files. " +
        $"Download {UpdateSource.ZipAssetName(version)} from the Releases page and unzip it into a folder of your own (for example Documents\\Tsukihime), " +
        "or move this folder there and update from the copy. Your saves and settings stay where they are.";

    // -- unpack -------------------------------------------------------------------------------------------------------

    /// <summary>Null when <paramref name="entryName"/> (a zip entry's full name) may be unpacked under a folder, else why not.</summary>
    public static string? EntryProblem(string entryName)
    {
        string name = (entryName ?? string.Empty).Replace('\\', '/');
        if (name.Length == 0)
        {
            return "an entry without a name";
        }

        if (name.StartsWith('/') || (name.Length >= 2 && name[1] == ':'))
        {
            return $"{entryName}: an absolute path";
        }

        string[] parts = name.TrimEnd('/').Split('/');
        foreach (string part in parts)
        {
            if (part.Length == 0 || part is "." or "..")
            {
                return $"{entryName}: the path leaves the folder or has an empty part";
            }

            if (part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 || part.Any(char.IsControl))
            {
                return $"{entryName}: a character Windows does not allow in a file name";
            }

            if (part.EndsWith(' ') || part.EndsWith('.'))
            {
                return $"{entryName}: a name that ends with a space or a dot";
            }

            string stem = part.Split('.')[0];
            if (DeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            {
                return $"{entryName}: a device name";
            }
        }

        return null;
    }

    /// <summary>
    /// Unpacks <paramref name="zipPath"/> into <paramref name="destination"/> (emptied first). Every entry is checked (<see cref="EntryProblem"/>) and must land
    /// inside the folder; two entries that name the same file, more than <see cref="MaxEntries"/> entries or more than <see cref="MaxUnpackedBytes"/> refuse the
    /// whole zip (<see cref="UpdateRefusedException"/>) and leave nothing behind. Returns the files written, relative.
    /// </summary>
    public static IReadOnlyList<string> Extract(string zipPath, string destination, CancellationToken cancel = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string root = Path.GetFullPath(destination);
        DeleteTree(root);
        Directory.CreateDirectory(root);
        string rootWithSlash = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var written = new List<string>();
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count > MaxEntries)
            {
                throw new UpdateRefusedException($"the zip has {zip.Entries.Count.ToString(CultureInfo.InvariantCulture)} entries, more than a release has");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                cancel.ThrowIfCancellationRequested();
                if (EntryProblem(entry.FullName) is { } problem)
                {
                    throw new UpdateRefusedException("the zip was refused: " + problem);
                }

                string relative = entry.FullName.Replace('\\', '/').TrimEnd('/').Replace('/', Path.DirectorySeparatorChar);
                string target = Path.GetFullPath(Path.Combine(root, relative));
                if (!target.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateRefusedException($"the zip was refused: {entry.FullName} would land outside the folder");
                }

                bool isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                if (isDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                if (!seen.Add(relative))
                {
                    throw new UpdateRefusedException($"the zip was refused: {entry.FullName} is in it twice");
                }

                total += entry.Length;
                if (total > MaxUnpackedBytes)
                {
                    throw new UpdateRefusedException("the zip unpacks to more than a release can be");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (Stream input = entry.Open())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    CopyAtMost(input, output, entry.Length, entry.FullName, cancel);
                }

                File.SetLastWriteTime(target, entry.LastWriteTime.LocalDateTime);
                written.Add(relative);
            }
        }
        catch (InvalidDataException e)
        {
            DeleteTree(root);
            throw new UpdateRefusedException("the zip cannot be read: " + e.Message);
        }
        catch
        {
            DeleteTree(root);
            throw;
        }

        return written;
    }

    private static void CopyAtMost(Stream input, Stream output, long length, string name, CancellationToken cancel)
    {
        byte[] buffer = new byte[1 << 16];
        long copied = 0;
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            copied += n;
            if (copied > length)
            {
                throw new UpdateRefusedException($"the zip was refused: {name} is longer than its entry says");
            }

            output.Write(buffer, 0, n);
        }
    }

    // -- check --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Checks an unpacked release: TsukihimeLauncher.exe and Tsukihime.exe at its top (a zip whose files sit in one folder is accepted, that folder is the top), no
    /// PERSONAL_BUILD.txt anywhere, and TsukihimeLauncher.dll of version <paramref name="expected"/> without a personal mark. Throws <see cref="UpdateRefusedException"/>.
    /// </summary>
    public static StagedRelease Check(string staged, ReleaseVersion expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(staged);
        string root = Path.GetFullPath(staged);
        if (!File.Exists(Path.Combine(root, GameLauncher.LauncherFileName))
            && Directory.GetFiles(root).Length == 0 && Directory.GetDirectories(root) is [string only]
            && File.Exists(Path.Combine(only, GameLauncher.LauncherFileName)))
        {
            root = only;
        }

        foreach (string program in new[] { GameLauncher.LauncherFileName, GameLauncher.AppFileName })
        {
            if (!File.Exists(Path.Combine(root, program)))
            {
                throw new UpdateRefusedException($"the release has no {program}: it is not a release of the port");
            }
        }

        foreach (string file in Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), PersonalNote, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateRefusedException($"the release holds {PersonalNote}: it is a personal build, which is never installed");
            }
        }

        string dll = Path.Combine(root, Path.ChangeExtension(GameLauncher.LauncherFileName, ".dll"));
        AssemblyMarks? marks = File.Exists(dll) ? PeIcons.Marks(dll) : null;
        if (marks is null)
        {
            throw new UpdateRefusedException($"the release has no readable {Path.GetFileName(dll)}");
        }

        if (marks.IsPersonalBuild || (marks.InformationalVersion?.Contains("-personal", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            throw new UpdateRefusedException("the release's launcher is a personal build, which is never installed");
        }

        if (!ReleaseVersion.TryParseInformational(marks.InformationalVersion, out ReleaseVersion staged2) || staged2 != expected)
        {
            throw new UpdateRefusedException($"the release is labelled {expected.Tag}, but its launcher says {marks.InformationalVersion ?? "no version"}");
        }

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        return new StagedRelease(root, files, marks.InformationalVersion);
    }

    // -- apply and roll back ------------------------------------------------------------------------------------------

    /// <summary>
    /// Moves the staged files into <paramref name="appDirectory"/>: each file that exists is renamed to <c>.old</c> first, then the new one is moved in. Every step goes
    /// to <paramref name="journal"/> before it is taken. When a step fails, every step taken is undone in reverse and the result says whether that worked.
    /// <paramref name="beforeStep"/> runs before each file (the tests make a step fail with it).
    /// </summary>
    public static ApplyResult Apply(string appDirectory, StagedRelease release, string journal, Action<string>? beforeStep = null, string? launcherFileName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(journal);
        launcherFileName ??= GameLauncher.LauncherFileName;
        string app = Path.GetFullPath(appDirectory);
        string appWithSlash = app.EndsWith(Path.DirectorySeparatorChar) ? app : app + Path.DirectorySeparatorChar;
        var steps = new List<(string Kind, string Target)>();
        int replaced = 0;
        int added = 0;
        using var log = new StreamWriter(new FileStream(journal, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        try
        {
            // the launcher goes last: if anything before it fails, the program the player starts next is still the old one in the old folder
            foreach (string relative in release.Files.OrderBy(f => string.Equals(f, launcherFileName, StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            {
                string source = Path.Combine(release.Root, relative);
                string target = Path.GetFullPath(Path.Combine(app, relative));
                if (!target.StartsWith(appWithSlash, StringComparison.OrdinalIgnoreCase) || target.StartsWith(Path.Combine(app, UpdateStaging.FolderName) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateRefusedException($"{relative} would land outside the launcher's folder");
                }

                beforeStep?.Invoke(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    string old = target + OldSuffix;
                    if (File.Exists(old))
                    {
                        // a left-over of an earlier update that the cleanup could not remove yet
                        File.SetAttributes(old, FileAttributes.Normal);
                        File.Delete(old);
                    }

                    log.WriteLine("old\t" + target);
                    File.Move(target, old);
                    steps.Add(("old", target));
                    replaced++;
                }
                else
                {
                    added++;
                }

                log.WriteLine("new\t" + target);
                File.Move(source, target);
                steps.Add(("new", target));
            }

            log.WriteLine("done");
            return new ApplyResult(true, null, false, replaced, added);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or UpdateRefusedException)
        {
            bool undone = Undo(steps);
            log.WriteLine(undone ? "rolled back" : "rollback incomplete");
            return new ApplyResult(false, e.Message, undone, replaced, added);
        }
    }

    /// <summary>
    /// Restores the files named by a completed apply journal when starting the new program fails, and writes the outcome after its "done" ("rolled back" or
    /// "rollback incomplete"), so that the next start repairs what could not be put back instead of deleting the .old copies (<see cref="Cleanup"/>). The Windows
    /// updater does not need this separate step.
    /// </summary>
    public static bool Rollback(string journal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journal);
        var steps = new List<(string Kind, string Target)>();
        foreach (string line in File.ReadLines(journal))
        {
            string[] parts = line.Split('\t', 2);
            if (parts.Length == 2 && parts[0] is "old" or "new") { steps.Add((parts[0], parts[1])); }
        }

        bool undone = Undo(steps);
        try
        {
            File.AppendAllText(journal, (undone ? "rolled back" : "rollback incomplete") + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the folder cannot be written: the .old copies stay as they are
        }

        return undone;
    }

    /// <summary>Undoes the steps in reverse: a moved-in file is removed, a renamed one is renamed back. False when a step could not be undone.</summary>
    private static bool Undo(List<(string Kind, string Target)> steps)
    {
        bool ok = true;
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            (string kind, string target) = steps[i];
            try
            {
                if (kind == "new")
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
                else
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }

                    File.Move(target + OldSuffix, target);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }

        return ok;
    }

    // -- cleanup at the next start ------------------------------------------------------------------------------------

    /// <summary>
    /// Deletes what an update left: the <c>.old</c> files its journal names (the replaced programs, deletable once the old launcher has exited) and then the staging
    /// folder. A file still in use stays, and so does the journal, for the next start. Returns how many .old files are left (0 when everything is gone).
    /// <paramref name="launcherFileName"/> is the program the apply moved last (the Linux launcher has no .exe).
    /// </summary>
    public static int Cleanup(string appDirectory, string? launcherFileName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        var staging = new UpdateStaging(Path.GetFullPath(appDirectory));
        if (!Directory.Exists(staging.Root))
        {
            return 0;
        }

        int left = 0;
        if (File.Exists(staging.Journal))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(staging.Journal);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return 1;
            }

            string app = staging.AppDirectory.EndsWith(Path.DirectorySeparatorChar) ? staging.AppDirectory : staging.AppDirectory + Path.DirectorySeparatorChar;
            string launcher = Path.Combine(staging.AppDirectory, launcherFileName ?? GameLauncher.LauncherFileName);
            bool launcherMoved = lines.Contains("new\t" + launcher, StringComparer.OrdinalIgnoreCase) && File.Exists(launcher);
            // a restart that failed after "done" was undone by Rollback (macOS/Linux), which writes its outcome after "done"; the Windows updater never does
            bool restartUndoIncomplete = lines.Contains("done") && lines.LastOrDefault(line => line is "done" or "rolled back" or "rollback incomplete") == "rollback incomplete";
            if ((!lines.Contains("done") && !lines.Contains("rolled back") && !launcherMoved) || restartUndoIncomplete)
            {
                // an apply that was cut off (power loss) before its last step, the launcher, or whose rollback could not finish ("rollback incomplete"): put the old
                // version back before anything is deleted (once the new launcher is in place every other file is too, so that journal counts as done). When a file
                // cannot be put back yet (in use), its .old copy is the only old version left: nothing is deleted and the journal stays for the next start (review K41).
                int unrepaired = RepairInterrupted(lines, app);
                if (unrepaired > 0)
                {
                    return unrepaired;
                }
            }

            foreach (string line in lines)
            {
                if (!line.StartsWith("old\t", StringComparison.Ordinal) || !IsInside(line[4..], app))
                {
                    continue;
                }

                string old = line[4..] + OldSuffix;
                if (!File.Exists(old))
                {
                    continue;
                }

                try
                {
                    File.SetAttributes(old, FileAttributes.Normal);
                    File.Delete(old);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    left++;
                }
            }
        }

        if (left == 0)
        {
            DeleteTree(staging.Root);
            if (Directory.Exists(staging.Root))
            {
                left++;
            }
        }

        return left;
    }

    /// <summary>
    /// A journal without its end: every replaced file whose .old copy is still there gets it back, and a file that was new to the folder is removed. A file the
    /// journal names as replaced ("old") is never removed as new: when its .old copy is gone, the rollback already put it back (or the rename never happened), so
    /// it IS the old version (review K41: a second repair, after a "rollback incomplete", deleted those). Only files inside <paramref name="appWithSlash"/> are
    /// touched. Returns the number of files that could not be repaired (0 or 1).
    /// </summary>
    private static int RepairInterrupted(string[] lines, string appWithSlash)
    {
        var replaced = new HashSet<string>(lines.Where(l => l.StartsWith("old\t", StringComparison.Ordinal)).Select(l => l[4..]), StringComparer.OrdinalIgnoreCase);
        var steps = new List<(string Kind, string Target)>();
        foreach (string line in lines)
        {
            bool old = line.StartsWith("old\t", StringComparison.Ordinal);
            if ((!old && !line.StartsWith("new\t", StringComparison.Ordinal)) || !IsInside(line[4..], appWithSlash))
            {
                continue;
            }

            string target = line[4..];
            if (old)
            {
                if (File.Exists(target + OldSuffix))
                {
                    steps.Add(("old", target));
                }
            }
            else if (!replaced.Contains(target))
            {
                steps.Add(("new", target));
            }
        }

        return Undo(steps) ? 0 : 1;
    }

    /// <summary>True when <paramref name="path"/> (a journal's line) lies inside the launcher's folder, outside its staging folder.</summary>
    private static bool IsInside(string path, string appWithSlash)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return full.StartsWith(appWithSlash, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(appWithSlash + UpdateStaging.FolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>Deletes a folder tree; quiet when it is not there or partly in use.</summary>
    public static void DeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // what is left goes at the next start
        }
    }
}

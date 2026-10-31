// The saves page (package K17): what is in %APPDATA%\Tsuki\save.dat, backups of save.dat and settings.ini, restoring one, and importing a PS4 / shadPS4 save image.
// The launcher only touches %APPDATA%\Tsuki; a PS4 image is read (never written) from wherever it lies.
using System.Globalization;
using Tsuki.App.Services;
using Tsuki.Engine.Save;

namespace Tsuki.Launcher.Core;

/// <summary>What the save file holds.</summary>
/// <param name="Exists">save.dat is there.</param>
/// <param name="Slots">Stored slots (manual, auto and quick).</param>
/// <param name="ManualSlots">Stored manual slots.</param>
/// <param name="LastWrite">When the file was last written.</param>
/// <param name="Error">Why it could not be read; null when it could.</param>
/// <param name="ConfigLanguage">The language of the config block (0 ja, 1 en, 2 zc, 3 zt), or null.</param>
/// <param name="WindowMode">The window mode byte of the config block (1 = fullscreen), or null.</param>
/// <param name="WriteError">Why the save folder cannot be written (the game would lose every save of a session there, package R1); null when a probe file could be made.</param>
public sealed record SaveSummary(bool Exists, int Slots, int ManualSlots, DateTime? LastWrite, string? Error, int? ConfigLanguage = null, int? WindowMode = null, string? WriteError = null)
{
    /// <summary>"No saves yet", "12 save slots", "save.dat is damaged", "The save folder cannot be written".</summary>
    public string Describe() => WriteError is not null ? "The save folder cannot be written: " + WriteError
        : Error is not null ? "save.dat cannot be read: " + Error
        : !Exists ? "No saves yet; they are created on the first play"
        : Slots == 1 ? "1 save slot"
        : string.Create(CultureInfo.InvariantCulture, $"{Slots} save slots");
}

/// <summary>One backup folder.</summary>
/// <param name="Name">The folder name, a timestamp (<c>20261027-153000</c>).</param>
/// <param name="Path">The folder.</param>
/// <param name="Created">When it was made.</param>
/// <param name="Files">The files in it.</param>
/// <param name="Bytes">Their size.</param>
/// <param name="Save">What the saved save.dat holds.</param>
public sealed record BackupInfo(string Name, string Path, DateTime Created, IReadOnlyList<string> Files, long Bytes, SaveSummary Save);

/// <summary>Save summaries, backups and imports.</summary>
public static class SaveTools
{
    private static readonly string[] BackedUp = ["save.dat", "settings.ini"];

    /// <summary>Reads a save file's slot count and config.</summary>
    public static SaveSummary Summarize(string savePath)
    {
        if (Directory.Exists(savePath))
        {
            // the game cannot write its file over a folder of that name (it logs "Cannot create a file when that file already exists" and keeps every save in memory only)
            return new SaveSummary(true, 0, 0, null, "save.dat is a folder, not a file; move or rename it");
        }

        if (!File.Exists(savePath))
        {
            return new SaveSummary(false, 0, 0, null, null);
        }

        DateTime written = File.GetLastWriteTime(savePath);
        try
        {
            SaveStore? store = SaveFile.ReadWithFallback(savePath, out _);
            if (store is null)
            {
                return new SaveSummary(false, 0, 0, null, null);
            }

            int[] slots = store.StoredSlots().ToArray();
            short language = store.SystemData.Config.Language;
            return new SaveSummary(
                true,
                slots.Length,
                slots.Count(s => s < SaveStore.ManualSlotCount),
                written,
                null,
                language is >= 0 and <= 3 ? language : null,
                store.SystemData.Config.Bytes[AppSettings.WindowModeOffset]);
        }
        catch (Exception e) when (e is SaveFormatException or IOException or UnauthorizedAccessException)
        {
            return new SaveSummary(true, 0, 0, written, e.Message);
        }
    }

    /// <summary>
    /// Tries to create (and remove) a file in the profile folder, as the game's save commit will: null when that works, else the operating system's reason. The game
    /// reports a failed commit as a failed save since package R1, and the launcher's ready check warns before the first play.
    /// </summary>
    public static string? ProbeWritable(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try
        {
            Directory.CreateDirectory(directory);
            string probe = System.IO.Path.Combine(directory, ".write_test");
            using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return e.Message;
        }
    }

    /// <summary>Copies save.dat and settings.ini into <c>backups\(timestamp)\</c>; null when there is nothing to back up.</summary>
    /// <param name="paths">The profile.</param>
    /// <param name="now">The time for the folder name.</param>
    public static BackupInfo? Backup(LauncherPaths paths, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] sources = BackedUp.Select(f => System.IO.Path.Combine(paths.DataDirectory, f)).Where(File.Exists).ToArray();
        if (sources.Length == 0)
        {
            return null;
        }

        string name = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string folder = System.IO.Path.Combine(paths.BackupsDirectory, name);
        for (int n = 2; Directory.Exists(folder); n++)
        {
            folder = System.IO.Path.Combine(paths.BackupsDirectory, name + "-" + n.ToString(CultureInfo.InvariantCulture));
        }

        Directory.CreateDirectory(folder);
        foreach (string source in sources)
        {
            File.Copy(source, System.IO.Path.Combine(folder, System.IO.Path.GetFileName(source)), overwrite: false);
        }

        return Describe(folder);
    }

    /// <summary>The backups, newest first.</summary>
    public static IReadOnlyList<BackupInfo> List(LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Directory.Exists(paths.BackupsDirectory))
        {
            return [];
        }

        return Directory.EnumerateDirectories(paths.BackupsDirectory)
            .Select(Describe)
            .Where(b => b.Files.Count > 0)
            .OrderByDescending(b => b.Created)
            .ThenByDescending(b => b.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Puts a backup's save.dat back. The current save.dat is backed up first (so a restore can be undone); settings.ini is left as it is.
    /// Returns the backup of the save that was replaced, or null when there was none.
    /// </summary>
    /// <exception cref="FileNotFoundException">The backup holds no save.dat.</exception>
    public static BackupInfo? Restore(LauncherPaths paths, BackupInfo backup, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(backup);
        string source = System.IO.Path.Combine(backup.Path, "save.dat");
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("this backup holds no save.dat", source);
        }

        BackupInfo? previous = Backup(paths, now);
        Directory.CreateDirectory(paths.DataDirectory);
        string temp = paths.SaveFile + ".restore.tmp";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, paths.SaveFile, overwrite: true);
        return previous;
    }

    private static BackupInfo Describe(string folder)
    {
        string[] files = Directory.EnumerateFiles(folder)
            .Select(System.IO.Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        long bytes = files.Sum(f => new FileInfo(System.IO.Path.Combine(folder, f)).Length);
        string name = System.IO.Path.GetFileName(folder);
        DateTime created = DateTime.TryParseExact(name.Length >= 15 ? name[..15] : name, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed
            : Directory.GetCreationTime(folder);
        return new BackupInfo(name, folder, created, files, bytes, Summarize(System.IO.Path.Combine(folder, "save.dat")));
    }

    // -- PS4 / shadPS4 images ------------------------------------------------------------------------------------------

    /// <summary>
    /// Looks for the 4 MiB console save image of the game under shadPS4's user data (<c>%APPDATA%\shadPS4\home\(user)\savedata\CUSA47999</c>): the first file of exactly that size
    /// (normally <c>savedata000</c>). It only enumerates; nothing is opened for writing.
    /// </summary>
    /// <param name="appData">The roaming application data folder.</param>
    /// <param name="titleId">The title id.</param>
    public static string? FindShadPs4Image(string appData, string titleId = "CUSA47999")
    {
        string home = System.IO.Path.Combine(appData, "shadPS4", "home");
        if (!Directory.Exists(home))
        {
            return null;
        }

        try
        {
            foreach (string user in Directory.EnumerateDirectories(home))
            {
                string dir = System.IO.Path.Combine(user, "savedata", titleId);
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (new FileInfo(file).Length == SaveFile.Ps4ImageSize)
                    {
                        return file;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>Reads a PS4 image (read-only, shared with a running emulator) into memory and turns the copy into a store.</summary>
    /// <exception cref="SaveFormatException">The file is not a save image of this game.</exception>
    public static SaveStore ReadPs4Image(string imagePath)
    {
        byte[] image;
        using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (stream.Length != SaveFile.Ps4ImageSize)
            {
                throw new SaveFormatException($"a PS4 save image is {SaveFile.Ps4ImageSize:N0} bytes, this file has {stream.Length:N0}");
            }

            image = new byte[SaveFile.Ps4ImageSize];
            stream.ReadExactly(image);
        }

        return SaveFile.ImportPs4Image(image);
    }

    /// <summary>
    /// Replaces save.dat by the PS4 image's slots and system data. The current save is backed up first.
    /// Returns the backup (null when there was no save) and the number of slots imported.
    /// </summary>
    public static (BackupInfo? Backup, int Slots) ImportPs4(LauncherPaths paths, string imagePath, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(paths);
        SaveStore imported = ReadPs4Image(imagePath);
        BackupInfo? backup = Backup(paths, now);
        Directory.CreateDirectory(paths.DataDirectory);
        SaveFile.WriteAtomic(paths.SaveFile, imported);
        return (backup, imported.StoredSlots().Count());
    }
}

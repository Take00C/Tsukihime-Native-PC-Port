// Package K43: Arcueid's face on the two programs themselves (Tsukihime.exe and TsukihimeLauncher.exe, as Explorer, the taskbar and Alt+Tab show them). A release
// ships them with the port's own crescent (her art is TYPE-MOON's and must never be inside a public download; Tsuki.Cli release-check refuses a publish folder whose
// programs carry anything else), so the launcher writes her face into the installed programs at run time, from the icon it already cuts from the player's own game
// files (FaceIconCache, package K23). The switch on Help & about (settings.ini arcueid_icon, on by default) puts the crescent back the same way.
//
// How one program is stamped (the Windows resource API, as the .NET SDK's own ResourceUpdater sets the apphost's icon): the program is copied to "<name>.icon-new" in
// the same folder, the RT_ICON images its icon group 32512 names are deleted from the copy in the language they really have, the new images and a new group are
// written (BeginUpdateResourceW / UpdateResourceW / EndUpdateResourceW), and the copy is VERIFIED before anything is replaced: its group reads back as exactly the
// wanted images, no RT_ICON is left without a group, every other resource (the version block, the manifest) is byte-identical, every section other than the
// resources is byte-identical at the same address, the headers and data directories are the same, it is still the apphost it was (its bundle marker, no signature,
// nothing after the last section) and its size is sane. Only then is the copy moved in. The game (never running when it is stamped) is replaced in one step
// (MoveFileEx with REPLACE_EXISTING), so its name always holds the old or the new file. The launcher is the program that runs, and a running program can only be
// renamed: it becomes "<name>.icon-old" and the copy is moved in; when that fails, the original is moved back, or else the checked copy moved in after all (a few
// tries each), so its name is never left empty while either file can be moved. The .icon-old is deleted (the running launcher's cannot be: the next start deletes
// it). Any failure deletes the copy and leaves the program as it was; the one exception is a launcher whose name could not be given back either file: then both
// stay, the log says MISSING, and the next stamp, or the launcher's exit, moves one back (review of K43). Every stamp first puts back a program an earlier one left
// missing (CleanLeftovers). A program that already carries the wanted images is never written, so a normal start changes nothing.
//
// Never touched: a single-file bundle, a signed file, a file with data after its last section, a program without its .dll beside it, a read-only file, the game
// while the game of this folder runs, a folder that cannot be written (Program Files), a folder with an unfinished update (update-staging). The suffixes differ from
// the updater's ".old" (UpdateInstaller), whose journal and cleanup never see these files. Nothing here decides whether a build is personal (LauncherBuild reads the
// assembly's attributes and PERSONAL_BUILD.txt, never an icon), and the updater's SHA-256 checks are of the downloaded zip, not of the installed files. The window
// stamps only a self-contained publish (IsSelfContained: every release zip is one), never a build output of the source.
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Tsuki.Data.Icons;

namespace Tsuki.Launcher.Core;

/// <summary>What happened to one program.</summary>
public enum StampOutcome
{
    /// <summary>The program carries the wanted icon now: it was written, checked and moved in.</summary>
    Stamped,

    /// <summary>The program already carried the wanted icon: nothing was written.</summary>
    Unchanged,

    /// <summary>Not touched, on purpose (not there, read-only, the game running, not a plain apphost, the folder cannot be written).</summary>
    Skipped,

    /// <summary>The stamp did not work; the program is as it was.</summary>
    Failed,
}

/// <summary>The points of one stamp a test can make fail (<see cref="ExeIconStamp.StampExe"/>'s hook).</summary>
public enum StampStep
{
    /// <summary>The copy <c>&lt;name&gt;.icon-new</c> was made.</summary>
    Copied,

    /// <summary>The copy carries the new icon (the resource update ended).</summary>
    Updated,

    /// <summary>The copy passed every check.</summary>
    Verified,

    /// <summary>The launcher only (the game is replaced in one step): the program was renamed to <c>.icon-old</c>; the copy is about to be moved in.</summary>
    Renamed,
}

/// <summary>Why a program was left as it is or could not be changed (the Help page says it in plain words: <see cref="ExeIconStamp.PlayerNote"/>).</summary>
public enum StampReason
{
    /// <summary>Nothing to say: stamped or unchanged.</summary>
    None,

    /// <summary>The program is not in the folder.</summary>
    NotThere,

    /// <summary>The game of this folder is running (it is stamped at a later refresh, after the game ends).</summary>
    GameRunning,

    /// <summary>The account cannot write the folder (Program Files) or may not replace the program.</summary>
    NotWritable,

    /// <summary>An update of the folder is not finished (update-staging).</summary>
    UpdateUnfinished,

    /// <summary>The file is not one the stamp may change: read-only, signed, a single-file build, no single icon group.</summary>
    NotStampable,

    /// <summary>The stamp was tried and did not work (a file held by another program, a failed check).</summary>
    Error,
}

/// <summary>An icon the stamp knows by name.</summary>
/// <param name="Name">"Arcueid", "the port's crescent".</param>
/// <param name="Images">Its images, as in its .ico file.</param>
public sealed record IconChoice(string Name, IReadOnlyList<IcoImage> Images);

/// <summary>One program's result.</summary>
/// <param name="Program">The file name (Tsukihime.exe, TsukihimeLauncher.exe).</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Detail">Why, in words (empty when stamped or unchanged).</param>
/// <param name="Before">The icon it carried before.</param>
/// <param name="After">The icon it carries now (<see cref="NoFile"/> when the program is not there).</param>
/// <param name="Reason">Why it was skipped or failed (<see cref="StampReason.None"/> when stamped or unchanged).</param>
public sealed record StampResult(string Program, StampOutcome Outcome, string Detail, string Before, string After, StampReason Reason = StampReason.None)
{
    /// <summary>What <see cref="Before"/> and <see cref="After"/> say for a program that is not in the folder.</summary>
    public const string NoFile = "no file";

    /// <summary>"Tsukihime.exe: Arcueid written (it was the port's crescent, 10 images)", for launcher.log and the self-test.</summary>
    public string Describe() => Outcome switch
    {
        StampOutcome.Stamped => $"{Program}: {After} written (it was {Before})",
        StampOutcome.Unchanged => $"{Program}: already {After}, nothing written",
        StampOutcome.Skipped => $"{Program}: left as it is ({Before}): {Detail}",
        StampOutcome.Failed when After == NoFile && Before != NoFile => $"{Program}: MISSING, the failed stamp could not give its name back ({Detail})",
        _ => $"{Program}: NOT changed, it is as it was ({Before}): {Detail}",
    };
}

/// <summary>What the clean-up of an earlier stamp found (<see cref="ExeIconStamp.CleanLeftovers"/>).</summary>
/// <param name="Repaired">Programs that were missing and were put back from their <c>.icon-old</c> copy (a stamp cut off between its two renames).</param>
/// <param name="Deleted">Left-over copies deleted.</param>
/// <param name="Left">Left-over copies still in use (the running launcher's old image when this runs right after a stamp).</param>
public sealed record StampCleanup(IReadOnlyList<string> Repaired, int Deleted, int Left);

/// <summary>Writes an icon into the installed programs (package K43).</summary>
public static class ExeIconStamp
{
    /// <summary>The suffix of the stamped copy until it is moved in.</summary>
    public const string NewSuffix = ".icon-new";

    /// <summary>The suffix of the program it replaced until that is deleted (differs from the updater's <see cref="UpdateInstaller.OldSuffix"/>).</summary>
    public const string OldSuffix = ".icon-old";

    /// <summary>A program larger than this is not an apphost (a single-file bundle of the port is over 100 MB; the apphost is about 340 KB).</summary>
    public const long MaxProgramBytes = 16L * 1024 * 1024;

    /// <summary>The marker every .NET apphost carries (the SHA-256 of ".net core bundle"); the 8 bytes before it are the bundle's header offset, 0 in an apphost that is no bundle (Microsoft.NET.HostModel HostWriter).</summary>
    private static readonly byte[] BundleSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    ];

    /// <summary>The Windows errors of a file held for a moment by someone else (a virus scanner reading the new copy): the resource update is tried again.</summary>
    private static readonly int[] Transient = [5, 32, 33, 1224];

    /// <summary>The two programs, in the order they are stamped (the game first: the launcher is the one that runs).</summary>
    public static IReadOnlyList<string> Programs { get; } = [GameLauncher.AppFileName, GameLauncher.LauncherFileName];

    /// <summary>
    /// Held for the two renames of a swap and by PLAY while it starts the game, so that the game is never started in the moment Tsukihime.exe is between its old
    /// and its new file (microseconds; PLAY never waits for more than that).
    /// </summary>
    public static Lock SwapGate { get; } = new();

    /// <summary>
    /// Programs whose replace Windows refused with "access denied" in this session (the file's path, size and time; why): an account that may create files in the
    /// folder but not rename the ones another account unzipped. They are not copied and rewritten again at every refresh; a changed file (another size or time)
    /// or the next start tries again.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> NotReplaceable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="appDirectory"/> is a self-contained publish (hostfxr.dll beside the programs), as every release zip is. The window stamps only
    /// such a folder: a build output of the source keeps a stamped program over later builds (the SDK copies the apphost only when it is newer), and its tests
    /// start from the crescent.
    /// </summary>
    public static bool IsSelfContained(string appDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        return File.Exists(Path.Combine(appDirectory, "hostfxr.dll"));
    }

    /// <summary>
    /// What the line under Help &amp; about's switch adds after a stamp, in plain words for players: why the programs do not show the wanted icon, or empty when
    /// they do (review of K43: a switch that changes nothing must say why).
    /// </summary>
    public static string PlayerNote(IReadOnlyList<StampResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        StampResult[] open = [.. results.Where(r => r.Outcome is StampOutcome.Skipped or StampOutcome.Failed)];
        StampResult? first = open.FirstOrDefault(r => r.Reason == StampReason.NotWritable)
            ?? open.FirstOrDefault(r => r.Reason == StampReason.UpdateUnfinished)
            ?? open.FirstOrDefault(r => r.Reason != StampReason.GameRunning)
            ?? open.FirstOrDefault();
        return first?.Reason switch
        {
            null => string.Empty,
            StampReason.NotWritable => "Not applied: your account can't change the files in the port's folder (under Program Files, for example). Move the folder somewhere of your own, such as Documents, and start the launcher from there.",
            StampReason.UpdateUnfinished => "Not applied yet: the last update isn't finished. The launcher tries again at its next start.",
            StampReason.GameRunning => $"{first.Program} changes once the game is closed.",
            StampReason.NotThere => $"Not applied: {first.Program} isn't in the launcher's folder.",
            _ => $"Not applied: {first.Program} couldn't be changed. launcher.log, next to your saves, says why.",
        };
    }

    // -- one folder ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Puts <paramref name="wanted"/> on both programs of <paramref name="appDirectory"/>: each one that does not carry it already is stamped (<see cref="StampExe"/>);
    /// the game is skipped while the game of this folder runs. Nothing is written when the folder cannot be written or an update of it is not finished.
    /// <paramref name="known"/> names the icons for the results ("Arcueid", "the port's crescent").
    /// </summary>
    public static IReadOnlyList<StampResult> StampFolder(string appDirectory, IconChoice wanted, IReadOnlyList<IconChoice> known, Action<string, StampStep>? onStep = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(known);
        string app = Path.GetFullPath(appDirectory);
        (string Why, StampReason Reason)? folderProblem = !Directory.Exists(app) ? ("the folder is not there", StampReason.NotThere)
            : Directory.Exists(new UpdateStaging(app).Root) ? ("an update of this folder is not finished (update-staging), so nothing is changed until it is", StampReason.UpdateUnfinished)
            : null;
        if (folderProblem is null && Programs.Any(p => NeedsStamp(Path.Combine(app, p), wanted)) && UpdateInstaller.ProbeWritable(app) is { } notWritable)
        {
            folderProblem = ($"the folder cannot be written by this account ({notWritable})", StampReason.NotWritable);
        }

        var results = new List<StampResult>(Programs.Count);
        foreach (string program in Programs)
        {
            string exe = Path.Combine(app, program);
            if (folderProblem is { } folder)
            {
                string now = File.Exists(exe) ? Describe(ReadIcon(exe), known) : StampResult.NoFile;
                results.Add(new StampResult(program, StampOutcome.Skipped, folder.Why, now, now, folder.Reason));
                continue;
            }

            Func<bool>? running = program == GameLauncher.AppFileName ? () => GameLauncher.IsGameRunningFrom(app) : null;
            results.Add(StampExe(exe, wanted, known, running, onStep));
        }

        return results;
    }

    /// <summary>
    /// Writes <paramref name="wanted"/> into the icon group 32512 of <paramref name="exe"/> through a checked copy (see the file's header). <paramref name="running"/>
    /// says whether the program runs now (the game of the folder: it is skipped then). <paramref name="onStep"/> runs after each step (the tests make one fail).
    /// Never throws for a file problem: the result says what happened, and on any failure the program is the file it was.
    /// </summary>
    public static StampResult StampExe(string exe, IconChoice wanted, IReadOnlyList<IconChoice> known, Func<bool>? running = null, Action<string, StampStep>? onStep = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exe);
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(known);
        string program = Path.GetFileName(exe);
        if (!File.Exists(exe))
        {
            return new StampResult(program, StampOutcome.Skipped, "it is not in the folder", StampResult.NoFile, StampResult.NoFile, StampReason.NotThere);
        }

        IReadOnlyList<IcoImage>? current = ReadIcon(exe);
        string before = Describe(current, known);
        if (IcoFile.SameImages(current, wanted.Images))
        {
            return new StampResult(program, StampOutcome.Unchanged, string.Empty, before, before);
        }

        StampResult Skip(string why, StampReason reason) => new(program, StampOutcome.Skipped, why, before, before, reason);
        StampResult Fail(string why, StampReason reason) => new(program, StampOutcome.Failed, why, before, File.Exists(exe) ? Describe(ReadIcon(exe), known) : StampResult.NoFile, reason);
        try
        {
            if ((File.GetAttributes(exe) & FileAttributes.ReadOnly) != 0)
            {
                return Skip("the file is read-only", StampReason.NotStampable);
            }

            if (ApphostProblem(exe) is { } notApphost)
            {
                return Skip(notApphost, StampReason.NotStampable);
            }

            if (running?.Invoke() == true)
            {
                return Skip("the game of this folder is running; it is stamped at a later start", StampReason.GameRunning);
            }

            if (NotReplaceable.TryGetValue(KeyOf(exe), out string? refused))
            {
                // review of K43: not copied and rewritten again at every refresh only to be refused once more
                return Skip($"this account may not replace it ({refused}); found by an earlier try of this session, tried again at the next start", StampReason.NotWritable);
            }

            if (IconLayout.Of(exe) is not { } layout)
            {
                return Skip($"it has no single application icon group (id {PeIcons.ApplicationIconGroup}) to replace", StampReason.NotStampable);
            }

            string copy = exe + NewSuffix;
            try
            {
                WriteCopy(exe, copy, layout, wanted.Images, program, onStep);
                if (Verify(exe, copy, layout, wanted.Images) is { } problem)
                {
                    throw new StampRefusedException("the stamped copy failed its check: " + problem);
                }

                onStep?.Invoke(program, StampStep.Verified);
                Swap(exe, copy, program, running, onStep);
            }
            catch
            {
                // the checked copy stays while the program's name is empty (a swap that could give it back neither file): the next stamp moves one of them back
                if (File.Exists(exe))
                {
                    DeleteQuietly(copy);
                }

                throw;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or StampRefusedException or BadImageFormatException or ArgumentException or NotSupportedException)
        {
            return Fail(e.Message, e is UnauthorizedAccessException ? StampReason.NotWritable : StampReason.Error);
        }

        NotifyShell(exe);
        IReadOnlyList<IcoImage>? now = ReadIcon(exe);
        return IcoFile.SameImages(now, wanted.Images)
            ? new StampResult(program, StampOutcome.Stamped, string.Empty, before, Describe(now, known))
            : new StampResult(program, StampOutcome.Failed, "the program moved in does not read back as the wanted icon", before, Describe(now, known), StampReason.Error);
    }

    /// <summary>True when <paramref name="exe"/> exists and does not carry <paramref name="wanted"/> yet.</summary>
    private static bool NeedsStamp(string exe, IconChoice wanted) => File.Exists(exe) && !IcoFile.SameImages(ReadIcon(exe), wanted.Images);

    // -- reading ------------------------------------------------------------------------------------------------------

    /// <summary>The images of the icon group 32512 of <paramref name="exe"/>; null when it has none or cannot be read.</summary>
    public static IReadOnlyList<IcoImage>? ReadIcon(string exe)
    {
        try
        {
            return File.Exists(exe) && PeIcons.IconGroup(exe) is { } group ? IcoFile.Read(group) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"Arcueid (7 images)", "the port's crescent (10 images)", "another icon (3 images, 16, 32, 48 px)", "no icon".</summary>
    public static string Describe(IReadOnlyList<IcoImage>? images, IReadOnlyList<IconChoice> known)
    {
        ArgumentNullException.ThrowIfNull(known);
        if (images is null)
        {
            return "no icon";
        }

        string count = images.Count.ToString(CultureInfo.InvariantCulture);
        return known.FirstOrDefault(k => IcoFile.SameImages(k.Images, images)) is { } k
            ? $"{k.Name} ({count} images)"
            : $"another icon ({count} images, {IcoFile.Sizes(images)})";
    }

    /// <summary>
    /// Null when <paramref name="exe"/> is a plain .NET apphost the stamp may rewrite, else why not: not a PE program, a managed assembly, too large, signed, data after
    /// its last section, no bundle marker or a single-file bundle (the marker's offset is set), its .dll not beside it, or a resource section that is not its last one.
    /// <paramref name="program"/> is the program <paramref name="exe"/> stands for when it is the stamped copy (its .dll is the program's).
    /// </summary>
    public static string? ApphostProblem(string exe, string? program = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exe);
        var info = new FileInfo(exe);
        if (!info.Exists)
        {
            return "it is not there";
        }

        if (info.Length > MaxProgramBytes)
        {
            return $"it is {GameFolderCheck.FormatSize(info.Length)}, too large for the port's small program (a single-file build?)";
        }

        byte[] bytes;
        using (FileStream stream = new(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        string? layoutProblem = PeLayout.Read(bytes) switch
        {
            null => "it is not a Windows program",
            { IsManaged: true } => "it is a .NET assembly, not the program that starts one",
            { IsDll: true } => "it is a library, not a program",
            { Signed: true } => "it is signed, and a stamp would break the signature",
            { Overlay: > 0 } l => $"it carries {l.Overlay.ToString(CultureInfo.InvariantCulture)} bytes after its last section (a single-file bundle or an installer)",
            { ResourcesLast: false } => "its resource section is not its last section",
            _ => null,
        };
        if (layoutProblem is not null)
        {
            return layoutProblem;
        }

        int marker = bytes.AsSpan().IndexOf(BundleSignature);
        if (marker < 8)
        {
            return "it is not a .NET apphost (no bundle marker)";
        }

        if (BitConverter.ToInt64(bytes, marker - 8) != 0)
        {
            return "it is a single-file bundle";
        }

        string dll = Path.ChangeExtension(program ?? exe, ".dll");
        return File.Exists(dll) ? null : $"{Path.GetFileName(dll)} is not beside it";
    }

    // -- writing ------------------------------------------------------------------------------------------------------

    /// <summary>Copies <paramref name="exe"/> to <paramref name="copy"/> and writes the images and the group into the copy; a scanner holding the new file is waited for (three tries).</summary>
    private static void WriteCopy(string exe, string copy, IconLayout layout, IReadOnlyList<IcoImage> images, string program, Action<string, StampStep>? onStep)
    {
        for (int attempt = 1; ; attempt++)
        {
            DeleteQuietly(copy);
            if (File.Exists(copy))
            {
                throw new IOException($"{Path.GetFileName(copy)} is left from an earlier stamp and in use");
            }

            File.Copy(exe, copy);
            File.SetAttributes(copy, File.GetAttributes(copy) & ~FileAttributes.ReadOnly);
            onStep?.Invoke(program, StampStep.Copied);
            try
            {
                UpdateIcon(copy, layout, images);
                onStep?.Invoke(program, StampStep.Updated);
                return;
            }
            catch (Win32Exception e) when (attempt < 3 && Transient.Contains(e.NativeErrorCode))
            {
                Thread.Sleep(250 * attempt);
            }
        }
    }

    /// <summary>
    /// The resource update of the copy: every RT_ICON the old group names is deleted in each language it has, the new images become RT_ICON ids that no other group
    /// uses (the smallest free ones from 1), and the group 32512 is replaced in its own language. Throws <see cref="Win32Exception"/> when Windows refuses a call.
    /// </summary>
    private static void UpdateIcon(string path, IconLayout layout, IReadOnlyList<IcoImage> images)
    {
        var ids = new List<int>(images.Count);
        for (int id = 1; ids.Count < images.Count && id <= ushort.MaxValue; id++)
        {
            if (!layout.KeptIconIds.Contains(id))
            {
                ids.Add(id);
            }
        }

        byte[] group = IcoFile.GroupDirectory(images, ids);
        nint update = Native.BeginUpdateResource(path, deleteExistingResources: false);
        if (update == 0)
        {
            throw LastError($"Windows did not open {Path.GetFileName(path)} for the resource update");
        }

        bool written = false;
        try
        {
            foreach ((int id, int language) in layout.OldIcons)
            {
                Call(Native.UpdateResource(update, PeIcons.IconType, id, (ushort)language, null, 0), $"delete RT_ICON {id}");
            }

            for (int i = 0; i < images.Count; i++)
            {
                Call(Native.UpdateResource(update, PeIcons.IconType, ids[i], (ushort)layout.Language, images[i].Data, (uint)images[i].Data.Length), $"write RT_ICON {ids[i]}");
            }

            Call(Native.UpdateResource(update, PeIcons.GroupIconType, PeIcons.ApplicationIconGroup, (ushort)layout.Language, group, (uint)group.Length), "write the icon group");
            written = true;
        }
        finally
        {
            bool ended = Native.EndUpdateResource(update, discard: !written);
            if (written && !ended)
            {
                throw LastError($"Windows did not finish the resource update of {Path.GetFileName(path)}");
            }
        }
    }

    private static void Call(bool ok, string what)
    {
        if (!ok)
        {
            throw LastError($"the resource update could not {what}");
        }
    }

    /// <summary>The error of the Windows call that just failed, with Windows' own words for it.</summary>
    private static Win32Exception LastError(string what)
    {
        int code = Marshal.GetLastPInvokeError();
        return new Win32Exception(code, $"{what} ({new Win32Exception(code).Message})");
    }

    // -- the check of the copy ----------------------------------------------------------------------------------------

    /// <summary>Null when the stamped <paramref name="copy"/> of <paramref name="original"/> may replace it, else what is wrong (see the file's header).</summary>
    internal static string? Verify(string original, string copy, IconLayout layout, IReadOnlyList<IcoImage> wanted)
    {
        long before = new FileInfo(original).Length;
        long after = new FileInfo(copy).Length;
        long wantedBytes = wanted.Sum(i => (long)i.Data.Length);
        if (after > before + wantedBytes + (64 * 1024) || after < before - layout.OldIconBytes - (64 * 1024) || after > MaxProgramBytes)
        {
            return $"its size ({after.ToString(CultureInfo.InvariantCulture)} bytes, the program has {before.ToString(CultureInfo.InvariantCulture)}) is not what the new icon makes it";
        }

        if (ApphostProblem(copy, original) is { } apphost)
        {
            return apphost;
        }

        PeLayout? a = PeLayout.Read(File.ReadAllBytes(original));
        PeLayout? b = PeLayout.Read(File.ReadAllBytes(copy));
        if (a is null || b is null)
        {
            return "it is not a Windows program";
        }

        if (a.Machine != b.Machine || a.Headers != b.Headers)
        {
            return $"its headers changed (machine {a.Machine} / {b.Machine})";
        }

        if (!a.OtherSections.SequenceEqual(b.OtherSections))
        {
            return "a section other than the resources changed";
        }

        IReadOnlyList<IcoImage>? group = IcoFile.Read(PeIcons.IconGroup(copy) ?? []);
        if (!IcoFile.SameImages(group, wanted))
        {
            return "its icon group does not read back as the wanted images";
        }

        IReadOnlyList<PeResource> was = PeIcons.ResourceList(original) ?? [];
        IReadOnlyList<PeResource> now = PeIcons.ResourceList(copy) ?? [];
        PeResource[] groups = [.. now.Where(r => r.Type == PeIcons.GroupIconType && r.Id == PeIcons.ApplicationIconGroup)];
        if (groups is not [{ } only] || only.Language != layout.Language)
        {
            return $"it has {groups.Length.ToString(CultureInfo.InvariantCulture)} copies of the icon group, not one in language {layout.Language.ToString(CultureInfo.InvariantCulture)}";
        }

        var named = new HashSet<int>();
        foreach (PeResource g in now.Where(r => r.Type == PeIcons.GroupIconType))
        {
            named.UnionWith(IcoFile.GroupImageIds(PeIcons.ResourceData(copy, g.Type, g.Id, g.Language) ?? []) ?? []);
        }

        if (now.FirstOrDefault(r => r.Type == PeIcons.IconType && !named.Contains(r.Id)) is { Size: > 0 } orphan)
        {
            return $"RT_ICON {orphan.Id.ToString(CultureInfo.InvariantCulture)} is left without a group";
        }

        PeResource[] otherWas = [.. was.Where(r => r.Type is not (PeIcons.IconType or PeIcons.GroupIconType))];
        PeResource[] otherNow = [.. now.Where(r => r.Type is not (PeIcons.IconType or PeIcons.GroupIconType))];
        if (!otherWas.SequenceEqual(otherNow))
        {
            return "its other resources (the version block, the manifest) are not the ones it had";
        }

        foreach (PeResource r in otherWas)
        {
            if (!(PeIcons.ResourceData(original, r.Type, r.Id, r.Language) ?? []).AsSpan().SequenceEqual(PeIcons.ResourceData(copy, r.Type, r.Id, r.Language) ?? []))
            {
                return $"resource type {r.Type.ToString(CultureInfo.InvariantCulture)} id {r.Id.ToString(CultureInfo.InvariantCulture)} changed";
            }
        }

        return null;
    }

    // -- the swap -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Moves the checked <paramref name="copy"/> in place of <paramref name="exe"/>, under <see cref="SwapGate"/>; refused when an update of the folder started
    /// meanwhile. The game (never running when it is stamped: <paramref name="running"/> was asked) is replaced in one step, so its name always holds the old or the
    /// new file; Windows refuses that when the game was started after the check (its image is in use), and the game is as it was. The launcher runs, and a running
    /// program can only be renamed: it becomes "&lt;name&gt;.icon-old" and the copy is moved in; when that fails, the original is moved back, or else the checked copy
    /// moved in after all (<see cref="GiveNameBack"/>), so its name is never left empty while either file can be moved. The .icon-old is deleted afterwards when it is
    /// not running (the launcher's is deleted at the next start).
    /// </summary>
    private static void Swap(string exe, string copy, string program, Func<bool>? running, Action<string, StampStep>? onStep)
    {
        string staging = new UpdateStaging(Path.GetDirectoryName(exe)!).Root;
        string? old = string.Equals(program, GameLauncher.LauncherFileName, StringComparison.OrdinalIgnoreCase) ? FreeOldName(exe) : null;
        lock (SwapGate)
        {
            if (Directory.Exists(staging))
            {
                // review of K43: an update that started after the stamp did replaces these files itself; the stamp must not put its copy of the old version over them
                throw new StampRefusedException("an update of this folder started meanwhile; the program is left to it");
            }

            if (old is null)
            {
                Refusable(exe, running, () => File.Move(copy, exe, overwrite: true));
                return;
            }

            Refusable(exe, running, () => File.Move(exe, old));
            try
            {
                onStep?.Invoke(program, StampStep.Renamed);
                File.Move(copy, exe);
            }
            catch
            {
                switch (GiveNameBack(exe, old, copy))
                {
                    case NameBack.Original:
                        throw;
                    case NameBack.None:
                        throw new IOException($"{program} is missing: neither {Path.GetFileName(old)} nor the stamped copy could be moved back to its name (another program holds them); the next stamp or the launcher's exit tries again, or rename {Path.GetFileName(old)} to {program} by hand");
                    default:
                        // the checked copy went in after all: the program carries the wanted icon
                        break;
                }
            }
        }

        DeleteQuietly(old);
    }

    /// <summary>Which file holds the launcher's name after a move-in that failed.</summary>
    private enum NameBack
    {
        /// <summary>The original, moved back (or the name was not empty): the stamp did not happen.</summary>
        Original,

        /// <summary>The checked copy, moved in on a later try: the stamp did happen.</summary>
        Copy,

        /// <summary>Neither could be moved: the name is empty and both files are left.</summary>
        None,
    }

    /// <summary>
    /// After a failed move-in of the launcher's copy: the original back under <paramref name="exe"/>, else the checked <paramref name="copy"/>, five tries in about a
    /// second (a scanner or a backup lets go within moments). PLAY waits on <see cref="SwapGate"/> meanwhile, as it should: there is no program to start.
    /// </summary>
    private static NameBack GiveNameBack(string exe, string old, string copy)
    {
        for (int attempt = 1; ; attempt++)
        {
            if (File.Exists(exe) || TryMove(old, exe))
            {
                return NameBack.Original;
            }

            if (TryMove(copy, exe))
            {
                return NameBack.Copy;
            }

            if (attempt == 5)
            {
                return NameBack.None;
            }

            Thread.Sleep(50 * attempt);
        }
    }

    /// <summary>Runs the move that replaces or renames <paramref name="exe"/>; a refusal of access is remembered for this file (<see cref="NotReplaceable"/>) unless the game was started meanwhile.</summary>
    private static void Refusable(string exe, Func<bool>? running, Action move)
    {
        try
        {
            move();
        }
        catch (UnauthorizedAccessException e)
        {
            if (running?.Invoke() != true)
            {
                NotReplaceable[KeyOf(exe)] = e.Message;
            }

            throw;
        }
    }

    /// <summary>The key of <see cref="NotReplaceable"/>: the full path, the size and the time of the file.</summary>
    private static string KeyOf(string exe)
    {
        var info = new FileInfo(exe);
        return string.Create(CultureInfo.InvariantCulture, $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
    }

    /// <summary>Moves <paramref name="from"/> to <paramref name="to"/>; false when it is not there or cannot be moved now.</summary>
    private static bool TryMove(string from, string to)
    {
        try
        {
            if (!File.Exists(from))
            {
                return false;
            }

            File.Move(from, to);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>"Tsukihime.exe.icon-old", or "Tsukihime.exe.2.icon-old" when that one is still in use (the launcher stamped twice in one session).</summary>
    private static string FreeOldName(string exe)
    {
        for (int n = 1; n < 100; n++)
        {
            string old = n == 1 ? exe + OldSuffix : $"{exe}.{n.ToString(CultureInfo.InvariantCulture)}{OldSuffix}";
            DeleteQuietly(old);
            if (!File.Exists(old))
            {
                return old;
            }
        }

        throw new IOException($"too many {OldSuffix} copies of {Path.GetFileName(exe)} are in use");
    }

    // -- clean-up at the next start -----------------------------------------------------------------------------------

    /// <summary>
    /// What an earlier stamp left in <paramref name="appDirectory"/>: a program that is missing gets back its newest <c>.icon-old</c> copy that reads as a program
    /// (a stamp cut off between its two renames: a crash, power loss), else its checked <c>.icon-new</c> copy when that is a plain apphost; while neither can be
    /// moved, nothing of that program is deleted (the next time tries again). Then, with <paramref name="deleteCopies"/> (the first stamp of a session), every
    /// <c>.icon-old</c> and <c>.icon-new</c> copy of the two programs is deleted (a copy still in use stays for the next time). Every stamp runs the repair
    /// (<paramref name="deleteCopies"/> false), so a program missing after a failed swap comes back as soon as the file that held it lets go (review of K43).
    /// </summary>
    public static StampCleanup CleanLeftovers(string appDirectory, bool deleteCopies = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        string app = Path.GetFullPath(appDirectory);
        var repaired = new List<string>();
        int deleted = 0;
        int left = 0;
        if (!Directory.Exists(app))
        {
            return new StampCleanup(repaired, 0, 0);
        }

        foreach (string program in Programs)
        {
            string exe = Path.Combine(app, program);
            string fresh = exe + NewSuffix;
            List<string> olds = [.. Directory.EnumerateFiles(app, program + "*" + OldSuffix).Where(f => IsOldCopy(Path.GetFileName(f), program))];
            if (!File.Exists(exe))
            {
                string? back = null;
                foreach (string candidate in olds.Where(IsProgram).OrderByDescending(File.GetLastWriteTimeUtc).Append(fresh))
                {
                    if ((candidate != fresh || IsPlainApphost(fresh, exe)) && TryMove(candidate, exe))
                    {
                        back = candidate;
                        break;
                    }
                }

                if (back is null)
                {
                    // nothing is deleted while the program is missing: the next time tries again
                    left += olds.Count + (File.Exists(fresh) ? 1 : 0);
                    continue;
                }

                olds.Remove(back);
                repaired.Add(program);
            }

            if (!deleteCopies)
            {
                continue;
            }

            foreach (string file in olds.Append(fresh).Where(File.Exists))
            {
                if (DeleteQuietly(file))
                {
                    deleted++;
                }
                else
                {
                    left++;
                }
            }
        }

        return new StampCleanup(repaired, deleted, left);
    }

    /// <summary>True when <paramref name="path"/> can be read as a Windows program (an .icon-old copy that may be put back).</summary>
    private static bool IsProgram(string path)
    {
        try
        {
            return PeIcons.ResourceList(path) is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>True when the stamped copy <paramref name="copy"/> is a plain apphost that may stand in for <paramref name="exe"/> (its .dll beside it).</summary>
    private static bool IsPlainApphost(string copy, string exe)
    {
        try
        {
            return File.Exists(copy) && ApphostProblem(copy, exe) is null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True for "Tsukihime.exe.icon-old" and "Tsukihime.exe.2.icon-old", nothing else.</summary>
    private static bool IsOldCopy(string name, string program)
    {
        if (!name.StartsWith(program, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string middle = name[program.Length..^OldSuffix.Length];
        return middle.Length == 0 || (middle.Length > 1 && middle[0] == '.' && middle[1..].All(char.IsAsciiDigit));
    }

    private static bool DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }

            return !File.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Tells Explorer that this one file changed, so that it reads the new icon (SHCNE_UPDATEITEM for the path only: no cache flush, no setting).</summary>
    public static void NotifyShell(string exe)
    {
        try
        {
            Native.SHChangeNotify(Native.ShcneUpdateItem, Native.ShcnfPathW, exe, 0);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // no shell (a server core): nothing to tell
        }
    }

    // -- the parts ----------------------------------------------------------------------------------------------------

    /// <summary>The icon group of a program as it is now: its language, the RT_ICON resources it names (to delete) and the ids other groups keep.</summary>
    /// <param name="Language">The language of the group 32512 (the stamp writes the new group and images in it).</param>
    /// <param name="OldIcons">The RT_ICON resources (id, language) the group names.</param>
    /// <param name="KeptIconIds">RT_ICON ids that stay (named by another group, or by none): the new images take other ids.</param>
    /// <param name="OldIconBytes">The bytes of the old images.</param>
    internal sealed record IconLayout(int Language, IReadOnlyList<(int Id, int Language)> OldIcons, IReadOnlySet<int> KeptIconIds, long OldIconBytes)
    {
        /// <summary>The layout of <paramref name="exe"/>; null when it has no group 32512 or has it in more than one language (not a program the compiler made).</summary>
        public static IconLayout? Of(string exe)
        {
            IReadOnlyList<PeResource>? all = PeIcons.ResourceList(exe);
            PeResource[] groups = [.. (all ?? []).Where(r => r.Type == PeIcons.GroupIconType && r.Id == PeIcons.ApplicationIconGroup)];
            if (all is null || groups is not [{ } group])
            {
                return null;
            }

            IReadOnlyList<int> named = IcoFile.GroupImageIds(PeIcons.ResourceData(exe, group.Type, group.Id, group.Language) ?? []) ?? [];
            (int, int)[] old = [.. all.Where(r => r.Type == PeIcons.IconType && named.Contains(r.Id)).Select(r => (r.Id, r.Language))];
            var kept = all.Where(r => r.Type == PeIcons.IconType && !named.Contains(r.Id)).Select(r => r.Id).ToHashSet();
            long bytes = all.Where(r => r.Type == PeIcons.IconType && named.Contains(r.Id)).Sum(r => (long)r.Size);
            return new IconLayout(group.Language, old, kept, bytes);
        }
    }

    /// <summary>What the check compares of a PE file: the headers, every section other than the resources (name, place, bytes) and every data directory but the resources.</summary>
    /// <param name="Machine">The machine type.</param>
    /// <param name="Headers">The fields of the headers that a resource update must not change, and the data directories other than the resource table.</param>
    /// <param name="OtherSections">Each section but .rsrc: its name, address, sizes and the SHA-256 of its bytes.</param>
    /// <param name="IsManaged">The file carries .NET metadata (an assembly, not an apphost).</param>
    /// <param name="IsDll">The file is a library.</param>
    /// <param name="Signed">The certificate table is not empty.</param>
    /// <param name="Overlay">Bytes after the end of the last section.</param>
    /// <param name="ResourcesLast">The resource table lies in the last section (by file position and address), so a bigger one moves nothing else.</param>
    internal sealed record PeLayout(Machine Machine, string Headers, IReadOnlyList<string> OtherSections, bool IsManaged, bool IsDll, bool Signed, long Overlay, bool ResourcesLast)
    {
        /// <summary>The layout of the PE image <paramref name="bytes"/>; null when it is not one.</summary>
        public static PeLayout? Read(byte[] bytes)
        {
            try
            {
                using var pe = new PEReader(System.Collections.Immutable.ImmutableArray.Create(bytes));
                PEHeaders h = pe.PEHeaders;
                if (h.PEHeader is not { } opt)
                {
                    return null;
                }

                int rsrc = opt.ResourceTableDirectory.RelativeVirtualAddress;
                SectionHeader[] sections = [.. h.SectionHeaders];
                int resourceIndex = Array.FindIndex(sections, s => opt.ResourceTableDirectory.Size > 0 && rsrc >= s.VirtualAddress && rsrc < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData));
                long end = sections.Length == 0 ? 0 : sections.Max(s => (long)s.PointerToRawData + s.SizeOfRawData);
                bool last = resourceIndex >= 0
                    && sections.All(s => s.PointerToRawData <= sections[resourceIndex].PointerToRawData && s.VirtualAddress <= sections[resourceIndex].VirtualAddress);
                var others = new List<string>();
                for (int i = 0; i < sections.Length; i++)
                {
                    if (i == resourceIndex)
                    {
                        continue;
                    }

                    SectionHeader s = sections[i];
                    byte[] raw = s.SizeOfRawData > 0 && s.PointerToRawData + (long)s.SizeOfRawData <= bytes.Length ? bytes.AsSpan(s.PointerToRawData, s.SizeOfRawData).ToArray() : [];
                    others.Add($"{s.Name}|{s.VirtualAddress}|{s.VirtualSize}|{s.PointerToRawData}|{s.SizeOfRawData}|{s.SectionCharacteristics}|{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw))}");
                }

                DirectoryEntry[] directories =
                [
                    opt.ExportTableDirectory, opt.ImportTableDirectory, opt.ExceptionTableDirectory, opt.CertificateTableDirectory, opt.BaseRelocationTableDirectory,
                    opt.DebugTableDirectory, opt.CopyrightTableDirectory, opt.GlobalPointerTableDirectory, opt.ThreadLocalStorageTableDirectory, opt.LoadConfigTableDirectory,
                    opt.BoundImportTableDirectory, opt.ImportAddressTableDirectory, opt.DelayImportTableDirectory, opt.CorHeaderTableDirectory,
                ];
                string headers = string.Join(
                    '|',
                    h.CoffHeader.Machine,
                    h.CoffHeader.Characteristics,
                    h.CoffHeader.NumberOfSections,
                    opt.Magic,
                    opt.AddressOfEntryPoint,
                    opt.ImageBase,
                    opt.SectionAlignment,
                    opt.FileAlignment,
                    opt.Subsystem,
                    opt.DllCharacteristics,
                    opt.SizeOfStackReserve,
                    opt.SizeOfHeapReserve,
                    opt.MajorSubsystemVersion,
                    string.Join(',', directories.Select(d => $"{d.RelativeVirtualAddress}+{d.Size}")));
                return new PeLayout(
                    h.CoffHeader.Machine,
                    headers,
                    others,
                    h.CorHeader is not null,
                    h.IsDll,
                    opt.CertificateTableDirectory.Size > 0,
                    bytes.Length - end,
                    last);
            }
            catch (BadImageFormatException)
            {
                return null;
            }
        }
    }

    /// <summary>A stamp that must not go on (its copy failed a check).</summary>
    internal sealed class StampRefusedException(string message) : Exception(message);

    private static class Native
    {
        public const int ShcneUpdateItem = 0x0000_2000;
        public const uint ShcnfPathW = 0x0005;

        // DllImport, not LibraryImport: the launcher compiles without unsafe code (LauncherApp.NativeMethods)
        [DllImport("kernel32.dll", EntryPoint = "BeginUpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint BeginUpdateResource(string fileName, [MarshalAs(UnmanagedType.Bool)] bool deleteExistingResources);

        [DllImport("kernel32.dll", EntryPoint = "UpdateResourceW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateResource(nint update, nint type, nint name, ushort language, byte[]? data, uint size);

        [DllImport("kernel32.dll", EntryPoint = "EndUpdateResourceW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EndUpdateResource(nint update, [MarshalAs(UnmanagedType.Bool)] bool discard);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern void SHChangeNotify(int eventId, uint flags, string item1, nint item2);
    }
}

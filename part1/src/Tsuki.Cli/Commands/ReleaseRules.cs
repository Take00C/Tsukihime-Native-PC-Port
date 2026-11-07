using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tsuki.Data.Icons;

namespace Tsuki.Cli.Commands;

/// <summary>
/// The limits every release archive is held to, by release-sums and by the macOS and Linux updaters (<see cref="Release"/>); the tests pass smaller ones to reach
/// each check.
/// </summary>
/// <param name="MaxFileBytes">The largest file, unpacked.</param>
/// <param name="MaxTotalBytes">All files together, unpacked.</param>
/// <param name="MaxEntries">Entries in one archive (a release has about 300).</param>
public sealed record ReleaseLimits(long MaxFileBytes, long MaxTotalBytes, int MaxEntries)
{
    /// <summary>The limits of a real release.</summary>
    public static ReleaseLimits Release { get; } = new(ReleaseRules.MaxFileBytes, ReleaseRules.MaxTotalBytes, ReleaseRules.MaxEntries);
}

/// <summary>
/// Which rules a release archive is held to. release-check and release-sums hold this version's release to everything it must hold; the macOS and Linux updaters
/// hold a downloaded release only to what keeps an install safe and able to start, so that an installed launcher still takes later releases whose files change.
/// </summary>
public enum ReleaseRuleSet
{
    /// <summary>release-check and release-sums: the policy, plus this release's exact libraries, licences, notices, modes by file name and Mac signing identifiers.</summary>
    Complete,

    /// <summary>
    /// The updaters: safe names, folders and plain files only, no set-id or sticky bit, nothing the group or others may write, the limits, nothing forbidden, no
    /// personal note, one top folder or Tsukihime.app, both programs with what they need to start, the release's version and the right architecture.
    /// </summary>
    Update,
}

/// <summary>The few facts of an ELF file that release-check and release-sums look at (package K46: the Linux release).</summary>
/// <param name="Is64">ELFCLASS64, little-endian.</param>
/// <param name="Machine">e_machine (62: x86-64).</param>
/// <param name="Type">e_type (2: an executable, 3: a shared object or a position-independent executable).</param>
/// <param name="SoName">DT_SONAME, when the dynamic section names one.</param>
public sealed record ElfFacts(bool Is64, int Machine, int Type, string? SoName)
{
    /// <summary>e_machine of x86-64.</summary>
    public const int X86_64 = 62;

    /// <summary>True for a 64-bit little-endian x86-64 file.</summary>
    public bool IsX64 => Is64 && Machine == X86_64;

    /// <summary>True when <paramref name="head"/> starts with the ELF magic.</summary>
    public static bool HasMagic(ReadOnlySpan<byte> head) => head.Length >= 4 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F';

    /// <summary>Reads <paramref name="data"/>; null when it is not an ELF file.</summary>
    public static ElfFacts? Read(ReadOnlySpan<byte> data)
    {
        if (!HasMagic(data) || data.Length < 64)
        {
            return null;
        }

        bool is64 = data[4] == 2 && data[5] == 1;
        int type = BinaryPrimitives.ReadUInt16LittleEndian(data[16..]);
        int machine = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]);
        return new ElfFacts(is64, machine, type, is64 ? SoNameOf(data) : null);
    }

    private static string? SoNameOf(ReadOnlySpan<byte> d)
    {
        ulong shoff = BinaryPrimitives.ReadUInt64LittleEndian(d[0x28..]);
        int shentsize = BinaryPrimitives.ReadUInt16LittleEndian(d[0x3A..]);
        int shnum = BinaryPrimitives.ReadUInt16LittleEndian(d[0x3C..]);
        if (shoff == 0 || shentsize < 64 || shoff + ((ulong)shentsize * (ulong)shnum) > (ulong)d.Length)
        {
            return null;
        }

        for (int i = 0; i < shnum; i++)
        {
            ReadOnlySpan<byte> sh = d.Slice((int)shoff + (i * shentsize), 64);
            if (BinaryPrimitives.ReadUInt32LittleEndian(sh[4..]) != 6)
            {
                continue;                                                      // SHT_DYNAMIC only
            }

            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(sh[24..]);
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(sh[32..]);
            uint link = BinaryPrimitives.ReadUInt32LittleEndian(sh[40..]);
            if (link >= shnum || offset + size > (ulong)d.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> strings = d.Slice((int)shoff + ((int)link * shentsize), 64);
            ulong strOff = BinaryPrimitives.ReadUInt64LittleEndian(strings[24..]);
            ulong strSize = BinaryPrimitives.ReadUInt64LittleEndian(strings[32..]);
            if (strOff + strSize > (ulong)d.Length)
            {
                return null;
            }

            for (ulong o = offset; o + 16 <= offset + size; o += 16)
            {
                long tag = BinaryPrimitives.ReadInt64LittleEndian(d[(int)o..]);
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(d[((int)o + 8)..]);
                if (tag == 0)
                {
                    break;
                }

                if (tag == 14 && value < strSize)                              // DT_SONAME
                {
                    ReadOnlySpan<byte> name = d[(int)(strOff + value)..(int)(strOff + strSize)];
                    int end = name.IndexOf((byte)0);
                    return Encoding.ASCII.GetString(end < 0 ? name : name[..end]);
                }
            }
        }

        return null;
    }
}

/// <summary>
/// What a folder or archive to be released must and must not hold, beyond the personal-build rules of <see cref="ReleaseCheck"/> (K46/K47): no unapproved picture
/// or game data on any platform, and the native player, SDL, FFmpeg LGPL libraries and licences required by Linux and macOS.
/// </summary>
public static partial class ReleaseRules
{
    /// <summary>The Linux player's program (the apphost).</summary>
    public const string LinuxHost = "Tsukihime";

    /// <summary>The largest file a release archive may unpack to (512 MiB).</summary>
    public const long MaxFileBytes = 512L * 1024 * 1024;

    /// <summary>The most a release archive may unpack to (2 GiB).</summary>
    public const long MaxTotalBytes = 2_147_483_648L;

    /// <summary>The most entries a release archive may have.</summary>
    public const int MaxEntries = 20_000;

    /// <summary>0755: folders, programs and native libraries of a Unix release.</summary>
    public const UnixFileMode Mode755 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>0644: every other file of a Unix release.</summary>
    public const UnixFileMode Mode644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>The two programs of a Unix release, each started through its own runtimeconfig.json and deps.json.</summary>
    public static IReadOnlyList<string> UnixPrograms { get; } = [LinuxHost, "TsukihimeLauncher"];

    /// <summary>The libraries among <see cref="LinuxLibraries"/> that come with the launcher (Avalonia's Skia and HarfBuzz), not with the player.</summary>
    public static IReadOnlyList<string> LinuxLauncherLibraries { get; } = ["libSkiaSharp.so", "libHarfBuzzSharp.so"];

    /// <summary>The hint for a missing launcher library: the publish was of the player, not of the launcher.</summary>
    public const string LauncherPublishHint = "it comes with the launcher; publish src/Tsuki.Launcher.Avalonia, not src/Tsuki.App (docs/PUBLISHING.md 4.2, 4.3)";

    /// <summary>The README's logo, the one picture a release carries (the owner's decision, K46; src/Tsuki.Distribution.targets publishes it).</summary>
    public const string Logo = "assets/branding/tsukihime-logo.png";

    /// <summary>The player, launcher, SDL, Skia/HarfBuzz and FFmpeg libraries required beside the Linux assemblies.</summary>
    public static IReadOnlyList<string> LinuxLibraries { get; } = ["libSDL3.so", "libSkiaSharp.so", "libHarfBuzzSharp.so", "libavutil.so.61", "libswresample.so.7", "libavcodec.so.63", "libavformat.so.63"];

    /// <summary>
    /// The files of the self-contained .NET runtime a Linux release carries beside the player (K46 review): without them the player starts only where .NET 10 is
    /// installed, which the README promises it does not need.
    /// </summary>
    public static IReadOnlyList<string> LinuxRuntime { get; } = ["libhostfxr.so", "libhostpolicy.so", "libcoreclr.so", "libclrjit.so", "System.Private.CoreLib.dll"];

    /// <summary>The FFmpeg libraries among <see cref="LinuxLibraries"/>.</summary>
    public static IReadOnlyList<string> LinuxFfmpeg { get; } = ["libavutil.so.61", "libswresample.so.7", "libavcodec.so.63", "libavformat.so.63"];

    /// <summary>The licence files of a Linux release (the names the Windows release uses, plus the .NET runtime's two).</summary>
    public static IReadOnlyList<string> LinuxLicences { get; } =
    [
        "licenses/FFmpeg-LGPL.txt", "licenses/FFmpeg-GPL-3.0.txt", "licenses/FFmpeg-SOURCE.txt", "licenses/SDL3-zlib.txt", "licenses/knife-MPL-2.0.txt",
        "licenses/LICENSE.txt", "licenses/THIRD_PARTY.md", "licenses/Avalonia-MIT.txt", "licenses/SkiaSharp-LICENSE.txt", "licenses/HarfBuzzSharp-LICENSE.txt",
        "licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt", "licenses/MicroCom-MIT.txt", "licenses/Tmds-DBus-MIT.txt",
        "licenses/dotnet-LICENSE.txt", "licenses/dotnet-THIRD-PARTY-NOTICES.txt", "LINUX.md",
    ];

    private static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tga", ".dds", ".ico", ".icns", ".rgba", ".psd", ".svg"];

    private static readonly string[] GameDataExtensions = [".mrg", ".hed", ".nam", ".mzp", ".mzx", ".at9", ".vag", ".mp4", ".sfo", ".pkg", ".self", ".fself", ".prx", ".sprx", ".trp"];

    private static readonly string[] GameDataNames = ["eboot.bin", "save.dat", "save.dat.bak", "savedata000"];

    /// <summary>
    /// Why <paramref name="relativePath"/> (a path inside the folder or archive, '/' or '\' separated) must not be released, or null. Pictures other than
    /// <see cref="Logo"/> and, only when <paramref name="macBundle"/> is true, the Mac bundle's crescent ICNS are refused wherever they are.
    /// </summary>
    public static string? Forbidden(string relativePath, bool macBundle = false)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string path = relativePath.Replace('\\', '/').TrimStart('/');
        if (macBundle && path.Equals(MacIcon, StringComparison.Ordinal))
        {
            return null;
        }

        // package_macos.py moves the publish's documents and assets below Contents/Resources; the same logo rule applies there.
        if (macBundle && path.StartsWith("Contents/Resources/", StringComparison.Ordinal))
        {
            path = path["Contents/Resources/".Length..];
        }

        string name = path[(path.LastIndexOf('/') + 1)..];
        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (path.Equals(Logo, StringComparison.Ordinal))
        {
            return null;
        }

        if (path.Split('/').Any(p => p.Equals("branding", StringComparison.OrdinalIgnoreCase)) || PictureExtensions.Contains(ext))
        {
            return $"{path} is a picture; a release carries none but the README's logo ({Logo})";
        }

        if (GameDataExtensions.Contains(ext) || GameDataNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return $"{path} is game data or a save; a release carries none";
        }

        // the developer tools (a Debug publish brings the player's dev bridge along); no release of any platform has carried them
        if (DevPrograms.Any(dev => name.Equals(dev, StringComparison.OrdinalIgnoreCase) || name.StartsWith(dev + ".", StringComparison.OrdinalIgnoreCase)))
        {
            return $"{path} is a developer program; a release carries none (publish with -c Release)";
        }

        return null;
    }

    private static readonly string[] DevPrograms = ["Tsuki.Cli", "Tsuki.DevBridge"];

    /// <summary>The problems of every file under <paramref name="folder"/> that <see cref="Forbidden"/> refuses.</summary>
    public static IEnumerable<string> ForbiddenFiles(string folder, bool macBundle = false)
    {
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (Forbidden(Path.GetRelativePath(folder, file), macBundle) is { } why)
            {
                yield return why;
            }
        }
    }

    /// <summary>Why the bytes of the FFmpeg library <paramref name="name"/> are not those of an LGPL-only build, or null.</summary>
    public static string? FfmpegLicenceProblem(string name, ReadOnlySpan<byte> data)
    {
        // the library keeps its configure line (avcodec_configuration) and its licence string (avcodec_license: "libavcodec license: LGPL version 3 or later")
        if (data.IndexOf("--enable-gpl"u8) >= 0 || data.IndexOf("--enable-nonfree"u8) >= 0)
        {
            return $"{name} was built with --enable-gpl or --enable-nonfree: only an LGPL build of FFmpeg may be released";
        }

        return data.IndexOf("license: LGPL version"u8) < 0 ? $"{name} does not carry FFmpeg's LGPL licence string: not an LGPL build of FFmpeg" : null;
    }

    /// <summary>Why an archive entry of <paramref name="bytes"/> (unpacked) after <paramref name="total"/> bytes breaks the <paramref name="limits"/>, or null.</summary>
    public static string? UncompressedSizeProblem(long bytes, long total, string name, ReleaseLimits? limits = null)
    {
        limits ??= ReleaseLimits.Release;
        return bytes < 0 || bytes > limits.MaxFileBytes || total + bytes > limits.MaxTotalBytes
            ? $"{name}: exceeds the {Size(limits.MaxFileBytes)} per-file or {Size(limits.MaxTotalBytes)} total uncompressed-size limit"
            : null;
    }

    private static string Size(long bytes) => bytes % (1L << 30) == 0 ? $"{bytes >> 30} GiB" : bytes % (1L << 20) == 0 ? $"{bytes >> 20} MiB" : $"{bytes} bytes";

    /// <summary>
    /// Why the name of an archive entry could put it outside the folder it is unpacked into, or null: no NUL or control character, not absolute, no backslash or
    /// colon (Windows and macOS read those as separators or drive letters), and no empty, "." or ".." part. A folder entry's one trailing '/' is not a part.
    /// </summary>
    public static string? EntryPathProblem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string path = name.EndsWith('/') ? name[..^1] : name;
        return path.Any(c => c == '\0' || char.IsControl(c)) ? "it contains a NUL or control character"
            : path.StartsWith('/') ? "it is an absolute path"
            : path.Contains('\\', StringComparison.Ordinal) ? "it contains a backslash"
            : path.Contains(':', StringComparison.Ordinal) ? "it contains a colon"
            : path.Split('/').Any(part => part is "" or "." or "..") ? "it has an empty, '.' or '..' part"
            : null;
    }

    /// <summary>
    /// The mode tools/setup/package_linux.py gives an entry of the Linux archive: 0755 for folders, both programs, createdump and the shared objects (*.so, *.so.N),
    /// 0644 for every other file.
    /// </summary>
    public static UnixFileMode LinuxMode(string relative, bool directory)
    {
        ArgumentNullException.ThrowIfNull(relative);
        string name = relative[(relative.LastIndexOf('/') + 1)..];
        return directory || name is LinuxHost or "TsukihimeLauncher" or "createdump" || SharedObject().IsMatch(name) ? Mode755 : Mode644;
    }

    /// <summary>
    /// Why one entry of the Linux archive (<paramref name="relative"/>: its path inside the archive's one folder) may not be released (<see cref="ReleaseRuleSet.Complete"/>)
    /// or installed (<see cref="ReleaseRuleSet.Update"/>), or null: only folders and plain files, nothing <see cref="Forbidden"/>, no personal note, and for a release
    /// the mode of <see cref="LinuxMode"/>, for an update any mode <see cref="ModePolicyProblem"/> allows (both programs runnable by their owner).
    /// </summary>
    public static string? LinuxTarEntryProblem(string relative, TarEntryType type, UnixFileMode mode, ReleaseRuleSet rules = ReleaseRuleSet.Complete)
    {
        ArgumentNullException.ThrowIfNull(relative);
        bool directory = type is TarEntryType.Directory;
        if (!directory && type is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
        {
            return $"{relative} is a {type} entry; the archive holds only folders and plain files";
        }

        if (!directory && Forbidden(relative) is { } forbidden)
        {
            return forbidden;
        }

        if (string.Equals(relative[(relative.LastIndexOf('/') + 1)..], ReleaseCheck.PersonalNote, StringComparison.OrdinalIgnoreCase))
        {
            return $"holds {relative}: a personal build is never released";
        }

        string shown = relative + (directory ? "/" : string.Empty);
        if (rules == ReleaseRuleSet.Update)
        {
            return ModePolicyProblem((int)mode, directory, program: !directory && UnixPrograms.Contains(relative)) is { } why
                ? $"{shown} has the mode {Convert.ToString((int)mode, 8)}: {why}"
                : null;
        }

        UnixFileMode expected = LinuxMode(relative, directory);
        return mode != expected
            ? $"{shown} has the mode {Convert.ToString((int)mode, 8)}, not {Convert.ToString((int)expected, 8)} (build the archive with tools/setup/package_linux.py)"
            : null;
    }

    /// <summary>
    /// Why the permission bits <paramref name="mode"/> of an entry are not allowed by the updaters' policy (<see cref="ReleaseRuleSet.Update"/>), or null: no set-id or
    /// sticky bit, nothing the group or others may write, a folder its owner may read, write and enter and a file its owner may read and write (so that a later update
    /// can replace them), and for one of the two programs (<paramref name="program"/>) one its owner may run.
    /// </summary>
    public static string? ModePolicyProblem(int mode, bool directory, bool program = false) =>
        (mode & 0xE00) != 0 ? "it has a set-id or sticky bit"
        : (mode & 0x12) != 0 ? "the group or others may write it"
        : directory && (mode & 0x1C0) != 0x1C0 ? "its owner cannot read, write and enter it"
        : !directory && (mode & 0x180) != 0x180 ? "its owner cannot read and write it"
        : program && (mode & 0x40) == 0 ? "its owner cannot run it"
        : null;

    /// <summary>
    /// Unpacks the Linux release archive <paramref name="tarPath"/> (NAME.tar.gz, in a release Tsukihime-vX.Y.Z-linux-x64.tar.gz) into <paramref name="destination"/>,
    /// emptied first, the one way release-sums and the Linux updater read it: every entry inside the one folder NAME/, with a safe name
    /// (<see cref="EntryPathProblem"/>), once, within <paramref name="limits"/>, and allowed by <see cref="LinuxTarEntryProblem"/> under <paramref name="rules"/>. Every
    /// entry is checked; none is written after the first problem, so a refused archive never puts a forbidden file on disk. Returns destination/NAME.
    /// </summary>
    public static string UnpackLinuxTar(string tarPath, string destination, List<string> problems, ReleaseLimits? limits = null, CancellationToken cancel = default,
        ReleaseRuleSet rules = ReleaseRuleSet.Complete)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tarPath);
        ArgumentNullException.ThrowIfNull(problems);
        limits ??= ReleaseLimits.Release;
        string archive = Path.GetFileName(tarPath);
        string top = archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? archive[..^".tar.gz".Length] : Path.GetFileNameWithoutExtension(archive);
        string root = Path.Combine(destination, top);
        if (!EmptyFolder(destination, problems))
        {
            return root;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);       // a release never has two names that differ in case only; Windows and macOS would merge them
        long total = 0;
        int entries = 0;
        using FileStream file = File.OpenRead(tarPath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            cancel.ThrowIfCancellationRequested();
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes)
            {
                continue;
            }

            if (++entries > limits.MaxEntries)
            {
                problems.Add($"the archive has more than {limits.MaxEntries} entries");
                break;
            }

            string name = entry.Name;
            bool directory = entry.EntryType is TarEntryType.Directory;
            string trimmed = directory && name.EndsWith('/') ? name[..^1] : name;
            string relative = trimmed.Length > top.Length ? trimmed[(top.Length + 1)..] : string.Empty;
            string? problem = EntryPathProblem(name) is { } unsafePath ? $"the entry {Printable(name)} would land outside the folder: {unsafePath}"
                : trimmed != top && !trimmed.StartsWith(top + "/", StringComparison.Ordinal) ? $"the entry {name} is not inside the one folder {top}/ (archive the publish folder under that name: tools/setup/package_linux.py)"
                : !paths.Add(trimmed) ? $"the entry {trimmed} occurs more than once"
                : relative.Length == 0 ? TopFolderProblem(top, directory, entry.Mode, rules)
                : LinuxTarEntryProblem(relative, entry.EntryType, entry.Mode, rules);
            if (problem is not null)
            {
                problems.Add(problem);
                continue;
            }

            if (!directory && UncompressedSizeProblem(entry.Length, total, relative, limits) is { } size)
            {
                problems.Add(size);
                break;
            }

            total += directory ? 0 : entry.Length;
            if (problems.Count > 0)
            {
                continue;
            }

            string target = relative.Length == 0 ? root : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (directory)
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (CopyExactly(entry.DataStream, target, entry.Length, relative, cancel) is { } copy)
                {
                    problems.Add(copy);
                    continue;
                }

                File.SetLastWriteTimeUtc(target, entry.ModificationTime.UtcDateTime);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, entry.Mode);
            }
        }

        return root;
    }

    private static string? TopFolderProblem(string top, bool directory, UnixFileMode mode, ReleaseRuleSet rules) =>
        rules == ReleaseRuleSet.Update
            ? directory && ModePolicyProblem((int)mode, directory: true) is null ? null : $"{top} is not a folder its owner may change (mode {Convert.ToString((int)mode, 8)})"
            : directory && mode == Mode755 ? null : $"{top} is not a folder of mode 755";

    /// <summary>Copies exactly <paramref name="expected"/> bytes of an archive entry into the new file <paramref name="target"/>; why not, or null.</summary>
    public static string? CopyExactly(Stream? input, string target, long expected, string name, CancellationToken cancel = default)
    {
        if (input is null && expected != 0)
        {
            return $"{name} has no file data";
        }

        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[1 << 16];
        long copied = 0;
        int read;
        while (input is not null && (read = input.Read(buffer)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            copied += read;
            if (copied > expected)
            {
                return $"{name} is longer than its archive entry";
            }

            output.Write(buffer, 0, read);
        }

        return copied != expected ? $"{name} is shorter than its archive entry" : null;
    }

    /// <summary>Deletes <paramref name="folder"/> and makes it again, empty; false (and a problem) when what was there could not be removed.</summary>
    private static bool EmptyFolder(string folder, List<string> problems)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{folder} could not be emptied ({e.Message})");
            return false;
        }

        Directory.CreateDirectory(folder);
        return true;
    }

    private static string Printable(string name) => string.Concat(name.Select(c => char.IsControl(c) ? '?' : c));

    /// <summary>
    /// The checks of one managed assembly of a release (<paramref name="program"/>.dll in <paramref name="folder"/>): readable metadata, no personal mark, the
    /// release's <paramref name="version"/> and, when <paramref name="crescent"/> is given, the port's crescent icon. The updaters pass no crescent: release-sums
    /// checked the icon before the upload, and a launcher must not refuse a later release for new icon art.
    /// </summary>
    public static void CheckReleaseAssembly(string folder, string program, string version, IReadOnlyList<IconFrame>? crescent, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        string dll = Path.Combine(folder, program + ".dll");
        if (!File.Exists(dll))
        {
            problems.Add($"{program}.dll is missing");
            return;
        }

        AssemblyMarks? marks = PeIcons.Marks(dll);
        string? info = marks?.InformationalVersion;
        string plain = info is null ? string.Empty : info.Split('+')[0];
        if (marks is null)
        {
            problems.Add($"{program}.dll carries no managed metadata");
        }
        else if (marks.IsPersonalBuild || plain.Contains("-personal", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"{program}.dll is a personal build ({info})");
        }
        else if (plain != version)
        {
            problems.Add($"{program}.dll says version {info ?? "(none)"}, the release says {version}: set <Version>{version}</Version> and publish again");
        }
        else if (crescent is not null && ReleaseCheck.IconProblem(dll, crescent) is { } icon)
        {
            problems.Add($"{program}.dll {icon}");
        }
    }

    /// <summary>
    /// The files without which a program of a Unix release does not start, for both <see cref="UnixPrograms"/> in <paramref name="folder"/> (the publish folder, or
    /// Contents/MacOS): its runtimeconfig.json and deps.json, and every assembly and native library its deps.json lists (for the launcher Avalonia, Skia and
    /// HarfBuzz; for both the self-contained runtime).
    /// </summary>
    public static void CheckStartFiles(string folder, List<string> problems, List<string> notes)
    {
        ArgumentNullException.ThrowIfNull(problems);
        ArgumentNullException.ThrowIfNull(notes);
        foreach (string program in UnixPrograms)
        {
            string role = program == LinuxHost ? "the player" : "the launcher";
            int before = problems.Count;
            if (!File.Exists(Path.Combine(folder, program + ".runtimeconfig.json")))
            {
                problems.Add($"{program}.runtimeconfig.json is missing: {role} cannot start without it");
            }

            string deps = Path.Combine(folder, program + ".deps.json");
            IReadOnlyList<string>? listed = File.Exists(deps) ? DepsFiles(deps) : null;
            if (!File.Exists(deps))
            {
                problems.Add($"{program}.deps.json is missing: {role} cannot start without it");
            }
            else if (listed is null)
            {
                problems.Add($"{program}.deps.json is not a readable dependency file");
            }
            else if (listed.Where(f => !File.Exists(Path.Combine(folder, f))).ToList() is { Count: > 0 } missing)
            {
                problems.Add($"{string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? $" and {missing.Count - 5} more" : string.Empty)}, listed in {program}.deps.json, not beside {program}: {role} would not start");
            }

            if (problems.Count == before)
            {
                notes.Add($"{program}: runtimeconfig.json, deps.json and the {listed!.Count} files it lists");
            }
        }
    }

    /// <summary>The file names of every runtime assembly and native library a deps.json lists (its "targets"), or null when it cannot be read.</summary>
    public static IReadOnlyList<string>? DepsFiles(string depsJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(depsJson));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("targets", out JsonElement targets) || targets.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty target in targets.EnumerateObject().Where(t => t.Value.ValueKind == JsonValueKind.Object))
            {
                foreach (JsonProperty library in target.Value.EnumerateObject().Where(l => l.Value.ValueKind == JsonValueKind.Object))
                {
                    foreach (string kind in new[] { "runtime", "native" })
                    {
                        if (library.Value.TryGetProperty(kind, out JsonElement assets) && assets.ValueKind == JsonValueKind.Object)
                        {
                            foreach (JsonProperty asset in assets.EnumerateObject())
                            {
                                string fileName = asset.Name[(asset.Name.LastIndexOf('/') + 1)..];
                                if (fileName.Length > 0 && EntryPathProblem(fileName) is null)
                                {
                                    names.Add(fileName);
                                }
                            }
                        }
                    }
                }
            }

            return [.. names];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\.so(\.\d+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SharedObject();

    /// <summary>
    /// What the Linux updater holds an unpacked release (<paramref name="root"/>) to (<see cref="ReleaseRuleSet.Update"/>): both programs x86-64 ELF programs with
    /// what they need to start (<see cref="CheckStartFiles"/>: their runtimeconfig.json, deps.json and every file the new deps.json lists), and both assemblies of
    /// <paramref name="version"/> without a personal mark. No list of libraries, licences or notices of this version: release-sums checked those before the upload.
    /// </summary>
    public static void CheckLinuxUpdate(string root, string version, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        CheckLinuxPrograms(root, problems, []);
        CheckStartFiles(root, problems, []);
        foreach (string program in UnixPrograms)
        {
            CheckReleaseAssembly(root, program, version, null, problems);
        }
    }

    private static void CheckLinuxPrograms(string folder, List<string> problems, List<string> notes)
    {
        foreach (string host in UnixPrograms)
        {
            ElfFacts? elf = File.Exists(Path.Combine(folder, host)) ? ElfFacts.Read(File.ReadAllBytes(Path.Combine(folder, host))) : null;
            if (elf is not { IsX64: true, Type: 2 or 3 })
            {
                problems.Add($"{host} is not an x86-64 ELF program");
            }
            else
            {
                notes.Add($"{host}: x86-64 ELF program");
            }
        }
    }

    /// <summary>The checks of a Linux publish folder (the player <see cref="LinuxHost"/> beside Tsukihime.dll): its programs, libraries and licences.</summary>
    public static void CheckLinuxFolder(string folder, List<string> problems, List<string> notes)
    {
        ArgumentNullException.ThrowIfNull(problems);
        ArgumentNullException.ThrowIfNull(notes);
        CheckLinuxPrograms(folder, problems, notes);
        foreach (string lib in LinuxLibraries)
        {
            string path = Path.Combine(folder, lib);
            if (!File.Exists(path))
            {
                problems.Add(LinuxLauncherLibraries.Contains(lib)
                    ? $"{lib} is not beside {LinuxHost}: {LauncherPublishHint}"
                    : $"{lib} is not beside {LinuxHost}: the Linux release bundles SDL and FFmpeg 9 (tools/setup/ffmpeg_linux.py, docs/PUBLISHING.md)");
                continue;
            }

            byte[] data = File.ReadAllBytes(path);
            ElfFacts? facts = ElfFacts.Read(data);
            if (facts is not { IsX64: true, Type: 3 })
            {
                problems.Add($"{lib} is not an x86-64 ELF shared object");
                continue;
            }

            string? licence = LinuxFfmpeg.Contains(lib) ? FfmpegLicenceProblem(lib, data) : null;
            if (licence is not null)
            {
                problems.Add(licence);
            }
            else
            {
                notes.Add($"{lib}: x86-64 shared object{(facts.SoName is { } so ? $" ({so})" : string.Empty)}{(LinuxFfmpeg.Contains(lib) ? ", LGPL build" : string.Empty)}");
            }
        }

        string[] runtime = [.. LinuxRuntime.Where(f => !File.Exists(Path.Combine(folder, f)))];
        if (runtime.Length > 0)
        {
            problems.Add($"{string.Join(", ", runtime)} not beside {LinuxHost}: the Linux release is a self-contained publish (--self-contained); a framework-dependent one starts only where .NET 10 is installed");
        }
        else
        {
            notes.Add("the self-contained .NET runtime is beside the player");
        }

        CheckStartFiles(folder, problems, notes);
        foreach (string gpl in Directory.EnumerateFiles(folder, "libpostproc*"))
        {
            problems.Add($"{Path.GetFileName(gpl)} is FFmpeg's GPL-only post-processing library: not an LGPL build");
        }

        foreach (string windows in Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories))
        {
            problems.Add($"{Path.GetRelativePath(folder, windows)} is a Windows program in the Linux folder");
        }

        foreach (string licence in LinuxLicences)
        {
            if (!File.Exists(Path.Combine(folder, licence)))
            {
                problems.Add($"{licence} is missing: a Linux publish carries it (src/Tsuki.Distribution.targets)");
            }
        }

        string source = Path.Combine(folder, "licenses", "FFmpeg-SOURCE.txt");
        if (File.Exists(source))
        {
            string text = File.ReadAllText(source);
            string[] missing = [.. LinuxFfmpeg.Where(l => !text.Contains(l, StringComparison.Ordinal))];
            if (!text.Contains("linux64-lgpl-shared", StringComparison.Ordinal) || missing.Length > 0)
            {
                problems.Add("licenses/FFmpeg-SOURCE.txt is not the notice of the Linux libraries (it must name BtbN's linux64-lgpl-shared build and the four .so files; tools/setup/ffmpeg_linux.py writes it)");
            }
            else
            {
                notes.Add("licenses/FFmpeg-SOURCE.txt names the Linux FFmpeg build");
            }
        }
    }
}

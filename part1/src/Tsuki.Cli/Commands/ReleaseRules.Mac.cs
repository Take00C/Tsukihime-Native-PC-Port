using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Tsuki.Data.Icons;

namespace Tsuki.Cli.Commands;

/// <summary>The fields release-check needs from a 64-bit little-endian Mach-O and its load commands.</summary>
/// <param name="CpuType">CPU_TYPE_ARM64 (0x0100000c) or CPU_TYPE_X86_64 (0x01000007).</param>
/// <param name="Type">MH_EXECUTE (2) for the player or MH_DYLIB (6) for a shared library.</param>
/// <param name="InstallName">The LC_ID_DYLIB name of a shared library.</param>
/// <param name="Dependencies">Names in the LC_LOAD_DYLIB family of commands.</param>
/// <param name="MinimumOS">The minimum system version in LC_BUILD_VERSION, when present.</param>
/// <param name="MinimumOSPlatform">The Mach-O platform; releases accept only macOS (1).</param>
/// <param name="HasCodeSignature">Whether LC_CODE_SIGNATURE names a signature blob.</param>
/// <param name="CodeSignatureIdentifier">The identifier in the embedded CodeDirectory, when readable.</param>
/// <param name="Universal">Whether this slice came from a universal binary.</param>
public sealed record MachOFacts(
    uint CpuType,
    int Type,
    string? InstallName,
    IReadOnlyList<string> Dependencies,
    Version? MinimumOS,
    uint? MinimumOSPlatform,
    bool HasCodeSignature,
    string? CodeSignatureIdentifier,
    bool Universal = false)
{
    /// <summary>Reads a thin Mach-O, or the requested architecture's slice of a universal library, with bounded load commands.</summary>
    public static MachOFacts? Read(ReadOnlySpan<byte> data, string? architecture = null)
    {
        if (data.Length >= 8 && data[..4].SequenceEqual(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }))
        {
            return architecture is null ? null : ReadUniversal(data, architecture);
        }

        if (data.Length < 32 || !data[..4].SequenceEqual(new byte[] { 0xCF, 0xFA, 0xED, 0xFE }))
        {
            return null;
        }

        uint commandCount = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        int commandBytes = BinaryPrimitives.ReadInt32LittleEndian(data[20..]);
        if (commandBytes < 0 || commandBytes > data.Length - 32)
        {
            return null;
        }

        string? installName = null;
        var dependencies = new List<string>();
        Version? minimumOS = null;
        uint? minimumPlatform = null;
        bool codeSignature = false;
        string? signatureIdentifier = null;
        int offset = 32;
        int end = offset + commandBytes;
        for (uint i = 0; i < commandCount; i++)
        {
            if (offset > end - 8)
            {
                return null;
            }

            uint command = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            int size = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);
            if (size < 8 || size > end - offset)
            {
                return null;
            }

            if (command is 0xD or 0xC or 0x20 or 0x80000018 or 0x8000001F or 0x80000023)
            {
                if (size < 24 || DylibName(data[offset..], size) is not { } name)
                {
                    return null;
                }

                if (command == 0xD)
                {
                    installName = name;
                }
                else
                {
                    dependencies.Add(name);
                }
            }
            else if (command is 0x32 or 0x24)
            {
                if (size < (command == 0x32 ? 24 : 16))
                {
                    return null;
                }

                uint packed = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + (command == 0x32 ? 12 : 8))..]);
                minimumOS = new Version((int)(packed >> 16), (int)((packed >> 8) & 0xFF), (int)(packed & 0xFF));
                minimumPlatform = command == 0x32 ? BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]) : 1;
            }
            else if (command == 0x1D)
            {
                if (size < 16)
                {
                    return null;
                }

                uint signatureOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]);
                uint signatureSize = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 12)..]);
                codeSignature = signatureSize != 0;
                signatureIdentifier = codeSignature ? ReadSignatureIdentifier(data, signatureOffset, signatureSize) : null;
            }

            offset += size;
        }

        return offset == end
            ? new MachOFacts(
                BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(data[12..]),
                installName,
                dependencies,
                minimumOS,
                minimumPlatform,
                codeSignature,
                signatureIdentifier)
            : null;
    }

    private static MachOFacts? ReadUniversal(ReadOnlySpan<byte> data, string architecture)
    {
        uint count = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (count == 0 || count > (uint)(data.Length - 8) / 20)
        {
            return null;
        }

        for (uint i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = data.Slice(8 + (int)(i * 20), 20);
            if (Architecture(BinaryPrimitives.ReadUInt32BigEndian(entry)) != architecture)
            {
                continue;
            }

            uint offset = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            if ((ulong)offset + size > (ulong)data.Length)
            {
                return null;
            }

            return Read(data.Slice((int)offset, (int)size)) is { } facts ? facts with { Universal = true } : null;
        }

        return null;
    }

    private static string? Architecture(uint cpuType) => cpuType switch
    {
        0x0100000C => "arm64",
        0x01000007 => "x64",
        _ => null,
    };

    private static string? DylibName(ReadOnlySpan<byte> command, int size)
    {
        uint nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(command[8..]);
        if (nameOffset < 24 || nameOffset >= (uint)size)
        {
            return null;
        }

        ReadOnlySpan<byte> bytes = command[(int)nameOffset..size];
        int length = bytes.IndexOf((byte)0);
        return length > 0 ? Encoding.UTF8.GetString(bytes[..length]) : null;
    }

    private static string? ReadSignatureIdentifier(ReadOnlySpan<byte> data, uint offset, uint size)
    {
        if ((ulong)offset + size > (ulong)data.Length || size < 12)
        {
            return null;
        }

        ReadOnlySpan<byte> blob = data.Slice((int)offset, (int)size);
        if (BinaryPrimitives.ReadUInt32BigEndian(blob) != 0xFADE0CC0)
        {
            return null;
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(blob[4..]);
        if (length < 12 || length > (uint)blob.Length)
        {
            return null;
        }

        blob = blob[..(int)length];
        uint count = BinaryPrimitives.ReadUInt32BigEndian(blob[8..]);
        if (count > (uint)(blob.Length - 12) / 8)
        {
            return null;
        }

        for (uint i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> index = blob[(int)(12 + (i * 8))..];
            uint directoryOffset = BinaryPrimitives.ReadUInt32BigEndian(index[4..]);
            if (blob.Length < 24 || directoryOffset > (uint)(blob.Length - 24))
            {
                return null;
            }

            ReadOnlySpan<byte> directory = blob[(int)directoryOffset..];
            uint magic = BinaryPrimitives.ReadUInt32BigEndian(directory);
            if (magic != 0xFADE0C02)
            {
                continue;
            }

            uint directoryLength = BinaryPrimitives.ReadUInt32BigEndian(directory[4..]);
            if (directoryLength < 24 || directoryLength > (uint)directory.Length)
            {
                return null;
            }

            uint identifierOffset = BinaryPrimitives.ReadUInt32BigEndian(directory[20..]);
            if (identifierOffset >= directoryLength)
            {
                return null;
            }

            ReadOnlySpan<byte> identifier = directory[(int)identifierOffset..(int)directoryLength];
            int end = identifier.IndexOf((byte)0);
            return end > 0 ? Encoding.UTF8.GetString(identifier[..end]) : null;
        }

        return null;
    }
}

/// <summary>A folder or file of a checked Mac release ZIP (<see cref="ReleaseRules.CheckMacZipEntries"/>).</summary>
/// <param name="Entry">The ZIP entry.</param>
/// <param name="Relative">Its path inside Tsukihime.app, '/' separated.</param>
/// <param name="IsDirectory">True for a folder.</param>
/// <param name="Mode">Its Unix permission bits.</param>
public sealed record MacZipEntry(ZipArchiveEntry Entry, string Relative, bool IsDirectory, int Mode);

public static partial class ReleaseRules
{
    /// <summary>The Mac player's program (the apphost).</summary>
    public const string MacHost = "Tsukihime";

    public const string MacLauncher = "TsukihimeLauncher";

    public static IReadOnlyList<string> MacHosts { get; } = [MacHost, MacLauncher];

    /// <summary>The app's CFBundleIdentifier; codesign gives it to the bundle's main program, the launcher, as its signing identifier.</summary>
    public const string MacBundleIdentifier = "io.github.take00c.tsukihime";

    /// <summary>
    /// What package_macos.py passes to codesign --prefix (tools/setup/package_macos.py holds the same literal). codesign puts it before the file name of a nested
    /// program whose name has no dot (signer.cpp), so the player is not signed as "Tsukihime-" plus a UUID that changes with every build.
    /// </summary>
    public const string MacSigningPrefix = MacBundleIdentifier + ".";

    /// <summary>The signing identifier release-sums expects of the nested player, Contents/MacOS/Tsukihime (the updater does not compare identifiers).</summary>
    public const string MacHostSignatureIdentifier = MacSigningPrefix + MacHost;

    /// <summary>The signing identifier release-sums expects of <paramref name="program"/>, one of <see cref="MacHosts"/>.</summary>
    public static string MacSignatureIdentifier(string program) => program == MacLauncher ? MacBundleIdentifier : MacHostSignatureIdentifier;

    /// <summary>The path of the one image besides the README logo that a Mac release carries: the port's crescent.</summary>
    public const string MacIcon = "Contents/Resources/Tsukihime.icns";

    private static readonly Version MacMinimumOS = new(15, 0, 0);

    /// <summary>The four replaceable FFmpeg 9 dylibs under Contents/MacOS.</summary>
    public static IReadOnlyList<string> MacFfmpeg { get; } = ["libavutil.61.dylib", "libswresample.7.dylib", "libavcodec.63.dylib", "libavformat.63.dylib"];

    /// <summary>The native libraries required by the player and launcher.</summary>
    public static IReadOnlyList<string> MacLibraries { get; } = ["libSDL3.dylib", "libAvaloniaNative.dylib", "libSkiaSharp.dylib", "libHarfBuzzSharp.dylib", .. MacFfmpeg];

    /// <summary>Files proving that the app does not need a separately installed .NET runtime.</summary>
    public static IReadOnlyList<string> MacRuntime { get; } = ["libhostfxr.dylib", "libhostpolicy.dylib", "libcoreclr.dylib", "libclrjit.dylib", "System.Private.CoreLib.dll"];

    /// <summary>The notices and player guide inside Contents/Resources.</summary>
    public static IReadOnlyList<string> MacLicences { get; } =
    [
        "licenses/FFmpeg-LGPL-2.1.txt", "licenses/FFmpeg-LICENSE.md", "licenses/FFmpeg-SOURCE.txt", "licenses/SDL3-zlib.txt", "licenses/knife-MPL-2.0.txt",
        "licenses/LICENSE.txt", "licenses/THIRD_PARTY.md", "licenses/Avalonia-MIT.txt", "licenses/SkiaSharp-LICENSE.txt", "licenses/HarfBuzzSharp-LICENSE.txt",
        "licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt", "licenses/MicroCom-MIT.txt", "licenses/Tmds-DBus-MIT.txt",
        "licenses/dotnet-LICENSE.txt", "licenses/dotnet-THIRD-PARTY-NOTICES.txt", "MACOS.md",
    ];

    /// <summary>The libraries among <see cref="MacLibraries"/> that come with the launcher (Avalonia's native part, Skia and HarfBuzz), not with the player.</summary>
    public static IReadOnlyList<string> MacLauncherLibraries { get; } = ["libAvaloniaNative.dylib", "libSkiaSharp.dylib", "libHarfBuzzSharp.dylib"];

    /// <summary>
    /// The largest ditto metadata entry a Mac release ZIP may hold: the AppleDouble file of one Contents/MacOS file's code-signature attributes (the page hashes of
    /// a 20 MB assembly take about 200 KB).
    /// </summary>
    public const long MaxMetadataBytes = 1024 * 1024;

    private const string MacApp = "Tsukihime.app";

    private const string MacMetadata = "__MACOSX";

    /// <summary>
    /// The per-entry rules of a Mac release ZIP, shared by release-sums and the macOS updater: at most <see cref="ReleaseLimits.MaxEntries"/> entries, one
    /// Tsukihime.app root, safe names (<see cref="EntryPathProblem"/>) each once, folders and plain files only, nothing <see cref="Forbidden"/>, no personal note, and
    /// the size limits. A release (<see cref="ReleaseRuleSet.Complete"/>) has package_macos.py's modes (0755 for folders, both programs, createdump and the dylibs;
    /// 0644 else); an update any mode <see cref="ModePolicyProblem"/> allows. codesign --deep keeps the signatures of the non-Mach-O files in Contents/MacOS (the .dll
    /// and .json files) in extended attributes, which ditto --sequesterRsrc stores as AppleDouble files: exactly those are accepted, __MACOSX/Tsukihime.app/PATH/._NAME
    /// beside an entry Tsukihime.app/PATH/NAME of the same ZIP (and __MACOSX/._Tsukihime.app for the bundle folder's own attributes), a small plain file that starts
    /// with the AppleDouble magic (00 05 16 07), under __MACOSX folders that mirror the app's own; every other __MACOSX or ._ entry is refused. The metadata entries
    /// are never returned: only ditto reads them. Returns the folders and files to unpack, in ZIP order.
    /// </summary>
    public static IReadOnlyList<MacZipEntry> CheckMacZipEntries(ZipArchive zip, List<string> problems, ReleaseLimits? limits = null, ReleaseRuleSet rules = ReleaseRuleSet.Complete)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(problems);
        limits ??= ReleaseLimits.Release;
        var entries = new List<MacZipEntry>();
        if (zip.Entries.Count > limits.MaxEntries)
        {
            problems.Add($"the ZIP has more than {limits.MaxEntries} entries");
            return entries;
        }

        var real = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);       // the app's entries (APFS ignores case), true for a folder
        var metadata = new List<ZipArchiveEntry>();
        long total = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string name = entry.FullName;
            bool directory = name.EndsWith('/');
            string trimmed = directory ? name[..^1] : name;
            if (EntryPathProblem(name) is { } unsafePath)
            {
                problems.Add($"the entry {Printable(name)} would land outside {MacApp}: {unsafePath}");
                continue;
            }

            if (trimmed.Split('/') is var parts && (parts[0] == MacMetadata || parts.Any(part => part.StartsWith("._", StringComparison.Ordinal))))
            {
                metadata.Add(entry);
                continue;
            }

            if (trimmed != MacApp && !trimmed.StartsWith(MacApp + "/", StringComparison.Ordinal))
            {
                problems.Add($"the entry {name} is outside the one {MacApp} root");
                continue;
            }

            if (!real.TryAdd(trimmed, directory))
            {
                problems.Add($"the entry {name} occurs more than once");
                continue;
            }

            int unix = (int)((uint)entry.ExternalAttributes >> 16);
            int kind = unix & 0xF000;
            int mode = unix & 0xFFF;
            if (kind == 0xA000)
            {
                problems.Add($"{name} is a symbolic link; a release contains plain files and folders");
                continue;
            }

            if ((directory && kind is not (0 or 0x4000)) || (!directory && kind is not (0 or 0x8000)))
            {
                problems.Add($"{name} has a special Unix file type (0x{kind:x}); a release contains plain files and folders");
                continue;
            }

            string relative = trimmed.Length > MacApp.Length ? trimmed[(MacApp.Length + 1)..] : string.Empty;
            if (relative.Length == 0)
            {
                if (!directory)
                {
                    problems.Add($"{MacApp} is not a directory entry");
                }

                continue;
            }

            if (!directory && Forbidden(relative, macBundle: true) is { } forbidden)
            {
                problems.Add(forbidden);
            }

            if (string.Equals(Path.GetFileName(relative), ReleaseCheck.PersonalNote, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"holds {relative}: a personal build is never released");
            }

            bool program = relative is "Contents/MacOS/Tsukihime" or "Contents/MacOS/TsukihimeLauncher";
            bool executable = program || relative == "Contents/MacOS/createdump"
                || (relative.StartsWith("Contents/MacOS/", StringComparison.Ordinal) && relative.EndsWith(".dylib", StringComparison.Ordinal));
            int expected = directory || executable ? 0x1ED : 0x1A4;
            if (rules == ReleaseRuleSet.Update)
            {
                if (ModePolicyProblem(unix == 0 ? 0 : mode, directory, program && !directory) is { } why)
                {
                    problems.Add($"{name} has mode {(unix == 0 ? "(none)" : Convert.ToString(mode, 8))}: {why}");
                }
            }
            else if (unix == 0 || mode != expected)
            {
                problems.Add($"{name} has mode {(unix == 0 ? "(none)" : Convert.ToString(mode, 8))}, expected {Convert.ToString(expected, 8)} (package_macos.py and ditto preserve it)");
            }

            if (!directory)
            {
                if (UncompressedSizeProblem(entry.Length, total, name, limits) is { } size)
                {
                    problems.Add(size);
                    return entries;
                }

                total += entry.Length;
            }

            entries.Add(new MacZipEntry(entry, relative, directory, mode));
        }

        foreach (ZipArchiveEntry entry in metadata)
        {
            if (MetadataProblem(entry, real) is { } refused)
            {
                problems.Add($"the entry {entry.FullName} is macOS metadata, not a release file: {refused}");
            }
            else if (UncompressedSizeProblem(entry.Length, total, entry.FullName, limits) is { } size)
            {
                problems.Add(size);
                break;
            }
            else
            {
                total += entry.Length;
            }
        }

        return entries;
    }

    /// <summary>Why a __MACOSX or ._ entry is not ditto's metadata of a file or folder of the app (see <see cref="CheckMacZipEntries"/>), or null.</summary>
    private static string? MetadataProblem(ZipArchiveEntry entry, Dictionary<string, bool> real)
    {
        string name = entry.FullName;
        bool directory = name.EndsWith('/');
        string trimmed = directory ? name[..^1] : name;
        if (trimmed != MacMetadata && !trimmed.StartsWith(MacMetadata + "/", StringComparison.Ordinal))
        {
            return $"ditto keeps metadata under {MacMetadata}/ (ditto -c -k --sequesterRsrc), never beside the files";
        }

        int unix = (int)((uint)entry.ExternalAttributes >> 16);
        int kind = unix & 0xF000;
        string mirror = trimmed == MacMetadata ? string.Empty : trimmed[(MacMetadata.Length + 1)..];
        if (directory)
        {
            return kind is not (0 or 0x4000) || (unix & 0xE00) != 0 ? "a special folder"
                : mirror.Length == 0 || (real.TryGetValue(mirror, out bool folder) && folder) ? null
                : "it mirrors no folder of the app";
        }

        // the app's own entries are all inside Tsukihime.app and none has a ._ part, so the pair alone pins the place; __MACOSX/._Tsukihime.app holds the
        // attributes of the bundle folder itself (ditto writes it when the build host gave that folder one, com.apple.provenance say)
        int slash = mirror.LastIndexOf('/');
        string file = mirror[(slash + 1)..];
        string pair = slash < 0 ? (file.Length > 2 ? file[2..] : string.Empty) : mirror[..slash] + "/" + file[2..];
        if (!file.StartsWith("._", StringComparison.Ordinal) || (slash < 0 && pair != MacApp) || !real.ContainsKey(pair))
        {
            return $"only {MacMetadata}/{MacApp}/PATH/._NAME beside an entry {MacApp}/PATH/NAME (or {MacMetadata}/._{MacApp}) is accepted";
        }

        if (kind is not (0 or 0x8000) || (unix & 0xE00) != 0)
        {
            return "not a plain file";
        }

        if (entry.Length < 4 || entry.Length > MaxMetadataBytes)
        {
            return $"{entry.Length} bytes, not an AppleDouble file of at most {MaxMetadataBytes >> 20} MiB";
        }

        Span<byte> head = stackalloc byte[4];
        using (Stream data = entry.Open())
        {
            data.ReadExactly(head);
        }

        return head.SequenceEqual(new byte[] { 0x00, 0x05, 0x16, 0x07 }) ? null : "it does not start with the AppleDouble magic 00 05 16 07";
    }

    /// <summary>
    /// Unpacks a Mac release ZIP into <paramref name="destination"/>, emptied first, after <see cref="CheckMacZipEntries"/> passed (nothing is written otherwise),
    /// and returns destination/Tsukihime.app. <paramref name="unpack"/>, when given, does the unpacking instead of .NET (the macOS updater passes ditto -x -k, which
    /// gives the files their code-signature attributes back); what it unpacked must then be exactly the checked folders and files. Without it the metadata entries
    /// are skipped. <paramref name="rules"/> chooses the entry rules (<see cref="CheckMacZipEntries"/>).
    /// </summary>
    public static string UnpackMacZip(string zipPath, string destination, List<string> problems, ReleaseLimits? limits = null, Func<string, string, string?>? unpack = null, CancellationToken cancel = default,
        ReleaseRuleSet rules = ReleaseRuleSet.Complete)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(problems);
        string app = Path.Combine(destination, MacApp);
        if (!EmptyFolder(destination, problems))
        {
            return app;
        }

        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        IReadOnlyList<MacZipEntry> entries = CheckMacZipEntries(zip, problems, limits, rules);
        if (problems.Count > 0)
        {
            return app;
        }

        if (unpack is not null)
        {
            if (unpack(zipPath, destination) is { } failed)
            {
                problems.Add(failed);
            }
            else
            {
                problems.AddRange(UnpackedProblems(destination, entries));
            }

            return app;
        }

        Directory.CreateDirectory(app);
        foreach (MacZipEntry entry in entries)
        {
            cancel.ThrowIfCancellationRequested();
            string target = Path.Combine(app, entry.Relative.Replace('/', Path.DirectorySeparatorChar));
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using Stream input = entry.Entry.Open();
                if (CopyExactly(input, target, entry.Entry.Length, entry.Entry.FullName, cancel) is { } copy)
                {
                    problems.Add(copy);
                    return app;
                }
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, (UnixFileMode)(entry.Mode & 0x1FF));
            }
        }

        return app;
    }

    /// <summary>What another unpacker (ditto) put into <paramref name="destination"/> beyond, or short of, the checked <paramref name="entries"/>.</summary>
    private static IEnumerable<string> UnpackedProblems(string destination, IReadOnlyList<MacZipEntry> entries)
    {
        var expected = entries.ToDictionary(e => e.Relative, e => e.IsDirectory ? -1 : e.Entry.Length, StringComparer.Ordinal);
        foreach (string top in Directory.EnumerateFileSystemEntries(destination))
        {
            if (Path.GetFileName(top) != MacApp || !Directory.Exists(top))
            {
                yield return $"{Path.GetFileName(top)} was unpacked beside {MacApp}";
            }
        }

        string app = Path.Combine(destination, MacApp);
        foreach (string path in Directory.Exists(app) ? Directory.EnumerateFileSystemEntries(app, "*", SearchOption.AllDirectories) : [])
        {
            string relative = Path.GetRelativePath(app, path).Replace(Path.DirectorySeparatorChar, '/');
            var info = new FileInfo(path);
            bool directory = info.Attributes.HasFlag(FileAttributes.Directory);
            if (info.LinkTarget is not null)
            {
                yield return $"{relative} was unpacked as a link";
            }
            else if (!expected.Remove(relative, out long length))
            {
                yield return $"{relative} was unpacked but is not an entry of the ZIP";
            }
            else if (directory != (length < 0) || (!directory && info.Length != length))
            {
                yield return $"{relative} was unpacked differently from its entry";
            }
        }

        foreach (string missing in expected.Keys.Order(StringComparer.Ordinal))
        {
            yield return $"{missing} is missing after unpacking";
        }
    }

    /// <summary>Builds the ICNS form used by package_macos.py from the three crescent PNGs already in the repository.</summary>
    public static byte[] MakeMacIcns(ReadOnlySpan<byte> png16, ReadOnlySpan<byte> png32, ReadOnlySpan<byte> png256)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        "icns"u8.CopyTo(header);
        stream.Write(header);
        Add("icp4"u8, png16, 16);
        Add("icp5"u8, png32, 32);
        Add("ic08"u8, png256, 256);
        byte[] bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)bytes.Length);
        return bytes;

        void Add(ReadOnlySpan<byte> kind, ReadOnlySpan<byte> png, int size)
        {
            if (png.Length < 24 || !png[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
                || BinaryPrimitives.ReadUInt32BigEndian(png[16..]) != size || BinaryPrimitives.ReadUInt32BigEndian(png[20..]) != size)
            {
                throw new InvalidDataException($"the crescent's {size}px PNG is missing or has the wrong dimensions");
            }

            Span<byte> entry = stackalloc byte[8];
            kind.CopyTo(entry);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)png.Length + 8);
            stream.Write(entry);
            stream.Write(png);
        }
    }

    /// <summary>Why an ICNS is not exactly the packaging script's crescent icon, or null.</summary>
    public static string? MacIcnsProblem(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected)
    {
        if (expected.IsEmpty)
        {
            return $"{MacIcon} cannot be compared with the port's crescent PNGs";
        }

        return MacIcnsShapeProblem(data) ?? (data.SequenceEqual(expected) ? null : $"{MacIcon} is not the port's crescent (icp4, icp5 and ic08 PNG entries)");
    }

    private static string? MacIcnsShapeProblem(ReadOnlySpan<byte> data) =>
        data.Length < 8 || !data[..4].SequenceEqual("icns"u8) || BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != data.Length ? $"{MacIcon} is not a well-formed ICNS file" : null;

    /// <summary>
    /// Checks a Tsukihime.app folder. <paramref name="expectedArch"/> is arm64, x64, or null to accept either; <paramref name="expectedVersion"/>
    /// additionally ties the plist and managed assembly to a release ZIP's name when one is supplied. These are release-check's and release-sums' checks
    /// (<see cref="ReleaseRuleSet.Complete"/>); the updater holds a download to <see cref="CheckMacUpdate"/> only.
    /// </summary>
    public static void CheckMacFolder(string app, string? expectedArch, byte[]? expectedIcns, string? expectedVersion, List<string> problems, List<string> notes, bool requireCodeSignature = false)
    {
        ArgumentNullException.ThrowIfNull(problems);
        ArgumentNullException.ThrowIfNull(notes);
        string macos = Path.Combine(app, "Contents", "MacOS");
        string resources = Path.Combine(app, "Contents", "Resources");
        string? actualArch = null;
        foreach (string program in MacHosts)
        {
            string host = Path.Combine(macos, program);
            MachOFacts? hostFacts = File.Exists(host) ? MachOFacts.Read(File.ReadAllBytes(host)) : null;
            string? hostArch = Architecture(hostFacts);
            actualArch ??= hostArch;
            if (hostFacts is not { Type: 2 } || hostArch is null)
            {
                problems.Add($"Contents/MacOS/{program} is not a thin arm64 or x86-64 Mach-O executable");
            }
            else if (expectedArch is not null && hostArch != expectedArch)
            {
                problems.Add($"Contents/MacOS/{program} is {hostArch}, not the release's {expectedArch}");
            }
            else if (actualArch is not null && hostArch != actualArch)
            {
                problems.Add($"Contents/MacOS/{program} is {hostArch}, but the other apphost is {actualArch}");
            }
            else if (hostFacts.MinimumOSPlatform is { } hostPlatform && hostPlatform != 1)
            {
                problems.Add($"Contents/MacOS/{program} is built for Mach-O platform {hostPlatform}, not macOS (1)");
            }
            else if (hostFacts.MinimumOS is null || hostFacts.MinimumOS > MacMinimumOS)
            {
                problems.Add($"Contents/MacOS/{program} has minimum macOS {hostFacts.MinimumOS?.ToString(2) ?? "(not declared)"}, expected 15.0 or older");
            }
            else if (requireCodeSignature && !hostFacts.HasCodeSignature)
            {
                problems.Add($"Contents/MacOS/{program} has no code signature; a release ZIP must contain an ad-hoc signed app");
            }
            else if (requireCodeSignature && hostFacts.CodeSignatureIdentifier != MacSignatureIdentifier(program))
            {
                problems.Add($"Contents/MacOS/{program} has apphost signature identifier {hostFacts.CodeSignatureIdentifier ?? "(none)"}, expected {MacSignatureIdentifier(program)}; sign the whole app bundle (codesign --force --deep --prefix {MacSigningPrefix} -s -, tools/setup/package_macos.py)");
            }
            else
            {
                notes.Add($"Contents/MacOS/{program}: thin {hostArch} Mach-O executable");
            }
        }

        if (requireCodeSignature && !File.Exists(Path.Combine(app, "Contents", "_CodeSignature", "CodeResources")))
        {
            problems.Add("Contents/_CodeSignature/CodeResources is missing; a release ZIP must contain the signed app bundle, not only an SDK-signed apphost");
        }

        string arch = expectedArch ?? actualArch ?? "x64";
        foreach (string lib in MacLibraries)
        {
            string path = Path.Combine(macos, lib);
            if (!File.Exists(path))
            {
                problems.Add(MacLauncherLibraries.Contains(lib)
                    ? $"Contents/MacOS/{lib} is missing: {LauncherPublishHint}"
                    : $"Contents/MacOS/{lib} is missing: the Mac release bundles SDL and FFmpeg 9 (tools/setup/ffmpeg_macos.sh)");
                continue;
            }

            byte[] data = File.ReadAllBytes(path);
            MachOFacts? facts = MachOFacts.Read(data, arch);
            if (facts is not { Type: 6 } || Architecture(facts) != arch)
            {
                problems.Add($"Contents/MacOS/{lib} is not a thin {arch} Mach-O dylib");
                continue;
            }

            if (facts.MinimumOSPlatform is { } platform && platform != 1)
            {
                problems.Add($"Contents/MacOS/{lib} is built for Mach-O platform {platform}, not macOS (1)");
                continue;
            }

            if (facts.MinimumOS is null || facts.MinimumOS > MacMinimumOS)
            {
                problems.Add($"Contents/MacOS/{lib} has minimum macOS {facts.MinimumOS?.ToString(2) ?? "(not declared)"}, expected 15.0 or older");
                continue;
            }

            bool ffmpeg = MacFfmpeg.Contains(lib);
            bool officialRpath = !ffmpeg && (facts.InstallName == $"@rpath/{lib}" || (lib == "libSDL3.dylib" && facts.InstallName == "@rpath/libSDL3.0.dylib"));
            bool relocatable = facts.InstallName == $"@loader_path/{lib}" || officialRpath;
            string? localDependency = facts.Dependencies.FirstOrDefault(dependency =>
            {
                if (dependency.StartsWith("@loader_path/", StringComparison.Ordinal) || dependency.StartsWith("@rpath/", StringComparison.Ordinal))
                {
                    string name = dependency[(dependency.IndexOf('/') + 1)..];
                    return (ffmpeg && dependency.StartsWith("@rpath/", StringComparison.Ordinal)) || name.Contains('/', StringComparison.Ordinal) || !File.Exists(Path.Combine(macos, name));
                }

                return !dependency.StartsWith("/usr/lib/", StringComparison.Ordinal)
                    && !dependency.StartsWith("/System/Library/", StringComparison.Ordinal);
            });
            string? licence = ffmpeg ? FfmpegLicenceProblem(lib, data) : null;
            if (licence is not null)
            {
                problems.Add(licence);
            }
            else if (!relocatable)
            {
                problems.Add($"Contents/MacOS/{lib} has no relocatable install name: use @loader_path (or SDL's official @rpath)");
            }
            else if (localDependency is not null)
            {
                problems.Add($"Contents/MacOS/{lib} has a non-relocatable dependency: {localDependency}");
            }
            else
            {
                notes.Add($"{lib}: {(facts.Universal ? "universal" : "thin")} {arch} dylib{(ffmpeg ? ", LGPL build, @loader_path install name" : officialRpath ? ", official @rpath install name" : string.Empty)}");
            }
        }

        string[] runtime = [.. MacRuntime.Where(file => !File.Exists(Path.Combine(macos, file)))];
        if (runtime.Length > 0)
        {
            problems.Add($"{string.Join(", ", runtime)} not in Contents/MacOS: the Mac release must be self-contained (--self-contained true)");
        }
        else
        {
            notes.Add("the self-contained .NET runtime is inside Contents/MacOS");
        }

        CheckStartFiles(macos, problems, notes);

        foreach (string gpl in Directory.EnumerateFiles(app, "libpostproc*", SearchOption.AllDirectories))
        {
            problems.Add($"{Path.GetRelativePath(app, gpl)} is FFmpeg's GPL-only post-processing library: not an LGPL build");
        }

        foreach (string windows in Directory.EnumerateFiles(app, "*.exe", SearchOption.AllDirectories))
        {
            problems.Add($"{Path.GetRelativePath(app, windows)} is a Windows program in Tsukihime.app");
        }

        foreach (string licence in MacLicences)
        {
            if (!File.Exists(Path.Combine(resources, licence)))
            {
                problems.Add($"Contents/Resources/{licence} is missing from the Mac release");
            }
        }

        string source = Path.Combine(resources, "licenses", "FFmpeg-SOURCE.txt");
        if (File.Exists(source))
        {
            string text = File.ReadAllText(source);
            if (!text.Contains("https://git.ffmpeg.org/ffmpeg.git", StringComparison.Ordinal) || !text.Contains("Exact source commit: ", StringComparison.Ordinal)
                || !text.Contains("LGPL", StringComparison.Ordinal) || MacFfmpeg.Any(lib => !text.Contains(lib, StringComparison.Ordinal)))
            {
                problems.Add("Contents/Resources/licenses/FFmpeg-SOURCE.txt must name the official FFmpeg source, exact commit, LGPL and all four dylibs");
            }
            else
            {
                notes.Add("FFmpeg-SOURCE.txt names the exact Mac source revision and four dylibs");
            }
        }

        string icon = Path.Combine(app, MacIcon);
        if (!File.Exists(icon))
        {
            problems.Add($"{MacIcon} is missing: package_macos.py writes the crescent icon");
        }
        else if (MacIcnsProblem(File.ReadAllBytes(icon), expectedIcns ?? []) is { } iconProblem)
        {
            problems.Add(iconProblem);
        }
        else
        {
            notes.Add("Contents/Resources/Tsukihime.icns is the port's crescent");
        }

        CheckMacPlist(Path.Combine(app, "Contents", "Info.plist"), expectedVersion, problems, notes);
    }

    /// <summary>
    /// What the macOS updater holds an unpacked Tsukihime.app to (<see cref="ReleaseRuleSet.Update"/>): both programs thin Mach-O executables of
    /// <paramref name="arch"/> that carry a code signature, the bundle's signature (Contents/_CodeSignature/CodeResources), Info.plist naming the launcher as
    /// CFBundleExecutable, what both programs need to start (<see cref="CheckStartFiles"/>), and both assemblies of <paramref name="version"/> without a personal
    /// mark. No library, licence, notice, icon or signing identifier of this version: release-sums checked those, and codesign --verify checks the signature on a Mac.
    /// </summary>
    public static void CheckMacUpdate(string app, string arch, string version, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        string macos = Path.Combine(app, "Contents", "MacOS");
        foreach (string program in MacHosts)
        {
            string host = Path.Combine(macos, program);
            MachOFacts? facts = File.Exists(host) ? MachOFacts.Read(File.ReadAllBytes(host)) : null;
            string? hostArch = Architecture(facts);
            if (facts is not { Type: 2 } || hostArch is null)
            {
                problems.Add($"Contents/MacOS/{program} is not a thin arm64 or x86-64 Mach-O executable");
            }
            else if (hostArch != arch)
            {
                problems.Add($"Contents/MacOS/{program} is {hostArch}, not the release's {arch}");
            }
            else if (!facts.HasCodeSignature)
            {
                problems.Add($"Contents/MacOS/{program} has no code signature");
            }
        }

        if (!File.Exists(Path.Combine(app, "Contents", "_CodeSignature", "CodeResources")))
        {
            problems.Add("Contents/_CodeSignature/CodeResources is missing: the app bundle is not signed");
        }

        if (PlistValue(Path.Combine(app, "Contents", "Info.plist"), "CFBundleExecutable", out string? executable) is { } unreadable)
        {
            problems.Add(unreadable);
        }
        else if (executable != MacLauncher)
        {
            problems.Add($"Contents/Info.plist has CFBundleExecutable={executable ?? "(missing)"}, expected {MacLauncher}");
        }

        CheckStartFiles(macos, problems, []);
        CheckMacAssembly(app, version, null, problems);
        CheckMacAssembly(app, version, null, problems, MacLauncher);
    }

    /// <summary>Reads <paramref name="key"/>'s string from the XML property list <paramref name="path"/>; returns why it cannot be read, or null.</summary>
    private static string? PlistValue(string path, string key, out string? value)
    {
        value = null;
        if (!File.Exists(path))
        {
            return "Contents/Info.plist is missing from Tsukihime.app";
        }

        try
        {
            XElement? dictionary = XDocument.Load(path).Root?.Element("dict");
            value = (dictionary?.Elements("key").FirstOrDefault(e => e.Value == key)?.NextNode as XElement)?.Value;
            return null;
        }
        catch (Exception e) when (e is XmlException or IOException)
        {
            return $"Contents/Info.plist is not a readable property list ({e.Message})";
        }
    }

    /// <summary>Checks the managed assembly and version of an extracted app (<see cref="CheckReleaseAssembly"/> in Contents/MacOS).</summary>
    public static void CheckMacAssembly(string app, string version, IReadOnlyList<Tsuki.Data.Icons.IconFrame>? crescent, List<string> problems, string program = "Tsukihime") =>
        CheckReleaseAssembly(Path.Combine(app, "Contents", "MacOS"), program, version, crescent, problems);

    private static string? Architecture(MachOFacts? facts) => facts?.CpuType switch
    {
        0x0100000C => "arm64",
        0x01000007 => "x64",
        _ => null,
    };

    private static void CheckMacPlist(string path, string? expectedVersion, List<string> problems, List<string> notes)
    {
        if (!File.Exists(path))
        {
            problems.Add("Contents/Info.plist is missing from Tsukihime.app");
            return;
        }

        try
        {
            XElement? dictionary = XDocument.Load(path).Root?.Element("dict");
            string? Value(string key) => (dictionary?.Elements("key").FirstOrDefault(e => e.Value == key)?.NextNode as XElement)?.Value;
            var required = new Dictionary<string, string?>
            {
                ["CFBundleExecutable"] = MacLauncher,
                ["CFBundleIdentifier"] = MacBundleIdentifier,
                ["CFBundlePackageType"] = "APPL",
                ["CFBundleIconFile"] = "Tsukihime",
                ["LSMinimumSystemVersion"] = "15.0",
                ["CFBundleShortVersionString"] = expectedVersion,
                ["CFBundleVersion"] = expectedVersion,
            };
            int previousProblems = problems.Count;
            foreach ((string key, string? expected) in required)
            {
                string? value = Value(key);
                if (value is null || (expected is not null && value != expected))
                {
                    problems.Add($"Contents/Info.plist has {key}={value ?? "(missing)"}, expected {expected ?? "a value"}");
                }
            }

            if (problems.Count == previousProblems)
            {
                notes.Add("Contents/Info.plist names the app, bundle identifier and macOS 15 minimum");
            }
        }
        catch (Exception e) when (e is XmlException or IOException)
        {
            problems.Add($"Contents/Info.plist is not a readable property list ({e.Message})");
        }
    }
}

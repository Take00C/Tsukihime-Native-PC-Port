using System.Buffers.Binary;
using System.Text;

namespace Tsuki.Cli.Commands;

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
/// What a folder or archive to be released must and must not hold, beyond the personal-build rules of <see cref="ReleaseCheck"/> (package K46): no picture but the
/// README's logo and no game data on any platform, and for Linux the player, SDL, the four FFmpeg 9 libraries of an LGPL build and their licences.
/// </summary>
public static class ReleaseRules
{
    /// <summary>The Linux player's program (the apphost).</summary>
    public const string LinuxHost = "Tsukihime";

    /// <summary>The README's logo, the one picture a release carries (the owner's decision, K46; src/Tsuki.Distribution.targets publishes it).</summary>
    public const string Logo = "assets/branding/tsukihime-logo.png";

    /// <summary>The shared objects a Linux release must carry beside the player: SDL and the four FFmpeg libraries FfmpegLibrary.RequiredDlls names on Linux.</summary>
    public static IReadOnlyList<string> LinuxLibraries { get; } = ["libSDL3.so", "libavutil.so.61", "libswresample.so.7", "libavcodec.so.63", "libavformat.so.63"];

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
        "licenses/LICENSE.txt", "licenses/THIRD_PARTY.md", "licenses/dotnet-LICENSE.txt", "licenses/dotnet-THIRD-PARTY-NOTICES.txt", "LINUX.md",
    ];

    private static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tga", ".dds", ".ico", ".rgba", ".psd", ".svg"];

    private static readonly string[] GameDataExtensions = [".mrg", ".hed", ".nam", ".mzp", ".mzx", ".at9", ".vag", ".mp4", ".sfo", ".pkg", ".self", ".fself", ".prx", ".sprx", ".trp"];

    private static readonly string[] GameDataNames = ["eboot.bin", "save.dat", "save.dat.bak", "savedata000"];

    /// <summary>
    /// Why <paramref name="relativePath"/> (a path inside the folder or archive, '/' or '\' separated) must not be released, or null. Pictures other than
    /// <see cref="Logo"/> are refused wherever they are: the port's own crescent is compiled into the programs, so any other picture is art cut from a game folder.
    /// </summary>
    public static string? Forbidden(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string path = relativePath.Replace('\\', '/').TrimStart('/');
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

        return null;
    }

    /// <summary>The problems of every file under <paramref name="folder"/> that <see cref="Forbidden"/> refuses.</summary>
    public static IEnumerable<string> ForbiddenFiles(string folder)
    {
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (Forbidden(Path.GetRelativePath(folder, file)) is { } why)
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

    /// <summary>The checks of a Linux publish folder (the player <see cref="LinuxHost"/> beside Tsukihime.dll): its programs, libraries and licences.</summary>
    public static void CheckLinuxFolder(string folder, List<string> problems, List<string> notes)
    {
        ArgumentNullException.ThrowIfNull(problems);
        ArgumentNullException.ThrowIfNull(notes);
        string host = Path.Combine(folder, LinuxHost);
        ElfFacts? elf = File.Exists(host) ? ElfFacts.Read(File.ReadAllBytes(host)) : null;
        if (elf is not { IsX64: true, Type: 2 or 3 })
        {
            problems.Add($"{LinuxHost} is not an x86-64 ELF program");
        }
        else
        {
            notes.Add($"{LinuxHost}: x86-64 ELF program");
        }

        foreach (string lib in LinuxLibraries)
        {
            string path = Path.Combine(folder, lib);
            if (!File.Exists(path))
            {
                problems.Add($"{lib} is not beside {LinuxHost}: the Linux release bundles SDL and FFmpeg 9 (tools/setup/ffmpeg_linux.py, docs/PUBLISHING.md)");
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

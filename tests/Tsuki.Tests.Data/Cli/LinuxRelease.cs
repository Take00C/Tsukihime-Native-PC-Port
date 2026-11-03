using System.Formats.Tar;
using System.IO.Compression;
using Tsuki.Cli.Commands;

namespace Tsuki.Tests.Data.Cli;

/// <summary>
/// Package K46: a made-up Linux publish folder and its release archive, for release-check and release-sums. The ELF files are headers only (an x86-64 program and
/// shared objects; the FFmpeg ones carry FFmpeg's licence string and configure line), Tsukihime.dll is the one built next to these tests (this build's version, the
/// crescent), the licences are short texts. No game data and no real library.
/// </summary>
internal static class LinuxRelease
{
    /// <summary>The configure line of an LGPL build, as BtbN's libraries record it.</summary>
    public const string LgplConfiguration = "--prefix=/ffbuild/prefix --enable-version3 --disable-debug --enable-shared --disable-static";

    /// <summary>A 64-byte ELF64 header: x86-64, <paramref name="type"/> 2 (a program) or 3 (a shared object), followed by <paramref name="tail"/>.</summary>
    public static byte[] Elf(int type, string tail = "")
    {
        var data = new byte[64];
        data[0] = 0x7F;
        data[1] = (byte)'E';
        data[2] = (byte)'L';
        data[3] = (byte)'F';
        data[4] = 2;
        data[5] = 1;
        data[6] = 1;
        data[16] = (byte)type;
        data[18] = 62;
        return [.. data, .. System.Text.Encoding.ASCII.GetBytes(tail)];
    }

    /// <summary>The bytes of a fake FFmpeg library <paramref name="name"/> configured with <paramref name="configuration"/>.</summary>
    public static byte[] Ffmpeg(string name, string configuration = LgplConfiguration) =>
        Elf(3, $"\0{name.Split('.')[0]} license: LGPL version 3 or later\0{configuration}\0");

    /// <summary>Makes the folder: what a linux-x64 publish of the player holds that the checks look at.</summary>
    public static string Folder(string root, string name)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(folder, "licenses"));
        Directory.CreateDirectory(Path.Combine(folder, "docs"));
        File.WriteAllBytes(Path.Combine(folder, ReleaseRules.LinuxHost), Elf(2));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Tsukihime.dll"), Path.Combine(folder, "Tsukihime.dll"));
        File.WriteAllBytes(Path.Combine(folder, "libSDL3.so"), Elf(3));
        foreach (string lib in ReleaseRules.LinuxFfmpeg)
        {
            File.WriteAllBytes(Path.Combine(folder, lib), Ffmpeg(lib));
        }

        foreach (string runtime in ReleaseRules.LinuxRuntime)
        {
            File.WriteAllBytes(Path.Combine(folder, runtime), runtime.EndsWith(".so", StringComparison.Ordinal) ? Elf(3) : [0x4D, 0x5A]);
        }

        foreach (string licence in ReleaseRules.LinuxLicences)
        {
            File.WriteAllText(Path.Combine(folder, licence), "licence text");
        }

        File.WriteAllText(Path.Combine(folder, "licenses", "FFmpeg-SOURCE.txt"),
            "BtbN's \"linux64-lgpl-shared-9.0\" build: " + string.Join("  ", ReleaseRules.LinuxFfmpeg) + "\n");
        File.WriteAllText(Path.Combine(folder, "README.md"), "readme");
        File.WriteAllText(Path.Combine(folder, "docs", "SETUP_GUIDE.md"), "guide");
        return folder;
    }

    /// <summary>
    /// Archives <paramref name="folder"/> as <paramref name="tarPath"/> the way tools/setup/package_linux.py does: one folder named after the archive, 0755 for the
    /// player and the shared objects, 0644 for the rest. <paramref name="mode"/> may change a file's mode; <paramref name="extra"/> may add entries.
    /// </summary>
    public static string Tar(string folder, string tarPath, Func<string, UnixFileMode, UnixFileMode>? mode = null, Action<TarWriter, string>? extra = null)
    {
        string root = Path.GetFileName(tarPath)[..^".tar.gz".Length];
        Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);
        using (FileStream stream = File.Create(tarPath))
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, root + "/") { Mode = ReleaseSumsCommand.ExecutableMode });
            foreach (string dir in Directory.GetDirectories(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, root + "/" + Path.GetRelativePath(folder, dir).Replace('\\', '/') + "/") { Mode = ReleaseSumsCommand.ExecutableMode });
            }

            foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(folder, file).Replace('\\', '/');
                bool executable = rel == ReleaseRules.LinuxHost || rel.Contains(".so", StringComparison.Ordinal);
                UnixFileMode m = executable ? ReleaseSumsCommand.ExecutableMode : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                var entry = new PaxTarEntry(TarEntryType.RegularFile, root + "/" + rel)
                {
                    Mode = mode?.Invoke(rel, m) ?? m,
                    DataStream = new MemoryStream(File.ReadAllBytes(file)),
                };
                writer.WriteEntry(entry);
            }

            extra?.Invoke(writer, root);
        }

        return tarPath;
    }
}

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
        File.WriteAllBytes(Path.Combine(folder, "TsukihimeLauncher"), Elf(2));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Tsukihime.dll"), Path.Combine(folder, "Tsukihime.dll"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Tsukihime.dll"), Path.Combine(folder, "TsukihimeLauncher.dll"));
        foreach (string lib in ReleaseRules.LinuxLibraries.Where(lib => !ReleaseRules.LinuxFfmpeg.Contains(lib)))
        {
            File.WriteAllBytes(Path.Combine(folder, lib), Elf(3));
        }

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
        StartFiles(folder, "linux-x64", "libSkiaSharp.so");
        return folder;
    }

    /// <summary>
    /// Changes a block of pixels of the first frame in <paramref name="pe"/>'s icon group: the file stays a valid PE image (an assembly keeps its metadata) with
    /// an icon that is not the crescent, as a launcher-stamped or personal program has.
    /// </summary>
    public static void ChangeIcon(string pe)
    {
        byte[] bytes = File.ReadAllBytes(pe);
        byte[] group = Tsuki.Data.Icons.PeIcons.IconGroup(pe) ?? throw new InvalidOperationException(pe + " has no icon group");
        int imageOffset = BitConverter.ToInt32(group, 6 + 12);
        int imageLength = BitConverter.ToInt32(group, 6 + 8);
        int at = bytes.AsSpan().IndexOf(group.AsSpan(imageOffset, imageLength));
        if (at <= 0)
        {
            throw new InvalidOperationException("the first icon image was not found in " + pe);
        }

        for (int i = 40; i < 40 + 256; i++)
        {
            bytes[at + i] ^= 0x5A;
        }

        File.WriteAllBytes(pe, bytes);
    }

    /// <summary>
    /// What both programs need to start, the way a publish has it: their runtimeconfig.json and a deps.json that lists the program's assembly and, for the launcher,
    /// Avalonia.Base.dll (a stand-in written here) and <paramref name="launcherNative"/>.
    /// </summary>
    public static void StartFiles(string folder, string rid, string launcherNative)
    {
        File.WriteAllBytes(Path.Combine(folder, "Avalonia.Base.dll"), [0x4D, 0x5A]);
        foreach (string program in ReleaseRules.UnixPrograms)
        {
            File.WriteAllText(Path.Combine(folder, program + ".runtimeconfig.json"), "{\"runtimeOptions\":{\"tfm\":\"net10.0\"}}");
            string assets = "\"" + program + "/1.0.0\":{\"runtime\":{\"" + program + ".dll\":{}}}";
            if (program != ReleaseRules.LinuxHost)
            {
                assets += ",\"Avalonia/11.3.22\":{\"runtime\":{\"lib/net8.0/Avalonia.Base.dll\":{}}}"
                    + ",\"SkiaSharp.NativeAssets/2.88.9\":{\"native\":{\"runtimes/" + rid + "/native/" + launcherNative + "\":{}}}";
            }

            File.WriteAllText(Path.Combine(folder, program + ".deps.json"), "{\"targets\":{\".NETCoreApp,Version=v10.0\":{},\".NETCoreApp,Version=v10.0/" + rid + "\":{" + assets + "}}}");
        }
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
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, root + "/") { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute });
            foreach (string dir in Directory.GetDirectories(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, root + "/" + Path.GetRelativePath(folder, dir).Replace('\\', '/') + "/") { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute });
            }

            foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(folder, file).Replace('\\', '/');
                UnixFileMode m = ReleaseRules.LinuxMode(rel, directory: false);
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

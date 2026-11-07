using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Tsuki.Cli.Commands;
using Tsuki.Data.Icons;

namespace Tsuki.Tests.Data.Cli;

/// <summary>A synthetic Tsukihime.app: thin Mach-O headers, the real managed test assembly and the port's own crescent, never game data.</summary>
internal static class MacRelease
{
    public static string Version => PeIcons.Marks(Path.Combine(AppContext.BaseDirectory, "Tsukihime.dll"))!.InformationalVersion!.Split('+')[0];

    public static byte[] MachO(
        string arch,
        int type = 6,
        string tail = "",
        string? installName = null,
        string[]? dependencies = null,
        int minimumOS = 15,
        bool signed = true,
        string signatureIdentifier = ReleaseRules.MacBundleIdentifier,
        bool legacyMinimum = false,
        int sdk = 15,
        uint platform = 1)
    {
        var commands = new List<byte[]>();
        if (legacyMinimum)
        {
            commands.Add(LoadCommand(0x24, 16, command =>
            {
                BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(8), (uint)minimumOS << 16);
                BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(12), (uint)sdk << 16);
            }));
        }
        else
        {
            commands.Add(LoadCommand(0x32, 24, command =>
            {
                BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(8), platform);
                BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(12), (uint)minimumOS << 16);
            }));
        }

        if (installName is not null)
        {
            commands.Add(DylibCommand(0xD, installName));
        }

        foreach (string dependency in dependencies ?? [])
        {
            commands.Add(DylibCommand(0xC, dependency));
        }

        byte[] suffix = Encoding.ASCII.GetBytes(tail);
        byte[] signature = type == 2 && signed ? CodeSignature(signatureIdentifier) : [];
        int signatureOffset = 0;
        if (signature.Length > 0)
        {
            signatureOffset = 32 + commands.Sum(command => command.Length) + 16 + suffix.Length;
            commands.Add(LoadCommand(0x1D, 16, command =>
            {
                BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(8), (uint)signatureOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(12), (uint)signature.Length);
            }));
        }

        var data = new byte[32 + commands.Sum(command => command.Length) + suffix.Length + signature.Length];
        new byte[] { 0xCF, 0xFA, 0xED, 0xFE }.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), arch == "arm64" ? 0x0100000Cu : 0x01000007u);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), type);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)commands.Count);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20), commands.Sum(command => command.Length));
        int offset = 32;
        foreach (byte[] command in commands)
        {
            command.CopyTo(data, offset);
            offset += command.Length;
        }

        suffix.CopyTo(data, offset);
        signature.CopyTo(data, signatureOffset);
        return data;
    }

    private static byte[] DylibCommand(uint kind, string name)
    {
        byte[] text = Encoding.UTF8.GetBytes(name + "\0");
        byte[] data = LoadCommand(kind, (24 + text.Length + 7) & ~7, command =>
        {
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(8), 24);
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(16), 0x10000);
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(20), 0x10000);
        });
        text.CopyTo(data, 24);
        return data;
    }

    private static byte[] LoadCommand(uint kind, int size, Action<byte[]> fill)
    {
        var data = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(), kind);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), size);
        fill(data);
        return data;
    }

    private static byte[] CodeSignature(string identifier)
    {
        byte[] name = Encoding.UTF8.GetBytes(identifier + "\0");
        var data = new byte[20 + 24 + name.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(), 0xFADE0CC0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), 20);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), 0xFADE0C02);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), (uint)(24 + name.Length));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(40), 24);
        name.CopyTo(data, 44);
        return data;
    }

    public static byte[] Ffmpeg(string name, string configuration = "--disable-gpl --disable-nonfree --disable-version3 --enable-shared") =>
        MachO("arm64", 6, $"\0{name.Split('.')[0]} license: LGPL version 2.1 or later\0{configuration}\0", installName: $"@loader_path/{name}");

    public static byte[] Icns()
    {
        string icons = Path.Combine(RepoRoot(), "assets", "icon");
        return ReleaseRules.MakeMacIcns(
            File.ReadAllBytes(Path.Combine(icons, "tsukihime_16.png")),
            File.ReadAllBytes(Path.Combine(icons, "tsukihime_32.png")),
            File.ReadAllBytes(Path.Combine(icons, "tsukihime_256.png")));
    }

    public static string Folder(string root, string arch = "arm64", string name = "mac")
    {
        string app = Path.Combine(root, name, "Tsukihime.app");
        string macos = Path.Combine(app, "Contents", "MacOS");
        string resources = Path.Combine(app, "Contents", "Resources");
        Directory.CreateDirectory(macos);
        Directory.CreateDirectory(resources);
        File.WriteAllBytes(Path.Combine(macos, "Tsukihime"), MachO(arch, 2, signatureIdentifier: ReleaseRules.MacHostSignatureIdentifier));
        File.WriteAllBytes(Path.Combine(macos, "TsukihimeLauncher"), MachO(arch, 2));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Tsukihime.dll"), Path.Combine(macos, "Tsukihime.dll"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Tsukihime.dll"), Path.Combine(macos, "TsukihimeLauncher.dll"));
        foreach (string lib in ReleaseRules.MacLibraries.Where(lib => !ReleaseRules.MacFfmpeg.Contains(lib)))
        {
            string installName = lib == "libSDL3.dylib" ? "@rpath/libSDL3.0.dylib" : lib == "libAvaloniaNative.dylib" ? "@rpath/libAvaloniaNative.dylib" : $"@loader_path/{lib}";
            File.WriteAllBytes(Path.Combine(macos, lib), MachO(arch, 6, installName: installName));
        }

        foreach (string lib in ReleaseRules.MacFfmpeg)
        {
            byte[] data = Ffmpeg(lib);
            if (arch == "x64")
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x01000007u);
            }

            File.WriteAllBytes(Path.Combine(macos, lib), data);
        }

        foreach (string runtime in ReleaseRules.MacRuntime)
        {
            File.WriteAllBytes(Path.Combine(macos, runtime), runtime.EndsWith(".dylib", StringComparison.Ordinal) ? MachO(arch) : [0x4D, 0x5A]);
        }

        foreach (string licence in ReleaseRules.MacLicences)
        {
            string path = Path.Combine(resources, licence);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "licence text");
        }

        File.WriteAllText(Path.Combine(resources, "licenses", "FFmpeg-SOURCE.txt"),
            "Official source: https://git.ffmpeg.org/ffmpeg.git\nExact source commit: 0123456789abcdef0123456789abcdef01234567\n"
            + "LGPL 2.1 or later; @loader_path install names\n" + string.Join("  ", ReleaseRules.MacFfmpeg) + "\n");
        File.WriteAllBytes(Path.Combine(app, ReleaseRules.MacIcon), Icns());
        File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), Plist(Version));
        File.WriteAllText(Path.Combine(resources, "README.md"), "readme");
        Directory.CreateDirectory(Path.Combine(resources, "assets", "branding"));
        File.WriteAllBytes(Path.Combine(resources, "assets", "branding", "tsukihime-logo.png"), [0x89, 0x50, 0x4E, 0x47]);
        Directory.CreateDirectory(Path.Combine(app, "Contents", "_CodeSignature"));
        File.WriteAllText(Path.Combine(app, "Contents", "_CodeSignature", "CodeResources"), "bundle signature");
        LinuxRelease.StartFiles(macos, "osx-" + arch, "libAvaloniaNative.dylib");
        return app;
    }

    /// <summary>
    /// The metadata entries ditto -c -k --sequesterRsrc writes for the extended attributes of <paramref name="files"/> (paths inside Tsukihime.app): the __MACOSX
    /// folders that mirror theirs and one AppleDouble file each (<paramref name="head"/> bytes first, then padding), as codesign leaves the signatures of the
    /// non-Mach-O files in Contents/MacOS.
    /// </summary>
    public static void AddSequesteredMetadata(ZipArchive zip, IReadOnlyList<string> files, byte[]? head = null, int length = 82)
    {
        AddDirectory(zip, "__MACOSX/", 0x1ED);
        var folders = new SortedSet<string>(StringComparer.Ordinal) { "Tsukihime.app" };
        foreach (string file in files)
        {
            string[] parts = ("Tsukihime.app/" + file).Split('/');
            for (int i = 2; i < parts.Length; i++)
            {
                folders.Add(string.Join('/', parts[..i]));
            }
        }

        foreach (string folder in folders)
        {
            AddDirectory(zip, "__MACOSX/" + folder + "/", 0x1ED);
        }

        foreach (string file in files)
        {
            string path = "Tsukihime.app/" + file;
            int slash = path.LastIndexOf('/');
            ZipArchiveEntry entry = zip.CreateEntry("__MACOSX/" + path[..slash] + "/._" + path[(slash + 1)..]);
            entry.ExternalAttributes = Attributes(0x8000, 0x1A4);
            byte[] data = new byte[Math.Max(length, 4)];
            (head ?? [0x00, 0x05, 0x16, 0x07, 0x00, 0x02, 0x00, 0x00]).CopyTo(data, 0);
            using Stream stream = entry.Open();
            stream.Write(data);
        }
    }

    public static string Plist(string version) => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict>"
        + "<key>CFBundleExecutable</key><string>TsukihimeLauncher</string><key>CFBundleIdentifier</key><string>" + ReleaseRules.MacBundleIdentifier + "</string>"
        + "<key>CFBundlePackageType</key><string>APPL</string><key>CFBundleIconFile</key><string>Tsukihime</string>"
        + "<key>LSMinimumSystemVersion</key><string>15.0</string>"
        + $"<key>CFBundleShortVersionString</key><string>{version}</string><key>CFBundleVersion</key><string>{version}</string></dict></plist>";

    /// <summary>
    /// Zips <paramref name="app"/> the way ditto -c -k --keepParent does (0755 folders and programs, 0644 files). <paramref name="mode"/> may change a file's mode
    /// (its path inside the app), <paramref name="directoryMode"/> a folder's (its path with a trailing '/'), <paramref name="extra"/> add entries.
    /// </summary>
    public static string Zip(string app, string zipPath, Func<string, int, int>? mode = null, Action<ZipArchive>? extra = null, Func<string, int, int>? directoryMode = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        AddDirectory(zip, "Tsukihime.app/", 0x1ED);
        foreach (string dir in Directory.GetDirectories(app, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(app, dir).Replace('\\', '/') + "/";
            AddDirectory(zip, "Tsukihime.app/" + relative, directoryMode?.Invoke(relative, 0x1ED) ?? 0x1ED);
        }

        foreach (string file in Directory.GetFiles(app, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(app, file).Replace('\\', '/');
            bool executable = relative is "Contents/MacOS/Tsukihime" or "Contents/MacOS/TsukihimeLauncher" or "Contents/MacOS/createdump"
                || (relative.StartsWith("Contents/MacOS/", StringComparison.Ordinal) && relative.EndsWith(".dylib", StringComparison.Ordinal));
            int permissions = mode?.Invoke(relative, executable ? 0x1ED : 0x1A4) ?? (executable ? 0x1ED : 0x1A4);
            ZipArchiveEntry entry = zip.CreateEntry("Tsukihime.app/" + relative, CompressionLevel.Fastest);
            entry.ExternalAttributes = Attributes(0x8000, permissions);
            using Stream stream = entry.Open();
            stream.Write(File.ReadAllBytes(file));
        }

        extra?.Invoke(zip);
        return zipPath;
    }

    public static void AddDirectory(ZipArchive zip, string name, int permissions)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name);
        entry.ExternalAttributes = Attributes(0x4000, permissions);
    }

    public static int Attributes(int kind, int permissions) => unchecked((int)((uint)(kind | permissions) << 16));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Tsuki.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }
}

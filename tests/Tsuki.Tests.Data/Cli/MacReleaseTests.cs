using System.IO.Compression;
using Tsuki.Cli;
using Tsuki.Cli.Commands;
using Tsuki.Data.Icons;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Data.Cli;

/// <summary>K47: synthetic Mac app and ditto-layout ZIP validation, including updater isolation. No macOS host or game files are used.</summary>
public sealed class MacReleaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k47-mac-release-" + Guid.NewGuid().ToString("N"));
    private static byte[] Crescent => File.ReadAllBytes(Path.Combine(RepoRoot(), "assets", "icon", "tsukihime.ico"));

    public MacReleaseTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("arm64")]
    [InlineData("x64")]
    public void ACompleteMacAppAndItsZipPassOnEitherArchitecture(string arch)
    {
        string app = MacRelease.Folder(_root, arch, "valid-" + arch);
        ReleaseCheckResult folder = ReleaseCheck.Run(app, Crescent, crescentIcns: MacRelease.Icns());
        Assert.True(folder.Ok, string.Join("; ", folder.Problems));
        Assert.Contains($"Contents/MacOS/Tsukihime: thin {arch} Mach-O executable", folder.Notes);
        Assert.Contains("libavcodec.63.dylib: thin " + arch + " dylib, LGPL build, @loader_path install name", folder.Notes);
        Assert.Contains("Contents/Resources/Tsukihime.icns is the port's crescent", folder.Notes);
        MachOFacts host = Assert.IsType<MachOFacts>(MachOFacts.Read(File.ReadAllBytes(Path.Combine(app, "Contents", "MacOS", "Tsukihime"))));
        Assert.Equal(new Version(15, 0, 0), host.MinimumOS);
        Assert.True(host.HasCodeSignature);
        Assert.Equal(ReleaseRules.MacHostSignatureIdentifier, host.CodeSignatureIdentifier);
        MachOFacts launcher = Assert.IsType<MachOFacts>(MachOFacts.Read(File.ReadAllBytes(Path.Combine(app, "Contents", "MacOS", "TsukihimeLauncher"))));
        Assert.Equal(ReleaseRules.MacBundleIdentifier, launcher.CodeSignatureIdentifier);

        string zip = MacRelease.Zip(app, Path.Combine(_root, "valid-zip-" + arch, $"Tsukihime-v{MacRelease.Version}-macos-{arch}.zip"));
        Assert.Empty(ReleaseSumsCommand.CheckReleaseMacZip(zip, Crescent, MacRelease.Icns()));
    }

    [Fact]
    public void TheSigningIdentifiersAreTheOnesCodesignGivesWithPackageMacossPrefix()
    {
        // review mac-bundle-1: codesign --deep -s - alone signs the nested player as "Tsukihime-" plus its LC_UUID; with --prefix io.github.take00c.tsukihime. a
        // name without a dot gets the prefix (Apple's signer.cpp), and the launcher keeps its bundle's CFBundleIdentifier
        Assert.Equal("io.github.take00c.tsukihime", ReleaseRules.MacBundleIdentifier);
        Assert.Equal("io.github.take00c.tsukihime.", ReleaseRules.MacSigningPrefix);
        Assert.Equal("io.github.take00c.tsukihime.Tsukihime", ReleaseRules.MacHostSignatureIdentifier);
        Assert.Equal(ReleaseRules.MacHostSignatureIdentifier, ReleaseRules.MacSignatureIdentifier(ReleaseRules.MacHost));
        Assert.Equal(ReleaseRules.MacBundleIdentifier, ReleaseRules.MacSignatureIdentifier(ReleaseRules.MacLauncher));
        foreach (string old in new[] { "Tsukihime", "Tsukihime-55554944a1b2c3d4e5f60718293a4b5c" })
        {
            string app = MacRelease.Folder(_root, name: "identifier-" + old.Length);
            File.WriteAllBytes(Path.Combine(app, "Contents", "MacOS", "Tsukihime"), MacRelease.MachO("arm64", 2, signatureIdentifier: old));
            string zip = MacRelease.Zip(app, Path.Combine(_root, "identifier-zip-" + old.Length, $"Tsukihime-v{MacRelease.Version}-macos-arm64.zip"));
            Assert.Contains(ReleaseSumsCommand.CheckReleaseMacZip(zip, Crescent, MacRelease.Icns()),
                problem => problem.Contains($"has apphost signature identifier {old}, expected io.github.take00c.tsukihime.Tsukihime", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheOfficialSdlRpathIsAcceptedAsRelocatable()
    {
        string app = MacRelease.Folder(_root, name: "official-sdl-rpath");
        string sdl = Path.Combine(app, "Contents", "MacOS", "libSDL3.dylib");
        File.WriteAllBytes(sdl, MacRelease.MachO("arm64", installName: "@rpath/libSDL3.0.dylib"));
        ReleaseCheckResult result = ReleaseCheck.Run(app, Crescent, crescentIcns: MacRelease.Icns());
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        Assert.Contains("libSDL3.dylib: thin arm64 dylib, official @rpath install name", result.Notes);
    }

    [Fact]
    public void VersionMinMacOsxReadsTheDeploymentVersionNotTheSdk()
    {
        MachOFacts facts = Assert.IsType<MachOFacts>(MachOFacts.Read(MacRelease.MachO("arm64", 2, minimumOS: 12, legacyMinimum: true, sdk: 15)));
        Assert.Equal(new Version(12, 0, 0), facts.MinimumOS);
    }

    [Theory]
    [InlineData("gpl", "was built with --enable-gpl or --enable-nonfree")]
    [InlineData("no loader", "has no relocatable install name")]
    [InlineData("fake loader", "has no relocatable install name")]
    [InlineData("local dependency", "has a non-relocatable dependency")]
    [InlineData("framework", "the Mac release must be self-contained")]
    [InlineData("minimum", "minimum macOS 16.0, expected 15.0 or older")]
    [InlineData("library minimum", "libSDL3.dylib has minimum macOS 16.0, expected 15.0 or older")]
    [InlineData("loader dependency", "has a non-relocatable dependency")]
    [InlineData("platform", "Contents/MacOS/Tsukihime is built for Mach-O platform 2, not macOS (1)")]
    [InlineData("launcher platform", "Contents/MacOS/TsukihimeLauncher is built for Mach-O platform 2, not macOS (1)")]
    [InlineData("licence", "is missing from the Mac release")]
    [InlineData("postproc", "GPL-only post-processing library")]
    [InlineData("personal", "is there: the folder is a personal build")]
    [InlineData("notice", "must name the official FFmpeg source, exact commit")]
    [InlineData("icon", "is not the port's crescent")]
    [InlineData("plist", "CFBundleIdentifier=wrong")]
    [InlineData("picture", "is a picture")]
    [InlineData("game", "is game data or a save")]
    [InlineData("windows", "is a Windows program in Tsukihime.app")]
    [InlineData("launcher icon", "TsukihimeLauncher.dll carries an icon that is not the port's crescent")]
    [InlineData("player icon", "Tsukihime.dll carries an icon that is not the port's crescent")]
    [InlineData("runtimeconfig", "Tsukihime.runtimeconfig.json is missing: the player cannot start without it")]
    [InlineData("deps", "TsukihimeLauncher.deps.json is missing: the launcher cannot start without it")]
    [InlineData("listed", "Avalonia.Base.dll, listed in TsukihimeLauncher.deps.json, not beside TsukihimeLauncher: the launcher would not start")]
    [InlineData("unreadable deps", "Tsukihime.deps.json is not a readable dependency file")]
    [InlineData("developer", "Tsuki.DevBridge.dll is a developer program")]
    [InlineData("skia", "Contents/MacOS/libSkiaSharp.dylib is missing: it comes with the launcher; publish src/Tsuki.Launcher.Avalonia")]
    [InlineData("sdl", "Contents/MacOS/libSDL3.dylib is missing: the Mac release bundles SDL and FFmpeg 9")]
    public void AnIncompleteOrForbiddenMacAppIsRefused(string what, string expected)
    {
        string app = MacRelease.Folder(_root, name: "bad-" + what.Replace(' ', '-'));
        string macos = Path.Combine(app, "Contents", "MacOS");
        switch (what)
        {
            case "gpl":
                File.WriteAllBytes(Path.Combine(macos, "libavcodec.63.dylib"), MacRelease.Ffmpeg("libavcodec.63.dylib", "--enable-gpl"));
                break;
            case "no loader":
                File.WriteAllBytes(Path.Combine(macos, "libSDL3.dylib"), MacRelease.MachO("arm64"));
                break;
            case "fake loader":
                File.WriteAllBytes(Path.Combine(macos, "libSDL3.dylib"), MacRelease.MachO("arm64", tail: "\0@rpath/libSDL3.0.dylib\0"));
                break;
            case "local dependency":
                File.WriteAllBytes(Path.Combine(macos, "libavutil.61.dylib"), MacRelease.MachO(
                    "arm64",
                    6,
                    "\0libavutil license: LGPL version 2.1 or later\0--disable-gpl --disable-nonfree\0",
                    installName: "@loader_path/libavutil.61.dylib",
                    dependencies: ["/Users/builder/ffmpeg/lib/libavcodec.63.dylib"]));
                break;
            case "framework":
                File.Delete(Path.Combine(macos, "libcoreclr.dylib"));
                break;
            case "minimum":
                File.WriteAllBytes(Path.Combine(macos, "Tsukihime"), MacRelease.MachO("arm64", 2, minimumOS: 16));
                break;
            case "library minimum":
                File.WriteAllBytes(Path.Combine(macos, "libSDL3.dylib"), MacRelease.MachO("arm64", 6, installName: "@loader_path/libSDL3.dylib", minimumOS: 16));
                break;
            case "loader dependency":
                File.WriteAllBytes(Path.Combine(macos, "libSDL3.dylib"), MacRelease.MachO("arm64", 6, installName: "@loader_path/libSDL3.dylib", dependencies: ["@loader_path/../../../evil.dylib", "@loader_path/missing.dylib"]));
                break;
            case "platform":
            case "launcher platform":
                File.WriteAllBytes(Path.Combine(macos, what == "platform" ? "Tsukihime" : "TsukihimeLauncher"), MacRelease.MachO("arm64", 2, platform: 2));
                break;
            case "licence":
                File.Delete(Path.Combine(app, "Contents", "Resources", ReleaseRules.MacLicences[0]));
                break;
            case "postproc":
                File.WriteAllBytes(Path.Combine(macos, "libpostproc.59.dylib"), [1]);
                break;
            case "personal":
                File.WriteAllText(Path.Combine(app, ReleaseCheck.PersonalNote), "personal");
                break;
            case "notice":
                File.WriteAllText(Path.Combine(app, "Contents", "Resources", "licenses", "FFmpeg-SOURCE.txt"), "unknown build");
                break;
            case "icon":
                File.WriteAllBytes(Path.Combine(app, ReleaseRules.MacIcon), [0x69, 0x63, 0x6E, 0x73, 0, 0, 0, 8]);
                break;
            case "plist":
                File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), MacRelease.Plist(MacRelease.Version).Replace("io.github.take00c.tsukihime", "wrong", StringComparison.Ordinal));
                break;
            case "picture":
                Directory.CreateDirectory(Path.Combine(app, "Contents", "Resources", "docs"));
                File.WriteAllBytes(Path.Combine(app, "Contents", "Resources", "docs", "screenshot.jpg"), [1]);
                break;
            case "game":
                File.WriteAllBytes(Path.Combine(macos, "eboot.bin"), [1]);
                break;
            case "launcher icon":
            case "player icon":
                LinuxRelease.ChangeIcon(Path.Combine(macos, what == "launcher icon" ? "TsukihimeLauncher.dll" : "Tsukihime.dll"));
                break;
            case "runtimeconfig":
                File.Delete(Path.Combine(macos, "Tsukihime.runtimeconfig.json"));
                break;
            case "deps":
                File.Delete(Path.Combine(macos, "TsukihimeLauncher.deps.json"));
                break;
            case "listed":
                File.Delete(Path.Combine(macos, "Avalonia.Base.dll"));
                break;
            case "unreadable deps":
                File.WriteAllText(Path.Combine(macos, "Tsukihime.deps.json"), "{ not json");
                break;
            case "developer":
                File.WriteAllBytes(Path.Combine(macos, "Tsuki.DevBridge.dll"), [0x4D, 0x5A]);
                break;
            case "skia":
                File.Delete(Path.Combine(macos, "libSkiaSharp.dylib"));
                break;
            case "sdl":
                File.Delete(Path.Combine(macos, "libSDL3.dylib"));
                break;
            default:
                File.WriteAllBytes(Path.Combine(macos, "createdump.exe"), [0x4D, 0x5A]);
                break;
        }

        ReleaseCheckResult result = ReleaseCheck.Run(app, Crescent, crescentIcns: MacRelease.Icns());
        Assert.False(result.Ok);
        Assert.Contains(result.Problems, problem => problem.Contains(expected, StringComparison.Ordinal));
        if (what == "plist") { Assert.DoesNotContain(result.Notes, note => note.StartsWith("Contents/Info.plist", StringComparison.Ordinal)); }
        if (what is "launcher icon" or "player icon" or "listed" or "developer")
        {
            // the ZIP of such an app gets no checksum either
            string zip = MacRelease.Zip(app, Path.Combine(_root, "bad-zip-of-" + what.Replace(' ', '-'), $"Tsukihime-v{MacRelease.Version}-macos-arm64.zip"));
            Assert.Contains(ReleaseSumsCommand.CheckReleaseMacZip(zip, Crescent, MacRelease.Icns()), problem => problem.Contains(expected, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("name", "a Mac release ZIP must be named")]
    [InlineData("mode", "has mode 644, expected 755")]
    [InlineData("outside", "is outside the one Tsukihime.app root")]
    [InlineData("traversal", "would land outside Tsukihime.app")]
    [InlineData("link", "is a symbolic link")]
    [InlineData("architecture", "is x64, not the release's arm64")]
    [InlineData("version", "the release says 9.9.9")]
    [InlineData("unsigned", "has no code signature")]
    [InlineData("sdk signed", "apphost signature identifier apphost")]
    [InlineData("code resources", "CodeResources is missing")]
    [InlineData("duplicate", "occurs more than once")]
    [InlineData("fifo", "special Unix file type")]
    [InlineData("metadata", "macOS metadata")]
    [InlineData("unpaired metadata", "is macOS metadata, not a release file: only __MACOSX/Tsukihime.app/PATH/._NAME beside an entry")]
    [InlineData("metadata magic", "it does not start with the AppleDouble magic")]
    [InlineData("setuid", "has mode 4755, expected 755")]
    [InlineData("writable", "Tsukihime.app/Contents/Resources/README.md has mode 664, expected 644")]
    [InlineData("folder mode", "Tsukihime.app/Contents/MacOS/ has mode 700, expected 755")]
    [InlineData("control", "NUL or control character")]
    [InlineData("personal", "holds Contents/MacOS/PERSONAL_BUILD.txt: a personal build is never released")]
    [InlineData("picture", "Contents/MacOS/x.png is a picture")]
    public void AnUnsafeOrMismatchedMacZipIsRefused(string what, string expected)
    {
        string arch = what == "architecture" ? "x64" : "arm64";
        string app = MacRelease.Folder(_root, arch, "zip-" + what);
        if (what == "unsigned")
        {
            File.WriteAllBytes(Path.Combine(app, "Contents", "MacOS", "Tsukihime"), MacRelease.MachO(arch, 2, signed: false));
        }
        else if (what == "sdk signed")
        {
            File.WriteAllBytes(Path.Combine(app, "Contents", "MacOS", "Tsukihime"), MacRelease.MachO(arch, 2, signatureIdentifier: "apphost"));
            File.Delete(Path.Combine(app, "Contents", "_CodeSignature", "CodeResources"));
        }
        else if (what == "code resources")
        {
            File.Delete(Path.Combine(app, "Contents", "_CodeSignature", "CodeResources"));
        }

        string version = what == "version" ? "9.9.9" : MacRelease.Version;
        string zipArch = what == "architecture" ? "arm64" : arch;
        string suffix = what == "name" ? "macos.zip" : $"macos-{zipArch}.zip";
        string path = Path.Combine(_root, "bad-zip-" + what, $"Tsukihime-v{version}-{suffix}");
        string escaped = Path.Combine(Path.GetTempPath(), "tsuki-k47-evil-" + Guid.NewGuid().ToString("N") + ".txt");
        if (what == "personal")
        {
            File.WriteAllText(Path.Combine(app, "Contents", "MacOS", ReleaseCheck.PersonalNote), "personal");
        }
        else if (what == "picture")
        {
            File.WriteAllBytes(Path.Combine(app, "Contents", "MacOS", "x.png"), [1]);
        }

        Func<string, int, int>? mode = what switch
        {
            "mode" => (relative, current) => relative == "Contents/MacOS/Tsukihime" ? 0x1A4 : current,
            "setuid" => (relative, current) => relative == "Contents/MacOS/Tsukihime" ? current | 0x800 : current,
            "writable" => (relative, current) => relative == "Contents/Resources/README.md" ? 0x1B4 : current,
            "folder mode" => (relative, current) => relative == "Contents/MacOS/" ? 0x1C0 : current,
            _ => null,
        };
        Action<ZipArchive>? extra = what switch
        {
            "outside" => zip => AddFile(zip, "elsewhere/README.md"),
            "traversal" => zip => AddFile(zip, "Tsukihime.app/../../" + Path.GetFileName(escaped)),
            "duplicate" => zip => AddFile(zip, "Tsukihime.app/Contents/Resources/readme.md"),
            "metadata" => zip => AddFile(zip, "__MACOSX/Tsukihime.app/._Contents"),
            "unpaired metadata" => zip => MacRelease.AddSequesteredMetadata(zip, ["Contents/MacOS/missing.dll"]),
            "metadata magic" => zip => MacRelease.AddSequesteredMetadata(zip, ["Contents/MacOS/Tsukihime.dll"], head: [0x00, 0x05, 0x16, 0x08]),
            "control" => zip => AddFile(zip, "Tsukihime.app/Contents/Resources/evil\0.txt"),
            "fifo" or "link" => zip =>
            {
                ZipArchiveEntry entry = zip.CreateEntry("Tsukihime.app/Contents/MacOS/libevil.dylib");
                entry.ExternalAttributes = MacRelease.Attributes(what == "fifo" ? 0x1000 : 0xA000, 0x1ED);
            },
            _ => null,
        };
        try
        {
            string zipPath = MacRelease.Zip(app, path, mode, extra, directoryMode: mode);
            IReadOnlyList<string> problems = ReleaseSumsCommand.CheckReleaseMacZip(zipPath, Crescent, MacRelease.Icns());
            Assert.Contains(problems, problem => problem.Contains(expected, StringComparison.Ordinal));
            if (what == "traversal")
            {
                Assert.False(File.Exists(escaped), "the traversal entry escaped the extraction folder");
            }
        }
        finally
        {
            if (what == "traversal")
            {
                File.Delete(escaped);
            }
        }
    }

    [Fact]
    public void DittosSequesteredSignatureMetadataIsAcceptedAndAZipWithoutItToo()
    {
        // ditto -c -k --sequesterRsrc keeps the code-signature attributes codesign --deep put on the .dll and .json files of Contents/MacOS (review K48)
        string app = MacRelease.Folder(_root, name: "sequestered");
        string[] signed = [.. Directory.GetFiles(Path.Combine(app, "Contents", "MacOS"))
            .Where(f => f.EndsWith(".dll", StringComparison.Ordinal) || f.EndsWith(".json", StringComparison.Ordinal))
            .Select(f => "Contents/MacOS/" + Path.GetFileName(f)), "Contents/MacOS", "Contents"];
        string zip = MacRelease.Zip(app, Path.Combine(_root, "sequestered-zip", $"Tsukihime-v{MacRelease.Version}-macos-arm64.zip"), extra: z => MacRelease.AddSequesteredMetadata(z, signed));
        using (ZipArchive archive = ZipFile.OpenRead(zip))
        {
            Assert.Contains(archive.Entries, entry => entry.FullName == "__MACOSX/Tsukihime.app/Contents/MacOS/._TsukihimeLauncher.dll");
            Assert.Contains(archive.Entries, entry => entry.FullName == "__MACOSX/Tsukihime.app/Contents/");
        }

        // review crit-4: ditto writes __MACOSX/._Tsukihime.app when the bundle folder itself has an attribute (com.apple.provenance of the build host, say)
        string withBundleAttributes = MacRelease.Zip(app, Path.Combine(_root, "bundle-attributes-zip", $"Tsukihime-v{MacRelease.Version}-macos-arm64.zip"), extra: z =>
        {
            MacRelease.AddSequesteredMetadata(z, signed);
            ZipArchiveEntry pair = z.CreateEntry("__MACOSX/._Tsukihime.app");
            pair.ExternalAttributes = MacRelease.Attributes(0x8000, 0x1A4);
            using Stream data = pair.Open();
            data.Write([0x00, 0x05, 0x16, 0x07, 0x00, 0x02, 0x00, 0x00, 0, 0, 0, 0]);
        });
        Assert.Empty(ReleaseSumsCommand.CheckReleaseMacZip(withBundleAttributes, Crescent, MacRelease.Icns()));

        Assert.Empty(ReleaseSumsCommand.CheckReleaseMacZip(zip, Crescent, MacRelease.Icns()));
        string withoutMetadata = MacRelease.Zip(app, Path.Combine(_root, "plain-zip", $"Tsukihime-v{MacRelease.Version}-macos-arm64.zip"));
        Assert.Empty(ReleaseSumsCommand.CheckReleaseMacZip(withoutMetadata, Crescent, MacRelease.Icns()));
    }

    [Fact]
    public void AMacZipIsHeldToTheReleaseSizeAndEntryLimits()
    {
        // review K48 deck-mac-runtime-3: the Mac ZIP's caps are the shared rule's
        string zip = MacRelease.Zip(MacRelease.Folder(_root, name: "limits"), Path.Combine(_root, "limits-zip", $"Tsukihime-v{MacRelease.Version}-macos-arm64.zip"));
        long largest;
        long sum;
        int count;
        using (ZipArchive archive = ZipFile.OpenRead(zip))
        {
            largest = archive.Entries.Max(e => e.Length);
            sum = archive.Entries.Sum(e => e.Length);
            count = archive.Entries.Count;
        }

        Assert.Empty(ReleaseSumsCommand.CheckReleaseMacZip(zip, Crescent, MacRelease.Icns(), new ReleaseLimits(largest, sum, count)));
        Assert.Contains(ReleaseSumsCommand.CheckReleaseMacZip(zip, Crescent, MacRelease.Icns(), new ReleaseLimits(largest - 1, sum, count)), p => p.Contains("exceeds the", StringComparison.Ordinal));
        Assert.Contains(ReleaseSumsCommand.CheckReleaseMacZip(zip, Crescent, MacRelease.Icns(), new ReleaseLimits(largest, sum - 1, count)), p => p.Contains("exceeds the", StringComparison.Ordinal));
        Assert.Contains(ReleaseSumsCommand.CheckReleaseMacZip(zip, Crescent, MacRelease.Icns(), new ReleaseLimits(largest, sum, count - 1)), p => p.Contains($"more than {count - 1} entries", StringComparison.Ordinal));
    }

    [Fact]
    public void BothMacAssetsGetChecksumsWithoutBecomingWindowsUpdaterAssets()
    {
        string arm = MacRelease.Zip(MacRelease.Folder(_root, "arm64", "sums-arm"), Path.Combine(_root, "sums", $"Tsukihime-v{MacRelease.Version}-macos-arm64.zip"));
        string x64 = MacRelease.Zip(MacRelease.Folder(_root, "x64", "sums-x64"), Path.Combine(_root, "sums", $"Tsukihime-v{MacRelease.Version}-macos-x64.zip"));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(ExitCode.Ok, CliApp.Run(["release-sums", arm, x64, "--icon", Path.Combine(RepoRoot(), "assets", "icon", "tsukihime.ico")], output, error));
        string text = File.ReadAllText(Path.Combine(_root, "sums", "SHA256SUMS.txt"));
        IReadOnlyDictionary<string, string> sums = ReleaseSums.Parse(text);
        Assert.Equal(2, sums.Count);
        Assert.True(sums.ContainsKey(Path.GetFileName(arm)) && sums.ContainsKey(Path.GetFileName(x64)));
        Assert.True(ReleaseVersion.TryParse(MacRelease.Version, out ReleaseVersion version));
        string windowsName = UpdateSource.ZipAssetName(version);
        Assert.Equal($"Tsukihime-v{MacRelease.Version}-win-x64.zip", windowsName);
        Assert.Equal(SumCheck.Missing, ReleaseSums.Check(text, windowsName, ReleaseSums.Sha256Hex(arm)));
        Assert.Equal(SumCheck.Match, ReleaseSums.Check(text, Path.GetFileName(arm), ReleaseSums.Sha256Hex(arm)));
        Assert.Equal(SumCheck.Match, ReleaseSums.Check(text, Path.GetFileName(x64), ReleaseSums.Sha256Hex(x64)));
    }

    private static void AddFile(ZipArchive zip, string name)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name);
        entry.ExternalAttributes = MacRelease.Attributes(0x8000, 0x1A4);
        using Stream stream = entry.Open();
        stream.WriteByte(1);
    }

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

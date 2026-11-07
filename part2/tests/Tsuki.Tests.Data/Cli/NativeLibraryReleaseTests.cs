using Tsuki.Cli.Commands;

namespace Tsuki.Tests.Data.Cli;

public sealed class NativeLibraryReleaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k48-native-libraries-" + Guid.NewGuid().ToString("N"));

    public NativeLibraryReleaseTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("libSkiaSharp.so")]
    [InlineData("libHarfBuzzSharp.so")]
    public void LinuxRefusesAMissingLauncherNativeLibrary(string library)
    {
        string folder = LinuxRelease.Folder(_root, "missing-" + library);
        File.Delete(Path.Combine(folder, library));
        Assert.Contains(LinuxProblems(folder), problem => problem.StartsWith($"{library} is not beside Tsukihime", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("libSkiaSharp.so", true)]
    [InlineData("libHarfBuzzSharp.so", true)]
    [InlineData("libSDL3.so", false)]
    [InlineData("libavcodec.so.63", false)]
    public void AMissingLibraryNamesThePublishStepThatBringsIt(string library, bool launcher)
    {
        // review K48: Skia and HarfBuzz come with the launcher's publish, SDL and FFmpeg with the player's
        string folder = LinuxRelease.Folder(_root, "hint-" + library);
        File.Delete(Path.Combine(folder, library));
        string problem = Assert.Single(LinuxProblems(folder), p => p.StartsWith(library + " is not beside", StringComparison.Ordinal));
        Assert.Equal(launcher, problem.Contains("publish src/Tsuki.Launcher.Avalonia, not src/Tsuki.App", StringComparison.Ordinal));
        Assert.Equal(!launcher, problem.Contains("tools/setup/ffmpeg_linux.py", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("libAvaloniaNative.dylib", true)]
    [InlineData("libSkiaSharp.dylib", true)]
    [InlineData("libHarfBuzzSharp.dylib", true)]
    [InlineData("libSDL3.dylib", false)]
    public void AMissingMacLibraryNamesThePublishStepThatBringsIt(string library, bool launcher)
    {
        string app = MacRelease.Folder(_root, name: "hint-" + library);
        File.Delete(Path.Combine(app, "Contents", "MacOS", library));
        string problem = Assert.Single(MacProblems(app), p => p.StartsWith($"Contents/MacOS/{library} is missing", StringComparison.Ordinal));
        Assert.Equal(launcher, problem.Contains("publish src/Tsuki.Launcher.Avalonia, not src/Tsuki.App", StringComparison.Ordinal));
        Assert.Equal(!launcher, problem.Contains("tools/setup/ffmpeg_macos.sh", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("TsukihimeLauncher")]
    [InlineData("Tsukihime")]
    public void BothLinuxProgramsMustBeX64ElfPrograms(string program)
    {
        // review K48 tests-docs-06: a Windows program, a script or a truncated file in the launcher's place
        string folder = LinuxRelease.Folder(_root, "host-" + program);
        File.WriteAllBytes(Path.Combine(folder, program), [0x4D, 0x5A]);
        Assert.Equal([$"{program} is not an x86-64 ELF program"], LinuxProblems(folder));
    }

    [Theory]
    [InlineData("libSkiaSharp.so")]
    [InlineData("libHarfBuzzSharp.so")]
    public void LinuxLauncherLibrariesMustBeX64SharedObjects(string library)
    {
        string folder = LinuxRelease.Folder(_root, "type-" + library);
        File.WriteAllBytes(Path.Combine(folder, library), LinuxRelease.Elf(2));
        Assert.Contains($"{library} is not an x86-64 ELF shared object", LinuxProblems(folder));
    }

    [Theory]
    [InlineData("libAvaloniaNative.dylib")]
    [InlineData("libSkiaSharp.dylib")]
    [InlineData("libHarfBuzzSharp.dylib")]
    public void MacRefusesAMissingLauncherNativeLibrary(string library)
    {
        string app = MacRelease.Folder(_root, name: "missing-" + library);
        File.Delete(Path.Combine(app, "Contents", "MacOS", library));
        Assert.Contains(MacProblems(app), problem => problem.StartsWith($"Contents/MacOS/{library} is missing", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("type", 2, "arm64", "@rpath/libAvaloniaNative.dylib", 1, "is not a thin arm64 Mach-O dylib")]
    [InlineData("architecture", 6, "x64", "@rpath/libAvaloniaNative.dylib", 1, "is not a thin arm64 Mach-O dylib")]
    [InlineData("absolute", 6, "arm64", "/opt/local/lib/libAvaloniaNative.dylib", 1, "has no relocatable install name")]
    [InlineData("platform", 6, "arm64", "@rpath/libAvaloniaNative.dylib", 2, "is built for Mach-O platform 2, not macOS (1)")]
    public void MacLauncherLibrariesMustHaveTheCorrectBinaryFacts(string name, int type, string arch, string installName, uint platform, string expected)
    {
        string app = MacRelease.Folder(_root, name: name);
        File.WriteAllBytes(Path.Combine(app, "Contents", "MacOS", "libAvaloniaNative.dylib"), MacRelease.MachO(arch, type, installName: installName, platform: platform));
        Assert.Contains(expected, Assert.Single(MacProblems(app)));
    }

    [Fact]
    public void MacSkiaAndHarfBuzzUseLoaderRelativeInstallNames()
    {
        string app = MacRelease.Folder(_root, name: "absolute-skia");
        File.WriteAllBytes(Path.Combine(app, "Contents", "MacOS", "libSkiaSharp.dylib"), MacRelease.MachO("arm64", installName: "/tmp/libSkiaSharp.dylib"));
        File.WriteAllBytes(Path.Combine(app, "Contents", "MacOS", "libHarfBuzzSharp.dylib"), MacRelease.MachO("arm64", installName: "/tmp/libHarfBuzzSharp.dylib"));
        List<string> problems = MacProblems(app);
        Assert.Equal(2, problems.Count);
        Assert.All(problems, problem => Assert.Contains("has no relocatable install name", problem, StringComparison.Ordinal));
    }

    [Fact]
    public void UniversalLauncherLibrariesAreValidatedPerArchitecture()
    {
        byte[] x64 = MacRelease.MachO("x64", installName: "@rpath/libAvaloniaNative.dylib");
        byte[] arm64 = MacRelease.MachO("arm64", installName: "@rpath/libAvaloniaNative.dylib");
        byte[] fat = new byte[48 + x64.Length + arm64.Length];
        new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }.CopyTo(fat, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(4), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(8), 0x01000007);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(16), 48);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(20), (uint)x64.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(24), 12);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(28), 0x0100000C);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(36), (uint)(48 + x64.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(40), (uint)arm64.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(44), 12);
        x64.CopyTo(fat, 48);
        arm64.CopyTo(fat, 48 + x64.Length);
        Assert.True(MachOFacts.Read(fat, "arm64")!.Universal);
        Assert.Equal(0x01000007u, MachOFacts.Read(fat, "x64")!.CpuType);
        Assert.Null(MachOFacts.Read(fat));
        Assert.Null(MachOFacts.Read(fat[..48], "arm64"));
        string app = MacRelease.Folder(_root, name: "universal-launcher");
        File.WriteAllBytes(Path.Combine(app, "Contents", "MacOS", "libAvaloniaNative.dylib"), fat);
        var problems = new List<string>();
        var notes = new List<string>();
        ReleaseRules.CheckMacFolder(app, "arm64", MacRelease.Icns(), null, problems, notes);
        Assert.Empty(problems);
        Assert.Contains(notes, note => note.StartsWith("libAvaloniaNative.dylib: universal arm64 dylib", StringComparison.Ordinal));
    }

    [Fact]
    public void FfmpegCannotUseAnRpathInstallNameOrDependency()
    {
        string app = MacRelease.Folder(_root, name: "ffmpeg-rpath");
        const string lib = "libavutil.61.dylib";
        File.WriteAllBytes(Path.Combine(app, "Contents", "MacOS", lib), MacRelease.MachO("arm64", 6,
            "\0libavutil license: LGPL version 2.1 or later\0--disable-gpl --disable-nonfree\0", "@rpath/" + lib, ["@rpath/libavcodec.63.dylib"]));
        Assert.Contains("has no relocatable install name", Assert.Single(MacProblems(app)));
    }

    [Fact]
    public void FfmpegCannotUseAnRpathDependency()
    {
        string app = MacRelease.Folder(_root, name: "ffmpeg-rpath-dependency");
        const string lib = "libavutil.61.dylib";
        File.WriteAllBytes(Path.Combine(app, "Contents", "MacOS", lib), MacRelease.MachO("arm64", 6,
            "\0libavutil license: LGPL version 2.1 or later\0--disable-gpl --disable-nonfree\0", "@loader_path/" + lib, ["@rpath/libavcodec.63.dylib"]));
        Assert.Contains("has a non-relocatable dependency", Assert.Single(MacProblems(app)));
    }

    private static List<string> LinuxProblems(string folder)
    {
        var problems = new List<string>();
        ReleaseRules.CheckLinuxFolder(folder, problems, []);
        return problems;
    }

    private static List<string> MacProblems(string app)
    {
        var problems = new List<string>();
        ReleaseRules.CheckMacFolder(app, "arm64", MacRelease.Icns(), null, problems, []);
        return problems;
    }
}

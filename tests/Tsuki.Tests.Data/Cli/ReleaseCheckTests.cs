using Tsuki.Cli;
using Tsuki.Cli.Commands;
using Tsuki.Data.Icons;

namespace Tsuki.Tests.Data.Cli;

/// <summary>
/// Package K28: <c>Tsuki.Cli release-check</c> refuses a folder that is a personal build. No game data is needed: the folders are made of copies of the Tsukihime.exe
/// and Tsukihime.dll built next to this test (a normal build), with the marks of a personal build added one at a time.
/// </summary>
public sealed class ReleaseCheckTests : IDisposable
{
    private static string HostName => OperatingSystem.IsWindows() ? "Tsukihime.exe" : "Tsukihime";

    private static string IconName => OperatingSystem.IsWindows() ? "Tsukihime.exe" : "Tsukihime.dll";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k28-release-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _crescent;

    public ReleaseCheckTests()
    {
        Directory.CreateDirectory(_root);
        _crescent = File.ReadAllBytes(Path.Combine(RepoRoot(), "assets", "icon", "tsukihime.ico"));
    }

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

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Tsuki.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// A folder that looks like a publish of a normal build: on Windows the apphost and its assembly, copied from the test output; elsewhere a whole Linux publish
    /// (<see cref="LinuxRelease.Folder"/>), since a Linux release needs SDL, FFmpeg, the runtime, the launcher and the licences beside the player.
    /// </summary>
    private string NormalFolder(string name)
    {
        if (!OperatingSystem.IsWindows()) { return LinuxRelease.Folder(_root, name); }
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (string file in new[] { HostName, "Tsukihime.dll" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(folder, file));
        }

        return folder;
    }

    [Fact]
    public void ANormalBuildMayBeReleasedAndItsIconIsTheCrescent()
    {
        string folder = NormalFolder("normal");
        var dumped = new List<string>();
        ReleaseCheckResult result = ReleaseCheck.Run(folder, _crescent, (program, frames) => dumped.Add($"{program}:{frames.Count}"));
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        Assert.Contains(result.Notes, n => n.StartsWith(IconName + ": the icon group is the crescent", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.StartsWith("Tsukihime.dll: version", StringComparison.Ordinal) && n.EndsWith("no personal mark", StringComparison.Ordinal));
        int frames = FaceIcon.DecodeIco(_crescent).Count;
        // a Linux publish also carries the launcher, whose assembly holds the same crescent
        Assert.Equal(OperatingSystem.IsWindows() ? [$"Tsukihime:{frames}"] : [$"Tsukihime:{frames}", $"TsukihimeLauncher:{frames}"], dumped);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        string dump = Path.Combine(_root, "dump");
        Assert.Equal(ExitCode.Ok, CliApp.Run(["release-check", folder, "--icon", Path.Combine(RepoRoot(), "assets", "icon", "tsukihime.ico"), "--dump", dump], stdout, stderr));
        Assert.Contains("RESULT: " + folder + " may be released", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
        // the crescent's 256 px entry is PNG-compressed and not decoded (FaceIcon.DecodeIco reads the 32-bit BMP entries); the 16 px frame is one of those
        Assert.Contains(Directory.GetFiles(dump), f => f.EndsWith("Tsukihime_16.png", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePersonalNoteAnywhereInTheFolderIsRefused()
    {
        string folder = NormalFolder("note");
        Directory.CreateDirectory(Path.Combine(folder, "licenses"));
        File.WriteAllText(Path.Combine(folder, "licenses", ReleaseCheck.PersonalNote), "do not share");
        ReleaseCheckResult result = ReleaseCheck.Run(folder, _crescent);
        Assert.False(result.Ok);
        Assert.Single(result.Problems);
        Assert.Contains("PERSONAL_BUILD.txt is there", result.Problems[0], StringComparison.Ordinal);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.Equal(ExitCode.Failed, CliApp.Run(["release-check", folder, "--icon", Path.Combine(RepoRoot(), "assets", "icon", "tsukihime.ico")], stdout, stderr));
        Assert.Contains("must NOT be released (1 problem(s))", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExecutableWhoseIconIsNotTheCrescentIsRefused()
    {
        // the icon images sit in the executable's resources: changing a few pixels of the first one keeps the file a valid PE image with another icon
        string folder = NormalFolder("icon");
        string exe = Path.Combine(folder, IconName);
        byte[] bytes = File.ReadAllBytes(exe);
        byte[] group = PeIcons.IconGroup(exe)!;
        IReadOnlyList<IconFrame> frames = FaceIcon.DecodeIco(group);
        int imageOffset = BitConverter.ToInt32(group, 6 + 12);
        int imageLength = BitConverter.ToInt32(group, 6 + 8);
        byte[] image = group.AsSpan(imageOffset, imageLength).ToArray();
        int at = bytes.AsSpan().IndexOf(image);
        Assert.True(at > 0, "the first icon image was not found in the executable");
        for (int i = 40; i < 40 + 256; i++)
        {
            bytes[at + i] ^= 0x5A;                                                            // a block of pixels of the 16 px frame
        }

        File.WriteAllBytes(exe, bytes);
        IReadOnlyList<IconFrame> changed = FaceIcon.DecodeIco(PeIcons.IconGroup(exe)!);
        Assert.Equal(frames.Count, changed.Count);
        Assert.NotEqual(frames[0].Rgba, changed[0].Rgba);

        ReleaseCheckResult result = ReleaseCheck.Run(folder, _crescent);
        Assert.False(result.Ok);
        Assert.Single(result.Problems);
        Assert.StartsWith(IconName + " carries an icon that is not the port's crescent", result.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ALinuxHostWithoutItsPlayerAssemblyIsRefused()
    {
        string folder = Path.Combine(_root, "linux-host-only");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Tsukihime"), [0x7F, 0x45, 0x4C, 0x46]);
        ReleaseCheckResult result = ReleaseCheck.Run(folder, _crescent);
        Assert.False(result.Ok);
        Assert.Contains("Tsukihime.dll is not in " + folder + ": the Linux player assembly is missing", result.Problems);
        Assert.Contains("Tsukihime is not an x86-64 ELF program", result.Problems);                // four bytes are no ELF header (K46)
    }

    [Fact]
    public void ALinuxPublishWithItsLibrariesAndLicencesMayBeReleased()
    {
        // package K46: the player, SDL, the four FFmpeg 9 libraries of an LGPL build and the licences
        string folder = LinuxRelease.Folder(_root, "linux");
        ReleaseCheckResult result = ReleaseCheck.Run(folder, _crescent);
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        Assert.Contains("Tsukihime: x86-64 ELF program", result.Notes);
        Assert.Contains(result.Notes, n => n.StartsWith("Tsukihime.dll: the icon group is the crescent", StringComparison.Ordinal));
        Assert.Contains("libavcodec.so.63: x86-64 shared object, LGPL build", result.Notes);
        Assert.Contains("libSDL3.so: x86-64 shared object", result.Notes);
        Assert.Contains("licenses/FFmpeg-SOURCE.txt names the Linux FFmpeg build", result.Notes);
        Assert.Contains("the self-contained .NET runtime is beside the player", result.Notes);
    }

    [Theory]
    [InlineData("gpl")]
    [InlineData("nonfree")]
    [InlineData("no licence string")]
    [InlineData("postproc")]
    [InlineData("no libavformat")]
    [InlineData("no SDL")]
    [InlineData("library not ELF")]
    [InlineData("windows notice")]
    [InlineData("no licence file")]
    [InlineData("windows program")]
    [InlineData("host not x86-64")]
    [InlineData("framework-dependent")]
    public void ALinuxPublishWithoutItsLibrariesOrWithAGplFfmpegIsRefused(string what)
    {
        string folder = LinuxRelease.Folder(_root, "linux-" + what.Replace(' ', '-'));
        string expected;
        switch (what)
        {
            case "gpl":
                File.WriteAllBytes(Path.Combine(folder, "libavcodec.so.63"), LinuxRelease.Ffmpeg("libavcodec.so.63", LinuxRelease.LgplConfiguration + " --enable-gpl --enable-libx264"));
                expected = "libavcodec.so.63 was built with --enable-gpl or --enable-nonfree";
                break;
            case "nonfree":
                File.WriteAllBytes(Path.Combine(folder, "libavformat.so.63"), LinuxRelease.Ffmpeg("libavformat.so.63", LinuxRelease.LgplConfiguration + " --enable-nonfree"));
                expected = "libavformat.so.63 was built with --enable-gpl or --enable-nonfree";
                break;
            case "no licence string":
                File.WriteAllBytes(Path.Combine(folder, "libavutil.so.61"), LinuxRelease.Elf(3, "\0" + LinuxRelease.LgplConfiguration));
                expected = "libavutil.so.61 does not carry FFmpeg's LGPL licence string";
                break;
            case "postproc":
                File.WriteAllBytes(Path.Combine(folder, "libpostproc.so.59"), LinuxRelease.Elf(3));
                expected = "libpostproc.so.59 is FFmpeg's GPL-only post-processing library";
                break;
            case "no libavformat":
                File.Delete(Path.Combine(folder, "libavformat.so.63"));
                expected = "libavformat.so.63 is not beside Tsukihime";
                break;
            case "no SDL":
                File.Delete(Path.Combine(folder, "libSDL3.so"));
                expected = "libSDL3.so is not beside Tsukihime";
                break;
            case "library not ELF":
                File.WriteAllText(Path.Combine(folder, "libswresample.so.7"), "libswresample.so.7.1.100");      // a link an extractor left as text
                expected = "libswresample.so.7 is not an x86-64 ELF shared object";
                break;
            case "windows notice":
                File.WriteAllText(Path.Combine(folder, "licenses", "FFmpeg-SOURCE.txt"), "BtbN's win64-lgpl-shared-9.0 build: avcodec-63.dll avformat-63.dll avutil-61.dll swresample-7.dll");
                expected = "licenses/FFmpeg-SOURCE.txt is not the notice of the Linux libraries";
                break;
            case "no licence file":
                File.Delete(Path.Combine(folder, "licenses", "dotnet-THIRD-PARTY-NOTICES.txt"));
                expected = "licenses/dotnet-THIRD-PARTY-NOTICES.txt is missing";
                break;
            case "windows program":
                File.WriteAllBytes(Path.Combine(folder, "createdump.exe"), [0x4D, 0x5A]);
                expected = "createdump.exe is a Windows program in the Linux folder";
                break;
            case "framework-dependent":
                // K46 review: an offline `--self-contained false` publish has everything but the runtime
                File.Delete(Path.Combine(folder, "libcoreclr.so"));
                File.Delete(Path.Combine(folder, "System.Private.CoreLib.dll"));
                expected = "libcoreclr.so, System.Private.CoreLib.dll not beside Tsukihime: the Linux release is a self-contained publish";
                break;
            default:
                byte[] host = LinuxRelease.Elf(2);
                host[18] = 183;                                                                                 // aarch64
                File.WriteAllBytes(Path.Combine(folder, "Tsukihime"), host);
                expected = "Tsukihime is not an x86-64 ELF program";
                break;
        }

        ReleaseCheckResult result = ReleaseCheck.Run(folder, _crescent);
        Assert.False(result.Ok);
        Assert.StartsWith(expected, Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("assets/branding/tsukihime-cg.png", "is a picture")]
    [InlineData("docs/screenshot.jpg", "is a picture")]
    [InlineData("icons/tsukihime.ico", "is a picture")]
    [InlineData("Contents/Resources/Tsukihime.icns", "is a picture")]
    [InlineData("image/movie/op.mp4", "is game data or a save")]
    [InlineData("eboot.bin", "is game data or a save")]
    [InlineData("save.dat", "is game data or a save")]
    public void APictureOrGameDataInAFolderToBeReleasedIsRefusedOnEitherPlatform(string file, string why)
    {
        // package K46: pictures (bar the README's logo) and anything cut from a game folder never go out
        foreach (string folder in new[] { NormalFolder("art-" + Guid.NewGuid().ToString("N")[..6]), LinuxRelease.Folder(_root, "linux-art-" + Guid.NewGuid().ToString("N")[..6]) })
        {
            string path = Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1, 2, 3]);
            ReleaseCheckResult result = ReleaseCheck.Run(folder, _crescent);
            Assert.False(result.Ok);
            Assert.StartsWith($"{file} {why}", Assert.Single(result.Problems).Replace('\\', '/'), StringComparison.Ordinal);
        }

        Assert.Null(ReleaseRules.Forbidden(ReleaseRules.Logo));
        Assert.Null(ReleaseRules.Forbidden("licenses/FFmpeg-LGPL.txt"));
        Assert.Null(ReleaseRules.Forbidden("libavcodec.so.63"));
        Assert.Null(ReleaseRules.Forbidden("Tsukihime.dll"));
    }

    [Theory]
    [InlineData("Tsuki.Cli.dll")]
    [InlineData("Tsuki.Cli")]
    [InlineData("Tsuki.Cli.exe")]
    [InlineData("Tsuki.Cli.runtimeconfig.json")]
    [InlineData("Tsuki.DevBridge.dll")]
    [InlineData("Tsuki.DevBridge.deps.json")]
    public void NoReleaseOfAnyPlatformCarriesADeveloperProgram(string file)
    {
        // review K48: a Debug publish brings the dev bridge along; release-check, release-sums and the updaters refuse it by name
        Assert.Equal($"{file} is a developer program; a release carries none (publish with -c Release)", ReleaseRules.Forbidden(file));
        Assert.Equal($"Contents/MacOS/{file} is a developer program; a release carries none (publish with -c Release)", ReleaseRules.Forbidden("Contents/MacOS/" + file, macBundle: true));
        string folder = LinuxRelease.Folder(_root, "dev-" + file);
        File.WriteAllBytes(Path.Combine(folder, file), [0x4D, 0x5A]);
        Assert.Contains($"{file} is a developer program; a release carries none (publish with -c Release)", ReleaseCheck.Run(folder, _crescent).Problems);
        Assert.Null(ReleaseRules.Forbidden("Tsuki.Data.dll"));
        Assert.Null(ReleaseRules.Forbidden("Tsuki.Client.dll"));
    }

    [Theory]
    [InlineData("runtimeconfig", "TsukihimeLauncher.runtimeconfig.json is missing: the launcher cannot start without it")]
    [InlineData("deps", "Tsukihime.deps.json is missing: the player cannot start without it")]
    [InlineData("listed", "Avalonia.Base.dll, listed in TsukihimeLauncher.deps.json, not beside TsukihimeLauncher: the launcher would not start")]
    [InlineData("unreadable", "TsukihimeLauncher.deps.json is not a readable dependency file")]
    public void ALinuxPublishWhoseProgramsCannotStartIsRefused(string what, string expected)
    {
        // review K48: a launcher without its runtimeconfig, deps.json or Avalonia passed every gate and died at its start
        string folder = LinuxRelease.Folder(_root, "start-" + what);
        switch (what)
        {
            case "runtimeconfig": File.Delete(Path.Combine(folder, "TsukihimeLauncher.runtimeconfig.json")); break;
            case "deps": File.Delete(Path.Combine(folder, "Tsukihime.deps.json")); break;
            case "listed": File.Delete(Path.Combine(folder, "Avalonia.Base.dll")); break;
            default: File.WriteAllText(Path.Combine(folder, "TsukihimeLauncher.deps.json"), "[]"); break;
        }

        ReleaseCheckResult result = ReleaseCheck.Run(folder, _crescent);
        Assert.Equal([expected], result.Problems);
    }

    [Fact]
    public void ADepsFileListsTheRuntimeAndNativeFilesOfEveryTarget()
    {
        string folder = LinuxRelease.Folder(_root, "deps-files");
        Assert.Equal(["Avalonia.Base.dll", "TsukihimeLauncher.dll", "libSkiaSharp.so"], ReleaseRules.DepsFiles(Path.Combine(folder, "TsukihimeLauncher.deps.json")));
        Assert.Equal(["Tsukihime.dll"], ReleaseRules.DepsFiles(Path.Combine(folder, "Tsukihime.deps.json")));
        Assert.Contains("TsukihimeLauncher: runtimeconfig.json, deps.json and the 3 files it lists", ReleaseCheck.Run(folder, _crescent).Notes);
    }

    [Fact]
    public void TheRequiredLicencesArePinned()
    {
        // review K48 tests-docs-12: the fixtures are built from these lists, so the lists themselves are pinned, and every notice the launcher publishes is in both
        string[] common =
        [
            "licenses/FFmpeg-SOURCE.txt", "licenses/SDL3-zlib.txt", "licenses/knife-MPL-2.0.txt", "licenses/LICENSE.txt", "licenses/THIRD_PARTY.md",
            "licenses/Avalonia-MIT.txt", "licenses/SkiaSharp-LICENSE.txt", "licenses/HarfBuzzSharp-LICENSE.txt", "licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt",
            "licenses/MicroCom-MIT.txt", "licenses/Tmds-DBus-MIT.txt", "licenses/dotnet-LICENSE.txt", "licenses/dotnet-THIRD-PARTY-NOTICES.txt",
        ];
        string[] linux = [.. common, "licenses/FFmpeg-LGPL.txt", "licenses/FFmpeg-GPL-3.0.txt", "LINUX.md"];
        string[] mac = [.. common, "licenses/FFmpeg-LGPL-2.1.txt", "licenses/FFmpeg-LICENSE.md", "MACOS.md"];
        Assert.Equal(linux.Order(StringComparer.Ordinal), ReleaseRules.LinuxLicences.Order(StringComparer.Ordinal));
        Assert.Equal(mac.Order(StringComparer.Ordinal), ReleaseRules.MacLicences.Order(StringComparer.Ordinal));
        string project = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Tsuki.Launcher.Avalonia", "Tsuki.Launcher.Avalonia.csproj"));
        string[] published = [.. System.Text.RegularExpressions.Regex.Matches(project, "Link=\"(licenses\\\\[^\"]+)\"").Select(m => m.Groups[1].Value.Replace('\\', '/'))];
        Assert.NotEmpty(published);
        Assert.Subset(ReleaseRules.LinuxLicences.ToHashSet(StringComparer.Ordinal), published.ToHashSet(StringComparer.Ordinal));
        Assert.Subset(ReleaseRules.MacLicences.ToHashSet(StringComparer.Ordinal), published.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void TheMacIconsPngsCannotStopAWindowsOrLinuxCheck()
    {
        // review K48 windows-regression-7: the Mac ICNS is made from the PNGs beside --icon for every check; one that cannot be read must not throw
        string icons = Path.Combine(_root, "odd-icons");
        Directory.CreateDirectory(icons);
        string icon = Path.Combine(icons, "tsukihime.ico");
        File.Copy(Path.Combine(RepoRoot(), "assets", "icon", "tsukihime.ico"), icon);
        Directory.CreateDirectory(Path.Combine(icons, "tsukihime_16.png"));        // a folder where a PNG belongs: reading it is an UnauthorizedAccessException
        File.Copy(Path.Combine(RepoRoot(), "assets", "icon", "tsukihime_32.png"), Path.Combine(icons, "tsukihime_32.png"));
        File.Copy(Path.Combine(RepoRoot(), "assets", "icon", "tsukihime_256.png"), Path.Combine(icons, "tsukihime_256.png"));
        string folder = LinuxRelease.Folder(_root, "odd-icon-linux");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.Equal(ExitCode.Ok, CliApp.Run(["release-check", folder, "--icon", icon], stdout, stderr));
        string tar = LinuxRelease.Tar(LinuxRelease.Folder(_root, "odd-icon-tar"), Path.Combine(_root, "odd-icon-sums", $"Tsukihime-v{PeIcons.Marks(Path.Combine(AppContext.BaseDirectory, "Tsukihime.dll"))!.InformationalVersion!.Split('+')[0]}-linux-x64.tar.gz"));
        Assert.Equal(ExitCode.Ok, CliApp.Run(["release-sums", tar, "--icon", icon], stdout, stderr));
    }

    [Fact]
    public void TheWindowsUpdaterAndTheReleaseRulesNameTheSamePersonalNote()
    {
        // the two constants live in the Windows launcher's Core and in the shared release rules; they must not drift apart
        Assert.Equal(Tsuki.Launcher.Core.UpdateInstaller.PersonalNote, ReleaseCheck.PersonalNote);
    }

    [Fact]
    public void TheAssemblyMarksAreReadAndAFolderWithoutTheProgramIsNotARelease()
    {
        // this test assembly carries a known AssemblyMetadata (Tsuki.Tests.Data.csproj), so the reader's positive case is covered without a personal build
        AssemblyMarks? marks = PeIcons.Marks(typeof(ReleaseCheckTests).Assembly.Location);
        Assert.NotNull(marks);
        Assert.Equal("k28", marks.Metadata["TsukiTestMark"]);
        Assert.False(marks.IsPersonalBuild);
        Assert.NotNull(marks.InformationalVersion);
        var personal = new AssemblyMarks(new Dictionary<string, string> { [PeIcons.PersonalBuildKey] = "true" }, "1.0.0-personal+abc");
        Assert.True(personal.IsPersonalBuild);

        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        ReleaseCheckResult result = ReleaseCheck.Run(empty, _crescent);
        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("not a published folder of the port", StringComparison.Ordinal));
        Assert.False(ReleaseCheck.Run(Path.Combine(_root, "nowhere"), _crescent).Ok);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.Equal(ExitCode.Usage, CliApp.Run(["release-check"], stdout, stderr));
        Assert.Equal(ExitCode.Usage, CliApp.Run(["release-check", empty, "--icon", Path.Combine(_root, "none.ico")], stdout, stderr));
    }
}

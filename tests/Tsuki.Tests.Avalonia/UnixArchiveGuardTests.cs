using System.Formats.Tar;
using System.IO.Compression;
using Tsuki.Cli.Commands;
using Tsuki.Launcher.Avalonia;
using Tsuki.Launcher.Core;
using Tsuki.Tests.Data.Cli;
using Tsuki.Tests.Synthetic.Launcher;

namespace Tsuki.Tests.Avalonia;

/// <summary>
/// The macOS and Linux updaters' archive checks (<see cref="UnixReleaseArchive"/>, <see cref="ReleaseRuleSet.Update"/>): what would make an install unsafe or unable to
/// start is refused with its own reason in launcher.log and a sentence for the player, and nothing a refused entry carries is written; what only release-sums holds a
/// release of this version to (its libraries, licences, notices, modes by name and signing identifiers) does not stop a later release from installing.
/// </summary>
public sealed class UnixArchiveGuardTests : IDisposable
{
    private readonly string _root = UnixUpdateTestKit.TempRoot("tsuki-k48-archive-guards-");
    private static ReleaseVersion Version => ReleaseFiles.BuildVersion();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("/absolute", "it is an absolute path")]
    [InlineData("folder\\evil", "it contains a backslash")]
    [InlineData("C:/evil", "it contains a colon")]
    [InlineData("./README.md", "it has an empty, '.' or '..' part")]
    [InlineData("../escaped", "it has an empty, '.' or '..' part")]
    [InlineData("docs//twice", "it has an empty, '.' or '..' part")]
    [InlineData("bad\u0001name", "it contains a NUL or control character")]
    [InlineData("README.md", "occurs more than once")]
    [InlineData("readme.md", "occurs more than once")]
    public void LinuxRefusesAnUnsafeOrDuplicateEntryName(string entry, string expected)
    {
        string archive = Tar("path-" + Name(entry), extra: (writer, root) => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entry.StartsWith('/') ? entry : root + "/" + entry)
        {
            Mode = ReleaseRules.Mode644,
            DataStream = new MemoryStream([1]),
        }));
        Assert.Contains(expected, Refusal(() => Extract(archive, "path-out-" + Name(entry))), StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxRefusesAnArchiveThatIsNotOneFolderOfItsName()
    {
        string archive = Tar("flat", extra: (writer, _) => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "elsewhere/README.md") { Mode = ReleaseRules.Mode644, DataStream = new MemoryStream([1]) }));
        Assert.Contains("is not inside the one folder flat/", Refusal(() => Extract(archive, "flat-out")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    [InlineData(TarEntryType.CharacterDevice)]
    [InlineData(TarEntryType.BlockDevice)]
    [InlineData(TarEntryType.Fifo)]
    public void LinuxRefusesLinksAndSpecialEntries(TarEntryType type)
    {
        string archive = Tar("type-" + type, extra: (writer, root) =>
        {
            var entry = new PaxTarEntry(type, root + "/" + type) { Mode = ReleaseRules.Mode644 };
            if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink) { entry.LinkName = "README.md"; }
            writer.WriteEntry(entry);
        });
        Assert.Contains($"{type} is a {type} entry; the archive holds only folders and plain files", Refusal(() => Extract(archive, "type-out-" + type)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("README.md", 0x800, "README.md has the mode 4644: it has a set-id or sticky bit")]
    [InlineData("README.md", 0x400, "README.md has the mode 2644: it has a set-id or sticky bit")]
    [InlineData("README.md", 0x200, "README.md has the mode 1644: it has a set-id or sticky bit")]
    [InlineData("README.md", 0x10, "README.md has the mode 664: the group or others may write it")]
    [InlineData("TsukihimeLauncher", 0x12, "TsukihimeLauncher has the mode 777: the group or others may write it")]
    [InlineData("README.md", -0x80, "README.md has the mode 444: its owner cannot read and write it")]
    [InlineData("Tsukihime", -0x40, "Tsukihime has the mode 655: its owner cannot run it")]
    [InlineData("TsukihimeLauncher", -0x49, "TsukihimeLauncher has the mode 644: its owner cannot run it")]
    public void LinuxRefusesAModeThatIsNotSafe(string file, int change, string expected)
    {
        // no set-id bits, nothing others may write, files a later update can replace, and both programs runnable
        string archive = Tar("mode-" + Name(file) + change, (relative, mode) => relative == file ? (UnixFileMode)((int)mode + change) : mode);
        Assert.Contains(expected, Refusal(() => Extract(archive, "mode-out-" + Name(file) + change)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "locked/ has the mode 0: its owner cannot read, write and enter it")]
    [InlineData(0x140, "locked/ has the mode 500: its owner cannot read, write and enter it")]
    [InlineData(0x1FF, "locked/ has the mode 777: the group or others may write it")]
    public void LinuxRefusesAFolderModeALaterUpdateCouldNotUndo(int mode, string expected)
    {
        // review K48 -10: a folder of mode 000 left a staging folder no later update could delete
        string archive = Tar("folder-" + mode, extra: (writer, root) =>
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, root + "/locked/") { Mode = (UnixFileMode)mode });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, root + "/locked/file.txt") { Mode = ReleaseRules.Mode644, DataStream = new MemoryStream([1]) });
        });
        string output = Path.Combine(_root, "folder-out-" + mode);
        Assert.Contains(expected, Refusal(() => Extract(archive, "folder-out-" + mode)), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(output, "folder-" + mode, "locked")));
    }

    [Fact]
    public void LinuxInstallsAnyOtherSafeMode()
    {
        // decision D1 of v0.1.7: the updater holds no file to package_linux.py's mode by its name; release-sums does
        string archive = Tar("modes", (relative, mode) => relative switch
        {
            "README.md" => UnixFileMode.UserRead | UnixFileMode.UserWrite,
            "libSDL3.so" => ReleaseRules.Mode644,
            "TsukihimeLauncher" => UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            _ => mode,
        }, extra: (writer, root) =>
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, root + "/private/") { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, root + "/private/tsukihime-steam.sh") { Mode = ReleaseRules.Mode755, DataStream = new MemoryStream("#!/bin/sh\n"u8.ToArray()) });
        });
        StagedRelease staged = UnixReleaseArchive.ExtractLinux(archive, Path.Combine(_root, "modes-out"), Version, CancellationToken.None);
        Assert.Contains(Path.Combine("private", "tsukihime-steam.sh"), staged.Files);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(staged.Root, "README.md")));
        }

        Assert.Contains(ReleaseProblems(archive), problem => problem.Contains("README.md has the mode 600, not 644", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("TsukihimeLauncher", "TsukihimeLauncher is not an x86-64 ELF program")]
    [InlineData("Tsukihime", "Tsukihime is not an x86-64 ELF program")]
    [InlineData("TsukihimeLauncher.dll", "TsukihimeLauncher.dll is missing")]
    [InlineData("Tsukihime.dll", "Tsukihime.dll is missing")]
    public void LinuxRequiresBothProgramsAndBothAssemblies(string missing, string expected)
    {
        string archive = Tar("missing-" + missing, prepare: folder => File.Delete(Path.Combine(folder, missing)));
        Assert.Contains(expected, Refusal(() => Extract(archive, "missing-out-" + missing)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Tsukihime")]
    [InlineData("TsukihimeLauncher")]
    public void LinuxRequiresEachAssemblyToCarryTheReleasesVersion(string program)
    {
        // review K48 -05: the fixture's two assemblies are one file, so each is replaced alone by an assembly of another version
        string archive = Tar("version-" + program, prepare: folder => File.Copy(typeof(Assert).Assembly.Location, Path.Combine(folder, program + ".dll"), overwrite: true));
        string refusal = Refusal(() => Extract(archive, "version-out-" + program));
        Assert.Contains($"{program}.dll says version", refusal, StringComparison.Ordinal);
        Assert.Single(refusal.Split("; "), problem => problem.Contains("says version", StringComparison.Ordinal));
    }

    [Fact]
    public void LinuxRequiresTheReleasesVersionOnBothAssemblies()
    {
        string archive = Tar("wrong-version");
        var refused = Assert.Throws<UnixUpdateRefusedException>(() => UnixReleaseArchive.ExtractLinux(archive, Path.Combine(_root, "wrong-version-out"), new ReleaseVersion(9, 9, 9), CancellationToken.None));
        string refusal = string.Join("; ", refused.Problems);
        Assert.Contains("Tsukihime.dll says version", refusal, StringComparison.Ordinal);
        Assert.Contains("TsukihimeLauncher.dll says version", refusal, StringComparison.Ordinal);
        Assert.Contains("the release says 9.9.9", refusal, StringComparison.Ordinal);

        // review linux-deck-4: the player is told what to do, not how to publish (that goes to launcher.log)
        Assert.Equal(UnixReleaseArchive.RefusedSentence("wrong-version.tar.gz", mac: false), refused.Message);
        Assert.Contains("Download wrong-version.tar.gz from the Releases page and replace the old folder by hand; your saves and settings stay where they are.", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("<Version>", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("licenses")]
    public void LinuxRefusesAPersonalBuild(string folderName)
    {
        string archive = Tar("personal-" + folderName, prepare: folder => File.WriteAllText(Path.Combine(folder, folderName, ReleaseCheck.PersonalNote), "personal"));
        Assert.Contains("a personal build is never released", Refusal(() => Extract(archive, "personal-out-" + folderName)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("skia", "libSkiaSharp.so, listed in TsukihimeLauncher.deps.json, not beside TsukihimeLauncher: the launcher would not start")]
    [InlineData("runtimeconfig", "TsukihimeLauncher.runtimeconfig.json is missing: the launcher cannot start")]
    [InlineData("deps", "Tsukihime.deps.json is missing: the player cannot start")]
    [InlineData("avalonia", "Avalonia.Base.dll, listed in TsukihimeLauncher.deps.json, not beside TsukihimeLauncher")]
    [InlineData("devbridge", "Tsuki.DevBridge.dll is a developer program")]
    [InlineData("cli", "Tsuki.Cli is a developer program")]
    [InlineData("picture", "assets/screenshot.png is a picture")]
    [InlineData("save", "save.dat is game data or a save")]
    public void LinuxRefusesAReleaseThatCouldNotStartOrMustNotBeInstalled(string what, string expected)
    {
        // review K48 tests-docs-03: what keeps an install safe and able to start refuses the downloaded archive
        string archive = Tar("rules-" + what, prepare: folder =>
        {
            switch (what)
            {
                case "skia": File.Delete(Path.Combine(folder, "libSkiaSharp.so")); break;
                case "runtimeconfig": File.Delete(Path.Combine(folder, "TsukihimeLauncher.runtimeconfig.json")); break;
                case "deps": File.Delete(Path.Combine(folder, "Tsukihime.deps.json")); break;
                case "avalonia": File.Delete(Path.Combine(folder, "Avalonia.Base.dll")); break;
                case "devbridge": File.WriteAllBytes(Path.Combine(folder, "Tsuki.DevBridge.dll"), [0x4D, 0x5A]); break;
                case "cli": File.WriteAllBytes(Path.Combine(folder, "Tsuki.Cli"), LinuxRelease.Elf(2)); break;
                case "picture": Directory.CreateDirectory(Path.Combine(folder, "assets")); File.WriteAllBytes(Path.Combine(folder, "assets", "screenshot.png"), [1]); break;
                default: File.WriteAllBytes(Path.Combine(folder, "save.dat"), [1]); break;
            }
        });
        string output = Path.Combine(_root, "rules-out-" + what);
        Assert.Contains(expected, Refusal(() => Extract(archive, "rules-out-" + what)), StringComparison.Ordinal);
        if (what is "picture" or "save" or "devbridge")
        {
            // refused before it is written: a forbidden file never reaches the disk
            Assert.Empty(Directory.GetFiles(output, what == "picture" ? "screenshot.png" : what == "save" ? "save.dat" : "Tsuki.DevBridge.dll", SearchOption.AllDirectories));
        }
    }

    [Theory]
    [InlineData("licence", "licenses/Tmds-DBus-MIT.txt is missing")]
    [InlineData("guide moved", "LINUX.md is missing")]
    [InlineData("soname", "libavcodec.so.63 is not beside Tsukihime")]
    [InlineData("helper", "tsukihime-steam.sh has the mode 755, not 644")]
    [InlineData("postproc", "libpostproc.so.59 is FFmpeg's GPL-only post-processing library")]
    [InlineData("windows", "createdump.exe is a Windows program in the Linux folder")]
    public void ALinuxReleaseWhoseFilesChangedInstallsButIsNotThisVersionsRelease(string what, string releaseSums)
    {
        // decision D1 of v0.1.7 (review linux-deck-2): an installed launcher must take a later release that drops a licence, moves a guide, moves to the next
        // FFmpeg or adds a file; only release-sums holds a release to this version's lists
        string archive = Tar("changed-" + Name(what), (relative, mode) => relative == "tsukihime-steam.sh" ? ReleaseRules.Mode755 : mode, prepare: folder =>
        {
            switch (what)
            {
                case "licence": File.Delete(Path.Combine(folder, "licenses", "Tmds-DBus-MIT.txt")); break;
                case "guide moved": File.Move(Path.Combine(folder, "LINUX.md"), Path.Combine(folder, "docs", "LINUX.md")); break;
                case "soname":
                    File.Move(Path.Combine(folder, "libavcodec.so.63"), Path.Combine(folder, "libavcodec.so.64"));
                    File.WriteAllText(Path.Combine(folder, "licenses", "FFmpeg-SOURCE.txt"), "BtbN's \"linux64-lgpl-shared-10.0\" build: libavcodec.so.64\n");
                    break;
                case "helper": File.WriteAllText(Path.Combine(folder, "tsukihime-steam.sh"), "#!/bin/sh\nexec \"$(dirname \"$0\")/Tsukihime\" \"$@\"\n"); break;
                case "postproc": File.WriteAllBytes(Path.Combine(folder, "libpostproc.so.59"), LinuxRelease.Elf(3)); break;
                default: File.WriteAllBytes(Path.Combine(folder, "createdump.exe"), [0x4D, 0x5A]); break;
            }
        });
        StagedRelease staged = UnixReleaseArchive.ExtractLinux(archive, Path.Combine(_root, "changed-out-" + Name(what)), Version, CancellationToken.None);
        Assert.Contains("TsukihimeLauncher", staged.Files);
        Assert.Contains(ReleaseProblems(archive), problem => problem.Contains(releaseSums, StringComparison.Ordinal));
    }

    [Fact]
    public void LinuxHoldsTheArchiveToTheSizeAndEntryLimits()
    {
        string archive = Tar("limits");
        long largest = LargestEntry(Path.Combine(_root, "limits"));
        long sum = Directory.GetFiles(Path.Combine(_root, "limits"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        int entries = Directory.GetFileSystemEntries(Path.Combine(_root, "limits"), "*", SearchOption.AllDirectories).Length + 1;
        Assert.Equal(Version.ToString(), UnixReleaseArchive.ExtractLinux(archive, Path.Combine(_root, "limits-ok"), Version, CancellationToken.None, new ReleaseLimits(largest, sum, entries)).LauncherVersion?.Split('+')[0]);
        Assert.Contains("exceeds the", Refusal(() => UnixReleaseArchive.ExtractLinux(archive, Path.Combine(_root, "limits-file"), Version, CancellationToken.None, new ReleaseLimits(largest - 1, sum, entries))), StringComparison.Ordinal);
        Assert.Contains("exceeds the", Refusal(() => UnixReleaseArchive.ExtractLinux(archive, Path.Combine(_root, "limits-total"), Version, CancellationToken.None, new ReleaseLimits(largest, sum - 1, entries))), StringComparison.Ordinal);
        Assert.Contains($"more than {entries - 1} entries", Refusal(() => UnixReleaseArchive.ExtractLinux(archive, Path.Combine(_root, "limits-entries"), Version, CancellationToken.None, new ReleaseLimits(largest, sum, entries - 1))), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSizeRuleRefusesNegativeLargeAndOverTotalLengthsAndTheReleaseLimitsAreFixed()
    {
        Assert.NotNull(ReleaseRules.UncompressedSizeProblem(-1, 0, "negative"));
        Assert.NotNull(ReleaseRules.UncompressedSizeProblem(ReleaseRules.MaxFileBytes + 1, 0, "large"));
        Assert.NotNull(ReleaseRules.UncompressedSizeProblem(1, ReleaseRules.MaxTotalBytes, "over-total"));
        Assert.Null(ReleaseRules.UncompressedSizeProblem(ReleaseRules.MaxFileBytes, 0, "at-limit"));
        Assert.Null(ReleaseRules.UncompressedSizeProblem(1, ReleaseRules.MaxTotalBytes - 1, "total-at-limit"));
        Assert.Equal("large: exceeds the 512 MiB per-file or 2 GiB total uncompressed-size limit", ReleaseRules.UncompressedSizeProblem(ReleaseRules.MaxFileBytes + 1, 0, "large"));
        Assert.Equal(new ReleaseLimits(512L * 1024 * 1024, 2L * 1024 * 1024 * 1024, 20_000), ReleaseLimits.Release);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 }, 3L, null)]
    [InlineData(new byte[] { 1, 2, 3, 4 }, 3L, "entry is longer than its archive entry")]
    [InlineData(new byte[] { 1, 2 }, 3L, "entry is shorter than its archive entry")]
    public void AnEntryIsCopiedOnlyWhenItHoldsExactlyWhatItDeclares(byte[] data, long declared, string? expected)
    {
        // .NET's readers cut an over-long entry today; the copy does not rely on it
        string target = Path.Combine(_root, "copy-" + data.Length);
        Assert.Equal(expected, ReleaseRules.CopyExactly(new MemoryStream(data), target, declared, "entry", TestContext.Current.CancellationToken));
        Assert.Equal("entry has no file data", ReleaseRules.CopyExactly(null, target + "-none", 1, "entry", TestContext.Current.CancellationToken));
        Assert.Null(ReleaseRules.CopyExactly(null, target + "-empty", 0, "entry", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("elsewhere/README.md", "is outside the one Tsukihime.app root")]
    [InlineData("/absolute", "it is an absolute path")]
    [InlineData("Tsukihime.app/../escape", "it has an empty, '.' or '..' part")]
    [InlineData("Tsukihime.app/Contents\\escape", "it contains a backslash")]
    [InlineData("C:/escape", "it contains a colon")]
    [InlineData("Tsukihime.app/C:/x", "it contains a colon")]
    [InlineData("Tsukihime.app/Contents/./Info.plist", "it has an empty, '.' or '..' part")]
    [InlineData("Tsukihime.app//twice", "it has an empty, '.' or '..' part")]
    [InlineData("Tsukihime.app/bad\u0001name", "it contains a NUL or control character")]
    public void MacRefusesUnsafeZipPaths(string entry, string expected)
    {
        string archive = MacZip("zip-path-" + Name(entry), extra: zip => AddFile(zip, entry));
        Assert.Contains(expected, Refusal(() => ExtractMac(archive, "zip-path-out-" + Name(entry))), StringComparison.Ordinal);
    }

    [Fact]
    public void MacAcceptsDittosSequesteredSignatureMetadataAndNeverWritesIt()
    {
        // codesign --deep keeps the signatures of Contents/MacOS's non-Mach-O files in extended attributes; ditto --sequesterRsrc stores them under __MACOSX, and
        // the bundle folder's own attributes (com.apple.provenance of the build host, say) as __MACOSX/._Tsukihime.app (review crit-4)
        string archive = MacZip("zip-sequestered", extra: zip =>
        {
            MacRelease.AddSequesteredMetadata(zip, ["Contents/MacOS/Tsukihime.dll", "Contents/MacOS/TsukihimeLauncher.dll", "Contents/MacOS"]);
            AddFile(zip, "__MACOSX/._Tsukihime.app", [0x00, 0x05, 0x16, 0x07, 0x00, 0x02, 0x00, 0x00]);
        });
        string app = UnixReleaseArchive.ExtractMac(archive, Path.Combine(_root, "zip-sequestered-out"), Info(archive), CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(app, "Contents", "MacOS", "TsukihimeLauncher.dll")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_root, "zip-sequestered-out"), "*MACOSX*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_root, "zip-sequestered-out"), "._*", SearchOption.AllDirectories));
        Assert.Empty(ReleaseProblems(archive));
    }

    [Theory]
    [InlineData("beside", "never beside the files")]
    [InlineData("unpaired", "beside an entry Tsukihime.app/PATH/NAME")]
    [InlineData("other bundle", "beside an entry Tsukihime.app/PATH/NAME")]
    [InlineData("no dot", "beside an entry Tsukihime.app/PATH/NAME")]
    [InlineData("nested", "beside an entry Tsukihime.app/PATH/NAME")]
    [InlineData("folder", "it mirrors no folder of the app")]
    [InlineData("magic", "it does not start with the AppleDouble magic 00 05 16 07")]
    [InlineData("bundle magic", "it does not start with the AppleDouble magic 00 05 16 07")]
    [InlineData("large", "not an AppleDouble file of at most 1 MiB")]
    [InlineData("link", "not a plain file")]
    [InlineData("setuid", "not a plain file")]
    public void MacRefusesEveryOtherMetadataEntry(string what, string expected)
    {
        string archive = MacZip("zip-metadata-" + Name(what), extra: zip =>
        {
            switch (what)
            {
                case "beside": AddFile(zip, "Tsukihime.app/Contents/MacOS/._Tsukihime.dll", [0x00, 0x05, 0x16, 0x07]); break;
                case "unpaired": AddFile(zip, "__MACOSX/Tsukihime.app/Contents/MacOS/._missing.dll", [0x00, 0x05, 0x16, 0x07]); break;
                case "other bundle": AddFile(zip, "__MACOSX/._Other.app", [0x00, 0x05, 0x16, 0x07]); break;
                case "no dot": AddFile(zip, "__MACOSX/Tsukihime.app/Contents/MacOS/xxTsukihime.dll", [0x00, 0x05, 0x16, 0x07]); break;
                case "nested": AddFile(zip, "__MACOSX/Tsukihime.app/Contents/._MacOS/._Tsukihime.dll", [0x00, 0x05, 0x16, 0x07]); break;
                case "folder": MacRelease.AddDirectory(zip, "__MACOSX/Tsukihime.app/Elsewhere/", 0x1ED); break;
                case "magic": MacRelease.AddSequesteredMetadata(zip, ["Contents/MacOS/Tsukihime.dll"], head: [0x00, 0x05, 0x16, 0x00]); break;
                case "bundle magic": AddFile(zip, "__MACOSX/._Tsukihime.app", [0x00, 0x05, 0x16, 0x00]); break;
                case "large": MacRelease.AddSequesteredMetadata(zip, ["Contents/MacOS/Tsukihime.dll"], length: (int)ReleaseRules.MaxMetadataBytes + 1); break;
                case "link": AddFile(zip, "__MACOSX/Tsukihime.app/Contents/MacOS/._Tsukihime.dll", [0x00, 0x05, 0x16, 0x07], 0xA000); break;
                default: AddFile(zip, "__MACOSX/Tsukihime.app/Contents/MacOS/._Tsukihime.dll", [0x00, 0x05, 0x16, 0x07], 0x8000, 0x9A4); break;
            }
        });
        string refusal = Refusal(() => ExtractMac(archive, "zip-metadata-out-" + Name(what)));
        Assert.Contains("is macOS metadata, not a release file", refusal, StringComparison.Ordinal);
        Assert.Contains(expected, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void MacRefusesDuplicateEntries()
    {
        string archive = MacZip("zip-duplicate", extra: zip => zip.CreateEntry("Tsukihime.app/Contents/Resources/README.md"));
        Assert.Contains("occurs more than once", Refusal(() => ExtractMac(archive, "zip-duplicate-out")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("link", "is a symbolic link")]
    [InlineData("fifo", "has a special Unix file type (0x1000)")]
    [InlineData("fifo folder", "has a special Unix file type (0x1000)")]
    [InlineData("setuid", "Contents/Resources/README.md has mode 4644: it has a set-id or sticky bit")]
    [InlineData("writable", "Contents/MacOS/TsukihimeLauncher has mode 777: the group or others may write it")]
    [InlineData("host", "Contents/MacOS/Tsukihime has mode 644: its owner cannot run it")]
    [InlineData("folder", "Contents/Resources/ has mode 500: its owner cannot read, write and enter it")]
    [InlineData("no mode", "Contents/Resources/plain.txt has mode (none): its owner cannot read and write it")]
    public void MacRefusesLinksSpecialFilesAndModesThatAreNotSafe(string what, string expected)
    {
        string archive = MacZip("zip-kind-" + Name(what), (relative, mode) => what switch
        {
            "setuid" when relative == "Contents/Resources/README.md" => mode | 0x800,
            "writable" when relative == "Contents/MacOS/TsukihimeLauncher" => 0x1FF,
            "host" when relative == "Contents/MacOS/Tsukihime" => 0x1A4,
            "folder" when relative == "Contents/Resources/" => 0x140,
            _ => mode,
        }, what is "link" or "fifo" or "fifo folder" or "no mode" ? zip =>
        {
            if (what == "no mode")
            {
                // an entry without Unix attributes (a ZIP not made by ditto) would be unpacked with no mode at all; .NET on Unix gives new entries 0644
                ZipArchiveEntry plain = zip.CreateEntry("Tsukihime.app/Contents/Resources/plain.txt");
                plain.ExternalAttributes = 0;
                using Stream data = plain.Open();
                data.Write([1]);
                return;
            }

            ZipArchiveEntry added = zip.CreateEntry(what == "fifo folder" ? "Tsukihime.app/Contents/odd/" : "Tsukihime.app/Contents/MacOS/extra.dylib");
            added.ExternalAttributes = MacRelease.Attributes(what == "link" ? 0xA000 : 0x1000, 0x1ED);
        } : null);
        Assert.Contains(expected, Refusal(() => ExtractMac(archive, "zip-kind-out-" + Name(what))), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("entry", "CFBundleExecutable")]
    [InlineData("resources", "CodeResources")]
    [InlineData("unsigned", "Contents/MacOS/TsukihimeLauncher has no code signature")]
    [InlineData("architecture", "is x64, not the release's arm64")]
    [InlineData("player version", "Tsukihime.dll says version")]
    [InlineData("launcher version", "TsukihimeLauncher.dll says version")]
    [InlineData("personal", "holds Contents/MacOS/PERSONAL_BUILD.txt: a personal build is never released")]
    [InlineData("picture", "Contents/MacOS/x.png is a picture")]
    [InlineData("save", "Contents/MacOS/save.dat is game data or a save")]
    [InlineData("developer", "Contents/MacOS/Tsuki.Cli.dll is a developer program")]
    [InlineData("deps", "TsukihimeLauncher.deps.json is missing: the launcher cannot start")]
    [InlineData("avalonia", "Avalonia.Base.dll, listed in TsukihimeLauncher.deps.json, not beside TsukihimeLauncher")]
    public void MacRequiresTheReleasesBundle(string what, string expected)
    {
        string archive = MacZip("zip-bundle-" + Name(what), arch: what == "architecture" ? "x64" : "arm64", prepare: app =>
        {
            string macos = Path.Combine(app, "Contents", "MacOS");
            switch (what)
            {
                case "entry": File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), MacRelease.Plist(Version.ToString()).Replace(">TsukihimeLauncher<", ">Tsukihime<", StringComparison.Ordinal)); break;
                case "resources": File.Delete(Path.Combine(app, "Contents", "_CodeSignature", "CodeResources")); break;
                case "unsigned": File.WriteAllBytes(Path.Combine(macos, "TsukihimeLauncher"), MacRelease.MachO("arm64", 2, signed: false)); break;
                case "player version": File.Copy(typeof(Assert).Assembly.Location, Path.Combine(macos, "Tsukihime.dll"), overwrite: true); break;
                case "launcher version": File.Copy(typeof(Assert).Assembly.Location, Path.Combine(macos, "TsukihimeLauncher.dll"), overwrite: true); break;
                case "personal": File.WriteAllText(Path.Combine(macos, ReleaseCheck.PersonalNote), "personal"); break;
                case "picture": File.WriteAllBytes(Path.Combine(macos, "x.png"), [1]); break;
                case "save": File.WriteAllBytes(Path.Combine(macos, "save.dat"), [1]); break;
                case "developer": File.WriteAllBytes(Path.Combine(macos, "Tsuki.Cli.dll"), [0x4D, 0x5A]); break;
                case "deps": File.Delete(Path.Combine(macos, "TsukihimeLauncher.deps.json")); break;
                case "avalonia": File.Delete(Path.Combine(macos, "Avalonia.Base.dll")); break;
            }
        }, zipArch: "arm64");
        var refused = Assert.Throws<UnixUpdateRefusedException>(() => ExtractMac(archive, "zip-bundle-out-" + Name(what)));
        string refusal = string.Join("; ", refused.Problems);
        Assert.Contains(expected, refusal, StringComparison.Ordinal);
        Assert.Equal(UnixReleaseArchive.RefusedSentence(Path.GetFileName(archive), mac: true), refused.Message);
        Assert.Contains("replace the old app by hand; your saves and settings stay where they are.", refused.Message, StringComparison.Ordinal);
        if (what.EndsWith("version", StringComparison.Ordinal))
        {
            Assert.Single(refused.Problems, problem => problem.Contains("says version", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("licence", "Contents/Resources/licenses/Tmds-DBus-MIT.txt is missing from the Mac release")]
    [InlineData("ffmpeg", "Contents/MacOS/libavcodec.63.dylib is missing")]
    [InlineData("identifier", "has apphost signature identifier Tsukihime-55554944")]
    [InlineData("minimum", "LSMinimumSystemVersion=15.4, expected 15.0")]
    [InlineData("icon", "Contents/Resources/Tsukihime.icns is not a well-formed ICNS file")]
    [InlineData("helper", "Tsukihime.app/Contents/MacOS/tsukihime-helper has mode 755, expected 644")]
    [InlineData("folder", "Tsukihime.app/Contents/Resources/ has mode 700, expected 755")]
    public void AMacReleaseWhoseFilesChangedInstallsButIsNotThisVersionsRelease(string what, string releaseSums)
    {
        // decision D1 of v0.1.7 (reviews crit-5, mac-bundle-1): an installed Mac launcher takes a later release that drops a licence, moves to the next FFmpeg,
        // is signed with other identifiers, needs a newer macOS point release, draws a new icon or adds a file; only release-sums holds a release to this version
        string archive = MacZip("zip-changed-" + Name(what), (relative, mode) => what switch
        {
            "helper" when relative == "Contents/MacOS/tsukihime-helper" => 0x1ED,
            "folder" when relative == "Contents/Resources/" => 0x1C0,
            _ => mode,
        }, prepare: app =>
        {
            string macos = Path.Combine(app, "Contents", "MacOS");
            switch (what)
            {
                case "licence": File.Delete(Path.Combine(app, "Contents", "Resources", "licenses", "Tmds-DBus-MIT.txt")); break;
                case "ffmpeg": File.Move(Path.Combine(macos, "libavcodec.63.dylib"), Path.Combine(macos, "libavcodec.64.dylib")); break;
                case "identifier": File.WriteAllBytes(Path.Combine(macos, "Tsukihime"), MacRelease.MachO("arm64", 2, signatureIdentifier: "Tsukihime-55554944a1b2c3d4")); break;
                case "minimum": File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), MacRelease.Plist(Version.ToString()).Replace(">15.0<", ">15.4<", StringComparison.Ordinal)); break;
                case "icon": File.WriteAllBytes(Path.Combine(app, ReleaseRules.MacIcon), [0x69, 0x63, 0x6E, 0x73, 0, 0, 0, 9]); break;
                case "helper": File.WriteAllText(Path.Combine(macos, "tsukihime-helper"), "#!/bin/sh\n"); break;
            }
        });
        string app = UnixReleaseArchive.ExtractMac(archive, Path.Combine(_root, "zip-changed-out-" + Name(what)), Info(archive), CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(app, "Contents", "MacOS", "TsukihimeLauncher")));
        Assert.Contains(ReleaseProblems(archive), problem => problem.Contains(releaseSums, StringComparison.Ordinal));
    }

    [Fact]
    public void AnIntelMacTakesTheX64ZipAndAnOtherwiseDrawnCrescent()
    {
        // review K48 -02/-08: the x64 asset is checked as x64; the updater does not pin the icon to the installed copy's (release-sums compares it before the upload)
        string other = Path.Combine(_root, "other.icns");
        byte[] icns = MacRelease.Icns();
        icns[^1] ^= 0x5A;
        File.WriteAllBytes(other, icns);
        string archive = MacZip("zip-x64", arch: "x64", prepare: app => File.Copy(other, Path.Combine(app, ReleaseRules.MacIcon), overwrite: true));
        string app = UnixReleaseArchive.ExtractMac(archive, Path.Combine(_root, "zip-x64-out"), Info(archive), CancellationToken.None);
        Assert.Equal(icns, File.ReadAllBytes(Path.Combine(app, ReleaseRules.MacIcon)));
        string arm = MacZip("zip-arm-as-x64", arch: "arm64", zipArch: "x64");
        Assert.Contains("is arm64, not the release's x64", Refusal(() => ExtractMac(arm, "zip-arm-as-x64-out")), StringComparison.Ordinal);
        string wrongName = MacZip("zip-wrong-name", zipName: $"Tsukihime-v{Version}-macos-universal.zip");
        Assert.Contains("is not a Mac release of", Assert.Throws<UpdateRefusedException>(() => ExtractMac(wrongName, "zip-wrong-name-out")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MacHoldsTheZipToTheSizeAndEntryLimits()
    {
        string archive = MacZip("zip-limits", extra: zip => MacRelease.AddSequesteredMetadata(zip, ["Contents/MacOS/Tsukihime.dll"]));
        using ZipArchive zip = ZipFile.OpenRead(archive);
        long largest = zip.Entries.Max(e => e.Length);
        long sum = zip.Entries.Sum(e => e.Length);
        int entries = zip.Entries.Count;
        string Out(string name) => Path.Combine(_root, "zip-limits-" + name);
        Assert.True(Directory.Exists(UnixReleaseArchive.ExtractMac(archive, Out("ok"), Info(archive), CancellationToken.None, limits: new ReleaseLimits(largest, sum, entries))));
        Assert.Contains("exceeds the", Refusal(() => UnixReleaseArchive.ExtractMac(archive, Out("file"), Info(archive), CancellationToken.None, limits: new ReleaseLimits(largest - 1, sum, entries))), StringComparison.Ordinal);
        Assert.Contains("exceeds the", Refusal(() => UnixReleaseArchive.ExtractMac(archive, Out("total"), Info(archive), CancellationToken.None, limits: new ReleaseLimits(largest, sum - 1, entries))), StringComparison.Ordinal);
        Assert.Contains($"more than {entries - 1} entries", Refusal(() => UnixReleaseArchive.ExtractMac(archive, Out("entries"), Info(archive), CancellationToken.None, limits: new ReleaseLimits(largest, sum, entries - 1))), StringComparison.Ordinal);
    }

    [Fact]
    public void OnAMacDittoUnpacksTheCheckedZipAndCodesignHasTheLastWord()
    {
        // what only a Mac runs (ditto -x -k, codesign --verify), through stand-ins: they run after the .NET checks, and what ditto unpacked must be the checked entries
        string archive = MacZip("zip-tools", extra: zip => MacRelease.AddSequesteredMetadata(zip, ["Contents/MacOS/Tsukihime.dll"]));
        var calls = new List<string>();
        var tools = new MacTools((zip, folder) => { calls.Add("unpack " + Path.GetFileName(zip)); UnixUpdateTestKit.Ditto(zip, folder); return null; }, app => { calls.Add("verify " + Path.GetFileName(app)); return null; });
        string app = UnixReleaseArchive.ExtractMac(archive, Path.Combine(_root, "zip-tools-ok"), Info(archive), CancellationToken.None, tools);
        Assert.Equal(["unpack " + Path.GetFileName(archive), "verify Tsukihime.app"], calls);
        Assert.True(File.Exists(Path.Combine(app, "Contents", "MacOS", "TsukihimeLauncher.dll")));

        string Refused(MacTools with, string name) => Refusal(() => UnixReleaseArchive.ExtractMac(archive, Path.Combine(_root, "zip-tools-" + name), Info(archive), CancellationToken.None, with));
        Assert.Equal("the downloaded app fails codesign verification: test", Refused(tools with { Verify = _ => "the downloaded app fails codesign verification: test" }, "unsigned"));
        Assert.Equal("ditto could not unpack the downloaded app: test", Refused(tools with { Unpack = (_, _) => "ditto could not unpack the downloaded app: test" }, "ditto"));
        Assert.Contains("Contents/Resources/extra.txt was unpacked but is not an entry of the ZIP",
            Refused(tools with { Unpack = (zip, folder) => { UnixUpdateTestKit.Ditto(zip, folder); File.WriteAllText(Path.Combine(folder, "Tsukihime.app", "Contents", "Resources", "extra.txt"), "x"); return null; } }, "extra"), StringComparison.Ordinal);
        Assert.Contains("Contents/Resources/README.md is missing after unpacking",
            Refused(tools with { Unpack = (zip, folder) => { UnixUpdateTestKit.Ditto(zip, folder); File.Delete(Path.Combine(folder, "Tsukihime.app", "Contents", "Resources", "README.md")); return null; } }, "missing"), StringComparison.Ordinal);
        Assert.Contains("__MACOSX was unpacked beside Tsukihime.app",
            Refused(tools with { Unpack = (zip, folder) => { ZipFile.ExtractToDirectory(zip, folder); return null; } }, "metadata"), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains("Contents/MacOS/TsukihimeLauncher was unpacked with the mode 644: its owner cannot run it", Refused(tools with
            {
                Unpack = (zip, folder) => { UnixUpdateTestKit.Ditto(zip, folder); UnixUpdateTestKit.Chmod(Path.Combine(folder, "Tsukihime.app", "Contents", "MacOS", "TsukihimeLauncher"), ReleaseRules.Mode644); return null; },
            }, "mode"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ACancelWhileDittoRunsEndsTheMacUnpackingBeforeCodesign()
    {
        // review mac-update-3: Cancel during "Unpacking" used to be ignored, and the update went in
        string archive = MacZip("zip-cancel");
        using var cancel = new CancellationTokenSource();
        bool verified = false;
        var tools = new MacTools((zip, folder) => { UnixUpdateTestKit.Ditto(zip, folder); cancel.Cancel(); return null; }, _ => { verified = true; return null; });
        Assert.ThrowsAny<OperationCanceledException>(() => UnixReleaseArchive.ExtractMac(archive, Path.Combine(_root, "zip-cancel-out"), Info(archive), cancel.Token, tools));
        Assert.False(verified);
    }

    [Fact]
    public void TheMacToolRunnerReportsWhatTheProgramSaid()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("the runner starts /bin/sh, as it starts /usr/bin/ditto and /usr/bin/codesign on a Mac"); }
        Assert.Null(MacTools.Run("/bin/sh", ["-c", "exit 0"], "failed"));
        Assert.Equal("failed: bad", MacTools.Run("/bin/sh", ["-c", "echo bad >&2; exit 3"], "failed"));
        Assert.StartsWith("missing-program could not run", MacTools.Run(Path.Combine(_root, "missing-program"), [], "failed"), StringComparison.Ordinal);
        Assert.Equal(OperatingSystem.IsMacOS(), MacTools.ForHost() is not null);
    }

    private string Tar(string name, Func<string, UnixFileMode, UnixFileMode>? mode = null, Action<string>? prepare = null, Action<TarWriter, string>? extra = null)
    {
        string folder = LinuxRelease.Folder(_root, name);
        prepare?.Invoke(folder);
        return LinuxRelease.Tar(folder, Path.Combine(_root, name + ".tar.gz"), mode, extra);
    }

    private string MacZip(string name, Func<string, int, int>? mode = null, Action<ZipArchive>? extra = null, string arch = "arm64", Action<string>? prepare = null, string? zipArch = null, string? zipName = null)
    {
        string app = MacRelease.Folder(_root, arch, name);
        prepare?.Invoke(app);
        return MacRelease.Zip(app, Path.Combine(_root, name + "-zip", zipName ?? $"Tsukihime-v{Version}-macos-{zipArch ?? arch}.zip"), mode, extra, directoryMode: mode);
    }

    private static void AddFile(ZipArchive zip, string name, byte[]? data = null, int kind = 0x8000, int permissions = 0x1A4)
    {
        ZipArchiveEntry added = zip.CreateEntry(name);
        added.ExternalAttributes = MacRelease.Attributes(kind, permissions);
        using Stream stream = added.Open();
        stream.Write(data ?? [1]);
    }

    /// <summary>What release-sums holds the archive to (<see cref="ReleaseRuleSet.Complete"/>; release-sums also compares the assemblies' icons).</summary>
    private List<string> ReleaseProblems(string archive)
    {
        var problems = new List<string>();
        string folder = Path.Combine(_root, "complete-" + Guid.NewGuid().ToString("N")[..8]);
        if (archive.EndsWith(".tar.gz", StringComparison.Ordinal))
        {
            string root = ReleaseRules.UnpackLinuxTar(archive, folder, problems);
            if (problems.Count == 0) { ReleaseRules.CheckLinuxFolder(root, problems, []); }
        }
        else
        {
            string app = ReleaseRules.UnpackMacZip(archive, folder, problems);
            string arch = archive.EndsWith("-x64.zip", StringComparison.Ordinal) ? "x64" : "arm64";
            if (problems.Count == 0) { ReleaseRules.CheckMacFolder(app, arch, MacRelease.Icns(), Version.ToString(), problems, [], requireCodeSignature: true); }
        }

        return problems;
    }

    private static long LargestEntry(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Max(f => new FileInfo(f).Length);

    /// <summary>The reasons launcher.log gets for a refused archive (the player's sentence is the exception's message).</summary>
    private static string Refusal(Action extract) => string.Join("; ", Assert.Throws<UnixUpdateRefusedException>(extract).Problems);

    private string Extract(string archive, string output) => UnixReleaseArchive.ExtractLinux(archive, Path.Combine(_root, output), Version, CancellationToken.None).Root;

    private void ExtractMac(string archive, string output) => UnixReleaseArchive.ExtractMac(archive, Path.Combine(_root, output), Info(archive), CancellationToken.None);

    private static ReleaseInfo Info(string archive) => new(Version, Version.Tag, "test", "notes", null, false,
        new ReleaseAsset(Path.GetFileName(archive), new Uri("http://127.0.0.1/archive"), new FileInfo(archive).Length),
        new ReleaseAsset(UpdateSource.SumsAssetName, new Uri("http://127.0.0.1/sums"), 1));

    private static string Name(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}

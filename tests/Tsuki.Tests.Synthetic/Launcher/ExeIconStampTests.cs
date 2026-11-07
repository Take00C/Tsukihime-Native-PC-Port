using System.Diagnostics;
using Tsuki.App.Services;
using Tsuki.Data.Icons;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>
/// Package K43: the launcher writes an icon into the installed Tsukihime.exe and TsukihimeLauncher.exe (Arcueid's face from the player's files; the crescent again
/// when the switch is off). Every test works on COPIES of the real apphosts of this build (the launcher's build output beside the tests, as the update's end-to-end
/// tests find it) in a temporary folder, with a made-up seven-size icon in place of her face (no game data here): the images read back, nothing else of the program
/// changes, a second run writes nothing, the crescent comes back exactly as the compiler wrote it, a failure at any step leaves the program byte-identical, the
/// clean-up repairs a swap that was cut off, and the stamped programs still start.
/// </summary>
public sealed class ExeIconStampTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-k43-stamp-" + Guid.NewGuid().ToString("N"));

    public ExeIconStampTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (string file in Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*", SearchOption.AllDirectories) : [])
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a launcher that has not quite ended; the temp folder is the system's to clean
        }
    }

    /// <summary>The port's crescent (assets/icon/tsukihime.ico, what a release's programs carry).</summary>
    private static IconChoice Crescent { get; } = new("the port's crescent", IcoFile.Read(File.ReadAllBytes(Path.Combine(Repo.Root, "assets", "icon", "tsukihime.ico")))!);

    /// <summary>A made-up icon in the seven sizes of the Arcueid icon (FaceIcon.Sizes), every pixel different per seed.</summary>
    internal static byte[] FakeIco(int seed)
    {
        var frames = new List<IconFrame>();
        foreach (int size in FaceIcon.Sizes)
        {
            byte[] rgba = new byte[size * size * 4];
            for (int i = 0; i < size * size; i++)
            {
                rgba[i * 4] = (byte)((i * 7) + seed);
                rgba[(i * 4) + 1] = (byte)(size + (seed * 31));
                rgba[(i * 4) + 2] = (byte)(i >> 3);
                rgba[(i * 4) + 3] = (byte)(i % 5 == 0 ? 0 : 255);
            }

            frames.Add(new IconFrame(size, rgba));
        }

        return FaceIcon.EncodeIco(frames);
    }

    private static IconChoice Face(int seed = 1) => new("the test face", IcoFile.Read(FakeIco(seed))!);

    private static IconChoice[] Known(IconChoice face) => [Crescent, face];

    /// <summary>
    /// The two programs of this build and their libraries, copied into a folder of the test (nothing else of the output: the stamp only needs the program and its
    /// .dll beside it). With <paramref name="runnable"/> the whole runnable set comes along (the launcher's libraries and SDL3 for the game's --help).
    /// </summary>
    private string Install(string name = "Tsukihime", bool runnable = false)
    {
        string? output = UpdateEndToEndTests.LauncherOutput();
        if (output is null)
        {
            Assert.Skip("the launcher's build output is not beside the tests (build src/Tsuki.slnx)");
        }

        string to = Path.Combine(_dir, name);
        if (runnable)
        {
            // the update's copy leaves out SDL3 (managed binding and native library), which the game's exit path (SDL_Quit) needs even after --help
            UpdateEndToEndTests.CopyInstall(output, to);
            foreach (string sdl in Directory.GetFiles(output, "SDL3*.dll"))
            {
                File.Copy(sdl, Path.Combine(to, Path.GetFileName(sdl)), overwrite: true);
            }

            string native = Path.Combine(output, "runtimes", "win-x64", "native");
            if (Directory.Exists(native))
            {
                Directory.CreateDirectory(Path.Combine(to, "runtimes", "win-x64", "native"));
                foreach (string file in Directory.GetFiles(native))
                {
                    File.Copy(file, Path.Combine(to, "runtimes", "win-x64", "native", Path.GetFileName(file)));
                }
            }

            return to;
        }

        Directory.CreateDirectory(to);
        foreach (string file in new[] { "Tsukihime.exe", "Tsukihime.dll", "TsukihimeLauncher.exe", "TsukihimeLauncher.dll" })
        {
            File.Copy(Path.Combine(output, file), Path.Combine(to, file));
        }

        return to;
    }

    private static string Sha(string file) => ReleaseSums.Sha256Hex(file);

    private static string[] Names(string folder) => [.. Directory.GetFiles(folder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    private static byte[]? Resource(string exe, int type, int id = 1) => PeIcons.ResourceData(exe, type, id, 0);

    [Fact]
    public void TheStampWritesTheNewImagesAndNothingElseOfTheProgramChanges()
    {
        string app = Install();
        IconChoice face = Face();
        var before = ExeIconStamp.Programs.ToDictionary(p => p, p => Path.Combine(app, p));
        Dictionary<string, (byte[]? Version, byte[]? Manifest, string Dll, long Length)> was = before.ToDictionary(
            kv => kv.Key,
            kv => (Resource(kv.Value, PeIcons.VersionType), Resource(kv.Value, PeIcons.ManifestType), Sha(Path.ChangeExtension(kv.Value, ".dll")), new FileInfo(kv.Value).Length));
        foreach (string exe in before.Values)
        {
            Assert.True(IcoFile.SameImages(ExeIconStamp.ReadIcon(exe), Crescent.Images), exe + " starts as a release does, with the crescent");
            Assert.Null(ExeIconStamp.ApphostProblem(exe));
        }

        IReadOnlyList<StampResult> results = ExeIconStamp.StampFolder(app, face, Known(face));

        Assert.Equal(ExeIconStamp.Programs, results.Select(r => r.Program));
        Assert.All(results, r => Assert.Equal(StampOutcome.Stamped, r.Outcome));
        Assert.All(results, r => Assert.Equal("the port's crescent (10 images)", r.Before));
        Assert.All(results, r => Assert.Equal("the test face (7 images)", r.After));
        foreach ((string program, string exe) in before)
        {
            // the images read back exactly (every byte, in order), and nothing else of the program changed: version block, manifest, its .dll, still a plain apphost
            IReadOnlyList<IcoImage>? now = ExeIconStamp.ReadIcon(exe);
            Assert.True(IcoFile.SameImages(now, face.Images), program);
            Assert.Equal(FaceIcon.Sizes, now!.Select(i => i.Size));
            Assert.Equal(was[program].Version, Resource(exe, PeIcons.VersionType));
            Assert.Equal(was[program].Manifest, Resource(exe, PeIcons.ManifestType));
            Assert.Equal(was[program].Dll, Sha(Path.ChangeExtension(exe, ".dll")));
            Assert.Null(ExeIconStamp.ApphostProblem(exe));
            Assert.Null(PeIcons.Marks(exe));                                                    // still the native apphost, no managed metadata
            Assert.Equal([PeIcons.ApplicationIconGroup], PeIcons.IconGroupIds(exe));             // the game window's class takes this group (GameWindow)
            Assert.InRange(new FileInfo(exe).Length, was[program].Length, was[program].Length + 400_000);

            // one group in language 0 naming exactly the RT_ICON images there are
            IReadOnlyList<PeResource> resources = PeIcons.ResourceList(exe)!;
            PeResource group = Assert.Single(resources, r => r.Type == PeIcons.GroupIconType);
            Assert.Equal(0, group.Language);
            IReadOnlyList<int> ids = IcoFile.GroupImageIds(Resource(exe, PeIcons.GroupIconType, PeIcons.ApplicationIconGroup))!;
            Assert.Equal(resources.Where(r => r.Type == PeIcons.IconType).Select(r => r.Id).Order(), ids.Order());
            Assert.Equal([1, 2, 3, 4, 5, 6, 7], ids);
        }

        // the dll that decides "personal" for the updater is untouched, so a stamped release still counts as a release
        AssemblyMarks? marks = PeIcons.Marks(Path.Combine(app, "TsukihimeLauncher.dll"));
        Assert.NotNull(marks);
        Assert.False(marks.IsPersonalBuild);
        Assert.DoesNotContain("-personal", marks.InformationalVersion ?? string.Empty, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(app, UpdateInstaller.PersonalNote)));
        Assert.Empty(Directory.GetFiles(app, "*.icon-*"));
    }

    [Fact]
    public void ASecondRunWritesNothing()
    {
        string app = Install();
        IconChoice face = Face();
        Assert.All(ExeIconStamp.StampFolder(app, face, Known(face)), r => Assert.Equal(StampOutcome.Stamped, r.Outcome));
        Dictionary<string, (string Sha, DateTime Time)> first = ExeIconStamp.Programs.ToDictionary(p => p, p => (Sha(Path.Combine(app, p)), File.GetLastWriteTimeUtc(Path.Combine(app, p))));
        string[] filesBefore = [.. Directory.GetFiles(app).Order(StringComparer.Ordinal)];

        IReadOnlyList<StampResult> again = ExeIconStamp.StampFolder(app, face, Known(face));

        Assert.All(again, r => Assert.Equal(StampOutcome.Unchanged, r.Outcome));
        Assert.All(again, r => Assert.Equal("Tsukihime.exe: already the test face (7 images), nothing written".Replace("Tsukihime.exe", r.Program, StringComparison.Ordinal), r.Describe()));
        foreach (string p in ExeIconStamp.Programs)
        {
            Assert.Equal(first[p], (Sha(Path.Combine(app, p)), File.GetLastWriteTimeUtc(Path.Combine(app, p))));
        }

        Assert.Equal(filesBefore, Directory.GetFiles(app).Order(StringComparer.Ordinal));                // not even the write probe of the folder
    }

    [Fact]
    public void OffPutsTheCrescentBackAsTheCompilerWroteItAndOnStampsAgain()
    {
        string app = Install();
        string game = Path.Combine(app, "Tsukihime.exe");
        byte[] compilerGroup = Resource(game, PeIcons.GroupIconType, PeIcons.ApplicationIconGroup)!;
        byte[][] compilerImages = [.. Enumerable.Range(1, 10).Select(id => Resource(game, PeIcons.IconType, id)!)];
        IconChoice face = Face();

        Assert.All(ExeIconStamp.StampFolder(app, face, Known(face)), r => Assert.Equal(StampOutcome.Stamped, r.Outcome));
        IReadOnlyList<StampResult> off = ExeIconStamp.StampFolder(app, Crescent, Known(face));
        Assert.All(off, r => Assert.Equal(StampOutcome.Stamped, r.Outcome));
        Assert.All(off, r => Assert.Equal("the port's crescent (10 images)", r.After));

        // the group and its ten images are byte for byte the ones the C# compiler made from assets/icon/tsukihime.ico (so release-check's comparison passes again)
        Assert.Equal(compilerGroup, Resource(game, PeIcons.GroupIconType, PeIcons.ApplicationIconGroup));
        for (int id = 1; id <= 10; id++)
        {
            Assert.Equal(compilerImages[id - 1], Resource(game, PeIcons.IconType, id));
        }

        Assert.Equal(compilerGroup, IcoFile.GroupDirectory(Crescent.Images, [.. Enumerable.Range(1, 10)]));
        Assert.Equal(10, PeIcons.ResourceList(game)!.Count(r => r.Type == PeIcons.IconType));

        IconChoice other = Face(seed: 9);
        IReadOnlyList<StampResult> on = ExeIconStamp.StampFolder(app, other, [Crescent, face, other]);
        Assert.All(on, r => Assert.Equal(StampOutcome.Stamped, r.Outcome));
        Assert.All(on, r => Assert.True(IcoFile.SameImages(ExeIconStamp.ReadIcon(Path.Combine(app, r.Program)), other.Images)));
        Assert.Equal(7, PeIcons.ResourceList(game)!.Count(r => r.Type == PeIcons.IconType));
    }

    [Theory]
    [InlineData("Tsukihime.exe", StampStep.Copied)]
    [InlineData("Tsukihime.exe", StampStep.Updated)]
    [InlineData("Tsukihime.exe", StampStep.Verified)]
    [InlineData("TsukihimeLauncher.exe", StampStep.Copied)]
    [InlineData("TsukihimeLauncher.exe", StampStep.Updated)]
    [InlineData("TsukihimeLauncher.exe", StampStep.Verified)]
    [InlineData("TsukihimeLauncher.exe", StampStep.Renamed)]
    public void AFailureAtAnyStepLeavesTheProgramByteIdentical(string program, StampStep failAt)
    {
        // the game is replaced in one step (no Renamed step); the launcher, which runs, is renamed first and the copy moved in
        string app = Install();
        string exe = Path.Combine(app, program);
        string sha = Sha(exe);
        IconChoice face = Face();

        StampResult result = ExeIconStamp.StampExe(exe, face, Known(face), onStep: (_, step) =>
        {
            if (step == failAt)
            {
                throw new IOException($"made to fail at {step}");
            }
        });

        Assert.Equal(StampOutcome.Failed, result.Outcome);
        Assert.Contains($"made to fail at {failAt}", result.Detail, StringComparison.Ordinal);
        Assert.StartsWith($"{program}: NOT changed, it is as it was (the port's crescent (10 images))", result.Describe(), StringComparison.Ordinal);
        Assert.Equal(StampReason.Error, result.Reason);
        Assert.Equal(sha, Sha(exe));
        Assert.Equal(["Tsukihime.dll", "Tsukihime.exe", "TsukihimeLauncher.dll", "TsukihimeLauncher.exe"], Names(app));
    }

    [Fact]
    public void ALockedCopyOrAMoveInThatFailsLeavesTheOriginalAndTheCleanUpRemovesTheCopy()
    {
        string app = Install();
        string exe = Path.Combine(app, "Tsukihime.exe");
        string sha = Sha(exe);
        IconChoice face = Face();

        // a scanner (or anyone) holding the new copy from the moment it exists: the resource update cannot open it, is tried again, and gives up
        FileStream? held = null;
        StampResult locked = ExeIconStamp.StampExe(exe, face, Known(face), onStep: (_, step) =>
        {
            if (step == StampStep.Copied && held is null)
            {
                held = new FileStream(exe + ExeIconStamp.NewSuffix, FileMode.Open, FileAccess.Read, FileShare.None);
            }
        });
        Assert.Equal(StampOutcome.Failed, locked.Outcome);
        Assert.Equal(sha, Sha(exe));
        held!.Dispose();

        // the game's copy held while it should replace the game in one step: the game was never renamed and is as it was
        held = null;
        StampResult replace = ExeIconStamp.StampExe(exe, face, Known(face), onStep: (_, step) =>
        {
            if (step == StampStep.Verified)
            {
                held = new FileStream(exe + ExeIconStamp.NewSuffix, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            Assert.NotEqual(StampStep.Renamed, step);
        });
        Assert.Equal(StampOutcome.Failed, replace.Outcome);
        Assert.Equal(sha, Sha(exe));
        Assert.False(File.Exists(exe + ExeIconStamp.OldSuffix));
        held!.Dispose();

        // the launcher's copy held while it is being moved in (after the launcher was renamed): the launcher is renamed back
        string launcher = Path.Combine(app, "TsukihimeLauncher.exe");
        string launcherSha = Sha(launcher);
        held = null;
        StampResult moveIn = ExeIconStamp.StampExe(launcher, face, Known(face), onStep: (_, step) =>
        {
            if (step == StampStep.Renamed)
            {
                Assert.False(File.Exists(launcher));
                held = new FileStream(launcher + ExeIconStamp.NewSuffix, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        });
        Assert.Equal(StampOutcome.Failed, moveIn.Outcome);
        Assert.Equal(launcherSha, Sha(launcher));
        Assert.False(File.Exists(launcher + ExeIconStamp.OldSuffix));
        Assert.True(File.Exists(launcher + ExeIconStamp.NewSuffix), "the held copy could not be deleted yet");
        held!.Dispose();

        StampCleanup cleanup = ExeIconStamp.CleanLeftovers(app);
        Assert.Empty(cleanup.Repaired);
        Assert.Equal((2, 0), (cleanup.Deleted, cleanup.Left));
        Assert.Empty(Directory.GetFiles(app, "*.icon-*"));
        Assert.Equal(sha, Sha(exe));
        Assert.Equal(launcherSha, Sha(launcher));
    }

    [Fact]
    public void AProgramThatMustNotBeWrittenIsLeftAlone()
    {
        string app = Install();
        string game = Path.Combine(app, "Tsukihime.exe");
        string launcher = Path.Combine(app, "TsukihimeLauncher.exe");
        IconChoice face = Face();

        // the game of this folder running
        StampResult running = ExeIconStamp.StampExe(game, face, Known(face), running: () => true);
        Assert.Equal((StampOutcome.Skipped, StampReason.GameRunning), (running.Outcome, running.Reason));
        Assert.Contains("the game of this folder is running", running.Detail, StringComparison.Ordinal);

        // a read-only program
        string sha = Sha(launcher);
        File.SetAttributes(launcher, FileAttributes.ReadOnly);
        StampResult readOnly = ExeIconStamp.StampExe(launcher, face, Known(face));
        Assert.Equal((StampOutcome.Skipped, "the file is read-only", StampReason.NotStampable), (readOnly.Outcome, readOnly.Detail, readOnly.Reason));
        Assert.Equal(sha, Sha(launcher));
        File.SetAttributes(launcher, FileAttributes.Normal);

        // an unfinished update of the folder: nothing is touched until its clean-up is done
        Directory.CreateDirectory(Path.Combine(app, UpdateStaging.FolderName));
        IReadOnlyList<StampResult> updating = ExeIconStamp.StampFolder(app, face, Known(face));
        Assert.All(updating, r => Assert.Equal(StampOutcome.Skipped, r.Outcome));
        Assert.All(updating, r => Assert.Contains("an update of this folder is not finished", r.Detail, StringComparison.Ordinal));
        Assert.All(updating, r => Assert.Equal(StampReason.UpdateUnfinished, r.Reason));
        Directory.Delete(Path.Combine(app, UpdateStaging.FolderName));
        Assert.All(ExeIconStamp.Programs, p => Assert.True(IcoFile.SameImages(ExeIconStamp.ReadIcon(Path.Combine(app, p)), Crescent.Images)));
    }

    [Fact]
    public void OnlyAPlainApphostWithItsLibraryBesideItIsStamped()
    {
        string app = Install();
        string game = Path.Combine(app, "Tsukihime.exe");
        byte[] bytes = File.ReadAllBytes(game);
        IconChoice face = Face();

        string text = Path.Combine(_dir, "notes.exe");
        File.WriteAllText(text, "not a program");
        Assert.Equal("it is not a Windows program", ExeIconStamp.ApphostProblem(text));
        Assert.Equal("it is a .NET assembly, not the program that starts one", ExeIconStamp.ApphostProblem(Path.Combine(app, "Tsukihime.dll")));

        // data after the last section: a single-file bundle or an installer, which a resource update would cut off
        string overlay = Path.Combine(_dir, "overlay", "Tsukihime.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
        File.WriteAllBytes(overlay, [.. bytes, .. new byte[4096]]);
        File.Copy(Path.Combine(app, "Tsukihime.dll"), Path.ChangeExtension(overlay, ".dll"));
        Assert.Equal("it carries 4096 bytes after its last section (a single-file bundle or an installer)", ExeIconStamp.ApphostProblem(overlay));

        // the bundle header offset set beside the marker: a single-file bundle
        string bundle = Path.Combine(_dir, "bundle", "Tsukihime.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
        byte[] marked = [.. bytes];
        int marker = marked.AsSpan().IndexOf(new byte[] { 0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32 });
        Assert.True(marker > 8, "the apphost carries the bundle marker");
        Assert.Equal(0L, BitConverter.ToInt64(marked, marker - 8));
        BitConverter.GetBytes(123456L).CopyTo(marked, marker - 8);
        File.WriteAllBytes(bundle, marked);
        File.Copy(Path.Combine(app, "Tsukihime.dll"), Path.ChangeExtension(bundle, ".dll"));
        Assert.Equal("it is a single-file bundle", ExeIconStamp.ApphostProblem(bundle));
        StampResult refused = ExeIconStamp.StampExe(bundle, face, Known(face));
        Assert.Equal((StampOutcome.Skipped, "it is a single-file bundle"), (refused.Outcome, refused.Detail));
        Assert.Equal(marked, File.ReadAllBytes(bundle));

        // a program without its .dll beside it
        string alone = Path.Combine(_dir, "alone", "Tsukihime.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(alone)!);
        File.WriteAllBytes(alone, bytes);
        Assert.Equal("Tsukihime.dll is not beside it", ExeIconStamp.ApphostProblem(alone));
        Assert.Equal(StampOutcome.Skipped, ExeIconStamp.StampExe(alone, face, Known(face)).Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(alone));
    }

    [Fact]
    public void TheCleanUpPutsBackAProgramASwapLeftMissingAndDeletesTheOldCopies()
    {
        string app = Install();
        string game = Path.Combine(app, "Tsukihime.exe");
        string launcher = Path.Combine(app, "TsukihimeLauncher.exe");
        string sha = Sha(game);

        // power lost between the two renames: the program is only there as .icon-old, the checked copy as .icon-new
        File.Move(game, game + ExeIconStamp.OldSuffix);
        File.Copy(launcher, game + ExeIconStamp.NewSuffix);
        // a stamped launcher whose old images are left from two stamps of one session, and files that only look similar
        File.Copy(launcher, launcher + ExeIconStamp.OldSuffix);
        File.Copy(launcher, launcher + ".2" + ExeIconStamp.OldSuffix);
        File.WriteAllText(Path.Combine(app, "notes.icon-old"), "the player's");
        File.WriteAllText(Path.Combine(app, "TsukihimeLauncher.exe.backup.icon-old"), "the player's too");
        File.WriteAllText(Path.Combine(app, "Tsukihime.exe.old"), "the updater's, not this one's");

        StampCleanup cleanup = ExeIconStamp.CleanLeftovers(app);

        Assert.Equal(["Tsukihime.exe"], cleanup.Repaired);
        Assert.Equal((3, 0), (cleanup.Deleted, cleanup.Left));
        Assert.Equal(sha, Sha(game));
        Assert.Equal(
            ["Tsukihime.dll", "Tsukihime.exe", "Tsukihime.exe.old", "TsukihimeLauncher.dll", "TsukihimeLauncher.exe", "TsukihimeLauncher.exe.backup.icon-old", "notes.icon-old"],
            Names(app));
        StampCleanup again = ExeIconStamp.CleanLeftovers(app);
        Assert.Equal((0, 0, 0), (again.Repaired.Count, again.Deleted, again.Left));
    }

    [Fact]
    public void AnOldCopyStillInUseGetsAnotherNameAndGoesAtTheNextStart()
    {
        string app = Install();
        string launcher = Path.Combine(app, "TsukihimeLauncher.exe");
        IconChoice face = Face();
        Assert.Equal(StampOutcome.Stamped, ExeIconStamp.StampExe(launcher, face, Known(face)).Outcome);

        // the first stamp's old image is the running launcher (held without delete sharing, as a running image cannot be deleted): the second stamp of the session goes on
        File.Copy(Path.Combine(app, "Tsukihime.exe"), launcher + ExeIconStamp.OldSuffix);
        using (new FileStream(launcher + ExeIconStamp.OldSuffix, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            StampResult second = ExeIconStamp.StampExe(launcher, Crescent, Known(face));
            Assert.Equal(StampOutcome.Stamped, second.Outcome);
            Assert.True(File.Exists(launcher + ExeIconStamp.OldSuffix));
            Assert.False(File.Exists(launcher + ".2" + ExeIconStamp.OldSuffix), "the second old copy was not in use and is deleted at once");
            Assert.Equal(1, ExeIconStamp.CleanLeftovers(app).Left);
        }

        StampCleanup next = ExeIconStamp.CleanLeftovers(app);
        Assert.Equal((0, 1, 0), (next.Repaired.Count, next.Deleted, next.Left));
        Assert.True(IcoFile.SameImages(ExeIconStamp.ReadIcon(launcher), Crescent.Images));
    }

    [Fact]
    public async Task TheSwapWaitsWhilePlayHoldsTheGate()
    {
        string app = Install();
        string game = Path.Combine(app, "Tsukihime.exe");
        string sha = Sha(game);
        IconChoice face = Face();
        Task<StampResult> stamp;
        using (ExeIconStamp.SwapGate.EnterScope())
        {
            stamp = Task.Run(() => ExeIconStamp.StampExe(game, face, Known(face)));
            var clock = Stopwatch.StartNew();
            while (!File.Exists(game + ExeIconStamp.NewSuffix) && clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                Thread.Sleep(20);
            }

            Thread.Sleep(300);
            Assert.False(stamp.IsCompleted);
            Assert.Equal(sha, Sha(game));                                                       // PLAY would start the program as it was
        }

        Assert.Equal(StampOutcome.Stamped, (await stamp).Outcome);
        Assert.True(IcoFile.SameImages(ExeIconStamp.ReadIcon(game), face.Images));
    }

    [Fact]
    public void TheStampedProgramsStillStartAndTheLauncherPutsTheCrescentBackInItsOwnRunningFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
        }

        string app = Install(runnable: true);
        string game = Path.Combine(app, "Tsukihime.exe");
        string launcher = Path.Combine(app, "TsukihimeLauncher.exe");
        IconChoice face = Face();
        Assert.All(ExeIconStamp.StampFolder(app, face, Known(face)), r => Assert.Equal(StampOutcome.Stamped, r.Outcome));

        // the stamped game: the apphost still finds and runs its .dll (--help prints the usage and ends; no window, no game folder, no profile)
        var help = new ProcessStartInfo(game) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        help.ArgumentList.Add("--help");
        help.Environment.Remove("TSUKI_GAME_DIR");
        using (Process p = Process.Start(help)!)
        {
            string output = p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(60_000));
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("usage: Tsukihime", output, StringComparison.Ordinal);
        }

        // the stamped launcher, in its verification mode, writes the crescent back into its own folder while it runs from it: the game's program is replaced and
        // its old copy deleted, the running launcher is renamed (its old image stays until the next start), a second run writes nothing, both programs start
        string profile = Path.Combine(_dir, "profile");
        string report = Path.Combine(_dir, "stamp.txt");
        var info = new ProcessStartInfo(launcher) { UseShellExecute = false, WorkingDirectory = app };
        foreach (string a in new[] { "--stamp-selftest", report, "--data", profile, "--stamp-off" })
        {
            info.ArgumentList.Add(a);
        }

        info.Environment.Remove("TSUKI_GAME_DIR");
        using (Process p = Process.Start(info)!)
        {
            Assert.True(p.WaitForExit(180_000), "the stamp self-test ended");
            string text = File.Exists(report) ? File.ReadAllText(report) : "(no report)";
            Assert.True(p.ExitCode == 0, text);
            Assert.DoesNotContain("FAIL", text, StringComparison.Ordinal);
            Assert.Contains("info  before: Tsukihime.exe: another icon (7 images, 16, 24, 32, 48, 64, 128, 256 px)", text, StringComparison.Ordinal);
            Assert.Contains("ok    Tsukihime.exe carries the port's crescent", text, StringComparison.Ordinal);
            Assert.Contains("ok    TsukihimeLauncher.exe carries the port's crescent", text, StringComparison.Ordinal);
            Assert.Contains("ok    a second run left TsukihimeLauncher.exe as it was", text, StringComparison.Ordinal);
            Assert.Contains("ok    the stamped Tsukihime.exe starts and ends", text, StringComparison.Ordinal);
            Assert.Contains("ok    the stamped TsukihimeLauncher.exe starts and passes its own --selftest", text, StringComparison.Ordinal);
            Assert.Contains("program icons (the game folder was read, the port's crescent): Tsukihime.exe: the port's crescent (10 images) written", text, StringComparison.Ordinal);
        }

        Assert.All(ExeIconStamp.Programs, p => Assert.True(IcoFile.SameImages(ExeIconStamp.ReadIcon(Path.Combine(app, p)), Crescent.Images), p));
        Assert.False(File.Exists(game + ExeIconStamp.OldSuffix));
        Assert.True(AppSettings.Load(Path.Combine(profile, AppSettings.FileName)).ArcueidIcon is false, "the self-test turned the switch off in its own profile");

        // the next start removes the old image of the launcher that ran the self-test (it has ended now)
        var clock = Stopwatch.StartNew();
        StampCleanup cleanup;
        while ((cleanup = ExeIconStamp.CleanLeftovers(app)).Left > 0 && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(200);
        }

        Assert.Equal(0, cleanup.Left);
        Assert.Empty(Directory.GetFiles(app, "*.icon-*"));
    }

    [Fact]
    public void ASwapThatCanGiveTheLauncherBackNeitherFileKeepsBothAndTheNextStampPutsItBack()
    {
        // review of K43: the move-in failed and the rename back failed too (a scanner without delete sharing holds both files); the old code then deleted the checked
        // copy, said "NOT changed" and left no TsukihimeLauncher.exe at all
        string app = Install();
        string launcher = Path.Combine(app, "TsukihimeLauncher.exe");
        string sha = Sha(launcher);
        IconChoice face = Face();
        var held = new List<FileStream>();
        StampResult result = ExeIconStamp.StampExe(launcher, face, Known(face), onStep: (_, step) =>
        {
            if (step == StampStep.Renamed)
            {
                held.Add(new FileStream(launcher + ExeIconStamp.OldSuffix, FileMode.Open, FileAccess.Read, FileShare.Read));
                held.Add(new FileStream(launcher + ExeIconStamp.NewSuffix, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
        });

        try
        {
            Assert.Equal((StampOutcome.Failed, StampResult.NoFile), (result.Outcome, result.After));
            Assert.StartsWith("TsukihimeLauncher.exe: MISSING, the failed stamp could not give its name back", result.Describe(), StringComparison.Ordinal);
            Assert.Contains("rename TsukihimeLauncher.exe.icon-old to TsukihimeLauncher.exe by hand", result.Detail, StringComparison.Ordinal);
            Assert.False(File.Exists(launcher));
            Assert.True(File.Exists(launcher + ExeIconStamp.OldSuffix), "the original is kept");
            Assert.True(File.Exists(launcher + ExeIconStamp.NewSuffix), "the checked copy is kept while the name is empty");

            // while both are held, the clean-up deletes nothing of the missing program and counts both as left
            StampCleanup stillHeld = ExeIconStamp.CleanLeftovers(app);
            Assert.Equal((0, 0, 2), (stillHeld.Repaired.Count, stillHeld.Deleted, stillHeld.Left));
            Assert.Equal(StampOutcome.Skipped, ExeIconStamp.StampFolder(app, face, Known(face))[1].Outcome);
        }
        finally
        {
            held.ForEach(h => h.Dispose());
        }

        // the next stamp (every stamp of the window runs the repair first) puts the original back; the first of a session also deletes the copy
        StampCleanup repair = ExeIconStamp.CleanLeftovers(app, deleteCopies: false);
        Assert.Equal(["TsukihimeLauncher.exe"], repair.Repaired);
        Assert.Equal((0, 0), (repair.Deleted, repair.Left));
        Assert.Equal(sha, Sha(launcher));
        Assert.True(File.Exists(launcher + ExeIconStamp.NewSuffix), "deleteCopies: false only repairs");
        StampCleanup cleanup = ExeIconStamp.CleanLeftovers(app);
        Assert.Equal((0, 1, 0), (cleanup.Repaired.Count, cleanup.Deleted, cleanup.Left));
        Assert.Equal(StampOutcome.Stamped, ExeIconStamp.StampExe(launcher, face, Known(face)).Outcome);
    }

    [Fact]
    public void AMoveInThatFailsWhileTheOriginalIsHeldPutsTheCheckedCopyInAfterAll()
    {
        string app = Install();
        string launcher = Path.Combine(app, "TsukihimeLauncher.exe");
        IconChoice face = Face();
        FileStream? held = null;
        StampResult result = ExeIconStamp.StampExe(launcher, face, Known(face), onStep: (_, step) =>
        {
            if (step == StampStep.Renamed && held is null)
            {
                held = new FileStream(launcher + ExeIconStamp.OldSuffix, FileMode.Open, FileAccess.Read, FileShare.Read);
                throw new IOException("made to fail at the move-in");
            }
        });

        using (held)
        {
            // the original cannot be renamed back, the checked copy can go in: the launcher's name is never left empty, and the stamp did happen
            Assert.Equal(StampOutcome.Stamped, result.Outcome);
            Assert.True(IcoFile.SameImages(ExeIconStamp.ReadIcon(launcher), face.Images));
            Assert.Null(ExeIconStamp.ApphostProblem(launcher));
            Assert.True(File.Exists(launcher + ExeIconStamp.OldSuffix), "held: deleted at the next start");
            Assert.False(File.Exists(launcher + ExeIconStamp.NewSuffix));
        }

        StampCleanup cleanup = ExeIconStamp.CleanLeftovers(app);
        Assert.Equal((0, 1, 0), (cleanup.Repaired.Count, cleanup.Deleted, cleanup.Left));
    }

    [Fact]
    public void TheCleanUpDeletesNothingOfAMissingProgramWhoseCopiesCannotBeUsedAndUsesACheckedCopy()
    {
        string app = Install();
        string game = Path.Combine(app, "Tsukihime.exe");
        string launcher = Path.Combine(app, "TsukihimeLauncher.exe");
        string gameSha = Sha(game);
        string launcherSha = Sha(launcher);

        // review of K43: the only old copy is held without read sharing but with delete sharing (a backup tool): it cannot be read as a program, and the old
        // clean-up then deleted it, the last copy of the game; a half-written .icon-new is no program either
        File.Move(game, game + ExeIconStamp.OldSuffix);
        File.WriteAllText(game + ExeIconStamp.NewSuffix, "half written");
        using (new FileStream(game + ExeIconStamp.OldSuffix, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            StampCleanup held = ExeIconStamp.CleanLeftovers(app);
            Assert.Equal((0, 0, 2), (held.Repaired.Count, held.Deleted, held.Left));
            Assert.True(File.Exists(game + ExeIconStamp.OldSuffix));
            Assert.True(File.Exists(game + ExeIconStamp.NewSuffix));
        }

        StampCleanup back = ExeIconStamp.CleanLeftovers(app);
        Assert.Equal(["Tsukihime.exe"], back.Repaired);
        Assert.Equal((1, 0), (back.Deleted, back.Left));
        Assert.Equal(gameSha, Sha(game));

        // only the checked copy is left (the original is gone): a plain apphost with its .dll beside it is moved in rather than leaving no program
        File.Move(launcher, launcher + ExeIconStamp.NewSuffix);
        StampCleanup fromCopy = ExeIconStamp.CleanLeftovers(app);
        Assert.Equal(["TsukihimeLauncher.exe"], fromCopy.Repaired);
        Assert.Equal((0, 0), (fromCopy.Deleted, fromCopy.Left));
        Assert.Equal(launcherSha, Sha(launcher));
    }

    [Fact]
    public void AReplaceTheAccountMayNotDoIsNotRewrittenAgainUntilTheFileChanges()
    {
        // review of K43: a folder where the account can create files but not replace the ones another account unzipped; the write probe passes, the swap is
        // refused, and each refresh used to copy, rewrite and check the program again only to be refused once more
        string app = Install();
        string game = Path.Combine(app, "Tsukihime.exe");
        string sha = Sha(game);
        IconChoice face = Face();
        StampResult refused = ExeIconStamp.StampExe(game, face, Known(face), onStep: (_, step) =>
        {
            if (step == StampStep.Verified)
            {
                File.SetAttributes(game, FileAttributes.ReadOnly);                              // Windows refuses to replace it: access denied
            }
        });
        File.SetAttributes(game, FileAttributes.Normal);
        Assert.Equal((StampOutcome.Failed, StampReason.NotWritable), (refused.Outcome, refused.Reason));
        Assert.Equal(sha, Sha(game));

        var steps = new List<StampStep>();
        StampResult again = ExeIconStamp.StampExe(game, face, Known(face), onStep: (_, step) => steps.Add(step));
        Assert.Equal((StampOutcome.Skipped, StampReason.NotWritable), (again.Outcome, again.Reason));
        Assert.Contains("this account may not replace it", again.Detail, StringComparison.Ordinal);
        Assert.Empty(steps);                                                                    // not even copied
        Assert.Empty(Directory.GetFiles(app, "*.icon-*"));

        // a changed file (another time) is tried again
        File.SetLastWriteTimeUtc(game, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(StampOutcome.Stamped, ExeIconStamp.StampExe(game, face, Known(face)).Outcome);
    }

    [Theory]
    [InlineData("Tsukihime.exe")]
    [InlineData("TsukihimeLauncher.exe")]
    public void AnUpdateThatStartsDuringTheStampKeepsItsFiles(string program)
    {
        // review of K43: the update's staging folder appears after the stamp's check; the swap is refused under the gate, so a stamped copy of the old version is
        // never moved over a program the update installs
        string app = Install();
        string exe = Path.Combine(app, program);
        string sha = Sha(exe);
        IconChoice face = Face();
        StampResult result = ExeIconStamp.StampExe(exe, face, Known(face), onStep: (_, step) =>
        {
            if (step == StampStep.Verified)
            {
                Directory.CreateDirectory(Path.Combine(app, UpdateStaging.FolderName));
            }
        });
        Assert.Equal(StampOutcome.Failed, result.Outcome);
        Assert.Contains("an update of this folder started meanwhile", result.Detail, StringComparison.Ordinal);
        Assert.Equal(sha, Sha(exe));
        Assert.Empty(Directory.GetFiles(app, "*.icon-*"));
    }

    [Fact]
    public void TheHelpPageSaysInPlainWordsWhyTheProgramsWereNotChanged()
    {
        static StampResult R(string program, StampOutcome outcome, StampReason reason) => new(program, outcome, "why", "before", "after", reason);
        static string Note(params StampResult[] results) => ExeIconStamp.PlayerNote(results);

        Assert.Equal(string.Empty, Note(R("Tsukihime.exe", StampOutcome.Stamped, StampReason.None), R("TsukihimeLauncher.exe", StampOutcome.Unchanged, StampReason.None)));
        Assert.Equal("Tsukihime.exe changes once the game is closed.", Note(R("Tsukihime.exe", StampOutcome.Skipped, StampReason.GameRunning), R("TsukihimeLauncher.exe", StampOutcome.Stamped, StampReason.None)));
        Assert.StartsWith("Not applied: your account can't change the files in the port's folder", Note(R("Tsukihime.exe", StampOutcome.Skipped, StampReason.GameRunning), R("TsukihimeLauncher.exe", StampOutcome.Failed, StampReason.NotWritable)), StringComparison.Ordinal);
        Assert.StartsWith("Not applied yet: the last update isn't finished.", Note(R("Tsukihime.exe", StampOutcome.Skipped, StampReason.UpdateUnfinished)), StringComparison.Ordinal);
        Assert.Equal("Not applied: TsukihimeLauncher.exe couldn't be changed. launcher.log, next to your saves, says why.", Note(R("Tsukihime.exe", StampOutcome.Skipped, StampReason.GameRunning), R("TsukihimeLauncher.exe", StampOutcome.Failed, StampReason.Error)));
        Assert.Equal("Not applied: Tsukihime.exe isn't in the launcher's folder.", Note(R("Tsukihime.exe", StampOutcome.Skipped, StampReason.NotThere)));

        // the reasons come from the stamp itself: a folder that is not there, an unfinished update (the other reasons: AProgramThatMustNotBeWrittenIsLeftAlone)
        string app = Install();
        IconChoice face = Face();
        Assert.All(ExeIconStamp.StampFolder(Path.Combine(app, "missing"), face, Known(face)), r => Assert.Equal((StampOutcome.Skipped, StampReason.NotThere), (r.Outcome, r.Reason)));
        Directory.CreateDirectory(Path.Combine(app, UpdateStaging.FolderName));
        Assert.StartsWith("Not applied yet: the last update isn't finished.", ExeIconStamp.PlayerNote(ExeIconStamp.StampFolder(app, face, Known(face))), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyASelfContainedPublishIsAReleaseFolderForTheWindow()
    {
        // review of K43: the window stamps a release folder only; a build output of the source (no hostfxr.dll beside the launcher) keeps a stamped apphost over
        // later builds (the SDK copies it only when it is newer), which then fails these very tests
        string app = Install();
        Assert.False(ExeIconStamp.IsSelfContained(app));
        string? output = UpdateEndToEndTests.LauncherOutput();
        Assert.False(output is not null && ExeIconStamp.IsSelfContained(output), "the build output beside the tests is framework-dependent");
        File.WriteAllBytes(Path.Combine(app, "hostfxr.dll"), []);
        Assert.True(ExeIconStamp.IsSelfContained(app));
    }

    [Theory]
    [InlineData("arcueid_icon=off", false)]
    [InlineData("ARCUEID_ICON = no", false)]
    [InlineData("arcueid_icon=on", true)]
    [InlineData("arcueid_icon=sometimes", null)]
    [InlineData("prompts=auto", null)]
    public void TheArcueidIconKeyReadsOnAndOffAndTheGameKeepsIt(string line, bool? expected)
    {
        // Help & about > Show Arcueid on the program icons; absent means on (the launcher decides that)
        Assert.Equal(expected, AppSettings.Parse(line).ArcueidIcon);
        string text = AppSettings.Format(new AppSettingsData(ArcueidIcon: false));
        Assert.Contains("arcueid_icon=off", text, StringComparison.Ordinal);
        Assert.True(AppSettings.Parse(text).ArcueidIcon is false);
        Assert.DoesNotContain("arcueid_icon=", AppSettings.Format(new AppSettingsData()), StringComparison.Ordinal);

        // the game rewrites settings.ini for its own keys and keeps the launcher's
        string path = Path.Combine(_dir, "settings.ini");
        Assert.True(AppSettings.Save(path, new AppSettingsData(ArcueidIcon: false, UpdateCheck: true)));
        Assert.True(AppSettings.Update(path, s => s with { Prompts = Tsuki.Ui.PromptSetting.Xbox }));
        Assert.Equal((false, true), (AppSettings.Load(path).ArcueidIcon, AppSettings.Load(path).UpdateCheck));
    }

    [Fact]
    public void AnIconFileIsReadImageByImageAndItsGroupLaidOutAsTheCompilerDoes()
    {
        IReadOnlyList<IcoImage> crescent = Crescent.Images;
        Assert.Equal([16, 20, 24, 32, 40, 48, 64, 96, 128, 256], crescent.Select(i => i.Size));
        Assert.Equal((byte)0x89, crescent[^1].Data[0]);                                           // the 256 px image is a PNG and is compared byte for byte too
        Assert.True(IcoFile.SameImages(crescent, IcoFile.Read(File.ReadAllBytes(Path.Combine(Repo.Root, "assets", "icon", "tsukihime.ico")))));
        Assert.False(IcoFile.SameImages(crescent, Face().Images));
        Assert.False(IcoFile.SameImages(Face(1).Images, Face(2).Images));
        Assert.False(IcoFile.SameImages(crescent, null));
        Assert.Equal("16, 20, 24, 32, 40, 48, 64, 96, 128, 256 px", IcoFile.Sizes(crescent));

        Assert.Null(IcoFile.Read("not an icon"u8));
        Assert.Null(IcoFile.Read([0, 0, 1, 0, 0, 0]));                                            // no image
        byte[] cut = FakeIco(1)[..5000];
        Assert.Null(IcoFile.Read(cut));                                                            // an entry that points past the end

        byte[] group = IcoFile.GroupDirectory(Face().Images, [3, 4, 5, 6, 7, 8, 9]);
        Assert.Equal(6 + (7 * IcoFile.GroupEntryBytes), group.Length);
        Assert.Equal([3, 4, 5, 6, 7, 8, 9], IcoFile.GroupImageIds(group));
        Assert.Throws<ArgumentException>(() => IcoFile.GroupDirectory(Face().Images, [1, 2]));
        Assert.Throws<ArgumentOutOfRangeException>(() => IcoFile.GroupDirectory(Face().Images, [0, 1, 2, 3, 4, 5, 6]));
    }
}

using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>
/// Package K40 (issue #6): replacing the launcher's files on a temporary folder. In-use files are simulated by keeping them open: a handle that allows renaming (as a
/// running program and its loaded libraries do) does not stop the update; one that does not makes it fail, and then every step taken is undone.
/// </summary>
public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-k40-apply-" + Guid.NewGuid().ToString("N"));
    private readonly string _app;
    private readonly UpdateStaging _staging;

    public UpdateInstallerTests()
    {
        _app = Path.Combine(_dir, "Tsukihime");
        Directory.CreateDirectory(Path.Combine(_app, "licenses"));
        File.WriteAllText(Path.Combine(_app, "TsukihimeLauncher.exe"), "launcher old");
        File.WriteAllText(Path.Combine(_app, "Tsukihime.exe"), "game old");
        File.WriteAllText(Path.Combine(_app, "TsukihimeLauncher.dll"), "dll old");
        File.WriteAllText(Path.Combine(_app, "licenses", "LICENSE.txt"), "licence old");
        File.WriteAllText(Path.Combine(_app, "only-in-old.txt"), "kept");
        _staging = new UpdateStaging(_app);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A staged release with new contents for every old file, one new file (marker.txt) and one new folder.</summary>
    private StagedRelease Stage()
    {
        string files = _staging.FilesDirectory;
        Directory.CreateDirectory(Path.Combine(files, "licenses"));
        Directory.CreateDirectory(Path.Combine(files, "new-folder"));
        File.WriteAllText(Path.Combine(files, "TsukihimeLauncher.exe"), "launcher new");
        File.WriteAllText(Path.Combine(files, "Tsukihime.exe"), "game new");
        File.WriteAllText(Path.Combine(files, "TsukihimeLauncher.dll"), "dll new");
        File.WriteAllText(Path.Combine(files, "licenses", "LICENSE.txt"), "licence new");
        File.WriteAllText(Path.Combine(files, "marker.txt"), "new file");
        File.WriteAllText(Path.Combine(files, "new-folder", "x.txt"), "new folder");
        var list = Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(files, f)).ToList();
        return new StagedRelease(files, list, "test");
    }

    private string Read(params string[] parts) => File.ReadAllText(Path.Combine([_app, .. parts]));

    private string[] OldFiles() => Directory.GetFiles(_app, "*.old", SearchOption.AllDirectories);

    [Fact]
    public void FilesInUseAreRenamedAndReplacedAndTheNextStartDeletesTheOldOnes()
    {
        StagedRelease release = Stage();
        // the running launcher: Windows lets a mapped image be renamed, as a handle opened with FileShare.Delete does
        using (var running = new FileStream(Path.Combine(_app, "TsukihimeLauncher.exe"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            ApplyResult result = UpdateInstaller.Apply(_app, release, _staging.Journal);
            Assert.True(result.Applied, result.Problem);
            Assert.Equal(4, result.Replaced);
            Assert.Equal(2, result.Added);
            Assert.Equal("launcher new", Read("TsukihimeLauncher.exe"));
            Assert.Equal("game new", Read("Tsukihime.exe"));
            Assert.Equal("licence new", Read("licenses", "LICENSE.txt"));
            Assert.Equal("new file", Read("marker.txt"));
            Assert.Equal("new folder", Read("new-folder", "x.txt"));
            Assert.Equal("kept", Read("only-in-old.txt"));
            Assert.Equal("launcher old", Read("TsukihimeLauncher.exe.old"));
            Assert.Equal(4, OldFiles().Length);
            string[] journal = File.ReadAllLines(_staging.Journal);
            Assert.Equal("done", journal[^1]);
            // the launcher is the last file replaced: an apply cut off before it leaves the old launcher in place
            Assert.EndsWith("TsukihimeLauncher.exe", journal[^2], StringComparison.Ordinal);

            // while the old launcher still runs, its .old copy cannot go (a handle that does not share deletion, as a mapped image's): it waits for the next start
            using (new FileStream(Path.Combine(_app, "TsukihimeLauncher.exe.old"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Assert.Equal(1, UpdateInstaller.Cleanup(_app));
                Assert.True(File.Exists(_staging.Journal), "the journal stays until every .old file is gone");
                Assert.Single(OldFiles());
            }
        }

        Assert.Equal(0, UpdateInstaller.Cleanup(_app));
        Assert.Empty(OldFiles());
        Assert.False(Directory.Exists(_staging.Root));
        Assert.Equal("launcher new", Read("TsukihimeLauncher.exe"));
        Assert.Equal(0, UpdateInstaller.Cleanup(_app));         // nothing to do the next time
    }

    [Fact]
    public void AFileThatCannotBeRenamedRollsEverythingBack()
    {
        StagedRelease release = Stage();
        // an exclusive handle (no FileShare.Delete): Windows refuses the rename
        using (new FileStream(Path.Combine(_app, "licenses", "LICENSE.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ApplyResult result = UpdateInstaller.Apply(_app, release, _staging.Journal);
            Assert.False(result.Applied);
            Assert.True(result.RolledBack);
            Assert.NotNull(result.Problem);
            Assert.Equal("rolled back", File.ReadAllLines(_staging.Journal)[^1]);
        }

        Assert.Equal("launcher old", Read("TsukihimeLauncher.exe"));
        Assert.Equal("game old", Read("Tsukihime.exe"));
        Assert.Equal("dll old", Read("TsukihimeLauncher.dll"));
        Assert.Equal("licence old", Read("licenses", "LICENSE.txt"));
        Assert.Equal("kept", Read("only-in-old.txt"));
        Assert.False(File.Exists(Path.Combine(_app, "marker.txt")), "a file the release added is removed again");
        Assert.False(File.Exists(Path.Combine(_app, "new-folder", "x.txt")));
        Assert.Empty(OldFiles());
        Assert.Equal(0, UpdateInstaller.Cleanup(_app));
        Assert.False(Directory.Exists(_staging.Root));
    }

    [Fact]
    public void AFailureHalfWayIsUndoneInReverse()
    {
        StagedRelease release = Stage();
        int steps = 0;
        ApplyResult result = UpdateInstaller.Apply(_app, release, _staging.Journal, relative =>
        {
            if (++steps == 5)
            {
                throw new IOException("the disk is full (test)");
            }
        });
        Assert.False(result.Applied);
        Assert.True(result.RolledBack);
        Assert.Equal("the disk is full (test)", result.Problem);
        Assert.Equal("launcher old", Read("TsukihimeLauncher.exe"));
        Assert.Equal("game old", Read("Tsukihime.exe"));
        Assert.Equal("dll old", Read("TsukihimeLauncher.dll"));
        Assert.Equal("licence old", Read("licenses", "LICENSE.txt"));
        Assert.False(File.Exists(Path.Combine(_app, "marker.txt")));
        Assert.False(File.Exists(Path.Combine(_app, "new-folder", "x.txt")));
        Assert.Empty(OldFiles());
    }

    [Fact]
    public void AnApplyCutOffBeforeTheLauncherIsPutBackAtTheNextStart()
    {
        StagedRelease release = Stage();
        // the journal of an apply that stopped (power loss) after Tsukihime.exe: renamed and moved, no "done", the launcher untouched
        string game = Path.Combine(_app, "Tsukihime.exe");
        File.Move(game, game + ".old");
        File.Move(Path.Combine(release.Root, "Tsukihime.exe"), game);
        File.WriteAllLines(_staging.Journal, ["old\t" + game, "new\t" + game]);
        Assert.Equal("game new", Read("Tsukihime.exe"));

        Assert.Equal(0, UpdateInstaller.Cleanup(_app));
        Assert.Equal("game old", Read("Tsukihime.exe"));
        Assert.Equal("launcher old", Read("TsukihimeLauncher.exe"));
        Assert.Empty(OldFiles());
        Assert.False(Directory.Exists(_staging.Root));
    }

    [Fact]
    public void AnApplyThatReachedTheLauncherCountsAsDone()
    {
        StagedRelease release = Stage();
        string launcher = Path.Combine(_app, "TsukihimeLauncher.exe");
        File.Move(launcher, launcher + ".old");
        File.Move(Path.Combine(release.Root, "TsukihimeLauncher.exe"), launcher);
        File.WriteAllLines(_staging.Journal, ["old\t" + launcher, "new\t" + launcher]);
        Assert.Equal(0, UpdateInstaller.Cleanup(_app));
        Assert.Equal("launcher new", Read("TsukihimeLauncher.exe"));
        Assert.Empty(OldFiles());
    }

    [Fact]
    public void TheCleanupDeletesOnlyTheOldFilesItsJournalNames()
    {
        File.WriteAllText(Path.Combine(_app, "notes.old"), "the player's own file");
        string outside = Path.Combine(_dir, "outside.txt");
        File.WriteAllText(outside + ".old", "outside the launcher's folder");
        Directory.CreateDirectory(_staging.Root);
        File.WriteAllLines(_staging.Journal, ["old\t" + outside, "new\t" + outside, "done"]);
        Assert.Equal(0, UpdateInstaller.Cleanup(_app));
        Assert.True(File.Exists(Path.Combine(_app, "notes.old")));
        Assert.True(File.Exists(outside + ".old"));
        Assert.False(Directory.Exists(_staging.Root));
    }

    /// <summary>
    /// Review K41: an apply whose rollback could not finish (a new file was held open while it was undone) ends its journal "rollback incomplete", and the next start
    /// repairs it. The files the rollback had already put back (their .old copy is gone because it IS the file again) must stay: they are the old version, not files
    /// the release added.
    /// </summary>
    [Fact]
    public void AnIncompleteRollbackIsFinishedAtTheNextStartAndKeepsTheFilesItHadPutBack()
    {
        StagedRelease staged = Stage();
        // an explicit order: the game, the launcher's library, the licence (the step that fails), the launcher last
        StagedRelease release = staged with { Files = ["Tsukihime.exe", "TsukihimeLauncher.dll", Path.Combine("licenses", "LICENSE.txt"), "TsukihimeLauncher.exe"] };
        FileStream? held = null;
        try
        {
            ApplyResult result = UpdateInstaller.Apply(_app, release, _staging.Journal, relative =>
            {
                if (relative == Path.Combine("licenses", "LICENSE.txt"))
                {
                    // the new TsukihimeLauncher.dll, just moved in, is opened without delete sharing (a virus scanner, say): the rollback cannot remove it
                    held = new FileStream(Path.Combine(_app, "TsukihimeLauncher.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
                    throw new IOException("the disk is full (test)");
                }
            });
            Assert.False(result.Applied);
            Assert.False(result.RolledBack);
            Assert.Equal("rollback incomplete", File.ReadAllLines(_staging.Journal)[^1]);
            Assert.Equal("game old", Read("Tsukihime.exe"));                  // put back by the rollback; no Tsukihime.exe.old any more
            Assert.False(File.Exists(Path.Combine(_app, "Tsukihime.exe.old")));
            Assert.Equal("dll new", Read("TsukihimeLauncher.dll"));           // could not be undone
            Assert.Equal("dll old", Read("TsukihimeLauncher.dll.old"));

            // a start while the file is still held: nothing more is lost, the journal stays for the next one
            Assert.True(UpdateInstaller.Cleanup(_app) > 0);
            Assert.Equal("game old", Read("Tsukihime.exe"));
            Assert.Equal("dll old", Read("TsukihimeLauncher.dll.old"));
            Assert.True(File.Exists(_staging.Journal));
        }
        finally
        {
            held?.Dispose();
        }

        // the next start, with the file free again: the rollback is finished and nothing the old version had is missing
        Assert.Equal(0, UpdateInstaller.Cleanup(_app));
        Assert.Equal("game old", Read("Tsukihime.exe"));
        Assert.Equal("dll old", Read("TsukihimeLauncher.dll"));
        Assert.Equal("launcher old", Read("TsukihimeLauncher.exe"));
        Assert.Equal("licence old", Read("licenses", "LICENSE.txt"));
        Assert.Empty(OldFiles());
        Assert.False(Directory.Exists(_staging.Root));
    }

    /// <summary>
    /// Review K41: when the repair of an interrupted apply cannot put a file back (it is in use), the .old copy is that file's only old version: the cleanup must not
    /// delete it, or the next start has nothing to put back.
    /// </summary>
    [Fact]
    public void ARepairThatCannotFinishKeepsTheOldCopiesForTheNextStart()
    {
        StagedRelease release = Stage();
        string game = Path.Combine(_app, "Tsukihime.exe");
        File.Move(game, game + ".old");
        File.Move(Path.Combine(release.Root, "Tsukihime.exe"), game);
        File.WriteAllLines(_staging.Journal, ["old\t" + game, "new\t" + game]);           // cut off (power loss) before the launcher

        using (new FileStream(game, FileMode.Open, FileAccess.Read, FileShare.Read))        // the new Tsukihime.exe is in use: it cannot be replaced yet
        {
            Assert.True(UpdateInstaller.Cleanup(_app) > 0);
            Assert.Equal("game old", Read("Tsukihime.exe.old"));
            Assert.True(File.Exists(_staging.Journal));
        }

        Assert.Equal(0, UpdateInstaller.Cleanup(_app));
        Assert.Equal("game old", Read("Tsukihime.exe"));
        Assert.Empty(OldFiles());
        Assert.False(Directory.Exists(_staging.Root));
    }

    /// <summary>
    /// Review K41: the repair of an interrupted journal renames and deletes files, so like the cleanup it only acts inside the launcher's own folder (a folder copied
    /// with its update-staging folder carries a journal that names the original folder's files).
    /// </summary>
    [Fact]
    public void TheRepairOfAnInterruptedJournalNeverTouchesFilesOutsideTheFolder()
    {
        string other = Path.Combine(_dir, "original");
        Directory.CreateDirectory(other);
        string replaced = Path.Combine(other, "Tsukihime.exe");
        string added = Path.Combine(other, "marker.txt");
        File.WriteAllText(replaced, "the original folder's new game");
        File.WriteAllText(replaced + ".old", "the original folder's old game");
        File.WriteAllText(added, "the original folder's new file");
        Directory.CreateDirectory(_staging.Root);
        File.WriteAllLines(_staging.Journal, ["new\t" + added, "old\t" + replaced, "new\t" + replaced]);   // no end, and the launcher was not reached

        Assert.Equal(0, UpdateInstaller.Cleanup(_app));
        Assert.Equal("the original folder's new game", File.ReadAllText(replaced));
        Assert.Equal("the original folder's old game", File.ReadAllText(replaced + ".old"));
        Assert.Equal("the original folder's new file", File.ReadAllText(added));
        Assert.Equal("launcher old", Read("TsukihimeLauncher.exe"));
        Assert.False(Directory.Exists(_staging.Root));
    }

    [Fact]
    public void AStagedFileCannotLandOutsideTheFolderOrInTheStagingFolder()
    {
        StagedRelease release = Stage();
        var escaping = release with { Files = [.. release.Files, Path.Combine("..", "evil.txt")] };
        ApplyResult result = UpdateInstaller.Apply(_app, escaping, _staging.Journal);
        Assert.False(result.Applied);
        Assert.True(result.RolledBack);
        Assert.Contains("outside", result.Problem, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.Equal("launcher old", Read("TsukihimeLauncher.exe"));

        var intoStaging = release with { Files = [Path.Combine(UpdateStaging.FolderName, "journal.txt")] };
        Assert.False(UpdateInstaller.Apply(_app, intoStaging, Path.Combine(_dir, "journal2.txt")).Applied);
    }
}

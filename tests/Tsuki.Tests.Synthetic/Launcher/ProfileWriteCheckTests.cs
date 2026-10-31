using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>
/// Package R1: the launcher's ready check tells a player whose profile folder cannot be written (or whose save.dat is a folder) before the first play, instead of
/// "No saves yet" with a check mark while the game loses every save of the session.
/// </summary>
public sealed class ProfileWriteCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-r1-profile-" + Guid.NewGuid().ToString("N"));

    public ProfileWriteCheckTests()
    {
        Directory.CreateDirectory(_root);
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

    [Fact]
    public void AWritableFolderPassesTheProbeAndLeavesNothingBehind()
    {
        string dir = Path.Combine(_root, "profile");
        Assert.Null(SaveTools.ProbeWritable(dir));
        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.GetFileSystemEntries(dir));
        LauncherProfile profile = LauncherProfile.Read(new LauncherPaths(dir));
        Assert.Null(profile.Saves.WriteError);
        Assert.Equal("No saves yet; they are created on the first play", profile.Saves.Describe());
    }

    [Fact]
    public void AFolderThatCannotBeMadeIsAWarningWithAFixOnTheSavesPage()
    {
        // a file where the folder should be: nothing can be created under it
        string blocker = Path.Combine(_root, "Tsuki");
        File.WriteAllText(blocker, "not a folder");
        string? error = SaveTools.ProbeWritable(blocker);
        Assert.NotNull(error);

        var paths = new LauncherPaths(blocker);
        LauncherProfile profile = LauncherProfile.Read(paths);
        Assert.NotNull(profile.Saves.WriteError);
        Assert.StartsWith("The save folder cannot be written: ", profile.Saves.Describe(), StringComparison.Ordinal);

        IReadOnlyList<ReadyRow> rows = ReadyCheck.Build(GameFolderCheck.NotSet, null, [], [], profile.Saves);
        ReadyRow saves = Assert.Single(rows, r => r.Title == "Saves");
        Assert.Equal(CheckState.Warning, saves.State);
        Assert.Equal(LauncherPage.Saves, saves.Fix);
        Assert.Contains("cannot be written", saves.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ASaveFileThatIsAFolderIsSaidSo()
    {
        string dir = Path.Combine(_root, "profile2");
        Directory.CreateDirectory(Path.Combine(dir, "save.dat"));
        SaveSummary summary = SaveTools.Summarize(Path.Combine(dir, "save.dat"));
        Assert.True(summary.Exists);
        Assert.NotNull(summary.Error);
        Assert.Contains("folder", summary.Error, StringComparison.Ordinal);
        ReadyRow saves = Assert.Single(ReadyCheck.Build(GameFolderCheck.NotSet, null, [], [], summary), r => r.Title == "Saves");
        Assert.Equal(CheckState.Warning, saves.State);
    }
}

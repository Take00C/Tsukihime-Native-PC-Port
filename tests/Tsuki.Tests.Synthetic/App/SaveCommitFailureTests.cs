namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package R1: a save whose file could not be written is reported as a failed save (the save screen's failure text, no quick-save banner) instead of a success, and the
/// first failure is raised once with the path and the reason, so the game can tell the player why. The slot stays in memory either way.
/// </summary>
public sealed class SaveCommitFailureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-r1-commit-" + Guid.NewGuid().ToString("N"));

    public SaveCommitFailureTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ASaveWhoseFileCannotBeWrittenReportsFailureOnceWithThePath()
    {
        // save.dat as a folder: the atomic write cannot move its temporary file over it
        string savePath = Path.Combine(_dir, "save.dat");
        Directory.CreateDirectory(savePath);
        var rig = new AppRig(savePath: savePath);
        var failures = new List<(string Path, string Error)>();
        var log = new List<string>();
        rig.Services.SaveFailed += (p, e) => failures.Add((p, e));
        rig.Services.Logged += log.Add;
        rig.Begin();

        Assert.False(rig.Services.Save(3));
        Assert.True(rig.Services.Slots[3].Used);                                    // kept in memory: the next commit that works writes it
        Assert.False(rig.Services.QuickSave());
        Assert.False(rig.Services.Commit());
        Assert.Single(failures);
        Assert.Equal(savePath, failures[0].Path);
        Assert.False(string.IsNullOrWhiteSpace(failures[0].Error));
        Assert.Contains(log, l => l.StartsWith("save file not written:", StringComparison.Ordinal) && l.Contains(savePath, StringComparison.Ordinal));
        Assert.True(log.Count(l => l.StartsWith("save file not written:", StringComparison.Ordinal)) >= 3, "every failed commit is logged (the notice is raised once)");
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void ASaveThatIsWrittenStaysASuccessAndNoSaveFileMeansNothingToFail()
    {
        string savePath = Path.Combine(_dir, "save.dat");
        var rig = new AppRig(savePath: savePath);
        int failures = 0;
        rig.Services.SaveFailed += (_, _) => failures++;
        rig.Begin();
        Assert.True(rig.Services.Save(0));
        Assert.True(rig.Services.Commit());
        Assert.True(File.Exists(savePath));
        Assert.Equal(0, failures);

        var noFile = new AppRig(savePath: null);
        noFile.Begin();
        Assert.True(noFile.Services.Save(0));
        Assert.True(noFile.Services.Commit());
    }
}

using Tsuki.Cli;
using Tsuki.Data;
using Tsuki.Data.Icons;

namespace Tsuki.Tests.Data.App;

/// <summary>
/// Package K28 on the real game: <c>Tsuki.Cli face-icon</c>, the step a PERSONAL build runs before its compile, writes the same Arcueid .ico into the folder it is
/// given as the game builds into the profile, says so once and leaves a current file alone; a folder that is not the game fails the command.
/// </summary>
public sealed class FaceIconCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k28-faceicon-" + Guid.NewGuid().ToString("N"));

    public FaceIconCommandTests()
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

    [GameDataFact]
    public void TheCommandWritesTheIconOfTheGameFolderOnceAndFailsForAFolderThatIsNotTheGame()
    {
        string game = GameFolder.Require();
        string outDir = Path.Combine(_root, "obj-personal");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.Equal(ExitCode.Ok, CliApp.Run(["face-icon", "--game", game, "--out", outDir], stdout, stderr));
        Assert.Equal(string.Empty, stderr.ToString());
        string ico = FaceIconCache.IconPath(outDir);
        Assert.Contains("face icon: built from ARU_A_00_00_N00.MZP", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("face-icon: " + ico, stdout.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(ico));
        Assert.True(FaceIconCache.IsCurrent(outDir, game));

        // the file is what the game itself would build: the same seven frames, pixel for pixel
        using GameFolderSource source = GameFolderSource.Open(game);
        (FaceCrop wide, FaceCrop tight) = FaceIcon.Crops(source)!.Value;
        IReadOnlyList<IconFrame> expected = FaceIcon.RenderAll(wide, tight);
        IReadOnlyList<IconFrame> written = FaceIcon.DecodeIco(File.ReadAllBytes(ico));
        Assert.Equal(expected.Select(f => f.Size), written.Select(f => f.Size));
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Rgba, written[i].Rgba);
        }

        // the second run finds the file current and does not rebuild it
        using var stdout2 = new StringWriter();
        DateTime stamp = File.GetLastWriteTimeUtc(ico);
        Assert.Equal(ExitCode.Ok, CliApp.Run(["face-icon", "--game", game, "--out", outDir], stdout2, stderr));
        Assert.Contains("(current)", stdout2.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("face icon: built", stdout2.ToString(), StringComparison.Ordinal);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(ico));

        // --force rebuilds; a folder that is not the game fails with exit 1 and writes nothing
        using var stdout3 = new StringWriter();
        Assert.Equal(ExitCode.Ok, CliApp.Run(["face-icon", "--game", game, "--out", outDir, "--force"], stdout3, stderr));
        Assert.Contains("face icon: built", stdout3.ToString(), StringComparison.Ordinal);
        string notGame = Path.Combine(_root, "not-the-game");
        Directory.CreateDirectory(notGame);
        string outDir2 = Path.Combine(_root, "obj-wrong");
        using var stderr2 = new StringWriter();
        Assert.Equal(ExitCode.Failed, CliApp.Run(["face-icon", "--game", notGame, "--out", outDir2], stdout3, stderr2));
        Assert.Contains("face-icon:", stderr2.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(FaceIconCache.IconPath(outDir2)));
        Assert.Equal(ExitCode.Usage, CliApp.Run(["face-icon", "--game", game], stdout3, stderr2));
    }
}

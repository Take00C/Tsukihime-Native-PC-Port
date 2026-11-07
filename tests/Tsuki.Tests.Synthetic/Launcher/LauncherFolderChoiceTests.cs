using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

public sealed class LauncherFolderChoiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k48-folder-choice-" + Guid.NewGuid().ToString("N"));

    public LauncherFolderChoiceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task AcceptedFoldersAreRememberedAndRejectedFoldersDoNotReplaceThem()
    {
        var paths = new LauncherPaths(Path.Combine(_root, "profile"));
        string game = Path.Combine(_root, "CUSA47999");
        Directory.CreateDirectory(Path.Combine(game, "image"));
        Directory.CreateDirectory(Path.Combine(game, "sce_sys"));
        File.WriteAllBytes(Path.Combine(game, "eboot.bin"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(game, "sce_sys", "param.sfo"), GameFolderTests.Sfo(("APP_VER", "01.00"), ("CONTENT_ID", "HP3279-CUSA47999_00-GAME000TSUKIHIME"), ("TITLE", "TSUKIHIME")));
        LauncherFolderChoice accepted = await LauncherFolderChoice.ChooseAsync(paths, game);
        Assert.True(accepted.Accepted);
        Assert.True(accepted.Saved);
        Assert.Equal(Path.GetFullPath(game), paths.ReadGameDirectory());
        LauncherFolderChoice rejected = await LauncherFolderChoice.ChooseAsync(paths, Path.Combine(_root, "nowhere"));
        Assert.False(rejected.Accepted);
        Assert.False(rejected.Saved);
        Assert.Contains("not remembered", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(game), paths.ReadGameDirectory());
    }
}

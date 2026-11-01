using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>Package K20: the shortcuts the launcher makes, written into a temporary folder (never the desktop) and read back through the same shell interfaces.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ShortcutTests : IDisposable
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k20-links-" + Guid.NewGuid().ToString("N"));

    public ShortcutTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact(Skip = "Windows shell shortcuts require COM.", SkipUnless = nameof(IsWindows))]
    public void TheGameShortcutPointsAtTheGameInItsOwnFolderWithItsOwnIcon()
    {
        string app = Path.Combine(_root, "dist", GameLauncher.AppFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(app)!);
        File.WriteAllBytes(app, []);
        string desktop = Path.Combine(_root, "Desktop");

        ShortcutSpec spec = Shortcuts.Game(desktop, app);
        Assert.Equal(Path.Combine(desktop, "Tsukihime.lnk"), spec.LinkPath);
        Assert.Equal(app, spec.TargetPath);
        Assert.Equal(Path.Combine(_root, "dist"), spec.WorkingDirectory);
        Assert.Equal(app, spec.IconPath);
        Assert.Equal(0, spec.IconIndex);
        Assert.Equal(string.Empty, spec.Arguments);
        Assert.Contains("Tsukihime", spec.Description, StringComparison.Ordinal);

        Shortcuts.Create(spec);                                                      // the folder is created on the way
        Assert.True(File.Exists(spec.LinkPath));
        ShortcutInfo info = Shortcuts.Read(spec.LinkPath)!;
        Assert.Equal(app, info.TargetPath);
        Assert.Equal(Path.Combine(_root, "dist"), info.WorkingDirectory);
        Assert.Equal(app, info.IconPath);
        Assert.Equal(0, info.IconIndex);
        Assert.Equal(spec.Description, info.Description);
        Assert.Equal(string.Empty, info.Arguments);
    }

    [Fact(Skip = "Windows shell shortcuts require COM.", SkipUnless = nameof(IsWindows))]
    public void TheLauncherShortcutAndTheStartMenuPairHaveTheirOwnNamesAndAnExistingFileIsReplacedOnlyWhenAsked()
    {
        string dist = Path.Combine(_root, "dist");
        Directory.CreateDirectory(dist);
        string app = Path.Combine(dist, GameLauncher.AppFileName);
        string launcher = Path.Combine(dist, GameLauncher.LauncherFileName);
        File.WriteAllBytes(app, []);
        File.WriteAllBytes(launcher, []);
        string start = Path.Combine(_root, "Programs", "Tsukihime");

        ShortcutSpec game = Shortcuts.Game(start, app);
        ShortcutSpec link = Shortcuts.Launcher(start, launcher);
        Assert.Equal("Tsukihime.lnk", Path.GetFileName(game.LinkPath));
        Assert.Equal("Tsukihime Launcher.lnk", Path.GetFileName(link.LinkPath));
        Shortcuts.Create(game);
        Shortcuts.Create(link);
        Assert.Equal(2, Directory.GetFiles(start, "*.lnk").Length);
        Assert.Equal(launcher, Shortcuts.Read(link.LinkPath)!.TargetPath);

        // Create replaces (the model asks the user first); the file keeps its name and takes the new target
        string other = Path.Combine(_root, "elsewhere", GameLauncher.AppFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllBytes(other, []);
        Shortcuts.Create(Shortcuts.Game(start, other));
        Assert.Equal(other, Shortcuts.Read(game.LinkPath)!.TargetPath);
        Assert.Equal(2, Directory.GetFiles(start, "*.lnk").Length);

        Assert.Null(Shortcuts.Read(Path.Combine(_root, "none.lnk")));
        File.WriteAllText(Path.Combine(_root, "text.lnk"), "not a shortcut");
        Assert.Null(Shortcuts.Read(Path.Combine(_root, "text.lnk")));
    }

    [Fact(Skip = "Windows shell shortcuts require COM.", SkipUnless = nameof(IsWindows))]
    public void TheGameShortcutTakesTheArcueidIconFileWhenOneIsGivenAndKeepsItThroughTheFile()
    {
        // package K23: the launcher hands the cached icons\tsukihime.ico of the profile; without one the executable's own crescent stays
        string dist = Path.Combine(_root, "dist");
        Directory.CreateDirectory(dist);
        string app = Path.Combine(dist, GameLauncher.AppFileName);
        File.WriteAllBytes(app, []);
        string icon = Path.Combine(_root, "profile", "icons", "tsukihime.ico");
        Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
        File.WriteAllBytes(icon, []);
        string desktop = Path.Combine(_root, "Desktop");

        ShortcutSpec spec = Shortcuts.Game(desktop, app, icon);
        Assert.Equal(icon, spec.IconPath);
        Assert.Equal(0, spec.IconIndex);
        Assert.Equal(app, spec.TargetPath);
        Shortcuts.Create(spec);
        ShortcutInfo info = Shortcuts.Read(spec.LinkPath)!;
        Assert.Equal(icon, info.IconPath);
        Assert.Equal(app, info.TargetPath);
        Assert.Equal(app, Shortcuts.Game(desktop, app, null).IconPath);
    }

    [Fact]
    public void TheUsersFoldersAreTheDesktopAndAProgramsSubfolderNamedAfterTheGame()
    {
        ShortcutFolders folders = ShortcutFolders.ForUser();
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), folders.Desktop);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Tsukihime"), folders.StartMenu);
        Assert.Equal("Tsukihime.exe", GameLauncher.AppFileName);
        Assert.Equal("TsukihimeLauncher.exe", GameLauncher.LauncherFileName);
    }
}

using Tsuki.Launcher.Core;
using Tsuki.Platform.Input;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>Package K17: finding and starting Tsukihime.exe, and the key table the pages show.</summary>
public sealed class GameLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k17-launch-" + Guid.NewGuid().ToString("N"));

    public GameLauncherTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void TheGameProgramIsFoundNextToTheLauncher()
    {
        string dir = Path.Combine(_root, "dist");
        Directory.CreateDirectory(dir);
        Assert.Null(GameLauncher.FindApp(dir));
        File.WriteAllBytes(Path.Combine(dir, "Tsukihime.exe"), []);
        Assert.Equal(Path.Combine(dir, "Tsukihime.exe"), GameLauncher.FindApp(dir));
    }

    [Fact]
    public void ADevelopmentBuildFindsTheGameInItsOwnOutputFolder()
    {
        string launcher = Path.Combine(_root, "artifacts", "bin", "Tsuki.Launcher", "release");
        string app = Path.Combine(_root, "artifacts", "bin", "Tsuki.App", "release");
        Directory.CreateDirectory(launcher);
        Directory.CreateDirectory(app);
        File.WriteAllBytes(Path.Combine(app, "Tsukihime.exe"), []);
        Assert.Equal(Path.Combine(app, "Tsukihime.exe"), GameLauncher.FindApp(launcher));

        string classicLauncher = Path.Combine(_root, "src", "Tsuki.Launcher", "bin", "Release", "net10.0-windows");
        string classicApp = Path.Combine(_root, "src", "Tsuki.App", "bin", "Release", "net10.0");
        Directory.CreateDirectory(classicLauncher);
        Directory.CreateDirectory(classicApp);
        File.WriteAllBytes(Path.Combine(classicApp, "Tsukihime.exe"), []);
        Assert.Equal(Path.Combine(classicApp, "Tsukihime.exe"), GameLauncher.FindApp(classicLauncher));
    }

    [Fact]
    public void MissingFfmpegLibrariesAreNamed()
    {
        string dir = Path.Combine(_root, "app");
        Directory.CreateDirectory(dir);
        Assert.Equal(GameLauncher.FfmpegDlls, GameLauncher.MissingFfmpegDlls(dir));
        foreach (string dll in GameLauncher.FfmpegDlls.Skip(1))
        {
            File.WriteAllBytes(Path.Combine(dir, dll), []);
        }

        Assert.Equal([GameLauncher.FfmpegDlls[0]], GameLauncher.MissingFfmpegDlls(dir));
    }

    [Fact]
    public void ThePlanPassesTheGameFolderAndALogAndKeepsPathsWithSpacesWhole()
    {
        var paths = new LauncherPaths(Path.Combine(_root, "Tsuki Data"));
        string app = Path.Combine(_root, "Game Dir", "Tsukihime.exe");
        LaunchPlan plan = GameLauncher.Plan(app, @"D:\My Games\CUSA47999", paths, fullscreen: false);
        Assert.Equal(app, plan.ExePath);
        Assert.Equal(Path.Combine(_root, "Game Dir"), plan.WorkingDirectory);
        // a profile that is not the user's (--data) travels to the game as --save-dir, so PLAY from a verification launcher never touches the real %APPDATA%\Tsuki
        Assert.False(paths.IsUserProfile);
        Assert.Equal(["--game", @"D:\My Games\CUSA47999", "--log", paths.LastRunLog, "--windowed", "--save-dir", paths.DataDirectory], plan.Arguments);

        LaunchPlan test = GameLauncher.Plan(app, @"D:\g", paths, fullscreen: true, ["--hidden", "--mute"]);
        Assert.Equal(["--game", @"D:\g", "--log", paths.LastRunLog, "--fullscreen", "--save-dir", paths.DataDirectory, "--hidden", "--mute"], test.Arguments);
        Assert.Throws<ArgumentException>(() => GameLauncher.Plan(app, " ", paths, fullscreen: false));

        LauncherPaths user = LauncherPaths.ForUser();
        Assert.True(user.IsUserProfile);
        Assert.True(new LauncherPaths(user.DataDirectory.ToUpperInvariant() + Path.DirectorySeparatorChar).IsUserProfile);
        Assert.DoesNotContain("--save-dir", GameLauncher.Plan(app, @"D:\g", user, fullscreen: false).Arguments);
    }

    [Theory]
    [InlineData(false, "--windowed", "--fullscreen")]
    [InlineData(true, "--fullscreen", "--windowed")]
    public void EveryPlanCarriesExactlyOneExplicitWindowModeAndTheGameParsesIt(bool fullscreen, string expected, string other)
    {
        var paths = new LauncherPaths(Path.Combine(_root, "Tsuki Data"));
        LaunchPlan plan = GameLauncher.Plan(Path.Combine(_root, "Tsukihime.exe"), @"D:\g", paths, fullscreen);
        Assert.Single(plan.Arguments, expected);
        Assert.DoesNotContain(other, plan.Arguments);

        // the very list the launcher hands to the process is accepted by the game's own command line parser and means the same thing
        Assert.True(Tsuki.App.AppArgs.TryParse([.. plan.Arguments], out Tsuki.App.AppArgs? parsed, out string? error), error);
        Assert.Equal(fullscreen, parsed!.WindowModeChoice);
    }

    [Fact]
    public void TheKeyTableUsesTheWordsOfTheGamesOwnBindings()
    {
        var bindings = InputMapper.Bindings.ToDictionary(b => b.Input, b => b, StringComparer.Ordinal);
        Assert.NotEmpty(KeyTable.Rows);
        foreach (KeyRow row in KeyTable.Rows)
        {
            Assert.True(bindings.ContainsKey(row.Keys), $"the launcher lists \"{row.Keys}\", which InputMapper.Bindings does not have");
            Assert.False(string.IsNullOrWhiteSpace(row.Action));
        }

        Assert.Equal(KeyTable.Rows.Count, KeyTable.Rows.Select(r => r.Keys).Distinct().Count());
        Assert.All(KeyTable.Essentials, e => Assert.Contains(e, KeyTable.Rows));
        Assert.Equal(6, KeyTable.Essentials.Count);

        // and the other way round: every key a keyboard user can press is on the Controls page (package R1: H, M, Delete and the page keys were missing)
        var listed = KeyTable.Rows.Select(r => r.Keys).ToHashSet(StringComparer.Ordinal);
        string[] missing = InputMapper.Bindings.Select(b => b.Input).Where(KeyTable.IsKeyboardBinding).Where(k => !listed.Contains(k)).ToArray();
        Assert.True(missing.Length == 0, "keyboard bindings the Controls page does not list: " + string.Join("; ", missing));
    }
}

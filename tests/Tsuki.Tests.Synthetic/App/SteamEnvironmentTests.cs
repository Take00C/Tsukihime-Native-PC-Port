using Tsuki.App;
using Tsuki.App.Play;
using Tsuki.Data.IO;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// deck-journey-4/5: what the game reads from its environment on Linux for a folder named in Steam's Launch Options: the SteamDeck variable (the folder box then
/// points there) and the home folder a <c>~/</c> path means, for --game and (unix-4) the other path options: Steam passes <c>~</c> as typed, and a folder named
/// <c>~</c> in the install folder would go with it. These tests set the variables of the test process, so they run alone.
/// </summary>
[Collection("Profile environment")]
public sealed class SteamEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-steam-env-" + Guid.NewGuid().ToString("N"));
    private readonly string? _deck = Environment.GetEnvironmentVariable("SteamDeck");
    private readonly string? _home = Environment.GetEnvironmentVariable("HOME");

    public SteamEnvironmentTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SteamDeck", _deck);
        Environment.SetEnvironmentVariable("HOME", _home);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void OnASteamDeckTheFolderBoxPointsAtTheGamesLaunchOptions()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("the Steam Deck is a Linux machine; Windows and macOS give their own command (StartupMessageTests)");
        }

        Environment.SetEnvironmentVariable("SteamDeck", "1");
        string deck = GameApp.FolderCommandExample(Language.En);
        Assert.Equal("In Steam, the game's Properties > General > Launch Options:\n--game \"/home/deck/Games/CUSA47999\"", deck);
        Assert.Contains(deck, GameApp.FolderDialogFailedMessage("no portal"), StringComparison.Ordinal);
        Assert.Equal(deck, GameApp.FolderCommandExample(Language.En, steamDeck: true));
        Assert.Equal("./Tsukihime --game /path/to/CUSA47999", GameApp.FolderCommandExample(Language.En, steamDeck: false));

        Environment.SetEnvironmentVariable("SteamDeck", null);
        Assert.Equal("./Tsukihime --game /path/to/CUSA47999", GameApp.FolderCommandExample(Language.En));
    }

    [Fact]
    public void OffWindowsATildePathIsTheHomeFolder()
    {
        string home = Path.Combine(_root, "home");
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HOME", home);
        GameLayout layout = GameLayoutResolver.Resolve("\"~/Games/CUSA47999\"");
        Assert.Equal(GameLayoutKind.Missing, layout.Kind);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.GetFullPath("~/Games/CUSA47999"), layout.Chosen);        // as v0.1.6 read it
        }
        else
        {
            Assert.Equal(Path.Combine(home, "Games", "CUSA47999"), layout.Chosen);
        }
    }

    [Theory]
    [InlineData("~", "/home/deck")]
    [InlineData("~/", "/home/deck")]
    [InlineData("~/Games/CUSA47999", "/home/deck/Games/CUSA47999")]
    [InlineData("~deck/Games", "~deck/Games")]
    [InlineData("/run/media/deck/SD Card/~/CUSA47999", "/run/media/deck/SD Card/~/CUSA47999")]
    [InlineData("Games/~/x", "Games/~/x")]
    public void ALeadingTildeIsTheHomeFolderAndNothingElseIs(string path, string expected)
    {
        Assert.Equal(expected.Replace('/', Path.DirectorySeparatorChar), GameLayoutResolver.ExpandHome(path, "/home/deck").Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(path, GameLayoutResolver.ExpandHome(path, null));
        Assert.Equal(path, GameLayoutResolver.ExpandHome(path, string.Empty));
    }

    [Theory]
    [InlineData("--save-dir")]
    [InlineData("--log")]
    [InlineData("--report")]
    [InlineData("--shot-dir")]
    [InlineData("--screenshot-dir")]
    [InlineData("--clipboard-file")]
    [InlineData("--sweep-dir")]
    [InlineData("--sweep-tour")]
    [InlineData("--script")]
    public void OffWindowsATildeInAPathOptionIsTheHomeFolder(string option)
    {
        string value = option == "--script" ? "@~/Tsuki/run" : "~/Tsuki/run";
        string Expanded(string home) => (option == "--script" ? "@" : string.Empty) + Path.Combine(home, "Tsuki/run");

        // as Linux and macOS read it, and with no home folder
        Assert.True(AppArgs.TryParse([option, value], "/home/deck", out AppArgs? deck, out string? error), error);
        Assert.Equal(Expanded("/home/deck"), PathOption(deck!, option));
        Assert.True(AppArgs.TryParse([option, value], null, out AppArgs? typed, out error), error);
        Assert.Equal(value, PathOption(typed!, option));

        // the game's own reading on this machine: Windows keeps the path as typed (v0.1.6), Linux and macOS take HOME
        string home = Path.Combine(_root, "home");
        Environment.SetEnvironmentVariable("HOME", home);
        Assert.True(AppArgs.TryParse([option, value], out AppArgs? here, out error), error);
        Assert.Equal(OperatingSystem.IsWindows() ? value : Expanded(home), PathOption(here!, option));
    }

    [Fact]
    public void OnlyALeadingTildeOfAPathOptionIsTheHomeFolder()
    {
        Assert.True(AppArgs.TryParse(["--save-dir", "saves/~/x", "--script", "60:Enter;120:log", "--game", "~/Games/CUSA47999"], "/home/deck", out AppArgs? a, out string? error), error);
        Assert.Equal("saves/~/x", a!.SaveDir);
        Assert.Equal("60:Enter;120:log", a.Script);
        Assert.Equal("~/Games/CUSA47999", a.GameDir);        // as typed: the game folder sentences name it so, and GameLayoutResolver.Resolve expands it
    }

    private static string? PathOption(AppArgs a, string option) => option switch
    {
        "--save-dir" => a.SaveDir,
        "--log" => a.LogFile,
        "--report" => a.ReportPath,
        "--shot-dir" => a.ShotDir,
        "--screenshot-dir" => a.ScreenshotDir,
        "--clipboard-file" => a.ClipboardFile,
        "--sweep-dir" => a.SweepDir,
        "--sweep-tour" => a.SweepTour,
        _ => a.Script,
    };
}

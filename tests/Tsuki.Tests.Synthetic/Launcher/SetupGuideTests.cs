// The player's setup guide (package K29): docs/SETUP_GUIDE.md and its plain-text copy docs/SETUP_GUIDE.txt, which the launcher's publish puts next to the programs. The
// guide is prose, so these tests pin the few things that must stay true of it: the two files exist and agree, the text copy is plain text, the launcher project publishes
// it, and the guide names what the programs name (the key table, the FFmpeg libraries, the update-folder names, the profile files, the WebSocket address).
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tsuki.Data.IO;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

public sealed partial class SetupGuideTests
{
    private static string MarkdownPath => Path.Combine(Repo.Root, "docs", "SETUP_GUIDE.md");

    private static string TextPath => Path.Combine(Repo.Root, "docs", "SETUP_GUIDE.txt");

    private static string Markdown => File.ReadAllText(MarkdownPath);

    private static string Text => File.ReadAllText(TextPath);

    [Fact]
    public void BothCopiesExistAndAreNotStubs()
    {
        Assert.True(File.Exists(MarkdownPath), MarkdownPath);
        Assert.True(File.Exists(TextPath), TextPath);
        Assert.True(Markdown.Length > 20_000, $"the Markdown guide is {Markdown.Length} characters");
        Assert.True(Text.Length > 20_000, $"the text guide is {Text.Length} characters");
    }

    [Fact]
    public void TheTextCopyIsPlainText()
    {
        string[] lines = Text.Split('\n');
        Assert.DoesNotContain(lines, l => l.StartsWith('#'));
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith('|'));
        Assert.DoesNotContain("```", Text);
        Assert.DoesNotContain("](http", Text);
        Assert.DoesNotContain("**", Text);
        Assert.DoesNotContain('`', Text);
    }

    [Fact]
    public void EveryHeadingOfTheMarkdownIsInTheTextCopy()
    {
        var headings = Heading().Matches(Markdown).Select(m => m.Groups[1].Value.Replace("`", string.Empty).Trim()).ToList();
        Assert.True(headings.Count >= 20, $"only {headings.Count} headings");
        var textLines = Text.Split('\n').Select(l => l.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal);
        foreach (string heading in headings)
        {
            Assert.Contains(heading, textLines);
        }

        foreach (string section in new[] { "What this is", "What you need", "Getting your game files", "First start", "Playing", "For Japanese learners", "Troubleshooting" })
        {
            Assert.Contains(headings, h => h.Contains(section, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheLauncherPublishesTheTextCopyNextToThePrograms()
    {
        XDocument project = XDocument.Load(Path.Combine(Repo.Src, "Tsuki.Launcher", "Tsuki.Launcher.csproj"));
        XElement? item = project.Descendants("None").FirstOrDefault(e => (string?)e.Attribute("Link") == "SETUP_GUIDE.txt");
        Assert.NotNull(item);
        Assert.Equal(@"..\..\docs\SETUP_GUIDE.txt", (string?)item.Attribute("Include"));
        Assert.Equal("PreserveNewest", (string?)item.Attribute("CopyToPublishDirectory"));
        // the README travels the same way; the guide follows its example
        Assert.Contains(project.Descendants("None"), e => (string?)e.Attribute("Link") == "README.md" && (string?)e.Attribute("CopyToPublishDirectory") == "PreserveNewest");
    }

    [Fact]
    public void TheHelpPageHasAButtonThatOpensTheGuideNextToTheLauncher()
    {
        // the Help & about page (package K27): a button bound to the model's OpenGuideCommand, and the model has that command
        XDocument page = XDocument.Load(Path.Combine(Repo.Src, "Tsuki.Launcher", "Views", "HelpPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement? button = page.Descendants(x + "Button").FirstOrDefault(b => ((string?)b.Attribute("Command"))?.Contains("OpenGuideCommand", StringComparison.Ordinal) == true);
        Assert.NotNull(button);
        Assert.Contains("guide", (string?)button.Attribute("Content") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(page.Descendants(x + "TextBlock"), t => ((string?)t.Attribute("Text"))?.Contains(GameLauncher.SetupGuideFileName, StringComparison.Ordinal) == true);
        // the view model (WPF, not compiled into these tests): the command and the path it opens, pinned in its source
        string model = File.ReadAllText(Path.Combine(Repo.Src, "Tsuki.Launcher", "LauncherModel.cs"));
        Assert.Contains("public ICommand OpenGuideCommand { get; }", model, StringComparison.Ordinal);
        Assert.Contains("OpenGuideCommand = new RelayCommand(_ => OpenGuide());", model, StringComparison.Ordinal);
        Assert.Contains("GameLauncher.FindSetupGuide(AppContext.BaseDirectory)", model, StringComparison.Ordinal);

        // the guide is found next to the launcher only: the published folder and the build output carry it (CopyToOutputDirectory and CopyToPublishDirectory)
        string dir = Path.Combine(Path.GetTempPath(), "tsuki-k27-guide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(GameLauncher.FindSetupGuide(dir));
            string guide = Path.Combine(dir, GameLauncher.SetupGuideFileName);
            File.WriteAllText(guide, "guide");
            Assert.Equal(guide, GameLauncher.FindSetupGuide(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        XDocument project = XDocument.Load(Path.Combine(Repo.Src, "Tsuki.Launcher", "Tsuki.Launcher.csproj"));
        XElement? item = project.Descendants("None").FirstOrDefault(e => (string?)e.Attribute("Link") == GameLauncher.SetupGuideFileName);
        Assert.NotNull(item);
        Assert.Equal("PreserveNewest", (string?)item.Attribute("CopyToOutputDirectory"));
    }

    [Fact]
    public void TheReadmeLinksTheGuideAtTheTop()
    {
        string[] head = File.ReadLines(Path.Combine(Repo.Root, "README.md")).Take(8).ToArray();
        Assert.Contains(head, l => l.Contains("](docs/SETUP_GUIDE.md)", StringComparison.Ordinal));
        Assert.Contains(head, l => l.Contains(GameLauncher.SetupGuideFileName, StringComparison.Ordinal));
    }

    [Fact]
    public void TheGuideStatesTheGameFolderPrecedenceOfTheProgram()
    {
        // Tsukihime.exe: --game, then TSUKI_GAME_DIR, then the remembered game_dir.txt, then the dialog (GameApp.ResolveGameFolder); README and the guide say the same order
        foreach (string text in new[] { Markdown, Text, File.ReadAllText(Path.Combine(Repo.Root, "README.md")) })
        {
            int env = text.IndexOf("TSUKI_GAME_DIR", StringComparison.Ordinal);
            int remembered = text.IndexOf("game_dir.txt", StringComparison.Ordinal);
            Assert.True(env >= 0 && remembered >= 0, "both are named");
            Assert.DoesNotContain("else game_dir.txt, else the TSUKI_GAME_DIR", text.Replace("`", string.Empty), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheGuideNamesWhatTheProgramsName()
    {
        foreach (KeyRow row in KeyTable.Rows)
        {
            Assert.Contains(row.Keys, Markdown);
        }

        foreach (string dll in GameLauncher.FfmpegDlls)
        {
            Assert.Contains(dll, Markdown);
        }

        foreach (string suffix in GameLayoutResolver.PatchSuffixes)
        {
            Assert.Contains(GameLayoutResolver.TitleId + suffix, Markdown, StringComparison.OrdinalIgnoreCase);
        }

        foreach (string name in new[]
        {
            GameLauncher.AppFileName, GameLauncher.LauncherFileName, "save.dat", "settings.ini", "game_dir.txt", "last_run.log", "crash.log",
            @"%APPDATA%\Tsuki", "ws://127.0.0.1:6677", "ExtTextOutW", @"Pictures\Tsukihime", "eboot.bin", "param.sfo", "https://github.com/shadps4-emu/shadPS4",
        })
        {
            Assert.Contains(name, Markdown);
            Assert.Contains(name, Text);
        }
    }

    [Fact]
    public void TheGuideCarriesNoPersonalPathAndNoGameDownloadLink()
    {
        // the names of the account running the test (the Windows user name and the profile folder's name): the guide must not carry whoever wrote it
        string[] accountNames = [Environment.UserName, Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))];
        foreach (string text in new[] { Markdown, Text })
        {
            Assert.DoesNotContain(@"Users\", text);
            Assert.DoesNotContain("Users/", text);
            foreach (string name in accountNames.Where(n => n.Length >= 5))
            {
                Assert.DoesNotContain(name, text, StringComparison.Ordinal);
            }
            // the one link the guide may carry is the shadPS4 project page
            var links = Link().Matches(text).Select(m => m.Value).Distinct().ToList();
            Assert.All(links, l => Assert.StartsWith("https://github.com/shadps4-emu/shadPS4", l));
        }
    }

    [GeneratedRegex(@"^#{1,3}\s+(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"https?://[^\s)>\]]+")]
    private static partial Regex Link();
}

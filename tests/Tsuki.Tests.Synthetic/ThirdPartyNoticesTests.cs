using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Tsuki.Tests.Synthetic;

/// <summary>
/// docs/THIRD_PARTY.md is the notice file the launcher's Help page points at (review R1, core): every MPL file of the knife port is in its table and nothing else is
/// (knife_port.md 1), every licence the Help page names (knife, FFmpeg, SDL3, the MIT libraries) has a section there, and the FFmpeg build is LGPL v3 (ARCHITECTURE 0.5),
/// which the setup script's SOURCE.txt must say too. Package K30 (docs/PUBLISHING.md findings 4 and 5): the four MIT notices are complete, the licence texts the publish
/// copies are tracked files (not the git-ignored third_party/ and ref/ folders), the root LICENSE is the MPL-2.0 text, a Release publish carries no .pdb files, and the
/// README and CONTRIBUTING.md state the licence and the no-game-data rule.
/// </summary>
public sealed partial class ThirdPartyNoticesTests
{
    private static string Notices => File.ReadAllText(Path.Combine(Repo.Root, "docs", "THIRD_PARTY.md"));

    private static string LauncherDir => Path.Combine(Repo.Src, "Tsuki.Launcher");

    [Fact]
    public void EveryMplFileIsListedAndEveryListedFileCarriesTheHeader()
    {
        var headed = Repo.Projects()
            .SelectMany(Repo.SourceFiles)
            .Where(f => Repo.Relative(f).StartsWith("src/", StringComparison.Ordinal) && File.ReadLines(f).Take(8).Any(l => l.Contains("Mozilla Public", StringComparison.Ordinal)))
            .Select(Repo.Relative)
            .ToHashSet(StringComparer.Ordinal);
        var listed = ListedFile().Matches(Notices).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.True(headed.Count > 0, "no file under src/ carries the MPL header");
        Assert.True(headed.SetEquals(listed),
            "MPL files not in the table: " + string.Join(", ", headed.Except(listed).Order(StringComparer.Ordinal)) +
            "; table rows without the header: " + string.Join(", ", listed.Except(headed).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void EveryLicenceOfTheHelpPageHasASection()
    {
        string help = File.ReadAllText(Path.Combine(LauncherDir, "Views", "HelpPage.xaml"));
        var names = LicenseName().Matches(help).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["knife", "FFmpeg", "SDL3", "MIT libraries"], names);
        var sections = Section().Matches(Notices).Select(m => m.Groups[1].Value).ToList();
        foreach (string name in names)
        {
            Assert.Contains(sections, s => s.StartsWith(name + " (", StringComparison.Ordinal));
        }

        Assert.Contains("## FFmpeg (LGPL v3 or later)", Notices);
        Assert.Contains("## SDL3 (zlib)", Notices);
        Assert.Contains("## knife (MPL-2.0)", Notices);
        Assert.Contains("## MIT libraries (.NET runtime and WPF, Silk.NET, SDL3-CS, FFmpeg.AutoGen)", Notices);
    }

    [Fact]
    public void TheFfmpegSetupScriptWritesTheLgplV3Notice()
    {
        string script = File.ReadAllText(Path.Combine(Repo.Root, "tools", "setup", "ffmpeg_dlls.ps1"));
        Assert.Contains("'licence: LGPL v3 or later", script);
        Assert.DoesNotContain("v2.1", script);
        Assert.DoesNotContain("v2.1", Notices);
    }

    /// <summary>Finding 5: the four MIT components that ship as DLLs each have their copyright line and the permission notice, at the pinned versions.</summary>
    [Fact]
    public void TheMitNoticesCoverEveryShippedBinding()
    {
        int at = Notices.IndexOf("## MIT libraries (", StringComparison.Ordinal);
        Assert.True(at >= 0, "THIRD_PARTY.md has no MIT section");
        string mit = Notices[at..];
        foreach (string name in new[] { ".NET runtime and WPF", "Silk.NET", "SDL3-CS", "FFmpeg.AutoGen" })
        {
            Assert.Contains("### " + name, mit, StringComparison.Ordinal);
        }

        Assert.Equal(4, Regex.Matches(mit, "Permission is hereby granted, free of charge").Count);
        Assert.Equal(4, Regex.Matches(mit, "The above copyright notice and this permission notice shall be included").Count);
        Assert.Equal(4, Regex.Matches(mit, "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND").Count);
        Assert.Contains("Copyright (c) .NET Foundation and Contributors", mit, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2024 ppy Pty Ltd", mit, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2025 Ruslan Balanukhin", mit, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2019-2020 Ultz Limited", mit, StringComparison.Ordinal);

        // the versions named are the pinned ones
        var packages = XDocument.Load(Path.Combine(Repo.Root, "Directory.Packages.props"));
        foreach (XElement pv in packages.Descendants("PackageVersion").Where(e => (string?)e.Attribute("Include") is "ppy.SDL3-CS" or "Silk.NET.OpenGL" or "FFmpeg.AutoGen"))
        {
            Assert.Contains((string)pv.Attribute("Version")!, mit, StringComparison.Ordinal);
        }
    }

    /// <summary>Finding 5: the licence texts the publish ships are tracked copies under src/Tsuki.Launcher/Licenses, copied unconditionally; the root LICENSE is the same MPL-2.0 text.</summary>
    [Fact]
    public void TheLicenceTextsAreTrackedCopiesAndThePublishCopiesThem()
    {
        string licenses = Path.Combine(LauncherDir, "Licenses");
        string lgpl = File.ReadAllText(Path.Combine(licenses, "LGPL-3.0.txt"));
        Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", lgpl, StringComparison.Ordinal);
        Assert.Contains("Version 3, 29 June 2007", lgpl, StringComparison.Ordinal);
        Assert.Contains("6. Revised Versions of the GNU Lesser General Public License.", lgpl, StringComparison.Ordinal);
        // the LGPL v3 "incorporates the terms and conditions of version 3 of the GNU General Public License", so that text ships beside it (package K31)
        Assert.Contains("incorporates the terms and conditions of version 3 of the GNU General Public License", Regex.Replace(lgpl, @"\s+", " "), StringComparison.Ordinal);
        string gpl = File.ReadAllText(Path.Combine(licenses, "GPL-3.0.txt"));
        Assert.Contains("GNU GENERAL PUBLIC LICENSE", gpl, StringComparison.Ordinal);
        Assert.DoesNotContain("LESSER", gpl, StringComparison.Ordinal);
        Assert.Contains("Version 3, 29 June 2007", gpl, StringComparison.Ordinal);
        Assert.Contains("17. Interpretation of Sections 15 and 16.", gpl, StringComparison.Ordinal);
        Assert.Contains("How to Apply These Terms to Your New Programs", gpl, StringComparison.Ordinal);
        Assert.Contains("FFmpeg-GPL-3.0.txt", Notices, StringComparison.Ordinal);
        Assert.Contains("FFmpeg-GPL-3.0.txt", File.ReadAllText(Path.Combine(licenses, "FFmpeg-SOURCE.txt")), StringComparison.Ordinal);
        string mpl = File.ReadAllText(Path.Combine(licenses, "MPL-2.0.txt"));
        Assert.StartsWith("Mozilla Public License Version 2.0", mpl, StringComparison.Ordinal);
        Assert.Contains("Exhibit A - Source Code Form License Notice", mpl, StringComparison.Ordinal);
        Assert.Contains("Exhibit B - \"Incompatible With Secondary Licenses\" Notice", mpl, StringComparison.Ordinal);
        Assert.Equal(mpl, File.ReadAllText(Path.Combine(Repo.Root, "LICENSE")));

        var project = XDocument.Load(Path.Combine(LauncherDir, "Tsuki.Launcher.csproj"));
        var none = project.Descendants("None").ToList();
        foreach ((string link, string include) in new[]
        {
            (@"licenses\FFmpeg-LGPL.txt", @"Licenses\LGPL-3.0.txt"),
            (@"licenses\FFmpeg-GPL-3.0.txt", @"Licenses\GPL-3.0.txt"),
            (@"licenses\knife-MPL-2.0.txt", @"Licenses\MPL-2.0.txt"),
            (@"licenses\LICENSE.txt", @"..\..\LICENSE"),
            (@"licenses\FFmpeg-SOURCE.txt", @"Licenses\FFmpeg-SOURCE.txt"),
            (@"licenses\SDL3-zlib.txt", @"Licenses\SDL3-zlib.txt"),
            (@"licenses\THIRD_PARTY.md", @"..\..\docs\THIRD_PARTY.md"),
        })
        {
            XElement item = Assert.Single(none, e => (string?)e.Attribute("Link") == link);
            Assert.Equal(include, (string?)item.Attribute("Include"));
            Assert.Equal("PreserveNewest", (string?)item.Attribute("CopyToPublishDirectory"));
            Assert.Null(item.Attribute("Condition"));
            Assert.True(File.Exists(Path.Combine(LauncherDir, include)), include + " is missing");
        }

        // nothing is taken from the git-ignored folders any more (a clean clone has neither)
        Assert.DoesNotContain(none, e => ((string?)e.Attribute("Include") ?? "").Contains(@"\third_party\", StringComparison.OrdinalIgnoreCase)
                                        || ((string?)e.Attribute("Include") ?? "").Contains(@"\ref\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finding 5 / table 1.2: a Release publish produces no .pdb files (Directory.Build.props, conditioned on the SDK's _IsPublishing).</summary>
    [Fact]
    public void ReleasePublishesCarryNoDebugSymbols()
    {
        var props = XDocument.Load(Path.Combine(Repo.Root, "Directory.Build.props"));
        XElement group = Assert.Single(props.Descendants("PropertyGroup"), g => ((string?)g.Attribute("Condition") ?? "").Contains("_IsPublishing", StringComparison.Ordinal));
        Assert.Contains("'Release'", (string)group.Attribute("Condition")!, StringComparison.Ordinal);
        Assert.Equal("none", group.Element("DebugType")?.Value);
        Assert.Equal("false", group.Element("DebugSymbols")?.Value);
    }

    /// <summary>Section 3.5 / 3.6 of docs/PUBLISHING.md: the README's first screen, CONTRIBUTING.md and the Help page carry the disclaimer, the licence line and the no-game-data rule.</summary>
    [Fact]
    public void TheReadmeContributingAndHelpPageCarryTheDisclaimerAndTheLicence()
    {
        const string Disclaimer = "this project does not provide the files or link to any source of them, and it will not accept contributions that do";
        string readme = File.ReadAllText(Path.Combine(Repo.Root, "README.md"));
        string help = File.ReadAllText(Path.Combine(LauncherDir, "Views", "HelpPage.xaml"));
        string contributing = File.ReadAllText(Path.Combine(Repo.Root, "CONTRIBUTING.md"));

        // the README wraps the disclaimer in a block quote (line breaks and "> " markers); the Help page has it on one line
        string readmeProse = Regex.Replace(readme.Replace("\n>", "\n"), @"\s+", " ");
        Assert.Contains("unofficial fan project", readmeProse, StringComparison.Ordinal);
        Assert.Contains(Disclaimer, readmeProse, StringComparison.Ordinal);
        Assert.Contains(Disclaimer, help, StringComparison.Ordinal);
        Assert.Contains("MPL-2.0", readme, StringComparison.Ordinal);
        Assert.Contains("](LICENSE)", readme, StringComparison.Ordinal);
        Assert.Contains("](CONTRIBUTING.md)", readme, StringComparison.Ordinal);
        Assert.Contains("](docs/THIRD_PARTY.md)", readme, StringComparison.Ordinal);
        Assert.Contains("AI coding agents", readme, StringComparison.Ordinal);
        Assert.Contains("/releases", readme, StringComparison.Ordinal);

        Assert.Contains("No game data", contributing, StringComparison.Ordinal);
        Assert.Contains("screenshots of the game", contributing, StringComparison.Ordinal);
        Assert.Contains("last_run.log", contributing, StringComparison.Ordinal);
        Assert.Contains(@"dotnet build src\Tsuki.slnx", contributing, StringComparison.Ordinal);
        Assert.Contains("Mozilla Public License 2.0", contributing, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^\| `(src/[^`]+\.cs)` \|", RegexOptions.Multiline)]
    private static partial Regex ListedFile();

    [GeneratedRegex(@"<TextBlock Text=""([^""]+)"" Style=""\{StaticResource LicenseName\}""")]
    private static partial Regex LicenseName();

    [GeneratedRegex(@"^## (.+)$", RegexOptions.Multiline)]
    private static partial Regex Section();
}

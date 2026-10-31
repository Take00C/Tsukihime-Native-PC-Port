using System.Xml.Linq;
using Tsuki.App.Play;
using Tsuki.Data.Icons;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package K28: a normal build is not a personal one. The Tsukihime.exe and Tsukihime.dll built next to this test carry the port's crescent icon group and no
/// personal mark, the PE readers tell an executable from a text file, and the opt-in lives in one targets file that both programs import and that applies nothing
/// without its property.
/// </summary>
public sealed class PersonalBuildTests
{
    private static string Root => Repo.Root;

    private static string Built(string name) => Path.Combine(AppContext.BaseDirectory, name);

    [Fact]
    public void ANormalBuildCarriesTheCrescentIconGroupAndNoPersonalMark()
    {
        string exe = Built("Tsukihime.exe");
        Assert.True(File.Exists(exe), exe);
        byte[]? group = PeIcons.IconGroup(exe);
        Assert.NotNull(group);
        IReadOnlyList<IconFrame> frames = FaceIcon.DecodeIco(group);
        IReadOnlyList<IconFrame> crescent = FaceIcon.DecodeIco(File.ReadAllBytes(Path.Combine(Root, "assets", "icon", "tsukihime.ico")));
        Assert.NotEmpty(crescent);
        Assert.Equal(crescent.Count, frames.Count);
        for (int i = 0; i < crescent.Count; i++)
        {
            Assert.Equal(crescent[i].Size, frames[i].Size);
            Assert.Equal(crescent[i].Rgba, frames[i].Rgba);
        }

        Assert.Contains(PeIcons.ApplicationIconGroup, PeIcons.IconGroupIds(exe)!);
        Assert.Null(PeIcons.IconGroup(exe, 12345));                                           // no such group
        Assert.Null(PeIcons.Marks(exe));                                                      // the apphost is native: no managed metadata

        AssemblyMarks? marks = PeIcons.Marks(Built("Tsukihime.dll"));
        Assert.NotNull(marks);
        Assert.False(marks.IsPersonalBuild);
        Assert.False(marks.Metadata.ContainsKey(PeIcons.PersonalBuildKey));
        Assert.DoesNotContain("-personal", marks.InformationalVersion ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(AppIdentity.IsPersonalBuild);
        Assert.False(File.Exists(Built("PERSONAL_BUILD.txt")));
    }

    [Fact]
    public void TheReadersAnswerNullForAFileThatIsNotAnExecutable()
    {
        string text = Path.Combine(Path.GetTempPath(), "tsuki-k28-notpe-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(text, "not a program");
        try
        {
            Assert.Null(PeIcons.IconGroup(text));
            Assert.Null(PeIcons.IconGroupIds(text));
            Assert.Null(PeIcons.Marks(text));
        }
        finally
        {
            File.Delete(text);
        }

        Assert.Throws<ArgumentException>(() => PeIcons.IconGroup(" "));
        Assert.Throws<FileNotFoundException>(() => PeIcons.Marks(Path.Combine(Path.GetTempPath(), "tsuki-k28-missing-" + Guid.NewGuid().ToString("N") + ".dll")));
    }

    [Fact]
    public void TheOptInIsOneTargetsFileThatBothProgramsImportAndThatAppliesNothingWithoutItsProperty()
    {
        string targets = Path.Combine(Root, "src", "Tsuki.PersonalIcon.targets");
        Assert.True(File.Exists(targets), targets);
        XDocument xml = XDocument.Load(targets);
        // everything in the file is conditioned on the property: the marks, the target
        foreach (XElement e in xml.Root!.Elements())
        {
            Assert.Equal("'$(TsukiPersonalIcon)' != ''", (string?)e.Attribute("Condition"));
        }

        XElement target = xml.Root.Elements("Target").Single();
        Assert.Equal("TsukiPersonalIcon", (string?)target.Attribute("Name"));
        Assert.Equal("BeforeBuild", (string?)target.Attribute("BeforeTargets"));
        Assert.Contains(target.Descendants("ApplicationIcon"), e => e.Value == "$(_TsukiPersonalIco)");
        Assert.Contains(target.Descendants("_TsukiPersonalIco"), e => e.Value.EndsWith(@"\icons\tsukihime.ico", StringComparison.Ordinal));
        Assert.Contains(target.Descendants("None"), e => (string?)e.Attribute("Link") == ReleaseNoteName && (string?)e.Attribute("CopyToPublishDirectory") == "PreserveNewest");
        Assert.NotEmpty(target.Descendants("Exec"));
        Assert.Contains("face-icon --game", target.ToString(), StringComparison.Ordinal);       // the Cli command the Exec tasks run
        XElement metadata = xml.Root.Descendants("AssemblyMetadata").Single();
        Assert.Equal(PeIcons.PersonalBuildKey, (string?)metadata.Attribute("Include"));
        Assert.Equal("true", (string?)metadata.Attribute("Value"));
        Assert.Contains(xml.Root.Descendants("InformationalVersion"), e => e.Value.EndsWith("-personal", StringComparison.Ordinal));

        foreach (string project in new[] { Path.Combine("Tsuki.App", "Tsuki.App.csproj"), Path.Combine("Tsuki.Launcher", "Tsuki.Launcher.csproj") })
        {
            XDocument csproj = XDocument.Load(Path.Combine(Root, "src", project));
            Assert.Contains(csproj.Root!.Elements("Import"), e => (string?)e.Attribute("Project") == @"..\Tsuki.PersonalIcon.targets");
            Assert.Contains(csproj.Root.Descendants("ApplicationIcon"), e => e.Value.EndsWith(@"assets\icon\tsukihime.ico", StringComparison.Ordinal));
        }

        // the icon a personal build makes lands under obj/ (or the artifacts folder), both git-ignored
        string[] ignored = File.ReadAllLines(Path.Combine(Root, ".gitignore"));
        Assert.Contains("obj/", ignored);
        Assert.Contains("artifacts/", ignored);
    }

    private const string ReleaseNoteName = "PERSONAL_BUILD.txt";
}

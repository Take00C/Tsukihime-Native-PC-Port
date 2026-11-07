using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Tsuki.Tests.Synthetic;

/// <summary>
/// The project-reference rules of ARCHITECTURE 3.1, checked on the .csproj files themselves
/// (ROADMAP M0 scope 3 and acceptance 1). A new project, reference or native package needs the
/// lead: change the tables below in the same review.
/// </summary>
public sealed partial class ProjectRulesTests
{
    private const string Data = "Tsuki.Data";
    private const string Engine = "Tsuki.Engine";
    private const string Media = "Tsuki.Media";
    private const string Render = "Tsuki.Render";
    private const string Platform = "Tsuki.Platform";
    private const string Ui = "Tsuki.Ui";
    private const string DevBridge = "Tsuki.DevBridge";
    private const string App = "Tsuki.App";
    private const string Cli = "Tsuki.Cli";
    private const string Launcher = "Tsuki.Launcher";
    private const string UnixLauncher = "Tsuki.Launcher.Avalonia";
    private const string AvaloniaTests = "Tsuki.Tests.Avalonia";

    private static readonly string[] Libraries = [Data, Engine, Media, Render, Platform, Ui, DevBridge];
    private static readonly string[] Shells = [Render, Media, Platform];
    private static readonly string[] Tests = ["Tsuki.Tests.Synthetic", "Tsuki.Tests.Data", "Tsuki.Tests.Render", AvaloniaTests];
    private static readonly string[] Spikes = ["S1.AudioLoop", "S2.MovieSync", "S3.Blend1080", "S4.BigPicture"];

    /// <summary>
    /// Which project may reference which (ARCHITECTURE 3.1):
    /// Tsuki.Engine references nothing, so it sees Tsuki.Data only through Contracts/IGameData;
    /// Tsuki.Data references Tsuki.Engine only, to implement those contracts;
    /// only Tsuki.App, Tsuki.Cli, the tests (and the throwaway spikes) reference Render, Media or Platform.
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedReferences = BuildAllowedReferences();

    /// <summary>Native packages and the one library that may wrap each (ARCHITECTURE 0.1, 3.1).</summary>
    private static readonly (string Package, bool Prefix, string Owner)[] NativePackages =
    [
        ("ppy.SDL3-CS", false, Platform),
        ("Silk.NET.", true, Render),
        ("FFmpeg.AutoGen", false, Media),
    ];

    /// <summary>Settings that only Directory.Build.props / Directory.Packages.props may set.</summary>
    private static readonly string[] CentralProperties =
    [
        "TargetFramework", "TargetFrameworks", "LangVersion", "Nullable", "TreatWarningsAsErrors",
        "MSBuildTreatWarningsAsErrors", "WarningsNotAsErrors", "InvariantGlobalization", "Deterministic",
        "ManagePackageVersionsCentrally",
    ];

    [Fact]
    public void EveryProjectHasARule()
    {
        var found = Repo.Projects().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = found.Where(n => !AllowedReferences.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var missing = AllowedReferences.Keys.Where(n => !found.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(unknown.Count == 0, "projects without a rule (ask the lead): " + string.Join(", ", unknown));
        Assert.True(missing.Count == 0, "projects of ARCHITECTURE 3.1 missing: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryProjectIsInTheSolution()
    {
        var xml = XDocument.Load(Repo.SolutionFile);
        var inSolution = xml.Descendants("Project")
            .Select(e => Path.GetFullPath(Path.Combine(Repo.Src, (string)e.Attribute("Path")!)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notInSolution = Repo.Projects().Where(p => p.Name is not UnixLauncher and not AvaloniaTests && !inSolution.Contains(Path.GetFullPath(p.Path))).Select(p => Repo.Relative(p.Path)).ToList();
        var stale = inSolution.Where(p => !File.Exists(p)).Select(Repo.Relative).ToList();
        Assert.DoesNotContain(inSolution, path => path.Contains(UnixLauncher, StringComparison.OrdinalIgnoreCase) || path.Contains(AvaloniaTests, StringComparison.OrdinalIgnoreCase));
        Assert.True(notInSolution.Count == 0, "projects missing from src/Tsuki.slnx: " + string.Join(", ", notInSolution));
        Assert.True(stale.Count == 0, "src/Tsuki.slnx lists missing projects: " + string.Join(", ", stale));
    }

    [Fact]
    public void EveryUnixProjectIsInTheLinuxSolution()
    {
        // src/Tsuki.Linux.slnx is what Linux and macOS build (CI included): everything but the WPF launcher and the Windows spikes
        string solution = Path.Combine(Repo.Src, "Tsuki.Linux.slnx");
        var inSolution = XDocument.Load(solution).Descendants("Project")
            .Select(e => Path.GetFullPath(Path.Combine(Repo.Src, (string)e.Attribute("Path")!)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool WindowsOnly(string name) => name == Launcher || Spikes.Contains(name, StringComparer.Ordinal);
        var notInSolution = Repo.Projects().Where(p => !WindowsOnly(p.Name) && !inSolution.Contains(Path.GetFullPath(p.Path))).Select(p => Repo.Relative(p.Path)).ToList();
        var windowsOnly = inSolution.Where(p => WindowsOnly(Path.GetFileNameWithoutExtension(p))).Select(Repo.Relative).ToList();
        var stale = inSolution.Where(p => !File.Exists(p)).Select(Repo.Relative).ToList();
        Assert.True(notInSolution.Count == 0, "projects missing from src/Tsuki.Linux.slnx: " + string.Join(", ", notInSolution));
        Assert.True(windowsOnly.Count == 0, "Windows-only projects in src/Tsuki.Linux.slnx: " + string.Join(", ", windowsOnly));
        Assert.True(stale.Count == 0, "src/Tsuki.Linux.slnx lists missing projects: " + string.Join(", ", stale));
    }

    [Fact]
    public void AvaloniaBuildTelemetryIsSwitchedOff()
    {
        // Avalonia.BuildServices, restored with Avalonia, runs a build-time usage report in its AvaloniaStats target before every compile of the Unix launcher and its
        // tests. Directory.Build.targets is imported after the package targets, so its empty AvaloniaStats replaces that target.
        XDocument targets = XDocument.Load(Path.Combine(Repo.Root, "Directory.Build.targets"));
        XElement stats = Assert.Single(targets.Root!.Elements("Target"), t => (string?)t.Attribute("Name") == "AvaloniaStats");
        Assert.Empty(stats.Elements());
        Assert.Equal(["Name"], stats.Attributes().Select(a => a.Name.LocalName));

        // where the package is restored (CI builds the launcher before these tests), its target still has the name this override replaces
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string services = Path.Combine(packages, "avalonia.buildservices");
        foreach (string file in Directory.Exists(services) ? Directory.EnumerateFiles(services, "*.targets", SearchOption.AllDirectories) : [])
        {
            var names = XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "Target").Select(e => (string?)e.Attribute("Name")).ToList();
            Assert.True(names.All(n => n == "AvaloniaStats"), $"{file} defines {string.Join(", ", names)}: replace each of its targets in Directory.Build.targets");
        }
    }

    [Fact]
    public void ProjectReferencesFollowArchitecture31()
    {
        var violations = ReferenceViolations(Repo.Projects());
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void EngineReferencesNoProjectAndDataOnlyTheEngine()
    {
        var projects = Repo.Projects().ToDictionary(p => p.Name, StringComparer.Ordinal);
        Assert.Empty(projects[Engine].ProjectReferences);
        Assert.All(projects[Data].ProjectReferences, r => Assert.Equal(Engine, r));
    }

    [Fact]
    public void OnlyAppCliTestsAndSpikesReferenceRenderMediaOrPlatform()
    {
        var allowed = new HashSet<string>([App, Cli, .. Tests, .. Spikes], StringComparer.Ordinal);
        var offenders = Repo.Projects()
            .Where(p => !allowed.Contains(p.Name))
            .SelectMany(p => p.ProjectReferences.Where(r => Shells.Contains(r, StringComparer.Ordinal)).Select(r => $"{p.Name} -> {r}"))
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    [Fact]
    public void NativePackagesOnlyInTheirOwnerTestsAndSpikes()
    {
        var violations = PackageViolations(Repo.Projects());
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void DataAndEngineMakeNoPInvoke()
    {
        var offenders = new List<string>();
        foreach (var project in Repo.Projects().Where(p => p.Name is Data or Engine))
        {
            foreach (string file in Repo.SourceFiles(project))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (NativeInterop().IsMatch(lines[i]))
                    {
                        offenders.Add($"{Repo.Relative(file)}:{i + 1}: {lines[i].Trim()}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, "native interop in Tsuki.Data / Tsuki.Engine (ARCHITECTURE 3.1):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void ProjectsKeepTheCentralSettings()
    {
        var offenders = new List<string>();
        foreach (var project in Repo.Projects())
        {
            // the launcher is a WPF program: its target framework carries the -windows suffix and WPF needs culture data (the two exceptions, package K17)
            offenders.AddRange(CentralProperties
                .Where(name => project.Property(name).Any() && !(project.Name == Launcher && name is "TargetFramework" or "InvariantGlobalization"))
                .Select(name => $"{project.Name}: sets {name}"));
            offenders.AddRange(project.ItemElements("PackageReference")
                .Where(e => e.Attribute("Version") is not null || e.Attribute("VersionOverride") is not null)
                .Select(e => $"{project.Name}: {(string?)e.Attribute("Include")} has its own version (use Directory.Packages.props)"));
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void AppIsWinExeCliAndTestsAreExe()
    {
        var projects = Repo.Projects().ToDictionary(p => p.Name, StringComparer.Ordinal);
        Assert.Equal(["WinExe"], projects[App].Property("OutputType"));
        Assert.Equal(["WinExe"], projects[Launcher].Property("OutputType"));
        Assert.Equal(["net10.0-windows"], projects[Launcher].Property("TargetFramework"));
        Assert.Equal(["false"], projects[Launcher].Property("InvariantGlobalization"));
        Assert.Equal(["Exe"], projects[Cli].Property("OutputType"));
        Assert.All(Tests, t => Assert.Equal(["Exe"], projects[t].Property("OutputType")));
        Assert.All(Libraries, l => Assert.Empty(projects[l].Property("OutputType")));
    }

    [Fact]
    public void ThePlayerAndBothLaunchersHaveOneVersionAndTheDocsNameIt()
    {
        // release-sums and the updaters refuse a release whose player and launcher assemblies disagree, and the Mac workflow names its ZIPs after this
        // version (review ci-tests-docs-6, mac-ci-7): the bump commit sets all three projects and these doc lines together
        var projects = Repo.Projects().ToDictionary(p => p.Name, StringComparer.Ordinal);
        var versions = new[] { App, Launcher, UnixLauncher }.Select(name => $"{name} {string.Join(",", projects[name].Property("Version"))}").ToList();
        var distinct = new[] { App, Launcher, UnixLauncher }.SelectMany(name => projects[name].Property("Version")).Distinct(StringComparer.Ordinal).ToList();
        Assert.True(distinct.Count == 1 && versions.All(v => v.Split(' ')[1] == distinct[0]), "set one <Version> in all three projects: " + string.Join("; ", versions));
        string version = distinct[0];
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        Assert.Contains($"Current source version is **{version}**.", File.ReadAllText(Path.Combine(Repo.Root, "README.md")), StringComparison.Ordinal);
        Assert.Contains($"Current source versions are **{version}**", File.ReadAllText(Path.Combine(Repo.Root, "docs", "PUBLISHING.md")), StringComparison.Ordinal);
        foreach (string guide in new[] { "SETUP_GUIDE.md", "SETUP_GUIDE.txt" })
        {
            Assert.Contains($"This guide describes version {version}.", File.ReadAllText(Path.Combine(Repo.Root, "docs", guide)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheCheckerCatchesForbiddenReferencesAndPackages()
    {
        // A synthetic project set that breaks every rule once, so the tests above cannot pass vacuously.
        var bad = new[]
        {
            Fake(Engine, refs: [Data]),
            Fake(Ui, refs: [Render]),
            Fake(Data, packages: ["FFmpeg.AutoGen"]),
            Fake(Media, packages: ["Silk.NET.OpenGL"]),
        };
        var refViolations = ReferenceViolations(bad);
        var pkgViolations = PackageViolations(bad);
        Assert.Contains("Tsuki.Engine -> Tsuki.Data is not allowed", refViolations);
        Assert.Contains("Tsuki.Ui -> Tsuki.Render is not allowed", refViolations);
        Assert.Contains(pkgViolations, v => v.StartsWith("Tsuki.Data: FFmpeg.AutoGen", StringComparison.Ordinal));
        Assert.Contains(pkgViolations, v => v.StartsWith("Tsuki.Media: Silk.NET.OpenGL", StringComparison.Ordinal));
        Assert.Matches(NativeInterop(), "    [DllImport(\"avcodec-63\")]");
        Assert.Matches(NativeInterop(), "    private static extern int f();");
        Assert.DoesNotMatch(NativeInterop(), "extern alias Foo;");
    }

    private static List<string> ReferenceViolations(IEnumerable<ProjectFile> projects)
    {
        var list = new List<string>();
        foreach (var project in projects)
        {
            if (!AllowedReferences.TryGetValue(project.Name, out string[]? allowed))
            {
                list.Add($"{project.Name} has no rule");
                continue;
            }

            list.AddRange(project.ProjectReferences
                .Where(r => !allowed.Contains(r, StringComparer.Ordinal))
                .Select(r => $"{project.Name} -> {r} is not allowed"));
        }

        return list;
    }

    private static List<string> PackageViolations(IEnumerable<ProjectFile> projects)
    {
        var list = new List<string>();
        foreach (var project in projects)
        {
            bool free = Tests.Contains(project.Name, StringComparer.Ordinal) || Spikes.Contains(project.Name, StringComparer.Ordinal);
            foreach (string package in project.PackageReferences)
            {
                foreach (var (name, prefix, owner) in NativePackages)
                {
                    bool match = prefix ? package.StartsWith(name, StringComparison.OrdinalIgnoreCase) : string.Equals(package, name, StringComparison.OrdinalIgnoreCase);
                    if (match && !free && project.Name != owner)
                    {
                        list.Add($"{project.Name}: {package} is a native package owned by {owner}");
                    }
                }
            }
        }

        return list;
    }

    private static Dictionary<string, string[]> BuildAllowedReferences()
    {
        string[] appRefs = Libraries;
        string[] testRefs = [.. Libraries, App, Cli];
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [Engine] = [],
            [Data] = [Engine],
            [Media] = [Engine, Data],
            [Render] = [Engine, Data],
            [Platform] = [Engine],
            [Ui] = [Engine, Data],
            [DevBridge] = [Engine, Data, Ui],
            [App] = appRefs,
            [Cli] = appRefs,
            // package K17: the launcher reads the game folder and the save format; it builds Tsuki.App first (no assembly reference) and never references SDL / OpenGL / FFmpeg
            [Launcher] = [Data, Engine, Ui, App],
            [UnixLauncher] = [Data, Engine, Ui, App],
        };
        foreach (string t in Tests)
        {
            map[t] = t == AvaloniaTests ? [.. testRefs, UnixLauncher] : testRefs;
        }

        foreach (string s in Spikes)
        {
            map[s] = Libraries;
        }

        return map;
    }

    private static ProjectFile Fake(string name, string[]? refs = null, string[]? packages = null)
    {
        var root = new XElement("Project");
        var items = new XElement("ItemGroup");
        foreach (string r in refs ?? [])
        {
            items.Add(new XElement("ProjectReference", new XAttribute("Include", $"..\\{r}\\{r}.csproj")));
        }

        foreach (string p in packages ?? [])
        {
            items.Add(new XElement("PackageReference", new XAttribute("Include", p)));
        }

        root.Add(items);
        return ProjectFile.FromXml(Path.Combine(Path.GetTempPath(), name, name + ".csproj"), new XDocument(root));
    }

    /// <summary>P/Invoke and other native entry points (DllImport, LibraryImport, extern methods, NativeLibrary, unmanaged function pointers).</summary>
    [GeneratedRegex(@"\bDllImport\b|\bLibraryImport\b|\bNativeLibrary\s*\.|\bextern\b(?!\s+alias\b)|delegate\s*\*\s*unmanaged|GetDelegateForFunctionPointer", RegexOptions.CultureInvariant)]
    private static partial Regex NativeInterop();
}

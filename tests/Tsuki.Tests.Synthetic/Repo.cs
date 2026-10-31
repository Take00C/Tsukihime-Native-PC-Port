using System.Xml.Linq;

namespace Tsuki.Tests.Synthetic;

/// <summary>Locates the repository and reads its project files (no game data).</summary>
internal static class Repo
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    /// <summary>The repository root: the first ancestor of the test binary that holds src/Tsuki.slnx.</summary>
    public static string Root => RootPath.Value;

    public static string Src => Path.Combine(Root, "src");

    public static string SolutionFile => Path.Combine(Src, "Tsuki.slnx");

    /// <summary>Every .csproj under src/ and tests/, outside bin/ and obj/.</summary>
    public static IReadOnlyList<ProjectFile> Projects()
    {
        var list = new List<ProjectFile>();
        foreach (string dir in new[] { Path.Combine(Root, "src"), Path.Combine(Root, "tests") })
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories))
            {
                if (!IsBuildOutput(path))
                {
                    list.Add(ProjectFile.Load(path));
                }
            }
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    /// <summary>Every .cs source file of a project, outside bin/ and obj/.</summary>
    public static IEnumerable<string> SourceFiles(ProjectFile project) =>
        Directory.EnumerateFiles(project.Directory, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p))
            .OrderBy(p => p, StringComparer.Ordinal);

    public static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    private static bool IsBuildOutput(string path)
    {
        string p = path.Replace('\\', '/');
        return p.Contains("/bin/", StringComparison.OrdinalIgnoreCase) || p.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Tsuki.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"src/Tsuki.slnx not found above {AppContext.BaseDirectory}");
    }
}

/// <summary>The parts of a .csproj the rules look at.</summary>
internal sealed record ProjectFile(
    string Path,
    string Name,
    string Directory,
    XDocument Xml,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences)
{
    public static ProjectFile Load(string path) => FromXml(path, XDocument.Load(path));

    public static ProjectFile FromXml(string path, XDocument xml)
    {
        string dir = System.IO.Path.GetDirectoryName(path)!;
        var projectRefs = Items(xml, "ProjectReference")
            .Select(i => System.IO.Path.GetFileNameWithoutExtension(i.Replace('\\', '/')))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var packageRefs = Items(xml, "PackageReference")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        return new ProjectFile(path, System.IO.Path.GetFileNameWithoutExtension(path), dir, xml, projectRefs, packageRefs);
    }

    /// <summary>Values of a property anywhere in the file (any condition).</summary>
    public IEnumerable<string> Property(string name) =>
        Xml.Descendants().Where(e => e.Name.LocalName == name && e.Parent?.Name.LocalName == "PropertyGroup").Select(e => e.Value.Trim());

    /// <summary>Every item element of a type.</summary>
    public IEnumerable<XElement> ItemElements(string itemType) =>
        Xml.Descendants().Where(e => e.Name.LocalName == itemType);

    private static IEnumerable<string> Items(XDocument xml, string itemType) =>
        xml.Descendants()
            .Where(e => e.Name.LocalName == itemType)
            .Select(e => (string?)e.Attribute("Include"))
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!);
}

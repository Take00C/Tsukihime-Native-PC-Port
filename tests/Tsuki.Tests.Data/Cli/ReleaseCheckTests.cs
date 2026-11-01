using Tsuki.Cli;
using Tsuki.Cli.Commands;
using Tsuki.Data.Icons;

namespace Tsuki.Tests.Data.Cli;

/// <summary>
/// Package K28: <c>Tsuki.Cli release-check</c> refuses a folder that is a personal build. No game data is needed: the folders are made of copies of the Tsukihime.exe
/// and Tsukihime.dll built next to this test (a normal build), with the marks of a personal build added one at a time.
/// </summary>
public sealed class ReleaseCheckTests : IDisposable
{
    private static string HostName => OperatingSystem.IsWindows() ? "Tsukihime.exe" : "Tsukihime";

    private static string IconName => OperatingSystem.IsWindows() ? "Tsukihime.exe" : "Tsukihime.dll";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k28-release-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _crescent;

    public ReleaseCheckTests()
    {
        Directory.CreateDirectory(_root);
        _crescent = File.ReadAllBytes(Path.Combine(RepoRoot(), "assets", "icon", "tsukihime.ico"));
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

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Tsuki.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A folder that looks like a publish of a normal build: the apphost and its assembly, copied from the test output.</summary>
    private string NormalFolder(string name)
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (string file in new[] { HostName, "Tsukihime.dll" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(folder, file));
        }

        return folder;
    }

    [Fact]
    public void ANormalBuildMayBeReleasedAndItsIconIsTheCrescent()
    {
        string folder = NormalFolder("normal");
        var dumped = new List<string>();
        ReleaseCheckResult result = ReleaseCheck.Run(folder, _crescent, (program, frames) => dumped.Add($"{program}:{frames.Count}"));
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        Assert.Contains(result.Notes, n => n.StartsWith(IconName + ": the icon group is the crescent", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.StartsWith("Tsukihime.dll: version", StringComparison.Ordinal) && n.EndsWith("no personal mark", StringComparison.Ordinal));
        Assert.Equal([$"Tsukihime:{FaceIcon.DecodeIco(_crescent).Count}"], dumped);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        string dump = Path.Combine(_root, "dump");
        Assert.Equal(ExitCode.Ok, CliApp.Run(["release-check", folder, "--icon", Path.Combine(RepoRoot(), "assets", "icon", "tsukihime.ico"), "--dump", dump], stdout, stderr));
        Assert.Contains("RESULT: " + folder + " may be released", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
        // the crescent's 256 px entry is PNG-compressed and not decoded (FaceIcon.DecodeIco reads the 32-bit BMP entries); the 16 px frame is one of those
        Assert.Contains(Directory.GetFiles(dump), f => f.EndsWith("Tsukihime_16.png", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePersonalNoteAnywhereInTheFolderIsRefused()
    {
        string folder = NormalFolder("note");
        Directory.CreateDirectory(Path.Combine(folder, "licenses"));
        File.WriteAllText(Path.Combine(folder, "licenses", ReleaseCheck.PersonalNote), "do not share");
        ReleaseCheckResult result = ReleaseCheck.Run(folder, _crescent);
        Assert.False(result.Ok);
        Assert.Single(result.Problems);
        Assert.Contains("PERSONAL_BUILD.txt is there", result.Problems[0], StringComparison.Ordinal);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.Equal(ExitCode.Failed, CliApp.Run(["release-check", folder, "--icon", Path.Combine(RepoRoot(), "assets", "icon", "tsukihime.ico")], stdout, stderr));
        Assert.Contains("must NOT be released (1 problem(s))", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExecutableWhoseIconIsNotTheCrescentIsRefused()
    {
        // the icon images sit in the executable's resources: changing a few pixels of the first one keeps the file a valid PE image with another icon
        string folder = NormalFolder("icon");
        string exe = Path.Combine(folder, IconName);
        byte[] bytes = File.ReadAllBytes(exe);
        byte[] group = PeIcons.IconGroup(exe)!;
        IReadOnlyList<IconFrame> frames = FaceIcon.DecodeIco(group);
        int imageOffset = BitConverter.ToInt32(group, 6 + 12);
        int imageLength = BitConverter.ToInt32(group, 6 + 8);
        byte[] image = group.AsSpan(imageOffset, imageLength).ToArray();
        int at = bytes.AsSpan().IndexOf(image);
        Assert.True(at > 0, "the first icon image was not found in the executable");
        for (int i = 40; i < 40 + 256; i++)
        {
            bytes[at + i] ^= 0x5A;                                                            // a block of pixels of the 16 px frame
        }

        File.WriteAllBytes(exe, bytes);
        IReadOnlyList<IconFrame> changed = FaceIcon.DecodeIco(PeIcons.IconGroup(exe)!);
        Assert.Equal(frames.Count, changed.Count);
        Assert.NotEqual(frames[0].Rgba, changed[0].Rgba);

        ReleaseCheckResult result = ReleaseCheck.Run(folder, _crescent);
        Assert.False(result.Ok);
        Assert.Single(result.Problems);
        Assert.StartsWith(IconName + " carries an icon that is not the port's crescent", result.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ALinuxHostWithoutItsPlayerAssemblyIsRefused()
    {
        string folder = Path.Combine(_root, "linux-host-only");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Tsukihime"), [0x7F, 0x45, 0x4C, 0x46]);
        ReleaseCheckResult result = ReleaseCheck.Run(folder, _crescent);
        Assert.False(result.Ok);
        Assert.Equal("Tsukihime.dll is not in " + folder + ": the Linux player assembly is missing", Assert.Single(result.Problems));
    }

    [Fact]
    public void TheAssemblyMarksAreReadAndAFolderWithoutTheProgramIsNotARelease()
    {
        // this test assembly carries a known AssemblyMetadata (Tsuki.Tests.Data.csproj), so the reader's positive case is covered without a personal build
        AssemblyMarks? marks = PeIcons.Marks(typeof(ReleaseCheckTests).Assembly.Location);
        Assert.NotNull(marks);
        Assert.Equal("k28", marks.Metadata["TsukiTestMark"]);
        Assert.False(marks.IsPersonalBuild);
        Assert.NotNull(marks.InformationalVersion);
        var personal = new AssemblyMarks(new Dictionary<string, string> { [PeIcons.PersonalBuildKey] = "true" }, "1.0.0-personal+abc");
        Assert.True(personal.IsPersonalBuild);

        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        ReleaseCheckResult result = ReleaseCheck.Run(empty, _crescent);
        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("not a published folder of the port", StringComparison.Ordinal));
        Assert.False(ReleaseCheck.Run(Path.Combine(_root, "nowhere"), _crescent).Ok);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.Equal(ExitCode.Usage, CliApp.Run(["release-check"], stdout, stderr));
        Assert.Equal(ExitCode.Usage, CliApp.Run(["release-check", empty, "--icon", Path.Combine(_root, "none.ico")], stdout, stderr));
    }
}

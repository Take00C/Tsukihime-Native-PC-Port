using Tsuki.Data.Icons;

namespace Tsuki.Cli.Commands;

/// <summary>The outcome of <see cref="ReleaseCheck.Run"/>: what forbids a release and what was seen.</summary>
/// <param name="Problems">Each one forbids releasing the folder.</param>
/// <param name="Notes">What was checked, for the record.</param>
public sealed record ReleaseCheckResult(IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    /// <summary>True when the folder may be released.</summary>
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// The rules that keep a PERSONAL build (package K28: a build whose programs carry the Arcueid icon cut from the owner's copy of the game, TYPE-MOON's art) out of a
/// release: the folder to be zipped must hold no <c>PERSONAL_BUILD.txt</c>, its programs' icon groups must be the port's crescent (<c>assets/icon/tsukihime.ico</c>),
/// and its assemblies must carry neither the <c>TsukiPersonalBuild</c> metadata nor a <c>-personal</c> version. The folder must look like a publish of the port
/// (<c>Tsukihime.exe</c> present) so that a wrong path is a failure, not a pass.
/// </summary>
public static class ReleaseCheck
{
    /// <summary>The note a personal build writes beside its programs (src/Tsuki.PersonalIcon.targets).</summary>
    public const string PersonalNote = "PERSONAL_BUILD.txt";

    /// <summary>The programs whose icon group and assembly marks are checked (the apphost and its managed assembly).</summary>
    public static IReadOnlyList<string> Programs { get; } = ["Tsukihime", "TsukihimeLauncher"];

    /// <summary>
    /// Checks <paramref name="folder"/> against <paramref name="crescentIco"/> (the bytes of the port's own icon file). <paramref name="dump"/>, when given, receives
    /// the frames of each program's icon group (for a PNG of what the executable really carries).
    /// </summary>
    public static ReleaseCheckResult Run(string folder, byte[] crescentIco, Action<string, IReadOnlyList<IconFrame>>? dump = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(crescentIco);
        var problems = new List<string>();
        var notes = new List<string>();
        if (!Directory.Exists(folder))
        {
            problems.Add($"{folder} is not a folder");
            return new ReleaseCheckResult(problems, notes);
        }

        if (!File.Exists(Path.Combine(folder, Programs[0] + ".exe")))
        {
            problems.Add($"{Programs[0]}.exe is not in {folder}: not a published folder of the port");
        }

        foreach (string note in Directory.EnumerateFiles(folder, PersonalNote, SearchOption.AllDirectories))
        {
            problems.Add($"{Path.GetRelativePath(folder, note)} is there: the folder is a personal build (it says so itself)");
        }

        IReadOnlyList<IconFrame> crescent = FaceIcon.DecodeIco(crescentIco);
        if (crescent.Count == 0)
        {
            problems.Add("the crescent icon file could not be read, so the programs' icons cannot be compared");
        }

        foreach (string program in Programs)
        {
            string exe = Path.Combine(folder, program + ".exe");
            if (File.Exists(exe))
            {
                byte[]? group = PeIcons.IconGroup(exe);
                if (group is null)
                {
                    problems.Add($"{program}.exe carries no application icon group (id {PeIcons.ApplicationIconGroup})");
                }
                else
                {
                    IReadOnlyList<IconFrame> frames = FaceIcon.DecodeIco(group);
                    dump?.Invoke(program, frames);
                    if (crescent.Count > 0 && SameFrames(crescent, frames))
                    {
                        notes.Add($"{program}.exe: the icon group is the crescent ({frames.Count} frames)");
                    }
                    else if (crescent.Count > 0)
                    {
                        problems.Add($"{program}.exe carries an icon that is not the port's crescent ({frames.Count} frames, {string.Join(", ", frames.Select(f => f.Size))} px): a personal build");
                    }
                }
            }

            string dll = Path.Combine(folder, program + ".dll");
            if (File.Exists(dll))
            {
                AssemblyMarks? marks = PeIcons.Marks(dll);
                if (marks is null)
                {
                    problems.Add($"{program}.dll carries no managed metadata");
                    continue;
                }

                if (marks.IsPersonalBuild)
                {
                    problems.Add($"{program}.dll carries the assembly attribute AssemblyMetadata(\"{PeIcons.PersonalBuildKey}\", \"true\"): a personal build");
                }

                if (marks.InformationalVersion is { } version && version.Contains("-personal", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{program}.dll has the version {version}: a personal build");
                }
                else
                {
                    notes.Add($"{program}.dll: version {marks.InformationalVersion ?? "(none)"}, no personal mark");
                }
            }
        }

        return new ReleaseCheckResult(problems, notes);
    }

    private static bool SameFrames(IReadOnlyList<IconFrame> a, IReadOnlyList<IconFrame> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Size != b[i].Size || !a[i].Rgba.AsSpan().SequenceEqual(b[i].Rgba))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// <c>Tsuki.Cli release-check FOLDER [--icon FILE] [--dump DIR]</c> (package K28): refuses a folder that must not be released (a personal build, see
/// <see cref="ReleaseCheck"/>). <c>--icon</c> names the port's crescent .ico (default: <c>assets/icon/tsukihime.ico</c> of the repository the tool runs from);
/// <c>--dump</c> writes a PNG of every program's icon group, so what the executable carries can be looked at. Exit 0 when the folder may go out, 1 when not.
/// </summary>
public sealed class ReleaseCheckCommand : ICliCommand
{
    /// <inheritdoc/>
    public string Name => "release-check";

    /// <inheritdoc/>
    public string Summary => "refuse a folder that is a personal build (PERSONAL_BUILD.txt, a non-crescent icon, the TsukiPersonalBuild mark); release-check FOLDER [--icon FILE] [--dump DIR]";

    /// <inheritdoc/>
    public int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? folder = null;
        string? icon = null;
        string? dumpDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--icon" when i + 1 < args.Length:
                    icon = args[++i];
                    break;
                case "--dump" when i + 1 < args.Length:
                    dumpDir = args[++i];
                    break;
                default:
                    if (args[i].StartsWith('-') || folder is not null)
                    {
                        error.WriteLine($"release-check: unexpected argument '{args[i]}'");
                        return ExitCode.Usage;
                    }

                    folder = args[i];
                    break;
            }
        }

        if (folder is null)
        {
            error.WriteLine("release-check: name the folder to be released");
            return ExitCode.Usage;
        }

        icon ??= FindRoot() is { } root ? Path.Combine(root, "assets", "icon", "tsukihime.ico") : null;
        if (icon is null || !File.Exists(icon))
        {
            error.WriteLine("release-check: the crescent icon file was not found; pass --icon <assets/icon/tsukihime.ico>");
            return ExitCode.Usage;
        }

        Action<string, IReadOnlyList<IconFrame>>? dump = null;
        if (dumpDir is not null)
        {
            Directory.CreateDirectory(dumpDir);
            dump = (program, frames) =>
            {
                foreach (IconFrame f in frames)
                {
                    PngWriter.Write(Path.Combine(dumpDir, $"{program}_{f.Size}.png"), f.Size, f.Size, f.Rgba);
                }

                output.WriteLine($"{program}.exe: {frames.Count} frames written to {dumpDir}");
            };
        }

        ReleaseCheckResult result = ReleaseCheck.Run(folder, File.ReadAllBytes(icon), dump);
        foreach (string note in result.Notes)
        {
            output.WriteLine("ok      " + note);
        }

        foreach (string problem in result.Problems)
        {
            output.WriteLine("REFUSED " + problem);
        }

        output.WriteLine(result.Ok ? $"RESULT: {folder} may be released" : $"RESULT: {folder} must NOT be released ({result.Problems.Count} problem(s))");
        return result.Ok ? ExitCode.Ok : ExitCode.Failed;
    }

    private static string? FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Tsuki.slnx")))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}

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
/// (<c>Tsukihime.exe</c> on Windows, <c>Tsukihime</c> on Linux, or <c>Tsukihime.app</c> on macOS) so that a wrong path fails. No platform may hold a picture
/// beyond approved branding or game data (<see cref="ReleaseRules.Forbidden"/>); Linux and macOS also check their native programs, libraries and licences
/// (<see cref="ReleaseRules.CheckLinuxFolder"/>, <see cref="ReleaseRules.CheckMacFolder"/>).
/// </summary>
public static partial class ReleaseCheck
{
    /// <summary>The programs whose icon group and assembly marks are checked (the apphost and its managed assembly).</summary>
    public static IReadOnlyList<string> Programs { get; } = ["Tsukihime", "TsukihimeLauncher"];

    /// <summary>
    /// Checks <paramref name="folder"/> against <paramref name="crescentIco"/> (the bytes of the port's own icon file). <paramref name="dump"/>, when given, receives
    /// the frames of each program's icon group (for a PNG of what the executable really carries).
    /// </summary>
    public static ReleaseCheckResult Run(string folder, byte[] crescentIco, Action<string, IReadOnlyList<IconFrame>>? dump = null, byte[]? crescentIcns = null)
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

        string macos = Path.Combine(folder, "Contents", "MacOS");
        bool mac = File.Exists(Path.Combine(macos, Programs[0]));
        bool linux = !mac && !File.Exists(Path.Combine(folder, Programs[0] + ".exe")) && File.Exists(Path.Combine(folder, Programs[0]));
        string programFolder = mac ? macos : folder;
        if (!linux && !mac && !File.Exists(Path.Combine(folder, Programs[0] + ".exe")))
        {
            problems.Add($"{Programs[0]}.exe is not in {folder}: not a published folder of the port");
        }

        foreach (string note in Directory.EnumerateFiles(folder, PersonalNote, SearchOption.AllDirectories))
        {
            problems.Add($"{Path.GetRelativePath(folder, note)} is there: the folder is a personal build (it says so itself)");
        }

        problems.AddRange(ReleaseRules.ForbiddenFiles(folder, mac));
        if (linux)
        {
            ReleaseRules.CheckLinuxFolder(folder, problems, notes);
        }
        else if (mac)
        {
            crescentIcns ??= ReleaseCheckCommand.DefaultMacIcns();
            ReleaseRules.CheckMacFolder(folder, null, crescentIcns, null, problems, notes);
        }

        IReadOnlyList<IconFrame> crescent = FaceIcon.DecodeIco(crescentIco);
        if (crescent.Count == 0)
        {
            problems.Add("the crescent icon file could not be read, so the programs' icons cannot be compared");
        }

        foreach (string program in Programs)
        {
            string iconName = program + (linux || mac ? ".dll" : ".exe");
            string iconPath = Path.Combine(programFolder, iconName);
            if ((linux || mac) && !File.Exists(iconPath))
            {
                problems.Add($"{iconName} is not in {programFolder}: the {(mac ? "Mac" : "Linux")} {(program == Programs[0] ? "player" : "launcher")} assembly is missing");
            }

            if (File.Exists(iconPath))
            {
                byte[]? group = PeIcons.IconGroup(iconPath);
                if (group is null)
                {
                    problems.Add($"{iconName} carries no application icon group (id {PeIcons.ApplicationIconGroup})");
                }
                else
                {
                    IReadOnlyList<IconFrame> frames = FaceIcon.DecodeIco(group);
                    dump?.Invoke(program, frames);
                    if (crescent.Count > 0 && IconProblem(iconPath, crescent) is { } notCrescent)
                    {
                        problems.Add($"{iconName} {notCrescent}");
                    }
                    else if (crescent.Count > 0)
                    {
                        notes.Add($"{iconName}: the icon group is the crescent ({frames.Count} frames)");
                    }
                }
            }

            string dll = Path.Combine(programFolder, program + ".dll");
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
    public string Summary => "refuse a personal or incomplete Windows, Linux or macOS release folder; release-check FOLDER [--icon FILE] [--dump DIR]";

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

        icon ??= DefaultIcon();
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

        ReleaseCheckResult result = ReleaseCheck.Run(folder, File.ReadAllBytes(icon), dump, DefaultMacIcns(icon));
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

    /// <summary><c>assets/icon/tsukihime.ico</c> of the repository the tool runs from (release-check's and release-sums' default <c>--icon</c>), or null.</summary>
    internal static string? DefaultIcon() => FindRoot() is { } root ? Path.Combine(root, "assets", "icon", "tsukihime.ico") : null;

    /// <summary>
    /// The ICNS that package_macos.py makes from the PNGs beside <paramref name="iconPath"/>, or null if they are unavailable (missing, unreadable or not the
    /// crescent's sizes): release-check and release-sums make it for every folder and file, so it must never stop a Windows or Linux check.
    /// </summary>
    internal static byte[]? DefaultMacIcns(string? iconPath = null)
    {
        iconPath ??= DefaultIcon();
        if (iconPath is null)
        {
            return null;
        }

        try
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(iconPath));
            return folder is null ? null : ReleaseRules.MakeMacIcns(
                File.ReadAllBytes(Path.Combine(folder, "tsukihime_16.png")),
                File.ReadAllBytes(Path.Combine(folder, "tsukihime_32.png")),
                File.ReadAllBytes(Path.Combine(folder, "tsukihime_256.png")));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
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

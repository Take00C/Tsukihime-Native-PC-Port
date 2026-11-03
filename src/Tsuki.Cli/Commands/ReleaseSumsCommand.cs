using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Tsuki.Data.Icons;

namespace Tsuki.Cli.Commands;

/// <summary>
/// <c>Tsuki.Cli release-sums FILE... [--out FILE]</c> (package K40, GitHub issue #6): writes <c>SHA256SUMS.txt</c>, the checksum file every release attaches beside its
/// zip, so that the launcher's update button can check the download. One line per file, "&lt;sha-256 hex&gt;  &lt;file name&gt;" (GNU sha256sum's format, LF line ends);
/// by default it is written next to the first file. A release zip (a name that starts with "Tsukihime-" and ends with ".zip") must be named
/// <c>Tsukihime-vX.Y.Z-win-x64.zip</c> (the name the launcher looks for) and must be what the launcher accepts: TsukihimeLauncher.exe and Tsukihime.exe at its top,
/// no PERSONAL_BUILD.txt, and a TsukihimeLauncher.dll of version X.Y.Z without a personal mark. A zip that is not is refused (exit 1) and nothing is written, so a
/// release that the update would refuse cannot get its checksum file by mistake. Other files (a source archive, say) are listed as they are.
/// Package K43 (review): both programs in the zip must carry the port's crescent (<c>--icon FILE</c>, default <c>assets/icon/tsukihime.ico</c> of the repository,
/// the rule of <see cref="ReleaseCheck"/>), and no <c>.icon-old</c> / <c>.icon-new</c> copy may be in it: a folder whose launcher has run carries Arcueid's face in
/// its programs without any personal mark, and this is the last check that sees the zip itself.
/// Package K46: a Linux archive (a name that starts with "Tsukihime-" and ends with ".tar.gz") must be named <c>Tsukihime-vX.Y.Z-linux-x64.tar.gz</c> and hold one
/// folder of that name with the x86-64 player <c>Tsukihime</c> and its libraries (mode 0755), a <c>Tsukihime.dll</c> of version X.Y.Z with the crescent and no personal
/// mark, the licences and only folders and plain files; neither archive may carry a picture or game data (<see cref="ReleaseRules.Forbidden"/>), and a zip and an
/// archive listed together must have the same version. The checksum file then lists both; the Windows launcher reads only its zip's line.
/// </summary>
public sealed partial class ReleaseSumsCommand : ICliCommand
{
    /// <summary>The checksum file's name (the launcher looks for exactly this asset).</summary>
    public const string SumsFileName = "SHA256SUMS.txt";

    /// <inheritdoc/>
    public string Name => "release-sums";

    /// <inheritdoc/>
    public string Summary => "write SHA256SUMS.txt for the files of a GitHub release (the Windows zip, the Linux .tar.gz; the launcher's update checks the zip with it); release-sums FILE... [--out FILE] [--icon FILE]";

    /// <inheritdoc/>
    public int Run(string[] args, TextWriter output, TextWriter error)
    {
        var files = new List<string>();
        string? outPath = null;
        string? icon = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--icon" when i + 1 < args.Length:
                    icon = args[++i];
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        error.WriteLine($"release-sums: unexpected argument '{args[i]}'");
                        return ExitCode.Usage;
                    }

                    files.Add(args[i]);
                    break;
            }
        }

        if (files.Count == 0)
        {
            error.WriteLine("release-sums: name the release files (Tsukihime-vX.Y.Z-win-x64.zip, Tsukihime-vX.Y.Z-linux-x64.tar.gz)");
            return ExitCode.Usage;
        }

        foreach (string f in files)
        {
            if (!File.Exists(f))
            {
                error.WriteLine($"release-sums: {f} does not exist");
                return ExitCode.Usage;
            }
        }

        icon ??= ReleaseCheckCommand.DefaultIcon();
        byte[]? crescentIco = icon is not null && File.Exists(icon) ? File.ReadAllBytes(icon) : null;
        if (crescentIco is null && files.Any(f => IsReleaseZipName(Path.GetFileName(f)) || IsReleaseTarName(Path.GetFileName(f))))
        {
            error.WriteLine("release-sums: the crescent icon file was not found; pass --icon <assets/icon/tsukihime.ico> (the programs in the zip are compared with it)");
            return ExitCode.Usage;
        }

        var problems = new List<string>();
        var lines = new List<string>();
        int releaseZips = 0;
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            if (string.Equals(name, SumsFileName, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{name}: the checksum file does not list itself");
                continue;
            }

            if (IsReleaseZipName(name))
            {
                releaseZips++;
                problems.AddRange(CheckReleaseZip(file, crescentIco!));
                if (ReleaseZipName().Match(name) is { Success: true } zm)
                {
                    versions.Add(zm.Groups[1].Value + "." + zm.Groups[2].Value + "." + zm.Groups[3].Value);
                }
            }
            else if (IsReleaseTarName(name))
            {
                problems.AddRange(CheckReleaseTar(file, crescentIco!));
                if (ReleaseTarName().Match(name) is { Success: true } tm)
                {
                    versions.Add(tm.Groups[1].Value + "." + tm.Groups[2].Value + "." + tm.Groups[3].Value);
                }
            }

            string hex;
            using (FileStream stream = File.OpenRead(file))
            {
                hex = Convert.ToHexStringLower(SHA256.HashData(stream));
            }

            lines.Add($"{hex}  {name}");
        }

        if (lines.Select(l => l[66..]).Distinct(StringComparer.OrdinalIgnoreCase).Count() != lines.Count)
        {
            problems.Add("two files have the same name");
        }

        if (versions.Count > 1)
        {
            problems.Add($"the release files name different versions ({string.Join(", ", versions.Order(StringComparer.Ordinal))}): one release, one version");
        }

        foreach (string problem in problems)
        {
            output.WriteLine("REFUSED " + problem);
        }

        if (problems.Count > 0)
        {
            output.WriteLine($"RESULT: {SumsFileName} was NOT written ({problems.Count.ToString(CultureInfo.InvariantCulture)} problem(s))");
            return ExitCode.Failed;
        }

        outPath ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(files[0]))!, SumsFileName);
        File.WriteAllText(outPath, string.Join("\n", lines) + "\n");
        foreach (string line in lines)
        {
            output.WriteLine(line);
        }

        if (releaseZips == 0)
        {
            output.WriteLine("note: no Tsukihime-vX.Y.Z-win-x64.zip among the files; the launcher's update needs that zip listed");
        }

        output.WriteLine($"RESULT: wrote {outPath}: attach it to the release beside the zip");
        return ExitCode.Ok;
    }

    /// <summary>True for a name release-sums checks as a release zip ("Tsukihime-...zip").</summary>
    private static bool IsReleaseZipName(string name) => name.StartsWith("Tsukihime-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a name release-sums checks as a Linux release archive ("Tsukihime-...tar.gz", package K46).</summary>
    private static bool IsReleaseTarName(string name) => name.StartsWith("Tsukihime-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The reasons the launcher's update would refuse <paramref name="zipPath"/>, or the rules of a release forbid it (empty: it may be released).
    /// <paramref name="crescentIco"/> is the port's crescent .ico (<c>assets/icon/tsukihime.ico</c>), which both programs in the zip must carry.
    /// </summary>
    public static IReadOnlyList<string> CheckReleaseZip(string zipPath, byte[] crescentIco)
    {
        ArgumentNullException.ThrowIfNull(crescentIco);
        string name = Path.GetFileName(zipPath);
        var problems = new List<string>();
        Match m = ReleaseZipName().Match(name);
        if (!m.Success)
        {
            problems.Add($"{name}: a release zip must be named Tsukihime-vX.Y.Z-win-x64.zip, the name the launcher's update looks for");
            return problems;
        }

        string version = $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}";
        IReadOnlyList<IconFrame> crescent = FaceIcon.DecodeIco(crescentIco);
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(zipPath);
            var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
            string top = TopFolder(entries);
            ZipArchiveEntry? Find(string file) => entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), top + file, StringComparison.OrdinalIgnoreCase));
            foreach (string program in new[] { "TsukihimeLauncher.exe", "Tsukihime.exe" })
            {
                if (Find(program) is not { } entry)
                {
                    problems.Add($"{name}: no {program} at the top of the zip (zip the contents of the publish folder)");
                }
                else if (WithExtracted(entry, ".exe", exe => ReleaseCheck.IconProblem(exe, crescent)) is { } notCrescent)
                {
                    // package K43, review: a folder whose launcher has run carries Arcueid in its programs and none of the personal marks
                    problems.Add($"{name}: {program} {notCrescent}; zip a fresh publish folder, never one the launcher ran in");
                }
            }

            if (entries.Any(e => string.Equals(Path.GetFileName(e.FullName.Replace('\\', '/')), ReleaseCheck.PersonalNote, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"{name}: holds {ReleaseCheck.PersonalNote}: a personal build is never released");
            }

            foreach (ZipArchiveEntry e in entries.Where(e => e.FullName.EndsWith(".icon-old", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".icon-new", StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"{name}: holds {e.FullName}, a copy the launcher leaves when it writes the program icons (package K43): the zip was made from a folder the launcher ran in; zip a fresh publish folder");
            }

            foreach (ZipArchiveEntry e in entries)
            {
                // the rules see the path inside the publish folder, as release-check and the tar check do: the README's logo sits under the zip's one folder
                string rel = e.FullName.Replace('\\', '/');
                if (ReleaseRules.Forbidden(rel.StartsWith(top, StringComparison.OrdinalIgnoreCase) ? rel[top.Length..] : rel) is { } forbidden)
                {
                    problems.Add($"{name}: {forbidden}");
                }
            }

            foreach (ZipArchiveEntry e in entries)
            {
                string n = e.FullName.Replace('\\', '/');
                if (n.StartsWith('/') || n.Contains(':', StringComparison.Ordinal) || n.Split('/').Any(part => part is "" or "." or ".."))
                {
                    problems.Add($"{name}: the entry {e.FullName} would land outside the folder; the launcher refuses such a zip");
                }
            }

            if (Find("TsukihimeLauncher.dll") is { } dll)
            {
                string? dllProblem = WithExtracted(dll, ".dll", temp =>
                {
                    AssemblyMarks? marks = PeIcons.Marks(temp);
                    string? info = marks?.InformationalVersion;
                    string plain = info is null ? string.Empty : info.Split('+')[0];
                    return marks is null ? "TsukihimeLauncher.dll carries no managed metadata"
                        : marks.IsPersonalBuild || plain.Contains("-personal", StringComparison.OrdinalIgnoreCase) ? $"TsukihimeLauncher.dll is a personal build ({info})"
                        : !string.Equals(plain, version, StringComparison.Ordinal) ? $"TsukihimeLauncher.dll says version {info ?? "(none)"}, the zip's name says {version}: set <Version>{version}</Version> in Tsuki.App.csproj and Tsuki.Launcher.csproj and publish again"
                        : null;
                });
                if (dllProblem is not null)
                {
                    problems.Add($"{name}: {dllProblem}");
                }
            }
            else
            {
                problems.Add($"{name}: no TsukihimeLauncher.dll at the top of the zip");
            }
        }
        catch (InvalidDataException e)
        {
            problems.Add($"{name}: not a readable zip ({e.Message})");
        }

        return problems;
    }

    /// <summary>The permission bits a program or shared object of the Linux archive carries (0755).</summary>
    public const UnixFileMode ExecutableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>
    /// The reasons <paramref name="tarPath"/>, a Linux release archive (package K46), must not be released (empty: it may). <paramref name="crescentIco"/> is the port's
    /// crescent .ico, which Tsukihime.dll must carry (the icon a Linux build compiles in).
    /// </summary>
    public static IReadOnlyList<string> CheckReleaseTar(string tarPath, byte[] crescentIco)
    {
        ArgumentNullException.ThrowIfNull(crescentIco);
        string name = Path.GetFileName(tarPath);
        var problems = new List<string>();
        Match m = ReleaseTarName().Match(name);
        if (!m.Success)
        {
            problems.Add($"{name}: a Linux release archive must be named Tsukihime-vX.Y.Z-linux-x64.tar.gz");
            return problems;
        }

        string version = $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}";
        string root = name[..^".tar.gz".Length] + "/";
        string[] keep = [ReleaseRules.LinuxHost, "Tsukihime.dll", .. ReleaseRules.LinuxLibraries];
        var files = new Dictionary<string, (UnixFileMode Mode, byte[]? Data)>(StringComparer.Ordinal);
        try
        {
            using FileStream stream = File.OpenRead(tarPath);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            while (reader.GetNextEntry() is { } e)
            {
                if (e.EntryType is TarEntryType.GlobalExtendedAttributes)
                {
                    continue;
                }

                string n = e.Name;
                if (n.StartsWith('/') || n.Contains('\\', StringComparison.Ordinal) || n.Contains(':', StringComparison.Ordinal) || n.Split('/').Any(part => part is "." or ".."))
                {
                    problems.Add($"{name}: the entry {n} would land outside the folder");
                    continue;
                }

                if (!(n + "/").StartsWith(root, StringComparison.Ordinal))
                {
                    problems.Add($"{name}: the entry {n} is not inside the one folder {root} (archive the publish folder under that name: tools/setup/package_linux.py)");
                    continue;
                }

                string rel = n.Length > root.Length ? n[root.Length..].TrimEnd('/') : string.Empty;
                if (e.EntryType is TarEntryType.Directory)
                {
                    continue;
                }

                if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    problems.Add($"{name}: {n} is a {e.EntryType} entry; the archive holds only folders and plain files");
                    continue;
                }

                if (ReleaseRules.Forbidden(rel) is { } forbidden)
                {
                    problems.Add($"{name}: {forbidden}");
                }

                if (string.Equals(Path.GetFileName(rel), ReleaseCheck.PersonalNote, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{name}: holds {ReleaseCheck.PersonalNote}: a personal build is never released");
                }

                byte[]? data = null;
                if (keep.Contains(rel, StringComparer.Ordinal) && e.DataStream is { } body)
                {
                    using var copy = new MemoryStream();
                    body.CopyTo(copy);
                    data = copy.ToArray();
                }

                files[rel] = (e.Mode, data);
            }
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or EndOfStreamException)
        {
            problems.Add($"{name}: not a readable .tar.gz ({e.Message})");
            return problems;
        }

        foreach (string program in new[] { ReleaseRules.LinuxHost }.Concat(ReleaseRules.LinuxLibraries))
        {
            if (!files.TryGetValue(program, out var f) || f.Data is null)
            {
                problems.Add($"{name}: no {program} in {root}");
                continue;
            }

            if (f.Mode != ExecutableMode)
            {
                problems.Add($"{name}: {program} has the mode {Convert.ToString((int)f.Mode, 8)}, not 755 (build the archive with tools/setup/package_linux.py)");
            }

            ElfFacts? elf = ElfFacts.Read(f.Data);
            bool library = program != ReleaseRules.LinuxHost;
            if (elf is not { IsX64: true } || (library ? elf.Type != 3 : elf.Type is not (2 or 3)))
            {
                problems.Add($"{name}: {program} is not an x86-64 ELF {(library ? "shared object" : "program")}");
            }
            else if (ReleaseRules.LinuxFfmpeg.Contains(program) && ReleaseRules.FfmpegLicenceProblem(program, f.Data) is { } licence)
            {
                problems.Add($"{name}: {licence}");
            }
        }

        string[] runtime = [.. ReleaseRules.LinuxRuntime.Where(f => !files.ContainsKey(f))];
        if (runtime.Length > 0)
        {
            problems.Add($"{name}: no {string.Join(", ", runtime)} in {root}: not a self-contained publish");
        }

        foreach (string licence in ReleaseRules.LinuxLicences)
        {
            if (!files.ContainsKey(licence))
            {
                problems.Add($"{name}: no {licence} in {root}");
            }
        }

        if (files.TryGetValue("Tsukihime.dll", out var dll) && dll.Data is not null)
        {
            string temp = Path.Combine(Path.GetTempPath(), "tsuki-release-sums-" + Guid.NewGuid().ToString("N") + ".dll");
            try
            {
                File.WriteAllBytes(temp, dll.Data);
                AssemblyMarks? marks = PeIcons.Marks(temp);
                string? info = marks?.InformationalVersion;
                string plain = info is null ? string.Empty : info.Split('+')[0];
                string? dllProblem = marks is null ? "Tsukihime.dll carries no managed metadata"
                    : marks.IsPersonalBuild || plain.Contains("-personal", StringComparison.OrdinalIgnoreCase) ? $"Tsukihime.dll is a personal build ({info})"
                    : !string.Equals(plain, version, StringComparison.Ordinal) ? $"Tsukihime.dll says version {info ?? "(none)"}, the archive's name says {version}: set <Version>{version}</Version> in Tsuki.App.csproj and publish again"
                    : ReleaseCheck.IconProblem(temp, FaceIcon.DecodeIco(crescentIco)) is { } icon ? "Tsukihime.dll " + icon
                    : null;
                if (dllProblem is not null)
                {
                    problems.Add($"{name}: {dllProblem}");
                }
            }
            finally
            {
                File.Delete(temp);
            }
        }
        else
        {
            problems.Add($"{name}: no Tsukihime.dll in {root}");
        }

        return problems;
    }

    /// <summary>Unpacks <paramref name="entry"/> to a temporary file with <paramref name="extension"/>, runs <paramref name="check"/> on it and deletes it again.</summary>
    private static string? WithExtracted(ZipArchiveEntry entry, string extension, Func<string, string?> check)
    {
        string temp = Path.Combine(Path.GetTempPath(), "tsuki-release-sums-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            entry.ExtractToFile(temp);
            return check(temp);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>"" when the programs sit at the top of the zip, "folder/" when every entry is inside one folder that holds them (the launcher accepts both).</summary>
    private static string TopFolder(List<ZipArchiveEntry> entries)
    {
        if (entries.Any(e => string.Equals(e.FullName, "TsukihimeLauncher.exe", StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        var firsts = entries.Select(e => e.FullName.Replace('\\', '/')).Select(n => n.Contains('/', StringComparison.Ordinal) ? n[..(n.IndexOf('/', StringComparison.Ordinal) + 1)] : string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return firsts is [{ Length: > 0 } only] ? only : string.Empty;
    }

    [GeneratedRegex(@"^Tsukihime-v(\d{1,5})\.(\d{1,5})\.(\d{1,5})-win-x64\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseZipName();

    [GeneratedRegex(@"^Tsukihime-v(\d{1,5})\.(\d{1,5})\.(\d{1,5})-linux-x64\.tar\.gz$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseTarName();
}

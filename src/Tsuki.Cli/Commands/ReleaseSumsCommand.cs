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
/// Package K47: macOS ARM64 and x64 ZIPs receive the same app-folder, native-library, licence, safety and version checks before joining that one checksum file.
/// </summary>
public sealed partial class ReleaseSumsCommand : ICliCommand
{
    /// <summary>The checksum file's name (the launcher looks for exactly this asset).</summary>
    public const string SumsFileName = "SHA256SUMS.txt";

    /// <inheritdoc/>
    public string Name => "release-sums";

    /// <inheritdoc/>
    public string Summary => "write SHA256SUMS.txt for Windows, Linux and macOS release assets; the Windows updater reads only its own zip; release-sums FILE... [--out FILE] [--icon FILE]";

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
            error.WriteLine("release-sums: name the release files (Windows zip, Linux .tar.gz and macOS ARM64/x64 zips)");
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
        byte[]? crescentIcns = crescentIco is null ? null : ReleaseCheckCommand.DefaultMacIcns(icon);
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

            if (IsReleaseMacZipName(name))
            {
                problems.AddRange(CheckReleaseMacZip(file, crescentIco!, crescentIcns));
                if (ReleaseMacZipName().Match(name) is { Success: true } mm)
                {
                    versions.Add(mm.Groups[1].Value + "." + mm.Groups[2].Value + "." + mm.Groups[3].Value);
                }
            }
            else if (IsReleaseZipName(name))
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
    public const UnixFileMode ExecutableMode = ReleaseRules.Mode755;

    /// <summary>
    /// The reasons <paramref name="tarPath"/>, a Linux release archive (package K46), must not be released (empty: it may). It is unpacked into a temporary folder
    /// the way the Linux updater unpacks it (<see cref="ReleaseRules.UnpackLinuxTar"/>: one folder of the archive's name, folders and plain files with
    /// package_linux.py's modes, nothing forbidden, the size limits) and that folder gets release-check's Linux checks; both assemblies must carry this version, no
    /// personal mark and the crescent of <paramref name="crescentIco"/>. <paramref name="limits"/> are the tests' smaller limits.
    /// </summary>
    public static IReadOnlyList<string> CheckReleaseTar(string tarPath, byte[] crescentIco, ReleaseLimits? limits = null)
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
        string temporary = Path.Combine(Path.GetTempPath(), "tsuki-linux-release-" + Guid.NewGuid().ToString("N"));
        try
        {
            var found = new List<string>();
            string root = ReleaseRules.UnpackLinuxTar(tarPath, temporary, found, limits);
            if (found.Count == 0)
            {
                ReleaseRules.CheckLinuxFolder(root, found, []);
                IReadOnlyList<IconFrame> crescent = FaceIcon.DecodeIco(crescentIco);
                foreach (string program in ReleaseRules.UnixPrograms)
                {
                    ReleaseRules.CheckReleaseAssembly(root, program, version, crescent, found);
                }
            }

            problems.AddRange(found.Select(problem => $"{name}: {problem}"));
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            problems.Add($"{name}: not a readable .tar.gz ({e.Message})");
        }
        finally
        {
            DeleteQuietly(temporary);
        }

        return problems;
    }

    /// <summary>Deletes a temporary folder; what cannot go stays in the temp folder.</summary>
    private static void DeleteQuietly(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
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

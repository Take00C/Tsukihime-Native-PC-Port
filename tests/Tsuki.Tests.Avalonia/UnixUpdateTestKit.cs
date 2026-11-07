using System.IO.Compression;
using Tsuki.Launcher.Avalonia;

namespace Tsuki.Tests.Avalonia;

/// <summary>
/// What the macOS/Linux update tests share: temp roots without symbolic links (macOS's temp folder is under /var, a link to /private/var), and stand-ins for the
/// Mac's ditto, codesign and renameatx_np, so that no test runs the real Mac programs on a synthetic app (only a signed app passes codesign).
/// </summary>
internal static class UnixUpdateTestKit
{
    /// <summary>A new folder name under the temp folder, every link on the way resolved (realpath): the paths a process reports for itself are of this form.</summary>
    public static string TempRoot(string prefix)
    {
        string root = Path.Combine(Canonical(Path.GetTempPath()), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary><paramref name="path"/> with every symbolic link among its existing folders resolved (Windows: the full path).</summary>
    public static string Canonical(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows())
        {
            return full;
        }

        string current = "/";
        foreach (string part in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, part);
            FileSystemInfo? target = Directory.Exists(next) ? Directory.ResolveLinkTarget(next, returnFinalTarget: true) : null;
            current = target is null ? next : Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
        }

        return current;
    }

    /// <summary>A stand-in for ditto -x -k and a codesign --verify that passes: the app's own entries unpacked by .NET with their modes, the metadata skipped.</summary>
    public static MacTools MacTools() => new((zip, folder) =>
    {
        Ditto(zip, folder);
        return null;
    }, _ => null);

    /// <summary>A stand-in for ditto -x -k: the app's own entries, unpacked by .NET with their modes (ditto would also merge the metadata into attributes).</summary>
    public static void Ditto(string zipPath, string folder)
    {
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in zip.Entries.Where(e => e.FullName.StartsWith("Tsukihime.app/", StringComparison.Ordinal)))
        {
            string target = Path.Combine(folder, entry.FullName.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
            Chmod(target, (UnixFileMode)((entry.ExternalAttributes >> 16) & 0x1FF));
        }
    }

    /// <summary>A stand-in for renameatx_np's RENAME_SWAP: three moves (not one step, but the same outcome).</summary>
    public static bool Exchange(string source, string target)
    {
        string temporary = source + ".exchange";
        Directory.Move(source, temporary);
        Directory.Move(target, source);
        Directory.Move(temporary, target);
        return true;
    }

    public static void Chmod(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, mode); }
    }
}

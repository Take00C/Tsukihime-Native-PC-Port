namespace Tsuki.Tests.Avalonia;

/// <summary>
/// Temporary folders for the launcher tests, named as the system names them: with every symbolic link resolved. On macOS the temporary folder lies under /var,
/// which is a link to /private/var, and a running program is reported under /private/var; a test that compares the two must start from the real path.
/// </summary>
internal static class TestFolders
{
    /// <summary>A new folder name under the real temporary folder (not created).</summary>
    public static string Temp(string prefix) => Path.Combine(Real(Path.GetTempPath()), prefix + Guid.NewGuid().ToString("N"));

    /// <summary><paramref name="path"/> with each link in it replaced by its target (the parts that do not exist are kept as they are).</summary>
    public static string Real(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)!;
        string real = root;
        foreach (string part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(real, part);
            real = Directory.Exists(next) && Directory.ResolveLinkTarget(next, returnFinalTarget: true) is { } target ? Path.TrimEndingDirectorySeparator(target.FullName) : next;
        }

        return real;
    }
}

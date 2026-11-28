namespace Tsuki.Data;

/// <summary>Game-folder paths are remembered as written, but read relative to the program, never the process's working directory.</summary>
public static class GameFolderPath
{
    /// <summary>Resolves a typed or remembered path against <see cref="AppContext.BaseDirectory"/>.</summary>
    public static string Resolve(string? path) => Resolve(path, AppContext.BaseDirectory);

    /// <summary>Resolves a typed or remembered path against <paramref name="programDirectory"/>.</summary>
    public static string Resolve(string? path, string programDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(programDirectory);
        string text = path.Trim().Trim('"');
        if (text.Length == 0)
        {
            return string.Empty;
        }

        bool driveRelative = text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':' && (text.Length == 2 || text[2] is not ('\\' or '/'));
        return Path.IsPathRooted(text) && !driveRelative ? text : Path.GetFullPath(text, Path.GetFullPath(programDirectory));
    }

    /// <summary>A folder chosen in a picker is remembered relative to the program when both are on the same drive.</summary>
    public static string ForPicker(string path) => ForPicker(path, AppContext.BaseDirectory);

    /// <summary>A folder chosen in a picker is remembered relative to <paramref name="programDirectory"/> when both are on the same drive.</summary>
    public static string ForPicker(string path, string programDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(programDirectory);
        string folder = Path.GetFullPath(path);
        string program = Path.GetFullPath(programDirectory);
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(program), comparison) ? Path.GetRelativePath(program, folder) : folder;
    }
}

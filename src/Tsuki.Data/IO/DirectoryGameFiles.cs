namespace Tsuki.Data.IO;

/// <summary>
/// <see cref="IGameFiles"/> over a directory on disk. The folder is only ever opened for reading
/// (ARCHITECTURE 1.2). Path lookups ignore case on every file system (RR 3.1).
/// </summary>
public sealed class DirectoryGameFiles : IGameFiles
{
    private readonly string _root;

    /// <summary>Creates a view of <paramref name="root"/>; the directory itself need not contain game files.</summary>
    public DirectoryGameFiles(string root)
    {
        _root = Path.GetFullPath(root);
    }

    /// <summary>The root directory.</summary>
    public string Root => _root;

    /// <inheritdoc />
    public string Description => _root;

    /// <inheritdoc />
    /// <remarks>Full path of a relative path, resolving case differences; null if the file does not exist.</remarks>
    public string? ResolveFile(string relativePath)
    {
        string? p = Resolve(relativePath);
        return p is not null && File.Exists(p) ? p : null;
    }

    /// <inheritdoc />
    public bool Exists(string relativePath) => ResolveFile(relativePath) is not null;

    /// <inheritdoc />
    public long Length(string relativePath) => new FileInfo(Require(relativePath)).Length;

    /// <inheritdoc />
    public int ReadAt(string relativePath, long offset, Span<byte> buffer)
    {
        string path = Require(relativePath);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        int total = 0;
        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(handle, buffer[total..], offset + total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListFiles(string relativeDirectory)
    {
        string? dir = ResolveDirectory(relativeDirectory);
        if (dir is null)
        {
            return [];
        }

        var names = new List<string>();
        foreach (string f in Directory.EnumerateFiles(dir))
        {
            names.Add(Path.GetFileName(f));
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    private string Require(string relativePath) =>
        ResolveFile(relativePath) ?? throw new FileNotFoundException($"{relativePath} not found in {_root}", relativePath);

    private string? Resolve(string relativePath)
    {
        string direct = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(direct) || Directory.Exists(direct))
        {
            return direct;
        }

        // Case-insensitive walk for case-sensitive file systems.
        string current = _root;
        string[] parts = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            if (!Directory.Exists(current))
            {
                return null;
            }

            string? match = null;
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (string.Equals(Path.GetFileName(entry), part, StringComparison.OrdinalIgnoreCase))
                {
                    match = entry;
                    break;
                }
            }

            if (match is null)
            {
                return null;
            }

            current = match;
        }

        return current;
    }

    private string? ResolveDirectory(string relativeDirectory)
    {
        string? p = relativeDirectory.Length == 0 ? _root : Resolve(relativeDirectory);
        return p is not null && Directory.Exists(p) ? p : null;
    }
}

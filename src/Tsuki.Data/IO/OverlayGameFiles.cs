namespace Tsuki.Data.IO;

/// <summary>
/// A view over another <see cref="IGameFiles"/> with some files replaced by in-memory bytes or hidden. It
/// never writes anything. The build-check tests use it to present "the real folder with one byte of
/// eboot.bin flipped" or "the real folder without bgm.hed" (ROADMAP M0 acceptance 2).
/// </summary>
public sealed class OverlayGameFiles : IGameFiles
{
    private readonly IGameFiles _inner;
    private readonly Dictionary<string, byte[]> _replaced = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an overlay with no changes.</summary>
    public OverlayGameFiles(IGameFiles inner)
    {
        _inner = inner;
    }

    /// <inheritdoc />
    public string Description => _inner.Description + " (modified view)";

    /// <summary>Makes <paramref name="relativePath"/> read as <paramref name="bytes"/> (the array is not copied).</summary>
    public OverlayGameFiles Replace(string relativePath, byte[] bytes)
    {
        string key = Normalize(relativePath);
        _hidden.Remove(key);
        _replaced[key] = bytes;
        return this;
    }

    /// <summary>Copies <paramref name="relativePath"/> into memory with the byte at <paramref name="position"/> XOR <paramref name="mask"/>.</summary>
    public OverlayGameFiles FlipByte(string relativePath, long position, byte mask = 0x01)
    {
        byte[] data = _inner.ReadAll(relativePath);
        data[position] ^= mask;
        return Replace(relativePath, data);
    }

    /// <summary>Makes <paramref name="relativePath"/> look missing.</summary>
    public OverlayGameFiles Hide(string relativePath)
    {
        string key = Normalize(relativePath);
        _replaced.Remove(key);
        _hidden.Add(key);
        return this;
    }

    /// <inheritdoc />
    public bool Exists(string relativePath)
    {
        string key = Normalize(relativePath);
        return _replaced.ContainsKey(key) || (!_hidden.Contains(key) && _inner.Exists(relativePath));
    }

    /// <inheritdoc />
    public long Length(string relativePath)
    {
        string key = Normalize(relativePath);
        if (_replaced.TryGetValue(key, out byte[]? data))
        {
            return data.Length;
        }

        return _hidden.Contains(key) ? throw Missing(relativePath) : _inner.Length(relativePath);
    }

    /// <inheritdoc />
    public int ReadAt(string relativePath, long offset, Span<byte> buffer)
    {
        string key = Normalize(relativePath);
        if (_replaced.TryGetValue(key, out byte[]? data))
        {
            if (offset >= data.Length)
            {
                return 0;
            }

            int n = (int)Math.Min(buffer.Length, data.Length - offset);
            data.AsSpan((int)offset, n).CopyTo(buffer);
            return n;
        }

        return _hidden.Contains(key) ? throw Missing(relativePath) : _inner.ReadAt(relativePath, offset, buffer);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListFiles(string relativeDirectory)
    {
        string dir = Normalize(relativeDirectory);
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string n in _inner.ListFiles(relativeDirectory))
        {
            if (!_hidden.Contains(Join(dir, n)))
            {
                names.Add(n);
            }
        }

        foreach (string key in _replaced.Keys)
        {
            int slash = key.LastIndexOf('/');
            string parent = slash < 0 ? string.Empty : key[..slash];
            if (string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(key[(slash + 1)..]);
            }
        }

        return [.. names];
    }

    /// <inheritdoc />
    /// <remarks>A replaced file lives in memory and has no path; a hidden one none either.</remarks>
    public string? ResolveFile(string relativePath)
    {
        string key = Normalize(relativePath);
        return _replaced.ContainsKey(key) || _hidden.Contains(key) ? null : _inner.ResolveFile(relativePath);
    }

    private static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;

    private static FileNotFoundException Missing(string path) => new($"{path} is not part of this view", path);

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}

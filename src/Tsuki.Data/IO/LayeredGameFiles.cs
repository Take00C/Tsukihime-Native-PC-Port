namespace Tsuki.Data.IO;

/// <summary>
/// One read-only view over several folders (package K25): the update folder over the base game, as the PS4 mounts a patch over its app. A file is served by the
/// first layer that has it; a directory listing is the union. The layers are on disk, so <see cref="ResolveFile"/> gives the path of the winning file for the
/// memory-mapped archives. Nothing is ever written.
/// </summary>
public sealed class LayeredGameFiles : IGameFiles
{
    private readonly IGameFiles[] _layers;

    /// <summary>Creates the view; <paramref name="layers"/> are tried in order (the update first, the base last).</summary>
    public LayeredGameFiles(params IGameFiles[] layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Length == 0)
        {
            throw new ArgumentException("at least one layer", nameof(layers));
        }

        _layers = layers;
    }

    /// <summary>The layers, the update first.</summary>
    public IReadOnlyList<IGameFiles> Layers => _layers;

    /// <inheritdoc />
    public string Description => string.Join(" + ", _layers.Reverse().Select(l => l.Description));

    /// <inheritdoc />
    public bool Exists(string relativePath) => _layers.Any(l => l.Exists(relativePath));

    /// <inheritdoc />
    public long Length(string relativePath) => Owner(relativePath).Length(relativePath);

    /// <inheritdoc />
    public int ReadAt(string relativePath, long offset, Span<byte> buffer) => Owner(relativePath).ReadAt(relativePath, offset, buffer);

    /// <inheritdoc />
    public IReadOnlyList<string> ListFiles(string relativeDirectory)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IGameFiles layer in _layers)
        {
            names.UnionWith(layer.ListFiles(relativeDirectory));
        }

        return [.. names];
    }

    /// <inheritdoc />
    public string? ResolveFile(string relativePath)
    {
        foreach (IGameFiles layer in _layers)
        {
            if (layer.Exists(relativePath))
            {
                return layer.ResolveFile(relativePath);
            }
        }

        return null;
    }

    /// <summary>The layer that serves <paramref name="relativePath"/>, or null when none has it.</summary>
    public IGameFiles? LayerOf(string relativePath) => _layers.FirstOrDefault(l => l.Exists(relativePath));

    private IGameFiles Owner(string relativePath) =>
        LayerOf(relativePath) ?? throw new FileNotFoundException($"{relativePath} is in none of {Description}", relativePath);
}

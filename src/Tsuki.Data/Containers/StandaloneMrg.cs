namespace Tsuki.Data.Containers;

/// <summary>
/// A standalone mrgd00 file: <c>allscr.mrg</c>, <c>script_text.mrg</c>, <c>ft_*.mrg</c>, <c>se_nam.mrg</c>
/// (docs/formats/containers.md 8), memory-mapped.
/// </summary>
public sealed class StandaloneMrg : IDisposable
{
    private readonly MappedFile _file;

    private StandaloneMrg(MappedFile file, MzpContainer container)
    {
        _file = file;
        Container = container;
    }

    /// <summary>The container over the whole file.</summary>
    public MzpContainer Container { get; }

    /// <summary>File length in bytes.</summary>
    public long Length => _file.Length;

    /// <summary>Opens and parses a standalone file.</summary>
    /// <exception cref="ContainerFormatException">The file is not an mrgd00 container.</exception>
    public static StandaloneMrg Open(string path)
    {
        var file = MappedFile.Open(path);
        try
        {
            if (file.Length > int.MaxValue)
            {
                throw new ContainerFormatException($"{path} is {file.Length} bytes; standalone files are below 2 GiB");
            }

            return new StandaloneMrg(file, MzpContainer.Parse(file.Memory(0, (int)file.Length)));
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _file.Dispose();
}

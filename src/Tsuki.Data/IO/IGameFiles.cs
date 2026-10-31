namespace Tsuki.Data.IO;

/// <summary>
/// A read-only view of the files of a CUSA47999 folder, addressed by relative path with <c>/</c> as the
/// separator (<c>eboot.bin</c>, <c>image/allpac0.hed</c>). The build check (RR 3.3) works on this view, so
/// tests can show it a folder with one byte flipped or one archive missing without touching the disk
/// (ROADMAP M0 acceptance 2). Lookups ignore case (RR 3.1).
/// </summary>
public interface IGameFiles
{
    /// <summary>A name for messages: the folder path, or a description of the view.</summary>
    string Description { get; }

    /// <summary>True if the file exists.</summary>
    bool Exists(string relativePath);

    /// <summary>File length in bytes.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    long Length(string relativePath);

    /// <summary>
    /// Positional read of up to <c>buffer.Length</c> bytes at <paramref name="offset"/>; returns the number of
    /// bytes read (fewer only at the end of the file).
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    int ReadAt(string relativePath, long offset, Span<byte> buffer);

    /// <summary>Names (not paths) of the files directly inside a directory, sorted; empty if it does not exist.</summary>
    IReadOnlyList<string> ListFiles(string relativeDirectory);

    /// <summary>
    /// The path on disk of <paramref name="relativePath"/> for a view backed by real files (the memory-mapped archives need one); null when the file does not
    /// exist or the view is not on disk (package K25: the layered view of a base game and its update answers with the winning layer's file).
    /// </summary>
    string? ResolveFile(string relativePath) => null;
}

/// <summary>Helpers over <see cref="IGameFiles"/>.</summary>
public static class GameFilesExtensions
{
    /// <summary>Reads a whole (small) file. Never call it for an archive .mrg.</summary>
    public static byte[] ReadAll(this IGameFiles files, string relativePath)
    {
        long length = files.Length(relativePath);
        if (length > Array.MaxLength)
        {
            throw new IOException($"{relativePath} is {length} bytes; refusing to read it whole");
        }

        byte[] data = new byte[length];
        int read = files.ReadAt(relativePath, 0, data);
        if (read != data.Length)
        {
            throw new IOException($"short read of {relativePath}: {read} of {data.Length} bytes");
        }

        return data;
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes at <paramref name="offset"/>.</summary>
    public static byte[] ReadRange(this IGameFiles files, string relativePath, long offset, int count)
    {
        byte[] data = new byte[count];
        int read = files.ReadAt(relativePath, offset, data);
        if (read != count)
        {
            throw new IOException($"short read of {relativePath} at {offset}: {read} of {count} bytes");
        }

        return data;
    }
}

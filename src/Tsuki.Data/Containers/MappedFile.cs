using System.Buffers;
using System.IO.MemoryMappedFiles;

namespace Tsuki.Data.Containers;

/// <summary>
/// A read-only memory-mapped view of one whole file (an archive can be 24 GB, so nothing is ever read
/// into managed memory unless a caller copies a range out). The view maps address space only; the
/// operating system pages in what is touched (docs/formats/containers.md 2.4, ARCHITECTURE 4.2).
/// </summary>
/// <remarks>
/// Spans and memories handed out by this class point into the mapping. They stay valid until
/// <see cref="Dispose"/>; a span obtained earlier must not be used after that. Reads from several threads
/// are safe.
/// </remarks>
public sealed unsafe class MappedFile : IDisposable
{
    private readonly MemoryMappedFile? _file;
    private readonly MemoryMappedViewAccessor? _view;
    private byte* _base;
    private volatile bool _disposed;

    private MappedFile(string path, MemoryMappedFile? file, MemoryMappedViewAccessor? view, byte* basePtr, long length)
    {
        Path = path;
        _file = file;
        _view = view;
        _base = basePtr;
        Length = length;
    }

    /// <summary>The full path that was opened.</summary>
    public string Path { get; }

    /// <summary>File length in bytes.</summary>
    public long Length { get; }

    /// <summary>Opens <paramref name="path"/> read-only and maps it.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static MappedFile Open(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        long length = new FileInfo(full).Length;
        if (length == 0)
        {
            return new MappedFile(full, null, null, null, 0);
        }

        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            // The mapping owns the stream from here on (leaveOpen: false).
            file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* ptr = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            return new MappedFile(full, file, view, ptr + view.PointerOffset, length);
        }
        catch
        {
            view?.Dispose();
            file?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    /// <summary>A span over <c>[offset, offset + length)</c> of the file (no copy).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside the file.</exception>
    /// <exception cref="ObjectDisposedException">The mapping has been disposed.</exception>
    public ReadOnlySpan<byte> Span(long offset, int length)
    {
        CheckRange(offset, length);
        return new ReadOnlySpan<byte>(_base + offset, length);
    }

    /// <summary>A memory over <c>[offset, offset + length)</c> of the file (no copy).</summary>
    public ReadOnlyMemory<byte> Memory(long offset, int length)
    {
        CheckRange(offset, length);
        return length == 0 ? ReadOnlyMemory<byte>.Empty : new Manager(this, offset, length).Memory;
    }

    /// <summary>
    /// Hands the whole mapped range to <paramref name="trim"/> (address, length), in pieces of at most 1 GiB, so that the caller can drop the pages this process touched
    /// from its working set (package K11). The mapping stays valid; a page read afterwards is a soft fault. Tsuki.Data makes no P/Invoke, so the native call is the caller's.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The mapping has been disposed.</exception>
    public void TrimResident(Action<nint, nuint> trim)
    {
        ArgumentNullException.ThrowIfNull(trim);
        ObjectDisposedException.ThrowIf(_disposed, this);
        const long piece = 1L << 30;
        for (long offset = 0; offset < Length && !_disposed; offset += piece)
        {
            trim((nint)(_base + offset), (nuint)Math.Min(piece, Length - offset));
        }
    }

    /// <summary>Copies <c>[offset, offset + length)</c> into a new array.</summary>
    public byte[] Read(long offset, int length) => Span(offset, length).ToArray();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_view is not null)
        {
            if (_base is not null)
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
                _base = null;
            }

            _view.Dispose();
        }

        _file?.Dispose();
    }

    private void CheckRange(long offset, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || length < 0 || offset > Length - length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), $"range [{offset}, {offset + length}) is outside the {Length}-byte file {Path}");
        }
    }

    private sealed class Manager(MappedFile owner, long offset, int length) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(owner._disposed, owner);
            return new Span<byte>(owner._base + offset, length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ObjectDisposedException.ThrowIf(owner._disposed, owner);
            return new MemoryHandle(owner._base + offset + elementIndex);
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}

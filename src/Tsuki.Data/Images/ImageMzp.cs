using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Tsuki.Data.Compression;
using Tsuki.Data.Containers;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Images;

/// <summary>
/// A reusable buffer for the MZX0 output of one tile. A decode worker keeps one and passes it to every
/// <see cref="ImageMzp.DecodeTile"/> call; it grows to the largest tile it has seen and is not shared between threads.
/// </summary>
public sealed class TileScratch
{
    private byte[] _buffer = [];

    /// <summary>A buffer of at least <paramref name="size"/> bytes (contents unspecified).</summary>
    public byte[] Get(int size)
    {
        if (_buffer.Length < size)
        {
            _buffer = GC.AllocateUninitializedArray<byte>(Math.Max(size, 1 << 20));
        }

        return _buffer;
    }
}

/// <summary>
/// An image MZP (docs/formats/images.md): an mrgd00 container whose section 0 is the <see cref="ImageHeader"/> and whose
/// sections 1..N are the MZX0 streams of the tiles, row-major. Decodes tiles to straight-alpha RGBA8 and builds the
/// <see cref="Picture"/> of the data contract. A port of <c>tsuki.image</c> (<c>decode_image</c>, <c>decode_tile</c>,
/// <c>assemble</c>), starting from spike S4's prototype.
/// </summary>
public sealed class ImageMzp
{
    private ImageMzp(MzpContainer container, ImageHeader header)
    {
        Container = container;
        Header = header;
    }

    /// <summary>The container (a window onto the archive mapping; not copied).</summary>
    public MzpContainer Container { get; }

    /// <summary>Section 0, parsed.</summary>
    public ImageHeader Header { get; }

    /// <summary>
    /// The container is an image MZP by the rule of <c>tsuki.sniff.classify_mzp</c>: not every section nested, section 0
    /// starts with a self-consistent header, every other section is an MZX0 stream, and the count is 1 + tiles.
    /// </summary>
    public static bool IsImage(MzpContainer container)
    {
        int n = container.Count;
        if (n == 0)
        {
            return false;
        }

        bool allNested = true;
        for (int i = 0; i < n && allNested; i++)
        {
            allNested = container.IsNested(i);
        }

        if (allNested)
        {
            return false;
        }

        var head = container.Head(0, 16);
        if (head.Length < 16)
        {
            return false;
        }

        int w = BinaryPrimitives.ReadUInt16LittleEndian(head);
        int h = BinaryPrimitives.ReadUInt16LittleEndian(head[2..]);
        int tw = BinaryPrimitives.ReadUInt16LittleEndian(head[4..]);
        int th = BinaryPrimitives.ReadUInt16LittleEndian(head[6..]);
        int tx = BinaryPrimitives.ReadUInt16LittleEndian(head[8..]);
        int ty = BinaryPrimitives.ReadUInt16LittleEndian(head[10..]);
        if (w == 0 || h == 0 || tw == 0 || th == 0 || tx == 0 || ty == 0 || tx != (w + tw - 1) / tw || ty != (h + th - 1) / th)
        {
            return false;
        }

        for (int i = 1; i < n; i++)
        {
            if (!container.Head(i, 4).SequenceEqual("MZX0"u8))
            {
                return false;
            }
        }

        return n == 1 + (tx * ty);
    }

    /// <summary>Parses the header of an image container.</summary>
    /// <exception cref="ImageFormatException">The header breaks a rule of images.md 2, or the section count is not 1 + tiles.</exception>
    public static ImageMzp Open(MzpContainer container)
    {
        if (container.Count == 0)
        {
            throw new ImageFormatException("container has no sections");
        }

        var header = ImageHeader.Parse(container.Section(0).Span);
        if (container.Count != 1 + header.Tiles)
        {
            throw new ImageFormatException($"{container.Count} sections for {header.Tiles} tiles");
        }

        return new ImageMzp(container, header);
    }

    /// <summary>Parses <paramref name="data"/> as an mrgd00 image container.</summary>
    public static ImageMzp Open(ReadOnlyMemory<byte> data) => Open(MzpContainer.Parse(data));

    /// <summary>
    /// Decompresses and converts tile <paramref name="t"/> to its stored RGBA8 tile (<see cref="ImageHeader.TileRgbaBytes"/>
    /// bytes, border included). Flag-0 tiles can be decoded too; the engine never displays them.
    /// </summary>
    /// <param name="t">Tile number, row-major.</param>
    /// <param name="scratch">Per-thread MZX0 output buffer.</param>
    /// <param name="rgba">At least <see cref="ImageHeader.TileRgbaBytes"/> bytes.</param>
    /// <param name="simd">Vector width for RGB565+LO+A.</param>
    /// <exception cref="ImageFormatException">The stream is malformed, or smaller than the format needs.</exception>
    public void DecodeTile(int t, TileScratch scratch, Span<byte> rgba, SimdLevel simd = SimdLevel.Auto)
    {
        ArgumentNullException.ThrowIfNull(scratch);
        if ((uint)t >= (uint)Header.Tiles)
        {
            throw new ArgumentOutOfRangeException(nameof(t));
        }

        var stream = Container.Section(1 + t).Span;
        try
        {
            int declared = Mzx.DeclaredSize(stream);
            if (declared < Header.TileBytes)
            {
                throw new ImageFormatException($"tile {t}: tile data is {declared} bytes, {Header.Format} needs {Header.TileBytes}");
            }

            // One MZX0 command writes at most 128 bytes from one input byte, so a stream cannot declare more.
            if (declared > (long)(stream.Length - Mzx.HeaderSize) * 128)
            {
                throw new ImageFormatException($"tile {t}: MZX0 declared size {declared} cannot come from a {stream.Length}-byte stream");
            }

            byte[] buffer = scratch.Get(Mzx.BufferSize(declared));
            Mzx.Decode(stream, buffer);
            TileConverter.Convert(Header, buffer.AsSpan(0, declared), rgba, simd);
        }
        catch (ImageFormatException e) when (!e.Message.StartsWith("tile ", StringComparison.Ordinal))
        {
            throw new ImageFormatException($"tile {t}: {e.Message}");
        }
        catch (ContainerFormatException e) when (e is not ImageFormatException)
        {
            throw new ImageFormatException($"tile {t}: {e.Message}");
        }
    }

    /// <summary>
    /// The picture of the data contract: every tile in row-major order as straight-alpha RGBA8 (stored tile, border
    /// included), flag-0 tiles empty. Never builds the displayed bitmap (ARCHITECTURE 4.3, S4).
    /// </summary>
    /// <param name="maxDegreeOfParallelism">Tile decode workers; 1 decodes on the calling thread.</param>
    /// <param name="simd">Vector width for RGB565+LO+A.</param>
    public Picture ToPicture(int maxDegreeOfParallelism = 1, SimdLevel simd = SimdLevel.Auto)
    {
        var h = Header;
        var tiles = new PictureTile[h.Tiles];
        var live = new List<int>(h.Tiles);
        for (int t = 0; t < tiles.Length; t++)
        {
            var (sx, sy, w, ht, x, y) = h.TileWindow(t);
            var source = new PixelRect(sx, sy, w, ht);
            if (h.IsLive(t))
            {
                live.Add(t);
            }
            else
            {
                tiles[t] = new PictureTile(t, TileFlags.Empty, h.TileWidth, h.TileHeight, source, x, y, ReadOnlyMemory<byte>.Empty);
            }
        }

        void Decode(int t, TileScratch scratch)
        {
            var (sx, sy, w, ht, x, y) = h.TileWindow(t);
            byte[] rgba = GC.AllocateUninitializedArray<byte>(h.TileRgbaBytes);
            DecodeTile(t, scratch, rgba, simd);
            tiles[t] = new PictureTile(t, (TileFlags)h.TileFlags[t], h.TileWidth, h.TileHeight, new PixelRect(sx, sy, w, ht), x, y, rgba);
        }

        if (maxDegreeOfParallelism <= 1 || live.Count <= 1)
        {
            var scratch = new TileScratch();
            foreach (int t in live)
            {
                Decode(t, scratch);
            }
        }
        else
        {
            Parallel.ForEach(
                live,
                new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
                () => new TileScratch(),
                (t, _, scratch) =>
                {
                    Decode(t, scratch);
                    return scratch;
                },
                _ => { });
        }

        return new Picture(h.DisplayWidth, h.DisplayHeight, ImmutableCollectionsMarshal.AsImmutableArray(tiles));
    }

    /// <summary>
    /// The picture's tile layout without any decoded pixel (package K8): every tile has its flags, stored size and window, and an empty pixel array. For the
    /// callers that need geometry only (<c>SheetGeometry</c>): <see cref="ToPicture"/> decodes every tile, which cost 20..100 ms for a sheet.
    /// </summary>
    public Picture ToLayoutPicture()
    {
        var h = Header;
        var tiles = new PictureTile[h.Tiles];
        for (int t = 0; t < tiles.Length; t++)
        {
            var (sx, sy, w, ht, x, y) = h.TileWindow(t);
            TileFlags flags = h.IsLive(t) ? (TileFlags)h.TileFlags[t] : TileFlags.Empty;
            tiles[t] = new PictureTile(t, flags, h.TileWidth, h.TileHeight, new PixelRect(sx, sy, w, ht), x, y, ReadOnlyMemory<byte>.Empty);
        }

        return new Picture(h.DisplayWidth, h.DisplayHeight, ImmutableCollectionsMarshal.AsImmutableArray(tiles));
    }

    /// <summary>
    /// The displayed picture as straight-alpha RGBA8, <c>DisplayWidth x DisplayHeight</c> row-major, flag-0 tiles transparent: the
    /// value of <c>tsuki.image.decode_image(...).rgba</c>. For tests and tools; the runtime uploads tiles instead.
    /// </summary>
    public byte[] DecodeDisplayed(int maxDegreeOfParallelism = 1, SimdLevel simd = SimdLevel.Auto) =>
        Assemble(ToPicture(maxDegreeOfParallelism, simd));

    /// <summary>Copies the displayed part of every tile of <paramref name="picture"/> into one bitmap (images.md 2.2, 9).</summary>
    public static byte[] Assemble(Picture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        long bytes = 4L * picture.Width * picture.Height;
        if (bytes > Array.MaxLength)
        {
            throw new ImageFormatException($"displayed picture of {picture.Width}x{picture.Height} does not fit one array");
        }

        byte[] output = new byte[bytes];
        int stride = 4 * picture.Width;
        foreach (var tile in picture.Tiles)
        {
            if (tile.Flags == TileFlags.Empty || tile.Source.Width <= 0 || tile.Source.Height <= 0)
            {
                continue;
            }

            var rgba = tile.Rgba.Span;
            int rowBytes = 4 * tile.Source.Width;
            for (int y = 0; y < tile.Source.Height; y++)
            {
                rgba.Slice((4 * (((tile.Source.Y + y) * tile.StoredWidth) + tile.Source.X)), rowBytes)
                    .CopyTo(output.AsSpan(((tile.Y + y) * stride) + (4 * tile.X), rowBytes));
            }
        }

        return output;
    }
}

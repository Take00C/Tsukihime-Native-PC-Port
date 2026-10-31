using System.Buffers.Binary;
using System.Globalization;
using Tsuki.Data.Containers;

namespace Tsuki.Data.Images;

/// <summary>The four pixel formats of the retail image MZPs (docs/formats/images.md 1).</summary>
public enum PixelFormat
{
    /// <summary><c>bmp_type</c> 1, depth code 0: 16 x RGBA palette in the header, 4 bits per pixel (images.md 3.2).</summary>
    Pal4,

    /// <summary><c>bmp_type</c> 1, depth code 1: 256 x RGBA palette in the header in PS2 CSM1 order, 8 bits per pixel (images.md 3.1).</summary>
    Pal8,

    /// <summary><c>bmp_type</c> 12, depth code 1: per-tile "HEP" block with its own palette (images.md 4).</summary>
    Hep8,

    /// <summary><c>bmp_type</c> 11, depth code 4: planar u16 RGB565, u8 low bits, u8 alpha (images.md 5).</summary>
    Rgb565LoA,
}

/// <summary>An image MZP does not follow docs/formats/images.md. The message says which rule failed.</summary>
public sealed class ImageFormatException : ContainerFormatException
{
    /// <summary>Creates the exception.</summary>
    public ImageFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Section 0 of an image MZP: 16 bytes, an optional palette and one flag byte per tile (docs/formats/images.md 2).
/// A port of <c>tsuki.image.parse_header</c> and <c>ImageHeader</c>.
/// </summary>
public sealed class ImageHeader
{
    /// <summary>The longest tile edge or tile size in pixels the parser accepts (256 MiB of RGBA); retail tiles are at most 1024 x 1024.</summary>
    public const int MaxTilePixels = 1 << 26;

    private readonly byte[] _tileFlags;

    private ImageHeader(
        int width, int height, int tileWidth, int tileHeight, int tilesX, int tilesY, int bmpType, int depth, int attr,
        PixelFormat format, uint[]? palette, byte[] tileFlags, int headerSize)
    {
        Width = width;
        Height = height;
        TileWidth = tileWidth;
        TileHeight = tileHeight;
        TilesX = tilesX;
        TilesY = tilesY;
        BmpType = bmpType;
        Depth = depth;
        Attr = attr;
        Format = format;
        Palette = palette;
        _tileFlags = tileFlags;
        HeaderSize = headerSize;
    }

    /// <summary>Stored width (u16 at 0); includes 2 pixels per tile column when <see cref="Border"/> is 1.</summary>
    public int Width { get; }

    /// <summary>Stored height (u16 at 2).</summary>
    public int Height { get; }

    /// <summary>Stored tile width, border included.</summary>
    public int TileWidth { get; }

    /// <summary>Stored tile height, border included.</summary>
    public int TileHeight { get; }

    /// <summary>Tile columns, <c>ceil(Width / TileWidth)</c>.</summary>
    public int TilesX { get; }

    /// <summary>Tile rows, <c>ceil(Height / TileHeight)</c>.</summary>
    public int TilesY { get; }

    /// <summary><c>bmp_type</c> as stored (u16 at 0x0C).</summary>
    public int BmpType { get; }

    /// <summary><c>depth</c> as stored (u8 at 0x0E); the low nibble is the pixel size code.</summary>
    public int Depth { get; }

    /// <summary><c>attr</c> as stored (u8 at 0x0F); bit 0 means every tile carries a 1-pixel border.</summary>
    public int Attr { get; }

    /// <summary>The pixel format.</summary>
    public PixelFormat Format { get; }

    /// <summary>
    /// The header palette as the engine uses it, RGBA packed little-endian into u32 (CSM1 order fixed for 256 colours, alpha
    /// fixed): 16 entries for <see cref="PixelFormat.Pal4"/>, 256 for <see cref="PixelFormat.Pal8"/>, else null.
    /// </summary>
    public uint[]? Palette { get; }

    /// <summary>One flag byte per tile: 0 empty, 1 may be transparent, 2 opaque hint (images.md 2.3).</summary>
    public ReadOnlySpan<byte> TileFlags => _tileFlags;

    /// <summary>Size of section 0 in bytes.</summary>
    public int HeaderSize { get; }

    /// <summary>Number of tiles, <c>TilesX * TilesY</c>.</summary>
    public int Tiles => TilesX * TilesY;

    /// <summary>1 when every tile carries a 1-pixel border copied from its neighbours (attr bit 0), else 0.</summary>
    public int Border => Attr & 1;

    /// <summary>Displayed pixels per tile column.</summary>
    public int InnerWidth => TileWidth - (2 * Border);

    /// <summary>Displayed pixels per tile row.</summary>
    public int InnerHeight => TileHeight - (2 * Border);

    /// <summary>Displayed width (engine VA 0xD8C4F: the border pixels are dropped when attr bit 0 is set and the width is at least 8).</summary>
    public int DisplayWidth => Border != 0 && Width >= 8 ? Width - (2 * TilesX) : Width;

    /// <summary>Displayed height.</summary>
    public int DisplayHeight => Border != 0 && Height >= 8 ? Height - (2 * TilesY) : Height;

    /// <summary>Pixels of one stored tile.</summary>
    public int TilePixels => TileWidth * TileHeight;

    /// <summary>Bytes of one stored tile as RGBA8.</summary>
    public int TileRgbaBytes => 4 * TilePixels;

    /// <summary>The decompressed size of one tile stream (images.md 3.2, 4.1, 5).</summary>
    public int TileBytes => Format switch
    {
        PixelFormat.Pal4 => TilePixels / 2,
        PixelFormat.Pal8 => TilePixels,
        PixelFormat.Hep8 => HepHeaderSize + TilePixels + (256 * 4),
        _ => 4 * TilePixels,
    };

    /// <summary>The tile flag of tile <paramref name="t"/> is not 0.</summary>
    public bool IsLive(int t) => _tileFlags[t] != 0;

    /// <summary>Bytes of the HEP tile header (images.md 4.1).</summary>
    public const int HepHeaderSize = 0x20;

    /// <summary>Top-left of tile <paramref name="t"/>'s displayed area in the picture (images.md 2.2).</summary>
    public (int X, int Y) TileOrigin(int t) => (t % TilesX * InnerWidth, t / TilesX * InnerHeight);

    /// <summary>The part of stored tile <paramref name="t"/> that is displayed: inner rectangle, clipped to the picture; width or height 0 when the tile lies outside.</summary>
    public (int Sx, int Sy, int Width, int Height, int X, int Y) TileWindow(int t)
    {
        var (x0, y0) = TileOrigin(t);
        int w = Math.Max(0, Math.Min(InnerWidth, DisplayWidth - x0));
        int h = Math.Max(0, Math.Min(InnerHeight, DisplayHeight - y0));
        return (Border, Border, w, h, x0, y0);
    }

    /// <summary>Parses section 0 of an image MZP.</summary>
    /// <exception cref="ImageFormatException">The header breaks a rule of images.md 2.</exception>
    public static ImageHeader Parse(ReadOnlySpan<byte> section)
    {
        if (section.Length < 16)
        {
            throw new ImageFormatException($"image header too short ({section.Length} bytes)");
        }

        int w = BinaryPrimitives.ReadUInt16LittleEndian(section);
        int h = BinaryPrimitives.ReadUInt16LittleEndian(section[2..]);
        int tw = BinaryPrimitives.ReadUInt16LittleEndian(section[4..]);
        int th = BinaryPrimitives.ReadUInt16LittleEndian(section[6..]);
        int tx = BinaryPrimitives.ReadUInt16LittleEndian(section[8..]);
        int ty = BinaryPrimitives.ReadUInt16LittleEndian(section[10..]);
        int bt = BinaryPrimitives.ReadUInt16LittleEndian(section[12..]);
        int depth = section[14];
        int attr = section[15];
        if (w == 0 || h == 0 || tw == 0 || th == 0 || tx == 0 || ty == 0)
        {
            throw new ImageFormatException($"zero dimension in image header {Convert.ToHexStringLower(section[..16])}");
        }

        if (tx != (w + tw - 1) / tw || ty != (h + th - 1) / th)
        {
            throw new ImageFormatException($"tile counts {tx}x{ty} inconsistent with {w}x{h} / {tw}x{th}");
        }

        if ((long)tw * th > MaxTilePixels)
        {
            throw new ImageFormatException($"tile of {tw}x{th} pixels is larger than any retail tile");
        }

        // engine VA 0xD8DC7..0xD8DFB: a header palette exists only for bmp_type 1 and 2; depth code 0 gives 16 colours,
        // codes 1 and 7 give 256, any other code none (the flags then follow at 0x10)
        int code = depth & 0x0F;
        int palEntries = (bt & 0xFF) is 1 or 2 ? (code == 0 ? 16 : code is 1 or 7 ? 256 : 0) : 0;
        int need = 16 + (4 * palEntries) + (tx * ty);
        if (section.Length != need)
        {
            throw new ImageFormatException($"header is {section.Length} bytes, expected 16 + {4 * palEntries} + {tx * ty} = {need}");
        }

        var format = ((bt & 0xFF), code) switch
        {
            (1, 0) => PixelFormat.Pal4,
            (1, 1) => PixelFormat.Pal8,
            (12, 1) => PixelFormat.Hep8,
            (11, 4) => PixelFormat.Rgb565LoA,
            _ => throw new ImageFormatException($"unsupported image type: bmp_type={bt:x} depth={depth:x}"),
        };
        uint[]? palette = null;
        if (format is PixelFormat.Pal4 or PixelFormat.Pal8)
        {
            palette = Type1Palette(section.Slice(16, 4 * palEntries), palEntries);
        }

        return new ImageHeader(w, h, tw, th, tx, ty, bt, depth, attr, format, palette, section.Slice(16 + (4 * palEntries), tx * ty).ToArray(), section.Length);
    }

    /// <summary>
    /// Header palette of a <c>bmp_type</c> 1 image as the engine uses it (images.md 3.1): CSM1 unswizzle for 256 colours
    /// (entries 8..15 and 16..23 of every block of 32 exchanged) and alpha <c>a</c> to 0xFF if <c>a == 0x80</c> else <c>(2a) &amp; 0xFF</c>.
    /// </summary>
    internal static uint[] Type1Palette(ReadOnlySpan<byte> raw, int n)
    {
        var pal = new uint[n];
        for (int i = 0; i < n; i++)
        {
            int src = n == 256 && ((((i >> 3) ^ (i >> 4)) & 1) != 0) ? i ^ 0x18 : i;
            uint r = raw[4 * src];
            uint g = raw[(4 * src) + 1];
            uint b = raw[(4 * src) + 2];
            uint a = raw[(4 * src) + 3];
            a = a == 0x80 ? 0xFFu : (2 * a) & 0xFF;
            pal[i] = r | (g << 8) | (b << 16) | (a << 24);
        }

        return pal;
    }

    /// <summary>One line in the style of <c>ImageHeader.describe</c> of the Python reference.</summary>
    public string Describe()
    {
        int f0 = 0, f1 = 0, f2 = 0;
        foreach (byte f in _tileFlags)
        {
            if (f == 0)
            {
                f0++;
            }
            else if (f == 1)
            {
                f1++;
            }
            else if (f == 2)
            {
                f2++;
            }
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{Width}x{Height} (shown {DisplayWidth}x{DisplayHeight}) tiles {TilesX}x{TilesY} of {TileWidth}x{TileHeight} " +
            $"type={BmpType:x} depth={Depth:x2} attr={Attr:x2} format={Format} border={Border} flags={{0:{f0},1:{f1},2:{f2}}}");
    }
}

// Spike S4: image MZP header and tile conversion (docs/formats/images.md 2-5, 9).
// Output: straight RGBA8 (byte order R, G, B, A), exactly what tsuki.image produces.
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace S4.BigPicture;

internal enum PixelFormat { Pal4, Pal8, Hep8, Rgb565LoA }

/// <summary>Section 0 of an image MZP (images.md 2).</summary>
internal sealed class ImageHeader
{
    public int Width, Height, TileW, TileH, TilesX, TilesY, BmpType, Depth, Attr;
    public PixelFormat Format;
    public byte[] TileFlags = [];
    /// <summary>Header palette converted for type-1 images (CSM1 order fixed, alpha fixed), RGBA as u32.</summary>
    public uint[]? Palette;

    public int Tiles => TilesX * TilesY;
    public int Border => Attr & 1;
    public int InnerW => TileW - 2 * Border;
    public int InnerH => TileH - 2 * Border;
    public int OutW => Border != 0 && Width >= 8 ? Width - 2 * TilesX : Width;
    public int OutH => Border != 0 && Height >= 8 ? Height - 2 * TilesY : Height;
    public int TilePixels => TileW * TileH;

    /// <summary>Decompressed size of one tile stream (images.md 3.2, 4.1, 5).</summary>
    public int TileBytes => Format switch
    {
        PixelFormat.Pal4 => TilePixels / 2,
        PixelFormat.Pal8 => TilePixels,
        PixelFormat.Hep8 => 0x20 + TilePixels + 1024,
        _ => 4 * TilePixels,
    };

    public static ImageHeader Parse(ReadOnlySpan<byte> s)
    {
        if (s.Length < 16)
            throw new InvalidDataException("image header too short");
        var h = new ImageHeader
        {
            Width = BinaryPrimitives.ReadUInt16LittleEndian(s),
            Height = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]),
            TileW = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]),
            TileH = BinaryPrimitives.ReadUInt16LittleEndian(s[6..]),
            TilesX = BinaryPrimitives.ReadUInt16LittleEndian(s[8..]),
            TilesY = BinaryPrimitives.ReadUInt16LittleEndian(s[10..]),
            BmpType = BinaryPrimitives.ReadUInt16LittleEndian(s[12..]),
            Depth = s[14],
            Attr = s[15],
        };
        if (h.Width == 0 || h.Height == 0 || h.TileW == 0 || h.TileH == 0
            || h.TilesX != (h.Width + h.TileW - 1) / h.TileW || h.TilesY != (h.Height + h.TileH - 1) / h.TileH)
            throw new InvalidDataException("inconsistent image header");
        int type = h.BmpType & 0xFF, code = h.Depth & 0xF;
        h.Format = (type, code) switch
        {
            (1, 0) => PixelFormat.Pal4,
            (1, 1) => PixelFormat.Pal8,
            (12, 1) => PixelFormat.Hep8,
            (11, 4) => PixelFormat.Rgb565LoA,
            _ => throw new InvalidDataException($"unsupported bmp_type {h.BmpType:x} depth {h.Depth:x}"),
        };
        // palette rule, engine VA 0xD8DC7 (images.md 2)
        int palEntries = 0;
        if (type is 1 or 2)
            palEntries = code == 0 ? 16 : code is 1 or 7 ? 256 : 0;
        int need = 16 + 4 * palEntries + h.Tiles;
        if (s.Length != need)
            throw new InvalidDataException($"header is {s.Length} bytes, expected {need}");
        if (palEntries > 0)
            h.Palette = Type1Palette(s.Slice(16, 4 * palEntries), palEntries);
        h.TileFlags = s.Slice(16 + 4 * palEntries, h.Tiles).ToArray();
        return h;
    }

    /// <summary>images.md 3.1: CSM1 unswizzle for 256 colours; alpha 0x80 -> 0xFF else (2a) &amp; 0xFF.</summary>
    private static uint[] Type1Palette(ReadOnlySpan<byte> raw, int n)
    {
        var pal = new uint[n];
        for (int i = 0; i < n; i++)
        {
            int src = n == 256 && (((i >> 3) ^ (i >> 4)) & 1) != 0 ? i ^ 0x18 : i;
            uint r = raw[4 * src], g = raw[4 * src + 1], b = raw[4 * src + 2], a = raw[4 * src + 3];
            a = a == 0x80 ? 0xFFu : (2 * a) & 0xFF;
            pal[i] = r | g << 8 | b << 16 | a << 24;
        }
        return pal;
    }

    public string Describe() =>
        $"{Width}x{Height} shown {OutW}x{OutH}, {TilesX}x{TilesY} tiles of {TileW}x{TileH}, {Format}, border {Border}, " +
        $"flags 0:{TileFlags.Count(f => f == 0)} 1:{TileFlags.Count(f => f == 1)} 2:{TileFlags.Count(f => f == 2)}";
}

internal static unsafe class TileDecoder
{
    /// <summary>
    /// Decompressed tile bytes -> stored tile RGBA (TileW x TileH, border included), images.md 3-5.
    /// <paramref name="rgba"/> holds TilePixels u32 values.
    /// </summary>
    [SkipLocalsInit]
    public static void Convert(ImageHeader h, byte* raw, uint* rgba)
    {
        int n = h.TilePixels;
        switch (h.Format)
        {
            case PixelFormat.Hep8:
            {
                // images.md 4.1/4.2: 32-byte HEP header, n indices, 256 x RGBA, alpha 2*min(a, 0x7F)
                if (*(uint*)raw != 0x00504548u)
                    throw new InvalidDataException("HEP magic missing");
                uint* pal = stackalloc uint[256];
                byte* pr = raw + 0x20 + n;
                for (int i = 0; i < 256; i++)
                {
                    uint v = Unsafe.ReadUnaligned<uint>(pr + 4 * i);
                    uint a = v >> 24;
                    a = 2 * Math.Min(a, 0x7Fu);
                    pal[i] = (v & 0x00FF_FFFF) | a << 24;
                }
                Expand8(raw + 0x20, pal, rgba, n);
                break;
            }
            case PixelFormat.Pal8:
                fixed (uint* pal = h.Palette)
                    Expand8(raw, pal, rgba, n);
                break;
            case PixelFormat.Pal4:
            {
                // images.md 3.2: one linear stream; texel 2i = low nibble, 2i+1 = high nibble
                fixed (uint* pal = h.Palette)
                {
                    int m = n / 2;
                    for (int i = 0; i < m; i++)
                    {
                        byte b = raw[i];
                        rgba[2 * i] = pal[b & 0xF];
                        rgba[2 * i + 1] = pal[b >> 4];
                    }
                    if ((n & 1) != 0)
                        rgba[n - 1] = pal[0];
                }
                break;
            }
            default:
            {
                // images.md 5: u16 RGB565 plane, u8 low-bit plane, u8 alpha plane
                ushort* w = (ushort*)raw;
                byte* lo = raw + 2 * n;
                byte* al = raw + 3 * n;
                for (int i = 0; i < n; i++)
                {
                    uint v = w[i], l = lo[i];
                    uint r = (v >> 11) << 3 | l >> 5;
                    uint g = ((v >> 5) & 0x3F) << 2 | (l >> 3) & 3;
                    uint b = (v & 0x1F) << 3 | l & 7;
                    rgba[i] = r | g << 8 | b << 16 | (uint)al[i] << 24;
                }
                break;
            }
        }
    }

    private static void Expand8(byte* idx, uint* pal, uint* dst, int n)
    {
        int i = 0;
        for (; i + 8 <= n; i += 8)
        {
            ulong k = Unsafe.ReadUnaligned<ulong>(idx + i);
            dst[i] = pal[(byte)k];
            dst[i + 1] = pal[(byte)(k >> 8)];
            dst[i + 2] = pal[(byte)(k >> 16)];
            dst[i + 3] = pal[(byte)(k >> 24)];
            dst[i + 4] = pal[(byte)(k >> 32)];
            dst[i + 5] = pal[(byte)(k >> 40)];
            dst[i + 6] = pal[(byte)(k >> 48)];
            dst[i + 7] = pal[(byte)(k >> 56)];
        }
        for (; i < n; i++)
            dst[i] = pal[idx[i]];
    }

    /// <summary>Copies the displayed part of stored tile <paramref name="t"/> into the displayed image (images.md 2.2, 9).</summary>
    public static void Place(ImageHeader h, int t, uint* tile, uint* outImg)
    {
        int W = h.OutW, H = h.OutH, b = h.Border;
        int x0 = t % h.TilesX * h.InnerW, y0 = t / h.TilesX * h.InnerH;
        if (x0 >= W || y0 >= H)
            return;
        int cw = Math.Min(h.InnerW, W - x0), ch = Math.Min(h.InnerH, H - y0);
        for (int y = 0; y < ch; y++)
            Unsafe.CopyBlockUnaligned(outImg + (long)(y0 + y) * W + x0, tile + (long)(y + b) * h.TileW + b, (uint)(4 * cw));
    }
}

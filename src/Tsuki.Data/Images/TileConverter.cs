using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Tsuki.Data.Images;

/// <summary>Which instruction set <see cref="TileConverter.Rgb565LoAToRgba(ReadOnlySpan{byte}, Span{byte}, int, SimdLevel)"/> uses (tests compare the three).</summary>
public enum SimdLevel
{
    /// <summary>The widest vectors the machine accelerates.</summary>
    Auto,

    /// <summary>One pixel at a time.</summary>
    Scalar,

    /// <summary>128-bit vectors, 8 pixels per step.</summary>
    Vector128,

    /// <summary>256-bit vectors, 16 pixels per step.</summary>
    Vector256,
}

/// <summary>
/// Decompressed tile bytes to straight-alpha RGBA8 (docs/formats/images.md 3-5): the output of the engine's tile
/// upload (VA 0xD6900, 0xD7040, 0xD5970) in the byte order R, G, B, A, which is also <c>tsuki.image.decode_tile</c>.
/// </summary>
public static unsafe class TileConverter
{
    /// <summary>
    /// Converts one decompressed tile to RGBA8, <c>TileWidth x TileHeight x 4</c> bytes top-down, border included.
    /// </summary>
    /// <param name="header">The image header (format, tile size, palette).</param>
    /// <param name="raw">The decompressed tile stream, at least <see cref="ImageHeader.TileBytes"/> bytes.</param>
    /// <param name="rgba">At least <see cref="ImageHeader.TileRgbaBytes"/> bytes.</param>
    /// <param name="simd">The vector width for RGB565+LO+A; <see cref="SimdLevel.Auto"/> outside tests.</param>
    /// <exception cref="ImageFormatException">The tile is too short, or a HEP block disagrees with its header.</exception>
    public static void Convert(ImageHeader header, ReadOnlySpan<byte> raw, Span<byte> rgba, SimdLevel simd = SimdLevel.Auto)
    {
        int n = header.TilePixels;
        if (raw.Length < header.TileBytes)
        {
            throw new ImageFormatException($"tile data is {raw.Length} bytes, {header.Format} needs {header.TileBytes}");
        }

        if (rgba.Length < 4 * n)
        {
            throw new ArgumentException($"output of {rgba.Length} bytes is smaller than a {header.TileWidth}x{header.TileHeight} RGBA tile", nameof(rgba));
        }

        fixed (byte* src = raw)
        fixed (byte* dstBytes = rgba)
        {
            uint* dst = (uint*)dstBytes;
            switch (header.Format)
            {
                case PixelFormat.Hep8:
                    CheckHep(header, raw);
                    uint* hepPalette = stackalloc uint[256];
                    HepPalette(src + ImageHeader.HepHeaderSize + n, hepPalette);
                    Expand8(src + ImageHeader.HepHeaderSize, hepPalette, dst, n);
                    break;
                case PixelFormat.Pal8:
                    fixed (uint* pal = header.Palette)
                    {
                        Expand8(src, pal, dst, n);
                    }

                    break;
                case PixelFormat.Pal4:
                    fixed (uint* pal = header.Palette)
                    {
                        Expand4(src, pal, dst, n);
                    }

                    break;
                default:
                    Rgb565LoAToRgba(src, dst, n, simd);
                    break;
            }
        }
    }

    /// <summary>
    /// RGB565 + low bits + alpha, three planes one after another (images.md 5): <c>n</c> u16 words, <c>n</c> low-bit bytes
    /// (<c>b:3 g:2 r:3</c>, LSB first), <c>n</c> alpha bytes. R = 5 high + 3 low bits, G = 6 high + 2 low bits, B = 5 high + 3 low bits.
    /// </summary>
    /// <param name="planes">The three planes, <c>4 n</c> bytes.</param>
    /// <param name="rgba"><c>4 n</c> bytes, pixels as R, G, B, A.</param>
    /// <param name="n">Pixel count.</param>
    /// <param name="simd">Vector width.</param>
    public static void Rgb565LoAToRgba(ReadOnlySpan<byte> planes, Span<byte> rgba, int n, SimdLevel simd = SimdLevel.Auto)
    {
        if (n < 0 || planes.Length < 4L * n || rgba.Length < 4L * n)
        {
            throw new ArgumentException("buffers are smaller than 4 bytes per pixel");
        }

        fixed (byte* s = planes)
        fixed (byte* d = rgba)
        {
            Rgb565LoAToRgba(s, (uint*)d, n, simd);
        }
    }

    internal static void Rgb565LoAToRgba(byte* src, uint* dst, int n, SimdLevel simd = SimdLevel.Auto)
    {
        ushort* words = (ushort*)src;
        byte* lo = src + (2L * n);
        byte* alpha = src + (3L * n);
        int i = 0;
        bool v256 = simd is SimdLevel.Auto or SimdLevel.Vector256 && Vector256.IsHardwareAccelerated;
        bool v128 = !v256 && simd is SimdLevel.Auto or SimdLevel.Vector128 or SimdLevel.Vector256 && Vector128.IsHardwareAccelerated;
        if (v256)
        {
            for (; i + 16 <= n; i += 16)
            {
                var w = Vector256.Load(words + i);
                var l = Vector256.WidenLower(Vector128.Load(lo + i).ToVector256Unsafe());
                var a = Vector256.WidenLower(Vector128.Load(alpha + i).ToVector256Unsafe());
                var r = Vector256.ShiftLeft(Vector256.ShiftRightLogical(w, 11), 3) | Vector256.ShiftRightLogical(l, 5);
                var g = Vector256.ShiftLeft(Vector256.ShiftRightLogical(w, 5) & Vector256.Create((ushort)0x3F), 2)
                    | (Vector256.ShiftRightLogical(l, 3) & Vector256.Create((ushort)3));
                var b = Vector256.ShiftLeft(w & Vector256.Create((ushort)0x1F), 3) | (l & Vector256.Create((ushort)7));
                var rg = r | Vector256.ShiftLeft(g, 8);
                var ba = b | Vector256.ShiftLeft(a, 8);
                var p0 = Vector256.WidenLower(rg) | Vector256.ShiftLeft(Vector256.WidenLower(ba), 16);
                var p1 = Vector256.WidenUpper(rg) | Vector256.ShiftLeft(Vector256.WidenUpper(ba), 16);
                p0.Store(dst + i);
                p1.Store(dst + i + 8);
            }
        }
        else if (v128)
        {
            for (; i + 8 <= n; i += 8)
            {
                var w = Vector128.Load(words + i);
                var l = Vector128.WidenLower(Vector64.Load(lo + i).ToVector128Unsafe());
                var a = Vector128.WidenLower(Vector64.Load(alpha + i).ToVector128Unsafe());
                var r = Vector128.ShiftLeft(Vector128.ShiftRightLogical(w, 11), 3) | Vector128.ShiftRightLogical(l, 5);
                var g = Vector128.ShiftLeft(Vector128.ShiftRightLogical(w, 5) & Vector128.Create((ushort)0x3F), 2)
                    | (Vector128.ShiftRightLogical(l, 3) & Vector128.Create((ushort)3));
                var b = Vector128.ShiftLeft(w & Vector128.Create((ushort)0x1F), 3) | (l & Vector128.Create((ushort)7));
                var rg = r | Vector128.ShiftLeft(g, 8);
                var ba = b | Vector128.ShiftLeft(a, 8);
                var p0 = Vector128.WidenLower(rg) | Vector128.ShiftLeft(Vector128.WidenLower(ba), 16);
                var p1 = Vector128.WidenUpper(rg) | Vector128.ShiftLeft(Vector128.WidenUpper(ba), 16);
                p0.Store(dst + i);
                p1.Store(dst + i + 4);
            }
        }

        for (; i < n; i++)
        {
            uint v = words[i];
            uint l = lo[i];
            uint r = ((v >> 11) << 3) | (l >> 5);
            uint g = (((v >> 5) & 0x3F) << 2) | ((l >> 3) & 3);
            uint b = ((v & 0x1F) << 3) | (l & 7);
            dst[i] = r | (g << 8) | (b << 16) | ((uint)alpha[i] << 24);
        }
    }

    /// <summary>The HEP block must agree with the image header (images.md 4.1): magic, total size = the stream size, and the tile size.</summary>
    private static void CheckHep(ImageHeader header, ReadOnlySpan<byte> raw)
    {
        // The Python reference compares total_size with the decompressed length, so a longer stream is rejected too.
        if (!raw[..4].SequenceEqual("HEP\0"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]) != (uint)raw.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(raw[0x14..]) != (uint)header.TileWidth
            || BinaryPrimitives.ReadUInt32LittleEndian(raw[0x18..]) != (uint)header.TileHeight)
        {
            throw new ImageFormatException(
                $"HEP header mismatch: {Convert4(raw[..4])} total {BinaryPrimitives.ReadUInt32LittleEndian(raw[4..])} of {raw.Length}, " +
                $"{BinaryPrimitives.ReadUInt32LittleEndian(raw[0x14..])}x{BinaryPrimitives.ReadUInt32LittleEndian(raw[0x18..])} in a {header.TileWidth}x{header.TileHeight} tile");
        }

        static string Convert4(ReadOnlySpan<byte> b) => System.Convert.ToHexStringLower(b);
    }

    /// <summary>HEP palette (images.md 4.2): 256 RGBA entries in plain order, alpha <c>a</c> to <c>2 * min(a, 0x7F)</c> (engine VA 0xD70CE).</summary>
    private static void HepPalette(byte* raw, uint* palette)
    {
        for (int i = 0; i < 256; i++)
        {
            uint v = Unsafe.ReadUnaligned<uint>(raw + (4 * i));
            uint a = Math.Min(v >> 24, 0x7Fu) * 2;
            palette[i] = (v & 0x00FF_FFFF) | (a << 24);
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
        {
            dst[i] = pal[idx[i]];
        }
    }

    /// <summary>
    /// PAL4 (images.md 3.2, engine VA 0xD7040): the tile is one linear stream of <c>n / 2</c> bytes; byte i gives texel 2i
    /// (low nibble) and texel 2i + 1 (high nibble) whatever the row width. For an odd texel count the last texel is index 0.
    /// </summary>
    private static void Expand4(byte* src, uint* pal, uint* dst, int n)
    {
        int m = n / 2;
        for (int i = 0; i < m; i++)
        {
            byte b = src[i];
            dst[2 * i] = pal[b & 0xF];
            dst[(2 * i) + 1] = pal[b >> 4];
        }

        if ((n & 1) != 0)
        {
            dst[n - 1] = pal[0];
        }
    }
}

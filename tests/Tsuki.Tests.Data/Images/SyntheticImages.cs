using System.Buffers.Binary;
using Tsuki.Data.Images;

namespace Tsuki.Tests.Data.Images;

/// <summary>
/// Builders for image MZPs made in memory (no game data) and an independent, deliberately slow reference of the pixel formats,
/// written from docs/formats/images.md and <c>tsuki.image</c> rather than from the production decoder.
/// </summary>
public static class SyntheticImages
{
    /// <summary>A valid MZX0 stream that stores <paramref name="raw"/> with literal commands only (the encoder of <c>tsuki.mzx.compress_literal</c>).</summary>
    public static byte[] Mzx0Literal(ReadOnlySpan<byte> raw)
    {
        var output = new List<byte>(raw.Length + 16);
        output.AddRange("MZX0"u8.ToArray());
        output.AddRange(BitConverter.GetBytes((uint)raw.Length));
        int words = (raw.Length + 1) / 2;
        for (int w = 0; w < words; w += 64)
        {
            int n = Math.Min(64, words - w);
            output.Add((byte)(((n - 1) << 2) | 3));
            for (int i = 2 * w; i < 2 * (w + n); i++)
            {
                output.Add(i < raw.Length ? raw[i] : (byte)0);
            }
        }

        return [.. output];
    }

    /// <summary>An mrgd00 container with the packing rule of containers.md 4.3 (sections 8-byte aligned, 1..8 bytes of 0xFF between).</summary>
    public static byte[] Container(IReadOnlyList<byte[]> sections)
    {
        int n = sections.Count;
        int dataStart = 8 + (8 * n);
        var offsets = new long[n];
        long pos = dataStart;
        for (int i = 0; i < n; i++)
        {
            offsets[i] = pos;
            pos += sections[i].Length;
            pos += 8 - (pos % 8);
        }

        byte[] buf = new byte[pos];
        Array.Fill(buf, (byte)0xFF);
        "mrgd00"u8.CopyTo(buf);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6), (ushort)n);
        for (int i = 0; i < n; i++)
        {
            long rel = offsets[i] - dataStart;
            int size = sections[i].Length;
            int so = (int)(rel / 0x800);
            int bo = (int)(rel % 0x800);
            int ss = (bo + size + 0x7FF) / 0x800;
            var rec = buf.AsSpan(8 + (8 * i), 8);
            BinaryPrimitives.WriteUInt16LittleEndian(rec, (ushort)so);
            BinaryPrimitives.WriteUInt16LittleEndian(rec[2..], (ushort)bo);
            BinaryPrimitives.WriteUInt16LittleEndian(rec[4..], (ushort)ss);
            BinaryPrimitives.WriteUInt16LittleEndian(rec[6..], (ushort)(size & 0xFFFF));
            sections[i].CopyTo(buf, offsets[i]);
        }

        return buf;
    }

    /// <summary>Section 0 of an image MZP.</summary>
    public static byte[] Section0(int w, int h, int tw, int th, int bmpType, int depth, int attr, byte[]? palette, byte[] flags)
    {
        int tx = (w + tw - 1) / tw;
        int ty = (h + th - 1) / th;
        byte[] b = new byte[16 + (palette?.Length ?? 0) + flags.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)h);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)tw);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)th);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)tx);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), (ushort)ty);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), (ushort)bmpType);
        b[14] = (byte)depth;
        b[15] = (byte)attr;
        palette?.CopyTo(b, 16);
        flags.CopyTo(b, 16 + (palette?.Length ?? 0));
        return b;
    }

    /// <summary>The kinds of image the tests build.</summary>
    public enum Kind { Pal4, Pal8, Hep8, Rgb565 }

    /// <summary>A generated image: the container bytes and the displayed picture the reference expects.</summary>
    public sealed record Made(byte[] Container, byte[] ExpectedDisplayed, int Width, int Height, int TilesLive);

    /// <summary>
    /// Builds a random image of the given kind and shape and computes its displayed RGBA with the reference implementation
    /// (images.md 2.2: tile t shows its inner rectangle at (tx * inner_w, ty * inner_h); flag-0 tiles stay transparent).
    /// </summary>
    public static Made Make(Kind kind, int w, int h, int tw, int th, bool border, int seed, byte[]? flagsOverride = null)
    {
        var rng = new Random(seed);
        int tx = (w + tw - 1) / tw;
        int ty = (h + th - 1) / th;
        int tiles = tx * ty;
        byte[] flags = flagsOverride ?? Enumerable.Range(0, tiles).Select(_ => (byte)rng.Next(0, 3)).ToArray();
        int b = border ? 1 : 0;
        int n = tw * th;
        byte[]? palette = kind switch
        {
            Kind.Pal4 => RandomBytes(rng, 64),
            Kind.Pal8 => RandomBytes(rng, 1024),
            _ => null,
        };
        int bmpType = kind switch { Kind.Hep8 => 12, Kind.Rgb565 => 11, _ => 1 };
        int depth = kind switch { Kind.Pal4 => 0x00, Kind.Pal8 => 0x01, Kind.Hep8 => 0x11, _ => 0x14 };
        int attr = border ? 1 : 0;
        var sections = new List<byte[]> { Section0(w, h, tw, th, bmpType, depth, attr, palette, flags) };
        var tilePixels = new byte[tiles][];
        for (int t = 0; t < tiles; t++)
        {
            byte[] raw = kind switch
            {
                Kind.Pal4 => RandomBytes(rng, n / 2),
                Kind.Pal8 => RandomBytes(rng, n),
                Kind.Hep8 => HepTile(rng, tw, th),
                _ => RandomBytes(rng, 4 * n),
            };
            sections.Add(Mzx0Literal(raw));
            tilePixels[t] = ReferenceTile(kind, raw, palette, tw, th);
        }

        int outW = border && w >= 8 ? w - (2 * tx) : w;
        int outH = border && h >= 8 ? h - (2 * ty) : h;
        int iw = tw - (2 * b);
        int ih = th - (2 * b);
        byte[] expected = new byte[4 * outW * outH];
        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                int ttx = x / iw;
                int tty = y / ih;
                if (ttx >= tx || tty >= ty)
                {
                    continue;
                }

                int t = (tty * tx) + ttx;
                if (flags[t] == 0)
                {
                    continue;
                }

                Array.Copy(tilePixels[t], 4 * ((((y % ih) + b) * tw) + (x % iw) + b), expected, 4 * ((y * outW) + x), 4);
            }
        }

        return new Made(Container(sections), expected, outW, outH, flags.Count(f => f != 0));
    }

    /// <summary>A decompressed HEP tile: 32-byte header, indices, 256 x RGBA.</summary>
    public static byte[] HepTile(Random rng, int tw, int th)
    {
        int n = tw * th;
        byte[] raw = new byte[0x20 + n + 1024];
        "HEP\0"u8.CopyTo(raw);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), (uint)raw.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x10), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x14), (uint)tw);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x18), (uint)th);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x1C), 2);
        rng.NextBytes(raw.AsSpan(0x20));
        return raw;
    }

    public static byte[] RandomBytes(Random rng, int n)
    {
        byte[] b = new byte[n];
        rng.NextBytes(b);
        return b;
    }

    /// <summary>Reference conversion of one decompressed tile to RGBA bytes, from images.md 3-5.</summary>
    public static byte[] ReferenceTile(Kind kind, byte[] raw, byte[]? headerPalette, int tw, int th)
    {
        int n = tw * th;
        byte[] rgba = new byte[4 * n];
        switch (kind)
        {
            case Kind.Rgb565:
                for (int i = 0; i < n; i++)
                {
                    int word = raw[2 * i] | (raw[(2 * i) + 1] << 8);
                    int lo = raw[(2 * n) + i];
                    rgba[4 * i] = (byte)(((word >> 11) << 3) | (lo >> 5));
                    rgba[(4 * i) + 1] = (byte)((((word >> 5) & 0x3F) << 2) | ((lo >> 3) & 3));
                    rgba[(4 * i) + 2] = (byte)(((word & 0x1F) << 3) | (lo & 7));
                    rgba[(4 * i) + 3] = raw[(3 * n) + i];
                }

                return rgba;
            case Kind.Hep8:
            {
                var pal = new byte[256][];
                for (int i = 0; i < 256; i++)
                {
                    int o = 0x20 + n + (4 * i);
                    pal[i] = [raw[o], raw[o + 1], raw[o + 2], (byte)(2 * Math.Min((int)raw[o + 3], 0x7F))];
                }

                for (int i = 0; i < n; i++)
                {
                    pal[raw[0x20 + i]].CopyTo(rgba, 4 * i);
                }

                return rgba;
            }

            default:
            {
                int count = kind == Kind.Pal4 ? 16 : 256;
                var pal = new byte[count][];
                for (int i = 0; i < count; i++)
                {
                    // PS2 CSM1 order: logical entry i is stored at i with entries 8..15 and 16..23 of each block of 32 exchanged
                    int stored = i;
                    if (count == 256)
                    {
                        int blockPos = i & 31;
                        if (blockPos is >= 8 and < 16)
                        {
                            stored = i + 8;
                        }
                        else if (blockPos is >= 16 and < 24)
                        {
                            stored = i - 8;
                        }
                    }

                    byte a = headerPalette![(4 * stored) + 3];
                    pal[i] = [headerPalette[4 * stored], headerPalette[(4 * stored) + 1], headerPalette[(4 * stored) + 2], a == 0x80 ? (byte)0xFF : (byte)((2 * a) & 0xFF)];
                }

                for (int i = 0; i < n; i++)
                {
                    int idx = kind == Kind.Pal8 ? raw[i] : i / 2 < n / 2 ? (i % 2 == 0 ? raw[i / 2] & 0xF : raw[i / 2] >> 4) : 0;
                    pal[idx].CopyTo(rgba, 4 * i);
                }

                return rgba;
            }
        }
    }
}

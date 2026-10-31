// Slot thumbnails in the save file (package K6): format 1 = u16 width, u16 height, then the RGBA8 pixels deflated (zlib). The PS4 image keeps none; our file
// stores them next to the slot (SaveFile: a non-zero format and at most 8 MiB).
using System.Buffers.Binary;
using System.IO.Compression;
using Tsuki.Engine.Save;

namespace Tsuki.App.Services;

/// <summary>Encodes and decodes slot thumbnails.</summary>
public static class ThumbnailCodec
{
    /// <summary>Width of a stored thumbnail.</summary>
    public const int Width = 320;

    /// <summary>Height of a stored thumbnail.</summary>
    public const int Height = 180;

    /// <summary>The format id this code writes.</summary>
    public const ushort Format = 1;

    /// <summary>Packs <paramref name="rgba"/> (<paramref name="width"/> x <paramref name="height"/>, straight alpha) into a slot thumbnail.</summary>
    public static SlotThumbnail Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("pixel buffer too small", nameof(rgba));
        }

        using var ms = new MemoryStream();
        Span<byte> head = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(head[2..], (ushort)height);
        ms.Write(head);
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(rgba[..(width * height * 4)]);
        }

        return new SlotThumbnail(Format, ms.ToArray());
    }

    /// <summary>Unpacks a thumbnail; null when the format is not ours or the data is damaged.</summary>
    public static (int Width, int Height, byte[] Rgba)? Decode(SlotThumbnail? thumbnail)
    {
        if (thumbnail is not { Format: Format } t || t.Data.Length < 8)
        {
            return null;
        }

        try
        {
            int w = BinaryPrimitives.ReadUInt16LittleEndian(t.Data);
            int h = BinaryPrimitives.ReadUInt16LittleEndian(t.Data.AsSpan(2));
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096)
            {
                return null;
            }

            byte[] rgba = new byte[w * h * 4];
            using var z = new ZLibStream(new MemoryStream(t.Data, 4, t.Data.Length - 4), CompressionMode.Decompress);
            z.ReadExactly(rgba);
            return (w, h, rgba);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>Box-filters an RGBA image down to <see cref="Width"/> x <see cref="Height"/> (the source must be a multiple of that size or any larger size; nearest-box).</summary>
    public static byte[] Downscale(ReadOnlySpan<byte> rgba, int width, int height)
    {
        byte[] output = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            int y0 = y * height / Height;
            int y1 = Math.Max(y0 + 1, (y + 1) * height / Height);
            for (int x = 0; x < Width; x++)
            {
                int x0 = x * width / Width;
                int x1 = Math.Max(x0 + 1, (x + 1) * width / Width);
                int r = 0, g = 0, b = 0, n = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * width * 4;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int o = row + (sx * 4);
                        r += rgba[o];
                        g += rgba[o + 1];
                        b += rgba[o + 2];
                        n++;
                    }
                }

                int d = ((y * Width) + x) * 4;
                output[d] = (byte)(r / n);
                output[d + 1] = (byte)(g / n);
                output[d + 2] = (byte)(b / n);
                output[d + 3] = 255;
            }
        }

        return output;
    }
}

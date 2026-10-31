// A minimal PNG writer for the verification mode's screenshots (RGBA8, no filter, zlib).
using System.Buffers.Binary;
using System.IO.Compression;

namespace Tsuki.App.Play;

internal static class PngWriter
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Writes <paramref name="rgba"/> (rows top-down, <paramref name="width"/> x <paramref name="height"/>) as a PNG.</summary>
    public static void Write(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var raw = new MemoryStream(height * ((width * 4) + 1));
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgba.Slice(y * width * 4, width * 4));
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw.GetBuffer().AsSpan(0, (int)raw.Length));
        }

        using FileStream f = File.Create(path);
        f.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(f, "IHDR"u8, ihdr);
        Chunk(f, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        Chunk(f, "IEND"u8, []);
    }

    private static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        s.Write(type);
        s.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        BinaryPrimitives.WriteUInt32BigEndian(len, ~crc);
        s.Write(len);
    }

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            t[n] = c;
        }

        return t;
    }
}

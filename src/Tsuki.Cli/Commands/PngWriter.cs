using System.IO.Compression;

namespace Tsuki.Cli.Commands;

/// <summary>A minimal PNG writer (8-bit RGBA, no interlace) for the screenshots of <c>ui-shot</c>.</summary>
internal static class PngWriter
{
    private static readonly uint[] CrcTable = BuildTable();

    /// <summary>Writes straight-alpha RGBA8 pixels (rows top-down) as a PNG.</summary>
    public static void Write(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("not enough pixels", nameof(rgba));
        }

        using var raw = new MemoryStream(height * ((width * 4) + 1));
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgba.Slice(y * width * 4, width * 4));
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(raw.GetBuffer(), 0, (int)raw.Length);
            }

            compressed = ms.ToArray();
        }

        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        WriteBe(ihdr, 0, (uint)width);
        WriteBe(ihdr, 4, (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(fs, "IHDR", ihdr);
        Chunk(fs, "IDAT", compressed);
        Chunk(fs, "IEND", []);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        byte[] len = new byte[4];
        WriteBe(len, 0, (uint)data.Length);
        s.Write(len);
        byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in t)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        byte[] c = new byte[4];
        WriteBe(c, 0, crc ^ 0xFFFFFFFFu);
        s.Write(c);
    }

    private static void WriteBe(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            t[n] = c;
        }

        return t;
    }
}

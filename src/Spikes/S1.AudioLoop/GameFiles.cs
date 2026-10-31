using System.Buffers.Binary;

namespace S1.AudioLoop;

// Tiny read-only reader for one bgm entry: image/bgm.hed (HED8, no terminator) + image/bgm.mrg
// (docs/formats/containers.md 2.1-2.4). The game folder is opened read-only and never written.
internal static class GameFiles
{
    // Same rule as tools/tsuki/__init__.py: the environment variable only, no built-in default path.
    public const string MissingMessage =
        "game folder not found: set the environment variable TSUKI_GAME_DIR to your extracted CUSA47999 folder (the one holding eboot.bin and image\\)";

    public static string? GameDir()
    {
        string? dir = Environment.GetEnvironmentVariable("TSUKI_GAME_DIR");
        return !string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(dir, "image")) ? dir : null;
    }

    // HED8 record i: sector = sector_lo | (sector_hi & 0xF000) << 4 (bank-less accessor VA 0xE7040;
    // bgm.mrg is < 2 GiB, so the bank rule of containers.md 2.2 adds nothing), exact size from
    // containers.md 2.3.
    public static (long Offset, int Size) Hed8Entry(ReadOnlySpan<byte> hed, int index)
    {
        if ((index + 1) * 8 > hed.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"HED8 has {hed.Length / 8} records");
        }

        ReadOnlySpan<byte> r = hed.Slice(index * 8, 8);
        ushort sectorLo = BinaryPrimitives.ReadUInt16LittleEndian(r);
        ushort sectorHi = BinaryPrimitives.ReadUInt16LittleEndian(r[2..]);
        ushort sizeSectors = BinaryPrimitives.ReadUInt16LittleEndian(r[4..]);
        ushort sizeLo = BinaryPrimitives.ReadUInt16LittleEndian(r[6..]);
        if ((sectorHi & 0x0FFF) != 0)
        {
            throw new InvalidDataException("HED8 byte offset inside the sector is not 0");
        }

        long sector = sectorLo | ((long)(sectorHi & 0xF000) << 4);
        long cap = (long)sizeSectors * 0x800;
        long size = (cap & ~0xFFFFL) | sizeLo;
        if (size > cap)
        {
            size -= 0x10000;
        }

        return (sector * 0x800, checked((int)size));
    }

    public static (byte[] Bytes, long Offset) ReadBgmEntry(string gameDir, int index)
    {
        string img = Path.Combine(gameDir, "image");
        byte[] hed = File.ReadAllBytes(Path.Combine(img, "bgm.hed"));
        (long offset, int size) = Hed8Entry(hed, index);
        byte[] buf = new byte[size];
        using var fs = new FileStream(Path.Combine(img, "bgm.mrg"), FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Position = offset;
        fs.ReadExactly(buf);
        return (buf, offset);
    }
}

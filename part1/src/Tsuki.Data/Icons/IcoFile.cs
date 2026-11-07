// Package K43: the images of a Windows icon file as they are, byte for byte (a BITMAPINFOHEADER image or a PNG), for the launcher's stamp of the program icons
// (Tsuki.Launcher Core/ExeIconStamp.cs): whether an executable's icon group holds exactly the images of an .ico file (the same images in the same order), and the
// GRPICONDIR that the stamp writes for them. The C# compiler turns an ApplicationIcon into one RT_ICON per image (its bytes as they are in the file) and an
// RT_GROUP_ICON whose entries are the file's directory entries with the RT_ICON id in place of the file offset; this file lays out the same. It never decodes a
// picture: FaceIcon.DecodeIco does that for the BMP images.
using System.Buffers.Binary;

namespace Tsuki.Data.Icons;

/// <summary>One image of an icon file: the fields of its directory entry and its bytes.</summary>
/// <param name="Width">The entry's width byte (0 stands for 256).</param>
/// <param name="Height">The entry's height byte (0 stands for 256).</param>
/// <param name="ColorCount">The entry's palette size (0 for images of more than 256 colours).</param>
/// <param name="Planes">The entry's colour planes.</param>
/// <param name="BitCount">The entry's bits per pixel.</param>
/// <param name="Data">The image: a BITMAPINFOHEADER bitmap with its mask, or a PNG file.</param>
public sealed record IcoImage(byte Width, byte Height, byte ColorCount, ushort Planes, ushort BitCount, byte[] Data)
{
    /// <summary>The side in pixels (256 for the entry's 0).</summary>
    public int Size => Width == 0 ? 256 : Width;
}

/// <summary>Reads the images of an icon file, compares two sets of them and writes the icon group directory of an executable.</summary>
public static class IcoFile
{
    /// <summary>An icon file with more images than this is not taken (the crescent has 10, the Arcueid icon 7).</summary>
    public const int MaxImages = 64;

    /// <summary>An image larger than this is not taken (the 256 px BMP of the Arcueid icon is 266 KiB).</summary>
    public const int MaxImageBytes = 1 << 20;

    /// <summary>The size of one GRPICONDIRENTRY: the 12 bytes of the file's directory entry before the offset, and the 16-bit RT_ICON id.</summary>
    public const int GroupEntryBytes = 14;

    /// <summary>The images of the icon file <paramref name="ico"/> in the order of its directory; null when the bytes are not an icon file or an entry points outside them.</summary>
    public static IReadOnlyList<IcoImage>? Read(ReadOnlySpan<byte> ico)
    {
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(ico[2..]) != 1)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico[4..]);
        if (count is 0 or > MaxImages || ico.Length < 6 + (16 * count))
        {
            return null;
        }

        var images = new List<IcoImage>(count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = ico.Slice(6 + (16 * i), 16);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (length is 0 or > MaxImageBytes || offset < 6 || offset > (uint)ico.Length || length > (uint)ico.Length - offset)
            {
                return null;
            }

            images.Add(new IcoImage(
                entry[0],
                entry[1],
                entry[2],
                BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]),
                ico.Slice((int)offset, (int)length).ToArray()));
        }

        return images;
    }

    /// <summary>The images of the icon file at <paramref name="path"/> (at most <see cref="FaceIconCache.MaxIconBytes"/>); null when it is missing, too large or not an icon file.</summary>
    public static IReadOnlyList<IcoImage>? ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using FileStream stream = File.OpenRead(path);
            if (stream.Length is 0 or > FaceIconCache.MaxIconBytes)
            {
                return null;
            }

            byte[] bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return Read(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> hold the same images in the same order: the same sizes and byte-identical image data (the pixels,
    /// the mask, a PNG's every byte). The directory fields that tools fill in differently (planes, bit count) do not count.
    /// </summary>
    public static bool SameImages(IReadOnlyList<IcoImage>? a, IReadOnlyList<IcoImage>? b)
    {
        if (a is null || b is null || a.Count != b.Count || a.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Width != b[i].Width || a[i].Height != b[i].Height || !a[i].Data.AsSpan().SequenceEqual(b[i].Data))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The RT_GROUP_ICON data for <paramref name="images"/> stored as RT_ICON resources <paramref name="ids"/> (one id per image): the GRPICONDIR header (reserved 0,
    /// type 1, the count) and one GRPICONDIRENTRY per image with the directory entry's fields as the file has them, the image's length and its id.
    /// </summary>
    public static byte[] GroupDirectory(IReadOnlyList<IcoImage> images, IReadOnlyList<int> ids)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(ids);
        if (images.Count is 0 or > MaxImages || ids.Count != images.Count)
        {
            throw new ArgumentException($"{images.Count} images need as many ids (1..{MaxImages}), not {ids.Count}", nameof(ids));
        }

        byte[] group = new byte[6 + (GroupEntryBytes * images.Count)];
        Span<byte> span = group;
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)images.Count);
        for (int i = 0; i < images.Count; i++)
        {
            if (ids[i] is < 1 or > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(ids), ids[i], "an RT_ICON id is 1..65535");
            }

            IcoImage image = images[i];
            Span<byte> entry = span.Slice(6 + (GroupEntryBytes * i), GroupEntryBytes);
            entry[0] = image.Width;
            entry[1] = image.Height;
            entry[2] = image.ColorCount;
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], image.Planes);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], image.BitCount);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)image.Data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[12..], (ushort)ids[i]);
        }

        return group;
    }

    /// <summary>The RT_ICON ids an RT_GROUP_ICON's entries name, in order; null when the bytes are not a group directory.</summary>
    public static IReadOnlyList<int>? GroupImageIds(ReadOnlySpan<byte> group)
    {
        if (group.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(group[2..]) != 1)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(group[4..]);
        if (group.Length < 6 + (GroupEntryBytes * count))
        {
            return null;
        }

        var ids = new int[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = BinaryPrimitives.ReadUInt16LittleEndian(group[(6 + (GroupEntryBytes * i) + 12)..]);
        }

        return ids;
    }

    /// <summary>"16, 24, 32, 48, 64, 128, 256 px": the sides of <paramref name="images"/>, for the log and the reports.</summary>
    public static string Sizes(IReadOnlyList<IcoImage> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        return string.Join(", ", images.Select(i => i.Size.ToString(System.Globalization.CultureInfo.InvariantCulture))) + " px";
    }
}

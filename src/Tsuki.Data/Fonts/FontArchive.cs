using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Tsuki.Data.Compression;
using Tsuki.Data.Containers;
using Tsuki.Data.IO;
using Tsuki.Data.Text;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Fonts;

/// <summary>
/// One decoded glyph page of <c>ft_&lt;lang&gt;.mrg</c> before it becomes pixels: the header fields, the palette in pixel-index order and the
/// 8-bit palette indices (docs/formats/fonts.md 3; <c>tsuki.font.FontPage</c>).
/// </summary>
/// <param name="Section">The section number (0..64); the page is texture slot <see cref="FontArchive.FirstSlot"/> + section.</param>
/// <param name="Width">Page width: 256 for the blank page 0, 1024 for the others.</param>
/// <param name="Height">Page height.</param>
/// <param name="Header">The eight header words: width, height, tile width, tile height, tiles x, tiles y, type a, type b.</param>
/// <param name="Flag">The byte after the palette: 1 for a page with glyphs, 0 for a blank page, -1 when the header ends before it.</param>
/// <param name="Palette">256 RGBA entries in pixel-index order (the PS2 CSM1 swizzle undone); alpha is the PS2 value 0..0x80.</param>
/// <param name="Indices">Width x Height palette indices, row-major, top-down.</param>
public sealed record FontPageData(int Section, int Width, int Height, ushort[] Header, int Flag, byte[] Palette, byte[] Indices)
{
    /// <summary>The texture slot the engine uploads this page to.</summary>
    public int Slot => FontArchive.FirstSlot + Section;

    /// <summary>
    /// The engine's palette alpha conversion (VA 0xD90B0..0xD90CE): 0x80 becomes 0xFF, anything else <c>(2 * a) &amp; 0xFF</c>.
    /// </summary>
    public static byte Alpha8(byte ps2Alpha) => ps2Alpha == 0x80 ? (byte)0xFF : (byte)((ps2Alpha * 2) & 0xFF);

    /// <summary>Straight-alpha RGBA8 of the whole page, top-down: each pixel takes its palette entry with the alpha converted.</summary>
    public byte[] ToRgba()
    {
        uint[] lut = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            byte r = Palette[(4 * i) + 0];
            byte g = Palette[(4 * i) + 1];
            byte b = Palette[(4 * i) + 2];
            byte a = Alpha8(Palette[(4 * i) + 3]);
            lut[i] = BitConverter.IsLittleEndian
                ? r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24)
                : ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a;
        }

        byte[] rgba = new byte[Indices.Length * 4];
        Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(rgba.AsSpan());
        for (int i = 0; i < Indices.Length; i++)
        {
            pixels[i] = lut[Indices[i]];
        }

        return rgba;
    }

    /// <summary>The 8-bit coverage plane (the converted alpha of every pixel).</summary>
    public byte[] ToCoverage()
    {
        byte[] alpha = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            alpha[i] = Alpha8(Palette[(4 * i) + 3]);
        }

        byte[] coverage = new byte[Indices.Length];
        for (int i = 0; i < coverage.Length; i++)
        {
            coverage[i] = alpha[Indices[i]];
        }

        return coverage;
    }
}

/// <summary>
/// <c>image/ft_&lt;lang&gt;.mrg</c>: a standalone mrgd00 of 65 sections, each a nested two-section image container (docs/formats/fonts.md 3):
/// a 1041-byte header with the palette and an MZX0 plane of palette indices. Section 0 is a blank 256 x 256 page, sections 1..64 are
/// 1024 x 1024 pages that the font loader (VA 0xC6980) uploads as texture slots 0x21 + section. A port of <c>tsuki.font.FontPages</c>.
/// </summary>
/// <remarks>The file (1.2 .. 10.8 MB) is not read whole: the section table is read when it is opened, a page when it is asked for.</remarks>
public sealed class FontArchive
{
    /// <summary>Texture slot of section 0 (VA 0xC69F4).</summary>
    public const int FirstSlot = 0x21;

    /// <summary>Sections in the file.</summary>
    public const int PageCount = 65;

    /// <summary>Bytes of palette header up to and including the flag byte.</summary>
    public const int HeaderBytes = 1041;

    private readonly Func<int, byte[]> _read;

    private FontArchive(Language language, Func<int, byte[]> read)
    {
        Language = language;
        _read = read;
    }

    /// <summary>The language of the file.</summary>
    public Language Language { get; }

    /// <summary>The palette-index unswizzle of PS2 CSM1 order: bits 3 and 4 of the index swapped.</summary>
    public static int UnswizzleCsm1(int i) => (i & 0xE7) | ((i & 0x08) << 1) | ((i & 0x10) >> 1);

    /// <summary>Opens <c>image/ft_&lt;lang&gt;.mrg</c> of a game folder view.</summary>
    /// <exception cref="ContainerFormatException">The file does not follow the layout.</exception>
    public static FontArchive Open(IGameFiles files, Language language)
    {
        ArgumentNullException.ThrowIfNull(files);
        string path = $"image/ft_{TextRules.Code(language)}.mrg";
        long length = files.Length(path);
        byte[] top = files.ReadRange(path, 0, (int)Math.Min(length, 8));
        if (!MzpContainer.IsMzp(top))
        {
            throw new ContainerFormatException($"{path}: not an mrgd00 container");
        }

        int tableBytes = 8 + (8 * BinaryPrimitives.ReadUInt16LittleEndian(top.AsSpan(6)));
        MzpSection[] sections = MzpContainer.ReadTable(files.ReadRange(path, 0, (int)Math.Min(length, tableBytes)), length);
        return Create(language, sections, i => files.ReadRange(path, sections[i].Offset, sections[i].Size), path);
    }

    /// <summary>Opens a font container that is already in memory.</summary>
    public static FontArchive FromMemory(Language language, ReadOnlyMemory<byte> file)
    {
        MzpContainer container = MzpContainer.Parse(file);
        return Create(language, [.. container.Sections], i => container.Section(i).ToArray(), $"ft_{TextRules.Code(language)}");
    }

    /// <summary>Decodes page <paramref name="page"/> to palette and indices.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not in 0..64.</exception>
    /// <exception cref="ContainerFormatException">The page does not follow the layout.</exception>
    public FontPageData ReadPageData(int page)
    {
        if ((uint)page >= PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, $"font pages are 0..{PageCount - 1}");
        }

        MzpContainer child = MzpContainer.Parse(_read(page));
        if (child.Count != 2)
        {
            throw new ContainerFormatException($"page {page}: {child.Count} sections, expected 2");
        }

        ReadOnlySpan<byte> hdr = child.Section(0).Span;
        if (hdr.Length < 16 + 1024)
        {
            throw new ContainerFormatException($"font page header too short ({hdr.Length} bytes)");
        }

        ushort[] fields = new ushort[8];
        for (int i = 0; i < 8; i++)
        {
            fields[i] = BinaryPrimitives.ReadUInt16LittleEndian(hdr[(2 * i)..]);
        }

        byte[] palette = new byte[1024];
        for (int i = 0; i < 256; i++)
        {
            hdr.Slice(16 + (4 * UnswizzleCsm1(i)), 4).CopyTo(palette.AsSpan(4 * i));
        }

        int flag = hdr.Length > 1040 ? hdr[1040] : -1;
        (int w, int h) = (fields[0], fields[1]);
        if (fields[2] != w || fields[3] != h || fields[4] != 1 || fields[5] != 1)
        {
            throw new ContainerFormatException($"page {page}: unexpected tiling ({string.Join(", ", fields)})");
        }

        MzxDecoded decoded = Mzx.Decompress(child.Section(1).Span);
        if (decoded.Length != w * h)
        {
            throw new ContainerFormatException($"page {page}: {decoded.Length} pixel bytes for {w}x{h}");
        }

        return new FontPageData(page, w, h, fields, flag, palette, decoded.ToArray());
    }

    /// <summary>Decodes page <paramref name="page"/> to the straight-alpha RGBA the renderer uploads (<c>IGameData.FontPage</c>).</summary>
    public FontPage ReadPage(int page)
    {
        FontPageData data = ReadPageData(page);
        return new FontPage(Language, page, data.Width, data.Height, data.ToRgba());
    }

    private static FontArchive Create(Language language, MzpSection[] sections, Func<int, byte[]> read, string name)
    {
        if (sections.Length != PageCount)
        {
            throw new ContainerFormatException($"{name}: {sections.Length} sections, expected {PageCount}");
        }

        return new FontArchive(language, read);
    }
}

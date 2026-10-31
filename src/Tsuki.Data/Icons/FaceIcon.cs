// The Arcueid icon (package K23): a close-up of her face for the window, the taskbar and the shortcuts, built at run time from the player's own game files and
// cached in the profile folder (icons\tsukihime.ico). Her art is TYPE-MOON's: nothing of it is embedded in a released binary or committed; a folder without the
// picture keeps the port's crescent (package K20). The game, the launcher and Tsuki.Cli all use it (package K28 moved it here from Tsuki.App/Play so that the
// `face-icon` command of Tsuki.Cli, which a PERSONAL build runs before its compile to put the icon into Tsukihime.exe itself, shares the code; src/Tsuki.PersonalIcon.targets).
//
// The picture: the face layer ARU_A_00_00_N00.MZP (Arcueid, the largest sprite class, pose 00, costume 00, her neutral smile; images.md 7) drawn over its body
// ARU_A_00_00_N00_B.MZP as the engine does. The crop is anchored on her eyes, the only saturated red in the face layer: the box of the red pixels gives the eye
// line and the span of both eyes; the square is WideScale x that span and puts the eye line at EyeLine of its height; 16..32 px take a tighter square so the face
// stays readable. Each size is sampled from the full-size crop on its own (never a resized copy of another size), masked to a rounded square and given a thin
// moon-blue rim, the launcher's border colour.
using System.Buffers.Binary;
using System.Text;
using Tsuki.Data.Catalog;
using Tsuki.Data.Images;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Icons;

/// <summary>One size of an icon: straight RGBA, row-major, <see cref="Size"/> squared.</summary>
/// <param name="Size">The side in pixels.</param>
/// <param name="Rgba">The pixels.</param>
public sealed record IconFrame(int Size, byte[] Rgba);

/// <summary>The square of the full-size picture an icon is sampled from: opaque RGBA (transparent pixels were laid over the plate colour).</summary>
/// <param name="Side">The side in pixels.</param>
/// <param name="Rgba">The pixels, row-major.</param>
/// <param name="EyeBox">Where the eyes were found in the face layer (x, y, width, height), for the log and the tests.</param>
/// <param name="Source">The face layer the crop came from.</param>
public sealed record FaceCrop(int Side, byte[] Rgba, PixelRect EyeBox, string Source);

/// <summary>Builds the face icon from the game data and encodes it as a Windows .ico file.</summary>
public static class FaceIcon
{
    /// <summary>The face layer the icon is cut from (the catalog record's name1; its body is the record's name0).</summary>
    public const string FaceLayer = "ARU_A_00_00_N00.MZP";

    /// <summary>The icon sizes, small to large.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [16, 24, 32, 48, 64, 128, 256];

    /// <summary>Sizes up to this one take the tighter crop.</summary>
    public const int TightUpTo = 32;

    /// <summary>The square's side as a multiple of the eyes' span (both eyes, outer corner to outer corner).</summary>
    public const double WideScale = 1.85;

    /// <summary>The tighter square of the small sizes.</summary>
    public const double TightScale = 1.45;

    /// <summary>Where the eye line sits in the square (a share of the side from the top).</summary>
    public const double EyeLine = 0.40;

    /// <summary>The corner radius of the rounded square, a share of the side.</summary>
    public const double CornerRadius = 0.20;

    /// <summary>Bumped when the arithmetic changes, so a cached icon of an older build is rebuilt.</summary>
    public const int Version = 1;

    // the plate behind a transparent pixel and the rim: the launcher's night indigo and its moon-blue border (tools/setup/make_icon.py)
    private const byte PlateR = 0x0E, PlateG = 0x15, PlateB = 0x32;
    private const byte RimR = 0x8F, RimG = 0xA6, RimB = 0xFF;

    /// <summary>The rim's width in pixels at <paramref name="size"/>.</summary>
    public static double RimWidth(int size) => size <= 32 ? 1.0 : size <= 64 ? 1.5 : size <= 128 ? 2.0 : 3.0;

    /// <summary>The side of the square at <paramref name="size"/> given the eyes' span.</summary>
    public static int SideFor(int eyeSpan, int size) => (int)Math.Round(eyeSpan * (size <= TightUpTo ? TightScale : WideScale));

    /// <summary>
    /// The catalog record of <see cref="FaceLayer"/>, or null when the game folder has none (the icon then stays the crescent). The record names the body too.
    /// </summary>
    public static CatalogEntry? FindRecord(GameFolderSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (CatalogEntry e in source.Catalog.Images.Entries)
        {
            if (e.Name1 is { } face && face.Equals(FaceLayer, StringComparison.OrdinalIgnoreCase) && !e.IsVariant)
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>
    /// Decodes the face layer, finds the eyes and cuts the square of <paramref name="scale"/> x their span out of body + face (only the tiles under the square are
    /// decoded; the body is 1778 x 6630 pixels). Null when the record, a file or the eyes are missing.
    /// </summary>
    public static FaceCrop? Crop(GameFolderSource source, double scale = WideScale) => Crops(source, [scale])?[0];

    /// <summary>Both crops (the wide one for 48 px and up, the tight one for 16..32) from one pass over the data; null when the picture is not there.</summary>
    public static (FaceCrop Wide, FaceCrop Tight)? Crops(GameFolderSource source)
    {
        FaceCrop[]? both = Crops(source, [WideScale, TightScale]);
        return both is null ? null : (both[0], both[1]);
    }

    /// <summary>One crop per scale in <paramref name="scales"/>, the face layer decoded and the eyes found once; null when the picture or the eyes are missing.</summary>
    public static FaceCrop[]? Crops(GameFolderSource source, IReadOnlyList<double> scales)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scales);
        CatalogEntry? record = FindRecord(source);
        if (record is null)
        {
            return null;
        }

        ImageId? faceId = source.Catalog.NameIndex.Find(record.Name1!);
        ImageId? bodyId = source.Catalog.NameIndex.Find(record.Name0);
        if (faceId is null || bodyId is null)
        {
            return null;
        }

        ImageMzp face = source.OpenImage((int)faceId.Value.Archive, faceId.Value.Index);
        ImageMzp body = source.OpenImage((int)bodyId.Value.Archive, bodyId.Value.Index);
        var scratch = new TileScratch();
        byte[][] faceTiles = DecodeAll(face, scratch);
        PixelRect? eyes = FindEyes(face.Header, faceTiles);
        if (eyes is not { } box)
        {
            return null;
        }

        var crops = new FaceCrop[scales.Count];
        for (int k = 0; k < scales.Count; k++)
        {
            int side = (int)Math.Round(box.Width * scales[k]);
            int x0 = (int)Math.Round(box.X + (box.Width / 2.0) - (side / 2.0));
            int y0 = (int)Math.Round(box.Y + (box.Height / 2.0) - (EyeLine * side));
            x0 = Math.Clamp(x0, 0, Math.Max(0, body.Header.DisplayWidth - side));
            y0 = Math.Clamp(y0, 0, Math.Max(0, body.Header.DisplayHeight - side));
            byte[] canvas = new byte[side * side * 4];
            for (int i = 0; i < canvas.Length; i += 4)
            {
                canvas[i] = PlateR;
                canvas[i + 1] = PlateG;
                canvas[i + 2] = PlateB;
                canvas[i + 3] = 255;
            }

            BlitUnder(body, null, scratch, canvas, side, x0, y0, record.LayerX, record.LayerY, isBody: true);
            BlitUnder(face, faceTiles, scratch, canvas, side, x0, y0, record.LayerX, record.LayerY, isBody: false);
            crops[k] = new FaceCrop(side, canvas, box, record.Name1!);
        }

        return crops;
    }

    /// <summary>Every size of the icon from the two crops.</summary>
    public static IReadOnlyList<IconFrame> RenderAll(FaceCrop wide, FaceCrop tight)
    {
        ArgumentNullException.ThrowIfNull(wide);
        ArgumentNullException.ThrowIfNull(tight);
        var frames = new List<IconFrame>(Sizes.Count);
        foreach (int size in Sizes)
        {
            frames.Add(new IconFrame(size, Render(size <= TightUpTo ? tight : wide, size)));
        }

        return frames;
    }

    /// <summary>
    /// One size of the icon from a crop: the square sampled down (supersampled bilinear, so every output pixel averages the source under it), masked to a rounded
    /// square with anti-aliased edges, the rim of <see cref="RimWidth"/> pixels in moon-blue inside the edge, transparent corners.
    /// </summary>
    public static byte[] Render(FaceCrop crop, int size)
    {
        ArgumentNullException.ThrowIfNull(crop);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        byte[] output = new byte[size * size * 4];
        int side = crop.Side;
        byte[] src = crop.Rgba;
        double step = side / (double)size;
        int samples = Math.Clamp((int)Math.Ceiling(2.0 * step), 2, 32);
        double radius = CornerRadius * size;
        double rim = RimWidth(size);
        const int MaskSamples = 4;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // the picture: the average of samples x samples bilinear reads spread over the source square under this pixel
                double r = 0, g = 0, b = 0;
                for (int sy = 0; sy < samples; sy++)
                {
                    double fy = ((y + ((sy + 0.5) / samples)) * step) - 0.5;
                    for (int sx = 0; sx < samples; sx++)
                    {
                        double fx = ((x + ((sx + 0.5) / samples)) * step) - 0.5;
                        Bilinear(src, side, fx, fy, out double pr, out double pg, out double pb);
                        r += pr;
                        g += pg;
                        b += pb;
                    }
                }

                double n = samples * samples;
                r /= n;
                g /= n;
                b /= n;

                // the mask: how much of this pixel lies inside the rounded square, and how much of that inside the rim band
                int outer = 0, inner = 0;
                for (int sy = 0; sy < MaskSamples; sy++)
                {
                    double py = y + ((sy + 0.5) / MaskSamples);
                    for (int sx = 0; sx < MaskSamples; sx++)
                    {
                        double px = x + ((sx + 0.5) / MaskSamples);
                        double d = RoundedBoxDistance(px, py, size, radius);
                        if (d <= 0)
                        {
                            outer++;
                            if (d <= -rim)
                            {
                                inner++;
                            }
                        }
                    }
                }

                int o = ((y * size) + x) * 4;
                if (outer == 0)
                {
                    continue;
                }

                double rimShare = (outer - inner) / (double)outer;
                output[o] = (byte)Math.Clamp(Math.Round((r * (1 - rimShare)) + (RimR * rimShare)), 0, 255);
                output[o + 1] = (byte)Math.Clamp(Math.Round((g * (1 - rimShare)) + (RimG * rimShare)), 0, 255);
                output[o + 2] = (byte)Math.Clamp(Math.Round((b * (1 - rimShare)) + (RimB * rimShare)), 0, 255);
                output[o + 3] = (byte)Math.Round(255.0 * outer / (MaskSamples * MaskSamples));
            }
        }

        return output;
    }

    // ---- the .ico file ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The frames as a Windows icon file: every size a 32-bit BMP entry (BITMAPINFOHEADER, BGRA rows bottom-up, an all-zero AND mask), the 256 px one included
    /// (its directory entry says 0 x 0, the Vista form). Explorer, the shell links and <c>LoadImage</c> read it.
    /// </summary>
    public static byte[] EncodeIco(IReadOnlyList<IconFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfZero(frames.Count);
        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)frames.Count);
        ms.Write(header);
        int offset = 6 + (16 * frames.Count);
        var images = new List<byte[]>(frames.Count);
        Span<byte> entry = stackalloc byte[16];
        foreach (IconFrame frame in frames)
        {
            byte[] image = BmpEntry(frame);
            images.Add(image);
            entry.Clear();
            entry[0] = (byte)(frame.Size >= 256 ? 0 : frame.Size);
            entry[1] = entry[0];
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)image.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)offset);
            ms.Write(entry);
            offset += image.Length;
        }

        foreach (byte[] image in images)
        {
            ms.Write(image);
        }

        return ms.ToArray();
    }

    /// <summary>The 32-bit BMP entries of an icon file as RGBA frames (PNG entries and other depths are skipped); empty when the bytes are not an icon file.</summary>
    public static IReadOnlyList<IconFrame> DecodeIco(ReadOnlySpan<byte> ico)
    {
        var frames = new List<IconFrame>();
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(ico[2..]) != 1)
        {
            return frames;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico[4..]);
        for (int i = 0; i < count; i++)
        {
            int at = 6 + (16 * i);
            if (at + 16 > ico.Length)
            {
                break;
            }

            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(ico[(at + 8)..]);
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(ico[(at + 12)..]);
            if (offset < 0 || length < 40 || offset + length > ico.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> image = ico.Slice(offset, length);
            if (BinaryPrimitives.ReadUInt32LittleEndian(image) != 40)
            {
                continue;                                                   // a PNG entry, or a header this reader does not know
            }

            int width = BinaryPrimitives.ReadInt32LittleEndian(image[4..]);
            int height = BinaryPrimitives.ReadInt32LittleEndian(image[8..]) / 2;
            int bpp = BinaryPrimitives.ReadUInt16LittleEndian(image[14..]);
            if (width <= 0 || height <= 0 || width != height || bpp != 32 || 40 + (width * height * 4) > image.Length)
            {
                continue;
            }

            byte[] rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> row = image.Slice(40 + ((height - 1 - y) * width * 4), width * 4);
                for (int x = 0; x < width; x++)
                {
                    int s = x * 4;
                    int d = ((y * width) + x) * 4;
                    rgba[d] = row[s + 2];
                    rgba[d + 1] = row[s + 1];
                    rgba[d + 2] = row[s];
                    rgba[d + 3] = row[s + 3];
                }
            }

            frames.Add(new IconFrame(width, rgba));
        }

        return frames;
    }

    private static byte[] BmpEntry(IconFrame frame)
    {
        int size = frame.Size;
        int maskRow = ((size + 31) / 32) * 4;
        byte[] image = new byte[40 + (size * size * 4) + (maskRow * size)];
        Span<byte> h = image.AsSpan(0, 40);
        BinaryPrimitives.WriteUInt32LittleEndian(h, 40);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], size);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], size * 2);         // the XOR and the AND bitmaps together
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)(size * size * 4));
        for (int y = 0; y < size; y++)
        {
            int srcRow = (size - 1 - y) * size * 4;                         // bottom-up
            int dstRow = 40 + (y * size * 4);
            for (int x = 0; x < size; x++)
            {
                int s = srcRow + (x * 4);
                int d = dstRow + (x * 4);
                image[d] = frame.Rgba[s + 2];
                image[d + 1] = frame.Rgba[s + 1];
                image[d + 2] = frame.Rgba[s];
                image[d + 3] = frame.Rgba[s + 3];
            }
        }

        return image;                                                       // the AND mask stays zero: the alpha channel decides
    }

    // ---- the picture -----------------------------------------------------------------------------------------------------------------------------------------

    private static byte[][] DecodeAll(ImageMzp image, TileScratch scratch)
    {
        ImageHeader h = image.Header;
        var tiles = new byte[h.Tiles][];
        for (int t = 0; t < h.Tiles; t++)
        {
            if (h.IsLive(t))
            {
                byte[] rgba = new byte[h.TileRgbaBytes];
                image.DecodeTile(t, scratch, rgba);
                tiles[t] = rgba;
            }
        }

        return tiles;
    }

    /// <summary>The box of the saturated red pixels of the face layer (her eyes), in displayed coordinates; null when there are too few to be eyes.</summary>
    public static PixelRect? FindEyes(ImageHeader header, byte[][] tiles)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(tiles);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
        for (int t = 0; t < header.Tiles; t++)
        {
            byte[]? tile = tiles[t];
            if (tile is null)
            {
                continue;
            }

            var (sx, sy, w, hgt, x0, y0) = header.TileWindow(t);
            for (int y = 0; y < hgt; y++)
            {
                int row = ((sy + y) * header.TileWidth) + sx;
                for (int x = 0; x < w; x++)
                {
                    int i = (row + x) * 4;
                    if (IsEyeRed(tile[i], tile[i + 1], tile[i + 2], tile[i + 3]))
                    {
                        int px = x0 + x;
                        int py = y0 + y;
                        minX = Math.Min(minX, px);
                        maxX = Math.Max(maxX, px);
                        minY = Math.Min(minY, py);
                        maxY = Math.Max(maxY, py);
                        count++;
                    }
                }
            }
        }

        if (count < 400 || maxX < minX)
        {
            return null;
        }

        var box = new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        // two eyes side by side: wider than tall, and not a stray red elsewhere in the picture
        return box.Width > box.Height && box.Width < header.DisplayWidth / 2 ? box : null;
    }

    /// <summary>Saturated red (her irises): opaque, red well above green and blue.</summary>
    public static bool IsEyeRed(byte r, byte g, byte b, byte a) => a > 16 && r > 150 && g < 90 && b < 90 && r - Math.Max(g, b) > 90;

    // straight-alpha "over" of the tiles of a layer that touch the square onto the opaque canvas
    private static void BlitUnder(ImageMzp image, byte[][]? decoded, TileScratch scratch, byte[] canvas, int side, int cx0, int cy0, int layerX, int layerY, bool isBody)
    {
        ImageHeader h = image.Header;
        int offX = isBody ? 0 : layerX;
        int offY = isBody ? 0 : layerY;
        byte[]? buffer = null;
        for (int t = 0; t < h.Tiles; t++)
        {
            if (!h.IsLive(t))
            {
                continue;
            }

            var (sx, sy, w, hgt, x0, y0) = h.TileWindow(t);
            int left = Math.Max(x0 + offX, cx0);
            int top = Math.Max(y0 + offY, cy0);
            int right = Math.Min(x0 + offX + w, cx0 + side);
            int bottom = Math.Min(y0 + offY + hgt, cy0 + side);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            byte[] tile;
            if (decoded is not null)
            {
                tile = decoded[t];
            }
            else
            {
                buffer ??= new byte[h.TileRgbaBytes];
                image.DecodeTile(t, scratch, buffer);
                tile = buffer;
            }

            for (int y = top; y < bottom; y++)
            {
                int srcRow = ((sy + (y - y0 - offY)) * h.TileWidth) + sx + (left - x0 - offX);
                int dstRow = ((y - cy0) * side) + (left - cx0);
                for (int x = 0; x < right - left; x++)
                {
                    int s = (srcRow + x) * 4;
                    int d = (dstRow + x) * 4;
                    int a = tile[s + 3];
                    if (a == 0)
                    {
                        continue;
                    }

                    canvas[d] = (byte)(((tile[s] * a) + (canvas[d] * (255 - a)) + 127) / 255);
                    canvas[d + 1] = (byte)(((tile[s + 1] * a) + (canvas[d + 1] * (255 - a)) + 127) / 255);
                    canvas[d + 2] = (byte)(((tile[s + 2] * a) + (canvas[d + 2] * (255 - a)) + 127) / 255);
                }
            }
        }
    }

    private static void Bilinear(byte[] src, int side, double fx, double fy, out double r, out double g, out double b)
    {
        fx = Math.Clamp(fx, 0, side - 1);
        fy = Math.Clamp(fy, 0, side - 1);
        int x0 = (int)fx;
        int y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, side - 1);
        int y1 = Math.Min(y0 + 1, side - 1);
        double tx = fx - x0;
        double ty = fy - y0;
        int i00 = ((y0 * side) + x0) * 4, i10 = ((y0 * side) + x1) * 4, i01 = ((y1 * side) + x0) * 4, i11 = ((y1 * side) + x1) * 4;
        double w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty;
        r = (src[i00] * w00) + (src[i10] * w10) + (src[i01] * w01) + (src[i11] * w11);
        g = (src[i00 + 1] * w00) + (src[i10 + 1] * w10) + (src[i01 + 1] * w01) + (src[i11 + 1] * w11);
        b = (src[i00 + 2] * w00) + (src[i10 + 2] * w10) + (src[i01 + 2] * w01) + (src[i11 + 2] * w11);
    }

    /// <summary>Signed distance from (<paramref name="px"/>, <paramref name="py"/>) to the edge of the rounded square of <paramref name="size"/> (negative inside).</summary>
    public static double RoundedBoxDistance(double px, double py, int size, double radius)
    {
        double half = size / 2.0;
        double qx = Math.Abs(px - half) - (half - radius);
        double qy = Math.Abs(py - half) - (half - radius);
        double outside = Math.Sqrt((Math.Max(qx, 0) * Math.Max(qx, 0)) + (Math.Max(qy, 0) * Math.Max(qy, 0)));
        return outside + Math.Min(Math.Max(qx, qy), 0) - radius;
    }
}

/// <summary>
/// The cached icon in the profile folder: <c>icons\tsukihime.ico</c> and, beside it, <c>tsukihime.src</c> saying which game folder and which build of the
/// arithmetic made it. The game and the launcher both call <see cref="Ensure"/>: a current file is used as it is, a missing or stale one is rebuilt from the game
/// data (the game folder changed, or <see cref="FaceIcon.Version"/> did).
/// </summary>
public static class FaceIconCache
{
    /// <summary>The folder under the profile folder.</summary>
    public const string FolderName = "icons";

    /// <summary>The icon file.</summary>
    public const string FileName = "tsukihime.ico";

    /// <summary>The stamp file beside it.</summary>
    public const string StampName = "tsukihime.src";

    /// <summary>The icon file of <paramref name="dataDirectory"/> (whether or not it exists).</summary>
    public static string IconPath(string dataDirectory) => Path.Combine(dataDirectory, FolderName, FileName);

    /// <summary>The stamp file of <paramref name="dataDirectory"/>.</summary>
    public static string StampPath(string dataDirectory) => Path.Combine(dataDirectory, FolderName, StampName);

    /// <summary>What the stamp says for <paramref name="gameFolder"/> with this build (the full path of the folder: it is a file of the player's own profile, never shared).</summary>
    public static string Stamp(string gameFolder) => $"v{FaceIcon.Version}|{FaceIcon.FaceLayer}|{Path.GetFullPath(gameFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}";

    /// <summary>True when the cached icon exists and was made from <paramref name="gameFolder"/> by this build.</summary>
    public static bool IsCurrent(string dataDirectory, string gameFolder)
    {
        try
        {
            string icon = IconPath(dataDirectory);
            string stamp = StampPath(dataDirectory);
            if (!File.Exists(icon) || !File.Exists(stamp) || new FileInfo(icon).Length == 0)
            {
                return false;
            }

            // the stamp is its first line (Tsuki.Data reads files by ranges, never whole: LvAndBuildCheckTests)
            using StreamReader reader = new(stamp, Encoding.UTF8);
            return string.Equals(reader.ReadLine()?.Trim(), Stamp(gameFolder), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The path of a current icon for <paramref name="gameFolder"/>, building it when it is missing or stale. <paramref name="open"/> opens the game folder only
    /// then; the returned source is disposed here. Null when the folder has no usable picture or the file could not be written (<paramref name="log"/> says why).
    /// </summary>
    public static string? Ensure(string dataDirectory, string gameFolder, Func<GameFolderSource> open, Action<string>? log = null, bool disposeSource = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        ArgumentNullException.ThrowIfNull(open);
        string icon = IconPath(dataDirectory);
        if (IsCurrent(dataDirectory, gameFolder))
        {
            return icon;
        }

        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            GameFolderSource source = open();
            (FaceCrop Wide, FaceCrop Tight)? crops;
            try
            {
                crops = FaceIcon.Crops(source);
            }
            finally
            {
                if (disposeSource)
                {
                    source.Dispose();
                }
            }

            if (crops is not { } c)
            {
                log?.Invoke($"face icon: {FaceIcon.FaceLayer} (or its eyes) not found in {gameFolder}; the crescent stays");
                return null;
            }

            IReadOnlyList<IconFrame> frames = FaceIcon.RenderAll(c.Wide, c.Tight);
            Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
            string temp = icon + ".tmp";
            File.WriteAllBytes(temp, FaceIcon.EncodeIco(frames));
            File.Move(temp, icon, overwrite: true);
            File.WriteAllText(StampPath(dataDirectory), Stamp(gameFolder) + Environment.NewLine, new UTF8Encoding(false));
            log?.Invoke($"face icon: built from {c.Wide.Source} (eyes at {c.Wide.EyeBox.X},{c.Wide.EyeBox.Y} {c.Wide.EyeBox.Width} x {c.Wide.EyeBox.Height}; squares {c.Wide.Side} and {c.Tight.Side} px) into {icon} in {watch.ElapsedMilliseconds} ms");
            return icon;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException
                                      or Eboot.EbootFormatException or Containers.ContainerFormatException or InvalidOperationException)
        {
            log?.Invoke($"face icon: not built ({e.GetType().Name}: {e.Message}); the crescent stays");
            return null;
        }
    }

    /// <summary>An icon file larger than this is not read (the seven frames of <see cref="FaceIcon.Sizes"/> are 364 KiB).</summary>
    public const int MaxIconBytes = 4 << 20;

    /// <summary>The frame of <paramref name="size"/> in the cached file, or the largest one, or null when the file is missing, too large or not an icon.</summary>
    public static IconFrame? ReadFrame(string iconPath, int size)
    {
        try
        {
            using FileStream stream = File.OpenRead(iconPath);
            if (stream.Length > MaxIconBytes)
            {
                return null;
            }

            byte[] ico = new byte[stream.Length];
            stream.ReadExactly(ico);
            IReadOnlyList<IconFrame> frames = FaceIcon.DecodeIco(ico);
            return frames.FirstOrDefault(f => f.Size == size) ?? frames.OrderByDescending(f => f.Size).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

// The banner picture (package K17): the game's own title art, decoded at run time from the user's files through Tsuki.Data (the TITLE_PARTS sheet of the title screen,
// docs/formats/system_flow.md 4.5). The pixels live in memory only; they are never written next to the launcher or into the repository.
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tsuki.Data;
using Tsuki.Engine.Contracts;

namespace Tsuki.Launcher;

/// <summary>Decodes picture sheets of the game into WPF bitmaps.</summary>
public static class LauncherArt
{
    /// <summary>TITLE_PARTS child 9: the 2670 x 1266 title background (system_flow.md 4.5, row 1).</summary>
    public const int TitleBackgroundChild = 9;

    /// <summary>TITLE_PARTS child 1: the logo sheet (system_flow.md 4.5, rows 2..4).</summary>
    public const int TitleLogoChild = 1;

    /// <summary>The largest width of a returned bitmap; wider pictures are box-filtered down by a whole factor.</summary>
    public const int MaxWidth = 1600;

    /// <summary>Reads a UI sheet child of the game folder as a frozen bitmap; null when the folder cannot be read.</summary>
    /// <param name="gameFolder">The CUSA47999 folder (opened read-only).</param>
    /// <param name="set">The sheet set.</param>
    /// <param name="language">The sheet's language variant.</param>
    /// <param name="child">The child.</param>
    public static BitmapSource? LoadSheet(string gameFolder, UiSet set, Language language, int child)
    {
        try
        {
            using GameFolderSource source = GameFolderSource.Open(gameFolder);
            return ToBitmap(source.UiSheet(set, language, child));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException or Tsuki.Data.Eboot.EbootFormatException)
        {
            return null;
        }
    }

    /// <summary>The banner pictures: the title screen's background and its logo (straight alpha kept), read through one opened folder. Either can be null.</summary>
    public static (BitmapSource? Background, BitmapSource? Logo) LoadHero(string gameFolder)
    {
        try
        {
            using GameFolderSource source = GameFolderSource.Open(gameFolder);
            return (ToBitmap(source.UiSheet(UiSet.TitleParts, Language.En, TitleBackgroundChild)), CropToContent(ToBitmap(source.UiSheet(UiSet.TitleParts, Language.En, TitleLogoChild))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException or Tsuki.Data.Eboot.EbootFormatException)
        {
            return (null, null);
        }
    }

    /// <summary>Turns a decoded picture (straight-alpha RGBA tiles) into a premultiplied BGRA bitmap, reduced by a whole factor when it is wider than <see cref="MaxWidth"/>.</summary>
    public static BitmapSource ToBitmap(Picture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        int w = picture.Width;
        int h = picture.Height;
        byte[] bgra = new byte[w * h * 4];
        foreach (PictureTile tile in picture.Tiles)
        {
            if (tile.Flags == TileFlags.Empty || tile.Rgba.IsEmpty)
            {
                continue;
            }

            ReadOnlySpan<byte> rgba = tile.Rgba.Span;
            PixelRect src = tile.Source;
            for (int row = 0; row < src.Height; row++)
            {
                int y = tile.Y + row;
                if ((uint)y >= (uint)h)
                {
                    continue;
                }

                for (int col = 0; col < src.Width; col++)
                {
                    int x = tile.X + col;
                    if ((uint)x >= (uint)w)
                    {
                        continue;
                    }

                    int s = (((src.Y + row) * tile.StoredWidth) + src.X + col) * 4;
                    int d = ((y * w) + x) * 4;
                    int a = rgba[s + 3];
                    bgra[d] = (byte)((rgba[s + 2] * a + 127) / 255);
                    bgra[d + 1] = (byte)((rgba[s + 1] * a + 127) / 255);
                    bgra[d + 2] = (byte)((rgba[s] * a + 127) / 255);
                    bgra[d + 3] = (byte)a;
                }
            }
        }

        int factor = Math.Max(1, (int)Math.Ceiling(w / (double)MaxWidth));
        if (factor > 1)
        {
            (bgra, w, h) = BoxReduce(bgra, w, h, factor);
        }

        BitmapSource bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bgra, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Cuts the transparent border off a premultiplied bitmap (the logo sheet has a wide empty margin); a bitmap with no visible pixel comes back unchanged.</summary>
    public static BitmapSource CropToContent(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        int w = bitmap.PixelWidth;
        int h = bitmap.PixelHeight;
        byte[] pixels = new byte[w * h * 4];
        bitmap.CopyPixels(pixels, w * 4, 0);
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (pixels[(((y * w) + x) * 4) + 3] > 12)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        if (maxX < minX)
        {
            return bitmap;
        }

        var cropped = new CroppedBitmap(bitmap, new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
        cropped.Freeze();
        return cropped;
    }

    private static (byte[] Pixels, int Width, int Height) BoxReduce(byte[] src, int w, int h, int factor)
    {
        int nw = w / factor;
        int nh = h / factor;
        byte[] dst = new byte[nw * nh * 4];
        int area = factor * factor;
        for (int y = 0; y < nh; y++)
        {
            for (int x = 0; x < nw; x++)
            {
                int b = 0, g = 0, r = 0, a = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (((y * factor) + dy) * w) + (x * factor);
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int i = (row + dx) * 4;
                        b += src[i];
                        g += src[i + 1];
                        r += src[i + 2];
                        a += src[i + 3];
                    }
                }

                int o = ((y * nw) + x) * 4;
                dst[o] = (byte)(b / area);
                dst[o + 1] = (byte)(g / area);
                dst[o + 2] = (byte)(r / area);
                dst[o + 3] = (byte)(a / area);
            }
        }

        return (dst, nw, nh);
    }

    /// <summary>Writes a bitmap as a PNG (the offscreen render check only; the files stay in scratch folders).</summary>
    public static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }
}

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Tsuki.Data;
using Tsuki.Engine.Contracts;

namespace Tsuki.Launcher.Avalonia;

internal static class UnixLauncherArt
{
    private const int TitleBackgroundChild = 9;
    private const int TitleLogoChild = 1;
    private const int MaxWidth = 1600;

    public static (WriteableBitmap? Background, WriteableBitmap? Logo) LoadHero(string gameFolder)
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

    /// <summary>
    /// Arcueid's face for the window icon: the profile's icons/tsukihime.ico, built from the player's own files when missing or stale (the game builds the same
    /// file), read at 256 px. Null when the folder has no usable picture.
    /// </summary>
    public static WriteableBitmap? LoadFaceIcon(string dataDirectory, string gameFolder, Action<string>? log = null)
    {
        if (Tsuki.Data.Icons.FaceIconCache.Ensure(dataDirectory, gameFolder, () => GameFolderSource.Open(gameFolder), log) is not { } icon
            || Tsuki.Data.Icons.FaceIconCache.ReadFrame(icon, 256) is not { } frame)
        {
            return null;
        }

        byte[] bgra = new byte[frame.Size * frame.Size * 4];
        for (int i = 0; i + 3 < frame.Rgba.Length && i + 3 < bgra.Length; i += 4)
        {
            int a = frame.Rgba[i + 3];
            bgra[i] = (byte)((frame.Rgba[i + 2] * a + 127) / 255);
            bgra[i + 1] = (byte)((frame.Rgba[i + 1] * a + 127) / 255);
            bgra[i + 2] = (byte)((frame.Rgba[i] * a + 127) / 255);
            bgra[i + 3] = (byte)a;
        }

        var bitmap = new WriteableBitmap(new PixelSize(frame.Size, frame.Size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (ILockedFramebuffer target = bitmap.Lock()) { System.Runtime.InteropServices.Marshal.Copy(bgra, 0, target.Address, bgra.Length); }
        return bitmap;
    }

    /// <summary>Writes <paramref name="icon"/> as icons/tsukihime.png in the profile (for the application-menu entry); null without an icon or when it cannot be written.</summary>
    public static string? SaveIconPng(Bitmap? icon, string dataDirectory)
    {
        if (icon is null) { return null; }
        try
        {
            string path = Path.Combine(dataDirectory, Tsuki.Data.Icons.FaceIconCache.FolderName, "tsukihime.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            icon.Save(path);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static WriteableBitmap ToBitmap(Picture picture)
    {
        int w = picture.Width;
        int h = picture.Height;
        byte[] bgra = new byte[w * h * 4];
        foreach (PictureTile tile in picture.Tiles)
        {
            if (tile.Flags == TileFlags.Empty || tile.Rgba.IsEmpty) { continue; }
            ReadOnlySpan<byte> rgba = tile.Rgba.Span;
            Tsuki.Engine.Contracts.PixelRect src = tile.Source;
            for (int row = 0; row < src.Height; row++)
            {
                int y = tile.Y + row;
                if ((uint)y >= (uint)h) { continue; }
                for (int col = 0; col < src.Width; col++)
                {
                    int x = tile.X + col;
                    if ((uint)x >= (uint)w) { continue; }
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
        if (factor > 1) { (bgra, w, h) = BoxReduce(bgra, w, h, factor); }
        var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (ILockedFramebuffer frame = bitmap.Lock()) { System.Runtime.InteropServices.Marshal.Copy(bgra, 0, frame.Address, bgra.Length); }
        return bitmap;
    }

    private static WriteableBitmap CropToContent(WriteableBitmap bitmap)
    {
        int w = bitmap.PixelSize.Width;
        int h = bitmap.PixelSize.Height;
        byte[] pixels = new byte[w * h * 4];
        using (ILockedFramebuffer frame = bitmap.Lock()) { System.Runtime.InteropServices.Marshal.Copy(frame.Address, pixels, 0, pixels.Length); }
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

        if (maxX < minX) { return bitmap; }
        var cropped = new WriteableBitmap(new PixelSize(maxX - minX + 1, maxY - minY + 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (ILockedFramebuffer frame = cropped.Lock())
        {
            for (int y = 0; y < cropped.PixelSize.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(pixels, (((minY + y) * w + minX) * 4), frame.Address + (y * frame.RowBytes), cropped.PixelSize.Width * 4);
            }
        }

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
}

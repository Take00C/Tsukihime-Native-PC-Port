using System.Diagnostics;
using Tsuki.App.Play;
using Tsuki.Data;
using Tsuki.Data.Icons;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Data.App;

/// <summary>
/// Package K23 on the real game: the Arcueid icon is cut from ARU_A_00_00_N00 (her A-size sprite's face layer over its body), anchored on her red eyes, rendered in
/// the seven sizes and cached in a profile folder with a stamp; the pictures go to scratch/k23/icon under the repository root for a look (git-ignored; game art: local
/// only, never committed).
/// </summary>
public sealed class FaceIconRealDataTests : IDisposable
{
    private static readonly string ScratchFolder = Path.Combine(GoldenData.RepoRoot, "scratch", "k23", "icon");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k23-face-" + Guid.NewGuid().ToString("N"));

    public FaceIconRealDataTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [GameDataFact]
    public void TheCropIsAnchoredOnHerEyesAndEverySizeRendersFromTheSpriteItself()
    {
        using GameFolderSource source = GameFolderSource.Open(GameFolder.Require());
        var record = FaceIcon.FindRecord(source);
        Assert.NotNull(record);
        Assert.Equal("ARU_A_00_00_N00_B.MZP", record.Name0, ignoreCase: true);
        Assert.Equal(2, record.CharId);                                                      // Arcueid (archives_ui_movies.md 3.2)
        Assert.Equal(0, record.ScaleClass);                                                  // the A size

        var watch = Stopwatch.StartNew();
        (FaceCrop wide, FaceCrop tight)? crops = FaceIcon.Crops(source);
        long cropMs = watch.ElapsedMilliseconds;
        Assert.NotNull(crops);
        (FaceCrop wide, FaceCrop tight) = crops.Value;
        PixelRect eyes = wide.EyeBox;
        // both irises side by side in the 1270 x 1020 face layer: a few hundred pixels wide, a third as tall, in the middle of the picture
        Assert.InRange(eyes.Width, 200, 500);
        Assert.InRange(eyes.Height, 60, 250);
        Assert.True(eyes.Width > eyes.Height);
        Assert.InRange(eyes.X, 300, 900);
        Assert.InRange(eyes.Y, 300, 800);
        Assert.Equal((int)Math.Round(eyes.Width * FaceIcon.WideScale), wide.Side);
        Assert.Equal((int)Math.Round(eyes.Width * FaceIcon.TightScale), tight.Side);
        Assert.Equal(wide.Side * wide.Side * 4, wide.Rgba.Length);
        Assert.Equal("ARU_A_00_00_N00.MZP", wide.Source, ignoreCase: true);
        // the square is opaque (the plate under any transparent pixel) and the eyes sit where the arithmetic puts them: the red of both irises in the band around 40 % of the height
        // (the two eyes sit at different heights in the three-quarter view, so one row alone may cut between them), and none in the top or bottom tenth
        Assert.All(Enumerable.Range(0, wide.Side * wide.Side).Select(i => wide.Rgba[(i * 4) + 3]), a => Assert.Equal(255, a));
        int RedInRows(double from, double to)
        {
            int count = 0;
            for (int y = (int)(from * wide.Side); y < (int)(to * wide.Side); y++)
            {
                for (int x = 0; x < wide.Side; x++)
                {
                    int i = ((y * wide.Side) + x) * 4;
                    if (FaceIcon.IsEyeRed(wide.Rgba[i], wide.Rgba[i + 1], wide.Rgba[i + 2], 255))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        int redInBand = RedInRows(FaceIcon.EyeLine - 0.12, FaceIcon.EyeLine + 0.12);
        Assert.True(redInBand > 1500, $"{redInBand} red pixels in the eye band");
        Assert.Equal(0, RedInRows(0.0, 0.1));
        Assert.Equal(0, RedInRows(0.9, 1.0));

        watch.Restart();
        IReadOnlyList<IconFrame> frames = FaceIcon.RenderAll(wide, tight);
        long renderMs = watch.ElapsedMilliseconds;
        Assert.Equal(FaceIcon.Sizes, frames.Select(f => f.Size));
        foreach (IconFrame f in frames)
        {
            Assert.Equal(0, f.Rgba[3]);                                                      // transparent corner
            Assert.Equal(255, f.Rgba[((((f.Size / 2) * f.Size) + (f.Size / 2)) * 4) + 3]);   // opaque middle
            int reds = Enumerable.Range(0, f.Size * f.Size).Count(i => FaceIcon.IsEyeRed(f.Rgba[i * 4], f.Rgba[(i * 4) + 1], f.Rgba[(i * 4) + 2], f.Rgba[(i * 4) + 3]));
            Assert.True(reds >= (f.Size >= 32 ? 4 : 1), $"{f.Size} px: {reds} red pixels, the eyes are gone");
        }

        // the pictures, for the eye: the sizes at 1 : 1 on grey, and the small ones enlarged four times with no filtering
        Directory.CreateDirectory(ScratchFolder);
        foreach (IconFrame f in frames)
        {
            PngWriter.Write(Path.Combine(ScratchFolder, $"icon_{f.Size}.png"), f.Size, f.Size, f.Rgba);
        }

        PngWriter.Write(Path.Combine(ScratchFolder, "crop_wide.png"), wide.Side, wide.Side, wide.Rgba);
        WriteContact(Path.Combine(ScratchFolder, "icon_contact.png"), frames);
        WriteEnlarged(Path.Combine(ScratchFolder, "icon_small_x4.png"), frames.Where(f => f.Size <= 48).ToList(), 4);
        File.WriteAllText(Path.Combine(ScratchFolder, "timing.txt"), $"crops {cropMs} ms, render {renderMs} ms; eyes {eyes}; wide {wide.Side}, tight {tight.Side}{Environment.NewLine}");
    }

    [GameDataFact]
    public void TheCacheIsBuiltOnceIntoTheProfileAndReusedUntilTheGameFolderChanges()
    {
        string game = GameFolder.Require();
        string data = Path.Combine(_root, "profile");
        var log = new List<string>();
        int opened = 0;
        GameFolderSource Open()
        {
            opened++;
            return GameFolderSource.Open(game);
        }

        string? path = FaceIconCache.Ensure(data, game, Open, log.Add);
        Assert.Equal(FaceIconCache.IconPath(data), path);
        Assert.Equal(1, opened);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(FaceIconCache.StampPath(data)));
        Assert.True(FaceIconCache.IsCurrent(data, game));
        Assert.Contains(log, l => l.StartsWith("face icon: built from ARU_A_00_00_N00.MZP", StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<IconFrame> frames = FaceIcon.DecodeIco(File.ReadAllBytes(path!));
        Assert.Equal(FaceIcon.Sizes, frames.Select(f => f.Size));
        Assert.Equal(256, FaceIconCache.ReadFrame(path!, 256)?.Size);

        // the second call is a hit: the folder is not opened
        Assert.Equal(path, FaceIconCache.Ensure(data, game, Open, log.Add));
        Assert.Equal(1, opened);

        // another folder spelling of the same place is current too; a different folder is stale and would be rebuilt (here: it cannot be opened, so the old file stays)
        Assert.True(FaceIconCache.IsCurrent(data, game + Path.DirectorySeparatorChar));
        string other = Path.Combine(_root, "not-the-game");
        Assert.Null(FaceIconCache.Ensure(data, other, () => throw new DirectoryNotFoundException(other), log.Add));
        Assert.True(File.Exists(path));
        Assert.Contains(log, l => l.StartsWith("face icon: not built (DirectoryNotFoundException", StringComparison.Ordinal));
    }

    private static void WriteContact(string path, IReadOnlyList<IconFrame> frames)
    {
        const int Gap = 12;
        int width = frames.Sum(f => f.Size + Gap) + Gap;
        int height = frames.Max(f => f.Size) + (2 * Gap);
        byte[] rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = rgba[i + 1] = rgba[i + 2] = 60;
            rgba[i + 3] = 255;
        }

        int x0 = Gap;
        foreach (IconFrame f in frames.OrderByDescending(f => f.Size))
        {
            Blit(rgba, width, f, x0, Gap, 1);
            x0 += f.Size + Gap;
        }

        PngWriter.Write(path, width, height, rgba);
    }

    private static void WriteEnlarged(string path, IReadOnlyList<IconFrame> frames, int factor)
    {
        const int Gap = 8;
        int width = frames.Sum(f => (f.Size * factor) + Gap) + Gap;
        int height = (frames.Max(f => f.Size) * factor) + (2 * Gap);
        byte[] rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = rgba[i + 1] = rgba[i + 2] = 255;
            rgba[i + 3] = 255;
        }

        int x0 = Gap;
        foreach (IconFrame f in frames)
        {
            Blit(rgba, width, f, x0, Gap, factor);
            x0 += (f.Size * factor) + Gap;
        }

        PngWriter.Write(path, width, height, rgba);
    }

    // straight-alpha "over" of a frame, each pixel enlarged by factor
    private static void Blit(byte[] dst, int dstWidth, IconFrame f, int x0, int y0, int factor)
    {
        for (int y = 0; y < f.Size * factor; y++)
        {
            for (int x = 0; x < f.Size * factor; x++)
            {
                int s = (((y / factor) * f.Size) + (x / factor)) * 4;
                int d = (((y0 + y) * dstWidth) + x0 + x) * 4;
                int a = f.Rgba[s + 3];
                for (int c = 0; c < 3; c++)
                {
                    dst[d + c] = (byte)(((f.Rgba[s + c] * a) + (dst[d + c] * (255 - a)) + 127) / 255);
                }
            }
        }
    }
}

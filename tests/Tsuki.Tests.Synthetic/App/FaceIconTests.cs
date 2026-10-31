using Tsuki.App.Play;
using Tsuki.Data.Icons;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package K23: the icon arithmetic without game data: the sizes, the rounded mask and its rim, the eye colour test, the .ico encoding and its reader, and the cache's
/// paths and stamp. The crop of the real sprite is checked by the data tests.
/// </summary>
public sealed class FaceIconTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k23-icon-" + Guid.NewGuid().ToString("N"));

    public FaceIconTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>A hand-made crop: a warm gradient with a saturated red square where the eyes would be.</summary>
    private static FaceCrop Synthetic(int side = 400)
    {
        byte[] rgba = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int i = ((y * side) + x) * 4;
                bool eye = x is >= 120 and < 280 && y is >= 140 and < 180;
                rgba[i] = (byte)(eye ? 220 : 200 + (x * 40 / side));
                rgba[i + 1] = (byte)(eye ? 20 : 170 + (y * 40 / side));
                rgba[i + 2] = (byte)(eye ? 20 : 140);
                rgba[i + 3] = 255;
            }
        }

        return new FaceCrop(side, rgba, new PixelRect(120, 140, 160, 40), "synthetic");
    }

    [Fact]
    public void TheSizesAreTheSevenOfAWindowsIconAndTheSmallOnesTakeTheTighterSquare()
    {
        Assert.Equal([16, 24, 32, 48, 64, 128, 256], FaceIcon.Sizes);
        Assert.Equal((int)Math.Round(319 * FaceIcon.TightScale), FaceIcon.SideFor(319, 16));
        Assert.Equal((int)Math.Round(319 * FaceIcon.TightScale), FaceIcon.SideFor(319, 32));
        Assert.Equal((int)Math.Round(319 * FaceIcon.WideScale), FaceIcon.SideFor(319, 48));
        Assert.True(FaceIcon.TightScale < FaceIcon.WideScale);
        Assert.Equal((1.0, 1.5, 2.0, 3.0), (FaceIcon.RimWidth(16), FaceIcon.RimWidth(64), FaceIcon.RimWidth(128), FaceIcon.RimWidth(256)));
        Assert.True(FaceIcon.IsEyeRed(200, 30, 30, 255));
        Assert.False(FaceIcon.IsEyeRed(200, 30, 30, 10));                                  // transparent
        Assert.False(FaceIcon.IsEyeRed(230, 190, 120, 255));                                // her hair
        Assert.False(FaceIcon.IsEyeRed(250, 220, 200, 255));                                // skin
        Assert.True(FaceIcon.RoundedBoxDistance(32, 32, 64, 12) < 0);                        // the centre is inside
        Assert.True(FaceIcon.RoundedBoxDistance(0.5, 0.5, 64, 12) > 0);                      // the corner pixel is outside the rounding
        Assert.True(FaceIcon.RoundedBoxDistance(32, 0.5, 64, 12) < 0);                       // the middle of the top edge is inside
    }

    [Fact]
    public void ARenderedFrameHasTheRimAtTheEdgeTransparentCornersAndThePictureInside()
    {
        FaceCrop crop = Synthetic();
        foreach (int size in FaceIcon.Sizes)
        {
            byte[] rgba = FaceIcon.Render(crop, size);
            Assert.Equal(size * size * 4, rgba.Length);
            Assert.Equal(0, rgba[3]);                                                        // the corner pixel is fully transparent
            Assert.Equal(0, rgba[(((size - 1) * size) + (size - 1)) * 4 + 3]);
            int centre = (((size / 2) * size) + (size / 2)) * 4;
            Assert.Equal(255, rgba[centre + 3]);
            Assert.InRange(rgba[centre], 190, 255);                                          // the gradient's red channel, not the rim's
            Assert.InRange(rgba[centre + 1], 150, 230);
            // the middle of the top edge is the rim (moon blue: blue well above red), opaque
            int top = ((size / 2) * 4);
            Assert.Equal(255, rgba[top + 3]);
            Assert.True(rgba[top + 2] > rgba[top] + 40, $"{size}: top edge pixel ({rgba[top]}, {rgba[top + 1]}, {rgba[top + 2]}) is not the rim");
            // the eyes' red survives the sampling at every size: some pixel in the eye band is red
            int ey = (int)(160.0 * size / crop.Side);
            bool red = false;
            for (int x = 0; x < size && !red; x++)
            {
                int i = ((ey * size) + x) * 4;
                red = rgba[i] > 150 && rgba[i + 1] < 120 && rgba[i + 2] < 120;
            }

            Assert.True(red, $"{size}: the red band is gone");
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => FaceIcon.Render(crop, 0));
    }

    [Fact]
    public void TheIcoFileHoldsEverySizeAsA32BitEntryAndReadsBack()
    {
        FaceCrop crop = Synthetic();
        IReadOnlyList<IconFrame> frames = FaceIcon.RenderAll(crop, crop);
        Assert.Equal(FaceIcon.Sizes, frames.Select(f => f.Size));
        byte[] ico = FaceIcon.EncodeIco(frames);
        Assert.Equal([0, 0, 1, 0, 7, 0], ico.AsSpan(0, 6).ToArray());                         // reserved, type 1, seven entries
        Assert.Equal(16, ico[6]);                                                            // the first entry's width
        Assert.Equal(0, ico[6 + (16 * 6)]);                                                  // the 256 px entry says 0
        Assert.Equal(32, ico[6 + 6]);                                                        // 32 bits per pixel
        IReadOnlyList<IconFrame> back = FaceIcon.DecodeIco(ico);
        Assert.Equal(frames.Count, back.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            Assert.Equal(frames[i].Size, back[i].Size);
            Assert.Equal(frames[i].Rgba, back[i].Rgba);                                      // BGRA bottom-up in the file, RGBA top-down again
        }

        Assert.Empty(FaceIcon.DecodeIco("not an icon"u8));
        Assert.Empty(FaceIcon.DecodeIco([]));
        // (that Windows itself reads the file is checked where a window is at hand: WindowIdentitySmokeTests.TheWindowTakesThePerSizeFramesOfAnIconFileOnWindows, LoadImage from the file)

        // the directory entries point at consecutive images of the right length
        int offset = 6 + (16 * frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            int at = 6 + (16 * i);
            int length = BitConverter.ToInt32(ico, at + 8);
            Assert.Equal(offset, BitConverter.ToInt32(ico, at + 12));
            Assert.Equal(40 + (frames[i].Size * frames[i].Size * 4) + (((frames[i].Size + 31) / 32) * 4 * frames[i].Size), length);
            offset += length;
        }

        Assert.Equal(offset, ico.Length);
    }

    [Fact]
    public void TheCacheLivesInTheProfilesIconsFolderAndIsStaleWithoutAMatchingStamp()
    {
        string data = Path.Combine(_root, "profile");
        string icon = FaceIconCache.IconPath(data);
        Assert.Equal(Path.Combine(data, "icons", "tsukihime.ico"), icon);
        Assert.Equal(Path.Combine(data, "icons", "tsukihime.src"), FaceIconCache.StampPath(data));
        string game = Path.Combine(_root, "CUSA47999");
        Assert.False(FaceIconCache.IsCurrent(data, game));                                   // nothing there

        Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
        FaceCrop crop = Synthetic();
        File.WriteAllBytes(icon, FaceIcon.EncodeIco(FaceIcon.RenderAll(crop, crop)));
        Assert.False(FaceIconCache.IsCurrent(data, game));                                   // no stamp
        File.WriteAllText(FaceIconCache.StampPath(data), FaceIconCache.Stamp(game));
        Assert.True(FaceIconCache.IsCurrent(data, game));
        Assert.True(FaceIconCache.IsCurrent(data, game + Path.DirectorySeparatorChar));      // a trailing separator is the same folder
        Assert.False(FaceIconCache.IsCurrent(data, Path.Combine(_root, "elsewhere")));       // another game folder: stale
        Assert.StartsWith($"v{FaceIcon.Version}|{FaceIcon.FaceLayer}|", FaceIconCache.Stamp(game), StringComparison.Ordinal);

        // a current cache is used without opening the game folder
        string? path = FaceIconCache.Ensure(data, game, () => throw new InvalidOperationException("the folder must not be opened for a current cache"));
        Assert.Equal(icon, path);
        IconFrame? frame = FaceIconCache.ReadFrame(icon, 256);
        Assert.Equal(256, frame?.Size);
        Assert.Equal(48, FaceIconCache.ReadFrame(icon, 48)?.Size);
        Assert.Equal(256, FaceIconCache.ReadFrame(icon, 500)?.Size);                        // a size that is not there: the largest
        Assert.Null(FaceIconCache.ReadFrame(Path.Combine(_root, "none.ico"), 32));

        // a stale cache is rebuilt: the opener runs; here it fails, so the old file stays and null comes back
        var log = new List<string>();
        Assert.Null(FaceIconCache.Ensure(data, Path.Combine(_root, "other"), () => throw new InvalidDataException("no such folder"), log.Add));
        Assert.Contains(log, l => l.StartsWith("face icon: not built (InvalidDataException", StringComparison.Ordinal));
        Assert.True(File.Exists(icon));
    }

    [Fact]
    public void TheShutdownWaitForTheIconBuildNeverThrows()
    {
        // package K22: GameApp.Dispose waits for the icon task so the archives are not unmapped under it; a build that faulted with an exception Ensure does not
        // catch (and that the loop never consumed, because the run failed before it) is finished too, and must not throw out of Dispose over the real failure
        CancellationToken test = TestContext.Current.CancellationToken;
        var faulted = Task.Run(() => throw new IndexOutOfRangeException("a decoder bug"), test);
        Assert.Throws<AggregateException>(() => faulted.Wait(TimeSpan.FromSeconds(5), test));
        Assert.True(GameApp.WaitQuietly(faulted, TimeSpan.FromSeconds(5)));
        Assert.True(GameApp.WaitQuietly(Task.FromResult<string?>("done"), TimeSpan.Zero));
        Assert.True(GameApp.WaitQuietly(Task.FromCanceled(new CancellationToken(canceled: true)), TimeSpan.Zero));
        using var gate = new ManualResetEventSlim(false);
        Task running = Task.Run(() => gate.Wait(test), test);
        Assert.False(GameApp.WaitQuietly(running, TimeSpan.FromMilliseconds(50)));         // still running: false, as Task.Wait says
        gate.Set();
        Assert.True(GameApp.WaitQuietly(running, TimeSpan.FromSeconds(5)));
    }
}

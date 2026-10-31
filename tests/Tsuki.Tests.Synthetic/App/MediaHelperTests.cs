using Tsuki.App.Play;
using Tsuki.App.Services;
using Tsuki.Media.Movie;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>The pixel helpers of the app: thumbnails and the movie colour conversion.</summary>
public sealed class MediaHelperTests
{
    [Fact]
    public void AThumbnailRoundTripsThroughItsSaveFileFormat()
    {
        byte[] px = new byte[ThumbnailCodec.Width * ThumbnailCodec.Height * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = (byte)(i / 4 % 251);
            px[i + 1] = (byte)(i / 1024 % 253);
            px[i + 2] = 7;
            px[i + 3] = 255;
        }

        var t = ThumbnailCodec.Encode(ThumbnailCodec.Width, ThumbnailCodec.Height, px);
        Assert.Equal(ThumbnailCodec.Format, t.Format);
        (int w, int h, byte[] back)? decoded = ThumbnailCodec.Decode(t);
        Assert.NotNull(decoded);
        Assert.Equal((ThumbnailCodec.Width, ThumbnailCodec.Height), (decoded.Value.w, decoded.Value.h));
        Assert.Equal(px, decoded.Value.back);
    }

    [Fact]
    public void ADamagedOrForeignThumbnailDecodesToNull()
    {
        Assert.Null(ThumbnailCodec.Decode(null));
        Assert.Null(ThumbnailCodec.Decode(new Tsuki.Engine.Save.SlotThumbnail(9, [1, 2, 3, 4, 5, 6, 7, 8, 9])));
        byte[] noise = new byte[8 * 8 * 4];
        new Random(5).NextBytes(noise);
        var good = ThumbnailCodec.Encode(8, 8, noise);
        byte[] cut = good.Data[..(good.Data.Length / 2)];
        Assert.Null(ThumbnailCodec.Decode(new Tsuki.Engine.Save.SlotThumbnail(ThumbnailCodec.Format, cut)));
    }

    [Fact]
    public void DownscaleAveragesBoxesAndKeepsAFlatColour()
    {
        byte[] px = new byte[1920 * 1080 * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 200;
            px[i + 1] = 100;
            px[i + 2] = 50;
            px[i + 3] = 17;
        }

        byte[] small = ThumbnailCodec.Downscale(px, 1920, 1080);
        Assert.Equal(ThumbnailCodec.Width * ThumbnailCodec.Height * 4, small.Length);
        Assert.Equal([200, 100, 50, 255], small[..4]);
        Assert.Equal([200, 100, 50, 255], small[^4..]);
    }

    [Theory]
    [InlineData(16, 128, 128, 0, 0, 0)]          // black
    [InlineData(235, 128, 128, 255, 255, 255)]   // white
    [InlineData(126, 128, 128, 128, 128, 128)]   // mid grey (limited range)
    public void LimitedRangeBt709GreysConvertToTheirRgb(int y, int u, int v, int r, int g, int b)
    {
        var f = new VideoFrame(4, 2);
        Array.Fill(f.Y, (byte)y);
        Array.Fill(f.U, (byte)u);
        Array.Fill(f.V, (byte)v);
        byte[] rgba = new byte[4 * 2 * 4];
        YuvConverter.ToRgba(f, rgba);
        for (int p = 0; p < 8; p++)
        {
            Assert.InRange(rgba[p * 4], r - 1, r + 1);
            Assert.InRange(rgba[(p * 4) + 1], g - 1, g + 1);
            Assert.InRange(rgba[(p * 4) + 2], b - 1, b + 1);
            Assert.Equal(255, rgba[(p * 4) + 3]);
        }
    }

    [Fact]
    public void RedAndBlueChromaPushTheRightChannels()
    {
        var f = new VideoFrame(2, 2);
        Array.Fill(f.Y, (byte)81);
        Array.Fill(f.U, (byte)90);
        Array.Fill(f.V, (byte)240);
        byte[] rgba = new byte[16];
        YuvConverter.ToRgba(f, rgba);
        Assert.True(rgba[0] > 200 && rgba[1] < 60 && rgba[2] < 60, $"{rgba[0]},{rgba[1]},{rgba[2]}");
    }
}

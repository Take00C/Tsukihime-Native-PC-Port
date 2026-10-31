using Tsuki.Data.Images;

namespace Tsuki.Tests.Data.Images;

/// <summary>The header rules of images.md 2: sizes, tile counts, the palette rule, the four formats, and what is rejected.</summary>
public sealed class ImageHeaderTests
{
    private static byte[] Palette(int entries, Func<int, (byte R, byte G, byte B, byte A)> f)
    {
        byte[] p = new byte[4 * entries];
        for (int i = 0; i < entries; i++)
        {
            var (r, g, b, a) = f(i);
            p[4 * i] = r;
            p[(4 * i) + 1] = g;
            p[(4 * i) + 2] = b;
            p[(4 * i) + 3] = a;
        }

        return p;
    }

    [Fact]
    public void AHepImageHasABorderAndAShownSizeWithoutIt()
    {
        // 3 x 2 tiles of 512 x 512, border: stored 1536 x 1024, shown 1536 - 2 * 3 by 1024 - 2 * 2 (images.md 2.2)
        var h = ImageHeader.Parse(SyntheticImages.Section0(1536, 1024, 512, 512, 12, 0x11, 1, null, new byte[6]));
        Assert.Equal(PixelFormat.Hep8, h.Format);
        Assert.Equal(1530, h.DisplayWidth);
        Assert.Equal(1020, h.DisplayHeight);
        Assert.Equal(510, h.InnerWidth);
        Assert.Equal(510, h.InnerHeight);
        Assert.Equal(6, h.Tiles);
        Assert.Equal(0x20 + (512 * 512) + 1024, h.TileBytes);
        Assert.Equal(4 * 512 * 512, h.TileRgbaBytes);
        Assert.Null(h.Palette);
        Assert.Equal((510, 510), h.TileOrigin(4));
        Assert.Equal((1, 1, 510, 510, 510, 510), h.TileWindow(4));
    }

    [Fact]
    public void TheBorderIsOnlyDroppedFromSizesOfAtLeastEight()
    {
        // engine VA 0xD8C4F: w -= 2 * tiles_x only when attr bit 0 is set and w >= 8
        var small = ImageHeader.Parse(SyntheticImages.Section0(7, 7, 4, 4, 12, 0x11, 1, null, new byte[4]));
        Assert.Equal(7, small.DisplayWidth);
        Assert.Equal(7, small.DisplayHeight);
        var eight = ImageHeader.Parse(SyntheticImages.Section0(8, 8, 8, 8, 12, 0x11, 1, null, new byte[1]));
        Assert.Equal(6, eight.DisplayWidth);
        var noBorder = ImageHeader.Parse(SyntheticImages.Section0(100, 50, 64, 32, 11, 0x14, 0, null, new byte[4]));
        Assert.Equal(PixelFormat.Rgb565LoA, noBorder.Format);
        Assert.Equal((100, 50), (noBorder.DisplayWidth, noBorder.DisplayHeight));
        Assert.Equal(4 * 64 * 32, noBorder.TileBytes);
    }

    [Fact]
    public void ThePaletteRuleFollowsTheDepthCode()
    {
        var pal16 = ImageHeader.Parse(SyntheticImages.Section0(16, 16, 16, 16, 1, 0x00, 0, Palette(16, i => ((byte)i, 0, 0, 0)), new byte[1]));
        Assert.Equal(PixelFormat.Pal4, pal16.Format);
        Assert.Equal(16, pal16.Palette!.Length);
        Assert.Equal(128, pal16.TileBytes);
        var pal16b = ImageHeader.Parse(SyntheticImages.Section0(16, 16, 16, 16, 1, 0x10, 0, Palette(16, i => ((byte)i, 0, 0, 0)), new byte[1]));
        Assert.Equal(PixelFormat.Pal4, pal16b.Format);

        foreach (int depth in new[] { 0x01, 0x11 })
        {
            var pal256 = ImageHeader.Parse(SyntheticImages.Section0(32, 32, 16, 16, 1, depth, 0, Palette(256, i => ((byte)i, 0, 0, 0)), new byte[4]));
            Assert.Equal(PixelFormat.Pal8, pal256.Format);
            Assert.Equal(256, pal256.Palette!.Length);
            Assert.Equal(256, pal256.TileBytes);
        }
    }

    [Fact]
    public void The256ColourPaletteIsUnswizzledAndItsAlphaIsFixed()
    {
        // raw entry k = (k, 255 - k, k ^ 0x55, alpha); CSM1 exchanges entries 8..15 and 16..23 of every block of 32
        byte AlphaOf(int k) => (byte)((k % 5) switch { 0 => 0x80, 1 => 0x40, 2 => 0x7F, 3 => 0xFF, _ => 0x00 });
        var raw = Palette(256, k => ((byte)k, (byte)(255 - k), (byte)(k ^ 0x55), AlphaOf(k)));
        var h = ImageHeader.Parse(SyntheticImages.Section0(16, 16, 16, 16, 1, 0x01, 0, raw, new byte[1]));
        for (int i = 0; i < 256; i++)
        {
            int block = i & ~31;
            int pos = i & 31;
            int stored = pos is >= 8 and < 16 ? i + 8 : pos is >= 16 and < 24 ? i - 8 : i;
            Assert.True(block <= stored && stored < block + 32);
            uint a = (uint)AlphaOf(stored);
            a = a == 0x80 ? 0xFF : (2 * a) & 0xFF;
            uint want = (uint)stored | ((uint)(255 - stored) << 8) | ((uint)(stored ^ 0x55) << 16) | (a << 24);
            Assert.True(want == h.Palette![i], $"entry {i}: {h.Palette[i]:x8} != {want:x8}");
        }

        // the alpha rule on its own: 0x80 -> 0xFF, 0x40 -> 0x80, 0x7F -> 0xFE, 0xFF -> 0xFE, 0 -> 0
        Assert.Equal(0xFFu, h.Palette![0] >> 24);
    }

    [Fact]
    public void FlagsFollowThePaletteAndAreExposedAsBytes()
    {
        byte[] flags = [0, 1, 2, 1];
        var h = ImageHeader.Parse(SyntheticImages.Section0(32, 32, 16, 16, 11, 0x14, 0, null, flags));
        Assert.True(h.TileFlags.SequenceEqual(flags));
        Assert.False(h.IsLive(0));
        Assert.True(h.IsLive(1) && h.IsLive(2) && h.IsLive(3));
        Assert.Contains("flags={0:1,1:2,2:1}", h.Describe());
        Assert.Contains("format=Rgb565LoA", h.Describe());
    }

    [Fact]
    public void ABrokenHeaderIsRejectedWithTheRuleThatFailed()
    {
        byte[] good = SyntheticImages.Section0(32, 32, 16, 16, 11, 0x14, 0, null, new byte[4]);
        Assert.Contains("too short", Assert.Throws<ImageFormatException>(() => ImageHeader.Parse(good.AsSpan(0, 15))).Message);

        byte[] zero = (byte[])good.Clone();
        zero[4] = 0;
        zero[5] = 0;
        Assert.Contains("zero dimension", Assert.Throws<ImageFormatException>(() => ImageHeader.Parse(zero)).Message);

        byte[] tiles = (byte[])good.Clone();
        tiles[8] = 3;
        Assert.Contains("inconsistent", Assert.Throws<ImageFormatException>(() => ImageHeader.Parse(tiles)).Message);

        Assert.Contains("expected 16 + 0 + 4", Assert.Throws<ImageFormatException>(() => ImageHeader.Parse(good.AsSpan(0, 19))).Message);

        // type 1 with depth code 4 has no palette, so the length would pass; the combination is not a retail format
        byte[] odd = SyntheticImages.Section0(32, 32, 16, 16, 1, 0x04, 0, null, new byte[4]);
        Assert.Contains("unsupported image type", Assert.Throws<ImageFormatException>(() => ImageHeader.Parse(odd)).Message);
        byte[] type3 = SyntheticImages.Section0(32, 32, 16, 16, 3, 0x11, 0, null, new byte[4]);
        Assert.Contains("unsupported image type", Assert.Throws<ImageFormatException>(() => ImageHeader.Parse(type3)).Message);
    }

    [Fact]
    public void ATileLargerThanAnyRetailTileIsRejectedBeforeAnythingIsAllocated()
    {
        byte[] huge = SyntheticImages.Section0(65535, 65535, 65535, 65535, 11, 0x14, 0, null, new byte[1]);
        Assert.Contains("larger than any retail tile", Assert.Throws<ImageFormatException>(() => ImageHeader.Parse(huge)).Message);
    }
}

using Tsuki.Data.Images;
using static Tsuki.Tests.Data.Images.SyntheticImages;

namespace Tsuki.Tests.Data.Images;

/// <summary>Pixel conversion of the four formats against the plain reference, and the three vector widths of RGB565+LO+A against each other.</summary>
public sealed class TileConverterTests
{
    private static ImageHeader HeaderFor(Kind kind, int tw, int th, byte[]? palette)
    {
        int bmp = kind switch { Kind.Hep8 => 12, Kind.Rgb565 => 11, _ => 1 };
        int depth = kind switch { Kind.Pal4 => 0, Kind.Pal8 => 1, Kind.Hep8 => 0x11, _ => 0x14 };
        return ImageHeader.Parse(Section0(tw, th, tw, th, bmp, depth, kind == Kind.Hep8 ? 1 : 0, palette, new byte[] { 1 }));
    }

    [Theory]
    [InlineData(Kind.Pal8, 16, 16)]
    [InlineData(Kind.Pal8, 7, 5)]
    [InlineData(Kind.Pal4, 16, 16)]
    [InlineData(Kind.Pal4, 5, 5)]
    [InlineData(Kind.Pal4, 3, 7)]
    [InlineData(Kind.Hep8, 8, 8)]
    [InlineData(Kind.Hep8, 11, 13)]
    [InlineData(Kind.Rgb565, 16, 16)]
    [InlineData(Kind.Rgb565, 9, 7)]
    [InlineData(Kind.Rgb565, 1, 1)]
    public void EveryFormatConvertsLikeTheReference(Kind kind, int tw, int th)
    {
        var rng = new Random(((int)kind * 1000) + (tw * 31) + th);
        int n = tw * th;
        byte[]? palette = kind switch { Kind.Pal4 => RandomBytes(rng, 64), Kind.Pal8 => RandomBytes(rng, 1024), _ => null };
        byte[] raw = kind switch
        {
            Kind.Pal4 => RandomBytes(rng, n / 2),
            Kind.Pal8 => RandomBytes(rng, n),
            Kind.Hep8 => HepTile(rng, tw, th),
            _ => RandomBytes(rng, 4 * n),
        };
        var header = HeaderFor(kind, tw, th, palette);
        Assert.Equal(raw.Length, header.TileBytes);
        byte[] got = new byte[4 * n];
        TileConverter.Convert(header, raw, got);
        Assert.True(ReferenceTile(kind, raw, palette, tw, th).AsSpan().SequenceEqual(got), $"{kind} {tw}x{th} differs from the reference");
    }

    [Fact]
    public void AHepPaletteAlphaIsTwiceTheValueClampedAt0x7F()
    {
        var rng = new Random(5);
        byte[] raw = HepTile(rng, 2, 2);
        for (int i = 0; i < 256; i++)
        {
            raw[0x20 + 4 + (4 * i) + 3] = (byte)i;
        }

        // indices 0..3 are the first four texels
        raw[0x20] = 0x7E;
        raw[0x21] = 0x7F;
        raw[0x22] = 0x80;
        raw[0x23] = 0xFF;
        byte[] got = new byte[16];
        TileConverter.Convert(HeaderFor(Kind.Hep8, 2, 2, null), raw, got);
        Assert.Equal(new byte[] { 0xFC, 0xFE, 0xFE, 0xFE }, new[] { got[3], got[7], got[11], got[15] });
    }

    [Fact]
    public void APal4TileIsOneLinearStreamOfNibbles()
    {
        // images.md 3.2: byte i gives texel 2i (low nibble) and 2i + 1 (high nibble) whatever the row width; an odd count ends on index 0
        byte[] palette = new byte[64];
        for (int i = 0; i < 16; i++)
        {
            palette[4 * i] = (byte)(i * 16);
            palette[(4 * i) + 3] = 0x40;
        }

        var header = HeaderFor(Kind.Pal4, 3, 3, palette);
        byte[] raw = [0x21, 0x43, 0x65, 0x87];
        byte[] got = new byte[36];
        TileConverter.Convert(header, raw, got);
        byte[] reds = Enumerable.Range(0, 9).Select(i => got[4 * i]).ToArray();
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80, 0x00 }, reds);
        Assert.Equal(0x80, got[3]);
    }

    [Fact]
    public void AHepBlockThatDisagreesWithTheHeaderIsRejected()
    {
        var rng = new Random(9);
        var header = HeaderFor(Kind.Hep8, 8, 8, null);
        byte[] ok = HepTile(rng, 8, 8);
        byte[] output = new byte[4 * 64];
        TileConverter.Convert(header, ok, output);

        byte[] magic = (byte[])ok.Clone();
        magic[0] = (byte)'X';
        Assert.Contains("HEP header mismatch", Assert.Throws<ImageFormatException>(() => TileConverter.Convert(header, magic, output)).Message);

        byte[] width = (byte[])ok.Clone();
        width[0x14] = 9;
        Assert.Throws<ImageFormatException>(() => TileConverter.Convert(header, width, output));

        byte[] total = (byte[])ok.Clone();
        total[4]++;
        Assert.Throws<ImageFormatException>(() => TileConverter.Convert(header, total, output));

        Assert.Contains("needs", Assert.Throws<ImageFormatException>(() => TileConverter.Convert(header, ok.AsSpan(0, ok.Length - 1), output)).Message);
        Assert.Throws<ArgumentException>(() => TileConverter.Convert(header, ok, new byte[10]));
    }

    [Fact]
    public void Rgb565LoAGivesTheSamePixelsWithEveryVectorWidth()
    {
        var rng = new Random(77);
        foreach (int n in Enumerable.Range(0, 80).Concat([255, 256, 257, 4099]))
        {
            byte[] planes = RandomBytes(rng, 4 * n);
            byte[] reference = n == 0 ? [] : ReferenceTile(Kind.Rgb565, planes, null, n, 1);
            foreach (var level in Enum.GetValues<SimdLevel>())
            {
                byte[] got = new byte[4 * n];
                TileConverter.Rgb565LoAToRgba(planes, got, n, level);
                Assert.True(reference.AsSpan().SequenceEqual(got), $"n={n} {level}");
            }
        }
    }

    [Fact]
    public void Rgb565LoAKeepsTheLowBitsOfEveryChannel()
    {
        // word 0xFFFF with low byte 0xFF gives 0xFF in every channel; word 0 with low byte 0 gives 0; the low plane fills the 3 / 2 / 3 bits
        byte[] planes = [0xFF, 0xFF, 0x00, 0x00, /* low */ 0xFF, 0x00, /* alpha */ 0x12, 0x34];
        byte[] got = new byte[8];
        TileConverter.Rgb565LoAToRgba(planes, got, 2);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0x12, 0, 0, 0, 0x34 }, got);
        byte[] bits = [0x00, 0x00, /* low = b:3 g:2 r:3 as r = low >> 5, g = (low >> 3) & 3, b = low & 7 */ 0b101_10_011, 0x00];
        got = new byte[4];
        TileConverter.Rgb565LoAToRgba(bits, got, 1);
        Assert.Equal(new byte[] { 0b101, 0b10, 0b011, 0 }, got);
    }

    [Fact]
    public void ARgb565PlaneThatIsTooShortIsRejected()
    {
        var header = HeaderFor(Kind.Rgb565, 4, 4, null);
        Assert.Throws<ImageFormatException>(() => TileConverter.Convert(header, new byte[63], new byte[64]));
        Assert.Throws<ArgumentException>(() => TileConverter.Rgb565LoAToRgba(new byte[7], new byte[8], 2));
    }
}

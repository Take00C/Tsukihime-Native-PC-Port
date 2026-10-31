using System.Security.Cryptography;
using Tsuki.Data.Containers;
using Tsuki.Data.Images;
using Tsuki.Engine.Contracts;
using static Tsuki.Tests.Data.Images.SyntheticImages;

namespace Tsuki.Tests.Data.Images;

/// <summary>
/// Whole synthetic images: tile placement with borders and clipping (images.md 2.2), flag-0 tiles, the <see cref="Picture"/> of the
/// data contract, the streaming SHA-1 and what is rejected. The expected pictures come from <see cref="SyntheticImages.Make"/>.
/// </summary>
public sealed class ImageAssemblyTests
{
    public static TheoryData<Kind, int, int, int, int, bool> Shapes => new()
    {
        { Kind.Hep8, 12, 15, 6, 5, true },       // 2 x 3 tiles, inner 4 x 3, shown 8 x 9
        { Kind.Hep8, 40, 24, 10, 8, true },      // 4 x 3 tiles
        { Kind.Hep8, 7, 7, 4, 4, true },         // below 8: nothing is dropped, and the inner tiles leave gaps
        { Kind.Pal8, 20, 13, 8, 8, false },      // 3 x 2 tiles, the last column is clipped
        { Kind.Pal8, 8, 8, 8, 8, false },
        { Kind.Rgb565, 17, 13, 9, 7, false },    // odd tile pixel count (63): the vector loop ends in a scalar tail
        { Kind.Rgb565, 64, 64, 32, 32, false },
        { Kind.Pal4, 10, 5, 5, 5, false },       // odd pixel count (25)
        { Kind.Pal4, 16, 16, 16, 16, false },
    };

    private static ImageMzp Open(Made made) => ImageMzp.Open(made.Container);

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheDisplayedPictureIsTheReferenceAssembly(Kind kind, int w, int h, int tw, int th, bool border)
    {
        var made = Make(kind, w, h, tw, th, border, seed: (w * 7) + h);
        var image = Open(made);
        Assert.Equal((made.Width, made.Height), (image.Header.DisplayWidth, image.Header.DisplayHeight));
        Assert.True(made.ExpectedDisplayed.AsSpan().SequenceEqual(image.DecodeDisplayed()), "sequential decode differs");
        Assert.True(made.ExpectedDisplayed.AsSpan().SequenceEqual(image.DecodeDisplayed(maxDegreeOfParallelism: 4)), "parallel decode differs");
        foreach (var level in Enum.GetValues<SimdLevel>())
        {
            Assert.True(made.ExpectedDisplayed.AsSpan().SequenceEqual(image.DecodeDisplayed(simd: level)), $"{level} differs");
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheStreamingSha1EqualsTheSha1OfTheDisplayedPicture(Kind kind, int w, int h, int tw, int th, bool border)
    {
        var made = Make(kind, w, h, tw, th, border, seed: (w * 11) + h);
        string want = Convert.ToHexStringLower(SHA1.HashData(made.ExpectedDisplayed));
        using var hasher = new PictureHasher();
        Assert.Equal(want, hasher.HashHex(Open(made)));
        // the hasher keeps its buffers: a second picture, and the same picture again, give their own digests
        Assert.Equal(want, hasher.HashHex(ImageMzp.Open(MzpContainer.Parse(made.Container))));
        var other = Make(kind, w, h, tw, th, border, seed: 99);
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(other.ExpectedDisplayed)), hasher.HashHex(Open(other)));
        Assert.Equal(want, hasher.HashHex(Open(made)));
    }

    [Fact]
    public void FlagZeroTilesStayTransparentAndAreNotDecoded()
    {
        // an all-empty picture hashes as transparent pixels; a stream that would not decode is never touched when its flag is 0
        var made = Make(Kind.Hep8, 12, 15, 6, 5, true, seed: 3, flagsOverride: new byte[6]);
        Assert.All(made.ExpectedDisplayed, b => Assert.Equal(0, b));
        Assert.Equal(0, made.TilesLive);
        var image = Open(made);
        Assert.True(image.DecodeDisplayed().All(b => b == 0));

        var sections = Enumerable.Range(0, 7).Select(i => MzpContainer.Parse(made.Container).Section(i).ToArray()).ToList();
        sections[3] = "MZX0"u8.ToArray().Concat(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 0x00 }).ToArray(); // declares 2 GiB, flag 0
        var broken = ImageMzp.Open(Container(sections));
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(made.ExpectedDisplayed)), new PictureHasher().HashHex(broken));
        Assert.All(broken.ToPicture().Tiles, t => Assert.Empty(t.Rgba.ToArray()));
    }

    [Fact]
    public void ThePictureOfTheContractCarriesStoredTilesAndTheirDisplayedWindows()
    {
        // 3 x 2 tiles of 6 x 5 with a border: stored 18 x 10, shown 12 x 6 -> inner 4 x 3 per tile
        byte[] flags = [1, 0, 2, 2, 1, 0];
        var made = Make(Kind.Hep8, 18, 10, 6, 5, true, seed: 8, flagsOverride: flags);
        var image = Open(made);
        Assert.Equal((12, 6), (image.Header.DisplayWidth, image.Header.DisplayHeight));
        var picture = image.ToPicture(maxDegreeOfParallelism: 3);
        Assert.Equal((12, 6), (picture.Width, picture.Height));
        Assert.Equal(6, picture.Tiles.Length);
        for (int t = 0; t < 6; t++)
        {
            var tile = picture.Tiles[t];
            Assert.Equal(t, tile.Index);
            Assert.Equal((TileFlags)flags[t], tile.Flags);
            Assert.Equal((6, 5), (tile.StoredWidth, tile.StoredHeight));
            Assert.Equal(new PixelRect(1, 1, 4, 3), tile.Source);
            Assert.Equal((t % 3 * 4, t / 3 * 3), (tile.X, tile.Y));
            Assert.Equal(flags[t] == 0 ? 0 : 4 * 6 * 5, tile.Rgba.Length);
        }

        Assert.True(made.ExpectedDisplayed.AsSpan().SequenceEqual(ImageMzp.Assemble(picture)));
    }

    [Fact]
    public void ATileOutsideTheDisplayedSizeHasAnEmptyWindow()
    {
        // 7 x 7 with a border: tiles of 4 x 4 have 2 x 2 inner pixels, the picture stays 7 x 7, tile column 3 does not exist: the second column starts at x = 2
        var made = Make(Kind.Hep8, 7, 7, 4, 4, true, seed: 4, flagsOverride: [1, 1, 1, 1]);
        var picture = Open(made).ToPicture();
        Assert.Equal((7, 7), (picture.Width, picture.Height));
        Assert.Equal(new PixelRect(1, 1, 2, 2), picture.Tiles[3].Source);
        Assert.Equal((2, 2), (picture.Tiles[3].X, picture.Tiles[3].Y));
    }

    [Fact]
    public void AMalformedTileNamesTheTileAndTheRule()
    {
        var made = Make(Kind.Pal8, 16, 16, 8, 8, false, seed: 2, flagsOverride: [1, 1, 1, 1]);
        var container = MzpContainer.Parse(made.Container);
        var scratch = new TileScratch();
        byte[] rgba = new byte[4 * 64];

        // a tile that declares fewer bytes than the format needs
        var sections = Enumerable.Range(0, 5).Select(i => container.Section(i).ToArray()).ToList();
        sections[2] = Mzx0Literal(new byte[10]);
        var shortTile = ImageMzp.Open(Container(sections));
        var e = Assert.Throws<ImageFormatException>(() => shortTile.DecodeTile(1, scratch, rgba));
        Assert.StartsWith("tile 1:", e.Message);
        Assert.Contains("needs 64", e.Message);

        // a stream that is cut off
        sections[2] = sections[2][..12];
        var truncated = ImageMzp.Open(Container(sections));
        var t = Assert.Throws<ImageFormatException>(() => truncated.DecodeTile(1, scratch, rgba));
        Assert.StartsWith("tile 1:", t.Message);

        // a stream that declares far more than it can hold
        sections[2] = "MZX0"u8.ToArray().Concat(new byte[] { 0xFF, 0xFF, 0xFF, 0x0F, 0x03, 0, 0 }).ToArray();
        var bomb = ImageMzp.Open(Container(sections));
        Assert.Contains("cannot come from", Assert.Throws<ImageFormatException>(() => bomb.DecodeTile(1, scratch, rgba)).Message);

        Assert.Throws<ArgumentOutOfRangeException>(() => shortTile.DecodeTile(4, scratch, rgba));
    }

    [Fact]
    public void TheSectionCountMustBeOnePlusTheTiles()
    {
        var made = Make(Kind.Pal8, 16, 16, 8, 8, false, seed: 2);
        var container = MzpContainer.Parse(made.Container);
        var sections = Enumerable.Range(0, 4).Select(i => container.Section(i).ToArray()).ToList();
        var e = Assert.Throws<ImageFormatException>(() => ImageMzp.Open(Container(sections)));
        Assert.Contains("4 sections for 4 tiles", e.Message);
        Assert.Throws<ImageFormatException>(() => ImageMzp.Open(Container([])));
    }

    [Fact]
    public void IsImageFollowsTheClassificationOfTheReference()
    {
        var made = Make(Kind.Pal8, 16, 16, 8, 8, false, seed: 2);
        var container = MzpContainer.Parse(made.Container);
        Assert.True(ImageMzp.IsImage(container));

        // a set (every section nested) is not an image; neither is a container with a wrong section count or a non-MZX tile
        var set = MzpContainer.Parse(Container([made.Container, made.Container]));
        Assert.False(ImageMzp.IsImage(set));
        Assert.True(ImageMzp.IsImage(set.Child(1)));
        var sections = Enumerable.Range(0, 5).Select(i => container.Section(i).ToArray()).ToList();
        Assert.False(ImageMzp.IsImage(MzpContainer.Parse(Container(sections.Take(4).ToList()))));
        sections[3] = new byte[20];
        Assert.False(ImageMzp.IsImage(MzpContainer.Parse(Container(sections))));
        Assert.False(ImageMzp.IsImage(MzpContainer.Parse(Container([]))));

        var found = new List<string>();
        PictureWalker.Walk(set, (path, c) => found.Add(string.Join('/', path)));
        Assert.Equal(["0", "1"], found);
        Assert.Equal("allpac3#5/17", PictureWalker.Label("allpac3", 5, [17]));
        Assert.Equal("allpac0#12", PictureWalker.Label("allpac0", 12, []));
    }
}

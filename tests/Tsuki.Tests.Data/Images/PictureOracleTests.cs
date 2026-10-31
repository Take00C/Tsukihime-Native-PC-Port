using System.Security.Cryptography;
using Tsuki.Data.Containers;
using Tsuki.Data.Images;

namespace Tsuki.Tests.Data.Images;

/// <summary>
/// The C# picture decoder against the Python oracle (ROADMAP M1 acceptance 4): the SHA-1 of the displayed RGBA8 of 2,000 stratified
/// pictures, and the header facts the parser reads, equal <c>tsuki.image</c> on the real data. The sample is
/// <c>samples/golden/image/sha1/pictures.tsv</c>, written by <c>tools/oracle/image.py</c>. The all-pictures run is
/// <c>Tsuki.Cli decode-all --pictures</c>.
/// </summary>
public sealed class PictureOracleTests
{
    [GameDataFact]
    public void TheGoldenSampleIsTheFileTheManifestLists()
    {
        ImageGolden.VerifyAgainstManifest("sha1");
        var rows = ImageGolden.Rows("sha1");
        Assert.Equal(2000, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.Label).Distinct().Count());
    }

    [GameDataFact]
    public void TheSampleIsStratifiedOverEveryArchiveFormatAndTileLayout()
    {
        var rows = ImageGolden.Rows("sha1");
        var bySource = rows.GroupBy(r => r.Source).ToDictionary(g => g.Key, g => g.Count());
        foreach (string source in new[] { "allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3" })
        {
            Assert.True(bySource.GetValueOrDefault(source) >= 100, $"{source}: {bySource.GetValueOrDefault(source)}");
        }

        Assert.True(bySource.GetValueOrDefault("allpac4") >= 10);
        Assert.True(bySource.GetValueOrDefault("parts") >= 10);
        Assert.Equal(1, bySource.GetValueOrDefault("allscr.mrg"));
        foreach (string ft in new[] { "ft_en.mrg", "ft_ja.mrg", "ft_ko.mrg", "ft_zc.mrg", "ft_zt.mrg" })
        {
            Assert.True(bySource.GetValueOrDefault(ft) >= 1, ft);
        }

        var byFormat = rows.GroupBy(r => r.Format).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(["hep8", "pal4", "pal8", "rgb565lo+a"], byFormat.Keys.Order());
        Assert.Equal(20, byFormat["pal4"]);          // all of the rare format
        Assert.True(byFormat["hep8"] >= 500 && byFormat["rgb565lo+a"] >= 100 && byFormat["pal8"] >= 500, string.Join(", ", byFormat));
        Assert.Contains(rows, r => r.Format == "hep8" && r.TileH == 256);      // the 256-row layout of IMG3885
        Assert.Contains(rows, r => r.Format == "hep8" && r.TileH == 512);
        Assert.Contains(rows, r => r.LiveTiles == 0);                          // all-transparent fills
        Assert.Contains(rows, r => r.LiveTiles < r.TilesX * r.TilesY && r.LiveTiles > 0);
        Assert.Contains(rows, r => r.ShownWidth * r.ShownHeight >= 80_000_000); // the giants of images.md 10
        Assert.Contains(rows, r => r.Attr == 1 && r.ShownWidth != r.Width);
        Assert.Contains(rows, r => r.Name == "IMG1481.MZP");
        Assert.Contains(rows, r => r.Name == "DUMMY.MRG");
    }

    [GameDataFact]
    public void PictureSha1sAndHeaderFactsEqualTheOracleOnTheStratifiedSample()
    {
        var rows = ImageGolden.Rows("sha1");
        var m = new Mismatches();
        using var archives = new ArchiveSet();
        long hashed = 0;
        Parallel.ForEach(
            rows,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            () => new PictureHasher(),
            (row, _, hasher) =>
            {
                try
                {
                    var image = ImageMzp.Open(archives.Open(row));
                    var h = image.Header;
                    m.Equal(row.Label + " format", row.Format, ImageGolden.FormatName(h.Format));
                    m.Equal(row.Label + " size", $"{row.Width}x{row.Height} shown {row.ShownWidth}x{row.ShownHeight}", $"{h.Width}x{h.Height} shown {h.DisplayWidth}x{h.DisplayHeight}");
                    m.Equal(row.Label + " tiles", $"{row.TilesX}x{row.TilesY} of {row.TileW}x{row.TileH} attr {row.Attr}", $"{h.TilesX}x{h.TilesY} of {h.TileWidth}x{h.TileHeight} attr {h.Attr}");
                    int empty = h.TileFlags.Count((byte)0);
                    m.Equal(row.Label + " live tiles", row.LiveTiles, h.Tiles - empty);
                    m.Equal(row.Label + " sha1", row.Sha1, hasher.HashHex(image));
                    Interlocked.Increment(ref hashed);
                }
                catch (ContainerFormatException e)
                {
                    m.Add($"{row.Label}: {e.Message}");
                }

                return hasher;
            },
            hasher => hasher.Dispose());
        m.AssertNone("picture SHA-1 sample");
        Assert.Equal(rows.Count, hashed);
    }

    [GameDataFact]
    public void ThePictureOfTheContractAssemblesToTheOraclePictureWithEveryVectorWidth()
    {
        // a few small pictures of each format through ToPicture (parallel tile decode) and the three RGB565 vector widths
        var rows = ImageGolden.Rows("sha1");
        using var archives = new ArchiveSet();
        var chosen = rows.GroupBy(r => r.Format)
            .SelectMany(g => g.Where(r => r.ShownWidth * r.ShownHeight is > 200 and < 3_000_000).OrderBy(r => r.Label, StringComparer.Ordinal).Where((_, i) => i % 7 == 0).Take(5))
            .ToList();
        Assert.Equal(4, chosen.Select(r => r.Format).Distinct().Count());
        var m = new Mismatches();
        foreach (var row in chosen)
        {
            var image = ImageMzp.Open(archives.Open(row));
            foreach (var level in Enum.GetValues<SimdLevel>())
            {
                var picture = image.ToPicture(maxDegreeOfParallelism: 4, simd: level);
                m.Equal($"{row.Label} {level} size", (row.ShownWidth, row.ShownHeight), (picture.Width, picture.Height));
                m.Equal($"{row.Label} {level} tiles", row.TilesX * row.TilesY, picture.Tiles.Length);
                m.Equal($"{row.Label} {level} sha1", row.Sha1, Convert.ToHexStringLower(SHA1.HashData(ImageMzp.Assemble(picture))));
                if (level != SimdLevel.Auto)
                {
                    continue;
                }

                // stored tiles: flag 0 empty, every other tile the stored size, its window inside both the tile and the picture
                foreach (var tile in picture.Tiles)
                {
                    bool live = image.Header.IsLive(tile.Index);
                    m.Equal($"{row.Label} tile {tile.Index} bytes", live ? 4 * row.TileW * row.TileH : 0, tile.Rgba.Length);
                    if (tile.Source.Width > 0 && tile.Source.Height > 0)
                    {
                        bool inside = tile.Source.X + tile.Source.Width <= tile.StoredWidth && tile.Source.Y + tile.Source.Height <= tile.StoredHeight
                            && tile.X + tile.Source.Width <= picture.Width && tile.Y + tile.Source.Height <= picture.Height;
                        m.Equal($"{row.Label} tile {tile.Index} window inside", true, inside);
                    }
                }
            }
        }

        m.AssertNone("Picture of the contract");
    }

    [GameDataFact]
    public void TheWalkerFindsTheImageCountsOfImagesMd()
    {
        // images.md 1: allpac3 has 3,764 images (65 top-level + 3,699 nested), allpac4 37, parts 24, each ft_*.mrg 65, allscr.mrg 1
        using (var allpac3 = GameImage.Open("allpac3"))
        {
            int top = 0, nested = 0;
            for (int e = 0; e < allpac3.Count; e++)
            {
                PictureWalker.Walk(allpac3.OpenContainer(e), (path, _) =>
                {
                    if (path.Length == 0)
                    {
                        top++;
                    }
                    else
                    {
                        nested++;
                    }
                });
            }

            Assert.Equal((65, 3699), (top, nested));
        }

        foreach (var (name, want) in new[] { ("allpac4", 37), ("parts", 24) })
        {
            using var archive = GameImage.Open(name);
            int n = 0;
            for (int e = 0; e < archive.Count; e++)
            {
                PictureWalker.Walk(archive.OpenContainer(e), (_, _) => n++);
            }

            Assert.Equal(want, n);
        }

        foreach (var (name, want) in new[] { ("ft_en.mrg", 65), ("ft_ja.mrg", 65), ("ft_ko.mrg", 65), ("ft_zc.mrg", 65), ("ft_zt.mrg", 65), ("allscr.mrg", 1) })
        {
            using var mrg = StandaloneMrg.Open(GameImage.Path(name));
            int n = 0;
            for (int s = 0; s < mrg.Container.Count; s++)
            {
                if (mrg.Container.IsNested(s))
                {
                    PictureWalker.Walk(mrg.Container.Child(s), (_, _) => n++);
                }
            }

            Assert.Equal(want, n);
        }
    }
}

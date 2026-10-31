using System.Collections.Concurrent;
using Tsuki.Data.Catalog;
using Tsuki.Data.Images;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;

namespace Tsuki.Data;

/// <summary>
/// <see cref="IPictureLayoutSource"/> over the game folder (package K2): the draw description of a catalog picture, from its image-table record and the
/// tile table of its image files, without decoding a pixel. The files of a picture are the catalog's <c>name0</c> (the body) and <c>name1</c> (the face
/// drawn over it), found by the NAM search; the displayed part of a file is the record's source rectangle for the language (the logical size, or the
/// language band of a stacked picture). A tile contributes the part of its inner rectangle that lies inside that rectangle, with texture coordinates
/// into the stored tile (the border pixels around the inner rectangle are the filter apron, ARCHITECTURE 4.3).
/// </summary>
/// <remarks>Layouts are immutable and cached per picture and language; the class is safe to call from several threads.</remarks>
public sealed class GamePictureLayouts : IPictureLayoutSource
{
    private readonly GameFolderSource _source;
    private readonly ConcurrentDictionary<(int Pic, int Language), PictureLayout?> _cache = new();

    /// <summary>Creates the source over a game folder.</summary>
    public GamePictureLayouts(GameFolderSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <summary>The layout of picture <paramref name="pic"/> for <paramref name="language"/>, or null when the catalog record is empty or a file of it is in no archive.</summary>
    /// <inheritdoc/>
    public PictureLayout? Get(int pic, int language) => _cache.GetOrAdd((pic, language), static (key, self) => self.Build(key.Pic, key.Language), this);

    private PictureLayout? Build(int pic, int language)
    {
        GameCatalog catalog = _source.Catalog;
        if ((uint)pic >= (uint)catalog.Images.Count)
        {
            return null;
        }

        CatalogEntry entry = catalog.Images[pic];
        if (string.IsNullOrEmpty(entry.Name0) || entry.Width <= 0 || entry.Height <= 0)
        {
            return null;
        }

        PixelRect band = entry.SourceRect(language);
        var files = new List<PictureFileLayout>(2);
        foreach ((string? name, int dx, int dy) in new[] { (entry.Name0, 0, 0), (entry.Name1, entry.LayerX, entry.LayerY) })
        {
            if (name is null)
            {
                continue;
            }

            if (catalog.NameIndex.Find(name) is not { } id)
            {
                return null;        // no archive holds the file: the picture cannot be drawn (the catalog names a few sprite variants that the data does not have)
            }

            files.Add(Layout(id, band, dx, dy));
        }

        return new PictureLayout(band.Width, band.Height, files);
    }

    private PictureFileLayout Layout(ImageId id, PixelRect band, int offsetX, int offsetY)
    {
        ImageHeader h = _source.OpenImage((int)id.Archive, id.Index).Header;
        var tiles = new List<TileQuad>(h.Tiles);
        for (int t = 0; t < h.Tiles; t++)
        {
            if (!h.IsLive(t))
            {
                continue;
            }

            (int sx, int sy, int w, int ht, int x, int y) = h.TileWindow(t);
            int x0 = Math.Max(x, band.X);
            int y0 = Math.Max(y, band.Y);
            int x1 = Math.Min(x + w, band.X + band.Width);
            int y1 = Math.Min(y + ht, band.Y + band.Height);
            if (x1 <= x0 || y1 <= y0)
            {
                continue;
            }

            tiles.Add(new TileQuad(
                t, x0 - band.X, y0 - band.Y, x1 - band.X, y1 - band.Y,
                (sx + (x0 - x)) / (float)h.TileWidth, (sy + (y0 - y)) / (float)h.TileHeight,
                (sx + (x1 - x)) / (float)h.TileWidth, (sy + (y1 - y)) / (float)h.TileHeight));
        }

        return new PictureFileLayout((int)id.Archive, id.Index, offsetX, offsetY, tiles);
    }
}

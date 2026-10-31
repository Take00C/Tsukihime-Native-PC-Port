// A synchronous texture provider over IGameData (package K7; the seam between a display list and the texture cache): it makes every texture a list
// names resident, decoding picture tiles, UI sheets (a tile or the whole sheet, the convention of the choice, noise and focus-line quads) and glyph pages
// on demand. The runtime path with decode workers (DecodeWorkers / UploadScheduler) fills the same cache with the same keys; this class is the simple
// path for tools, screenshots and tests, and documents what each TextureRef means in game data.
using Tsuki.Engine.Contracts;

namespace Tsuki.Render.Textures;

/// <summary>Resolves the <see cref="TextureRef"/>s of a display list to pixels and uploads them into a <see cref="TextureCache"/>.</summary>
public sealed class GameTextureProvider(IGameData data, TextureCache cache)
{
    /// <summary>The tile number that selects the whole sheet of a UI picture (the sheet's tiles assembled into one texture).</summary>
    public const int WholeSheet = -1;

    private readonly Dictionary<(int Archive, int Entry), Picture?> _pictures = [];
    private readonly Dictionary<(UiSet, Language, int), Picture> _sheets = [];
    private readonly Dictionary<(Language, int), FontPage> _fonts = [];
    private readonly Queue<(int, int)> _pictureOrder = new();

    /// <summary>Keys that could not be resolved (a missing picture, a movie or capture kind the provider does not serve).</summary>
    public HashSet<TextureRef> Missing { get; } = [];

    /// <summary>The largest number of decoded pictures kept for reuse (a full-screen picture is 8 MB; the biggest are 100 MB).</summary>
    public int PictureWindow { get; init; } = 6;

    /// <summary>Uploads every textured quad's texture that is not resident yet; returns the number of uploads.</summary>
    public int Upload(DisplayList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        int n = 0;
        foreach (ref readonly DrawCmd c in list.Commands)
        {
            if (c.Kind == DrawKind.TexturedQuad && Ensure(c.Texture))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>Makes <paramref name="key"/> resident; true when it had to be uploaded.</summary>
    public bool Ensure(in TextureRef key)
    {
        TextureRef k = key;
        // thumbnails and the menus' composed pictures (package K33) come from the UI, not from the game data: the hosts that draw menus upload them themselves
        if (key.Kind is TextureKind.None or TextureKind.Capture or TextureKind.MovieFrame or TextureKind.Thumbnail or TextureKind.UiComposed || cache.Contains(key) || Missing.Contains(key))
        {
            return false;
        }

        switch (key.Kind)
        {
            case TextureKind.PictureTile:
            {
                Picture? pic = PictureOf(key.A, key.B);
                PictureTile? tile = pic?.Tiles.FirstOrDefault(t => t.Index == k.C);
                if (tile is null || tile.Flags == TileFlags.Empty)
                {
                    Missing.Add(key);
                    return false;
                }

                cache.Upload(key, tile.StoredWidth, tile.StoredHeight, tile.Rgba.Span);       // the cache grades the tile when the key carries a grade
                return true;
            }

            case TextureKind.UiTile:
            {
                var set = (UiSet)key.A;
                var lang = (Language)key.B;
                if (!_sheets.TryGetValue((set, lang, key.C), out Picture? sheet))
                {
                    sheet = data.UiSheet(set, lang, key.C);
                    _sheets[(set, lang, key.C)] = sheet;
                }

                if (key.D == WholeSheet)
                {
                    cache.Upload(key, sheet.Width, sheet.Height, Assemble(sheet));
                    return true;
                }

                PictureTile? tile = sheet.Tiles.FirstOrDefault(t => t.Index == k.D);
                if (tile is null || tile.Flags == TileFlags.Empty)
                {
                    Missing.Add(key);
                    return false;
                }

                cache.Upload(key, tile.StoredWidth, tile.StoredHeight, tile.Rgba.Span);
                return true;
            }

            case TextureKind.GlyphPage:
            {
                var lang = (Language)key.A;
                if (!_fonts.TryGetValue((lang, key.B), out FontPage? page))
                {
                    page = data.FontPage(lang, key.B);
                    _fonts[(lang, key.B)] = page;
                }

                cache.Upload(key, page.Width, page.Height, page.Rgba.Span);
                return true;
            }

            default:
                Missing.Add(key);
                return false;
        }
    }

    /// <summary>
    /// The displayed picture as one RGBA image: each tile's <see cref="PictureTile.Source"/> window is copied to its displayed position
    /// (<see cref="PictureTile.X"/>, <see cref="PictureTile.Y"/>); empty tiles stay transparent.
    /// </summary>
    public static byte[] Assemble(Picture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        byte[] rgba = new byte[picture.Width * picture.Height * 4];
        foreach (PictureTile t in picture.Tiles)
        {
            if (t.Flags == TileFlags.Empty || t.Rgba.IsEmpty)
            {
                continue;
            }

            ReadOnlySpan<byte> src = t.Rgba.Span;
            for (int row = 0; row < t.Source.Height; row++)
            {
                int dy = t.Y + row;
                if ((uint)dy >= (uint)picture.Height)
                {
                    continue;
                }

                int w = Math.Min(t.Source.Width, picture.Width - t.X);
                if (w <= 0)
                {
                    continue;
                }

                src.Slice((((t.Source.Y + row) * t.StoredWidth) + t.Source.X) * 4, w * 4).CopyTo(rgba.AsSpan(((dy * picture.Width) + t.X) * 4));
            }
        }

        return rgba;
    }

    private Picture? PictureOf(int archive, int entry)
    {
        if (_pictures.TryGetValue((archive, entry), out Picture? pic))
        {
            return pic;
        }

        try
        {
            pic = data.Picture(archive, entry);
        }
        catch (ArgumentOutOfRangeException)
        {
            pic = null;
        }

        _pictures[(archive, entry)] = pic;
        _pictureOrder.Enqueue((archive, entry));
        while (_pictureOrder.Count > PictureWindow)
        {
            _pictures.Remove(_pictureOrder.Dequeue());
        }

        return pic;
    }
}

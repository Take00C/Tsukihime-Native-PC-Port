using Tsuki.App.Play;
using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;
using Tsuki.Media.Movie;
using Tsuki.Platform;
using Tsuki.Render.Gl;
using Tsuki.Render.Grade;
using Tsuki.Render.Textures;
using Tsuki.Tests.Data.Source;
using Tsuki.Ui;

namespace Tsuki.Tests.Data.App;

/// <summary>
/// The texture provider of the app on the GPU (a hidden window, the user's game): every kind of <see cref="TextureRef"/> a display list names becomes a resident
/// texture of the right size, with the colour grade applied through the cache's grader (package K6).
/// </summary>
[Collection(SdlWindowCollection.Name)]
public sealed class TextureFeederTests
{
    private static GameFolderSource Source => SharedSource.Source;

    private static DisplayList ListOf(params TextureRef[] keys)
    {
        var list = new DisplayList(16);
        list.Reset(1);
        foreach (TextureRef key in keys)
        {
            list.Add(new DrawCmd
            {
                Kind = DrawKind.TexturedQuad,
                Texture = key,
                V0 = new Vertex(0, 0, 0, 0, 0xFFFFFFFF),
                V1 = new Vertex(10, 0, 1, 0, 0xFFFFFFFF),
                V2 = new Vertex(0, 10, 0, 1, 0xFFFFFFFF),
                V3 = new Vertex(10, 10, 1, 1, 0xFFFFFFFF),
                BlendState = 1,
            });
        }

        list.Seal();
        return list;
    }

    [GameDataFact]
    public void EveryKindOfTextureResolvesAndTheWholeSheetQuadGetsTheWholeSheet()
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("feeder", 64, 64, Hidden: true, Resizable: false, SwapInterval: 0));
        GlDevice device = GlDevice.Create(window.GetProcAddress);
        using var cache = new TextureCache(device.Gl, 512L << 20) { Grader = TileGrader.FromEboot(Source.Tables.Reader) };
        using var feeder = new TextureFeeder(Source, cache);

        // a picture's first live tile, a UI sheet tile and the whole sheet, a glyph page, a parts picture
        int pic = Source.Eboot.Pictures.Lookup("IMG1696");
        Assert.True(pic >= 0);
        PictureLayout layout = new GamePictureLayouts(Source).Get(pic, (int)Language.En)!;
        PictureFileLayout file = layout.Files[0];
        TextureRef tile = TextureRef.ForPictureTile(file.Archive, file.Entry, file.Tiles[0].Tile);
        TextureRef sheetTile = TextureRef.ForUiTile(UiSet.DispParts, Language.En, 10, 0);
        TextureRef wholeSheet = TextureRef.ForUiTile(UiSet.DispParts, Language.En, 10, -1);
        TextureRef glyphs = TextureRef.ForGlyphPage(Language.En, 1);
        TextureRef parts = TextureRef.ForPictureTile(UiPictureArchive.Parts, 8 + 1, 0);
        TextureRef thumb = TextureRef.ForThumbnail(7, 1);
        TextureRef movie = TextureRef.ForMovieFrame(5);

        byte[] thumbPixels = new byte[4 * 3 * 4];
        for (int i = 0; i < thumbPixels.Length; i += 4)
        {
            thumbPixels[i] = 10;
            thumbPixels[i + 1] = 20;
            thumbPixels[i + 2] = 30;
            thumbPixels[i + 3] = 255;
        }

        feeder.Thumbnails = slot => slot == 7 ? new UiThumbnail(4, 3, 1, thumbPixels) : null;
        var frame = new VideoFrame(8, 4) { Index = 1 };
        Array.Fill(frame.Y, (byte)235);
        Array.Fill(frame.U, (byte)128);
        Array.Fill(frame.V, (byte)128);
        feeder.MovieFrames = slot => slot == 5 ? frame : null;

        int missing = feeder.EnsureResident(ListOf(tile, sheetTile, wholeSheet, glyphs, parts, thumb, movie), TimeSpan.FromSeconds(30));
        Assert.Equal(0, missing);
        foreach (TextureRef key in new[] { tile, sheetTile, wholeSheet, glyphs, parts, thumb, movie })
        {
            Assert.True(cache.Contains(key), $"{key} is not resident");
        }

        Picture sheet = Source.UiSheet(UiSet.DispParts, Language.En, 10);
        Assert.Equal(4 * sheet.Width * sheet.Height, cache.ReadBack(wholeSheet).Length);          // the whole sheet, not one tile
        Assert.Equal(4 * 1024 * 1024, cache.ReadBack(glyphs).Length);
        byte[] movieBack = cache.ReadBack(movie);
        Assert.Equal(4 * 8 * 4, movieBack.Length);
        Assert.InRange(movieBack[0], 254, 255);                                                   // Y 235 is white
        Assert.Equal(thumbPixels, cache.ReadBack(thumb));
        Assert.True(feeder.Stats.Requested >= 5);
        Assert.Null(feeder.Fault);
    }

    [GameDataFact]
    public void AGradedTileIsBuiltRecolouredAndCachedBesideThePlainOne()
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("feeder", 64, 64, Hidden: true, Resizable: false, SwapInterval: 0));
        GlDevice device = GlDevice.Create(window.GetProcAddress);
        using var cache = new TextureCache(device.Gl, 512L << 20) { Grader = TileGrader.FromEboot(Source.Tables.Reader) };
        using var feeder = new TextureFeeder(Source, cache);

        int pic = Source.Eboot.Pictures.Lookup("IMG1696");
        PictureFileLayout file = new GamePictureLayouts(Source).Get(pic, (int)Language.En)!.Files[0];
        int t = file.Tiles[0].Tile;
        int grey = GradeKeys.Intern(ColourGrade.From(mode: 2, brightness: 0, contrast: 0, rgb: 0, globalColourMode: 2));
        Assert.NotEqual(0, grey);
        TextureRef plain = TextureRef.ForPictureTile(file.Archive, file.Entry, t);
        TextureRef graded = TextureRef.ForPictureTile(file.Archive, file.Entry, t, grey);

        Assert.Equal(0, feeder.EnsureResident(ListOf(plain, graded), TimeSpan.FromSeconds(30)));
        byte[] a = cache.ReadBack(plain);
        byte[] b = cache.ReadBack(graded);
        Assert.Equal(a.Length, b.Length);
        Assert.NotEqual(a, b);
        int greys = 0;
        for (int i = 0; i < b.Length; i += 4)
        {
            if (b[i + 3] != 0 && b[i] == b[i + 1] && b[i + 1] == b[i + 2])
            {
                greys++;
            }
        }

        Assert.True(greys > b.Length / 8, "the grey grade should leave grey pixels");
    }
}

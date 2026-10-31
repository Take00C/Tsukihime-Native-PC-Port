namespace Tsuki.Engine.Stage;

/// <summary>One tile quad of a picture layer: a rectangle in picture pixels and the texture rectangle that fills it.</summary>
/// <param name="Tile">Tile number in the picture file (row-major).</param>
/// <param name="X0">Left edge in picture pixels (of the displayed band).</param>
/// <param name="Y0">Top edge.</param>
/// <param name="X1">Right edge.</param>
/// <param name="Y1">Bottom edge.</param>
/// <param name="U0">Texture coordinate of the left edge.</param>
/// <param name="V0">Texture coordinate of the top edge.</param>
/// <param name="U1">Texture coordinate of the right edge.</param>
/// <param name="V1">Texture coordinate of the bottom edge.</param>
public readonly record struct TileQuad(int Tile, float X0, float Y0, float X1, float Y1, float U0, float V0, float U1, float V1);

/// <summary>One picture file of a catalog picture: the body, or the face drawn over it (catalog name1).</summary>
/// <param name="Archive">Image archive id (archives_ui_movies.md 2.1: 0 allpac0 .. 5 allpac4).</param>
/// <param name="Entry">Entry in that archive.</param>
/// <param name="OffsetX">Position of this file relative to the picture's top-left (the catalog layer position; 0 in every record).</param>
/// <param name="OffsetY">See <paramref name="OffsetX"/>.</param>
/// <param name="Tiles">The tiles that cover the displayed band of the file.</param>
public sealed record PictureFileLayout(int Archive, int Entry, float OffsetX, float OffsetY, IReadOnlyList<TileQuad> Tiles);

/// <summary>
/// The draw description of a catalog picture: the files to draw and their tile geometry. The picture's texel space is that of the displayed band
/// (for a picture with language bands, the band of the current language), with the catalog anchor in the same space.
/// </summary>
/// <param name="Width">Displayed width in picture pixels.</param>
/// <param name="Height">Displayed height.</param>
/// <param name="Files">Body first, then the face.</param>
public sealed record PictureLayout(int Width, int Height, IReadOnlyList<PictureFileLayout> Files);

/// <summary>Resolves catalog pictures to their files and tile geometry (the data layer implements it over the image headers and the catalog).</summary>
public interface IPictureLayoutSource
{
    /// <summary>The layout of picture <paramref name="pic"/> for <paramref name="language"/> (0 ja, 1 en, 2 zc, 3 zt), or null when it has none.</summary>
    PictureLayout? Get(int pic, int language);
}

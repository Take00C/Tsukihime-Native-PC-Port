using Tsuki.Engine.Contracts;

namespace Tsuki.Ui;

/// <summary>
/// A picture the menus build at run time from the player's own sheets (package K33): straight-alpha RGBA8, top-down. It lives in memory only: it is the
/// player's art rearranged, so it is never written to disk and never part of the repository.
/// </summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Rgba">Width x Height x 4 bytes.</param>
public sealed record UiComposedPicture(int Width, int Height, ReadOnlyMemory<byte> Rgba);

/// <summary>The ids of the composed pictures (<see cref="TextureRef.ForUiComposed"/>, <c>A</c>).</summary>
public static class UiComposedId
{
    /// <summary>EXIT on the title in its normal look (white letters), a 296 x 72 cell like the other items (package K33).</summary>
    public const int TitleExit = 0;

    /// <summary>EXIT on the title under the cursor (cyan letters).</summary>
    public const int TitleExitCursor = 1;
}

/// <summary>Helpers for the composed pictures.</summary>
public static class UiComposed
{
    /// <summary>
    /// The displayed picture as one RGBA image: each tile's <see cref="PictureTile.Source"/> window is copied to its displayed position; empty tiles stay
    /// transparent (the same assembly the texture providers do for a whole-sheet quad).
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
}

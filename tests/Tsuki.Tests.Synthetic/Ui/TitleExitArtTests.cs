using System.Collections.Immutable;
using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>
/// The EXIT cell composed from the title menu sheet (package K33), on a made-up sheet with the real geometry: five words of block letters on rows 28..46, 14 px
/// wide and 11 px apart, white (column 0) or cyan (column 1), with a distance-based light-blue glow; the Y of GALLERY is a V on a stem.
/// </summary>
public sealed class TitleExitArtTests
{
    private const int W = TitleExitArt.SheetWidth;
    private const int H = TitleExitArt.SheetHeight;
    private const int LetterW = 14;
    private const int Gap = 11;
    private static readonly int[] Counts = [7, 8, 6, 7, 8];
    private static readonly int[] Tops = [24, 112, 200, 288, 376];

    /// <summary>A sheet with the real cell layout; <paramref name="rowShift"/> moves one word's letters down, <paramref name="blankWord"/> leaves one cell empty.</summary>
    internal static Picture Sheet(int width = W, int height = H, int rowShift = 0, int shiftedWord = -1, int blankWord = -1)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int column = 0; column < 3; column++)
        {
            for (int word = 0; word < 5; word++)
            {
                if (word == blankWord)
                {
                    continue;
                }

                int x0 = 24 + (312 * column);
                int y0 = Tops[word] + (word == shiftedWord ? rowShift : 0);
                if (y0 + 72 > height || x0 + 296 > width)
                {
                    continue;
                }

                DrawWord(rgba, width, x0, y0, Counts[word], column == 1, word == 3);
            }
        }

        return new Picture(width, height, [new PictureTile(0, TileFlags.MayBeTransparent, width, height, new PixelRect(0, 0, width, height), 0, 0, rgba)]);
    }

    private static void DrawWord(byte[] rgba, int stride, int x0, int y0, int letters, bool cyan, bool lastIsY)
    {
        int total = (letters * LetterW) + ((letters - 1) * Gap);
        int left = 148 - (total / 2);
        var ink = new bool[72, 296];
        for (int k = 0; k < letters; k++)
        {
            int l = left + (k * (LetterW + Gap));
            if (lastIsY && k == letters - 1)
            {
                // a V: two 2 px diagonals from the top corners to the middle row, then a 2 px stem down to the baseline
                for (int y = 28; y <= 37; y++)
                {
                    int t = y - 28;                                           // 0..9 -> the arms close in by 6 over 9 rows
                    int xl = l + ((t * 6) / 9);
                    int xr = l + LetterW - 1 - ((t * 6) / 9);
                    ink[y, xl] = ink[y, xl + 1] = true;
                    ink[y, xr] = ink[y, xr - 1] = true;
                }

                for (int y = 37; y <= 46; y++)
                {
                    ink[y, l + 6] = ink[y, l + 7] = true;
                }
            }
            else
            {
                for (int y = 28; y <= 46; y++)
                {
                    for (int x = l; x < l + LetterW; x++)
                    {
                        ink[y, x] = true;
                    }
                }
            }
        }

        // ink, then a glow whose alpha falls 40 -> 0 over 20 px of distance to the nearest ink pixel
        for (int y = 0; y < 72; y++)
        {
            for (int x = 0; x < 296; x++)
            {
                int o = (((y0 + y) * stride) + x0 + x) * 4;
                if (ink[y, x])
                {
                    rgba[o] = (byte)(cyan ? 17 : 255);
                    rgba[o + 1] = (byte)(cyan ? 222 : 255);
                    rgba[o + 2] = (byte)(cyan ? 248 : 255);
                    rgba[o + 3] = 230;
                    continue;
                }

                double best = double.MaxValue;
                for (int yy = Math.Max(0, y - 21); yy <= Math.Min(71, y + 21); yy++)
                {
                    for (int xx = Math.Max(0, x - 21); xx <= Math.Min(295, x + 21); xx++)
                    {
                        if (ink[yy, xx])
                        {
                            best = Math.Min(best, Math.Sqrt(((xx - x) * (xx - x)) + ((yy - y) * (yy - y))));
                        }
                    }
                }

                int a = (int)Math.Round(Math.Max(0, 40 - (2 * best)));
                if (a > 0)
                {
                    rgba[o] = 192;
                    rgba[o + 1] = 238;
                    rgba[o + 2] = 254;
                    rgba[o + 3] = (byte)a;
                }
            }
        }
    }

    private static byte Alpha(UiComposedPicture p, int x, int y) => p.Rgba.Span[(((y * p.Width) + x) * 4) + 3];

    private static (byte R, byte G, byte B) Rgb(UiComposedPicture p, int x, int y)
    {
        ReadOnlySpan<byte> s = p.Rgba.Span.Slice(((y * p.Width) + x) * 4, 3);
        return (s[0], s[1], s[2]);
    }

    [Fact]
    public void TheLettersOfTheSheetAreFoundByTheirAlphaColumns()
    {
        Picture sheet = Sheet();
        byte[] rgba = UiComposed.Assemble(sheet);
        for (int word = 0; word < 5; word++)
        {
            byte[] cell = new byte[296 * 72 * 4];
            for (int y = 0; y < 72; y++)
            {
                rgba.AsSpan((((Tops[word] + y) * W) + 24) * 4, 296 * 4).CopyTo(cell.AsSpan(y * 296 * 4));
            }

            LetterSpan[] letters = TitleExitArt.Segment(cell, out int top, out int bottom);
            Assert.Equal(Counts[word], letters.Length);
            Assert.Equal((28, 46), (top, bottom));
            Assert.All(letters, l => Assert.Equal(LetterW, l.Width));
            for (int i = 1; i < letters.Length; i++)
            {
                Assert.Equal(Gap, letters[i].Left - letters[i - 1].Right - 1);
            }
        }
    }

    [Fact]
    public void ExitIsFourLettersOnTheRowsAndAtTheSpacingOfTheOtherWordsCentredLikeThem()
    {
        (UiComposedPicture normal, UiComposedPicture cursor) = Assert.NotNull(TitleExitArt.Compose(Sheet()));
        foreach (UiComposedPicture p in new[] { normal, cursor })
        {
            Assert.Equal((TitleExitArt.CellWidth, TitleExitArt.CellHeight), (p.Width, p.Height));
            LetterSpan[] letters = TitleExitArt.Segment(p.Rgba.Span, out int top, out int bottom);
            Assert.Equal(4, letters.Length);
            Assert.Equal((28, 46), (top, bottom));
            Assert.All(letters, l => Assert.Equal(LetterW, l.Width));
            for (int i = 1; i < 4; i++)
            {
                Assert.Equal(Gap, letters[i].Left - letters[i - 1].Right - 1);
            }

            float centre = (letters[0].Left + letters[^1].Right) / 2f;
            Assert.InRange(centre, 147f, 149f);
        }

        byte[]? state = TitleExitArt.ComposeState(UiComposed.Assemble(Sheet()), 0, out ExitLayout layout);
        Assert.NotNull(state);
        Assert.Equal(Gap, layout.Gap);
        Assert.Equal((28, 46), (layout.RowTop, layout.RowBottom));
        Assert.Equal(4, layout.Letters.Length);
        Assert.Contains("[", layout.ESource, StringComparison.Ordinal);
    }

    [Fact]
    public void TheXIsTheVOfTheYAndItsPointMirrorAboutTheLetterCentre()
    {
        (UiComposedPicture normal, _) = Assert.NotNull(TitleExitArt.Compose(Sheet()));
        LetterSpan[] letters = TitleExitArt.Segment(normal.Rgba.Span, out _, out _);
        LetterSpan x = letters[1];
        // the upper half is a V (the arms meet at the middle row), the lower half an inverted V: every pixel equals its point mirror about (centre column, row 37)
        for (int y = 27; y <= 47; y++)
        {
            for (int c = x.Left - 1; c <= x.Right + 1; c++)
            {
                Assert.Equal(Alpha(normal, c, y), Alpha(normal, x.Left + x.Right - c, 74 - y));
            }
        }

        // the arms are open at the top and the bottom, closed in the middle
        Assert.True(Alpha(normal, x.Left, 28) >= TitleExitArt.Ink && Alpha(normal, x.Right, 28) >= TitleExitArt.Ink, "top corners are ink");
        Assert.True(Alpha(normal, x.Left, 46) >= TitleExitArt.Ink && Alpha(normal, x.Right, 46) >= TitleExitArt.Ink, "bottom corners are ink");
        Assert.True(Alpha(normal, x.Left + 7, 37) >= TitleExitArt.Ink, "the crossing is ink");
        Assert.True(Alpha(normal, x.Left, 37) < TitleExitArt.Ink && Alpha(normal, x.Right, 37) < TitleExitArt.Ink, "the middle row is open at the sides");
        Assert.True(Alpha(normal, x.Left + 7, 28) < TitleExitArt.Ink, "the top row is open in the middle");
    }

    [Fact]
    public void TheCursorLookIsCyanAndTheNormalLookWhite()
    {
        (UiComposedPicture normal, UiComposedPicture cursor) = Assert.NotNull(TitleExitArt.Compose(Sheet()));
        LetterSpan[] letters = TitleExitArt.Segment(normal.Rgba.Span, out _, out _);
        (int x, int y) = (letters[0].Left + 2, 37);
        Assert.Equal((255, 255, 255), ((int, int, int))Rgb(normal, x, y));
        Assert.Equal((17, 222, 248), ((int, int, int))Rgb(cursor, x, y));
        Assert.Equal(230, Alpha(normal, x, y));
        Assert.Equal(230, Alpha(cursor, x, y));
    }

    [Fact]
    public void TheGlowAroundExitIsTheSheetsOwnFallsOffWithoutASeamAndEndsWhereTheArtsDoes()
    {
        (UiComposedPicture normal, _) = Assert.NotNull(TitleExitArt.Compose(Sheet()));
        LetterSpan[] letters = TitleExitArt.Segment(normal.Rgba.Span, out _, out _);
        int l = letters[0].Left;
        int r = letters[^1].Right;
        // left of E and right of T, along the middle row: the alpha never rises away from the letter, has the art's colour, and is gone by 22 px
        for (int side = 0; side < 2; side++)
        {
            int previous = 255;
            for (int d = 2; d <= 24; d++)
            {
                int x = side == 0 ? l - d : r + d;
                int a = Alpha(normal, x, 37);
                Assert.True(a <= previous + 1, $"the glow rises at {d} px from the letters: {previous} -> {a}");
                if (a > 0)
                {
                    (byte cr, byte cg, byte cb) = Rgb(normal, x, 37);
                    Assert.InRange(cr, 189, 195);
                    Assert.InRange(cg, 235, 241);
                    Assert.InRange(cb, 251, 255);
                }

                previous = a;
            }

            Assert.InRange(Alpha(normal, side == 0 ? l - 5 : r + 5, 37), 24, 34);          // the art has 40 - 2 d = 30 there
            Assert.Equal(0, Alpha(normal, side == 0 ? l - 24 : r + 24, 37));
        }

        // between two letters the glow is the art's gap glow: the midpoint of an 11 px gap is 6 px from either letter (40 - 12 = 28 in the art)
        for (int i = 1; i < 4; i++)
        {
            int mid = (letters[i - 1].Right + letters[i].Left) / 2;
            Assert.InRange(Alpha(normal, mid, 37), 22, 34);
        }

        // above and below the word: glow, fading out
        Assert.InRange(Alpha(normal, l + 7, 23), 24, 34);
        Assert.True(Alpha(normal, l + 7, 10) < Alpha(normal, l + 7, 20));
        Assert.Equal(0, Alpha(normal, l + 7, 2));
    }

    [Fact]
    public void ASheetTheComposerDoesNotRecogniseGivesNothingSoTheTitleFallsBack()
    {
        Assert.Null(TitleExitArt.Compose(Sheet(width: 1000)));                                  // another size
        Assert.Null(TitleExitArt.Compose(Sheet(blankWord: 4)));                                 // no CONTINUE
        Assert.Null(TitleExitArt.Compose(Sheet(blankWord: 3)));                                 // no GALLERY
        Assert.Null(TitleExitArt.Compose(Sheet(rowShift: 2, shiftedWord: 3)));                  // GALLERY on other rows than CONTINUE
        Assert.Null(TitleExitArt.Compose(UiTestKit.Data().UiSheet(UiSet.TitleParts, Language.En, 0)));    // the test kit's blank sheet
        Assert.NotNull(TitleExitArt.Compose(Sheet(rowShift: 1, shiftedWord: 2)));              // CONFIG a row off is the real sheet's case: its I is not taken
        Assert.Null(TitleExitArt.ComposeState(new byte[16], 0, out _));
    }

    [Fact]
    public void TheAssetsBuildBothLooksOnceAndDropThemWithTheLanguage()
    {
        var assets = new UiAssets(UiTestKit.Data(Sheet()), Language.En);
        UiComposedPicture? a = assets.Composed(UiComposedId.TitleExit);
        UiComposedPicture? b = assets.Composed(UiComposedId.TitleExitCursor);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Same(a, assets.Composed(UiComposedId.TitleExit));
        Assert.Same(a, assets.Composed(UiComposedId.TitleExit, Language.En));
        assets.SetLanguage(Language.Ja);
        UiComposedPicture? c = assets.Composed(UiComposedId.TitleExit);
        Assert.NotNull(c);
        Assert.NotSame(a, c);
        Assert.Null(new UiAssets(UiTestKit.Data(), Language.En).Composed(UiComposedId.TitleExit));
    }

    [Fact]
    public void ThePainterDrawsAComposedPictureAsOneQuadUnderItsAlphaAndClipAndNothingForAMissingOne()
    {
        var assets = new UiAssets(UiTestKit.Data(Sheet()), Language.Zt);
        var painter = new UiPainter(assets);
        var list = new DisplayList();
        list.Reset(0);
        painter.Begin(list);
        using (painter.WithAlpha(0.5f))
        {
            Assert.True(painter.Composed(UiComposedId.TitleExitCursor, new RectF(0, 0, 296, 72), new RectF(816, 700, 296, 72), 0.5f));
        }

        DrawCmd c = Assert.Single(list.Commands.ToArray());
        Assert.Equal(TextureRef.ForUiComposed(UiComposedId.TitleExitCursor, Language.Zt), c.Texture);
        Assert.Equal((816f, 700f, 1112f, 772f), (c.V0.X, c.V0.Y, c.V3.X, c.V3.Y));
        Assert.Equal((0f, 0f, 1f, 1f), (c.V0.U, c.V0.V, c.V3.U, c.V3.V));
        Assert.Equal(64u, c.V0.Rgba >> 24);
        Assert.Equal(DrawKind.TexturedQuad, c.Kind);

        var blank = new UiPainter(new UiAssets(UiTestKit.Data(), Language.En));
        blank.Begin(new DisplayList());
        Assert.False(blank.Composed(UiComposedId.TitleExit, new RectF(0, 0, 296, 72), new RectF(0, 0, 296, 72)));
        Assert.Equal(0, blank.QuadCount);
    }

    [Fact]
    public void TheTitleDrawsExitAsTheComposedCellInItsSlotAndSwitchesToTheCursorLookUnderTheCursor()
    {
        var services = new PreviewServices(Language.Ja);
        var root = new UiRoot(UiTestKit.Data(Sheet()), services);
        root.Context.TitleIntroShown = true;
        var title = root.Push(new TitleScreen());
        for (int i = 0; i < 400 && !title.Idle; i++)
        {
            UiTestKit.Run(root, 1);
        }

        UiTestKit.Run(root, 200);
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        Assert.DoesNotContain(cmds, c => c.Texture.Kind == TextureKind.GlyphPage);
        DrawCmd exit = Assert.Single(cmds, c => c.Texture.Kind == TextureKind.UiComposed);
        Assert.Equal(TextureRef.ForUiComposed(UiComposedId.TitleExit, Language.Ja), exit.Texture);
        RectF slot = TitleScreen.ItemRect(5, 6);
        Assert.Equal((slot.X, slot.Y, slot.Right, slot.Bottom), (exit.V0.X, exit.V0.Y, exit.V3.X, exit.V3.Y));
        Assert.True((exit.V0.Rgba >> 24) > 250, "fully up");

        UiTestKit.Press(root, PadButtons.Up);
        UiTestKit.Run(root, 5);
        Assert.Equal(TitleItem.Exit, title.Cursor);
        cmds = UiTestKit.Draw(root).Commands.ToArray();
        exit = Assert.Single(cmds, c => c.Texture.Kind == TextureKind.UiComposed);
        Assert.Equal(TextureRef.ForUiComposed(UiComposedId.TitleExitCursor, Language.Ja), exit.Texture);

        // the idle fade takes the composed cell down with the menu, and any input brings it back
        UiTestKit.Run(root, TitleScreen.IdleFrames + 90);
        Assert.True(title.IdleHidden);
        Assert.DoesNotContain(UiTestKit.Draw(root).Commands.ToArray(), c => c.Texture.Kind == TextureKind.UiComposed && (c.V0.Rgba >> 24) > 3);
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Run(root, 2);
        Assert.Contains(UiTestKit.Draw(root).Commands.ToArray(), c => c.Texture.Kind == TextureKind.UiComposed && (c.V0.Rgba >> 24) > 250);
    }
}

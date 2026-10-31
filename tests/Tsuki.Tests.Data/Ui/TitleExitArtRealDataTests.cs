using Tsuki.Engine.Contracts;
using Tsuki.Tests.Data.Source;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Data.Ui;

/// <summary>
/// The EXIT cell composed from the user's own TITLE_PARTS sheet (package K33): four letters on the rows and at the spacing of the other items, cut from them
/// without a change, the X the point mirror of the Y's upper half, the colours and the glow those of the neighbours, in every language and both looks.
/// </summary>
public sealed class TitleExitArtRealDataTests
{
    private static readonly int[] CellTop = [24, 112, 200, 288, 376];          // NEW GAME, LOAD GAME, CONFIG, GALLERY, CONTINUE

    private static byte[] Sheet(Language lang) => UiComposed.Assemble(SharedSource.Source.UiSheet(UiSet.TitleParts, lang, UiSheets.TitleMenu.Child));

    private static byte[] Cell(byte[] sheet, int column, int word)
    {
        byte[] cell = new byte[296 * 72 * 4];
        for (int y = 0; y < 72; y++)
        {
            sheet.AsSpan((((CellTop[word] + y) * 968) + 24 + (312 * column)) * 4, 296 * 4).CopyTo(cell.AsSpan(y * 296 * 4));
        }

        return cell;
    }

    private static int A(byte[] cell, int x, int y) => cell[(((y * 296) + x) * 4) + 3];

    private static ReadOnlySpan<byte> Px(byte[] cell, int x, int y) => cell.AsSpan(((y * 296) + x) * 4, 4);

    [GameDataTheory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    [InlineData(Language.Zc)]
    [InlineData(Language.Zt)]
    public void ExitIsComposedInBothLooksOnTheRowsAndAtTheSpacingOfTheOtherItems(Language lang)
    {
        byte[] sheet = Sheet(lang);
        Assert.NotNull(TitleExitArt.Compose(SharedSource.Source.UiSheet(UiSet.TitleParts, lang, UiSheets.TitleMenu.Child)));
        for (int column = 0; column < 2; column++)
        {
            byte[]? cell = TitleExitArt.ComposeState(sheet, column, out ExitLayout layout);
            Assert.NotNull(cell);
            byte[] cont = Cell(sheet, column, 4);
            LetterSpan[] contLetters = TitleExitArt.Segment(cont, out int contTop, out int contBottom);
            Assert.Equal(8, contLetters.Length);
            LetterSpan[] letters = TitleExitArt.Segment(cell, out int top, out int bottom);
            Assert.Equal(4, letters.Length);
            Assert.Equal((contTop, contBottom), (layout.RowTop, layout.RowBottom));
            Assert.Equal(contTop, top);
            Assert.InRange(bottom, contBottom - 1, contBottom);                      // the last row of CONTINUE is a serif's anti-aliasing of a letter EXIT does not use
            Assert.InRange(layout.Gap, 9, 13);
            for (int i = 1; i < 4; i++)
            {
                Assert.Equal(layout.Gap, letters[i].Left - letters[i - 1].Right - 1);
            }

            // centred where the five words are: the cell's middle (148) within a pixel
            float centre = (letters[0].Left + letters[^1].Right) / 2f;
            Assert.InRange(centre, 146.5f, 149f);
            Assert.InRange(layout.Centre, 146.5f, 149f);
            Assert.Equal(letters, layout.Letters);

            // E, I and T are the letters of the sheet, unchanged (their box with the one-pixel margin)
            LetterSpan[] gal = TitleExitArt.Segment(Cell(sheet, column, 3), out _, out _);
            Assert.Equal(7, gal.Length);
            AssertSameBox(cell, letters[2], cont, contLetters[4], top, bottom, "I");
            AssertSameBox(cell, letters[3], cont, contLetters[3], top, bottom, "T");
            bool eFound = false;
            foreach ((int word, int index) in new[] { (0, 1), (1, 7), (4, 7) })
            {
                byte[] w = Cell(sheet, column, word);
                LetterSpan[] ls = TitleExitArt.Segment(w, out int wt, out int wb);
                if ((wt, wb) == (contTop, contBottom) && ls.Length > index && ls[index].Width == letters[0].Width && SameBox(cell, letters[0], w, ls[index], top, bottom))
                {
                    eFound = true;
                }
            }

            Assert.True(eFound, $"{lang} column {column}: the E is none of the sheet's three E's ({layout.ESource})");
        }
    }

    [GameDataTheory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public void TheXIsTheUpperHalfOfTheYAndItsPointMirror(Language lang)
    {
        byte[] sheet = Sheet(lang);
        for (int column = 0; column < 2; column++)
        {
            byte[]? cell = TitleExitArt.ComposeState(sheet, column, out ExitLayout layout);
            Assert.NotNull(cell);
            byte[] gal = Cell(sheet, column, 3);
            LetterSpan y = TitleExitArt.Segment(gal, out _, out _)[6];
            LetterSpan x = layout.Letters[1];
            Assert.Equal(y.Width, x.Width);
            int mid = (layout.RowTop + layout.RowBottom) / 2;
            Assert.Equal(37, mid);
            for (int row = layout.RowTop - 1; row <= layout.RowBottom + 1; row++)
            {
                for (int c = 0; c < x.Width + 2; c++)
                {
                    ReadOnlySpan<byte> got = Px(cell, x.Left - 1 + c, row);
                    if (row < mid)
                    {
                        Assert.True(got.SequenceEqual(Px(gal, y.Left - 1 + c, row)), $"{lang} {column}: row {row} col {c} of the X is not the Y's");
                    }
                    else if (row > mid)
                    {
                        Assert.True(got.SequenceEqual(Px(cell, x.Right + 1 - c, (2 * mid) - row)), $"{lang} {column}: row {row} col {c} of the X is not the mirror of row {(2 * mid) - row}");
                    }
                    else
                    {
                        Assert.True(got[3] >= Math.Max(A(gal, y.Left - 1 + c, row), A(gal, y.Right + 1 - c, row)), "the crossing row takes the brighter of its own pixel and the mirror");
                    }
                }
            }

            // an X: ink in all four corners and at the crossing, open at the sides of the middle row and in the middle of the top row
            Assert.True(A(cell, x.Left + 1, layout.RowTop + 1) >= TitleExitArt.Ink || A(cell, x.Left, layout.RowTop + 1) >= TitleExitArt.Ink);
            Assert.True(A(cell, x.Right - 1, layout.RowTop + 1) >= TitleExitArt.Ink || A(cell, x.Right, layout.RowTop + 1) >= TitleExitArt.Ink);
            Assert.True(A(cell, x.Left + 1, layout.RowBottom - 1) >= TitleExitArt.Ink || A(cell, x.Left, layout.RowBottom - 1) >= TitleExitArt.Ink);
            Assert.True(A(cell, x.Right - 1, layout.RowBottom - 1) >= TitleExitArt.Ink || A(cell, x.Right, layout.RowBottom - 1) >= TitleExitArt.Ink);
            Assert.True(A(cell, (x.Left + x.Right) / 2, mid) >= TitleExitArt.Ink || A(cell, ((x.Left + x.Right) / 2) + 1, mid) >= TitleExitArt.Ink);
            Assert.True(A(cell, x.Left, mid) < TitleExitArt.Ink && A(cell, x.Right, mid) < TitleExitArt.Ink);
            Assert.True(A(cell, (x.Left + x.Right) / 2, layout.RowTop + 2) < TitleExitArt.Ink);
        }
    }

    [GameDataTheory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    [InlineData(Language.Zc)]
    [InlineData(Language.Zt)]
    public void TheColoursAndTheGlowOfExitAreThoseOfItsNeighbours(Language lang)
    {
        byte[] sheet = Sheet(lang);
        for (int column = 0; column < 2; column++)
        {
            byte[]? cell = TitleExitArt.ComposeState(sheet, column, out ExitLayout layout);
            Assert.NotNull(cell);
            byte[] cont = Cell(sheet, column, 4);
            byte[] gal = Cell(sheet, column, 3);
            LetterSpan[] contLetters = TitleExitArt.Segment(cont, out _, out _);
            LetterSpan[] galLetters = TitleExitArt.Segment(gal, out _, out _);

            // the core colour (pixels of alpha 200 and more): the same white or cyan within a few units (the cyan art's hairlines are greener than its stems, so
            // the mean shifts a little with the letters a word has)
            (double R, double G, double B) exitCore = Core(cell);
            (double R, double G, double B) contCore = Core(cont);
            Assert.InRange(Math.Abs(exitCore.R - contCore.R), 0, 12);
            Assert.InRange(Math.Abs(exitCore.G - contCore.G), 0, 12);
            Assert.InRange(Math.Abs(exitCore.B - contCore.B), 0, 12);
            if (column == 1)
            {
                Assert.True(exitCore.R < 60 && exitCore.G > 200 && exitCore.B > 230, "the cursor look is cyan");
            }
            else
            {
                Assert.True(exitCore.R > 240 && exitCore.G > 240 && exitCore.B > 240, "the normal look is white");
            }

            // the far glow beyond the word ends is the level beyond CONTINUE's and GALLERY's ends: the same distance, the same alpha within a few units
            int mid = 37;
            foreach (int d in new[] { 4, 8, 12, 16, 20 })
            {
                int left = A(cell, layout.Letters[0].Left - d, mid);
                int right = A(cell, layout.Letters[^1].Right + d, mid);
                int refLeft = (A(cont, contLetters[0].Left - d, mid) + A(gal, galLetters[0].Left - d, mid)) / 2;
                int refRight = (A(cont, contLetters[^1].Right + d, mid) + A(gal, galLetters[^1].Right + d, mid)) / 2;
                Assert.InRange(Math.Abs(left - refLeft), 0, d <= 4 ? 16 : 8);
                Assert.InRange(Math.Abs(right - refRight), 0, d <= 4 ? 16 : 8);
            }

            // no seam: along the middle row the glow outside the letters never jumps by more than the art's own steps
            int previous = -1;
            for (int x = 2; x < 294; x++)
            {
                bool inBox = false;
                foreach (LetterSpan l in layout.Letters)
                {
                    inBox |= x >= l.Left - 1 && x <= l.Right + 1;
                }

                int a = A(cell, x, mid);
                if (!inBox && previous >= 0)
                {
                    Assert.True(Math.Abs(a - previous) <= 14, $"{lang} column {column}: the glow jumps from {previous} to {a} at column {x}");
                }

                previous = inBox ? -1 : a;
            }

            // far from the word there is nothing, and the glow's colour there is the art's (the light blue beyond 10 px)
            Assert.Equal(0, A(cell, 2, mid));
            Assert.Equal(0, A(cell, 293, mid));
            ReadOnlySpan<byte> far = Px(cell, layout.Letters[0].Left - 12, mid);
            ReadOnlySpan<byte> farRef = Px(cont, contLetters[0].Left - 12, mid);
            Assert.True(far[3] > 0 && farRef[3] > 0);
            Assert.InRange(Math.Abs(far[0] - farRef[0]), 0, 24);
            Assert.InRange(Math.Abs(far[1] - farRef[1]), 0, 24);
            Assert.InRange(Math.Abs(far[2] - farRef[2]), 0, 24);
        }
    }

    [GameDataTheory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    [InlineData(Language.Zc)]
    [InlineData(Language.Zt)]
    public void TheTitleDrawsTheComposedCellInExitsSlotAndTheCursorLookUnderTheCursor(Language lang)
    {
        foreach ((string scene, int id) in new[] { ("title", UiComposedId.TitleExit), ("title-exit", UiComposedId.TitleExitCursor), ("title-nosave", UiComposedId.TitleExit) })
        {
            var services = new PreviewServices(lang);
            UiRoot root = UiPreview.Open(scene, SharedSource.Source, services);
            var list = new DisplayList(2048);
            list.Reset(root.Context.Frame);
            root.Draw(list);
            list.Seal();
            DrawCmd[] cmds = list.Commands.ToArray();
            Assert.DoesNotContain(cmds, c => c.Texture.Kind == TextureKind.GlyphPage && (c.V0.Rgba >> 24) > 250);      // no glyph-font EXIT
            DrawCmd exit = Assert.Single(cmds, c => c.Texture.Kind == TextureKind.UiComposed);
            Assert.Equal(TextureRef.ForUiComposed(id, lang), exit.Texture);
            RectF slot = scene == "title-nosave" ? TitleScreen.ItemRect(4, 5) : TitleScreen.ItemRect(5, 6);
            Assert.Equal((slot.X, slot.Y, slot.Right, slot.Bottom), (exit.V0.X, exit.V0.Y, exit.V3.X, exit.V3.Y));
            Assert.True((exit.V0.Rgba >> 24) > 250);
            UiComposedPicture? pic = root.Context.Assets.Composed(id, lang);
            Assert.NotNull(pic);
            Assert.Equal((296, 72), (pic.Width, pic.Height));
            if (scene == "title-exit")
            {
                Assert.Equal(TitleItem.Exit, ((TitleScreen)root.Stack[0]).Cursor);
            }
        }
    }

    [GameDataTheory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public void TheGlowModelRepaintsEachWordsOwnGlowWithinAFewUnits(Language lang)
    {
        // the model that paints EXIT's glow, applied to the five words it was measured on: outside the letter boxes the repainted cell is the sheet's own cell
        // within a few alpha units on average (the art's glow varies with the letter shapes; the model has the mean of each offset)
        byte[] sheet = Sheet(lang);
        for (int column = 0; column < 2; column++)
        {
            for (int word = 0; word < 5; word++)
            {
                byte[]? painted = TitleExitArt.RepaintGlow(sheet, column, word);
                Assert.NotNull(painted);
                byte[] cell = Cell(sheet, column, word);
                LetterSpan[] letters = TitleExitArt.Segment(cell, out int top, out int bottom);
                double sum = 0, sumSq = 0;
                int n = 0, max = 0, over12 = 0;
                for (int y = 0; y < 72; y++)
                {
                    for (int x = 0; x < 296; x++)
                    {
                        bool inBox = y >= top - 1 && y <= bottom + 1 && letters.Any(l => x >= l.Left - 1 && x <= l.Right + 1);
                        if (inBox)
                        {
                            continue;
                        }

                        int diff = Math.Abs(A(painted, x, y) - A(cell, x, y));
                        sum += diff;
                        sumSq += diff * diff;
                        n++;
                        max = Math.Max(max, diff);
                        if (diff > 12)
                        {
                            over12++;
                        }
                    }
                }

                double mean = sum / n;
                Assert.True(mean <= 2.5, $"{lang} column {column} word {word}: mean alpha error {mean:0.00}");
                Assert.True(over12 <= n / 100, $"{lang} column {column} word {word}: {over12} of {n} pixels off by more than 12 (max {max})");
                Assert.True(max <= 60, $"{lang} column {column} word {word}: a pixel off by {max}");
            }
        }
    }

    private static void AssertSameBox(byte[] cell, LetterSpan at, byte[] src, LetterSpan from, int top, int bottom, string letter)
    {
        Assert.Equal(from.Width, at.Width);
        Assert.True(SameBox(cell, at, src, from, top, bottom), $"the {letter} is not the sheet's letter pixel for pixel");
    }

    private static bool SameBox(byte[] cell, LetterSpan at, byte[] src, LetterSpan from, int top, int bottom)
    {
        for (int y = top - 1; y <= bottom + 1; y++)
        {
            if (!cell.AsSpan((((y * 296) + at.Left - 1) * 4), (at.Width + 2) * 4).SequenceEqual(src.AsSpan((((y * 296) + from.Left - 1) * 4), (from.Width + 2) * 4)))
            {
                return false;
            }
        }

        return true;
    }

    private static (double R, double G, double B) Core(byte[] cell)
    {
        double r = 0, g = 0, b = 0;
        int n = 0;
        for (int i = 0; i < cell.Length; i += 4)
        {
            if (cell[i + 3] >= 200)
            {
                r += cell[i];
                g += cell[i + 1];
                b += cell[i + 2];
                n++;
            }
        }

        Assert.True(n > 50, "a word has core pixels");
        return (r / n, g / n, b / n);
    }
}

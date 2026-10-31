using Tsuki.Engine.Contracts;

namespace Tsuki.Ui.Screens;

/// <summary>
/// The EXIT item of the title, built at run time from the player's own TITLE_PARTS art (package K33). The sheet (child 0, 968 x 472) holds the five items
/// CONTINUE, NEW GAME, LOAD GAME, CONFIG and GALLERY as 296 x 72 cells in three columns (normal, cursor, pressed); no cell spells EXIT and no word has an X.
/// So the letters are cut out of the cells: E, I and T from the words that have them (the crispest E among NEW GAME, LOAD GAME and CONTINUE; the I and the T of
/// CONTINUE), and the X from the Y of GALLERY, whose upper half is a V: the V and its point mirror about the letter's centre make an X whose thick diagonal runs
/// top-left to bottom-right and whose thin one top-right to bottom-left, as in the face of the other letters, with the serifs of the Y's arms on all four ends.
/// </summary>
/// <remarks>
/// <para>
/// Letter boundaries come from the alpha columns of each cell: a column with any pixel at or above <see cref="Ink"/> is letter, a run of such columns is a letter
/// (dips of a few columns, the counters of C, O and G, are joined). The letters keep their rows, so the baseline of EXIT is the baseline of the other words. The
/// letter spacing is the median gap between letters over the five words; the word is centred where the five words are centred.
/// </para>
/// <para>
/// Every letter is taken with a one-pixel margin (its anti-aliasing and the dark edge the white art has). The glow around them cannot be cut from the art, because
/// a letter's glow in its own word mixes with its neighbours' (the gaps of EXIT are not the gaps the letters came from, and E and Y end their words): it is
/// synthesized from the art instead. Measured on the five words of the same column: the glow's alpha and colour for every offset from a pixel to the nearest
/// ink of the nearest letter (so the art's dark edge above and below the letters, its lighter halo beside them and its colour by distance come out as they are),
/// and how the glows of two letters add up where they meet, as the weight with which the weaker is added to the stronger. The synthesized glow has no seams
/// (every pixel is the mean the art itself has at that offset), and it is the player's own glow: nothing is hard-coded but the geometry of the sheet.
/// </para>
/// <para>
/// When the sheet is not the expected art (another size, a word that does not split into its letters, letters on different rows), <see cref="Compose"/> returns null
/// and the title keeps drawing EXIT with the game's glyph font (package K18).
/// </para>
/// </remarks>
public static class TitleExitArt
{
    /// <summary>The width of an item cell.</summary>
    public const int CellWidth = 296;

    /// <summary>The height of an item cell.</summary>
    public const int CellHeight = 72;

    /// <summary>The width of the menu sheet (TITLE_PARTS child 0).</summary>
    public const int SheetWidth = 968;

    /// <summary>The height of the menu sheet.</summary>
    public const int SheetHeight = 472;

    /// <summary>Alpha from which a pixel is letter: the white core of the normal art is 218, the cyan core of the cursor art 254; the dark edge and the glow stay under 120.</summary>
    public const int Ink = 128;

    /// <summary>How far a letter's glow reaches in the art (its halo ends about 27 px from the ink).</summary>
    public const int GlowReach = 28;

    /// <summary>Column gaps of at most this many columns lie inside a letter (the counters of C, O and G); the gaps between letters are 9 px and more.</summary>
    public const int CounterDip = 5;

    private const int MinGap = 6;
    private const int MaxGap = 20;
    private const float FarApart = 20f;
    private const int WordNewGame = 0;
    private const int WordLoadGame = 1;
    private const int WordConfig = 2;
    private const int WordGallery = 3;
    private const int WordContinue = 4;

    /// <summary>The top row of each word's cell in the sheet: NEW GAME, LOAD GAME, CONFIG, GALLERY, CONTINUE (<see cref="UiSheets.TitleMenu"/>: y 24 + 88 k).</summary>
    private static readonly int[] CellTop = [24, 112, 200, 288, 376];

    /// <summary>The letters each word must split into (a space is a wide gap, not a letter).</summary>
    private static readonly int[] LetterCounts = [7, 8, 6, 7, 8];

    /// <summary>The names, for messages.</summary>
    private static readonly string[] WordNames = ["NEW GAME", "LOAD GAME", "CONFIG", "GALLERY", "CONTINUE"];

    /// <summary>
    /// Composes EXIT in both looks from the menu sheet, or returns null when the sheet is not the art the composer expects (the title then draws EXIT with the glyph
    /// font). Both pictures are 296 x 72 cells like the other items.
    /// </summary>
    public static (UiComposedPicture Normal, UiComposedPicture Cursor)? Compose(Picture sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        if (sheet.Width != SheetWidth || sheet.Height != SheetHeight)
        {
            return null;
        }

        byte[] rgba = UiComposed.Assemble(sheet);
        byte[]? normal = ComposeState(rgba, 0, out _);
        byte[]? cursor = ComposeState(rgba, 1, out _);
        if (normal is null || cursor is null)
        {
            return null;
        }

        return (new UiComposedPicture(CellWidth, CellHeight, normal), new UiComposedPicture(CellWidth, CellHeight, cursor));
    }

    /// <summary>
    /// Composes one look of EXIT from column <paramref name="column"/> (0 normal, 1 cursor) of the assembled sheet, or null when the art cannot be cut;
    /// <paramref name="layout"/> tells what was measured and where the letters went.
    /// </summary>
    public static byte[]? ComposeState(ReadOnlySpan<byte> sheet, int column, out ExitLayout layout)
    {
        layout = default;
        WordCell[]? words = Words(sheet, column);
        if (words is null)
        {
            return null;
        }

        WordCell cont = words[WordContinue];
        WordCell gal = words[WordGallery];
        if (gal.RowTop != cont.RowTop || gal.RowBottom != cont.RowBottom)
        {
            return null;
        }

        // the letter spacing and the centre line, measured on the five words
        var gaps = new List<int>();
        float centre = 0f;
        foreach (WordCell w in words)
        {
            for (int i = 1; i < w.Letters.Length; i++)
            {
                int g = w.Letters[i].Left - w.Letters[i - 1].Right - 1;
                if (g is >= MinGap and <= MaxGap)
                {
                    gaps.Add(g);
                }
            }

            centre += (w.Letters[0].Left + w.Letters[^1].Right) / 2f;
        }

        if (gaps.Count == 0)
        {
            return null;
        }

        gaps.Sort();
        int gap = gaps[gaps.Count / 2];
        centre /= words.Length;

        // the sources: the crispest E of the three words that have one (on the rows of CONTINUE), the I and the T of CONTINUE, the Y of GALLERY
        (int Word, int Letter)[] eCandidates = [(WordNewGame, 1), (WordLoadGame, 7), (WordContinue, 7)];
        (int Word, int Letter) e = Crispest(words, eCandidates, cont);
        (int Word, int Letter) iSource = Crispest(words, [(WordConfig, 4), (WordContinue, 4)], cont);
        (int Word, int Letter) t = (WordContinue, 3);
        (int Word, int Letter) y = (WordGallery, 6);
        (int Word, int Letter)[] sources = [e, y, iSource, t];
        LetterSpan yInk = gal.Letters[6];
        (int yTop, int yBottom) = gal.LetterRows(yInk);
        if (yTop + yBottom != cont.RowTop + cont.RowBottom)
        {
            return null;            // the Y is not symmetric about the middle of the letters: its mirror would not meet its upper half
        }

        // the layout: the ink widths with the measured gap between them, centred like the other words
        int total = 3 * gap;
        foreach ((int w, int l) in sources)
        {
            total += words[w].Letters[l].Width;
        }

        int left = (int)MathF.Floor(centre - (total / 2f) + 0.5f);
        if (left < 2 || left + total > CellWidth - 2)
        {
            return null;
        }

        var placed = new LetterSpan[4];
        int x = left;
        for (int k = 0; k < 4; k++)
        {
            int w = words[sources[k].Word].Letters[sources[k].Letter].Width;
            placed[k] = new LetterSpan(x, x + w - 1);
            x += w + gap;
        }

        GlowModel glow = GlowModel.Of(words);

        // the cell: the letter boxes from the art (the X from the Y's upper half and its point mirror), then the glow around them
        byte[] cell = new byte[CellWidth * CellHeight * 4];
        int rowTop = cont.RowTop;
        int rowBottom = cont.RowBottom;
        for (int k = 0; k < 4; k++)
        {
            WordCell src = words[sources[k].Word];
            LetterSpan span = src.Letters[sources[k].Letter];
            CopyBox(src.Rgba, span, rowTop, rowBottom, cell, placed[k].Left);
        }

        MirrorLowerHalf(cell, placed[1], rowTop, rowBottom, yTop + yBottom);
        var composed = new WordCell(cell, placed, rowTop, rowBottom);
        glow.Paint(composed);
        layout = new ExitLayout(gap, centre, placed, rowTop, rowBottom, WordNames[e.Word] + "[" + e.Letter + "]", WordNames[iSource.Word] + "[" + iSource.Letter + "]", glow.Sum);
        return cell;
    }

    /// <summary>
    /// The five words of column <paramref name="column"/> of the assembled sheet with their letters, or null when one does not split into the letters it must
    /// have or its letters touch the cell's edge.
    /// </summary>
    internal static WordCell[]? Words(ReadOnlySpan<byte> sheet, int column)
    {
        if (sheet.Length < SheetWidth * SheetHeight * 4 || column is < 0 or > 2)
        {
            return null;
        }

        var words = new WordCell[CellTop.Length];
        for (int k = 0; k < words.Length; k++)
        {
            words[k] = WordCell.Cut(sheet, 24 + (312 * column), CellTop[k]);
            if (words[k].Letters.Length != LetterCounts[k] || words[k].RowTop < 1 || words[k].RowBottom >= CellHeight - 1)
            {
                return null;
            }
        }

        return words;
    }

    /// <summary>
    /// Repaints the glow of word <paramref name="word"/> (0 NEW GAME, 1 LOAD GAME, 2 CONFIG, 3 GALLERY, 4 CONTINUE) of column <paramref name="column"/> from its
    /// own letter boxes with the glow model of the five words, or null when the sheet is not the expected art. It is the check of the model, for the tests and the
    /// inspection command: outside the letter boxes the result should be the sheet's own cell.
    /// </summary>
    public static byte[]? RepaintGlow(ReadOnlySpan<byte> sheet, int column, int word)
    {
        WordCell[]? words = Words(sheet, column);
        if (words is null || (uint)word >= (uint)words.Length)
        {
            return null;
        }

        WordCell src = words[word];
        byte[] cell = new byte[CellWidth * CellHeight * 4];
        foreach (LetterSpan span in src.Letters)
        {
            CopyBox(src.Rgba, span, src.RowTop, src.RowBottom, cell, span.Left);
        }

        GlowModel.Of(words).Paint(new WordCell(cell, src.Letters, src.RowTop, src.RowBottom));
        return cell;
    }

    /// <summary>The ink column spans of a 296 x 72 cell and the rows its letters occupy (public for the tests and the inspection command).</summary>
    public static LetterSpan[] Segment(ReadOnlySpan<byte> cell, out int rowTop, out int rowBottom)
    {
        Span<int> colMax = stackalloc int[CellWidth];
        colMax.Clear();
        rowTop = -1;
        rowBottom = -1;
        for (int y = 0; y < CellHeight; y++)
        {
            for (int x = 0; x < CellWidth; x++)
            {
                int a = cell[(((y * CellWidth) + x) * 4) + 3];
                if (a > colMax[x])
                {
                    colMax[x] = a;
                }

                if (a >= Ink)
                {
                    if (rowTop < 0)
                    {
                        rowTop = y;
                    }

                    rowBottom = y;
                }
            }
        }

        var spans = new List<LetterSpan>();
        int start = -1;
        for (int x = 0; x <= CellWidth; x++)
        {
            bool ink = x < CellWidth && colMax[x] >= Ink;
            if (ink && start < 0)
            {
                start = x;
            }
            else if (!ink && start >= 0)
            {
                if (spans.Count > 0 && start - spans[^1].Right - 1 <= CounterDip)
                {
                    spans[^1] = new LetterSpan(spans[^1].Left, x - 1);
                }
                else
                {
                    spans.Add(new LetterSpan(start, x - 1));
                }

                start = -1;
            }
        }

        return [.. spans];
    }

    private static (int Word, int Letter) Crispest(WordCell[] words, (int Word, int Letter)[] candidates, WordCell reference)
    {
        (int Word, int Letter) best = default;
        int bestScore = -1;
        foreach ((int w, int l) in candidates)
        {
            WordCell word = words[w];
            if (word.RowTop != reference.RowTop || word.RowBottom != reference.RowBottom || l >= word.Letters.Length)
            {
                continue;           // CONFIG sits a row higher in the sheet: its letters would float
            }

            int score = word.Crispness(word.Letters[l]);
            if (score > bestScore)
            {
                bestScore = score;
                best = (w, l);
            }
        }

        return bestScore < 0 ? candidates[^1] : best;
    }

    /// <summary>Copies the box of a letter (its ink columns and rows plus one pixel of margin on every side) from one cell into another at <paramref name="dstLeft"/>.</summary>
    private static void CopyBox(byte[] src, LetterSpan span, int rowTop, int rowBottom, byte[] dst, int dstLeft)
    {
        int width = span.Width + 2;
        for (int y = rowTop - 1; y <= rowBottom + 1; y++)
        {
            src.AsSpan((((y * CellWidth) + span.Left - 1) * 4), width * 4).CopyTo(dst.AsSpan((((y * CellWidth) + dstLeft - 1) * 4), width * 4));
        }
    }

    /// <summary>
    /// Turns the Y in the box of <paramref name="span"/> into an X: every row under the letter's centre becomes the point mirror (rows and columns reversed about
    /// the centre) of the row above it, and the centre row itself takes the brighter of its own pixels and their mirror, so the two diagonals cross without a seam.
    /// </summary>
    private static void MirrorLowerHalf(byte[] cell, LetterSpan span, int rowTop, int rowBottom, int rowSum)
    {
        int colSum = span.Left + span.Right;
        Span<byte> mirror = stackalloc byte[(span.Width + 2) * 4];
        for (int y = rowTop - 1; y <= rowBottom + 1; y++)
        {
            int ySrc = rowSum - y;
            if (2 * y < rowSum || ySrc < rowTop - 1 || ySrc > rowBottom + 1)
            {
                continue;
            }

            for (int x = span.Left - 1; x <= span.Right + 1; x++)
            {
                int xSrc = colSum - x;
                cell.AsSpan((((ySrc * CellWidth) + xSrc) * 4), 4).CopyTo(mirror.Slice((x - span.Left + 1) * 4, 4));
            }

            for (int x = span.Left - 1; x <= span.Right + 1; x++)
            {
                int o = ((y * CellWidth) + x) * 4;
                int m = (x - span.Left + 1) * 4;
                if (2 * y != rowSum || mirror[m + 3] > cell[o + 3])
                {
                    mirror.Slice(m, 4).CopyTo(cell.AsSpan(o, 4));
                }
            }
        }
    }

    /// <summary>One 296 x 72 cell with its letters.</summary>
    internal sealed class WordCell
    {
        public WordCell(byte[] rgba, LetterSpan[] letters, int rowTop, int rowBottom)
        {
            Rgba = rgba;
            Letters = letters;
            RowTop = rowTop;
            RowBottom = rowBottom;
        }

        public byte[] Rgba { get; }

        public LetterSpan[] Letters { get; }

        /// <summary>The first row with ink in the cell (the serifs' anti-aliasing included).</summary>
        public int RowTop { get; }

        /// <summary>The last row with ink.</summary>
        public int RowBottom { get; }

        public static WordCell Cut(ReadOnlySpan<byte> sheet, int x0, int y0)
        {
            byte[] rgba = new byte[CellWidth * CellHeight * 4];
            for (int y = 0; y < CellHeight; y++)
            {
                sheet.Slice((((y0 + y) * SheetWidth) + x0) * 4, CellWidth * 4).CopyTo(rgba.AsSpan(y * CellWidth * 4, CellWidth * 4));
            }

            LetterSpan[] letters = Segment(rgba, out int top, out int bottom);
            return new WordCell(rgba, letters, top, bottom);
        }

        /// <summary>The heaviest column of a letter: the sum of its alpha over the letter rows (a two-pixel stem at full alpha scores highest).</summary>
        public int Crispness(LetterSpan span)
        {
            int best = 0;
            for (int x = span.Left; x <= span.Right; x++)
            {
                int sum = 0;
                for (int y = RowTop; y <= RowBottom; y++)
                {
                    sum += Rgba[(((y * CellWidth) + x) * 4) + 3];
                }

                best = Math.Max(best, sum);
            }

            return best;
        }

        /// <summary>The rows of one letter's ink.</summary>
        public (int Top, int Bottom) LetterRows(LetterSpan span)
        {
            int top = -1, bottom = -1;
            for (int y = RowTop; y <= RowBottom; y++)
            {
                for (int x = span.Left; x <= span.Right; x++)
                {
                    if (Rgba[(((y * CellWidth) + x) * 4) + 3] >= Ink)
                    {
                        if (top < 0)
                        {
                            top = y;
                        }

                        bottom = y;
                        break;
                    }
                }
            }

            return (top, bottom);
        }

        /// <summary>True inside a letter's box (its ink columns and rows plus the one-pixel margin).</summary>
        public bool InBox(int x, int y)
        {
            if (y < RowTop - 1 || y > RowBottom + 1)
            {
                return false;
            }

            foreach (LetterSpan s in Letters)
            {
                if (x >= s.Left - 1 && x <= s.Right + 1)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The squared distance of every pixel to the nearest ink pixel of each letter, with the offset to it (within <see cref="GlowReach"/>).</summary>
        public LetterField[] Fields()
        {
            var fields = new LetterField[Letters.Length];
            for (int i = 0; i < Letters.Length; i++)
            {
                fields[i] = LetterField.Of(Rgba, Letters[i], RowTop - 1, RowBottom + 1);
            }

            return fields;
        }
    }

    /// <summary>The distance field of one letter.</summary>
    internal sealed class LetterField
    {
        private LetterField()
        {
        }

        /// <summary>Squared distance to the nearest ink pixel of the letter, or <see cref="int.MaxValue"/> beyond the glow's reach.</summary>
        public int[] Dist2 { get; } = new int[CellWidth * CellHeight];

        /// <summary>x of the pixel minus x of its nearest ink pixel.</summary>
        public sbyte[] Dx { get; } = new sbyte[CellWidth * CellHeight];

        /// <summary>y of the pixel minus y of its nearest ink pixel.</summary>
        public sbyte[] Dy { get; } = new sbyte[CellWidth * CellHeight];

        public static LetterField Of(byte[] rgba, LetterSpan span, int rowFrom, int rowTo)
        {
            var f = new LetterField();
            Array.Fill(f.Dist2, int.MaxValue);
            for (int py = rowFrom; py <= rowTo; py++)
            {
                for (int px = span.Left; px <= span.Right; px++)
                {
                    if (rgba[(((py * CellWidth) + px) * 4) + 3] < Ink)
                    {
                        continue;
                    }

                    int y0 = Math.Max(0, py - GlowReach), y1 = Math.Min(CellHeight - 1, py + GlowReach);
                    int x0 = Math.Max(0, px - GlowReach), x1 = Math.Min(CellWidth - 1, px + GlowReach);
                    for (int y = y0; y <= y1; y++)
                    {
                        int dy = y - py;
                        int row = y * CellWidth;
                        for (int x = x0; x <= x1; x++)
                        {
                            int dx = x - px;
                            int dd = (dx * dx) + (dy * dy);
                            if (dd < f.Dist2[row + x])
                            {
                                f.Dist2[row + x] = dd;
                                f.Dx[row + x] = (sbyte)dx;
                                f.Dy[row + x] = (sbyte)dy;
                            }
                        }
                    }
                }
            }

            return f;
        }
    }

    /// <summary>
    /// The glow of the art, measured on the five words of one column: for every offset (dx, dy) from a pixel to the nearest ink pixel of the nearest letter, the
    /// mean alpha and colour of the pixels that have no second letter near (the single-letter kernel, exact on the pixel grid: the art's dark edge above and
    /// below, its lighter halo beside, its colour by distance), and one number for how the glows of two letters add up where they meet: the weaker of the two
    /// is added to the stronger with this weight (0: the stronger alone, 1: their sum), fitted by least squares on every pixel of the five words that has two
    /// letters near.
    /// </summary>
    internal sealed class GlowModel
    {
        private const int Side = GlowReach + 1;
        private readonly double[] _alpha = new double[Side * Side];
        private readonly double[] _r = new double[Side * Side];
        private readonly double[] _g = new double[Side * Side];
        private readonly double[] _b = new double[Side * Side];
        private readonly int[] _count = new int[Side * Side];
        private readonly float[] _alphaAt = new float[Side * Side];
        private readonly float[] _rAt = new float[Side * Side];
        private readonly float[] _gAt = new float[Side * Side];
        private readonly float[] _bAt = new float[Side * Side];
        private readonly List<(sbyte Dx1, sbyte Dy1, sbyte Dx2, sbyte Dy2, byte Alpha)> _pairs = [];

        /// <summary>How much of the weaker of two letters' glows is added to the stronger where they meet (0 none, 1 all).</summary>
        public float Sum { get; private set; }

        /// <summary>The model of the five words.</summary>
        public static GlowModel Of(WordCell[] words)
        {
            var model = new GlowModel();
            foreach (WordCell w in words)
            {
                model.Accumulate(w);
            }

            model.Finish();
            return model;
        }

        /// <summary>The single-letter glow alpha at offset (<paramref name="dx"/>, <paramref name="dy"/>) from the nearest ink pixel (for the tests).</summary>
        public float AlphaAt(int dx, int dy) => Math.Abs(dx) > GlowReach || Math.Abs(dy) > GlowReach ? 0f : _alphaAt[Index(dx, dy)];

        private static int Index(int dx, int dy) => (Math.Abs(dx) * Side) + Math.Abs(dy);

        private void Accumulate(WordCell word)
        {
            LetterField[] fields = word.Fields();
            for (int y = 0; y < CellHeight; y++)
            {
                for (int x = 0; x < CellWidth; x++)
                {
                    if (word.InBox(x, y))
                    {
                        continue;
                    }

                    int idx = (y * CellWidth) + x;
                    Nearest(fields, idx, out int d1, out int d2, out int dx1, out int dy1, out int dx2, out int dy2);
                    if (d1 == int.MaxValue || d1 > GlowReach * GlowReach)
                    {
                        continue;
                    }

                    int o = idx * 4;
                    byte a = word.Rgba[o + 3];
                    if (d2 == int.MaxValue || d2 >= FarApart * FarApart)
                    {
                        int k = Index(dx1, dy1);
                        _alpha[k] += a;
                        _r[k] += word.Rgba[o] * (double)a;
                        _g[k] += word.Rgba[o + 1] * (double)a;
                        _b[k] += word.Rgba[o + 2] * (double)a;
                        _count[k]++;
                    }
                    else
                    {
                        _pairs.Add(((sbyte)dx1, (sbyte)dy1, (sbyte)dx2, (sbyte)dy2, a));
                    }
                }
            }
        }

        /// <summary>Turns the sums into the kernel (an offset nobody sampled takes the mean of its distance ring) and fits the sum weight to the two-letter pixels.</summary>
        private void Finish()
        {
            // the ring means by rounded distance, for the offsets without samples
            double[] ringAlpha = new double[Side + 1];
            double[] ringR = new double[Side + 1];
            double[] ringG = new double[Side + 1];
            double[] ringB = new double[Side + 1];
            int[] ringCount = new int[Side + 1];
            for (int dx = 0; dx <= GlowReach; dx++)
            {
                for (int dy = 0; dy <= GlowReach; dy++)
                {
                    int k = Index(dx, dy);
                    int ring = Math.Min(Side, (int)Math.Round(Math.Sqrt((dx * dx) + (dy * dy))));
                    ringAlpha[ring] += _alpha[k];
                    ringR[ring] += _r[k];
                    ringG[ring] += _g[k];
                    ringB[ring] += _b[k];
                    ringCount[ring] += _count[k];
                }
            }

            for (int dx = 0; dx <= GlowReach; dx++)
            {
                for (int dy = 0; dy <= GlowReach; dy++)
                {
                    int k = Index(dx, dy);
                    int ring = Math.Min(Side, (int)Math.Round(Math.Sqrt((dx * dx) + (dy * dy))));
                    bool own = _count[k] > 0;
                    double a = own ? _alpha[k] : ringAlpha[ring];
                    int n = own ? _count[k] : ringCount[ring];
                    if (n == 0 || a <= 0)
                    {
                        continue;
                    }

                    _alphaAt[k] = (float)(a / n);
                    _rAt[k] = (float)((own ? _r[k] : ringR[ring]) / a);
                    _gAt[k] = (float)((own ? _g[k] : ringG[ring]) / a);
                    _bAt[k] = (float)((own ? _b[k] : ringB[ring]) / a);
                }
            }

            // least squares: observed = max(a1, a2) + Sum * min(a1, a2)
            double num = 0, den = 0;
            foreach ((sbyte dx1, sbyte dy1, sbyte dx2, sbyte dy2, byte observed) in _pairs)
            {
                float a1 = AlphaAt(dx1, dy1);
                float a2 = AlphaAt(dx2, dy2);
                float lo = Math.Min(a1, a2);
                float hi = Math.Max(a1, a2);
                num += lo * (observed - hi);
                den += lo * (double)lo;
            }

            Sum = den > 0 ? (float)Math.Clamp(num / den, 0.0, 1.5) : 0f;
            _pairs.Clear();
        }

        /// <summary>Paints the glow around the letters of <paramref name="word"/> (every pixel outside the letter boxes).</summary>
        public void Paint(WordCell word)
        {
            LetterField[] fields = word.Fields();
            byte[] rgba = word.Rgba;
            for (int y = 0; y < CellHeight; y++)
            {
                for (int x = 0; x < CellWidth; x++)
                {
                    if (word.InBox(x, y))
                    {
                        continue;
                    }

                    int idx = (y * CellWidth) + x;
                    Nearest(fields, idx, out int d1, out int d2, out int dx1, out int dy1, out int dx2, out int dy2);
                    if (d1 == int.MaxValue)
                    {
                        continue;
                    }

                    float a1 = AlphaAt(dx1, dy1);
                    float a2 = d2 == int.MaxValue ? 0f : AlphaAt(dx2, dy2);
                    float a = Math.Max(a1, a2) + (Sum * Math.Min(a1, a2));
                    if (a < 0.5f)
                    {
                        continue;
                    }

                    int k = a1 >= a2 ? Index(dx1, dy1) : Index(dx2, dy2);
                    int o = idx * 4;
                    rgba[o] = Round(_rAt[k]);
                    rgba[o + 1] = Round(_gAt[k]);
                    rgba[o + 2] = Round(_bAt[k]);
                    rgba[o + 3] = Round(a);
                }
            }
        }

        private static void Nearest(LetterField[] fields, int idx, out int d1, out int d2, out int dx1, out int dy1, out int dx2, out int dy2)
        {
            d1 = int.MaxValue;
            d2 = int.MaxValue;
            dx1 = 0;
            dy1 = 0;
            dx2 = 0;
            dy2 = 0;
            foreach (LetterField f in fields)
            {
                int d = f.Dist2[idx];
                if (d < d1)
                {
                    d2 = d1;
                    dx2 = dx1;
                    dy2 = dy1;
                    d1 = d;
                    dx1 = f.Dx[idx];
                    dy1 = f.Dy[idx];
                }
                else if (d < d2)
                {
                    d2 = d;
                    dx2 = f.Dx[idx];
                    dy2 = f.Dy[idx];
                }
            }
        }

        private static byte Round(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
    }
}

/// <summary>The ink columns of one letter in a cell, inclusive.</summary>
/// <param name="Left">The first column with ink.</param>
/// <param name="Right">The last column with ink.</param>
public readonly record struct LetterSpan(int Left, int Right)
{
    /// <summary>The number of columns.</summary>
    public int Width => Right - Left + 1;
}

/// <summary>What <see cref="TitleExitArt.ComposeState"/> measured and did (for the tests, the inspection command and the status note).</summary>
/// <param name="Gap">The letter spacing: the median gap between the letters of the five words.</param>
/// <param name="Centre">The column the five words are centred on (their mean).</param>
/// <param name="Letters">Where E, X, I and T landed in the cell.</param>
/// <param name="RowTop">The first row of the letters.</param>
/// <param name="RowBottom">The last row of the letters.</param>
/// <param name="ESource">The word and letter index the E was cut from.</param>
/// <param name="ISource">The word and letter index the I was cut from.</param>
/// <param name="GlowSum">How much of the weaker of two letters' glows the model adds to the stronger where they meet (0 none, 1 their sum).</param>
public readonly record struct ExitLayout(int Gap, float Centre, LetterSpan[] Letters, int RowTop, int RowBottom, string ESource, string ISource, float GlowSum);

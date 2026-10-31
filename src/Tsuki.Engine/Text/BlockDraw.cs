namespace Tsuki.Engine.Text;

/// <summary>Kind of a glyph quad the block draw emits.</summary>
public enum QuadKind : byte
{
    /// <summary>Drop shadow (draw flag 0x10): black, alpha 0.66 * a, one unit down and right.</summary>
    Shadow,

    /// <summary>Outline copy of the glyph (atlas v + 512).</summary>
    Outline,

    /// <summary>The glyph fill in the record colour.</summary>
    Fill,
}

/// <summary>One quad of a drawn text block, in text-space units.</summary>
/// <param name="Kind">Shadow, outline or fill.</param>
/// <param name="Tex">Texture slot (font page = slot - 0x21).</param>
/// <param name="U0">Atlas rectangle left (inclusive corners as in the record; the outline adds 512 to v).</param>
/// <param name="V0">Atlas rectangle top.</param>
/// <param name="U1">Atlas rectangle right.</param>
/// <param name="V1">Atlas rectangle bottom.</param>
/// <param name="X">Left edge of the quad.</param>
/// <param name="Y">Top edge of the quad.</param>
/// <param name="Sx">Text-space units per texel, x.</param>
/// <param name="Sy">Text-space units per texel, y.</param>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="Alpha">Alpha.</param>
/// <param name="Record">Index into <see cref="TextBlock.Records"/>.</param>
public readonly record struct Quad(
    QuadKind Kind, int Tex, int U0, int V0, int U1, int V1, double X, double Y, double Sx, double Sy,
    int R, int G, int B, double Alpha, int Record);

/// <summary>
/// The block draw (eboot VA 0xC8F50): line offsets, the reveal window, redaction-bar substitution and the
/// outline / fill / drop-shadow quads of a laid-out block (docs/formats/textbox.md 7; tools/tsuki/textbox.py
/// <c>line_offsets</c>, <c>block_quads</c>).
/// </summary>
public static class BlockDraw
{
    /// <summary>Drop shadow (config +0x28 "text shadow").</summary>
    public const int Shadow = 0x10;

    /// <summary>Set by the HUD for every message block.</summary>
    public const int Always = 0x20;

    /// <summary>Line x = x + (width_arg - line_width) / 2.</summary>
    public const int Center = 0x100;

    /// <summary>Line x = x + width_arg - line_width.</summary>
    public const int Right = 0x200;

    /// <summary>y += (height - total) / 2.</summary>
    public const int VCenter = 0x400;

    /// <summary>y += height - total.</summary>
    public const int Bottom = 0x800;

    /// <summary>Outline pass (also needs config +0x26 "text outline" != 0).</summary>
    public const int Outline = 0x1000;

    /// <summary>Head/tail value that shows a whole block (VA 0x1A7130).</summary>
    public const double RevealAll = 768.0;

    /// <summary>The <c>@b</c> mirrored x origin, non-English / English (VA 0x2D3260).</summary>
    private static readonly float[] MirrorX = [463f, 565f];

    /// <summary>Fill colour bytes 1..3 built from the record colour word (VA 0xC9C87..0xC9D06).</summary>
    public static (int R, int G, int B) Rgb565Bytes(int c) => ((c >> 8) & 0xF8, (c >> 3) & 0xF8, (c << 3) & 0xF8);

    /// <summary>
    /// Per-line x and y origins of a block drawn at (<paramref name="x"/>, <paramref name="y"/>)
    /// (VA 0xC8FE8..0xC9401): <c>y_i = y + sum_{j&lt;i}(leading + line_height_j)</c> in float32, x centred or
    /// right-aligned on the width argument by draw flags 0x100 / 0x200, and the block centred / bottom-aligned
    /// inside its height by 0x400 / 0x800. Returns the total height too.
    /// </summary>
    public static (float[] Xs, float[] Ys, float Total) LineOffsets(TextBlock block, float x, float y, int drawFlags = Always)
    {
        ArgumentNullException.ThrowIfNull(block);
        int nl = block.NLines;
        float lead = block.Leading;
        float acc = 0f;
        var ys = new float[nl];
        for (int i = 0; i < nl; i++)
        {
            ys[i] = acc + y;
            acc += lead + (i < TextBlock.LineSlots ? block.LineHeight[i] : 0f);
        }

        int flags = drawFlags | block.Flags;
        var xs = new float[nl];
        if ((flags & Center) != 0)
        {
            float wa = block.WidthArg;
            for (int i = 0; i < nl; i++)
            {
                xs[i] = (wa - block.LineWidth[i]) * 0.5f + x;
            }
        }
        else if ((flags & Right) != 0)
        {
            float x0 = block.WidthArg + x;
            for (int i = 0; i < nl; i++)
            {
                xs[i] = x0 - block.LineWidth[i];
            }
        }
        else
        {
            Array.Fill(xs, x);
        }

        if ((flags & (VCenter | Bottom)) != 0)
        {
            float off = block.Height - acc;
            if ((flags & VCenter) != 0)
            {
                off *= 0.5f;
            }

            for (int i = 0; i < nl; i++)
            {
                ys[i] = off + ys[i];
            }
        }

        return (xs, ys, acc);
    }

    /// <summary>Redaction-bar pictures (VA 0xC9401..0xC9732): (tex, u0, v0, u1, v1) for every record.</summary>
    private static (int Tex, int U0, int V0, int U1, int V1)[] SubstituteBars(GlyphRecord[] recs, float head)
    {
        int count = 0;
        foreach (var r in recs)
        {
            if (r.Tex >= 0x5F && (float)r.Ctr <= head)
            {
                count++;
            }
        }

        int n = Math.Min(count, 21);
        int baseK = n >= 2 ? (n - 2) * (n - 3) / 2 + 2 * n - 3 : 0;
        var output = new (int, int, int, int, int)[recs.Length];
        int kI = 0;
        for (int i = 0; i < recs.Length; i++)
        {
            var r = recs[i];
            (int t, int u0, int v0, int u1, int v1) = (r.Tex, r.X, r.Y, r.X2, r.Y2);
            if (r.Tex >= 0x61 && r.Y >= 0xDB)
            {
                int c = kI % 14;
                (t, u0, v0, u1, v1) = (0x61, c * 68, 0xDB, c * 68 + 0x43, 0x123);
                kI = (kI + 1) % 21;
            }
            else if (r.Tex >= 0x5F)
            {
                int k = baseK + kI;
                t = 0x5F + k / 98;
                u0 = k % 14 * 68;
                v0 = k % 98 / 14 * 73;
                u1 = u0 + 0x43;
                v1 = v0 + 0x48;
                kI = (kI + 1) % 21;
            }

            output[i] = (t, u0, v0, u1, v1);
        }

        return output;
    }

    /// <summary>
    /// The quads VA 0xC8F50 emits for a laid-out block drawn at (<paramref name="x"/>, <paramref name="y"/>) in
    /// text-space units. <paramref name="head"/> / <paramref name="tail"/> are the reveal positions (VA 0x1653E80 /
    /// 0x1653E84): a glyph whose counter is above head is not drawn, below tail fully, otherwise with alpha
    /// (head - ctr) / (head - tail). Order per glyph: drop shadow (draw flag 0x10), outline (draw flag 0x1000 and
    /// <paramref name="outlineOption"/>), fill. The engine stops at the first glyph whose y is below y + height.
    /// </summary>
    public static List<Quad> BlockQuads(
        TextBlock block, float x, float y, int drawFlags = Outline | Always, double alpha = 1.0, double head = RevealAll,
        double tail = RevealAll, double textFade = 1.0, bool outlineOption = true, bool instantOption = false,
        Contracts.Language lang = Contracts.Language.Ja, (int R, int G, int B)? outlineRgb = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        int flags = drawFlags | block.Flags;
        (float[] xs, float[] ys, _) = LineOffsets(block, x, y, flags);
        float hF = (float)head;
        float tF = (float)tail;
        var subs = SubstituteBars(block.Records, hF);
        var output = new List<Quad>(block.Records.Length * 2);
        bool wantOutline = (flags & Outline) != 0 && outlineOption;
        var (outR, outG, outB) = outlineRgb ?? (0, 0, 0);
        for (int ri = 0; ri < block.Records.Length; ri++)
        {
            var r = block.Records[ri];
            (int tex, int u0, int v0, int u1, int v1) = subs[ri];
            if (v0 > 0x1FF)
            {
                continue;
            }

            float c = r.Ctr;
            if (c > hF)
            {
                continue;
            }

            float aR;
            if (tF > c)
            {
                aR = 1f;
            }
            else if (!(hF - tF > 0f))
            {
                aR = 1f;
            }
            else
            {
                aR = (hF - c) / (hF - tF);
            }

            if (instantOption && aR > 0f)
            {
                aR = 1f;
            }

            if (r.Line < 0 || r.Line >= ys.Length)
            {
                continue;
            }

            int line = r.Line;
            float gx;
            if ((r.Flags & 0x10000) != 0)
            {
                // '@b': mirrored placement (VA 0xC9A9D)
                float wq = u1 - u0;
                if (u1 - u0 == 0x43 && tex == 0x5E)
                {
                    wq += -5f;
                }

                gx = MirrorX[lang == Contracts.Language.En ? 1 : 0] - r.Dx - wq * r.Sx;
                gx += xs[line];
            }
            else
            {
                gx = xs[line] + r.Dx;
            }

            float gy = r.Dy + ys[line];
            if (gy > block.Height + y)
            {
                break;
            }

            double fa = aR * (float)alpha;
            if ((flags & Shadow) != 0)
            {
                output.Add(new Quad(QuadKind.Shadow, tex, u0, v0, u1, v1, (double)gx + 1.0, (double)gy + 1.0, r.Sx, r.Sy, 0, 0, 0, fa * 0.66, ri));
            }

            if (wantOutline)
            {
                double oa = 2.0 * Math.Max(0.0, textFade - 0.5) * aR * alpha * alpha;
                output.Add(new Quad(QuadKind.Outline, tex, u0, v0 + 0x200, u1, v1 + 0x200, gx, gy, r.Sx, r.Sy, outR, outG, outB, oa, ri));
            }

            var (cr, cg, cb) = Rgb565Bytes(r.Color);
            output.Add(new Quad(QuadKind.Fill, tex, u0, v0, u1, v1, gx, gy, r.Sx, r.Sy, cr, cg, cb, fa, ri));
        }

        return output;
    }
}

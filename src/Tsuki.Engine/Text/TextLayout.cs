using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Text;

/// <summary>
/// <c>Text_Layout</c> (eboot VA 0xC6C20): both passes for one text block, bit-exact in IEEE float32 in
/// the engine's operation order. Port of <c>tsuki.textbox.layout_box</c> (docs/formats/textbox.md 3-6),
/// which was verified record for record against the game's own function.
/// </summary>
public static class TextLayout
{
    /// <summary>'^' or an automatic wrap on this line index ends the text (VA 0xC7FCD, 0xC8BD0).</summary>
    public const int MaxLineIndex = 31;

    // Constants of the eboot (text VA = file offset - 0x2280). Values typed as the double the Python
    // oracle uses and narrowed to float32, exactly like numpy.float32(x).
    private static readonly float SxDiv = (float)0.33333334;       // VA 0x2D2F94: sx, sy arguments are divided by 3
    private static readonly float RubyDy = (float)-7.04;           // VA 0x2D31A0
    private static readonly float RubySx = (float)0.1;             // VA 0xC7C9A
    private static readonly float RubySy = (float)0.11;
    private static readonly float RubyW = (float)0.1;              // VA 0x2D2FD8: reading pen advance = w * 0.1
    private static readonly float PInit = (float)-40.0;            // VA 0x2D2F98: initial ruby overlap guard
    private static readonly float KMinus160 = (float)-160.0;       // VA 0x2D2FB4: dash line_end margin
    private static readonly float KEmpty = (float)20.0;            // VA 0x2D2FB0: height of an empty line = 20 * sy
    private static readonly float KXBreak = (float)69.0;           // VA 0x2D2FA8: '@x' wrap sets the line height to 69 * sy
    private static readonly float KX555 = (float)555.0;            // VA 0x2D2FA4: English '@x' wrap threshold

    /// <summary>
    /// The scale table of <c>@h</c> / <c>@w</c> (VA 0x2D3730): ten float32 values, as the exact bit patterns
    /// of the eboot.
    /// </summary>
    public static ReadOnlySpan<uint> ScaleTableBits =>
    [
        0x3EB00000, 0x3EBEAAAB, 0x3ECD5555, 0x3EDC0000, 0x3EEAAAAB, 0x3EF95555, 0x3E6AAAAB, 0x3E840000, 0x3E92AAAA, 0x3EA15556,
    ];

    private static readonly float[] ScaleTable = [.. ScaleTableBits.ToArray().Select(BitConverter.UInt32BitsToSingle)];

    /// <summary>The <c>@f</c> box widths (VA 0x2D3758).</summary>
    private static readonly int[] FontMetricTable = [480, 536, 524, 0];

    /// <summary>The <c>@c0..@c5</c> colour words (VA 0x2D34C0).</summary>
    private static readonly int[] ColorTable = [0x632C, 0xF800, 0x345F, 0x1082, 0xAD55, 0x001F];

    /// <summary>The scale table of <c>@h</c> / <c>@w</c> as floats.</summary>
    public static IReadOnlyList<float> Scales => ScaleTable;

    /// <summary>The <c>@f</c> box widths.</summary>
    public static IReadOnlyList<int> FontMetrics => FontMetricTable;

    /// <summary>The <c>@c0..@c5</c> colour words.</summary>
    public static IReadOnlyList<int> Colors => ColorTable;

    /// <summary>
    /// VA 0x538F0(block, width): the width the layout really uses. For blocks 4..8 and 79..89 (the
    /// message slots and the page history): 600 when the window index is 42; else 574 when the language is
    /// English and the width is 474; else the width unchanged.
    /// </summary>
    public static int MainBoxWidth(Language lang, int width = 474, int window = 0, int block = 4)
    {
        if (!(block >= 4 && block <= 8) && !(block >= 79 && block <= 89))
        {
            return width;
        }

        if (window == 42)
        {
            return 600;
        }

        if (lang == Language.En && width == 474)
        {
            return 574;
        }

        return width;
    }

    /// <summary>Lays out <paramref name="text"/> (the message bytes without the terminating NUL).</summary>
    /// <param name="text">Message bytes with their markup.</param>
    /// <param name="lang">Text language: English has no ruby and breaks at words.</param>
    /// <param name="fonts">The glyph tables of the eboot.</param>
    /// <param name="p">Call arguments; null = the ordinary ADV message.</param>
    public static TextBlock Layout(ReadOnlySpan<byte> text, Language lang, FontTables fonts, LayoutParams? p = null)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        p ??= new LayoutParams();
        int langIndex = (int)lang;
        bool english = lang == Language.En;
        var glyphs = fonts.For(lang);
        var b = new byte[text.Length + TextClassifier.Padding];
        text.CopyTo(b);
        var pass1 = TextClassifier.Classify(b, langIndex, p.Flags, fonts.Tags, english);
        int n = pass1.N;
        byte[] cls = pass1.Classes;
        ushort[] wl = pass1.WordLen;
        int flags = p.Flags;
        int window = p.Window;
        int W = MainBoxWidth(lang, p.Width, window, p.Block);

        float sxa = (float)p.Sx;
        float sya = (float)p.Sy;
        float baseSx = SxDiv * sxa;
        float baseSy = SxDiv * sya;
        if (n == 0)
        {
            // VA 0xC7375: no element -> return 0; the block is not (re)initialised and the end counter is not written.
            return new TextBlock
            {
                N = 0, WidthArg = p.Width, Height = p.Height, Flags = flags, Spacing = p.Spacing, Leading = p.Leading,
                BoxWidth = W, Sx = baseSx, Sy = baseSy, NLines = 0, LastGlyphLine = 0,
                LineGlyphs = new ushort[TextBlock.LineSlots], LineWidth = new float[TextBlock.LineSlots],
                LineHeight = new float[TextBlock.LineSlots], Records = [], Classes = cls, WordLen = wl, EndCtr = -1, Empty = true,
            };
        }

        bool normalWindow = p.NormalWindow ?? window < 3;
        float curSx = baseSx;
        float curSy = baseSy;
        int style = (flags >> 19) & 2;
        int anim = 0;
        bool flagB = false;
        bool literalCaret = false;
        float spacingF = p.Spacing;
        int bit17 = (flags >> 17) & 1;

        var recs = new List<GlyphRecord>(n);
        var lineGlyphs = new ushort[TextBlock.LineSlots];
        var lineWidth = new float[TextBlock.LineSlots];
        var lineHeight = new float[TextBlock.LineSlots];
        int nlines = 0;
        int lastGlyphLine = 0;
        int yField = 0;

        int ctr = 1;
        int savedCtr = 0;
        int ext = 0;
        int colw = p.Color & 0xFFFF;
        int hangcap = 0;
        int hang = 0;
        int nowrap = 0;
        int kind = 0;
        float pen = 0f;
        float maxh = 0f;
        int line = 0;
        float P = PInit;
        float off = 0f;
        float rmin = 0f;
        float rpen = 0f;
        int rCtr = 0;
        int rLine = 0;
        int rLineNext = 0;
        int bu = 0;
        int ru = 0;
        int justWrapped = 0;
        int pos = 0;
        int prevPos = -1;
        int i = 0;
        Glyph stale = default;
        bool haveStale = false;

        while (i < n)
        {
            int c = b[pos];
            int q = pos + 1;
            if (c == 0x40)
            {
                // '@' (VA 0xC77D0)
                int op = b[pos + 1];
                q = pos + 2;
                switch (op)
                {
                    case 0x54:
                    case 0x6B:
                        break;
                    case 0x31:
                        b[pos + 2] = 0x5E;
                        literalCaret = true;
                        break;
                    case 0x32:
                        b[pos + 2] = 0x29;
                        break;
                    case 0x61:
                        q = pos + 3;
                        if ((flags & 0x40) == 0)
                        {
                            anim = b[pos + 2] - 0x30;
                        }

                        break;
                    case 0x62:
                        if ((flags & 0x40) == 0)
                        {
                            flagB = true;
                        }

                        break;
                    case 0x63:
                    {
                        q = pos + 3;
                        int v = (b[pos + 2] - 0x30) & 0xFF;
                        if (v == 0)
                        {
                            colw = p.Color & 0xFFFF;
                            ext = 0;
                        }
                        else if (v <= 5)
                        {
                            colw = ColorTable[v];
                        }
                        else if (v < 10)
                        {
                            ext = v;
                        }

                        break;
                    }

                    case 0x66:
                    {
                        q = pos + 3;
                        int v = (b[pos + 2] - 0x30) & 0xFF;
                        W = v < FontMetricTable.Length ? FontMetricTable[v] : 0;
                        break;
                    }

                    case 0x68:
                    case 0x77:
                    {
                        q = pos + 3;
                        int v = (b[pos + 2] - 0x30) & 0xFF;
                        float sc = v < ScaleTable.Length ? ScaleTable[v] : 0f;
                        if (op == 0x68)
                        {
                            curSy = sya * sc;
                        }
                        else
                        {
                            curSx = sxa * sc;
                        }

                        break;
                    }

                    case 0x69:
                        style = english ? style switch { 1 => 4, 2 => 5, _ => 3 } : 1;
                        break;
                    case 0x67:
                        style = 1;
                        break;
                    case 0x7A:
                        style = 2;
                        break;
                    case 0x6C:
                        ctr = savedCtr;
                        break;
                    case 0x75:
                        savedCtr = ctr;
                        break;
                    case 0x74:
                        ctr = 0;
                        break;
                    case 0x6F:
                        nowrap = 1;
                        break;
                    case 0x70:
                        nowrap = 0;
                        break;
                    case 0x72:
                        // VA 0xC87B9
                        anim = 0;
                        flagB = false;
                        ext = 0;
                        curSx = baseSx;
                        curSy = baseSy;
                        style = style == 4 ? 1 : style == 5 ? 2 : 0;
                        colw = p.Color & 0xFFFF;
                        break;
                    case 0x73:
                    {
                        // VA 0xC8836
                        int sign = 1;
                        uint val = 0;
                        int r = pos + 3;
                        bool ended = false;
                        for (int k = 0; k < 0x100; k++)
                        {
                            int d = b[r - 1];
                            if (d == 0 || d == 0x29 || d == 0x2C)
                            {
                                ended = true;
                                break;
                            }

                            if (d == 0x2D)
                            {
                                sign = -1;
                            }
                            else if (d >= 0x30 && d <= 0x39)
                            {
                                val = unchecked(val * 10 + (uint)(d - 0x30));
                            }

                            r++;
                        }

                        q = ended ? r : pos + 0x102;
                        int v = unchecked(sign < 0 ? (int)(0u - val) : (int)val);
                        curSx = curSy = (float)v * 0.015625f;
                        break;
                    }

                    case 0x78:
                    {
                        // VA 0xC88E1
                        int d = b[pos + 2];
                        if (d >= 0x30 && d <= 0x39)
                        {
                            if ((flags & 0x40) != 0)
                            {
                                q = pos + 3;
                                if (b[pos + 3] >= 0x30 && b[pos + 3] <= 0x39)
                                {
                                    q = pos + 4;
                                    if (b[pos + 4] >= 0x30 && b[pos + 4] <= 0x39)
                                    {
                                        q = pos + 5;
                                    }
                                }
                            }
                            else
                            {
                                float x1 = 0f * 10f + (d - 0x30);
                                q = pos + 3;
                                if (b[pos + 3] >= 0x30 && b[pos + 3] <= 0x39)
                                {
                                    x1 = x1 * 10f + (b[pos + 3] - 0x30);
                                    q = pos + 4;
                                    if (b[pos + 4] >= 0x30 && b[pos + 4] <= 0x39)
                                    {
                                        x1 = x1 * 10f + (b[pos + 4] - 0x30);
                                        q = pos + 5;
                                    }
                                }

                                pen = x1;
                            }
                        }
                        else if ((flags & 0x40) == 0)
                        {
                            float sxv = (float)p.SavedX;
                            float limit = curSx * -64f + (float)W;
                            if (sxv > limit || (english && sxv > KX555))
                            {
                                // wrap onto a new line (VA 0xC896F)
                                lineHeight[line] = curSy * KXBreak;
                                hang = 0;
                                justWrapped = 1;
                                nlines++;
                                line++;
                                pen = 0f;
                                maxh = 0f;
                            }
                            else
                            {
                                pen = sxv;
                            }
                        }

                        break;
                    }

                    case 0x79:
                    {
                        // VA 0xC89E4
                        yField = 0;
                        int d = b[pos + 2];
                        if (d >= 0x30 && d <= 0x39)
                        {
                            yField = d - 0x30;
                            q = pos + 3;
                            if (b[pos + 3] >= 0x30 && b[pos + 3] <= 0x39)
                            {
                                yField = yField * 10 + b[pos + 3] - 0x30;
                                q = pos + 4;
                                if (b[pos + 4] >= 0x30 && b[pos + 4] <= 0x39)
                                {
                                    yField = yField * 10 + b[pos + 4] - 0x30;
                                    q = pos + 5;
                                }
                            }
                        }

                        break;
                    }

                    default:
                        // VA 0xC8625: an unknown code skips a "(...)" group
                        if (b[pos + 2] == 0x28)
                        {
                            int r = pos + 4;
                            for (int k = 0; k < 0xFF; k++)
                            {
                                int d = b[r - 1];
                                if (d == 0)
                                {
                                    break;
                                }

                                if (d == 0x29)
                                {
                                    q = r;
                                    break;
                                }

                                r++;
                            }
                        }

                        break;
                }

                prevPos = pos;
                pos = q;
                i++;
                continue;
            }

            if (c == 0x5E && !literalCaret)
            {
                // '^' (VA 0xC7F3D)
                if (justWrapped != 0)
                {
                    justWrapped = 0;
                    nowrap = 0;
                }
                else
                {
                    bool brk = (flags & 0x4000) == 0 && ((flags & 0x8000) == 0 || pen != 0f);
                    if (brk)
                    {
                        nlines++;
                        if (lineHeight[line] == 0f)
                        {
                            lineHeight[line] = curSy * KEmpty;
                        }

                        if (line > 30)
                        {
                            break;
                        }

                        line++;
                        hang = 0;
                        pen = 0f;
                        maxh = 0f;
                    }

                    off = 0f;
                    nowrap = 0;
                    justWrapped = 0;
                }

                prevPos = pos;
                pos = q;
                i++;
                continue;
            }

            if (c == 0)
            {
                break;
            }

            if (c < 0x80 && c != 0x5E)
            {
                if (!english && c == 0x3C)
                {
                    // '<' ruby start (VA 0xC7717)
                    rLineNext = line + 1;
                    int k = 0;
                    bu = 0;
                    int baseOff = pos + 1;
                    while (true)
                    {
                        int d = b[baseOff + k];
                        if (d <= 0x5C)
                        {
                            if (d == 0 || d == 0x29)
                            {
                                break;
                            }
                        }
                        else
                        {
                            if (d == 0x5D)
                            {
                                k++;
                                bu = 2;
                                continue;
                            }

                            if (d == 0x7C)
                            {
                                k++;
                                break;
                            }
                        }

                        (ulong key, _) = TextClassifier.Decode(b, baseOff + k);
                        k += key < 0x100 ? 1 : key <= 0xFFFF ? 2 : key <= 0xFFFFFF ? 3 : 4;
                        bu += (TextClassifier.IsWide(key) ? 1 : 0) + 1;
                    }

                    ru = 0;
                    while (true)
                    {
                        int d = b[baseOff + k];
                        if (d is 0 or 0x29 or 0x3E)
                        {
                            break;
                        }

                        (ulong key, _) = TextClassifier.Decode(b, baseOff + k);
                        k += key < 0x100 ? 1 : key <= 0xFFFF ? 2 : key <= 0xFFFFFF ? 3 : 4;
                        ru += (TextClassifier.IsWide(key) ? 1 : 0) + 1;
                    }

                    float S = curSx * 32f * (float)bu + (float)ru * (float)-3.392;
                    float half = S * 0.5f;
                    if (P - half > 0f)
                    {
                        off = P;
                        P -= S;
                    }
                    else
                    {
                        off = half;
                        P = -half;
                    }

                    kind = 1;
                    rmin = 0f;
                    rpen = pen;
                    rCtr = ctr;
                    rLine = line;
                    prevPos = pos;
                    pos = q;
                    i++;
                    continue;
                }

                if (c == 0x7C && kind > 0)
                {
                    // '|' (VA 0xC7EE7)
                    if (kind == 1)
                    {
                        kind = 2;
                        if ((flags & 0x2000) != 0)
                        {
                            while (true)
                            {
                                int d = b[q];
                                q++;
                                if (d == 0 || d == 0x3E)
                                {
                                    break;
                                }
                            }

                            kind = 0;
                        }
                    }

                    prevPos = pos;
                    pos = q;
                    i++;
                    continue;
                }

                if (!english && c == 0x3E)
                {
                    // '>' (VA 0xC82D2)
                    kind = 0;
                    prevPos = pos;
                    pos = q;
                    i++;
                    continue;
                }
            }

            if (c == 0x5E)
            {
                literalCaret = false;
            }

            // ---- glyph (VA 0xC7824) ----
            float wF = W;
            bool lineEnd = false;
            if (pen >= curSx * KMinus160 + wF)
            {
                int m = Cmod10(i + 1 < TextClassifier.ElementBudget ? cls[i + 1] : 0);
                if (m != 2)
                {
                    lineEnd = m != 3 && nowrap == 0 && kind < 2;
                }
            }

            bool lineStart = 0f >= pen;
            int variant = LanguageGlyphs.DashVariantAt(b, pos, prevPos, lineStart, lineEnd, flagB);
            Glyph g;
            if (variant >= 0)
            {
                g = glyphs.DashGlyph(variant, style, kind, curSx);
            }
            else
            {
                g = glyphs.CharGlyph(b, pos, style, kind, curSx);
                if (g.Consumed == 0)
                {
                    // invalid lead byte: the engine leaves its outputs untouched
                    Glyph basis = haveStale ? stale : new Glyph(0, 0xFFFF, 0, 0, LanguageGlyphs.Cell, LanguageGlyphs.Cell, 0.0);
                    g = basis with { Consumed = 0 };
                }
            }

            stale = g;
            haveStale = true;
            float adj = (float)g.Adj;
            int fl = 1 | (style == 1 ? 4 : 0) | StyleFlags(style);
            if (variant < 0 && anim != 0)
            {
                fl |= 8;
            }

            if (flagB)
            {
                fl |= 0x10000;
            }

            int w = g.W;
            int h = g.H;
            int x2 = (g.X + w - 1) & 0xFFFF;
            int y2 = (g.Y + h - 1) & 0xFFFF;
            int nxtCls;
            int wrapI;
            if (kind >= 2)
            {
                // ruby reading (VA 0xC7BA1)
                int rctr = bu / 3 + 1 + rCtr;
                float x = rpen + off + adj;
                float baseX;
                if (x > wF + (float)-6.4)
                {
                    off = 0f;
                    rLine = rLineNext;
                    x = rmin + 0f + adj;
                    baseX = rmin;
                    P = PInit;
                }
                else
                {
                    baseX = rpen;
                }

                recs.Add(new GlyphRecord(i, fl | 2, colw, ext, rLine, g.Tex & 0xFFFF, g.X, g.Y, x2, y2, rctr & 0xFFFF, x, RubyDy, RubySx, RubySy));
                rpen = (float)w * RubyW + adj + baseX;
                nxtCls = i + 1 < TextClassifier.ElementBudget ? cls[i + 1] : 0;
                wrapI = i;
            }
            else
            {
                if (kind == 1)
                {
                    rmin = pen < rmin ? pen : rmin;
                }

                float dx = pen + adj;
                recs.Add(new GlyphRecord(i, fl, colw, ext, line, g.Tex & 0xFFFF, g.X, g.Y, x2, y2, ctr & 0xFFFF, dx, 0f, curSx, curSy));
                float adv = (float)(w - 3) * curSx + spacingF;
                pen = adv + dx + curSx * -3f;
                lineWidth[line] = pen;
                float hh = (float)(h - 4) * curSy;
                if (hh > maxh)
                {
                    lineHeight[line] = hh;
                    maxh = hh;
                }

                ctr++;
                lastGlyphLine = line;
                lineGlyphs[line] = (ushort)(lineGlyphs[line] + 1);
                if (kind == 0)
                {
                    P -= adv;
                }

                nxtCls = i + 1 < TextClassifier.ElementBudget ? cls[i + 1] : 0;
                wrapI = i;
                if (kind == 1)
                {
                    // last base glyph followed by '|': look past the reading (VA 0xC7DF2..0xC7EDF)
                    int lead = b[pos];
                    int nb = lead < 0x80 ? 1 : (lead & 0xE0) == 0xC0 ? 2 : (lead & 0xF0) == 0xE0 ? 3 : (lead & 0xF8) == 0xF0 ? 4 : 0;
                    if (b[pos + nb] == 0x7C && n - i >= 3)
                    {
                        int k = pos + nb + 1;
                        int cnt = 0;
                        int limit = n - 2 - i;
                        bool found = false;
                        while (cnt != limit)
                        {
                            int d = b[k];
                            if (d < 0x80)
                            {
                                if (d == 0x3E)
                                {
                                    found = true;
                                    break;
                                }

                                k++;
                            }
                            else if ((d & 0xE0) == 0xC0)
                            {
                                k += 2;
                            }
                            else if ((d & 0xF0) == 0xE0)
                            {
                                k += 3;
                            }
                            else if ((d & 0xF8) == 0xF0)
                            {
                                k += 4;
                            }

                            cnt++;
                        }

                        if (found)
                        {
                            wrapI = cnt + 2 + i;
                            int idx = cnt + 3 + i;
                            nxtCls = idx < TextClassifier.ElementBudget ? cls[idx] : 0;
                        }
                    }
                }
            }

            // ---- automatic wrap (VA 0xC7FFD..0xC8BD0) ----
            int consumed = g.Consumed;
            prevPos = pos;
            pos += consumed;
            i++;
            var res = Wrap(i - 1, wrapI, nxtCls, cls, wl, n, pen, wF, curSx, english, nowrap, kind, hang, hangcap, flags, bit17, normalWindow);
            hang = res.Hang;
            hangcap = res.HangCap;
            justWrapped = res.JustWrapped;
            if (res.Break)
            {
                nlines++;
                if (lineHeight[line] == 0f)
                {
                    lineHeight[line] = curSy * KEmpty;
                }

                line++;
                hang = 0;
                pen = 0f;
                maxh = 0f;
            }

            if (line > MaxLineIndex)
            {
                break;
            }
        }

        nlines++;
        return new TextBlock
        {
            N = n, WidthArg = p.Width, Height = p.Height, Flags = flags, Spacing = p.Spacing, Leading = p.Leading, BoxWidth = W,
            Sx = baseSx, Sy = baseSy, NLines = nlines, LastGlyphLine = lastGlyphLine, LineGlyphs = lineGlyphs, LineWidth = lineWidth,
            LineHeight = lineHeight, Records = [.. recs], Classes = cls, WordLen = wl, EndCtr = ctr, YField = yField,
        };
    }

    private static int StyleFlags(int style) => style switch { 2 => 0x100000, 3 => 0x200000, 4 => 0x400000, 5 => 0x600000, _ => 0 };

    /// <summary>Signed C remainder of the (signed char) class value by 10.</summary>
    private static int Cmod10(int c)
    {
        int s = c >= 128 ? c - 256 : c;
        return s % 10;
    }

    private readonly record struct WrapResult(bool Break, int Hang, int HangCap, int JustWrapped);

    /// <summary>
    /// Automatic wrap decision after a glyph (VA 0xC7FFD..0xC8BD0). <paramref name="iElem"/> is the glyph's
    /// element index; <paramref name="iLook"/> is r13, the start of the look-ahead (the '&gt;' of a ruby
    /// group after its last base glyph, else <paramref name="iElem"/>); <paramref name="nxt"/> is the class
    /// of the element after <paramref name="iLook"/>.
    /// </summary>
    private static WrapResult Wrap(
        int iElem, int iLook, int nxt, byte[] cls, ushort[] wl, int n, float pen, float W, float sx, bool english,
        int nowrap, int kind, int hang, int hangcap, int flags, int bit17, bool normalWindow)
    {
        bool enabled = nowrap == 0 && kind < 2;
        if (!enabled)
        {
            return new WrapResult(false, hang, hangcap, 0);
        }

        float lim0;
        float lim1;
        if (english)
        {
            lim0 = sx * -64f + W;
            lim1 = sx * -192f + lim0;
            int wlen = iElem < TextClassifier.ElementBudget ? wl[iElem] : 0;
            if (wlen != 0)
            {
                float x2 = (float)(wlen + 1) * -32f * sx + W;
                if (x2 < lim0)
                {
                    lim1 = x2;
                }

                lim0 = x2 < lim0 ? x2 : lim0;
            }
        }
        else
        {
            lim0 = sx * -96f + W;
            lim1 = lim0;
        }

        int nxtS = nxt >= 128 ? nxt - 256 : nxt;
        float lookX;
        if (pen > lim1)
        {
            if (pen > lim0)
            {
                // past the margin: kinsoku / word rules (VA 0xC80D8, 0xC8A94)
                int cap = 1;
                int al = 1;
                if ((flags & 0x20000) != 0)
                {
                    al = bit17;
                }
                else if (english)
                {
                    al = bit17;
                }
                else
                {
                    int curc = iElem < TextClassifier.ElementBudget ? cls[iElem] : 0;
                    cap = 7;
                    if (!(curc == 0x71 || nxt == 0x71))
                    {
                        if (curc == 0x0E)
                        {
                            cap = 6;
                        }
                        else if (curc == 0x0D)
                        {
                            cap = hangcap >= 7 ? 1 : 0;
                        }
                        else
                        {
                            cap = curc == 2 && nxt == 2 ? 7 : 1;
                        }
                    }
                }

                hangcap = cap;
                int mNext = Cmod10(nxt);
                if (mNext is 2 or 3)
                {
                    if (al == 0 || cap > hang)
                    {
                        return EnglishLookahead(pen, hang + 1, hangcap, iLook, nxt, cls, n, W, sx, english, normalWindow);
                    }

                    return new WrapResult(true, hang, hangcap, 1);
                }

                int cur = iElem < TextClassifier.ElementBudget ? cls[iElem] : 0;
                if (Cmod10(cur) != 4)
                {
                    return new WrapResult(true, hang, hangcap, 1);
                }

                if (al != 0 && cap <= hang)
                {
                    return new WrapResult(true, hang, hangcap, 1);
                }

                return EnglishLookahead(pen, hang + 1, hangcap, iLook, nxt, cls, n, W, sx, english, normalWindow);
            }

            // English: lim1 < pen <= lim0 (VA 0xC82A7)
            if (nxtS > 1)
            {
                if ((nxtS - 4) % 10 == 0 && nxtS >= 4)
                {
                    if (pen > sx * -128f + lim0)
                    {
                        return new WrapResult(true, hang, hangcap, 1);
                    }
                }

                lookX = pen;
            }
            else
            {
                float step0 = nxtS == 0 ? sx * 32f + 3f : sx * 64f;
                float x4 = step0 + pen;
                if (iLook + 2 < n)
                {
                    float step = sx * 32f + 3f;
                    int k = iLook + 2;
                    for (int t = 0; t < n - 2 - iLook; t++)
                    {
                        int d = k < TextClassifier.ElementBudget ? cls[k] : 0;
                        if (Cmod10(d) != 2)
                        {
                            break;
                        }

                        if (x4 > lim0)
                        {
                            return new WrapResult(true, hang, hangcap, 1);
                        }

                        x4 = (d == 2 ? step : sx * 64f) + x4;
                        k++;
                    }
                }

                lookX = x4;
            }

            return EnglishLookahead(lookX, hang, hangcap, iLook, nxt, cls, n, W, sx, english, normalWindow);
        }

        return EnglishLookahead(pen, hang, hangcap, iLook, nxt, cls, n, W, sx, english, normalWindow);
    }

    /// <summary>
    /// VA 0xC817F: in the normal (full-screen) window, English text past 3/4 of the box breaks before a
    /// word whose estimated width would end beyond W + 2 * (32 * sx + 3).
    /// </summary>
    private static WrapResult EnglishLookahead(
        float x, int hang, int hangcap, int iCur, int nxt, byte[] cls, int n, float W, float sx, bool english, bool normalWindow)
    {
        if (!normalWindow)
        {
            return new WrapResult(false, hang, hangcap, 0);
        }

        if (!(x > W * 3f * 0.25f))
        {
            return new WrapResult(false, hang, hangcap, 0);
        }

        if (!english)
        {
            return new WrapResult(false, hang, hangcap, 0);
        }

        if (nxt != 0)
        {
            return new WrapResult(false, hang, hangcap, 0);
        }

        if (iCur + 2 >= n)
        {
            return new WrapResult(false, hang, hangcap, 0);
        }

        float s32 = sx * 32f;
        float acc = s32 + x + 3f;
        float step = s32 + 3f;
        float lim = step + step + W;
        int k = iCur + 2;
        for (int t = 0; t < n - 2 - iCur; t++)
        {
            int d = k < TextClassifier.ElementBudget ? cls[k] : 0;
            if (d == 1 || (d != 0 && d > 5))
            {
                acc = sx * 64f + acc;
            }
            else if (d == 0)
            {
                return new WrapResult(false, hang, hangcap, 0);
            }
            else
            {
                acc = step + acc;
            }

            if (acc > lim)
            {
                return new WrapResult(true, hang, hangcap, 1);
            }

            k++;
        }

        return new WrapResult(false, hang, hangcap, 0);
    }
}

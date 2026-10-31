using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Text;

/// <summary>Result of layout pass 1: the element count, the break classes and the English word lengths.</summary>
/// <param name="N">Elements found (at most <see cref="TextClassifier.ElementBudget"/>).</param>
/// <param name="Classes">Break class of every element (byte array VA 0x178C100, 1200 entries).</param>
/// <param name="WordLen">English word-length table (u16 array VA 0x178C5B0): entry i-1 = length + 1 of the word that starts at element i.</param>
public readonly record struct ClassifyResult(int N, byte[] Classes, ushort[] WordLen);

/// <summary>
/// Layout pass 1 of <c>Text_Layout</c> (eboot VA 0xC6D80..0xC7375): one break class per element (glyph or
/// control) within a 1200-element budget, plus the English word-length table. Port of
/// <c>tsuki.textbox.classify</c> (docs/formats/textbox.md 5.1).
/// </summary>
public static class TextClassifier
{
    /// <summary>Pass 1 loop bound (VA 0xC6D95): 1200 elements.</summary>
    public const int ElementBudget = 0x4B0;

    /// <summary>Zero bytes appended after the text so look-ahead reads see the NUL terminator and more.</summary>
    internal const int Padding = 300;

    /// <summary>VA 0x11AB0: a "wide" character (break class 1, ruby unit 2).</summary>
    public static bool IsWide(ulong key)
    {
        if (key < 0x100)
        {
            return false;
        }

        if (key <= 0xFFFF)
        {
            return key is 0xC2B1 or 0xC397 or 0xC3B7;
        }

        if (key <= 0xFFFFFF)
        {
            return !(key >= 0xEFBDA1 && key <= 0xEFBE9F);
        }

        return true;
    }

    /// <summary>VA 0x11B00 plus the length rule of pass 1: (key, bytes); an invalid lead gives (0, 1).</summary>
    internal static (ulong Key, int Length) Decode(ReadOnlySpan<byte> b, int p)
    {
        int c = b[p];
        if (c < 0x80)
        {
            return ((ulong)c, 1);
        }

        if ((c & 0xE0) == 0xC0)
        {
            return (((ulong)c << 8) | b[p + 1], 2);
        }

        if ((c & 0xF0) == 0xE0)
        {
            return (((ulong)c << 16) | ((ulong)b[p + 1] << 8) | b[p + 2], 3);
        }

        if ((c & 0xF8) == 0xF0)
        {
            return (((ulong)c << 24) | ((ulong)b[p + 1] << 16) | ((ulong)b[p + 2] << 8) | b[p + 3], 4);
        }

        return (0, 1);
    }

    private static bool Bit(ulong mask, long k) => k >= 0 && k < 64 && ((mask >> (int)k) & 1) == 1;

    /// <summary>Pass-1 class of a wide character (VA 0xC6EF3..0xC7051, 0xC70C9..0xC7211).</summary>
    internal static int WideClass(ulong key)
    {
        long k;
        if (key > 0xE38282)
        {
            k = (long)key - 0xE38283;
            if (k <= 0x26 && Bit(0x5540000815UL, k))
            {
                return 0x0D;                                      // small kana
            }

            k = (long)key - 0xE38383;
            if (k >= 0 && k <= 0x39 && Bit(0x30C081500000001UL, k))
            {
                return 0x0D;
            }

            k = (long)key - 0xEFBC81;
            if (k >= 0 && k <= 0x1E)
            {
                if (Bit(0x6002900UL, k))
                {
                    return 0x71;
                }

                if (Bit(0x40000001UL, k))
                {
                    return 0x0D;
                }

                if (k == 7)
                {
                    return 0x0E;
                }
            }

            return 1;
        }

        k = (long)key - 0xE38081;
        if (k >= 0 && k <= 0x0E)
        {
            if (Bit(0x5412UL, k))
            {
                return 0x71;
            }

            if (Bit(0x2800UL, k))
            {
                return 0x0E;
            }

            if (k == 0)
            {
                return 0x0D;
            }
        }

        k = (long)key - 0xE38181;
        if (k >= 0 && k <= 0x22 && Bit(0x400000155UL, k))
        {
            return 0x0D;
        }

        k = (long)key - 0xE28098;
        if (k >= 0 && k <= 0x0F)
        {
            if (k is 0 or 4)
            {
                return 0x0E;
            }

            if (k is 1 or 5)
            {
                return 0x71;
            }

            if (k == 15)
            {
                return 0x0D;
            }
        }

        return 1;
    }

    private static bool AfterDotOk(int c) => c <= 0x2E && c is 0x22 or 0x27 or 0x2E;

    private static int TagLength(ReadOnlySpan<byte> b, int p, IReadOnlyList<byte[]> tags)
    {
        foreach (byte[] t in tags)
        {
            if (b[p..].StartsWith(t))
            {
                return t.Length;
            }
        }

        return 0;
    }

    /// <summary>Pass 1 (VA 0xC6D80..0xC7375) with the standard <c>[tag]</c> names.</summary>
    /// <param name="text">The message bytes without the terminating NUL.</param>
    /// <param name="lang">Text language (English: no ruby, English word table).</param>
    /// <param name="flags">Caller flags (0x2000 skips ruby readings).</param>
    public static ClassifyResult Classify(ReadOnlySpan<byte> text, Language lang, int flags = 0)
    {
        var padded = new byte[text.Length + Padding];
        text.CopyTo(padded);
        return Classify(padded, (int)lang, flags, FontTables.StandardTagBytes, lang == Language.En);
    }

    /// <summary>Pass 1 over a buffer that already carries at least <see cref="Padding"/> zero bytes after the text.</summary>
    internal static ClassifyResult Classify(byte[] b, int lang, int flags, IReadOnlyList<byte[]> tags, bool english)
    {
        var cls = new byte[ElementBudget];
        var wl = new ushort[ElementBudget];
        int p = 0;
        int i = 0;
        int budget = ElementBudget;
        int prev = 0;
        int ruby = 0;
        while (true)
        {
            int c = b[p];
            if (c == 0)
            {
                break;
            }

            int q = p + 1;
            int cur = c;
            if (c >= 0x80)
            {
                (ulong key, int ln) = Decode(b, p);
                q = p + ln;
                if (IsWide(key))
                {
                    cls[i] = (byte)WideClass(key);
                }
                else if (!(prev <= 0x2E && prev is 0x20 or 0x2C or 0x2E))
                {
                    cls[i] = 2;
                    if (key != 0xC2B7 && i != 0)
                    {
                        int a = cls[i - 1];
                        if ((a | 2) == 3 || a >= 5)
                        {
                            cls[i] = 0;
                        }
                    }
                }
            }
            else if (lang != 1 && c == 0x3C)
            {
                ruby = 1;
            }
            else if (c == 0x7C && ruby > 0)
            {
                if (ruby == 1)
                {
                    ruby = 2;
                }

                if ((flags & 0x2000) != 0)
                {
                    while (true)
                    {
                        cur = b[q];
                        q++;
                        if (cur == 0 || cur == 0x3E)
                        {
                            break;
                        }
                    }

                    ruby = 0;
                }
            }
            else if (lang != 1 && c == 0x3E)
            {
                ruby = 0;
            }
            else if (c == 0x40)
            {
                int d = b[p + 1];
                int e = d - 0x61;
                if (e >= 0 && e <= 0x16 && (Bit(0x4000A4UL, e) || e == 0))
                {
                    q = p + 3;
                }
                else if ((d & 0xFE) == 0x78)
                {
                    if (!(b[p + 2] >= 0x30 && b[p + 2] <= 0x39))
                    {
                        q = p + 2;
                    }
                    else if (!(b[p + 3] >= 0x30 && b[p + 3] <= 0x39))
                    {
                        q = p + 3;
                    }
                    else
                    {
                        q = b[p + 4] >= 0x30 && b[p + 4] <= 0x39 ? p + 5 : p + 4;
                    }
                }
                else if (b[p + 2] == 0x28)
                {
                    q = p + 2;
                    int r = p + 4;
                    for (int n = 0; n < 0xFF; n++)
                    {
                        int d2 = b[r - 1];
                        if (d2 == 0)
                        {
                            break;
                        }

                        if (d2 == 0x29)
                        {
                            q = r;
                            break;
                        }

                        r++;
                    }
                }
                else
                {
                    q = p + 2;
                }
            }
            else if (c == 0x5E)
            {
                // '^': a plain element
            }
            else if (c == 0x5B)
            {
                int tl = TagLength(b, p, tags);
                if (tl != 0)
                {
                    q = p + tl;
                    cls[i] = 1;
                }
            }
            else
            {
                bool wordStart = prev == 0x20 || (prev == 0x2C && c != 0x20) || (prev == 0x2E && !AfterDotOk(c));
                if (wordStart)
                {
                    if (english && i != 0)
                    {
                        wl[i - 1] = 1;
                        int k = 0;
                        int sc = c;
                        while (true)
                        {
                            int d = b[q + k];
                            if (d == 0 || d == 0x5E || sc == 0x20)
                            {
                                break;
                            }

                            if (sc == 0x2C && d != 0x20)
                            {
                                break;
                            }

                            if (sc == 0x2E && !AfterDotOk(d))
                            {
                                break;
                            }

                            wl[i - 1] = (ushort)(k + 2);
                            k++;
                            if (budget == k)
                            {
                                break;
                            }

                            sc = d;
                        }
                    }
                }
                else if ((c & 0xFD) == 0x2C)
                {
                    cls[i] = 3;
                }
                else if (prev is 0x21 or 0x2E or 0x3F && c is 0x27 or 0x22)
                {
                    cls[i] = 3;
                }
                else
                {
                    cls[i] = 2;
                }
            }

            i++;
            budget--;
            prev = cur;
            p = q;
            if (i == ElementBudget)
            {
                break;
            }
        }

        return new ClassifyResult(i, cls, wl);
    }
}

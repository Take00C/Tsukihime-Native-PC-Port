using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Text;

/// <summary>How plain-text rendering shows a ruby group.</summary>
public enum RubyRendering
{
    /// <summary>The base only.</summary>
    Drop,

    /// <summary><c>base(reading)</c>.</summary>
    Paren,

    /// <summary>The source markup <c>&lt;base|reading&gt;</c>.</summary>
    Markup,
}

/// <summary>
/// The markup tokenizer of the engine's layout pass 2 (VA 0xC76C0 ff., docs/formats/text.md 6), plain-text rendering, the style
/// state machine and the census; a port of <c>tsuki.text.tokenize</c>, <c>plain_text</c>, <c>to_runs</c> and <c>markup_census</c>.
/// </summary>
/// <remarks>
/// It works on the UTF-8 <em>bytes</em> of a line, as the engine does: one-byte parameters, byte-counted windows (<c>@s</c> 256,
/// <c>@X(...)</c> 255). Text that a parameter splits inside a multi-byte character is kept as the undecodable bytes (the Python
/// reference keeps them as surrogate escapes); it never happens in the retail data.
/// </remarks>
public static class Markup
{
    private static readonly byte[][] IconTags = BuildIconTags();

    private static byte[][] BuildIconTags()
    {
        var tags = new byte[TextRules.IconNames.Count][];
        for (int i = 0; i < tags.Length; i++)
        {
            tags[i] = Encoding.ASCII.GetBytes("[" + TextRules.IconNames[i] + "]");
        }

        return tags;
    }

    /// <summary>
    /// Splits a line into tokens following the engine's layout pass 2. Ruby <c>&lt;base|ruby&gt;</c> is parsed unless the language
    /// is English. With <paramref name="script"/> the string is a <c>_ZM/_ZD/_MSAD</c> message argument: a <c>$</c> followed by six
    /// digits becomes a <see cref="RefToken"/>, and <c>@e</c> / <c>@n</c> end the message (later text is returned as one final text
    /// token after that code; the engine discards it).
    /// </summary>
    public static ImmutableArray<MarkupToken> Tokenize(ReadOnlySpan<byte> line, Language lang, bool script = false)
    {
        bool rubyOn = TextRules.RubyEnabled(lang);
        return new Scanner(line.ToArray(), rubyOn, script).Run();
    }

    /// <summary>Tokenizes a string (its UTF-8 bytes).</summary>
    public static ImmutableArray<MarkupToken> Tokenize(string line, Language lang, bool script = false) =>
        Tokenize(Encoding.UTF8.GetBytes(line), lang, script);

    /// <summary>All tokens, descending into ruby groups (the group first, then its parts).</summary>
    public static IEnumerable<MarkupToken> Flatten(IEnumerable<MarkupToken> tokens)
    {
        foreach (MarkupToken t in tokens)
        {
            yield return t;
            if (t is RubyToken r)
            {
                foreach (MarkupToken inner in Flatten(r.BaseTokens))
                {
                    yield return inner;
                }

                foreach (MarkupToken inner in Flatten(r.ReadingTokens))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>
    /// Readable text as UTF-8 bytes: <c>^</c> becomes a line feed, codes are removed, ruby is rendered as asked, icons keep their
    /// <c>[name]</c> unless <paramref name="icons"/> is false, and references keep their source.
    /// </summary>
    public static byte[] PlainText(IEnumerable<MarkupToken> tokens, RubyRendering ruby = RubyRendering.Drop, bool icons = true)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var parts = new List<byte>();
        foreach (MarkupToken t in tokens)
        {
            switch (t)
            {
                case TextToken text:
                    parts.AddRange(text.Text);
                    break;
                case RubyToken r:
                    switch (ruby)
                    {
                        case RubyRendering.Drop:
                            parts.AddRange(r.Base);
                            break;
                        case RubyRendering.Paren:
                            parts.AddRange(r.Base);
                            parts.Add((byte)'(');
                            parts.AddRange(r.Reading);
                            parts.Add((byte)')');
                            break;
                        default:
                            parts.AddRange(r.Raw);
                            break;
                    }

                    break;
                case NewlineToken:
                    parts.Add((byte)'\n');
                    break;
                case IconToken icon:
                    if (icons)
                    {
                        parts.AddRange(icon.Raw);
                    }

                    break;
                case RefToken reference:
                    parts.AddRange(reference.Raw);
                    break;
            }
        }

        return [.. parts];
    }

    /// <summary>Tokenizes <paramref name="line"/> and renders it (see <see cref="PlainText(IEnumerable{MarkupToken}, RubyRendering, bool)"/>).</summary>
    public static byte[] PlainText(ReadOnlySpan<byte> line, Language lang, RubyRendering ruby = RubyRendering.Drop, bool icons = true) =>
        PlainText(Tokenize(line, lang), ruby, icons);

    /// <summary>The glyph flag word the layout writes for a style (VA 0xC790C..0xC7B39).</summary>
    public static int GlyphFlags(MarkupStyle style, bool ligaturePiece = false)
    {
        int f = 1 | (style.Style == 1 ? 4 : 0);
        f |= style.Style switch { 2 => 0x100000, 3 => 0x200000, 4 => 0x400000, 5 => 0x600000, _ => 0 };
        if (style.Anim != 0 && !ligaturePiece)
        {
            f |= 8;
        }

        if (style.FlagB)
        {
            f |= 0x10000;
        }

        return f;
    }

    /// <summary>
    /// Applies the control codes and returns runs that carry their style. <c>Seq</c> approximates the engine's per-glyph typewriter
    /// counter (starts at 1, +1 per code point; ruby glyphs are not counted; <c>@t</c> sets it to 0). Codes inside a ruby group take
    /// effect after the group's run.
    /// </summary>
    public static List<MarkupRun> ToRuns(IEnumerable<MarkupToken> tokens, Language lang)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        _ = TextRules.Code(lang);
        var machine = new RunMachine(lang == Language.En);
        foreach (MarkupToken t in tokens)
        {
            machine.Add(t);
        }

        return machine.Runs;
    }

    /// <summary>
    /// Counts every markup construct in <paramref name="lines"/> (line id = position): key to (count, lines, first ids). Tokens inside
    /// ruby groups are counted too, with the key prefixed by <c>in ruby: </c>. Keys are decoded as UTF-8 (invalid bytes become
    /// U+FFFD) and sorted ordinally.
    /// </summary>
    public static SortedDictionary<string, CensusEntry> Census(IEnumerable<ReadOnlyMemory<byte>> lines, Language lang)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var stats = new SortedDictionary<string, CensusEntry>(StringComparer.Ordinal);
        int id = 0;
        foreach (ReadOnlyMemory<byte> line in lines)
        {
            Visit(Tokenize(line.Span, lang), id, string.Empty, stats);
            id++;
        }

        return stats;
    }

    /// <summary>Number of Python <c>str</c> characters of UTF-8 bytes decoded with <c>surrogateescape</c>: one per scalar value, one per undecodable byte.</summary>
    public static int CountCodePoints(ReadOnlySpan<byte> utf8)
    {
        int count = 0;
        while (!utf8.IsEmpty)
        {
            System.Buffers.OperationStatus status = Rune.DecodeFromUtf8(utf8, out _, out int consumed);
            if (status == System.Buffers.OperationStatus.Done)
            {
                utf8 = utf8[consumed..];
            }
            else
            {
                utf8 = utf8[1..];
            }

            count++;
        }

        return count;
    }

    private static void Add(SortedDictionary<string, CensusEntry> stats, string key, int id)
    {
        if (!stats.TryGetValue(key, out CensusEntry? e))
        {
            e = new CensusEntry();
            stats[key] = e;
        }

        e.Count++;
        if (e.LastLine != id)
        {
            e.Lines++;
            if (e.FirstIds.Count < 5)
            {
                e.FirstIds.Add(id);
            }

            e.LastLine = id;
        }
    }

    private static void Visit(IEnumerable<MarkupToken> tokens, int id, string prefix, SortedDictionary<string, CensusEntry> stats)
    {
        foreach (MarkupToken t in tokens)
        {
            switch (t)
            {
                case RubyToken r:
                    Add(stats, prefix + "ruby <base|ruby>", id);
                    if (!r.Closed)
                    {
                        Add(stats, prefix + "ruby group not closed", id);
                    }

                    Visit(r.BaseTokens, id, "in ruby: ", stats);
                    Visit(r.ReadingTokens, id, "in ruby: ", stats);
                    break;
                case NewlineToken:
                    Add(stats, prefix + "^ (newline)", id);
                    break;
                case IconToken icon:
                    Add(stats, prefix + $"[{icon.Name}] (icon {icon.Index})", id);
                    break;
                case CodeToken code:
                    {
                        string letter = Encoding.UTF8.GetString(code.Code.AsSpan());
                        string arg = code.Arg is { } a ? Encoding.UTF8.GetString(a.AsSpan()) : string.Empty;
                        string key = letter != ">" ? $"@{letter}{arg}" : "> (stray, swallowed)";
                        Add(stats, prefix + key, id);
                        break;
                    }

                case TextToken text:
                    foreach (byte ch in text.Text)
                    {
                        if (ch is (byte)'<' or (byte)'>' or (byte)'|' or (byte)'[' or (byte)']' or (byte)'@' or (byte)'^' or (byte)'$' or (byte)'%')
                        {
                            Add(stats, prefix + $"literal '{(char)ch}'", id);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>One census entry.</summary>
    public sealed class CensusEntry
    {
        /// <summary>Occurrences.</summary>
        public int Count { get; set; }

        /// <summary>Lines that contain it.</summary>
        public int Lines { get; set; }

        /// <summary>The first five line ids.</summary>
        public List<int> FirstIds { get; } = [];

        internal int LastLine { get; set; } = -1;
    }

    private sealed class Scanner(byte[] b, bool rubyOn, bool script)
    {
        private readonly int _n = b.Length;
        private readonly List<MarkupToken> _out = [];
        private readonly List<byte> _buf = [];
        private readonly List<MarkupToken> _base = [];
        private readonly List<MarkupToken> _ruby = [];
        private int _state;
        private int _groupStart;

        public ImmutableArray<MarkupToken> Run()
        {
            int i = 0;
            while (i < _n)
            {
                int plainEnd = PlainEnd(i);
                if (plainEnd > i)
                {
                    for (int k = i; k < plainEnd; k++)
                    {
                        _buf.Add(b[k]);
                    }

                    i = plainEnd;
                    continue;
                }

                byte c = b[i];
                if (c == 0)
                {
                    break;
                }

                if (script && c == 0x24 && i + 7 <= _n && AllDigits(i + 1, 6))
                {
                    int id = 0;
                    for (int k = 1; k <= 6; k++)
                    {
                        id = (id * 10) + (b[i + k] - 0x30);
                    }

                    Emit(new RefToken(id, Slice(i, i + 7)));
                    i += 7;
                    continue;
                }

                if (c == 0x40)
                {
                    if (i + 1 >= _n)
                    {
                        // Engine: '@' + NUL goes to the default handler, which skips 2 bytes and reads past the end; stop here.
                        Emit(new CodeToken([], null, ImmutableArray.Create((byte)'@')));
                        break;
                    }

                    if (!ScanCode(ref i))
                    {
                        break;
                    }

                    continue;
                }

                if (c == 0x5E)
                {
                    Emit(new NewlineToken());
                    i++;
                    continue;
                }

                if (c == 0x5B)
                {
                    int icon = MatchIcon(i);
                    if (icon >= 0)
                    {
                        Emit(new IconToken(TextRules.IconNames[icon], icon, ImmutableArray.Create(IconTags[icon])));
                        i += IconTags[icon].Length;
                        continue;
                    }
                }

                if (rubyOn && c == 0x3C)
                {
                    if (_state != 0)
                    {
                        CloseGroup(i, closed: false);
                    }
                    else
                    {
                        Flush();
                    }

                    _state = 1;
                    _groupStart = i;
                    i++;
                    continue;
                }

                if (rubyOn && c == 0x7C && _state != 0)
                {
                    if (_state == 1)
                    {
                        Flush();
                        _state = 2;
                    }

                    i++;
                    continue;
                }

                if (rubyOn && c == 0x3E)
                {
                    if (_state != 0)
                    {
                        CloseGroup(i + 1, closed: true);
                    }
                    else
                    {
                        Emit(new CodeToken(ImmutableArray.Create((byte)'>'), null, ImmutableArray.Create((byte)'>')));
                    }

                    i++;
                    continue;
                }

                _buf.Add(c);
                i++;
            }

            if (_state != 0)
            {
                CloseGroup(_n, closed: false);
            }

            Flush();
            return [.. _out];
        }

        /// <summary>Scans the <c>@</c> code at <paramref name="i"/> (a following byte exists); false ends the scan (script mode <c>@e</c> / <c>@n</c>).</summary>
        private bool ScanCode(ref int i)
        {
            byte k = b[i + 1];
            int j = i + 2;
            ImmutableArray<byte>? arg = null;
            if (k is (byte)'a' or (byte)'c' or (byte)'f' or (byte)'h' or (byte)'w')
            {
                arg = Slice(j, j + 1);
                j++;
            }
            else if (k == (byte)'s')
            {
                int kk = j;
                while (kk < _n && kk - j < 256 && b[kk] is not (0x2C or 0x29 or 0))
                {
                    kk++;
                }

                arg = Slice(j, kk);
                j = kk < _n && kk - j < 256 ? kk + 1 : kk;
            }
            else if (k is (byte)'x' or (byte)'y')
            {
                int kk = j;
                while (kk < _n && kk - j < 3 && b[kk] is >= (byte)'0' and <= (byte)'9')
                {
                    kk++;
                }

                ImmutableArray<byte> digits = Slice(j, kk);
                arg = digits.Length > 0 ? digits : null;
                j = kk;
            }
            else if (k is (byte)'1' or (byte)'2')
            {
                Emit(new CodeToken(ImmutableArray.Create(k), null, Slice(i, i + 2)));
                if (j < _n)
                {
                    Target().Add(new TextToken(ImmutableArray.Create(k == (byte)'1' ? (byte)'^' : (byte)')'), Slice(j, j + 1)));
                }

                i = j + 1;
                return true;
            }
            else if (k is (byte)'b' or (byte)'g' or (byte)'i' or (byte)'k' or (byte)'l' or (byte)'o' or (byte)'p' or (byte)'r'
                or (byte)'t' or (byte)'u' or (byte)'z' or (byte)'T')
            {
                // no parameter
            }
            else if (j < _n && b[j] == 0x28)
            {
                int close = -1;
                int limit = Math.Min(_n, j + 1 + 255);
                for (int p = j + 1; p < limit; p++)
                {
                    if (b[p] == 0)
                    {
                        break;
                    }

                    if (b[p] == 0x29)
                    {
                        close = p;
                        break;
                    }
                }

                if (close != -1)
                {
                    arg = Slice(j, close + 1);
                    j = close + 1;
                }
            }

            Emit(new CodeToken(ImmutableArray.Create(k), arg, Slice(i, j)));
            i = j;
            if (script && k is (byte)'e' or (byte)'n')
            {
                if (_state != 0)
                {
                    CloseGroup(i, closed: false);
                }

                if (i < _n)
                {
                    _out.Add(new TextToken(Slice(i, _n)));
                }

                return false;
            }

            return true;
        }

        private ImmutableArray<byte> Slice(int from, int to)
        {
            int end = Math.Min(to, _n);
            int start = Math.Min(from, end);
            return ImmutableArray.Create(b, start, end - start);
        }

        private bool AllDigits(int from, int count)
        {
            for (int k = 0; k < count; k++)
            {
                if (b[from + k] is < (byte)'0' or > (byte)'9')
                {
                    return false;
                }
            }

            return true;
        }

        private int PlainEnd(int from)
        {
            int p = from;
            while (p < _n && b[p] is not (0 or 0x24 or 0x40 or 0x5E or 0x5B or 0x3C or 0x7C or 0x3E))
            {
                p++;
            }

            return p;
        }

        private int MatchIcon(int at)
        {
            for (int t = 0; t < IconTags.Length; t++)
            {
                byte[] tag = IconTags[t];
                if (at + tag.Length <= _n && b.AsSpan(at, tag.Length).SequenceEqual(tag))
                {
                    return t;
                }
            }

            return -1;
        }

        private List<MarkupToken> Target() => _state == 0 ? _out : _state == 1 ? _base : _ruby;

        private void Flush()
        {
            if (_buf.Count == 0)
            {
                return;
            }

            ImmutableArray<byte> t = [.. _buf];
            List<MarkupToken> target = Target();
            if (target.Count > 0 && target[^1] is TextToken prev)
            {
                target.RemoveAt(target.Count - 1);
                target.Add(new TextToken(prev.Text.AddRange(t), prev.Raw.AddRange(t)));
            }
            else
            {
                target.Add(new TextToken(t, t));
            }

            _buf.Clear();
        }

        private void Emit(MarkupToken token)
        {
            Flush();
            Target().Add(token);
        }

        private void CloseGroup(int end, bool closed)
        {
            Flush();
            _out.Add(new RubyToken(
                JoinText(_base), JoinText(_ruby), Slice(_groupStart, end), [.. _base], [.. _ruby], closed));
            _base.Clear();
            _ruby.Clear();
            _state = 0;
        }

        private static ImmutableArray<byte> JoinText(List<MarkupToken> tokens)
        {
            var builder = ImmutableArray.CreateBuilder<byte>();
            foreach (MarkupToken t in tokens)
            {
                if (t is TextToken text)
                {
                    builder.AddRange(text.Text);
                }
            }

            return builder.ToImmutable();
        }
    }

    private sealed class RunMachine(bool english)
    {
        private MarkupStyle _st;
        private int _seq = 1;
        private int _saved = 1;

        public List<MarkupRun> Runs { get; } = [];

        public void Add(MarkupToken token)
        {
            switch (token)
            {
                case TextToken t:
                    Runs.Add(new MarkupRun("text", t.Text, default, _st, null, _seq));
                    _seq += CountCodePoints(t.Text.AsSpan());
                    break;
                case RubyToken r:
                    Runs.Add(new MarkupRun("ruby", r.Base, r.Reading, _st, null, _seq));
                    // Python: len(t.base), where base joins the text of the tokens of the base part, each decoded on its own
                    _seq += r.BaseTokens.OfType<TextToken>().Sum(x => CountCodePoints(x.Text.AsSpan()));
                    foreach (MarkupToken inner in Flatten([.. r.BaseTokens, .. r.ReadingTokens]))
                    {
                        if (inner is CodeToken code)
                        {
                            Apply(code);
                        }
                    }

                    break;
                case IconToken icon:
                    Runs.Add(new MarkupRun("icon", ImmutableArray.Create(Encoding.ASCII.GetBytes(icon.Name)), default, _st, icon.Index, _seq));
                    _seq++;
                    break;
                case NewlineToken:
                    Runs.Add(new MarkupRun("newline", default, default, _st, null, _seq));
                    break;
                case RefToken reference:
                    Runs.Add(new MarkupRun("ref", reference.Raw, default, _st, reference.LineId, _seq));
                    break;
                case CodeToken code:
                    Apply(code);
                    break;
            }
        }

        private static ImmutableArray<byte> Ascii(string s) => ImmutableArray.Create(Encoding.ASCII.GetBytes(s));

        private void Apply(CodeToken t)
        {
            // c: the code as Python sees it. A one-byte ASCII code is that letter, the empty code (a lone '@') is "", and a lone high byte
            // matches nothing. Note that Python's `c in "xy"` is true for the empty string.
            string c = t.Code.Length == 0 ? string.Empty : t.Code.Length == 1 && t.Code[0] < 0x80 ? ((char)t.Code[0]).ToString() : "(non-ascii)";
            ImmutableArray<byte>? a = t.Arg;
            int? d = a is { Length: 1 } one && one[0] < 0x80 ? one[0] - 0x30 : null;

            if (c == "c" && d is { } dc)
            {
                dc &= 0xFF;
                if (dc == 0)
                {
                    _st = _st with { Color = null, ColorExt = 0 };
                }
                else if (dc <= 5)
                {
                    _st = _st with { Color = TextRules.ColorRgb565[dc] };
                }
                else if (dc <= 9)
                {
                    _st = _st with { ColorExt = dc };
                }
            }
            else if (c == "g")
            {
                _st = _st with { Style = 1 };
            }
            else if (c == "z")
            {
                _st = _st with { Style = 2 };
            }
            else if (c == "i")
            {
                _st = _st with { Style = english ? _st.Style switch { 1 => 4, 2 => 5, _ => 3 } : 1 };
            }
            else if (c == "b")
            {
                _st = _st with { FlagB = true };
            }
            else if (c == "a" && d is { } da)
            {
                _st = _st with { Anim = da };
            }
            else if (c == "r")
            {
                _st = _st with
                {
                    Style = _st.Style switch { 4 => 1, 5 => 2, _ => 0 },
                    Color = null,
                    ColorExt = 0,
                    FlagB = false,
                    Anim = 0,
                    ScaleW = null,
                    ScaleH = null,
                };
            }
            else if (c == "o")
            {
                _st = _st with { NoWrap = true };
            }
            else if (c == "p")
            {
                _st = _st with { NoWrap = false };
            }
            else if (c == "h" && d is { } dh && dh >= 0 && dh < TextRules.ScaleTable.Count)
            {
                _st = _st with { ScaleH = TextRules.ScaleTable[dh] };
            }
            else if (c == "w" && d is { } dw && dw >= 0 && dw < TextRules.ScaleTable.Count)
            {
                _st = _st with { ScaleW = TextRules.ScaleTable[dw] };
            }
            else if (c == "f" && d is { } df)
            {
                _st = _st with { FontMetric = df };
            }
            else if (c == "s" && a is { } sArg)
            {
                double v = ParseScale(sArg.AsSpan());
                _st = _st with { ScaleW = v, ScaleH = v };
            }
            else if (c == "t")
            {
                _seq = 0;
                Runs.Add(new MarkupRun("seq", Ascii("t"), default, _st, 0, _seq));
            }
            else if (c == "u")
            {
                _saved = _seq;
                Runs.Add(new MarkupRun("seq", Ascii("u"), default, _st, _seq, _seq));
            }
            else if (c == "l")
            {
                _seq = _saved;
                Runs.Add(new MarkupRun("seq", Ascii("l"), default, _st, _seq, _seq));
            }
            else if (c == "k")
            {
                Runs.Add(new MarkupRun("wait", Ascii("k"), default, _st, null, _seq));
            }
            else if (c is "x" or "y" or "")
            {
                int? value = a is { Length: > 0 } digits ? int.Parse(Encoding.ASCII.GetString(digits.AsSpan()), CultureInfo.InvariantCulture) : null;
                Runs.Add(new MarkupRun("pos", Ascii(c), default, _st, value, _seq));
            }
        }

        /// <summary><c>@s</c>: the digits of the parameter (all other bytes are skipped) over 64, negated when it holds a minus sign.</summary>
        private static double ParseScale(ReadOnlySpan<byte> a)
        {
            var digits = new StringBuilder();
            bool negative = false;
            foreach (byte ch in a)
            {
                if (ch is >= (byte)'0' and <= (byte)'9')
                {
                    digits.Append((char)ch);
                }
                else if (ch == (byte)'-')
                {
                    negative = true;
                }
            }

            double magnitude = digits.Length == 0 ? 0.0 : double.Parse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
            double signed = negative && magnitude != 0.0 ? -magnitude : magnitude;
            return signed / 64.0;
        }
    }
}

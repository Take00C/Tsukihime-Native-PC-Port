using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Vm;

/// <summary>A half-open byte range <c>[Start, End)</c> of one argument inside a buffer.</summary>
/// <param name="Start">Offset of the first byte.</param>
/// <param name="End">Offset just after the last byte (equal to <paramref name="Start"/> for an empty argument).</param>
public readonly record struct ArgRange(int Start, int End)
{
    /// <summary>Number of bytes.</summary>
    public int Length => End - Start;

    /// <summary>The bytes of this range in <paramref name="buf"/>.</summary>
    public ReadOnlySpan<byte> In(ReadOnlySpan<byte> buf) => buf[Start..End];
}

/// <summary>How <see cref="ObjectParser.Split(ReadOnlySpan{byte}, int, Span{ArgRange}, out int)"/> ended.</summary>
public enum SplitStatus : byte
{
    /// <summary>The argument list was split.</summary>
    Ok = 0,

    /// <summary>An argument has no <c>,</c> <c>)</c> or <c>;</c> after it: the engine's <c>strpbrk</c> would return NULL.</summary>
    NoTerminator = 1,

    /// <summary>A quoted argument has no closing quote: the engine's <c>strchr</c> would run past the statement.</summary>
    UnterminatedQuote = 2,
}

/// <summary>
/// Argument reader 2: the object argument parser of the PS4 interpreter (eboot text VA 0xD690 split, 0xD8C0 "has",
/// 0xE310 / 0xE3C0 strtol getters; docs/formats/script.md 3.3a), used by the 52 commands of
/// <see cref="ObjectCommands"/>. Integer arguments go through <see cref="ObjectExpression"/> instead of the
/// <see cref="InlineReader"/>. Byte-exact port of <c>tsuki.script.obj_split</c>, <c>obj_strict_int</c> and
/// <c>obj_partial_int</c>.
/// </summary>
public static class ObjectParser
{
    /// <summary>The parser keeps at most this many arguments (VA 0xD690).</summary>
    public const int MaxArguments = 20;

    /// <summary>Offset in <see cref="Statement.Args"/> of the separator byte that <see cref="Split(ReadOnlySpan{byte}, int, Span{ArgRange}, out int)"/> takes as <c>sep</c>.</summary>
    public const int SeparatorInArgs = 0;

    private const long LongMax = long.MaxValue;

    /// <summary>
    /// VA 0xD690: splits the argument list. <c>buf[sep]</c> is the separator byte after the four name bytes (normally
    /// <c>(</c>). Writes the (start, end) ranges to <paramref name="ranges"/> (at least <see cref="MaxArguments"/> long) and
    /// their number to <paramref name="count"/>.
    /// </summary>
    /// <remarks>
    /// <c>buf[sep + 1]</c> being <c>)</c> or <c>;</c> means no arguments (<c>()</c> has 0). An argument starting with a double
    /// quote runs to the next quote (it may contain <c>,</c> <c>)</c> <c>;</c>) and the byte after the closing quote is taken as
    /// the separator. Any other argument runs to the next <c>,</c> <c>)</c> or <c>;</c>. The list ends when the separator is
    /// <c>)</c> or <c>;</c>, or after 20 arguments.
    /// </remarks>
    public static SplitStatus Split(ReadOnlySpan<byte> buf, int sep, Span<ArgRange> ranges, out int count)
    {
        if (ranges.Length < MaxArguments)
        {
            throw new ArgumentException($"room for {MaxArguments} ranges is required", nameof(ranges));
        }

        count = 0;
        if (At(buf, sep + 1) is (byte)')' or (byte)';')
        {
            return SplitStatus.Ok;
        }

        int q = sep + 1;
        while (true)
        {
            int start;
            int e;
            if (At(buf, q) == (byte)'"')
            {
                start = q + 1;
                e = IndexFrom(buf, start, "\""u8);
                if (e < 0)
                {
                    return SplitStatus.UnterminatedQuote;
                }
            }
            else
            {
                start = q;
                e = IndexFrom(buf, start, ",);"u8);
                if (e < 0)
                {
                    return SplitStatus.NoTerminator;
                }
            }

            ranges[count++] = new ArgRange(start, e);
            int skip = At(buf, e) == (byte)'"' ? 1 : 0;
            if (At(buf, e + skip) is (byte)')' or (byte)';' || count == MaxArguments)
            {
                return SplitStatus.Ok;
            }

            q = e + skip + 1;
        }
    }

    /// <summary>Splits the arguments of a statement (the separator is <c>Args[0]</c>).</summary>
    public static SplitStatus Split(in Statement statement, Span<ArgRange> ranges, out int count) =>
        Split(statement.Args.Span, SeparatorInArgs, ranges, out count);

    /// <summary>VA 0xD8C0: argument <paramref name="i"/> exists and is not empty.</summary>
    public static bool Has(ReadOnlySpan<ArgRange> ranges, int count, int i) => i < count && ranges[i].End > ranges[i].Start;

    /// <summary>
    /// VA 0xE310 / 0xE3C0: <c>strtol(arg, &amp;end, 10)</c> on a copy of the argument (1..15 bytes). The value when the whole
    /// argument was consumed (leading white space and one sign are accepted), else null: <c>"5 "</c> and <c>"242.25"</c> are
    /// not. The result is truncated to 32 bits. Port of <c>obj_strict_int</c>.
    /// </summary>
    public static int? StrictInt(ReadOnlySpan<byte> arg)
    {
        if (arg.Length is < 1 or > 15)
        {
            return null;
        }

        return Strtol(arg, out int consumed, out int value) && consumed == arg.Length ? value : null;
    }

    /// <summary>
    /// The value VA 0xE3C0 stores even when it reports failure: <c>strtol</c> of the leading part (0 when there is no number);
    /// null when the argument is empty or longer than 15 bytes, in which case nothing is stored. Used by TOMP. Port of
    /// <c>obj_partial_int</c>.
    /// </summary>
    public static int? PartialInt(ReadOnlySpan<byte> arg)
    {
        if (arg.Length is < 1 or > 15)
        {
            return null;
        }

        return Strtol(arg, out _, out int value) ? value : 0;
    }

    /// <summary>
    /// <c>[ \t\n\v\f\r]*[+-]?[0-9]+</c> at the start of <paramref name="s"/>: false when it does not match. The magnitude
    /// saturates at <c>LONG_MAX</c> (<c>LONG_MAX + 1</c> for a minus sign) and the result keeps the low 32 bits.
    /// </summary>
    private static bool Strtol(ReadOnlySpan<byte> s, out int consumed, out int value)
    {
        consumed = 0;
        value = 0;
        int i = 0;
        while (i < s.Length && s[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or 0x0B or 0x0C or (byte)'\r')
        {
            i++;
        }

        bool negative = false;
        if (i < s.Length && s[i] is (byte)'+' or (byte)'-')
        {
            negative = s[i] == (byte)'-';
            i++;
        }

        int digits = i;
        ulong magnitude = 0;
        ulong cap = negative ? (ulong)LongMax + 1 : (ulong)LongMax;
        for (; i < s.Length && s[i] is >= (byte)'0' and <= (byte)'9'; i++)
        {
            if (magnitude <= (cap - (ulong)(s[i] - '0')) / 10)
            {
                magnitude = (magnitude * 10) + (ulong)(s[i] - '0');
            }
            else
            {
                magnitude = cap;
            }
        }

        if (i == digits)
        {
            return false;
        }

        consumed = i;
        long signedValue = negative ? unchecked(-(long)magnitude) : (long)magnitude;
        value = unchecked((int)signedValue);
        return true;
    }

    private static byte At(ReadOnlySpan<byte> buf, int i) => (uint)i < (uint)buf.Length ? buf[i] : (byte)0;

    /// <summary>Index of the first byte of <paramref name="any"/> at or after <paramref name="from"/>, or -1.</summary>
    private static int IndexFrom(ReadOnlySpan<byte> buf, int from, ReadOnlySpan<byte> any)
    {
        if ((uint)from >= (uint)buf.Length)
        {
            return -1;
        }

        int i = buf[from..].IndexOfAny(any);
        return i < 0 ? -1 : from + i;
    }
}

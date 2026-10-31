using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Vm;

/// <summary>
/// The textual view of a statement's arguments: the arguments cut at <c>,</c> until the first <c>)</c> or <c>;</c>, the
/// bytes between <c>)</c> and <c>;</c> (ignored by the engine), and the keyword tags
/// (docs/formats/script.md 3.1, 3.4). Port of <c>tsuki.script._split_args</c>, <c>Command.trailer</c> and <c>Arg.tag</c> /
/// <c>Arg.value</c>. Handlers use it for string arguments (labels, names, text references); the integer readers are
/// <see cref="InlineReader"/> and <see cref="ObjectParser"/>.
/// </summary>
public static class ArgumentList
{
    /// <summary>Offset in <see cref="Statement.Args"/> of the first argument of an ordinary command: after the separator byte.</summary>
    public const int CommandStartInArgs = 1;

    /// <summary>
    /// Offset in <see cref="Statement.Args"/> of the first argument of a <c>Z</c> marker: <c>_ZMbbbss(</c> is nine characters
    /// long, so the arguments start four bytes after the separator position.
    /// </summary>
    public const int MarkerStartInArgs = 4;

    /// <summary>
    /// Where the textual arguments of <paramref name="statement"/> start inside <see cref="Statement.Args"/>
    /// (<see cref="MarkerStartInArgs"/> for names starting with <c>Z</c>, else <see cref="CommandStartInArgs"/>), clamped to the end.
    /// </summary>
    public static int StartIn(in Statement statement)
    {
        int start = statement.Name.First == (byte)'Z' ? MarkerStartInArgs : CommandStartInArgs;
        return Math.Min(start, statement.Args.Length);
    }

    /// <summary>
    /// Splits <c>data[start..]</c> at <c>,</c> until a <c>)</c> or <c>;</c> (or the end). Appends the ranges to
    /// <paramref name="ranges"/> and returns the index of the terminating <c>)</c> / <c>;</c> (or <c>data.Length</c>).
    /// <c>()</c> gives no arguments; <c>(,)</c> gives two empty ones.
    /// </summary>
    public static int Split(ReadOnlySpan<byte> data, int start, List<ArgRange> ranges)
    {
        int cur = start;
        int i = start;
        int added = 0;
        while (i < data.Length)
        {
            byte c = data[i];
            if (c == (byte)',')
            {
                ranges.Add(new ArgRange(cur, i));
                added++;
                cur = i + 1;
            }
            else if (c is (byte)')' or (byte)';')
            {
                break;
            }

            i++;
        }

        if (added > 0 || i > cur)
        {
            ranges.Add(new ArgRange(cur, i));
        }

        return i;
    }

    /// <summary>
    /// The bytes after the first <c>)</c> up to the terminating <c>;</c>, which the engine ignores: <c>_STGS(0,,0,800,0)#);</c>
    /// has the trailer <c>#)</c> (<c>Command.trailer</c>). <paramref name="close"/> is the index <see cref="Split"/> returned.
    /// Empty when the list ended at a <c>;</c> or there is nothing after the <c>)</c>.
    /// </summary>
    public static ReadOnlySpan<byte> Trailer(ReadOnlySpan<byte> data, int close)
    {
        if (close >= data.Length || data[close] != (byte)')')
        {
            return default;
        }

        int stop = data[^1] == (byte)';' ? data.Length - 1 : data.Length;
        return close + 1 <= stop ? data[(close + 1)..stop] : default;
    }

    /// <summary>
    /// The keyword tag <c>`NNN:</c> (backtick, three decimal digits, colon) at the start of an argument, else null
    /// (<c>Arg.tag</c>). The tag digits are ASCII.
    /// </summary>
    public static int? Tag(ReadOnlySpan<byte> arg)
    {
        if (arg.Length >= 5 && arg[0] == (byte)'`' && IsDigit(arg[1]) && IsDigit(arg[2]) && IsDigit(arg[3]) && arg[4] == (byte)':')
        {
            return ((arg[1] - '0') * 100) + ((arg[2] - '0') * 10) + (arg[3] - '0');
        }

        return null;
    }

    /// <summary>The argument without its tag: the first five bytes are dropped when it starts with a backtick (<c>Arg.value</c>).</summary>
    public static ReadOnlySpan<byte> Untagged(ReadOnlySpan<byte> arg) =>
        arg.Length > 0 && arg[0] == (byte)'`' ? (arg.Length >= 5 ? arg[5..] : default) : arg;

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';
}

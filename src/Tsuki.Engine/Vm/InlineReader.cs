using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Vm;

/// <summary>
/// Argument reader 1: the inline integer readers of the PS4 interpreter (eboot text VA 0x76690 for 16 bits, 0x76780
/// for 32 bits, 0x76860 for "present"; docs/formats/script.md 3.3). Byte-exact port of
/// <c>tsuki.script.inline_int</c> / <c>inline_present</c>.
/// </summary>
/// <remarks>
/// <para>
/// The readers work on raw statement bytes and a start offset <c>p0</c>, the byte after the separator that follows the
/// four name bytes (for a <see cref="Statement"/> that is <c>Args[1]</c>, see <see cref="StartInArgs"/>). Argument
/// <c>index</c> is reached by skipping <c>index</c> commas (at most 500 bytes each); a <c>)</c> or <c>;</c> on the way
/// means the argument is missing (-1 / not present). A leading backtick then skips 5 bytes blindly (a keyword tag), and at
/// most 16 bytes are read: digits accumulate a decimal number, <c>+</c> and <c>-</c> close the current term and set the
/// sign of the next one, <c>,</c> <c>)</c> <c>;</c> end the value, every other byte (letters, <c>.</c>, NUL) is ignored.
/// So <c>()</c> index 0 is 0, <c>(5)</c> index 1 is -1, <c>242.25</c> is 24225 and <c>258-18</c> is 240.
/// </para>
/// <para>
/// Bytes past the end of the span read as 0, as in the reference (the engine would read the following statements; the
/// two agree on every retail statement).
/// </para>
/// </remarks>
public static class InlineReader
{
    /// <summary>Offset in <see cref="Statement.Args"/> of the first argument byte: the byte after the separator.</summary>
    public const int StartInArgs = 1;

    private const int MaxTermBytes = 16;
    private const int MaxSkipBytes = 500;

    /// <summary>
    /// Reads argument <paramref name="index"/> as a signed integer of <paramref name="bits"/> (16 or 32) bits; -1 when the
    /// argument does not exist. <paramref name="index"/> is the engine's i16, so at most 32767.
    /// </summary>
    public static int ReadInt(ReadOnlySpan<byte> buf, int p0, int index, int bits = 16)
    {
        if (bits is not (16 or 32))
        {
            throw new ArgumentOutOfRangeException(nameof(bits), bits, "the inline readers exist for 16 and 32 bits");
        }

        int p = Skip(buf, p0, index);
        if (p < 0)
        {
            return -1;
        }

        long v = ReadValue(buf, p);
        return bits == 16 ? unchecked((short)v) : unchecked((int)v);
    }

    /// <summary>The 16-bit reader (VA 0x76690).</summary>
    public static int ReadInt16(ReadOnlySpan<byte> buf, int p0, int index) => ReadInt(buf, p0, index, 16);

    /// <summary>The 32-bit reader (VA 0x76780).</summary>
    public static int ReadInt32(ReadOnlySpan<byte> buf, int p0, int index) => ReadInt(buf, p0, index, 32);

    /// <summary>
    /// VA 0x76860: argument <paramref name="index"/> exists and its first byte (after an optional tag) is not <c>,</c>
    /// <c>)</c> or <c>;</c>.
    /// </summary>
    public static bool IsPresent(ReadOnlySpan<byte> buf, int p0, int index)
    {
        int p = Skip(buf, p0, index);
        if (p < 0)
        {
            return false;
        }

        if (p < buf.Length && buf[p] == (byte)'`')
        {
            p += 5;
        }

        byte c = At(buf, p);
        return c is not ((byte)')' or (byte)',' or (byte)';');
    }

    /// <summary>The 16-bit reader on argument <paramref name="index"/> of a statement.</summary>
    public static int ReadInt16(in Statement statement, int index) => ReadInt(statement.Args.Span, StartInArgs, index, 16);

    /// <summary>The 32-bit reader on argument <paramref name="index"/> of a statement.</summary>
    public static int ReadInt32(in Statement statement, int index) => ReadInt(statement.Args.Span, StartInArgs, index, 32);

    /// <summary>The "present" test on argument <paramref name="index"/> of a statement.</summary>
    public static bool IsPresent(in Statement statement, int index) => IsPresent(statement.Args.Span, StartInArgs, index);

    /// <summary>
    /// Value of a single argument text under the inline reader, e.g. <c>"258-18"</c> is 240. Equivalent to
    /// <see cref="ReadInt(ReadOnlySpan{byte}, int, int, int)"/> on <c>raw + ")"</c> with index 0 (<c>tsuki.script.engine_int</c>).
    /// </summary>
    public static int ReadText(ReadOnlySpan<byte> raw, int bits = 16)
    {
        Span<byte> tmp = raw.Length < 256 ? stackalloc byte[raw.Length + 1] : new byte[raw.Length + 1];
        raw.CopyTo(tmp);
        tmp[raw.Length] = (byte)')';
        return ReadInt(tmp, 0, 0, bits);
    }

    private static byte At(ReadOnlySpan<byte> buf, int i) => (uint)i < (uint)buf.Length ? buf[i] : (byte)0;

    /// <summary>The skip loop shared by the three routines: position of argument <paramref name="index"/>, or -1 when missing.</summary>
    private static int Skip(ReadOnlySpan<byte> buf, int p, int index)
    {
        if (index > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "the engine's argument index is an i16");
        }

        for (int i = 0; i < index; i++)
        {
            bool found = false;
            for (int k = 0; k < MaxSkipBytes; k++)
            {
                byte c = At(buf, p + k);
                if (c is (byte)')' or (byte)';')
                {
                    return -1;
                }

                if (c == (byte)',')
                {
                    p += k + 1;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                p += MaxSkipBytes;
            }
        }

        return p;
    }

    /// <summary>The value loop of VA 0x76690 / 0x76780 starting at <c>buf[p]</c>; the unbounded result (at most 16 bytes are read).</summary>
    private static long ReadValue(ReadOnlySpan<byte> buf, int p)
    {
        if (p < buf.Length && buf[p] == (byte)'`')
        {
            p += 5;
        }

        long total = 0;
        long num = 0;
        long sign = 1;
        for (int i = 0; i < MaxTermBytes; i++)
        {
            byte c = At(buf, p + i);
            if (c is (byte)')' or (byte)',' or (byte)';')
            {
                break;
            }

            if (c == (byte)'+')
            {
                total += sign * num;
                num = 0;
                sign = 1;
            }
            else if (c == (byte)'-')
            {
                total += sign * num;
                num = 0;
                sign = -1;
            }
            else if (c is >= (byte)'0' and <= (byte)'9')
            {
                num = (num * 10) + (c - '0');
            }
        }

        return total + (sign * num);
    }
}

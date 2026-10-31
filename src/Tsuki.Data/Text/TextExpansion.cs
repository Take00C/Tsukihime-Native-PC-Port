namespace Tsuki.Data.Text;

/// <summary>
/// The <c>$NNNNNN</c> expansion of a script message (engine routine VA 0x76040, docs/formats/text.md 5.3; a port of
/// <c>tsuki.text.expand_refs</c>).
/// </summary>
public static class TextExpansion
{
    /// <summary>Returns the engine's line for an id (the bytes <see cref="TextTable.EngineLine"/> gives).</summary>
    public delegate ReadOnlyMemory<byte> LineFetch(int id);

    /// <summary>
    /// Expands the references of <paramref name="message"/> against <paramref name="table"/> with the engine's cap of
    /// <see cref="TextRules.EngineExpandMax"/> bytes.
    /// </summary>
    /// <param name="message">The raw message bytes (the argument of a text command, markup intact).</param>
    /// <param name="table">The language table the references are read from.</param>
    /// <param name="srcLen">The source length passed to the routine; null is the whole message, 0 or less gives an empty result.</param>
    /// <param name="expand">False is the engine's mode 0: a plain copy of <paramref name="srcLen"/> bytes up to the first NUL.</param>
    /// <exception cref="ArgumentOutOfRangeException">A reference names an id beyond the table (the engine reads unchecked).</exception>
    public static byte[] Expand(ReadOnlySpan<byte> message, TextTable table, int? srcLen = null, bool expand = true)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Expand(message, table.EngineLine, TextRules.EngineExpandMax, srcLen, expand);
    }

    /// <summary>
    /// The routine with a caller-supplied line source and output cap. A <c>$</c> takes the 6 bytes after it as decimal digits <em>without
    /// checking them</em> (<c>id = sum((b - 0x30) * 10^k)</c>) and skips 7 bytes; a negative id inserts nothing, otherwise the line is
    /// appended, cut to the room left under <paramref name="limit"/>; any other byte is copied. The loop stops when the output reaches
    /// <paramref name="limit"/>, after copying a NUL, or when the source length is reached. Bytes past the end of the message read as NUL
    /// (the engine reads whatever follows in its buffer). The result ends at its first NUL.
    /// </summary>
    public static byte[] Expand(ReadOnlySpan<byte> message, LineFetch fetch, int limit = TextRules.EngineExpandMax, int? srcLen = null, bool expand = true)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        int n = srcLen ?? message.Length;
        if (!expand)
        {
            ReadOnlySpan<byte> copy = message[..Math.Clamp(n, 0, message.Length)];
            int z = copy.IndexOf((byte)0);
            return (z >= 0 ? copy[..z] : copy).ToArray();
        }

        if (n <= 0)
        {
            return [];
        }

        var output = new List<byte>(Math.Min(limit, 0x400));
        int i = 0;
        while (true)
        {
            int c = At(message, i);
            if (c == 0x24)
            {
                long value = 0;
                for (int k = 1; k <= 6; k++)
                {
                    value = (value * 10) + At(message, i + k);
                }

                value -= 0x30L * 111111;
                i += 6;
                if (value >= 0)
                {
                    ReadOnlyMemory<byte> line = fetch(checked((int)value));
                    int room = Math.Max(limit - output.Count, 0);
                    output.AddRange(line.Span[..Math.Min(room, line.Length)].ToArray());
                }
            }
            else
            {
                output.Add((byte)c);
            }

            if (output.Count >= limit || c == 0)
            {
                break;
            }

            i++;
            if (i >= n)
            {
                break;
            }
        }

        int nul = output.IndexOf(0);
        if (nul >= 0)
        {
            output.RemoveRange(nul, output.Count - nul);
        }

        return [.. output];
    }

    private static int At(ReadOnlySpan<byte> message, int k) => (uint)k < (uint)message.Length ? message[k] : 0;
}

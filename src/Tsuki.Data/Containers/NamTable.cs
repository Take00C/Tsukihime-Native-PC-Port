namespace Tsuki.Data.Containers;

/// <summary>
/// A name table: NAM32 (allpac*.nam, allscr.mrg section 0, se_nam.mrg sections) or NAM8
/// (voice.nam, voice2.nam) (docs/formats/containers.md 7). Record i names entry i.
/// </summary>
public sealed class NamTable
{
    private NamTable(int recordSize, string[] names, string[] tags, bool terminator, byte[] trailing)
    {
        RecordSize = recordSize;
        Names = names;
        Tags = tags;
        Terminator = terminator;
        Trailing = trailing;
    }

    /// <summary>32 (NAM32) or 8 (NAM8).</summary>
    public int RecordSize { get; }

    /// <summary>The names, one per record, without the terminator record.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>
    /// The optional 2-character tag at bytes 28..29 of a NAM32 record (se_nam.mrg sections 0, 1, 3),
    /// "" when there is none. Always "" for NAM8.
    /// </summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>True when an all-zero record ended the table.</summary>
    public bool Terminator { get; }

    /// <summary>
    /// Bytes after the last full record (NAM8: the 2 zero bytes), or everything from the all-zero record
    /// on when one ended the table.
    /// </summary>
    public ReadOnlyMemory<byte> Trailing { get; }

    /// <summary>Number of names.</summary>
    public int Count => Names.Count;

    /// <summary>Name of entry <paramref name="index"/>.</summary>
    public string this[int index] => Names[index];

    /// <summary>
    /// True if <paramref name="data"/> is a sequence of 32-byte CR LF terminated name records, optionally
    /// ending with one all-zero record.
    /// </summary>
    public static bool LooksLikeNam32(ReadOnlySpan<byte> data)
    {
        int n = data.Length;
        if (n == 0 || n % 32 != 0)
        {
            return false;
        }

        for (int off = 0; off < n; off += 32)
        {
            var rec = data.Slice(off, 32);
            if (rec[30] == (byte)'\r' && rec[31] == (byte)'\n')
            {
                continue;
            }

            if (off == n - 32 && IsAllZero(rec))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>Parses a .nam file or section, detecting NAM32 or NAM8.</summary>
    public static NamTable Parse(ReadOnlySpan<byte> data) =>
        LooksLikeNam32(data) ? ParseNam32(data) : ParseNam8(data);

    /// <summary>
    /// Parses a 32-byte-record table: a name (ASCII, ended by the first NUL inside bytes 0..29), an optional
    /// 2-byte tag at bytes 28..29, then CR LF. An all-zero record ends the table.
    /// </summary>
    /// <exception cref="ContainerFormatException">Size not a multiple of 32, missing CR LF or non-ASCII name.</exception>
    public static NamTable ParseNam32(ReadOnlySpan<byte> data)
    {
        if (data.Length % 32 != 0)
        {
            throw new ContainerFormatException($"NAM32 size {data.Length} is not a multiple of 32");
        }

        var names = new List<string>(data.Length / 32);
        var tags = new List<string>(data.Length / 32);
        bool terminator = false;
        byte[] trailing = [];
        for (int idx = 0, off = 0; off < data.Length; idx++, off += 32)
        {
            var rec = data.Slice(off, 32);
            if (IsAllZero(rec))
            {
                terminator = true;
                trailing = data[(off + 32)..].ToArray();
                break;
            }

            if (rec[30] != (byte)'\r' || rec[31] != (byte)'\n')
            {
                throw new ContainerFormatException(
                    $"NAM32 record {idx} at 0x{off:x} does not end with CRLF: {Convert.ToHexStringLower(rec)}");
            }

            var body = rec[..30];
            int nul = body.IndexOf((byte)0);
            var nameBytes = nul < 0 ? body : body[..nul];
            string tag = string.Empty;
            if (nul >= 0 && nul <= 28 && IsAllZero(body[nul..28]) && !IsAllZero(body[28..30]))
            {
                tag = DecodeAscii(body[28..30]);
            }

            names.Add(DecodeAscii(nameBytes));
            tags.Add(tag);
        }

        return new NamTable(32, names.ToArray(), tags.ToArray(), terminator, trailing);
    }

    /// <summary>Parses an 8-byte-record table (voice.nam, voice2.nam): an ASCII name, NUL padded.</summary>
    /// <exception cref="ContainerFormatException">A non-ASCII name.</exception>
    public static NamTable ParseNam8(ReadOnlySpan<byte> data)
    {
        int full = data.Length / 8 * 8;
        var names = new List<string>(full / 8);
        bool terminator = false;
        byte[] trailing = data[full..].ToArray();
        for (int off = 0; off < full; off += 8)
        {
            var rec = data.Slice(off, 8);
            if (IsAllZero(rec))
            {
                terminator = true;
                trailing = data[off..].ToArray();
                break;
            }

            int nul = rec.IndexOf((byte)0);
            names.Add(DecodeAscii(nul < 0 ? rec : rec[..nul]));
        }

        string[] tags = new string[names.Count];
        Array.Fill(tags, string.Empty);
        return new NamTable(8, names.ToArray(), tags, terminator, trailing);
    }

    private static bool IsAllZero(ReadOnlySpan<byte> span) => !span.ContainsAnyExcept((byte)0);

    private static string DecodeAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b >= 0x80)
            {
                throw new ContainerFormatException($"non-ASCII name bytes {Convert.ToHexStringLower(bytes)}");
            }
        }

        return System.Text.Encoding.ASCII.GetString(bytes);
    }
}

using System.Buffers.Binary;
using System.Text;
using Tsuki.Data.Containers;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Text;

/// <summary><c>script_text.mrg</c> or one of its tables does not follow the documented layout (docs/formats/text.md 2).</summary>
public sealed class TextFormatException : ContainerFormatException
{
    /// <summary>Creates the exception.</summary>
    public TextFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// One language of <c>script_text.mrg</c>: the offset table (big-endian u32; N string starts, then <c>blob_size</c> twice) and the
/// UTF-8 string blob in which every string ends with CR LF (docs/formats/text.md 2). A port of <c>tsuki.text.TextTable</c> and
/// <c>parse_offset_table</c>.
/// </summary>
/// <remarks>
/// Ids outside the table raise <see cref="ArgumentOutOfRangeException"/> where the Python reference raises <c>IndexError</c>
/// (the engine reads past the table unchecked, text.md 4; the port rejects it, RR 4.7). Offsets that point outside the blob give
/// the same answers as Python slicing (clamped, possibly empty), so a damaged table never throws from a read.
/// </remarks>
public sealed class TextTable
{
    private readonly uint[] _offsets;
    private readonly Lazy<ReadOnlyMemory<byte>> _blob;

    /// <summary>Creates a table over an already parsed offset list and a blob.</summary>
    /// <param name="language">The language this table belongs to.</param>
    /// <param name="offsets">The N + 2 entries before the end mark(s): N string starts, then the blob size twice.</param>
    /// <param name="blob">The string blob.</param>
    public TextTable(Language language, uint[] offsets, ReadOnlyMemory<byte> blob)
        : this(language, offsets, new Lazy<ReadOnlyMemory<byte>>(blob))
    {
    }

    internal TextTable(Language language, uint[] offsets, Lazy<ReadOnlyMemory<byte>> blob)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        if (offsets.Length < 2)
        {
            throw new TextFormatException("offset table shorter than the two blob_size guard entries");
        }

        Language = language;
        _offsets = offsets;
        _blob = blob;
    }

    /// <summary>The language of this table.</summary>
    public Language Language { get; }

    /// <summary>Number of string ids (the two trailing <c>blob_size</c> entries are guards).</summary>
    public int Count => _offsets.Length - 2;

    /// <summary>The entries before the end mark: N string starts, then <c>blob_size</c> twice.</summary>
    public ReadOnlySpan<uint> Offsets => _offsets;

    /// <summary>The string blob (loaded on first use when the table was opened from a file).</summary>
    public ReadOnlyMemory<byte> Blob => _blob.Value;

    /// <summary>
    /// Parses a big-endian offset table: the entries before the first 0xFFFFFFFF. Retail tables end with exactly one end mark, tables
    /// repacked by mangetsu with three; any number of end marks is accepted as long as only end marks follow the first one.
    /// </summary>
    /// <param name="data">The section bytes.</param>
    /// <param name="strict">Demand the retail form (exactly one end mark).</param>
    /// <param name="endMarks">The number of end marks found.</param>
    /// <exception cref="TextFormatException">The table breaks the layout.</exception>
    public static uint[] ParseOffsets(ReadOnlySpan<byte> data, bool strict, out int endMarks)
    {
        if (data.Length % 4 != 0)
        {
            throw new TextFormatException($"offset table size {data.Length} is not a multiple of 4");
        }

        int n = data.Length / 4;
        int end = -1;
        for (int i = 0; i < n; i++)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(data[(4 * i)..]) == TextRules.EndMark)
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            throw new TextFormatException("offset table has no 0xFFFFFFFF end mark");
        }

        for (int i = end; i < n; i++)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(data[(4 * i)..]) != TextRules.EndMark)
            {
                throw new TextFormatException($"non-end-mark entries after the 0xFFFFFFFF at index {end}");
            }
        }

        if (strict && end != n - 1)
        {
            throw new TextFormatException($"{n - end} end marks; the retail layout has exactly one");
        }

        if (end < 2)
        {
            throw new TextFormatException("offset table shorter than the two blob_size guard entries");
        }

        var result = new uint[end];
        for (int i = 0; i < end; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt32BigEndian(data[(4 * i)..]);
        }

        endMarks = n - end;
        return result;
    }

    /// <summary>Exact bytes of string <paramref name="id"/> including its trailing CR LF.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is not in 0..Count-1.</exception>
    public ReadOnlyMemory<byte> Raw(int id)
    {
        if ((uint)id >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, $"{TextRules.Code(Language)}: line id {id} out of range 0..{Count - 1}");
        }

        return PySlice(_blob.Value, _offsets[id], _offsets[id + 1]);
    }

    /// <summary>String <paramref name="id"/> without its CR LF (a string that does not end with CR LF is returned whole).</summary>
    public ReadOnlyMemory<byte> LineBytes(int id)
    {
        ReadOnlyMemory<byte> raw = Raw(id);
        return raw.Span.EndsWith("\r\n"u8) ? raw[..^2] : raw;
    }

    /// <summary>String <paramref name="id"/> without its CR LF, decoded as strict UTF-8.</summary>
    /// <exception cref="DecoderFallbackException">The line is not valid UTF-8.</exception>
    public string Line(int id) => StrictUtf8.GetString(LineBytes(id).Span);

    /// <summary>
    /// What the engine's copy routine (VA 0x75F80) produces for <paramref name="id"/>: nothing for a negative id; nothing when the entry
    /// is shorter than 3 bytes (the guard entries <c>Count</c> and <c>Count + 1</c> included), else <c>min(len, 0x301) - 2</c> bytes
    /// (text.md 4). Ids past the table raise.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> lies beyond the guard entries.</exception>
    public ReadOnlyMemory<byte> EngineLine(int id)
    {
        if (id < 0 || id == _offsets.Length - 1)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        if (id >= _offsets.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, $"{TextRules.Code(Language)}: id {id} beyond the table");
        }

        long n = (long)_offsets[id + 1] - _offsets[id];
        if (n < 3)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        long start = _offsets[id];
        return PySlice(_blob.Value, start, start + Math.Min(n, 0x301) - 2);
    }

    /// <summary>Deviations from the retail layout, one message each (empty for the retail data); a port of <c>TextTable.validate</c>.</summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        string lang = TextRules.Code(Language);
        uint[] offs = _offsets;
        ReadOnlySpan<byte> blob = _blob.Value.Span;
        long size = blob.Length;
        if (offs.Length < 2 || offs[^1] != size || offs[^2] != size)
        {
            issues.Add($"{lang}: table does not end with blob_size twice (last=({offs[^2]}, {offs[^1]}), blob={size})");
        }

        for (int i = 0; i < offs.Length - 1; i++)
        {
            if (offs[i + 1] < offs[i])
            {
                issues.Add($"{lang}: offsets decrease at {i}");
                break;
            }
        }

        if (offs.Length > 0 && offs[0] != 0)
        {
            issues.Add($"{lang}: first offset {offs[0]} != 0");
        }

        var badEnd = new List<int>();
        for (int i = 0; i < Count; i++)
        {
            long a = offs[i];
            long b = offs[i + 1];
            if (b - a < 2 || !PySlice(blob, b - 2, b).SequenceEqual("\r\n"u8))
            {
                badEnd.Add(i);
            }
        }

        if (badEnd.Count > 0)
        {
            issues.Add($"{lang}: {badEnd.Count} strings without CRLF, first [{string.Join(", ", badEnd.Take(5))}]");
        }

        if (CountByte(blob, 0x0D) != Count || CountByte(blob, 0x0A) != Count)
        {
            var inner = new List<int>();
            for (int i = 0; i < Count; i++)
            {
                ReadOnlySpan<byte> line = LineBytes(i).Span;
                if (line.Contains((byte)0x0D) || line.Contains((byte)0x0A))
                {
                    inner.Add(i);
                }
            }

            issues.Add($"{lang}: {inner.Count} strings contain CR/LF before the end, first [{string.Join(", ", inner.Take(5))}]");
        }

        try
        {
            _ = StrictUtf8.GetCharCount(blob);
        }
        catch (DecoderFallbackException e)
        {
            issues.Add($"{lang}: blob is not UTF-8: {e.Message}");
        }

        if (size != 0 && offs[Count] != size)
        {
            issues.Add($"{lang}: last string does not end at the blob end");
        }

        return issues;
    }

    internal static UTF8Encoding StrictUtf8 { get; } = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Python slicing <c>blob[a:b]</c> for non-negative bounds: clamped to the data, empty when <c>b &lt;= a</c>.</summary>
    internal static ReadOnlyMemory<byte> PySlice(ReadOnlyMemory<byte> data, long a, long b)
    {
        long len = data.Length;
        long start = Math.Min(a, len);
        long end = Math.Min(b, len);
        return end <= start ? ReadOnlyMemory<byte>.Empty : data.Slice((int)start, (int)(end - start));
    }

    internal static ReadOnlySpan<byte> PySlice(ReadOnlySpan<byte> data, long a, long b)
    {
        long len = data.Length;
        long start = Math.Min(a, len);
        long end = Math.Min(b, len);
        return end <= start ? default : data.Slice((int)start, (int)(end - start));
    }

    private static int CountByte(ReadOnlySpan<byte> data, byte value)
    {
        int n = 0;
        int i;
        while ((i = data.IndexOf(value)) >= 0)
        {
            n++;
            data = data[(i + 1)..];
        }

        return n;
    }
}

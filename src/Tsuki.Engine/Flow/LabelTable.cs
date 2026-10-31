using System.Text;

namespace Tsuki.Engine.Flow;

/// <summary>
/// The label table of one script (allscr section 1, child k; docs/formats/script.md 2.4): records of exactly 32 ASCII
/// bytes, <c>name</c> padded with spaces to 21 bytes, the 3 hex digits of the defining marker's block id, <c>,</c>, the 5
/// hex digits of the marker's byte offset, <c>\r\n</c>. <see cref="Find(ReadOnlySpan{byte})"/> is the engine's lookup (VA 0x794D0).
/// </summary>
public sealed class LabelTable
{
    /// <summary>Bytes per record.</summary>
    public const int RecordSize = 32;

    /// <summary>The lookup scans at most this many records.</summary>
    public const int MaxRecords = 1000;

    /// <summary>The wanted name is copied up to this many bytes.</summary>
    public const int MaxName = 32;

    private const int NameField = 21;
    private const int OffsetField = 25;

    private readonly byte[] records;

    /// <summary>Wraps raw label records (a multiple of 32 bytes; the content is not validated, as in the engine).</summary>
    public LabelTable(ReadOnlyMemory<byte> records)
    {
        if (records.Length % RecordSize != 0)
        {
            throw new ArgumentException($"a label table is a multiple of {RecordSize} bytes, not {records.Length}", nameof(records));
        }

        this.records = records.ToArray();
    }

    /// <summary>Number of records.</summary>
    public int Count => records.Length / RecordSize;

    /// <summary>The raw records.</summary>
    public ReadOnlySpan<byte> Records => records;

    /// <summary>Builds the 32-byte records of (name, block, offset) labels, e.g. from <c>tsuki.script.parse_labels</c> output.</summary>
    public static LabelTable FromLabels(IEnumerable<(string Name, int Block, int Offset)> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var b = new List<byte>();
        foreach ((string name, int block, int offset) in labels)
        {
            if (name.Length > NameField || block is < 0 or > 0xFFF || offset is < 0 or > 0xFFFFF)
            {
                throw new ArgumentException($"label {name} ({block}, {offset}) does not fit a record", nameof(labels));
            }

            b.AddRange(Encoding.ASCII.GetBytes(name.PadRight(NameField)));
            b.AddRange(Encoding.ASCII.GetBytes($"{block:x3},{offset:x5}\r\n"));
        }

        return new LabelTable(b.ToArray());
    }

    /// <summary>
    /// The engine's lookup (VA 0x794D0): the wanted name is cut at NUL, TAB, LF, CR or <c>.</c> (at most 32 bytes); the
    /// first of at most 1000 records whose name, compared up to its first byte &lt;= 0x20, equals it gives the 5 hex digits
    /// at +25. Returns the byte offset, or -1 when no record matches (the engine then reads garbage; no retail label is
    /// missing, script.md 2.4).
    /// </summary>
    public int Find(ReadOnlySpan<byte> wanted)
    {
        int n = 0;
        while (n < wanted.Length && n < MaxName && wanted[n] is not (0 or 0x09 or 0x0A or 0x0D or 0x2E))
        {
            n++;
        }

        ReadOnlySpan<byte> name = wanted[..n];
        int count = Math.Min(Count, MaxRecords);
        for (int r = 0; r < count; r++)
        {
            ReadOnlySpan<byte> rec = records.AsSpan(r * RecordSize, RecordSize);
            int len = 0;
            while (len < NameField && rec[len] > 0x20)
            {
                len++;
            }

            if (rec[..len].SequenceEqual(name))
            {
                return ParseOffset(rec.Slice(OffsetField, 5));
            }
        }

        return -1;
    }

    /// <summary>Looks up an ASCII name.</summary>
    public int Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Find(Encoding.ASCII.GetBytes(name));
    }

    private static int ParseOffset(ReadOnlySpan<byte> hex)
    {
        int v = 0;
        foreach (byte c in hex)
        {
            v = (v * 16) + (c < (byte)'a' ? c - '0' : c - 'a' + 10);
        }

        return v;
    }
}

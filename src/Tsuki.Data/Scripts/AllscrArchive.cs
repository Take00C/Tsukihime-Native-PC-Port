using System.Buffers.Binary;
using System.Text;
using Tsuki.Data.Compression;
using Tsuki.Data.Containers;
using Tsuki.Data.IO;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Flow;
using Tsuki.Engine.Vm;

namespace Tsuki.Data.Scripts;

/// <summary>One record of a label table (docs/formats/script.md 2.4).</summary>
/// <param name="Name">The label name (the 21-byte field without its trailing spaces).</param>
/// <param name="Block">The block id of the <c>_ZZ</c> / <c>_ZY</c> marker that defines it.</param>
/// <param name="Offset">The byte offset of that marker in the decoded script.</param>
public sealed record ScriptLabel(string Name, int Block, int Offset);

/// <summary>
/// <c>image/allscr.mrg</c>: a standalone mrgd00 container of 552 sections (docs/formats/script.md 2): section 0 the NAM32 script names,
/// section 1 a nested container with one label table per script, section 2 an unreferenced picture, and sections 3.. the scripts
/// (MZX0, then XOR 0xFF). The names and the label tables (a few hundred KB) are read when the file is opened; a script is read and
/// decoded on request, so no more than one script has to be resident (a port of <c>tsuki.script.AllScr</c>).
/// </summary>
public sealed class AllscrArchive
{
    /// <summary>The first script section: script k is section k + 3 (VA 0x7917A).</summary>
    public const int ScriptSectionBase = 3;

    /// <summary>Bytes of a label record.</summary>
    public const int LabelRecordSize = LabelTable.RecordSize;

    private const string DefaultPath = "image/allscr.mrg";

    private readonly Func<int, byte[]> _readSection;
    private readonly MzpContainer _labels;

    private AllscrArchive(Func<int, byte[]> readSection, NamTable names, MzpContainer labels)
    {
        _readSection = readSection;
        NameTable = names;
        _labels = labels;
        Names = names.Names;
    }

    /// <summary>Number of scripts (549 in the retail data).</summary>
    public int Count => Names.Count;

    /// <summary>The script names, by script index (section 0).</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The parsed name table (section 0).</summary>
    public NamTable NameTable { get; }

    /// <summary>Opens <c>image/allscr.mrg</c> of a game folder view.</summary>
    /// <exception cref="ContainerFormatException">The file does not follow the layout.</exception>
    public static AllscrArchive Open(IGameFiles files, string path = DefaultPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        long length = files.Length(path);
        byte[] top = files.ReadRange(path, 0, (int)Math.Min(length, 8));
        if (!MzpContainer.IsMzp(top))
        {
            throw new ContainerFormatException($"{path}: not an mrgd00 container");
        }

        int tableBytes = 8 + (8 * BinaryPrimitives.ReadUInt16LittleEndian(top.AsSpan(6)));
        MzpSection[] sections = MzpContainer.ReadTable(files.ReadRange(path, 0, (int)Math.Min(length, tableBytes)), length);
        return Create(sections, i => files.ReadRange(path, sections[i].Offset, sections[i].Size));
    }

    /// <summary>Opens a container that is already in memory.</summary>
    public static AllscrArchive FromMemory(ReadOnlyMemory<byte> file)
    {
        MzpContainer container = MzpContainer.Parse(file);
        return Create([.. container.Sections], i => container.Section(i).ToArray());
    }

    /// <summary>The decoded script k: the MZX0 stream of section k + 3 decoded and XORed with 0xFF (docs/formats/script.md 2.2).</summary>
    public ReadOnlyMemory<byte> ScriptBytes(int k)
    {
        CheckIndex(k);
        byte[] stream = _readSection(k + ScriptSectionBase);
        int size = Mzx.DeclaredSize(stream);
        byte[] output = new byte[Mzx.BufferSize(size)];
        Mzx.Decode(stream, output, invert: true);
        return output.AsMemory(0, size);
    }

    /// <summary>The decoded script k with its segment offsets, as <c>IGameData.Script</c> returns it.</summary>
    public ScriptImage Script(int k) => ScriptSegmenter.CreateImage(k, ScriptBytes(k));

    /// <summary>The raw records of the label table of script k (a multiple of 32 bytes, no header, no terminator).</summary>
    public ReadOnlyMemory<byte> LabelRecords(int k)
    {
        CheckIndex(k);
        return _labels.Section(k);
    }

    /// <summary>The label table of script k as the engine's lookup (VA 0x794D0) sees it.</summary>
    public LabelTable LabelLookup(int k) => new(LabelRecords(k));

    /// <summary>The labels of script k, parsed and checked (<c>tsuki.script.parse_labels</c>).</summary>
    /// <exception cref="ContainerFormatException">A record does not follow the layout.</exception>
    public IReadOnlyList<ScriptLabel> Labels(int k) => ParseLabels(LabelRecords(k).Span);

    /// <summary>Parses a label table: 32-byte records <c>name(21, space padded) block(3 hex) , offset(5 hex) CR LF</c>.</summary>
    public static IReadOnlyList<ScriptLabel> ParseLabels(ReadOnlySpan<byte> data)
    {
        if (data.Length % LabelRecordSize != 0)
        {
            throw new ContainerFormatException($"label table size {data.Length} is not a multiple of {LabelRecordSize}");
        }

        var labels = new List<ScriptLabel>(data.Length / LabelRecordSize);
        for (int i = 0; i < data.Length; i += LabelRecordSize)
        {
            ReadOnlySpan<byte> r = data.Slice(i, LabelRecordSize);
            if (r[24] != (byte)',' || r[30] != (byte)'\r' || r[31] != (byte)'\n')
            {
                throw new ContainerFormatException($"bad label record {i / LabelRecordSize}: {Convert.ToHexStringLower(r)}");
            }

            labels.Add(new ScriptLabel(
                AsciiName(r[..21]), ParseHex(r.Slice(21, 3), i / LabelRecordSize), ParseHex(r.Slice(25, 5), i / LabelRecordSize)));
        }

        return labels;
    }

    private static AllscrArchive Create(MzpSection[] sections, Func<int, byte[]> readSection)
    {
        if (sections.Length < ScriptSectionBase)
        {
            throw new ContainerFormatException($"allscr.mrg has {sections.Length} sections; names, label tables and scripts need at least {ScriptSectionBase}");
        }

        NamTable names = NamTable.ParseNam32(readSection(0));
        MzpContainer labels = MzpContainer.Parse(readSection(1));
        if (names.Count + ScriptSectionBase != sections.Length || labels.Count != names.Count)
        {
            throw new ContainerFormatException(
                $"allscr.mrg layout: {names.Count} names, {labels.Count} label tables and {sections.Length - ScriptSectionBase} scripts do not line up");
        }

        return new AllscrArchive(readSection, names, labels);
    }

    private static string AsciiName(ReadOnlySpan<byte> field)
    {
        var sb = new StringBuilder(field.Length);
        foreach (byte c in field)
        {
            if (c >= 0x80)
            {
                throw new ContainerFormatException($"non-ASCII byte 0x{c:x2} in a label name");
            }

            sb.Append((char)c);
        }

        return sb.ToString().TrimEnd(' ');
    }

    private static int ParseHex(ReadOnlySpan<byte> digits, int record)
    {
        int v = 0;
        foreach (byte c in digits)
        {
            int d = c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - '0',
                >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                _ => throw new ContainerFormatException($"bad hex digit 0x{c:x2} in label record {record}"),
            };
            v = (v * 16) + d;
        }

        return v;
    }

    private void CheckIndex(int k)
    {
        if ((uint)k >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k, $"script index out of range 0..{Count - 1}");
        }
    }
}

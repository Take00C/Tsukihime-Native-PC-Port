using Tsuki.Data.Containers;
using Tsuki.Data.IO;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Text;

/// <summary>
/// <c>script_text.mrg</c>: an mrgd00 container with 10 sections, an offset table and a string blob for each of ja, en, zc, zt and
/// ko (docs/formats/text.md 2). The five offset tables (176 KB each) are read when the file is opened and checked to agree on the
/// line count; a language's blob (0.2 .. 3.4 MB) is read on first use, so a running game holds only the current language pair
/// (RR 3.1). A port of <c>tsuki.text.ScriptText</c>.
/// </summary>
public sealed class ScriptText
{
    private const string DefaultPath = "image/script_text.mrg";

    private readonly TextTable[] _tables;
    private readonly Func<IReadOnlyList<string>> _containerIssues;

    private ScriptText(TextTable[] tables, int[] endMarks, Func<IReadOnlyList<string>> containerIssues)
    {
        _tables = tables;
        EndMarks = endMarks;
        _containerIssues = containerIssues;
        Count = tables[0].Count;
    }

    /// <summary>Lines per language (43,969 in the retail data); all five languages have the same number.</summary>
    public int Count { get; }

    /// <summary>Number of 0xFFFFFFFF end marks of each language's offset table (1 in the retail data), by language index.</summary>
    public IReadOnlyList<int> EndMarks { get; }

    /// <summary>Opens <c>image/script_text.mrg</c> of a game folder view and reads the five offset tables.</summary>
    /// <exception cref="ContainerFormatException">The file does not follow the layout.</exception>
    public static ScriptText Open(IGameFiles files, string path = DefaultPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        long length = files.Length(path);
        byte[] top = files.ReadRange(path, 0, (int)Math.Min(length, 8));
        if (!MzpContainer.IsMzp(top))
        {
            throw new TextFormatException($"{path}: not an mrgd00 container");
        }

        int tableBytes = 8 + (8 * System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(top.AsSpan(6)));
        byte[] head = files.ReadRange(path, 0, (int)Math.Min(length, tableBytes));
        MzpSection[] sections = MzpContainer.ReadTable(head, length);
        CheckSectionCount(sections.Length);
        return Create(
            sections,
            i => files.ReadRange(path, sections[i].Offset, sections[i].Size),
            i => new Lazy<ReadOnlyMemory<byte>>(() => files.ReadRange(path, sections[i].Offset, sections[i].Size)),
            () => MzpContainer.Parse(files.ReadAll(path)).Validate());
    }

    /// <summary>Opens a container that is already in memory (tests, the golden slice).</summary>
    public static ScriptText FromMemory(ReadOnlyMemory<byte> file)
    {
        MzpContainer container = MzpContainer.Parse(file);
        CheckSectionCount(container.Count);
        return Create(
            [.. container.Sections],
            i => container.Section(i).ToArray(),
            i => new Lazy<ReadOnlyMemory<byte>>(container.Section(i)),
            () => container.Validate());
    }

    /// <summary>The table of one language.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not one of the five languages.</exception>
    public TextTable Table(Language lang)
    {
        _ = TextRules.Code(lang);
        return _tables[(int)lang];
    }

    /// <summary>The raw line, CR LF included (text.md 2).</summary>
    public ReadOnlyMemory<byte> Raw(Language lang, int id) => Table(lang).Raw(id);

    /// <summary>The line without its CR LF, markup intact, <c>$</c> references not expanded.</summary>
    public ReadOnlyMemory<byte> LineBytes(Language lang, int id) => Table(lang).LineBytes(id);

    /// <summary>The line as the engine's copy routine yields it (VA 0x75F80); this is what <c>IGameData.TextLine</c> returns.</summary>
    public ReadOnlyMemory<byte> EngineLine(Language lang, int id) => Table(lang).EngineLine(id);

    /// <summary>The line without its CR LF as a string (strict UTF-8).</summary>
    public string Line(Language lang, int id) => Table(lang).Line(id);

    /// <summary>
    /// Engine-exact <c>$NNNNNN</c> expansion (VA 0x76040) of a message in <paramref name="lang"/>; see
    /// <see cref="TextExpansion.Expand(ReadOnlySpan{byte}, TextTable, int?, bool)"/>.
    /// </summary>
    public byte[] ExpandBytes(ReadOnlySpan<byte> message, Language lang, int? srcLen = null) =>
        TextExpansion.Expand(message, Table(lang), srcLen);

    /// <summary>
    /// Every deviation from the retail layout, container and tables (empty for the retail file). Reads the whole file once more, and
    /// every blob.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>(_containerIssues());
        foreach (TextTable t in _tables)
        {
            issues.AddRange(t.Validate());
        }

        for (int i = 0; i < EndMarks.Count; i++)
        {
            if (EndMarks[i] != 1)
            {
                issues.Add($"{TextRules.Code((Language)i)}: {EndMarks[i]} 0xFFFFFFFF end marks (retail has 1)");
            }
        }

        return issues;
    }

    private static void CheckSectionCount(int count)
    {
        if (count != TextRules.SectionCount)
        {
            throw new TextFormatException($"expected {TextRules.SectionCount} sections, found {count}");
        }
    }

    private static ScriptText Create(
        MzpSection[] sections,
        Func<int, byte[]> readSection,
        Func<int, Lazy<ReadOnlyMemory<byte>>> blob,
        Func<IReadOnlyList<string>> containerIssues)
    {
        _ = sections;
        var tables = new TextTable[5];
        var endMarks = new int[5];
        for (int k = 0; k < 5; k++)
        {
            uint[] offsets = TextTable.ParseOffsets(readSection(2 * k), strict: false, out int marks);
            endMarks[k] = marks;
            tables[k] = new TextTable((Language)k, offsets, blob((2 * k) + 1));
        }

        int count = tables[0].Count;
        for (int k = 1; k < 5; k++)
        {
            if (tables[k].Count != count)
            {
                throw new TextFormatException(
                    "languages have different line counts: " + string.Join(", ", tables.Select((t, i) => $"{TextRules.Code((Language)i)}: {t.Count}")));
            }
        }

        return new ScriptText(tables, endMarks, containerIssues);
    }
}

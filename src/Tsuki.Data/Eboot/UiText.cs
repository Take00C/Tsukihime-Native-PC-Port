using System.Text;

namespace Tsuki.Data.Eboot;

/// <summary>One entry of the UI string table.</summary>
/// <param name="Lang">Language code: <c>ja en zc zt ko</c>.</param>
/// <param name="Id">String id; 0 is a placeholder.</param>
/// <param name="Va">VA of the NUL-terminated UTF-8 string.</param>
/// <param name="Text">The decoded string; <c>^</c> is the line-break marker of the text renderer.</param>
public sealed record UiString(string Lang, int Id, long Va, string Text);

/// <summary>
/// The system / menu string table of eboot.bin: <c>ui_text[5][1499]</c> at RELRO VA 0x41D5F0 (RR 3.2, engine.md 9).
/// A port of <c>tools/eboot/uitext.py</c>. Language index 0 ja, 1 en, 2 zc, 3 zt, 4 ko; ids 1..1498 are strings and id
/// 0 is a placeholder (<c>NULL</c>) in every language.
/// </summary>
public sealed class UiTextTable
{
    /// <summary>Entries per language (0x5DB); valid ids are 1..1498.</summary>
    public const int Count = 1499;

    /// <summary>The language codes in table order.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["ja", "en", "zc", "zt", "ko"];

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly UiString[][] _rows;

    private UiTextTable(UiString[][] rows)
    {
        _rows = rows;
    }

    /// <summary>The 1,499 entries of one language, indexed by id.</summary>
    public IReadOnlyList<UiString> Rows(int language) => _rows[language];

    /// <summary>
    /// Reads all 5 x 1,499 entries. Every pointer must equal the addend of its R_X86_64_RELATIVE relocation, and every
    /// string must be valid UTF-8.
    /// </summary>
    /// <exception cref="EbootFormatException">A pointer disagrees with its relocation, or a string is not UTF-8.</exception>
    public static UiTextTable Read(SelfImage image) => Read(new EbootReader(image));

    /// <summary>Reads the table where <see cref="EbootReader.Layout"/> puts it (package K25).</summary>
    /// <exception cref="EbootFormatException">A pointer disagrees with its relocation, or a string is not UTF-8.</exception>
    public static UiTextTable Read(EbootReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        SelfImage image = reader.Image;
        long tableVa = reader.Layout.UiText;
        int count = reader.Layout.UiTextCount;
        var rel = new Dictionary<ulong, long>();
        foreach (var r in image.Rela())
        {
            if (r.Type == SelfImage.RX86Relative)
            {
                rel[r.Offset] = r.Addend;
            }
        }

        int langs = Languages.Count;
        byte[] raw = image.ReadVa((ulong)tableVa, 8 * count * langs);
        var rows = new UiString[langs][];
        for (int li = 0; li < langs; li++)
        {
            var list = new UiString[count];
            for (int i = 0; i < count; i++)
            {
                ulong slot = (ulong)tableVa + (ulong)(8 * ((li * count) + i));
                ulong ptr = BitConverter.ToUInt64(raw, 8 * ((li * count) + i));
                if (!rel.TryGetValue(slot, out long addend) || (ulong)addend != ptr)
                {
                    string relocation = rel.TryGetValue(slot, out long a) ? a.ToString(System.Globalization.CultureInfo.InvariantCulture) : "None";
                    throw new EbootFormatException(
                        $"ui_text[{Languages[li]}][{i}] at 0x{slot:x}: file value 0x{ptr:x}, relocation {relocation}");
                }

                byte[] bytes = image.CString(ptr);
                string text;
                try
                {
                    text = StrictUtf8.GetString(bytes);
                }
                catch (DecoderFallbackException e)
                {
                    throw new EbootFormatException($"ui_text[{Languages[li]}][{i}] at 0x{ptr:x}: {e.Message}");
                }

                list[i] = new UiString(Languages[li], i, (long)ptr, text);
            }

            rows[li] = list;
        }

        return new UiTextTable(rows);
    }

    /// <summary>
    /// <c>UiText_Get(id)</c> (text VA 0xC29B0): <c>null</c> unless <c>1 &lt;= id &lt;= 0x5DA</c> and
    /// <c>language * 0x5DB + id &lt;= 0x1D47</c> (an unsigned compare, so a negative language gives null too).
    /// </summary>
    public string? Get(int language, int id)
    {
        if (id < 1 || id > 0x5DA)
        {
            return null;
        }

        long i = ((long)language * 0x5DB) + id;
        if ((ulong)i > 0x1D47)
        {
            return null;
        }

        return id < _rows[language].Length ? _rows[language][id].Text : null;
    }

    /// <summary>
    /// Escapes a string for the TSV export of <c>python -m eboot.uitext</c>: backslash, TAB, LF and CR (none of the
    /// last three occur in v01.01).
    /// </summary>
    public static string Escape(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal);

    /// <summary>The file <c>ui_text_&lt;lang&gt;.tsv</c> of the Python tool: a header, then <c>id</c> TAB hex VA TAB text per line, LF endings.</summary>
    public string ToTsv(int language)
    {
        var sb = new StringBuilder("id\tva\ttext\n");
        foreach (var u in _rows[language])
        {
            sb.Append(u.Id).Append('\t').Append(u.Va.ToString("x8", System.Globalization.CultureInfo.InvariantCulture))
                .Append('\t').Append(Escape(u.Text)).Append('\n');
        }

        return sb.ToString();
    }
}

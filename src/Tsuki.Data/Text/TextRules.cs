using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Text;

/// <summary>
/// The constants and the small pure rules of <c>script_text.mrg</c> and its markup (docs/formats/text.md 2-6; a port of the
/// top of <c>tools/tsuki/text.py</c>). Nothing here reads a file.
/// </summary>
public static class TextRules
{
    /// <summary>Sections of <c>script_text.mrg</c>: an offset table and a string blob per language (text.md 2.1).</summary>
    public const int SectionCount = 10;

    /// <summary>The end mark of an offset table.</summary>
    public const uint EndMark = 0xFFFFFFFF;

    /// <summary>Bytes of one line the engine copies: 0x301 minus the CR LF (VA 0x75F80).</summary>
    public const int EngineLineMax = 0x2FF;

    /// <summary>Output cap of the <c>$</c> expansion (VA 0x76040 stops once the output exceeds 0x2FB bytes).</summary>
    public const int EngineExpandMax = 0x2FC;

    /// <summary>Copy cap of a text command's argument (VA 0x92458) and the <c>src_len</c> passed to the expansion.</summary>
    public const int MessageArgMax = 0x300;

    /// <summary>Choice slot k &lt; 9 gets the number line 43960 + k (<c>1．</c>..<c>9．</c>).</summary>
    public const int ChoiceNumberBase = 43960;

    /// <summary>Choice slot k &gt;= 9 gets line 43959 (<c>０．</c>).</summary>
    public const int ChoiceNumberOverflow = 43959;

    /// <summary>Bytes per choice slot (VA 0x174FB70 + k * 0xFA).</summary>
    public const int ChoiceSlotSize = 0xFA;

    /// <summary>The shipped value of the language option word (VA 0x66C59C).</summary>
    public const int LanguageOptionUnset = 0xFFFF;

    /// <summary>Lines per language in the retail data.</summary>
    public const int RetailLineCount = 43_969;

    /// <summary>Language codes by engine index (the suffix table at VA 0x42C030).</summary>
    public static IReadOnlyList<string> LanguageCodes { get; } = ["ja", "en", "zc", "zt", "ko"];

    /// <summary>The four languages the options screen can select (text.md 3).</summary>
    public static IReadOnlyList<Language> SelectableLanguages { get; } = [Language.Ja, Language.En, Language.Zc, Language.Zt];

    /// <summary>The 39 inline icon names in engine order (table at VA 0x2D3580, 39 x 11 bytes; text.md 6.4).</summary>
    public static IReadOnlyList<string> IconNames { get; } =
    [
        "heart", "heart2", "star", "star2", "edia", "dummy", "onpu",
        "ber00", "ber01", "ber02", "ber03", "ber04", "ber05", "ber06", "ber07", "ber08", "ber09", "ber10",
        "ber11", "ber12", "ber13", "ber14", "ber15", "ber16", "ber17", "ber18", "ber19", "ber20",
        "ber21", "ber22", "ber23", "ber24", "ber25", "ber26", "ber27", "ber28", "ber29", "ber30",
        "zap00",
    ];

    /// <summary><c>@c1</c>..<c>@c5</c> colour words (u16 table at VA 0x2D34C0; entry 0 is unused, <c>@c0</c> is the default).</summary>
    public static IReadOnlyList<int> ColorRgb565 { get; } = [0, 0xF800, 0x345F, 0x1082, 0xAD55, 0x001F];

    /// <summary>
    /// <c>@hN</c> / <c>@wN</c> multiply the base glyph height / width by <c>ScaleTable[N]</c> (floats at VA 0x2D3730, widened to double
    /// as the Python table holds them).
    /// </summary>
    public static IReadOnlyList<double> ScaleTable { get; } =
    [
        0.34375, 0.3723958432674408, 0.4010416567325592, 0.4296875, 0.4583333432674408,
        0.4869791567325592, 0.2291666716337204, 0.2578125, 0.2864583134651184, 0.3151041865348816,
    ];

    /// <summary>The language code (<c>ja</c>, <c>en</c>, <c>zc</c>, <c>zt</c>, <c>ko</c>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not one of the five languages.</exception>
    public static string Code(Language lang)
    {
        int i = (int)lang;
        return (uint)i < (uint)LanguageCodes.Count
            ? LanguageCodes[i]
            : throw new ArgumentOutOfRangeException(nameof(lang), lang, "language index out of range 0..4");
    }

    /// <summary>Parses a language code.</summary>
    /// <exception cref="ArgumentException">Unknown code.</exception>
    public static Language Parse(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        for (int i = 0; i < LanguageCodes.Count; i++)
        {
            if (string.Equals(LanguageCodes[i], code, StringComparison.Ordinal))
            {
                return (Language)i;
            }
        }

        throw new ArgumentException($"unknown language \"{code}\"; expected one of {string.Join(", ", LanguageCodes)}", nameof(code));
    }

    /// <summary>The engine parses <c>&lt;base|ruby&gt;</c> for every layout language except English (VA 0xC6D58, text.md 3).</summary>
    public static bool RubyEnabled(Language lang)
    {
        _ = Code(lang);
        return lang != Language.En;
    }

    /// <summary>
    /// <c>sceSystemServiceParamGetInt(LANG)</c> to a text language (VA 0x407F0 and the fallback of 0x3ED90): 0 is ja, 11 is zc, 10 is zt, any
    /// other value and a failed call (null) are English.
    /// </summary>
    public static int SystemLanguageToText(int? systemLanguage) => systemLanguage switch
    {
        0 => 0,
        11 => 2,
        10 => 3,
        _ => 1,
    };

    /// <summary>
    /// (language used by the <c>$</c> expansion, language used by the layout) for a stored option word (u16 at VA 0x66C59C; text.md 3).
    /// A negative word (shipped default 0xFFFF) is replaced by the system-language mapping at load; the expansion then uses the signed
    /// word as it is, the layout only 0..3.
    /// </summary>
    public static (int Expansion, int Layout) EffectiveLanguages(int optionWord, int? systemLanguage)
    {
        int w = optionWord & 0xFFFF;
        int signed = (w & 0x8000) != 0 ? w - 0x10000 : w;
        if (signed < 0)
        {
            signed = SystemLanguageToText(systemLanguage);
        }

        int layout = signed is >= 0 and < 4 ? signed : SystemLanguageToText(systemLanguage);
        return (signed, layout);
    }

    /// <summary>
    /// Line id of the number the choice handler (VA 0x993E0) puts in front of choice slot <paramref name="slot"/> (the count of
    /// <c>_SELR</c> commands executed for the current menu, not the <c>_SELR</c> argument; text.md 5.2).
    /// </summary>
    public static int ChoiceNumberId(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        return slot < 9 ? ChoiceNumberBase + slot : ChoiceNumberOverflow;
    }

    /// <summary>
    /// Contents of choice slot <paramref name="slot"/> before expansion: <c>$0439NN</c> followed by the <c>_SELR</c> message
    /// (VA 0x99427..0x994E5), as UTF-8. It is expanded with a source length of <see cref="ChoiceSlotSize"/> when shown.
    /// </summary>
    public static byte[] ChoiceSlotText(int slot, ReadOnlySpan<byte> message)
    {
        string prefix = "$" + ChoiceNumberId(slot).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        byte[] result = new byte[prefix.Length + message.Length];
        System.Text.Encoding.ASCII.GetBytes(prefix, result);
        message.CopyTo(result.AsSpan(prefix.Length));
        return result;
    }
}

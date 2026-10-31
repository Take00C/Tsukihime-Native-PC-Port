// The game's name in each language, for the window title (package K20): read from the user's own sce_sys/param.sfo, whose TITLE_xx keys carry the localized titles
// under the PS4's language numbers, with the names of the analysed copy as the fallback when the file or the key is missing.
using Tsuki.Engine.Contracts;

namespace Tsuki.Data;

/// <summary>The localized titles of the game.</summary>
public static class GameTitles
{
    /// <summary>The name of the port as the player sees it in the programs, dialogs and shortcuts: never the short project name.</summary>
    public const string ProductName = "Tsukihime";

    /// <summary>
    /// The param.sfo key of a language: <c>TITLE_00</c> Japanese, <c>TITLE_01</c> English (US), <c>TITLE_11</c> Simplified Chinese, <c>TITLE_10</c> Traditional Chinese (the PS4's
    /// language numbers; the retail file has 00, 10 and 11 and leaves English to the plain <c>TITLE</c>). Korean has no selectable UI; it maps to the English key.
    /// </summary>
    public static string Key(Language language) => language switch
    {
        Language.Ja => "TITLE_00",
        Language.Zc => "TITLE_11",
        Language.Zt => "TITLE_10",
        _ => "TITLE_01",
    };

    /// <summary>The title of the analysed copy (CUSA47999 01.01) when param.sfo cannot be read: the same words the file holds.</summary>
    public static string Fallback(Language language) => language switch
    {
        Language.Ja => "月姫 -A piece of blue glass moon-",
        Language.Zc or Language.Zt => "月姬 -A piece of blue glass moon-",
        _ => "TSUKIHIME -A piece of blue glass moon-",
    };

    /// <summary>
    /// The title for <paramref name="language"/>: the file's <see cref="Key"/> entry, else the file's plain <c>TITLE</c> (for English only: the other languages fall back to
    /// their own words rather than to the English line), else <see cref="Fallback"/>. Blank values count as missing.
    /// </summary>
    public static string Of(ParamSfo? sfo, Language language)
    {
        string? exact = sfo?[Key(language)];
        if (!string.IsNullOrWhiteSpace(exact))
        {
            return exact.Trim();
        }

        if (language is not (Language.Ja or Language.Zc or Language.Zt))
        {
            string? plain = sfo?["TITLE"];
            if (!string.IsNullOrWhiteSpace(plain))
            {
                return plain.Trim();
            }
        }

        return Fallback(language);
    }

    /// <summary>The title of a game folder: its <c>sce_sys/param.sfo</c> read now (null folder: the fallback).</summary>
    public static string ForFolder(string? gameFolder, Language language) =>
        Of(gameFolder is null ? null : ParamSfo.TryRead(Path.Combine(gameFolder, "sce_sys", "param.sfo")), language);
}

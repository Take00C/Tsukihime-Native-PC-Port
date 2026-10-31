using Tsuki.Data.Catalog;

namespace Tsuki.Data.Census;

/// <summary>
/// What the census may leave unresolved, with the document that says so. Anything else that does not resolve is a failure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Five picture ids that are script bugs</b> (docs/formats/archives_ui_movies.md 2.4; FORMATS 5.2 "10,967 effective names, 10,962
/// resolve, 5 script bugs"): four <c>_STCH</c> names that are in no name list, and one <c>_SCH2</c> whose suffix is inherited from the layer
/// it takes over. The engine gets index -1 for them and presumably shows nothing.
/// </para>
/// <para>
/// <b>Three sprite bodies that are on no disk</b> (archives_ui_movies.md 3.3; FORMATS 5.2 "3 bodies missing on disk"): the paired records
/// of <c>ARU_M_03_35_N00_B</c>, <c>ARU_S_03_35_N00_B</c> and <c>NOE_L_60_03_N00_B</c> point at a body file that exists in no archive; the
/// engine skips that layer and draws the face. Of the three only <c>NOE_L_60_03_N01</c> is reached by a script (one <c>_SCH2</c>).
/// </para>
/// <para>
/// Not part of the T0 list of PACE row 10 but documented in the same places: the motion id 0 of 92 <c>_STMA</c> / <c>_SGMA</c> statements (no
/// motion) and the one motion id past the table (script.md, doc_checks item 3), and four characters of the Chinese texts that the fonts have
/// no glyph for (<see cref="GlyphFallbacks"/>, fonts.md 5.4 and 9).
/// </para>
/// </remarks>
public static class CensusAllowList
{
    /// <summary>Picture keys that name no picture, by script bug.</summary>
    public static IReadOnlyDictionary<string, string> PictureScriptBugs { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["IMG5007_02_02"] = "script bug: _STCH names a variant that is not in the picture list (archives_ui_movies.md 2.4)",
        ["IMG5007_00_27"] = "script bug: _STCH names a variant that is not in the picture list (archives_ui_movies.md 2.4)",
        ["IMG1637_28_28"] = "script bug: _STCH names a variant that is not in the picture list (archives_ui_movies.md 2.4)",
        ["Z20_M_61_00_A00_10_00"] = "script bug: _STCH names a sprite variant that is not in the picture list (archives_ui_movies.md 2.4)",
        ["SIK_A_32_00_N00_02_30"] = "script bug: _SCH2 takes the variant suffix of the layer it takes over and the result is not in the picture list (FORMATS 5.2)",
    };

    /// <summary>The body files that no archive holds (without the <c>.MZP</c> extension).</summary>
    public static IReadOnlySet<string> MissingSpriteBodies { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "ARU_M_03_35_N00_B", "ARU_S_03_35_N00_B", "NOE_L_60_03_N00_B" };

    /// <summary>Why an unresolved picture is excused, or null.</summary>
    public static string? ImageReason(ResolvedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.Found)
        {
            return PictureScriptBugs.GetValueOrDefault(image.Key);
        }

        bool any = false;
        foreach ((string? name, ImageId? id) in new[] { (image.File, image.FileId), (image.Overlay, image.OverlayId) })
        {
            if (name is null || id is not null)
            {
                continue;
            }

            any = true;
            string stem = name.EndsWith(".MZP", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            if (!MissingSpriteBodies.Contains(stem.ToUpperInvariant()))
            {
                return null;
            }
        }

        return any ? "a sprite body that exists in no archive; the engine skips that layer (archives_ui_movies.md 3.3)" : null;
    }

    /// <summary>
    /// The characters of the Chinese scripts that have no glyph lookup entry, as the engine's key (the UTF-8 bytes in hex) and the number of
    /// uses; the engine draws the blank fallback glyph for them (docs/formats/fonts.md 5.4 and 9: zc <c>ｂ</c> x3, <c>ａ</c> x2, <c>鲽</c> x1; zt <c>鰈</c> x1).
    /// </summary>
    public static IReadOnlyDictionary<(string Language, string Key), int> GlyphFallbacks { get; } = new Dictionary<(string, string), int>
    {
        [("zc", "efbd82")] = 3,
        [("zc", "efbd81")] = 2,
        [("zc", "e9b2bd")] = 1,
        [("zt", "e9b088")] = 1,
    };

    /// <summary>Why a character without a glyph is excused (exactly the documented characters, used as often as documented), or null.</summary>
    public static string? GlyphFallbackReason(string language, string key, int uses) =>
        GlyphFallbacks.TryGetValue((language, key), out int documented) && documented == uses
            ? "the font has no glyph for it; the engine draws the blank fallback (fonts.md 5.4, 9)"
            : null;

    /// <summary>Why an unresolved non-picture reference is excused, or null.</summary>
    public static string? Reason(CensusReference reference)
    {
        if (reference.Kind != ReferenceKind.Motion)
        {
            return null;
        }

        return reference.Result switch
        {
            "zero" => "motion id 0 selects no motion (script.md doc_checks item 3: 92 statements)",
            "out" when reference.Key == "101960" => "script bug: the one motion id past the table (script.md doc_checks item 3)",
            _ => null,
        };
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace Tsuki.Data.Catalog;

/// <summary>What an archive entry is for (docs/formats/archives_ui_movies.md 3); the wire names are those of <c>tsuki.catalog.Category</c>.</summary>
public enum NameCategory
{
    /// <summary>BLACK / BLUE / ... 1920x1080 fills.</summary>
    SolidColor,

    /// <summary>IMGnnnn: a background or event CG.</summary>
    Scene,

    /// <summary>IMGnnnn_aa_bb: a variant of IMGnnnn picked by STCH a9 / a10.</summary>
    SceneLayer,

    /// <summary>IMG3000..IMG4193: gallery and flowchart only, never scripted.</summary>
    GalleryImage,

    /// <summary>CCC_S_XX_YY_Pnn: a character sprite (a face of a body, or standalone).</summary>
    Sprite,

    /// <summary>..._B: the body a paired sprite's face is drawn over.</summary>
    SpriteBody,

    /// <summary>..._aa_bb: a sprite variant picked by STCH a9 / a10.</summary>
    SpriteLayer,

    /// <summary>allpac3 *_PARTS*.MRG: UI texture sheets.</summary>
    UiParts,

    /// <summary>One child image of a UI parts archive.</summary>
    UiPart,

    /// <summary>GALLERY_PARTS_{CG,FC}_TH.MRG.</summary>
    UiThumbnails,

    /// <summary>RULE_nn: 8-bit grey wipe masks.</summary>
    TransitionRule,

    /// <summary>CAUTION*: boot notices.</summary>
    CautionScreen,

    /// <summary>DUMMY.MRG, CGMODE_TOP.MRG, *_TOP.MZP.</summary>
    Placeholder,

    /// <summary>parts.mrg entries.</summary>
    SystemScreen,

    /// <summary>Anything else.</summary>
    Unknown,
}

/// <summary>The result of <see cref="NameGrammar.Classify"/>.</summary>
/// <param name="Name">The name as given.</param>
/// <param name="Category">What it is.</param>
/// <param name="Lang">Language code when the name carries one.</param>
/// <param name="Base">The name the entry belongs to (scene or sprite base, UI set).</param>
/// <param name="CharCode">The three-letter character code of a sprite.</param>
/// <param name="CharId">The engine's character id (0 when unknown).</param>
/// <param name="CharName">The character's name (inferred, [H] in the documents).</param>
/// <param name="SizeClass">The sprite size letter A / L / M / S.</param>
/// <param name="Layer">The <c>aa_bb</c> of a variant.</param>
/// <param name="Detail">One sentence about the category.</param>
public sealed record NameInfo(
    string Name, NameCategory Category, string Lang = "", string Base = "", string CharCode = "", int CharId = 0,
    string CharName = "", string SizeClass = "", string Layer = "", string Detail = "");

/// <summary>
/// The name grammar of the picture archives (docs/formats/archives_ui_movies.md 3): scenes, variants, sprites, bodies, UI
/// sets, rules, caution screens. A port of <c>tsuki.catalog.classify_name</c> and its tables.
/// </summary>
public static partial class NameGrammar
{
    private static readonly string[] SolidColors = ["BLACK", "BLUE", "GREEN", "ORANGE", "RED", "WHITE", "YELLOW"];

    /// <summary>Three-letter character code to (eboot char_id, name); the names are inferred from the code letters [H].</summary>
    public static IReadOnlyDictionary<string, (int CharId, string Name)> CharCodes { get; } = new Dictionary<string, (int, string)>
    {
        ["SIK"] = (1, "Shiki Tohno"), ["ARU"] = (2, "Arcueid Brunestud"), ["SIE"] = (3, "Ciel"),
        ["NOE"] = (4, "Noel"), ["ROA"] = (5, "Roa"), ["HIS"] = (6, "Hisui"), ["KOH"] = (7, "Kohaku"),
        ["AKI"] = (8, "Akiha Tohno"), ["ARI"] = (9, "Arihiko Inui"), ["MAR"] = (10, "Mario"),
        ["VRV"] = (11, "Vlov"), ["AOK"] = (12, "Aoko Aozaki"), ["BOY"] = (13, "boy (minor)"),
        ["ARA"] = (14, "Professor Arach"), ["AND"] = (15, "Ando"), ["KAR"] = (16, "Karius"),
        ["GOT"] = (17, "Gouto Saiki"), ["SAT"] = (18, "Satsuki Yumizuka"), ["MIO"] = (21, "Mio"),
        ["KUR"] = (23, "unknown (KUR)"), ["KRO"] = (24, "unknown (KRO)"), ["Z01"] = (25, "extra Z01"),
        ["Z02"] = (26, "extra Z02"), ["Z11"] = (27, "extra Z11"), ["Z12"] = (28, "extra Z12"),
        ["Z20"] = (29, "extra Z20"), ["Y00"] = (30, "extra Y00"),
    };

    /// <summary>Sprite size letter to eboot size_class (image-table +0x42).</summary>
    public static IReadOnlyDictionary<string, int> SizeClasses { get; } =
        new Dictionary<string, int> { ["A"] = 0, ["L"] = 1, ["M"] = 2, ["S"] = 3 };

    /// <summary>allpac3 UI set to (eboot base name, purpose). Children are image MZPs.</summary>
    public static IReadOnlyDictionary<string, (string EbootName, string Purpose)> UiPartsSets { get; } =
        new Dictionary<string, (string, string)>
        {
            ["CONF_PARTS"] = ("conf_parts.mrg", "config screen: tabs, buttons (3 states per sheet), sliders, key help, language selector"),
            ["DISP_PARTS"] = ("disp_parts.mrg", "in-game HUD: text windows, control panel, button glyphs, saving icon, quick menu"),
            ["MENU_PARTS"] = ("menu_parts.mrg", "system menu: Save/Load/Config/FlowChart/Title buttons, backgrounds, route list"),
            ["MODEPNL_PARTS"] = ("modepnl_parts.mrg", "mode panel bar (identical in all languages)"),
            ["SAVE_PARTS"] = ("save_parts.mrg", "save/load screen: route cards, digits, chapter list, AUTO SAVE tag"),
            ["TITLE_PARTS"] = ("title_parts.mrg", "title screen: logo, menu items, PRESS ANY BUTTON, (C)TYPE-MOON, backgrounds"),
            ["SELECT_PARTS"] = ("select_parts.mrg", "choice screen frames/backgrounds (language-neutral)"),
            ["SELECTTXT_PARTS"] = ("selecttxt_parts.mrg", "82 choice texts pre-rendered as 1416x416 images (Japanese; identical in all 5 sets) + digit column (child 82, loaded into slot 97 via the child clamp)"),
            ["BORDER_PARTS"] = ("border_parts.mrg", "border / frame textures at 20..512 px (type 11)"),
            ["EFFECT_PARTS"] = ("effect_parts.mrg", "effect textures (3 dummies + 1920x128 strip)"),
            ["NOISE_PARTS"] = ("noise_parts.mrg", "two 1920x1080 noise textures"),
            ["GALLERY_PARTS_COMMON"] = ("gallery_parts_common.mrg", "gallery shared frame/background"),
            ["GALLERY_PARTS_CG"] = ("gallery_parts_cg.mrg", "CG gallery headers"),
            ["GALLERY_PARTS_CG_TH"] = ("gallery_parts_cg_th.mrg", "CG gallery thumbnails (1979)"),
            ["GALLERY_PARTS_MUSIC"] = ("gallery_parts_music.mrg", "music room"),
            ["GALLERY_PARTS_FC"] = ("gallery_parts_fc.mrg", "flowchart screen"),
            ["GALLERY_PARTS_FC_TH"] = ("gallery_parts_fc_th.mrg", "flowchart scene thumbnails (495)"),
            ["GALLERY_PARTS_MOVIE"] = ("gallery_parts_movie.mrg", "movie gallery"),
            ["GALLERY_PARTS_ENDING"] = ("gallery_parts_ending.mrg", "ending list"),
        };

    /// <summary>The wire name of a category, as <c>tsuki.catalog.Category</c> writes it (<c>scene_layer</c>).</summary>
    public static string Wire(NameCategory c) => c switch
    {
        NameCategory.SolidColor => "solid_color",
        NameCategory.Scene => "scene",
        NameCategory.SceneLayer => "scene_layer",
        NameCategory.GalleryImage => "gallery_image",
        NameCategory.Sprite => "sprite",
        NameCategory.SpriteBody => "sprite_body",
        NameCategory.SpriteLayer => "sprite_layer",
        NameCategory.UiParts => "ui_parts",
        NameCategory.UiPart => "ui_part",
        NameCategory.UiThumbnails => "ui_thumbnails",
        NameCategory.TransitionRule => "transition_rule",
        NameCategory.CautionScreen => "caution_screen",
        NameCategory.Placeholder => "placeholder",
        NameCategory.SystemScreen => "system_screen",
        _ => "unknown",
    };

    [GeneratedRegex(@"^(?<char>[A-Z]{3}|[ZY][0-9][0-9])_(?<size>[ALMS])_(?<group>[0-9][0-9])_(?<pose>[0-9][0-9])_(?<vletter>[NABC])(?<expr>[0-9][0-9])(?:_(?<body>B)|_(?<la>[0-9][0-9])_(?<lb>[0-9][0-9]))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SpriteRe();

    [GeneratedRegex(@"^IMG(?<num>[0-9]{4})(?:_(?<la>[0-9][0-9])_(?<lb>[0-9][0-9]))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SceneRe();

    [GeneratedRegex(@"^(?<base>.+?)_(?<lang>JA|EN|ZC|ZT|KO|TH)\z", RegexOptions.CultureInvariant)]
    private static partial Regex LangRe();

    [GeneratedRegex(@"^CAUTION(?<n>[0-9]?)(?:_(?<lang>JA|EN|ZC|ZT))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex CautionRe();

    /// <summary>
    /// Classifies an asset by its NAM name (extension optional, case-insensitive). Only ASCII names are meaningful.
    /// A name that the Python reference would reject with a KeyError (a <c>_TH</c> suffix on a set without thumbnails) is
    /// <see cref="NameCategory.Unknown"/> here.
    /// </summary>
    public static NameInfo Classify(string name)
    {
        string up = name.ToUpperInvariant();
        int dot = up.LastIndexOf('.');
        string head = dot >= 0 ? up[..dot] : string.Empty;
        string stem = head.Length > 0 ? head : up;
        if (Array.IndexOf(SolidColors, stem) >= 0)
        {
            return new NameInfo(name, NameCategory.SolidColor, Detail: "1920x1080 solid fill");
        }

        if (stem is "DUMMY" or "CGMODE_TOP")
        {
            return new NameInfo(name, NameCategory.Placeholder, Detail: "128-byte 16x16 transparent image MZP despite .MRG");
        }

        if (stem.EndsWith("_TOP", StringComparison.Ordinal))
        {
            return new NameInfo(name, NameCategory.Placeholder, Detail: "128x96 white image (1216 bytes, shared)");
        }

        var m = SceneRe().Match(stem);
        if (m.Success)
        {
            int num = int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture);
            if (m.Groups["la"].Success)
            {
                return new NameInfo(
                    name, NameCategory.SceneLayer, Base: "IMG" + m.Groups["num"].Value, Layer: $"{m.Groups["la"].Value}_{m.Groups["lb"].Value}",
                    Detail: "variant of the base shown instead of it by _STCH/_SCH2(..., base, ..., a9=aa, a10=bb)");
            }

            return num is >= 3000 and <= 4193
                ? new NameInfo(name, NameCategory.GalleryImage, Base: stem, Detail: "allpac1_2 only, never referenced by scripts, listed by gallery tables")
                : new NameInfo(name, NameCategory.Scene, Base: stem, Detail: "background or event CG");
        }

        m = SpriteRe().Match(stem);
        if (m.Success)
        {
            string code = m.Groups["char"].Value;
            var (cid, cname) = CharCodes.TryGetValue(code, out var cc) ? cc : (0, "unknown");
            string baseName = $"{code}_{m.Groups["size"].Value}_{m.Groups["group"].Value}_{m.Groups["pose"].Value}_{m.Groups["vletter"].Value}{m.Groups["expr"].Value}";
            var info = new NameInfo(name, NameCategory.Sprite, Base: baseName, CharCode: code, CharId: cid, CharName: cname, SizeClass: m.Groups["size"].Value);
            if (m.Groups["body"].Success)
            {
                return info with { Category = NameCategory.SpriteBody, Detail = "body; drawn under the face of every image-table record whose file it is" };
            }

            if (m.Groups["la"].Success)
            {
                return info with
                {
                    Category = NameCategory.SpriteLayer,
                    Layer = $"{m.Groups["la"].Value}_{m.Groups["lb"].Value}",
                    Detail = "variant of the base sprite shown instead of it by _STCH/_SCH2(..., base, ..., a9=aa, a10=bb)",
                };
            }

            return info with
            {
                Detail = "script-visible sprite name; draw its image-table record: file, then overlay if set "
                    + "(3,529 records pair a _B body with this face, 1,221 are standalone)",
            };
        }

        if (stem.StartsWith("RULE_", StringComparison.Ordinal))
        {
            return new NameInfo(name, NameCategory.TransitionRule, Detail: "1920x1080 8-bit grey wipe mask");
        }

        if (stem.StartsWith("CAUTION", StringComparison.Ordinal))
        {
            var m2 = CautionRe().Match(stem);
            string lang = m2.Success ? (m2.Groups["lang"].Success ? m2.Groups["lang"].Value : "JA").ToLowerInvariant() : string.Empty;
            string kind = (m2.Success ? m2.Groups["n"].Value : string.Empty) switch
            {
                "" => "fiction/violence disclaimer",
                "1" => "recording/streaming notice",
                "2" => "disclaimer + recording notice",
                _ => "?",
            };
            return new NameInfo(name, NameCategory.CautionScreen, Lang: lang, Detail: kind);
        }

        var ml = LangRe().Match(stem);
        string setName = ml.Success ? ml.Groups["base"].Value : stem;
        string langCode = ml.Success ? ml.Groups["lang"].Value : string.Empty;
        if (UiPartsSets.ContainsKey(setName) || UiPartsSets.ContainsKey($"{setName}_{langCode}"))
        {
            if (langCode == "TH")
            {
                return UiPartsSets.TryGetValue($"{setName}_TH", out var th)
                    ? new NameInfo(name, NameCategory.UiThumbnails, Base: $"{setName}_TH", Detail: th.Purpose)
                    : new NameInfo(name, NameCategory.Unknown);
            }

            return UiPartsSets.TryGetValue(setName, out var set)
                ? new NameInfo(name, NameCategory.UiParts, Lang: langCode.ToLowerInvariant(), Base: setName, Detail: set.Purpose)
                : new NameInfo(name, NameCategory.Unknown);
        }

        return new NameInfo(name, NameCategory.Unknown);
    }
}

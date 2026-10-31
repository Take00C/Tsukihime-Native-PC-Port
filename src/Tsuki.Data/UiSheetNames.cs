using Tsuki.Data.Catalog;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data;

/// <summary>
/// Which allpac3 archive holds a UI sheet set (docs/formats/archives_ui_movies.md 5.2, 7.3): the eboot's base name of the set
/// (<c>conf_parts.mrg</c>), and for the sets with language variants the loader's rule that turns it into <c>conf_parts_en.mrg</c>
/// (<see cref="UiTextureTables.LocalizePartsName"/>). The NAM search upper-cases the name, so it finds <c>CONF_PARTS_EN.MRG</c>.
/// </summary>
public static class UiSheetNames
{
    /// <summary>The eboot base name of a set, as in its registration tables.</summary>
    public static string BaseName(UiSet set) => set switch
    {
        UiSet.ConfParts => "conf_parts.mrg",
        UiSet.DispParts => "disp_parts.mrg",
        UiSet.MenuParts => "menu_parts.mrg",
        UiSet.ModePnlParts => "modepnl_parts.mrg",
        UiSet.SaveParts => "save_parts.mrg",
        UiSet.TitleParts => "title_parts.mrg",
        UiSet.SelectParts => "select_parts.mrg",
        UiSet.SelectTxtParts => "selecttxt_parts.mrg",
        UiSet.BorderParts => "border_parts.mrg",
        UiSet.EffectParts => "effect_parts.mrg",
        UiSet.NoiseParts => "noise_parts.mrg",
        UiSet.GalleryPartsCommon => "gallery_parts_common.mrg",
        UiSet.GalleryPartsCg => "gallery_parts_cg.mrg",
        UiSet.GalleryPartsCgTh => "gallery_parts_cg_th.mrg",
        UiSet.GalleryPartsMusic => "gallery_parts_music.mrg",
        UiSet.GalleryPartsFc => "gallery_parts_fc.mrg",
        UiSet.GalleryPartsFcTh => "gallery_parts_fc_th.mrg",
        UiSet.GalleryPartsMovie => "gallery_parts_movie.mrg",
        UiSet.GalleryPartsEnding => "gallery_parts_ending.mrg",
        _ => throw new ArgumentOutOfRangeException(nameof(set), set, "not a UI set"),
    };

    /// <summary>True when the set exists once per language (<c>_JA</c> .. <c>_KO</c>); false for the six language-free sets.</summary>
    public static bool HasLanguageVariants(UiSet set) => set switch
    {
        UiSet.SelectParts or UiSet.BorderParts or UiSet.EffectParts or UiSet.NoiseParts
            or UiSet.GalleryPartsCgTh or UiSet.GalleryPartsFcTh => false,
        _ => Enum.IsDefined(set) ? true : throw new ArgumentOutOfRangeException(nameof(set), set, "not a UI set"),
    };

    /// <summary>The archive name searched in allpac3 for a set in a language (<c>conf_parts_en.mrg</c>, or <c>border_parts.mrg</c> for a language-free set).</summary>
    public static string FileName(UiSet set, Language lang) =>
        HasLanguageVariants(set) ? UiTextureTables.LocalizePartsName(BaseName(set), (int)lang) : BaseName(set);
}

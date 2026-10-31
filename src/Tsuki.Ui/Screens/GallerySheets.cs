using Tsuki.Engine.Contracts;

namespace Tsuki.Ui.Screens;

/// <summary>
/// The UI sheets of the gallery sets by PS4 child (docs/formats/archives_ui_movies.md 7.3; previews in scratch/K5b/sheets). The names in the comments are
/// knife's textures (<c>gallerymenu_btn</c>, <c>cg_cursor</c>, ...), which are the same pictures.
/// </summary>
public static class GallerySheets
{
    // ---- GALLERY_PARTS_COMMON (identical in every language: the mode names are English everywhere)

    /// <summary>304 x 56: the cursor line under the active mode button (<c>gallerycursor_line</c>).</summary>
    public static readonly UiSheet MenuLine = new(UiSet.GalleryPartsCommon, 0);

    /// <summary>800 x 632: the six mode buttons in three columns (normal, hover, active) at x 8 + 264 c, rows y 8 + 104 k, cells 256 x 96 (<c>gallerymenu_btn</c>).</summary>
    public static readonly UiSheet MenuButtons = new(UiSet.GalleryPartsCommon, 1);

    /// <summary>312 x 1080: the window on the left (<c>gallerymenu_window</c>).</summary>
    public static readonly UiSheet MenuWindow = new(UiSet.GalleryPartsCommon, 2);

    /// <summary>528 x 240: the cursor light (<c>systemcursor_light</c>).</summary>
    public static readonly UiSheet MenuLight = new(UiSet.GalleryPartsCommon, 3);

    /// <summary>1920 x 16: the underline.</summary>
    public static readonly UiSheet Underline = new(UiSet.GalleryPartsCommon, 4);

    /// <summary>1560 x 1080: the panel background (<c>config_innerbg</c>).</summary>
    public static readonly UiSheet Panel = new(UiSet.GalleryPartsCommon, 5);

    // ---- GALLERY_PARTS_CG, CG_TH

    /// <summary>520 x 160: the cursor frame (cell 8, 8, 248, 144) and its glow (264, 8, 248, 144) (<c>cg_cursor</c>).</summary>
    public static readonly UiSheet CgCursor = new(UiSet.GalleryPartsCg, 0);

    /// <summary>720 x 100: the title banner (<c>cg_title</c>); the PS4 has it in English only.</summary>
    public static readonly UiSheet CgTitle = new(UiSet.GalleryPartsCg, 1);

    /// <summary>The thumbnail of a CG: child <paramref name="child"/> of GALLERY_PARTS_CG_TH (231 x 130, or four such frames stacked).</summary>
    public static UiSheet CgThumb(int child) => new(UiSet.GalleryPartsCgTh, child);

    // ---- GALLERY_PARTS_MUSIC

    /// <summary>926 x 16: the line under a list row (<c>music_bgline</c>).</summary>
    public static readonly UiSheet MusicLine = new(UiSet.GalleryPartsMusic, 0);

    /// <summary>448 x 896: the player panel with TIME and VOLUME (<c>music_btnbg</c>).</summary>
    public static readonly UiSheet MusicPanel = new(UiSet.GalleryPartsMusic, 1);

    /// <summary>584 x 272: the player buttons, cells 88 x 80 at x 8 + 96 k and y 8 (normal), 96 (hover), 184 (active) (<c>music_controlbtn</c>).</summary>
    public static readonly UiSheet MusicButtons = new(UiSet.GalleryPartsMusic, 2);

    /// <summary>400 x 24: the time and volume slider tracks (<c>music_slider</c>).</summary>
    public static readonly UiSheet MusicSlider = new(UiSet.GalleryPartsMusic, 3);

    /// <summary>960 x 1736: the list panel behind the rows (<c>music_soundbg</c>).</summary>
    public static readonly UiSheet MusicList = new(UiSet.GalleryPartsMusic, 4);

    /// <summary>248 x 88: the slider knob, cells 72 x 72 at (8, 8) and its glow at (168, 8) (<c>music_soundbtn</c>).</summary>
    public static readonly UiSheet MusicKnob = new(UiSet.GalleryPartsMusic, 5);

    /// <summary>720 x 100: the title banner (<c>music_title</c>); English only.</summary>
    public static readonly UiSheet MusicTitle = new(UiSet.GalleryPartsMusic, 6);

    // ---- GALLERY_PARTS_MOVIE

    /// <summary>776 x 464: the three movie cards (the openings of Arcueid and Ciel, the prologue's moon) in three states (<c>movie_cur</c>).</summary>
    public static readonly UiSheet MovieCards = new(UiSet.GalleryPartsMovie, 0);

    /// <summary>The title banner (<c>movie_title</c>, 720 x 80, 720 x 100 in English).</summary>
    public static readonly UiSheet MovieTitle = new(UiSet.GalleryPartsMovie, 1);

    // ---- GALLERY_PARTS_ENDING

    /// <summary>1472 x 584: the bad end plates, locked (rows y 8 + 96 r) and reached (y 296 + 96 r), 180 x 88 at x 8 + 180 c (<c>ending_badend</c>).</summary>
    public static readonly UiSheet EndingPlates = new(UiSet.GalleryPartsEnding, 0);

    /// <summary>296 x 104: the play button on a plate, cells 88 x 88 at x 8 and 104 (hover) (<c>ending_badendbtn</c>).</summary>
    public static readonly UiSheet EndingPlateButtons = new(UiSet.GalleryPartsEnding, 1);

    /// <summary>368 x 128: the large play button, cells 112 x 112 at x 8 and 128 (hover) (<c>ending_btn</c>).</summary>
    public static readonly UiSheet EndingButtons = new(UiSet.GalleryPartsEnding, 2);

    /// <summary>432 x 544: the card of ending <paramref name="e"/> (0 Moon Princess, 1 Midnight Rainbow, 2 Cloudless Blue; <c>ending_icon_*</c>).</summary>
    public static UiSheet EndingCard(int e) => new(UiSet.GalleryPartsEnding, 3 + Math.Clamp(e, 0, 2));

    /// <summary>432 x 544: the card of an ending not reached (<c>ending_icon_non</c>).</summary>
    public static readonly UiSheet EndingCardLocked = new(UiSet.GalleryPartsEnding, 6);

    /// <summary>The title banner (<c>ending_title</c>, 720 x 80 / 100).</summary>
    public static readonly UiSheet EndingTitle = new(UiSet.GalleryPartsEnding, 7);

    // ---- GALLERY_PARTS_FC, FC_TH

    /// <summary>1920 x 1080: the chart's background (<c>flow_bg</c>).</summary>
    public static readonly UiSheet ChartBackground = new(UiSet.GalleryPartsFc, 0);

    /// <summary>232 x 80: the node cursor (cell 8, 8, 104, 64) and its glow (120, 8) (<c>flow_cur</c>).</summary>
    public static readonly UiSheet ChartCursor = new(UiSet.GalleryPartsFc, 1);

    /// <summary>144 x 32: the "Current Scene" tag (<c>flow_info</c>).</summary>
    public static readonly UiSheet ChartTag = new(UiSet.GalleryPartsFc, 2);

    /// <summary>488 x 496: the card of the selected scene (<c>flow_infobase</c>).</summary>
    public static readonly UiSheet ChartCard = new(UiSet.GalleryPartsFc, 3);

    /// <summary>The preview picture of a scene: child <paramref name="child"/> of GALLERY_PARTS_FC_TH (430 x 243, or four such bands stacked).</summary>
    public static UiSheet ChartPreview(int child) => new(UiSet.GalleryPartsFcTh, child);

    /// <summary>The sheet a <see cref="ChartSheet"/> piece is cut from.</summary>
    public static UiSheet Of(ChartSheet sheet) => sheet switch
    {
        ChartSheet.BandMiddle => new(UiSet.GalleryPartsFc, 8),
        ChartSheet.BandUnder => new(UiSet.GalleryPartsFc, 9),
        ChartSheet.BandUpper => new(UiSet.GalleryPartsFc, 10),
        ChartSheet.LineGrey => new(UiSet.GalleryPartsFc, 4),
        ChartSheet.LineLit => new(UiSet.GalleryPartsFc, 5),
        ChartSheet.Mosaic => new(UiSet.GalleryPartsFc, 7),
        ChartSheet.NodeFrame => new(UiSet.GalleryPartsFc, 11),
        _ => new(UiSet.GalleryPartsFc, 6),
    };

    /// <summary>
    /// True when the title banner of <paramref name="mode"/> exists in <paramref name="language"/>: the PS4's CG and music sets carry it in English only
    /// (docs/formats/archives_ui_movies.md 7.3: GALLERY_PARTS_CG and _MUSIC have one child fewer in the other languages).
    /// </summary>
    public static bool HasTitle(GalleryMode mode, Language language) => mode switch
    {
        GalleryMode.Cg or GalleryMode.Music => language == Language.En,
        _ => true,
    };

    /// <summary>The banner sheet of <paramref name="mode"/>.</summary>
    public static UiSheet TitleOf(GalleryMode mode) => mode switch
    {
        GalleryMode.Cg => CgTitle,
        GalleryMode.Music => MusicTitle,
        GalleryMode.Movies => MovieTitle,
        _ => EndingTitle,
    };
}

/// <summary>The modes of the gallery (the order of its six buttons; the Flowchart is a screen of its own).</summary>
public enum GalleryMode
{
    /// <summary>CG gallery.</summary>
    Cg = 0,

    /// <summary>Music player.</summary>
    Music = 1,

    /// <summary>Flowchart.</summary>
    Flowchart = 2,

    /// <summary>Movie gallery.</summary>
    Movies = 3,

    /// <summary>Ending list.</summary>
    Endings = 4,
}

/// <summary>What the gallery remembers between visits (knife <c>gal_ret</c>, the PS4's gallery memory at 0xAE0288): the mode it closed on and the entry a replay started from.</summary>
public sealed class GalleryMemory
{
    /// <summary>The mode the gallery closed with, or -1: the first mode of the next visit.</summary>
    public int Mode { get; set; } = -1;

    /// <summary>The flowchart node (or ending scene) a replay was picked from, or -1: the entry the gallery comes back on.</summary>
    public int Node { get; set; } = -1;

    /// <summary>The mode shown before the chart was opened, or -1.</summary>
    public int Previous { get; set; } = -1;
}

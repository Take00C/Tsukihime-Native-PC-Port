using Tsuki.Engine.Text;

namespace Tsuki.Engine.Contracts;

/// <summary>The eboot tables of the menus (package K5a; docs/research/knife_tables.md 3.5, docs/formats/archives_ui_movies.md 7).</summary>
public partial interface IEbootTables
{
    /// <summary>The UI cell, message and layout tables that Tsuki.Ui reads.</summary>
    IUiTables Ui { get; }
}

/// <summary>A cell of a UI sheet in texels: the corners (x0, y0) and (x1, y1), x1 and y1 exclusive.</summary>
/// <param name="X0">Left edge.</param>
/// <param name="Y0">Top edge.</param>
/// <param name="X1">Right edge (exclusive).</param>
/// <param name="Y1">Bottom edge (exclusive).</param>
public readonly record struct SheetCell(int X0, int Y0, int X1, int Y1)
{
    /// <summary>Width in texels.</summary>
    public int Width => X1 - X0;

    /// <summary>Height in texels.</summary>
    public int Height => Y1 - Y0;
}

/// <summary>One control of a config row (docs/formats/adv_state.md 9.1): item id, type code and position in 640 x 448 units.</summary>
/// <param name="Item">Item id.</param>
/// <param name="Kind">Type code: 1 test button, 2 check box, 3 / 4 / 5 minus / slider / plus [H], 6..10 radio groups.</param>
/// <param name="X">Position x in 640 x 448 units (-9999 where the screen places the control at run time).</param>
/// <param name="Y">Position y in 640 x 448 units.</param>
public sealed record UiConfigControl(int Item, int Kind, float X, float Y);

/// <summary>One row of a config page.</summary>
/// <param name="Controls">The controls, left to right.</param>
/// <param name="Current">The page's stored current value.</param>
/// <param name="Maximum">Slider maximum.</param>
public sealed record UiConfigRow(IReadOnlyList<UiConfigControl> Controls, int Current, int Maximum);

/// <summary>One data-driven config page (page 0 System, page 1 Sound).</summary>
/// <param name="FirstItem">First item id.</param>
/// <param name="LastItem">Last item id.</param>
/// <param name="Rows">The rows.</param>
public sealed record UiConfigPage(int FirstItem, int LastItem, IReadOnlyList<UiConfigRow> Rows);

/// <summary>
/// The eboot tables the menu screens need. Everything is read once from the user's eboot (K1b's readers, docs/research/knife_tables.md) and is
/// immutable. Cell tables are in texels of the named sheet; message ids are <c>ui_text</c> ids (the PS4 numbering, which is knife's SYSMES number
/// plus one from 217 on).
/// </summary>
public interface IUiTables
{
    /// <summary>The glyph lookup and rectangle tables of the text layout (docs/formats/fonts.md 5).</summary>
    FontTables Fonts { get; }

    /// <summary>The <c>ui_text</c> string <paramref name="id"/> (1..1498) in <paramref name="language"/>; <c>^</c> is the line break; empty when the id has no text.</summary>
    string Text(Language language, int id);

    /// <summary>The savetitle cells by phase F023 (38) in SAVE_PARTS child 7 (the English table for English, the other one for the rest).</summary>
    IReadOnlyList<SheetCell> SaveTitleCells(Language language);

    /// <summary>The system_phasetitle cells by phase (38) in MENU_PARTS child 2 (ja, en, and the third table for the others).</summary>
    IReadOnlyList<SheetCell> PhaseTitleCells(Language language);

    /// <summary>The help_key icon cells (23) in DISP_PARTS child 16.</summary>
    IReadOnlyList<SheetCell> GuideIconCells { get; }

    /// <summary>The help_txt word cells (27) in DISP_PARTS child 17 for <paramref name="language"/>.</summary>
    IReadOnlyList<SheetCell> GuideWordCells(Language language);

    /// <summary>The mode banner cells of the PS4 build (2) in DISP_PARTS child 10.</summary>
    IReadOnlyList<SheetCell> BannerCells { get; }

    /// <summary>The banner pop-in scale keys (10 x {x scale, y scale}) sampled over t * 9.</summary>
    IReadOnlyList<(float X, float Y)> BannerKeys { get; }

    /// <summary>The conf_faceicon cells per config voice row (20) in CONF_PARTS child 12.</summary>
    IReadOnlyList<SheetCell> VoiceFaceCells { get; }

    /// <summary>The conf_name cells per config voice row (20) in CONF_PARTS child 13 (the English table for English).</summary>
    IReadOnlyList<SheetCell> VoiceNameCells(Language language);

    /// <summary>The two data-driven config pages (System, Sound) with control positions.</summary>
    IReadOnlyList<UiConfigPage> ConfigPages { get; }

    /// <summary>The ui_text id of each confirm dialog and help message (55; -1 = none; docs/research/knife_tables.md 5.1).</summary>
    IReadOnlyList<int> DialogMessages { get; }

    /// <summary>The progress word (G) that says whether a config voice row's character has been heard, per row (19).</summary>
    IReadOnlyList<int> VoiceRowWords { get; }

    /// <summary>The 14 config SE test names.</summary>
    IReadOnlyList<string> TestSeNames { get; }

    /// <summary>
    /// Finds a picture of the image archives by its file name (the engine's NAM search, docs/formats/archives_ui_movies.md 2.3; case-insensitive), for example
    /// the caution screens <c>caution_en.mzp</c>; (archive id, entry), or null when there is none.
    /// </summary>
    (int Archive, int Entry)? FindPicture(string name);

    /// <summary>The title background table by G002 (6 x i32 at text VA 0x218A40; docs/formats/system_flow.md 4.2).</summary>
    IReadOnlyList<int> TitleBackgroundTable { get; }
}

// ---- package K5b: the gallery, music room, ending list and flowchart tables (docs/formats/gallery.md) ------------------------------------------------

/// <summary>The gallery and flowchart tables of the menus (package K5b).</summary>
public partial interface IEbootTables
{
    /// <summary>The gallery and flowchart tables, or null when the source holds none (the golden slice).</summary>
    IUiGalleryTables? Gallery { get; }
}

/// <summary>One CG of a gallery group (gallery.md 4.1).</summary>
/// <param name="Name">The picture name without the one-character prefix (<c>IMG1685.MZP</c>).</param>
/// <param name="Thumbnail">Child of the GALLERY_PARTS_CG_TH set.</param>
/// <param name="X">Source rectangle x in the picture (1920-wide units).</param>
/// <param name="Y">Source rectangle y.</param>
/// <param name="Width">Source rectangle width.</param>
/// <param name="Height">Source rectangle height.</param>
/// <param name="Flag">The +0x1C byte (meaning [H]).</param>
public sealed record GalleryCgRow(string Name, int Thumbnail, int X, int Y, int Width, int Height, int Flag);

/// <summary>A gallery group: the CGs shown as one tile of the main grid.</summary>
/// <param name="Category">Unlock category 0..19 (gallery.md 4.4).</param>
/// <param name="Entries">The CGs of the group.</param>
public sealed record GalleryGroupRow(int Category, IReadOnlyList<GalleryCgRow> Entries);

/// <summary>A CG that is also unlocked by seeing one of its alias pictures (gallery.md 4.3).</summary>
/// <param name="Key">The CG's picture name.</param>
/// <param name="Aliases">The alias picture names, in order.</param>
public sealed record GalleryAliasRow(string Key, IReadOnlyList<string> Aliases);

/// <summary>A track of the music player (gallery.md 6.1).</summary>
/// <param name="Bgm">The bgm.hed index; the heard bit is 0xC80 + this.</param>
/// <param name="TitleUi"><c>ui_text</c> id of the title.</param>
/// <param name="VocalUi">Credit ids after the four labels (-1 none): vocals.</param>
/// <param name="LyricsUi">Lyrics credit.</param>
/// <param name="ComposerUi">Composition credit.</param>
/// <param name="ArrangerUi">Arrangement credit.</param>
/// <param name="LengthMs">Shown length in ms (0 when the table has none).</param>
/// <param name="LoopStart">Loop start sample (48 kHz).</param>
/// <param name="LoopEnd">Loop end sample (48 kHz).</param>
/// <param name="Variants">Other BGM tracks that also unlock the entry.</param>
public sealed record GalleryTrackRow(int Bgm, int TitleUi, int VocalUi, int LyricsUi, int ComposerUi, int ArrangerUi, int LengthMs, int LoopStart, int LoopEnd, IReadOnlyList<int> Variants);

/// <summary>An entry of the ending list (gallery.md 8).</summary>
/// <param name="ScriptScene">The QA scene passed to the replay (1000..1024).</param>
/// <param name="FlowScene">The scene's flowchart node (533..557).</param>
/// <param name="UnlockA">scene_done(UnlockA) or scene_done(UnlockB) unlocks the entry.</param>
/// <param name="UnlockB">Second unlock scene, or -1.</param>
/// <param name="MessageUi"><c>ui_text</c> id of the play prompt, or -1.</param>
public sealed record GalleryEndingRow(int ScriptScene, int FlowScene, int UnlockA, int UnlockB, int MessageUi);

/// <summary>A node record of the flowchart (gallery.md 5.2).</summary>
/// <param name="Scene">Positive: the node (lit by scene_done); negative: a marker for -Scene; 0: empty.</param>
/// <param name="Connector">Connector code, -1 none.</param>
/// <param name="Link">Edge id 565..1194 (edge_seen) or -1.</param>
/// <param name="ScriptScene">The scene id a replay or a jump starts.</param>
/// <param name="ChapterLabel">Cell index into the chapter label cells, or -1.</param>
/// <param name="Mosaic">Cell of the scene-thumbnail mosaic (FC child 7).</param>
/// <param name="Preview">Child of the GALLERY_PARTS_FC_TH set.</param>
/// <param name="TitleUi"><c>ui_text</c> id of the title, -1 none.</param>
/// <param name="SynopsisUi"><c>ui_text</c> id of the synopsis, -1 none.</param>
public sealed record ChartNodeRow(int Scene, int Connector, int Link, int ScriptScene, int ChapterLabel, int Mosaic, int Preview, int TitleUi, int SynopsisUi);

/// <summary>A flowchart column of the static table.</summary>
/// <param name="Group">Chapter band 0..15 (the phase).</param>
/// <param name="Nodes">The nodes.</param>
/// <param name="OverrunScene">The scene word of the record that follows the last node in memory: the engine's column filter reads it (gallery.md 5.4).</param>
/// <param name="OverrunLink">The link word of that record.</param>
public sealed record ChartColumnRow(int Group, IReadOnlyList<ChartNodeRow> Nodes, int OverrunScene, int OverrunLink);

/// <summary>An edge of the flowchart: the edge ids set when the story goes from node From to node To.</summary>
/// <param name="From">Source node.</param>
/// <param name="To">Target node.</param>
/// <param name="GlobalIds">Edge ids for the persistent set.</param>
/// <param name="PlaythroughIds">Edge ids for this playthrough.</param>
public sealed record ChartEdgeRow(int From, int To, IReadOnlyList<int> GlobalIds, IReadOnlyList<int> PlaythroughIds);

/// <summary>The gallery, music room, ending list and flowchart tables read from the eboot (docs/formats/gallery.md).</summary>
public interface IUiGalleryTables
{
    /// <summary>The 458 CG groups.</summary>
    IReadOnlyList<GalleryGroupRow> CgGroups { get; }

    /// <summary>The 105 CG aliases.</summary>
    IReadOnlyList<GalleryAliasRow> CgAliases { get; }

    /// <summary>The 11 CG names that stack one picture per language (their source rectangle moves down by its height times the language).</summary>
    IReadOnlyList<string> CgLanguageStrips { get; }

    /// <summary>Per CG category 1..19, the scene_seen ids of which any one opens it.</summary>
    IReadOnlyDictionary<int, IReadOnlyList<int>> CgCategoryScenes { get; }

    /// <summary>The 95 music player tracks.</summary>
    IReadOnlyList<GalleryTrackRow> Tracks { get; }

    /// <summary>The 25 ending list entries.</summary>
    IReadOnlyList<GalleryEndingRow> Endings { get; }

    /// <summary>The 277 flowchart columns.</summary>
    IReadOnlyList<ChartColumnRow> ChartColumns { get; }

    /// <summary>The 630 flowchart edges (table order, first match wins).</summary>
    IReadOnlyList<ChartEdgeRow> ChartEdges { get; }

    /// <summary>The chapter label cells (30) in FC child 6 (the English table for English).</summary>
    IReadOnlyList<SheetCell> ChartTitleCells(Language language);

    /// <summary>The connector cells (12) of the route line sheets (FC children 4 and 5).</summary>
    IReadOnlyList<SheetCell> ChartLineCells { get; }

    /// <summary>The index of a picture name (extension ignored, case-insensitive) in the picture name table, or -1.</summary>
    int PictureIndex(string name);

    /// <summary>The picture-seen bit of picture <paramref name="pictureId"/> (gallery.md 2.2), or -1.</summary>
    int PictureSeenIndex(int pictureId);
}

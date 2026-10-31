using Tsuki.Engine.Contracts;

namespace Tsuki.Ui;

/// <summary>
/// The UI sheets the menus draw, by PS4 set and child (docs/formats/archives_ui_movies.md 5.2, 5.3 and the previews in samples/ui_misc/previews). The Switch
/// build of knife keeps the same pictures as separate textures (<c>menu_btn_ja</c>, <c>save_win</c>, ...); the names in the comments are knife's.
/// </summary>
public static class UiSheets
{
    // ---- TITLE_PARTS (registration table 0x873660: children 9+T, 0..8, 14 -> texture slots 125..136)

    /// <summary>968 x 472: the five menu items in three columns (normal, cursor, pressed) at x 24 + 312 c, rows y 24 + 88 k (knife <c>titlemenu</c>).</summary>
    public static readonly UiSheet TitleMenu = new(UiSet.TitleParts, 0);

    /// <summary>The logo: 560 x 648 (ja) or 1850 x 440 (en) (<c>title_titlelog</c>).</summary>
    public static readonly UiSheet TitleLogo = new(UiSet.TitleParts, 1);

    /// <summary>360 x 48: the cursor underline bar, cell (24, 24)-(360, 32) (system_flow.md 4.5 row 6).</summary>
    public static readonly UiSheet TitleUnderline = new(UiSet.TitleParts, 2);

    /// <summary>184 x 64: (C)TYPE-MOON (<c>titlemenu_(c)type-moon</c>).</summary>
    public static readonly UiSheet TitleCopyright = new(UiSet.TitleParts, 3);

    /// <summary>488 x 256: the moon glow (<c>titlemenu_lumi</c>).</summary>
    public static readonly UiSheet TitleGlow = new(UiSet.TitleParts, 4);

    /// <summary>The three logo shards (33 x 44, 32 x 55, 29 x 45); knife keeps them in one atlas (<c>logoparticle_all</c>).</summary>
    public static UiSheet TitleShard(int kind) => new(UiSet.TitleParts, 5 + Math.Clamp(kind, 0, 2));

    /// <summary>2670 x 1266: the PRESS ANY BUTTON background layer (<c>title_bg0</c>).</summary>
    public static readonly UiSheet TitleBg0 = new(UiSet.TitleParts, 9);

    /// <summary>The still behind the menu for title stage T (1..4): 1920 x 1080 children 10..13 (<c>title_bg1..4</c>).</summary>
    public static UiSheet TitleStill(int t) => new(UiSet.TitleParts, 9 + Math.Clamp(t, 1, 4));

    /// <summary>472 x 88: PRESS ANY BUTTON (<c>press_start</c>).</summary>
    public static readonly UiSheet PressAnyButton = new(UiSet.TitleParts, 14);

    // ---- MENU_PARTS (tables 0x7A6B20 and 0x7A67F0: children 0..17 -> texture slots 320..337)

    /// <summary>752 x 552: Save, Load, Config, FlowChart, Title in three columns (<c>menu_btn_ja</c>).</summary>
    public static readonly UiSheet MenuButtons = new(UiSet.MenuParts, 0);

    /// <summary>312 x 1080: the window on the left (<c>menu_window</c>).</summary>
    public static readonly UiSheet MenuWindow = new(UiSet.MenuParts, 1);

    /// <summary>3664 x 992 (English): the chapter titles (<c>system_phasetitle</c>).</summary>
    public static readonly UiSheet PhaseTitles = new(UiSet.MenuParts, 2);

    /// <summary>1168 x 80: the scene title band (<c>system_scenebg</c>).</summary>
    public static readonly UiSheet SceneBand = new(UiSet.MenuParts, 3);

    /// <summary>1920 x 392: the title band (<c>system_titlebg</c>).</summary>
    public static readonly UiSheet TitleBand = new(UiSet.MenuParts, 4);

    /// <summary>528 x 240: the cursor light (<c>systemcursor_light</c>).</summary>
    public static readonly UiSheet CursorLight = new(UiSet.MenuParts, 5);

    /// <summary>224 x 56: the cursor line (<c>systemcursor_line</c>).</summary>
    public static readonly UiSheet CursorLine = new(UiSet.MenuParts, 6);

    /// <summary>1920 x 16: the underline (<c>underline</c>).</summary>
    public static readonly UiSheet Underline = new(UiSet.MenuParts, 7);

    /// <summary>82 x 82: a sparkle (<c>kirakira</c>).</summary>
    public static readonly UiSheet Spark = new(UiSet.MenuParts, 8);

    /// <summary>1920 x 1080: the backdrop (<c>menuBG01</c>).</summary>
    public static readonly UiSheet Backdrop = new(UiSet.MenuParts, 9);

    /// <summary>Strip k of the backdrop's lower group (<c>menuBG04_k</c>, children 10..13: 1920 x 396, 321, 255, 165).</summary>
    public static UiSheet StripLow(int k) => new(UiSet.MenuParts, 10 + k);

    /// <summary>Strip k of the backdrop's upper group (<c>menuBG05_k</c>, children 14..17: 1920 x 311, 1700 x 236, 1920 x 170, 1497 x 80).</summary>
    public static UiSheet StripHigh(int k) => new(UiSet.MenuParts, 14 + k);

    // ---- SAVE_PARTS (table 0x7A8300: children 8, 0..7, 9 -> texture slots 338..347)

    /// <summary>192 x 32: the AUTO SAVE tag (<c>saveauto</c>).</summary>
    public static readonly UiSheet SaveAuto = new(UiSet.SaveParts, 0);

    /// <summary>376 x 64: the date digits 0..9 and a separator, cells 24 x 32 at x 16 + 32 k, y 16 (<c>savedate_number</c>).</summary>
    public static readonly UiSheet SaveDateDigits = new(UiSet.SaveParts, 1);

    /// <summary>472 x 712: the Arcueid route card (<c>saveinfobg_arc</c>).</summary>
    public static readonly UiSheet SaveInfoArc = new(UiSet.SaveParts, 2);

    /// <summary>472 x 712: the Ciel route card (<c>saveinfobg_ciel</c>).</summary>
    public static readonly UiSheet SaveInfoCiel = new(UiSet.SaveParts, 3);

    /// <summary>472 x 712: the neutral card (<c>saveinfobg_unknown</c>).</summary>
    public static readonly UiSheet SaveInfoUnknown = new(UiSet.SaveParts, 4);

    /// <summary>64 x 24: the SCENE tag.</summary>
    public static readonly UiSheet SaveSceneTag = new(UiSet.SaveParts, 5);

    /// <summary>376 x 64: the time digits and the colon (<c>savetime_number</c>).</summary>
    public static readonly UiSheet SaveTimeDigits = new(UiSet.SaveParts, 6);

    /// <summary>2176 x 496 (English): the chapter titles of the list (<c>savetitle</c>).</summary>
    public static readonly UiSheet SaveTitles = new(UiSet.SaveParts, 7);

    /// <summary>1024 x 640: the slot frame, white at (32, 32) and blue at (31, 336) (<c>save_win</c>).</summary>
    public static readonly UiSheet SaveFrame = new(UiSet.SaveParts, 8);

    /// <summary>1560 x 1080: the panel background (<c>config_innerbg</c>).</summary>
    public static readonly UiSheet SavePanel = new(UiSet.SaveParts, 9);

    // ---- CONF_PARTS (table 0x66C9A0: children 0..19 -> texture slots 361..380)

    /// <summary>608 x 272: the tab labels, 3 rows x 3 columns (<c>btn_base0</c>).</summary>
    public static readonly UiSheet ConfTabs = new(UiSet.ConfParts, 0);

    /// <summary>352 x 312: the Recommended Settings button (<c>btn_default</c>).</summary>
    public static readonly UiSheet ConfDefault = new(UiSet.ConfParts, 1);

    /// <summary>1560 x 1080: the panel background (<c>config_innerbg</c>).</summary>
    public static readonly UiSheet ConfPanel = new(UiSet.ConfParts, 2);

    /// <summary>1416 x 72: the header bar (<c>tab_bg</c>).</summary>
    public static readonly UiSheet ConfHeader = new(UiSet.ConfParts, 3);

    /// <summary>The radio buttons: ON / OFF, All Text / Read Only, Fade-in, Fade-out (<c>btn_base1</c>).</summary>
    public static readonly UiSheet ConfRadio = new(UiSet.ConfParts, 4);

    /// <summary>The two check boxes (<c>btn_base2</c>).</summary>
    public static readonly UiSheet ConfToggle = new(UiSet.ConfParts, 5);

    /// <summary>936 x 504: the slider knob and its glow (<c>conf_sbtn</c>).</summary>
    public static readonly UiSheet ConfKnob = new(UiSet.ConfParts, 6);

    /// <summary>168 x 137: the - and + buttons in three states (<c>conf_stxt01</c>).</summary>
    public static readonly UiSheet ConfStep = new(UiSet.ConfParts, 7);

    /// <summary>1432 x 224: the page frame.</summary>
    public static readonly UiSheet ConfFrame = new(UiSet.ConfParts, 8);

    /// <summary>1432 x 1602: the text settings page (<c>system_bg</c>).</summary>
    public static readonly UiSheet ConfSystemPage = new(UiSet.ConfParts, 9);

    /// <summary>504 x 128: the Mute button in three states (<c>btn_mute</c>).</summary>
    public static readonly UiSheet ConfMute = new(UiSet.ConfParts, 10);

    /// <summary>408 x 120: the Test button in three states (<c>btn_test</c>).</summary>
    public static readonly UiSheet ConfTest = new(UiSet.ConfParts, 11);

    /// <summary>688 x 1008: the character faces (<c>conf_faceicon</c>).</summary>
    public static readonly UiSheet ConfFaces = new(UiSet.ConfParts, 12);

    /// <summary>504 x 496 (ja) / 624 (en): the character names (<c>conf_name</c>).</summary>
    public static readonly UiSheet ConfNames = new(UiSet.ConfParts, 13);

    /// <summary>1432 x 328: the sound page header (<c>sound_bg</c>).</summary>
    public static readonly UiSheet ConfSoundPage = new(UiSet.ConfParts, 14);

    /// <summary>1432 x 2872: the voice page (<c>voice_bg</c>).</summary>
    public static readonly UiSheet ConfVoicePage = new(UiSet.ConfParts, 15);

    /// <summary>
    /// 1416 x 1528: the controls page with CIRCLE as decide (<c>conf_manual0</c>): the controller rows on top (12 rows, the icons in a column at x 976), the mouse
    /// rows below.
    /// </summary>
    public static readonly UiSheet ConfControlsPad = new(UiSet.ConfParts, 16);

    /// <summary>1416 x 1528: the same page with CROSS as decide (the confirm and cancel icons swapped; the game shows it with the enter-button setting).</summary>
    public static readonly UiSheet ConfControlsPadCross = new(UiSet.ConfParts, 17);

    /// <summary>320 x 32: the digits 0..9 of the config values (<c>config_number</c>).</summary>
    public static readonly UiSheet ConfDigits = new(UiSet.ConfParts, 18);

    /// <summary>760 x 344: the four language buttons in three states.</summary>
    public static readonly UiSheet ConfLanguage = new(UiSet.ConfParts, 19);

    // ---- DISP_PARTS (table 0x780C90)

    /// <summary>160 x 160: the glow of the autosave icon.</summary>
    public static readonly UiSheet DispGlow = new(UiSet.DispParts, 1);

    /// <summary>160 x 160: the spinner of the autosave icon.</summary>
    public static readonly UiSheet DispSpinner = new(UiSet.DispParts, 2);

    /// <summary>128 x 56: the Saving... caption.</summary>
    public static readonly UiSheet DispSaving = new(UiSet.DispParts, 3);

    /// <summary>432 x 264: the mode banners (<c>popup_ja</c>).</summary>
    public static readonly UiSheet Banner = new(UiSet.DispParts, 10);

    /// <summary>16 x 888: the scroll bar track (<c>scroll_bar</c>).</summary>
    public static readonly UiSheet ScrollBar = new(UiSet.DispParts, 14);

    /// <summary>208 x 336: the scroll slider, cell (48, 24)-(80, 312) (<c>scroll_slider</c>).</summary>
    public static readonly UiSheet ScrollSlider = new(UiSet.DispParts, 15);

    /// <summary>384 x 256: the button icons of the guide line (<c>help_key</c>).</summary>
    public static readonly UiSheet GuideIcons = new(UiSet.DispParts, 16);

    /// <summary>512 x 256: the words of the guide line (<c>help_txt</c>).</summary>
    public static readonly UiSheet GuideWords = new(UiSet.DispParts, 17);

    /// <summary>64 x 64: the pointer arrow.</summary>
    public static readonly UiSheet Pointer = new(UiSet.DispParts, 18);

    /// <summary>448 x 264: the dialog buttons, Yes and No in a normal and a glow cell (<c>dialog_btn_ja</c>).</summary>
    public static readonly UiSheet DialogButtons = new(UiSet.DispParts, 19);

    /// <summary>824 x 416: the dialog window (<c>dialog_win</c>).</summary>
    public static readonly UiSheet DialogWindow = new(UiSet.DispParts, 20);

    /// <summary>1920 x 1080: the dialog backdrop (<c>dialog_BG</c>).</summary>
    public static readonly UiSheet DialogBackdrop = new(UiSet.DispParts, 21);

    // ---- MODEPNL_PARTS (table 0x7A7680: children 0..1 -> texture slots 382, 383)

    /// <summary>1920 x 96: the mode panel bar.</summary>
    public static readonly UiSheet ModeBar = new(UiSet.ModePnlParts, 0);

    /// <summary>1864 x 296: the mode panel buttons (eight columns of five states).</summary>
    public static readonly UiSheet ModeButtons = new(UiSet.ModePnlParts, 1);
}

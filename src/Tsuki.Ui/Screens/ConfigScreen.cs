// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (Ctl, conf_layout_at, conf_rect, conf_slider, conf_change, conf_nav, draw_config, en_radio_cell, en_toggle_cell, refresh_voice_rows)
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Ui.Screens;

/// <summary>What a config control is.</summary>
public enum CtlKind
{
    /// <summary>A tab button (System, Sound, Controls).</summary>
    Tab,

    /// <summary>The Recommended Settings button.</summary>
    Default,

    /// <summary>A slider's - button.</summary>
    Minus,

    /// <summary>A slider's + button.</summary>
    Plus,

    /// <summary>A slider's knob.</summary>
    Slider,

    /// <summary>A check box (page end at once for unread and read text, the skip confirmation).</summary>
    Toggle,

    /// <summary>One button of a radio pair (index = the pair, flag = the first, "on" button).</summary>
    Radio,

    /// <summary>A mute button.</summary>
    Mute,

    /// <summary>A test button.</summary>
    Test,

    /// <summary>A language button.</summary>
    Lang,

    /// <summary>One option of the button prompts setting on the Controls tab (index = <see cref="PromptSetting"/>: Auto, Keyboard, PlayStation, Xbox).</summary>
    Prompt,

    /// <summary>One option of the Display Mode row at the bottom of the System tab (package K19, a port addition: index 0 windowed, 1 fullscreen); shown when the game has a window.</summary>
    Display,

    /// <summary>One preset of the Window Size row under it (index into <see cref="IUiDisplaySettings.SizeChoices"/>).</summary>
    WindowSize,

    /// <summary>One option of the Screenshot Notice row of the System tab (package K24, a port addition: index 0 off, 1 on); shown when the game has port settings.</summary>
    ScreenshotNotice,

    /// <summary>One option of the Copy Text to Clipboard row of the System tab (package K21, a port addition: index 0 off, 1 on); shown when the game has port settings.</summary>
    CopyText,

    /// <summary>One option of the Text Hooker Output row under it (package K23: index 0 off, 1 on).</summary>
    TextHooker,

    /// <summary>One option of the Text WebSocket row under that (package K23: index 0 off, 1 on).</summary>
    TextWebSocket,

    /// <summary>The Text Window Opacity slider of the System tab (package K21: the PS4's own window-opacity byte, config +0x10, which its screen never showed).</summary>
    Opacity,

    /// <summary>One option of the Mute in Background row of the Sound tab (package K21: index 0 off, 1 on); shown when the game has port settings.</summary>
    BackgroundMute,
}

/// <summary>The port's rows at the bottom of the System tab (packages K19, K21, K23 and K24), in the order they are laid out.</summary>
public enum PortRow
{
    /// <summary>Display Mode (K19; a game with a window).</summary>
    DisplayMode,

    /// <summary>Window Size (K19; a game with a window and a monitor that takes a preset).</summary>
    WindowSize,

    /// <summary>Screenshot Notice (K24; a game with port settings): with the window rows, as it is about the picture of the window.</summary>
    ScreenshotNotice,

    /// <summary>Copy Text to Clipboard (K21; a game with port settings).</summary>
    CopyText,

    /// <summary>Text Hooker Output (K23; a game with port settings).</summary>
    TextHooker,

    /// <summary>Text WebSocket (K23; a game with port settings).</summary>
    TextWebSocket,

    /// <summary>Text Window Opacity (K21; always).</summary>
    Opacity,
}

/// <summary>A control of the config screen: its kind, an index and (for radios) whether it is the first button of its pair.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Index">Which tab, slider, pair, toggle or language.</param>
/// <param name="First">For a radio: the first (top) button of the pair.</param>
public readonly record struct Ctl(CtlKind Kind, int Index = 0, bool First = false);

/// <summary>
/// The config screen (docs/formats/adv_state.md 9, knife <c>Screen::Config</c>, sub_2AA80): three tabs, System (text speeds, auto mode, skip, fades, language),
/// Sound (music, SFX and system volumes, voices, one row per heard character) and Controls (pictures). The control positions come from the eboot's two config
/// pages (docs/research/knife_tables.md 5.17); the widgets are CONF_PARTS sprites in a scroll view. Changes apply at once and are written when the screen closes.
/// </summary>
public sealed class ConfigScreen : UiScreen, ISubMenu
{
    private const float ScrollFrames = 12f;
    private const float RadioDy = 6.637f;
    private const float PageTop = 176f;          // the controls page starts at (400, 73.007 units) = y 176
    private const float PillX = 728f;
    private const float PillGap = 12f;
    private static readonly RectF View = new(407f, 165f, 1402f, 858f);

    // The port's rows (packages K19 and K21) sit in a band added to the bottom of the System page: the page sheet (1432 x 1602) is drawn in three pieces, its top down to
    // sheet row 1500 (a flat part of the band, below the top of the last language button's cell), a slice of that flat band (rows 1400..1500: below the "Language"
    // label, whose glyphs end at row 1388 at most in the four languages) stretched into the extra band pixels (BandFor: 56 per row), and its bottom edge (rows 1500..1602)
    // moved down by as much. The group rule is the sheet's own (row 1310, above "Language"), copied.
    private const float PageY = 68.03f * UiMetrics.UnitY;        // the page's top in canvas pixels (164) at scroll 0
    private const float DisplaySplitRow = 1500f;
    private const float DisplayFillerTop = 1400f;
    private const float DisplayRuleRow = 1310f;
    private const float DisplayRuleDy = 78f;                     // the rule, 33 px below the last language button (the sheet's own rule sits 43 px below the previous group)
    private const float DisplayRowDy = 96f;                      // the top of the first row's pills, from the band's top
    private const float DisplayRowPitch = 56f;                   // each further row is one pitch lower
    private const float BandBottom = 8f;                         // below the last row (the band of K19 was 160 px for its two rows: 40 + 2 x 56 + 8)
    private const float DisplayLabelX = 431f;                    // where the sheet's own labels start ("Language" at sheet x 45)
    private const float DimmedSizeRow = 0.5f;                    // the size row while the game is fullscreen (its choice still applies: it switches to a window)

    // The Mute in Background row (package K21) sits in the Sound Settings box of the Sound page (sheet 1432 x 328): the box is cut below its last slider (sheet row 300,
    // y 464; the System Effects value box ends at row 268), a flat slice of it (rows 272..300) is stretched into SoundBand pixels, and the box's bottom edge, the
    // Voice Settings box and the character rows move down by as much.
    private const float SoundSplitRow = 300f;
    private const float SoundFillerTop = 272f;
    private const float SoundRowDy = 15f;                        // the pills, centred in the band
    private const double TwoLineHeadingScale = 0.36;             // a heading that takes two lines (^) is drawn a little smaller, so the lines of two rows stay apart

    /// <summary>The height of the band inserted into the Sound Settings box for the Mute in Background row (package K21).</summary>
    public const float SoundBandHeight = 64f;

    // The Text Window Opacity slider (package K21): a track of OpacityTrack pixels from the pill column, a knob, the value to the right; Left / Right step 5 %.
    private const float OpacityTrack = 400f;
    private const float OpacityKnobWidth = 22f;
    private const int OpacityStep = 5;

    private readonly float[] _target = new float[3];
    private readonly float[][] _preview = [[0f, 0f, 0f], [0f, 0f, 0f]];
    private IReadOnlyList<(int Row, int Face, int Name)> _voiceRows = [];
    private int _tab;
    private int _nextTab = -1;
    private float _content = 1f;
    private float _scroll;
    private float _from;
    private float _tween = ScrollFrames;
    private int _cursor = -1;
    private int _bar;
    // frames until a held - / + repeats; -1 = no press of this screen is being held (package K16: a Decide still held from the key that opened the screen, or a mouse button held
    // from the click that opened it, must not step the first slider: the cursor starts on the Unseen speed's minus button)
    private int _hold = -1;
    private readonly UiScrollBar _scrollBar = new();
    private Language? _sampleLanguage;
    private float _sampleScale = 1f;
    private Ctl? _dragCtl;
    private float _dragOffset;
    private int _langCool;
    private bool _dirty;
    private (int Row, int Col)?[] _remember = [(0, 0), (0, 0), null];

    /// <summary>Creates the config screen.</summary>
    public ConfigScreen()
    {
        Side = new SideFocus(this, 2);
        StartHidden();
    }

    /// <inheritdoc/>
    public int Tab => 2;

    /// <inheritdoc/>
    public SideFocus Side { get; }

    /// <summary>The open tab (0 System, 1 Sound, 2 Controls).</summary>
    public int CurrentTab => _tab;

    /// <summary>The scroll of the open tab in canvas pixels.</summary>
    public float Scroll => _scroll;

    /// <summary>The scroll bar (its hover and held state).</summary>
    internal UiScrollBar Bar => _scrollBar;

    /// <summary>The cursor: an index into <see cref="Layout"/>, or -1.</summary>
    public int Cursor => _cursor;

    /// <summary>The voice rows the Sound tab shows.</summary>
    public IReadOnlyList<(int Row, int Face, int Name)> VoiceRows => _voiceRows;

    /// <inheritdoc/>
    protected override float FadeInStep => 0.024f;

    /// <inheritdoc/>
    protected override float FadeOutStep => 0.048f;

    private ConfigBlock Cfg => Ctx.Services.Config;

    private bool English => Ctx.Language == Language.En;

    // ---- layout ---------------------------------------------------------------------------------------------------------------

    /// <summary>The controls of the open tab at scroll <paramref name="scroll"/> with their canvas positions (knife <c>conf_layout_at</c>).</summary>
    public List<(Ctl Ctl, float X, float Y)> LayoutAt(float scroll)
    {
        var v = new List<(Ctl, float, float)>
        {
            (new Ctl(CtlKind.Tab, 0), 408f, 96f),
            (new Ctl(CtlKind.Tab, 1), 656f, 96f),
            (new Ctl(CtlKind.Tab, 2), 888f, 96f),
            (new Ctl(CtlKind.Default), 1536f, 96f),
        };
        IReadOnlyList<UiConfigPage> pages = Ctx.Assets.Tables.ConfigPages;
        (float X, float Y) U(float x, float y) => (x * 3f, (y * UiMetrics.UnitY) - scroll);
        if (_tab == 0)
        {
            UiConfigPage page = pages[0];
            int pair = 0;
            int lang = 0;
            for (int r = 0; r < page.Rows.Count; r++)
            {
                UiConfigRow row = page.Rows[r];
                foreach (UiConfigControl c in row.Controls)
                {
                    switch (c.Kind)
                    {
                        case 3:
                            Add(v, new Ctl(CtlKind.Minus, r), U(c.X, c.Y));
                            break;
                        case 4:
                            Add(v, new Ctl(CtlKind.Slider, r), U(c.X, c.Y));
                            break;
                        case 5:
                            Add(v, new Ctl(CtlKind.Plus, r), U(c.X, c.Y));
                            break;
                        case 2:
                            Add(v, new Ctl(CtlKind.Toggle, r < 2 ? r : 2), U(c.X, c.Y));
                            break;
                        case >= 6 and <= 9:
                            pair = c.Kind - 6;
                            Add(v, new Ctl(CtlKind.Radio, pair, First: IsFirstOfPair(page, r)), U(c.X, c.Y + RadioDy));
                            break;
                        case 10:
                            // a language the data does not carry is not offered (package K25: another player's dump may lack one)
                            if (LanguageAvailable(lang))
                            {
                                Add(v, new Ctl(CtlKind.Lang, lang), U(c.X, c.Y));
                            }

                            lang++;
                            break;
                    }
                }
            }

            float top = PageY + DisplaySplitRow - scroll;
            IReadOnlyList<PortRow> band = SystemBandRows();
            for (int row = 0; row < band.Count; row++)
            {
                float y = top + DisplayRowDy + (DisplayRowPitch * row);
                float x = PillX;
                switch (band[row])
                {
                    case PortRow.DisplayMode:
                        for (int i = 0; i < 2; i++)
                        {
                            v.Add((new Ctl(CtlKind.Display, i), x, y));
                            x += UiPrompts.DisplayPillWidth(i) + PillGap;
                        }

                        break;
                    case PortRow.WindowSize:
                        for (int i = 0; i < Display!.SizeChoices.Count; i++)
                        {
                            v.Add((new Ctl(CtlKind.WindowSize, i), x, y));
                            x += UiPrompts.SizePillWidth + PillGap;
                        }

                        break;
                    case PortRow.ScreenshotNotice or PortRow.CopyText or PortRow.TextHooker or PortRow.TextWebSocket:
                        for (int i = 0; i < 2; i++)
                        {
                            v.Add((new Ctl(SwitchKind(band[row]), i), x, y));
                            x += UiPrompts.SwitchPillWidth + PillGap;
                        }

                        break;
                    default:
                        v.Add((new Ctl(CtlKind.Opacity), x, y));
                        break;
                }
            }
        }
        else if (_tab == 1)
        {
            UiConfigPage page = pages[1];
            float band = SoundBand;
            for (int r = 0; r < 7; r++)
            {
                // the voice rows of the page (the two radio pairs, rows 3..6) sit below the Sound Settings box and move down with the band inserted into it
                float dy = r >= 3 ? band : 0f;
                foreach (UiConfigControl c in page.Rows[r].Controls)
                {
                    switch (c.Kind)
                    {
                        case 3:
                            Add(v, new Ctl(CtlKind.Minus, r), U(c.X, c.Y), dy);
                            break;
                        case 4:
                            Add(v, new Ctl(CtlKind.Slider, r), U(c.X, c.Y), dy);
                            break;
                        case 5:
                            Add(v, new Ctl(CtlKind.Plus, r), U(c.X, c.Y), dy);
                            break;
                        case 2:
                            Add(v, new Ctl(CtlKind.Mute, r), U(c.X, c.Y), dy);
                            break;
                        case 1:
                            Add(v, new Ctl(CtlKind.Test, 0), U(c.X, c.Y), dy);
                            break;
                        case 6 or 7:
                            Add(v, new Ctl(CtlKind.Radio, c.Kind - 2, First: IsFirstOfPair(page, r)), U(c.X, c.Y + RadioDy), dy);
                            break;
                    }
                }
            }

            if (Port is not null)
            {
                float x = PillX;
                float y = PageY + SoundSplitRow - scroll + SoundRowDy;
                for (int i = 0; i < 2; i++)
                {
                    v.Add((new Ctl(CtlKind.BackgroundMute, i), x, y));
                    x += UiPrompts.SwitchPillWidth + PillGap;
                }
            }

            // the character rows are placed at run time over the shown rows (sub_29FA0): 370.01 + 50.607 n
            for (int n = 0; n < _voiceRows.Count; n++)
            {
                int k = _voiceRows[n].Row;
                float y = (n * 50.607f) + 370.01f;
                Add(v, new Ctl(CtlKind.Minus, 3 + k), U(245f, y), band);
                Add(v, new Ctl(CtlKind.Slider, 3 + k), U(120.67f, y - 44.8f), band);
                Add(v, new Ctl(CtlKind.Plus, 3 + k), U(452f, y), band);
                Add(v, new Ctl(CtlKind.Mute, 3 + k), U(476.33f, y - 4.9778f), band);
                Add(v, new Ctl(CtlKind.Test, 1 + k), U(560.33f, y - 3.7333f), band);
            }
        }

        else if (_tab == 2)
        {
            // the button prompts setting sits in the blank header band at the top of the controls page (page pixel rows 5 .. 46)
            float x = PillX;
            for (int i = 0; i < 4; i++)
            {
                v.Add((new Ctl(CtlKind.Prompt, i), x, PageTop + 6f - scroll));
                x += UiPrompts.PillWidth(i) + PillGap;
            }
        }

        return v;
    }

    private static void Add(List<(Ctl, float, float)> list, Ctl c, (float X, float Y) p, float dy = 0f) => list.Add((c, p.X, p.Y + dy));

    /// <summary>The port's display settings, or null for a game without a window (no display rows then).</summary>
    private IUiDisplaySettings? Display => Ctx.Services.Display;

    /// <summary>The port's own switches (package K21), or null for a game without them (no clipboard and background-mute rows then).</summary>
    private IUiPortSettings? Port => Ctx.Services.Port;

    /// <summary>True when the System tab carries the display rows (the game has a window).</summary>
    public bool HasDisplayRows => Display is not null;

    /// <summary>True when the tabs carry the port's switch rows (Copy Text to Clipboard, Mute in Background).</summary>
    public bool HasPortRows => Port is not null;

    /// <summary>
    /// The rows of the band at the bottom of the System tab, top to bottom: the display rows of a game with a window, then for a game with port settings the Screenshot
    /// Notice row (K24) and the three text-output rows (K21, K23), and the opacity slider always.
    /// </summary>
    public IReadOnlyList<PortRow> SystemBandRows()
    {
        var rows = new List<PortRow>(7);
        if (Display is { } display)
        {
            rows.Add(PortRow.DisplayMode);
            if (display.SizeChoices.Count > 0)
            {
                rows.Add(PortRow.WindowSize);
            }
        }

        if (Port is not null)
        {
            rows.Add(PortRow.ScreenshotNotice);
            rows.Add(PortRow.CopyText);
            rows.Add(PortRow.TextHooker);
            rows.Add(PortRow.TextWebSocket);
        }

        rows.Add(PortRow.Opacity);
        return rows;
    }

    /// <summary>The control kind of a switch row of the band (two Off / On pills).</summary>
    public static CtlKind SwitchKind(PortRow row) => row switch
    {
        PortRow.ScreenshotNotice => CtlKind.ScreenshotNotice,
        PortRow.CopyText => CtlKind.CopyText,
        PortRow.TextHooker => CtlKind.TextHooker,
        PortRow.TextWebSocket => CtlKind.TextWebSocket,
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "not a switch row"),
    };

    /// <summary>True for the kinds drawn as a pair of Off / On pills in the pill palette (the port's switches of packages K21, K23 and K24).</summary>
    public static bool IsSwitch(CtlKind kind) => kind is CtlKind.ScreenshotNotice or CtlKind.CopyText or CtlKind.TextHooker or CtlKind.TextWebSocket or CtlKind.BackgroundMute;

    /// <summary>The height of the band that holds <paramref name="rows"/> rows (160 for the two rows of K19).</summary>
    public static float BandFor(int rows) => DisplayRowDy - DisplayRowPitch + (DisplayRowPitch * rows) + BandBottom;

    private float SystemBand => BandFor(SystemBandRows().Count);

    /// <summary>The height of the band inserted into the Sound Settings box for the Mute in Background row (0 without port settings).</summary>
    private float SoundBand => Port is not null ? SoundBandHeight : 0f;

    /// <summary>The Text Window Opacity value, 0..100 %: the PS4's window-opacity u16 (config +0x10, boot value 255 = the PS4 look) that the HUD multiplies into the message window's alpha (textbox.md 7). The text is never touched by it.</summary>
    public int WindowOpacityPercent
    {
        get => (int)MathF.Round(Cfg.Hud10 * 100f / 255f);
        set => Cfg.Hud10 = (ushort)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0, 100) * 255f / 100f), 0, 255);
    }

    // the first control of a pair is the one on the page's upper row (the pair spans two rows with the same kind)
    private static bool IsFirstOfPair(UiConfigPage page, int row)
    {
        int kind = page.Rows[row].Controls.First(c => c.Kind is >= 6 and <= 10).Kind;
        return row == 0 || !page.Rows[row - 1].Controls.Any(c => c.Kind == kind);
    }

    /// <summary>The controls of the open tab at the current scroll.</summary>
    public List<(Ctl Ctl, float X, float Y)> Layout => LayoutAt(_scroll);

    /// <summary>The hit rectangle of a control placed at (<paramref name="x"/>, <paramref name="y"/>) (knife <c>conf_rect</c>).</summary>
    public RectF HitRect(Ctl c, float x, float y)
    {
        switch (c.Kind)
        {
            case CtlKind.Tab:
                return new RectF(x, y, 192, 80);
            case CtlKind.Default:
                return new RectF(x, y, 288, 72);
            case CtlKind.Minus or CtlKind.Plus:
                return new RectF(x, y, 80, 40);
            case CtlKind.Slider:
                return new RectF(x + 432f, y + (39f * UiMetrics.UnitY), 69f, 24f * UiMetrics.UnitY);
            case CtlKind.Toggle:
            {
                SheetCell cell = ToggleCell(c.Index, 0);
                return new RectF(x, y, cell.Width, 72);
            }

            case CtlKind.Radio:
            {
                SheetCell cell = RadioCell(c.Index, c.First, 0);
                return new RectF(x, y, cell.Width, 48);
            }

            case CtlKind.Lang:
                return new RectF(x, y + (7f * UiMetrics.UnitY), 168, 19f * UiMetrics.UnitY);
            case CtlKind.Prompt:
                return new RectF(x, y, UiPrompts.PillWidth(c.Index), UiPrompts.PillHeight);
            case CtlKind.Display:
                return new RectF(x, y, UiPrompts.DisplayPillWidth(c.Index), UiPrompts.PillHeight);
            case CtlKind.WindowSize:
                return new RectF(x, y, UiPrompts.SizePillWidth, UiPrompts.PillHeight);
            case CtlKind.ScreenshotNotice or CtlKind.CopyText or CtlKind.TextHooker or CtlKind.TextWebSocket or CtlKind.BackgroundMute:
                return new RectF(x, y, UiPrompts.SwitchPillWidth, UiPrompts.PillHeight);
            case CtlKind.Opacity:
                // the whole track answers the pointer, knob included at either end
                return new RectF(x - (OpacityKnobWidth / 2f), y, OpacityTrack + OpacityKnobWidth, UiPrompts.PillHeight);
            case CtlKind.Mute:
                return new RectF(x, y, 136, 64);
            default:
                return new RectF(x, y, 104, 56);
        }
    }

    /// <summary>
    /// The overseas btn_base1 cells (knife <c>en_radio_cell</c>, frames 0x35EA98..) for pair <paramref name="k"/>, its first or second button, and column
    /// <paramref name="col"/> (normal, hover, selected); English takes the wider cells.
    /// </summary>
    public SheetCell RadioCell(int k, bool first, int col)
    {
        float c = col;
        bool en = English;
        SheetCell Narrow(float y) => Cell(256f * c, y, (256f * c) + 240f, y + 48f);
        SheetCell Wide(float y) => col switch
        {
            0 => Cell(0f, y, 441f, y + 48f),
            1 => Cell(448f, y, 889f, y + 48f),
            _ => Cell(896f, y, 1337f, y + 48f),
        };

        return (k, first) switch
        {
            (0 or 4 or 5, true) => Cell(256f * c, 16f, (256f * c) + 120f, 64f),
            (0 or 4 or 5, false) when col == 2 => Cell(512f, 100f, 632f, 148f),
            (0 or 4 or 5, false) => Cell(256f * c, 104f, (256f * c) + 120f, 152f),
            (1, true) => Cell(256f * c, 192f, (256f * c) + 248f, 240f),
            (1, false) => Cell(256f * c, 280f, (256f * c) + 248f, 328f),
            (2, true) when col == 0 => Cell(0f, 368f, en ? 241f : 240f, 416f),
            (2, true) => Cell(256f * c, 368f, (256f * c) + 248f, 416f),
            (2, false) => en ? Wide(456f) : Narrow(456f),
            (3, true) => Narrow(544f),
            (_, false) when en => Wide(632f),
            _ => Narrow(632f),
        };
    }

    /// <summary>The overseas btn_base2 cell of check box <paramref name="t"/> in column <paramref name="col"/> (knife <c>en_toggle_cell</c>).</summary>
    public SheetCell ToggleCell(int t, int col)
    {
        float y = t < 2 ? 0f : 80f;
        float c = col;
        return English ? Cell(400f * c, y, (400f * c) + 392f, y + 72f) : Cell(8f + (304f * c), y, 296f + (304f * c), y + 72f);
    }

    private static SheetCell Cell(float x0, float y0, float x1, float y1) => new((int)x0, (int)y0, (int)x1, (int)y1);

    // ---- values ---------------------------------------------------------------------------------------------------------------

    /// <summary>(value, maximum, step) of slider row <paramref name="i"/> of the open tab.</summary>
    public (float Value, float Max, float Step) Slider(int i)
    {
        ConfigBlock c = Cfg;
        return (_tab, i) switch
        {
            (0, 0) => (c.UnseenTextSpeed, 250f, 1f),
            (0, 1) => (c.SeenTextSpeed, 250f, 1f),
            (0, 2) => (c.AutoModePageSpeed, 10000f, 50f),
            (0, _) => (c.AutoModeWait, 10000f, 50f),
            (_, 0) => (c.MusicVolume, 100f, 1f),
            (_, 1) => (c.SfxVolume, 100f, 1f),
            (_, 2) => (c.SystemSeVolume, 100f, 1f),
            (_, var k) => (c.GetCharacterVoiceVolume(k - 3), 100f, 1f),
        };
    }

    private void SetSlider(int i, float value)
    {
        (_, float max, _) = Slider(i);
        ushort v = (ushort)Math.Clamp((int)MathF.Round(value), 0, (int)max);
        ConfigBlock c = Cfg;
        ConfigChange kind = ConfigChange.Setting;
        switch (_tab, i)
        {
            case (0, 0):
                c.UnseenTextSpeed = v;
                break;
            case (0, 1):
                c.SeenTextSpeed = v;
                break;
            case (0, 2):
                c.AutoModePageSpeed = v;
                break;
            case (0, _):
                c.AutoModeWait = v;
                break;
            case (_, 0):
                c.MusicVolume = v;
                c.MusicVolumeCopy = v;
                kind = ConfigChange.Volume;
                break;
            case (_, 1):
                c.SfxVolume = v;
                kind = ConfigChange.Volume;
                break;
            case (_, 2):
                c.SystemSeVolume = v;
                kind = ConfigChange.Volume;
                break;
            default:
                c.SetCharacterVoiceVolume(i - 3, v);
                kind = ConfigChange.Volume;
                break;
        }

        Ctx.Services.ConfigChanged(kind);
        _dirty = true;
    }

    /// <summary>Whether a check, radio, mute, tab or language control is on.</summary>
    public bool IsOn(Ctl c)
    {
        ConfigBlock cfg = Cfg;
        switch (c.Kind)
        {
            case CtlKind.Toggle:
                return c.Index switch { 0 => cfg.PageEndInstantUnseen != 0, 1 => cfg.PageEndInstantSeen != 0, _ => cfg.SkipConfirm != 0 };
            case CtlKind.Radio:
            {
                bool top = c.Index switch
                {
                    0 => cfg.AutoSkipCleared != 0,
                    1 => cfg.FastForwardReadOnly == 0,
                    2 => cfg.TextAnimations == 0,
                    3 => cfg.SeenTextAnimations == 0,
                    4 => cfg.PlayAllVoices != 0,
                    _ => cfg.InterruptVoices != 0,
                };
                return top == c.First;
            }

            case CtlKind.Mute:
                return c.Index switch
                {
                    0 => cfg.MusicMute != 0,
                    1 => cfg.SfxMute != 0,
                    2 => cfg.SystemSeMute != 0,
                    _ => ((cfg.VoiceEnableBits >> (c.Index - 3)) & 1) == 0,
                };
            case CtlKind.Tab:
                return _tab == c.Index;
            case CtlKind.Lang:
            {
                int w = cfg.Language;
                int current = w is >= 1 and <= 3 ? w : 0;
                return c.Index == current;
            }

            case CtlKind.Prompt:
                return (int)Ctx.Services.PromptSetting == c.Index;
            case CtlKind.Display:
                return Display is { } d && d.Fullscreen == (c.Index == 1);
            case CtlKind.WindowSize:
                // the remembered windowed size stays marked in fullscreen (the row is dimmed then): it is the size a return to the window gives
                return Display is { } ds && c.Index < ds.SizeChoices.Count && ds.SizeChoices[c.Index] == ds.WindowSize;
            case CtlKind.ScreenshotNotice:
                return Port is { } pn && pn.ScreenshotNotice == (c.Index == 1);
            case CtlKind.CopyText:
                return Port is { } pc && pc.CopyTextToClipboard == (c.Index == 1);
            case CtlKind.TextHooker:
                return Port is { } ph && ph.TextHookerOutput == (c.Index == 1);
            case CtlKind.TextWebSocket:
                return Port is { } pw && pw.TextWebSocket == (c.Index == 1);
            case CtlKind.BackgroundMute:
                return Port is { } pm && pm.MuteInBackground == (c.Index == 1);
            default:
                return false;
        }
    }

    private void SetRadio(int k, bool first)
    {
        ConfigBlock cfg = Cfg;
        ConfigChange kind = ConfigChange.Setting;
        switch (k)
        {
            case 0:
                cfg.AutoSkipCleared = (ushort)(first ? 1 : 0);
                break;
            case 1:
                cfg.FastForwardReadOnly = (ushort)(first ? 0 : 1);
                break;
            case 2:
                cfg.TextAnimations = (byte)(first ? 0 : 1);
                break;
            case 3:
                cfg.SeenTextAnimations = (byte)(first ? 0 : 1);
                break;
            case 4:
                cfg.PlayAllVoices = (ushort)(first ? 100 : 0);
                kind = ConfigChange.Volume;
                break;
            default:
                cfg.InterruptVoices = (ushort)(first ? 1 : 0);
                break;
        }

        Ctx.Services.ConfigChanged(kind);
        _dirty = true;
    }

    // ---- actions --------------------------------------------------------------------------------------------------------------

    /// <summary>Applies control <paramref name="c"/>: <paramref name="d"/> = +-1 steps a slider (left / right), 0 clicks (knife <c>conf_change</c>).</summary>
    public void Change(Ctl c, int d, float pointerX = float.NaN)
    {
        ConfigBlock cfg = Cfg;
        switch (c.Kind)
        {
            case CtlKind.Tab:
                SwitchTab(c.Index);
                break;
            case CtlKind.Default:
                Ctx.Sound(UiSound.Open);
                Root.Push(new ConfirmDialog(ConfirmKind.Defaults, yes =>
                {
                    if (yes)
                    {
                        ResetDefaults();
                    }
                }));
                break;
            case CtlKind.Minus or CtlKind.Plus or CtlKind.Slider:
            {
                (float v, float max, float step) = Slider(c.Index);
                float nv;
                if (c.Kind == CtlKind.Minus)
                {
                    nv = v - step;
                }
                else if (c.Kind == CtlKind.Plus)
                {
                    nv = v + step;
                }
                else if (d != 0)
                {
                    nv = v + (step * d);
                }
                else
                {
                    // a touch on the track (sub_2B930 case 4): the knob runs 508 px from the track start at (x + 468)
                    (Ctl _, float x, float _) = Layout.First(t => t.Ctl == c);
                    nv = float.IsNaN(pointerX) ? v : MathF.Truncate(MathF.Max(0f, (pointerX - (x + 468f)) / 508f * max));
                }

                SetSlider(c.Index, nv);
                break;
            }

            case CtlKind.Toggle:
                Ctx.Sound(UiSound.Decide);
                switch (c.Index)
                {
                    case 0:
                        cfg.PageEndInstantUnseen = (byte)(cfg.PageEndInstantUnseen != 0 ? 0 : 1);
                        break;
                    case 1:
                        cfg.PageEndInstantSeen = (byte)(cfg.PageEndInstantSeen != 0 ? 0 : 1);
                        break;
                    default:
                        cfg.SkipConfirm = (byte)(cfg.SkipConfirm != 0 ? 0 : 1);
                        break;
                }

                Ctx.Services.ConfigChanged(ConfigChange.Setting);
                _dirty = true;
                break;
            case CtlKind.Radio:
                Ctx.Sound(UiSound.Decide);
                SetRadio(c.Index, c.First);
                break;
            case CtlKind.Mute:
                Ctx.Sound(UiSound.Decide);
                switch (c.Index)
                {
                    case 0:
                        cfg.MusicMute = (byte)(cfg.MusicMute != 0 ? 0 : 1);
                        break;
                    case 1:
                        cfg.SfxMute = (byte)(cfg.SfxMute != 0 ? 0 : 1);
                        break;
                    case 2:
                        cfg.SystemSeMute = (byte)(cfg.SystemSeMute != 0 ? 0 : 1);
                        break;
                    default:
                        cfg.VoiceEnableBits ^= 1u << (c.Index - 3);
                        break;
                }

                Ctx.Services.ConfigChanged(ConfigChange.Volume);
                _dirty = true;
                break;
            case CtlKind.Test:
                if (c.Index == 0)
                {
                    Ctx.Services.PlayTestSe();
                }
                else
                {
                    Ctx.Services.PlayTestVoice(c.Index - 1);
                }

                break;
            case CtlKind.Prompt:
                // an app setting, not part of the PS4 config block: the services keep it in their own file
                Ctx.Sound(UiSound.Decide);
                Ctx.Services.PromptSetting = (PromptSetting)Math.Clamp(c.Index, 0, 3);
                break;
            case CtlKind.Display:
                // the port's window mode (package K19): applied by the game at once and remembered by it (the byte Alt+Enter and F11 keep)
                if (Display is { } display)
                {
                    Ctx.Sound(UiSound.Decide);
                    display.Fullscreen = c.Index == 1;
                }

                break;
            case CtlKind.WindowSize:
                // a size preset: the game leaves fullscreen when it is in it, resizes the window and remembers the size in its settings file
                if (Display is { } sizes && c.Index < sizes.SizeChoices.Count)
                {
                    Ctx.Sound(UiSound.Decide);
                    sizes.WindowSize = sizes.SizeChoices[c.Index];
                }

                break;
            case CtlKind.ScreenshotNotice:
                // the port's switches (packages K21, K23, K24) live in its settings file; the game saves them itself
                if (Port is { } notice)
                {
                    Ctx.Sound(UiSound.Decide);
                    notice.ScreenshotNotice = c.Index == 1;
                }

                break;
            case CtlKind.CopyText:
                if (Port is { } copy)
                {
                    Ctx.Sound(UiSound.Decide);
                    copy.CopyTextToClipboard = c.Index == 1;
                }

                break;
            case CtlKind.TextHooker:
                if (Port is { } hooker)
                {
                    Ctx.Sound(UiSound.Decide);
                    hooker.TextHookerOutput = c.Index == 1;
                }

                break;
            case CtlKind.TextWebSocket:
                if (Port is { } socket)
                {
                    Ctx.Sound(UiSound.Decide);
                    socket.TextWebSocket = c.Index == 1;
                }

                break;
            case CtlKind.BackgroundMute:
                if (Port is { } mute)
                {
                    Ctx.Sound(UiSound.Decide);
                    mute.MuteInBackground = c.Index == 1;
                }

                break;
            case CtlKind.Opacity:
                // Left / Right (or a held - / + on a pad) step 5 %; a click lands the knob under the pointer; decide alone does nothing, as on the PS4's sliders
                if (d != 0)
                {
                    SetOpacity(WindowOpacityPercent + (OpacityStep * d));
                }
                else if (!float.IsNaN(pointerX))
                {
                    (Ctl _, float x, float _) = Layout.First(t => t.Ctl == c);
                    OpacityTo(x, pointerX);
                }

                break;
            case CtlKind.Lang:
                Ctx.Sound(UiSound.Decide);
                if (cfg.Language != c.Index)
                {
                    cfg.Language = (short)c.Index;
                    _dirty = true;
                    Ctx.Services.ConfigChanged(ConfigChange.Language);
                    _langCool = 5;
                    _voiceRows = BuildVoiceRows();
                }

                break;
        }
    }

    /// <summary>True when the data carries language <paramref name="index"/> (0 ja, 1 en, 2 zc, 3 zt; package K25).</summary>
    private bool LanguageAvailable(int index) => Ctx.Assets.AvailableLanguages.Contains((Language)index);

    private void SetOpacity(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (percent == WindowOpacityPercent)
        {
            return;
        }

        WindowOpacityPercent = percent;
        Ctx.Services.ConfigChanged(ConfigChange.Setting);
        _dirty = true;
    }

    // moves the opacity knob so that its centre is at canvas x <paramref name="centreX"/> on a track that starts at x, in 5 % steps
    private void OpacityTo(float x, float centreX)
    {
        float t = Math.Clamp((centreX - x) / OpacityTrack, 0f, 1f);
        SetOpacity((int)MathF.Round(t * 100f / OpacityStep) * OpacityStep);
    }

    private void ResetDefaults()
    {
        short language = Cfg.Language;
        Cfg.CopyFrom(ConfigBlock.CreateBootDefaults());
        Cfg.Language = language;
        Ctx.Services.ConfigChanged(ConfigChange.Reset);
        _dirty = true;
    }

    private void SwitchTab(int tab)
    {
        if (tab != _tab || _nextTab >= 0)
        {
            _scrollBar.Release();
            _dragCtl = null;
            _nextTab = tab;
            Ctx.Sound(UiSound.Decide);
        }
    }

    // ---- voice rows -----------------------------------------------------------------------------------------------------------

    private List<(int Row, int Face, int Name)> BuildVoiceRows()
    {
        // PS4 VA 0x3DF20 (knife sub_29FA0): a row shows once its character has spoken (bit 4 of its row word, G[0x1A6850[r]], set by _VPLY); rows 15 / 16
        // keep the unknown face (18) until bit 1, the rows outside mask 0x430C9 (= the rows in 0x3CF36) the unknown name (19) until bit 2. Row 0 (Shiki)
        // depends on where the game is (0x3E285, 0x3E2CD): in a story (game mode word 0xE20CC8 != 0), hidden while F023 is 0, the unknown face (19) in
        // script 1 (00_00) and the unknown face and name in scripts 2 and 3 (00_01, 00_02; the script word 0x86AA18); from the title, unknown until
        // flowchart node 485 is reached, then face 19 until node 1 is. The rows never depend on Play All Voices (0x3DF20 does not read config +0x16).
        var rows = new List<(int, int, int)>();
        bool inGame = Root.Stack.Count == 0 || Root.Stack[0] is not TitleScreen;
        IReadOnlyList<int> words = Ctx.Assets.Tables.VoiceRowWords;
        for (int r = 0; r < Math.Min(19, words.Count); r++)
        {
            int w = Ctx.Services.Global(words[r]);
            if ((w & 4) == 0)
            {
                continue;
            }

            int face;
            int name;
            if (r == 0)
            {
                if (inGame)
                {
                    if (Ctx.Services.Phase == 0)
                    {
                        continue;
                    }

                    (face, name) = Ctx.Services.Script switch
                    {
                        1 => (19, 0),
                        2 or 3 => (18, 19),
                        _ => (0, 0),
                    };
                }
                else if (!Ctx.Services.SceneBit(485))
                {
                    (face, name) = (18, 19);
                }
                else
                {
                    (face, name) = (Ctx.Services.SceneBit(1) ? 0 : 19, 0);
                }
            }
            else
            {
                face = r is >= 15 and <= 16 && (w & 1) == 0 ? 18 : r;
                name = ((0x3CF36 >> r) & 1) != 0 && (w & 2) == 0 ? 19 : r;
            }

            rows.Add((r, face, name));
        }

        return rows;
    }

    private float MaxScroll(int tab) => tab switch
    {
        0 => 750f + SystemBand,
        1 => MathF.Max(122f * _voiceRows.Count, 113f) - 113f + SoundBand,
        // the PS4 controls pictures are 1528 texels tall (CONF_PARTS 16 / 17); the page starts at y 176 and the view ends at y 1023
        _ => MathF.Max(0f, (73.007f * UiMetrics.UnitY) + 1528f - (View.Y + View.H)),
    };

    /// <summary>Scrolls the open tab to <paramref name="v"/> pixels at once (the screenshot tool and the tests).</summary>
    internal void ScrollNow(float v)
    {
        v = MathF.Truncate(Math.Clamp(v, 0f, MaxScroll(_tab)));
        _target[_tab] = v;
        _scroll = v;
        _tween = ScrollFrames;
    }

    /// <summary>The largest scroll of the open tab.</summary>
    internal float ScrollMaximum => MaxScroll(_tab);

    /// <summary>Puts the cursor on control <paramref name="c"/> of the open tab and scrolls to it (the screenshot tool and the tests); nothing when it is not laid out.</summary>
    internal void CursorTo(Ctl c)
    {
        int at = Layout.FindIndex(t => t.Ctl == c);
        if (at >= 0)
        {
            _cursor = at;
            FollowCursor();
        }
    }

    private void ScrollTo(float v)
    {
        v = MathF.Truncate(Math.Clamp(v, 0f, MaxScroll(_tab)));
        if (v != _target[_tab])
        {
            _target[_tab] = v;
            _from = _scroll;
            _tween = 0f;
        }
    }

    // ---- navigation grid ------------------------------------------------------------------------------------------------------

    private List<List<int>> Rows(List<(Ctl Ctl, float X, float Y)> layout)
    {
        int At(Ctl c) => layout.FindIndex(t => t.Ctl == c);
        List<int> Row(params Ctl[] cs) => [.. cs.Select(At).Where(i => i >= 0)];
        var rows = new List<List<int>>();
        if (_tab == 0)
        {
            for (int i = 0; i < 4; i++)
            {
                rows.Add(i < 2
                    ? Row(new Ctl(CtlKind.Minus, i), new Ctl(CtlKind.Slider, i), new Ctl(CtlKind.Plus, i), new Ctl(CtlKind.Toggle, i))
                    : Row(new Ctl(CtlKind.Minus, i), new Ctl(CtlKind.Slider, i), new Ctl(CtlKind.Plus, i)));
            }

            rows.Add(Row(new Ctl(CtlKind.Radio, 0, true), new Ctl(CtlKind.Toggle, 2)));
            rows.Add(Row(new Ctl(CtlKind.Radio, 0, false)));
            for (int k = 1; k < 4; k++)
            {
                rows.Add(Row(new Ctl(CtlKind.Radio, k, true)));
                rows.Add(Row(new Ctl(CtlKind.Radio, k, false)));
            }

            for (int k = 0; k < 4; k++)
            {
                if (LanguageAvailable(k))
                {
                    rows.Add(Row(new Ctl(CtlKind.Lang, k)));
                }
            }

            foreach (PortRow band in SystemBandRows())
            {
                switch (band)
                {
                    case PortRow.DisplayMode:
                        rows.Add(Row(new Ctl(CtlKind.Display, 0), new Ctl(CtlKind.Display, 1)));
                        break;
                    case PortRow.WindowSize:
                        rows.Add(Row([.. Enumerable.Range(0, Display!.SizeChoices.Count).Select(i => new Ctl(CtlKind.WindowSize, i))]));
                        break;
                    case PortRow.ScreenshotNotice or PortRow.CopyText or PortRow.TextHooker or PortRow.TextWebSocket:
                        rows.Add(Row(new Ctl(SwitchKind(band), 0), new Ctl(SwitchKind(band), 1)));
                        break;
                    default:
                        rows.Add(Row(new Ctl(CtlKind.Opacity)));
                        break;
                }
            }
        }
        else if (_tab == 1)
        {
            for (int i = 0; i < 3; i++)
            {
                rows.Add(i == 1
                    ? Row(new Ctl(CtlKind.Minus, i), new Ctl(CtlKind.Slider, i), new Ctl(CtlKind.Plus, i), new Ctl(CtlKind.Mute, i), new Ctl(CtlKind.Test, 0))
                    : Row(new Ctl(CtlKind.Minus, i), new Ctl(CtlKind.Slider, i), new Ctl(CtlKind.Plus, i), new Ctl(CtlKind.Mute, i)));
            }

            if (Port is not null)
            {
                rows.Add(Row(new Ctl(CtlKind.BackgroundMute, 0), new Ctl(CtlKind.BackgroundMute, 1)));
            }

            for (int k = 4; k < 6; k++)
            {
                rows.Add(Row(new Ctl(CtlKind.Radio, k, true)));
                rows.Add(Row(new Ctl(CtlKind.Radio, k, false)));
            }

            foreach ((int row, _, _) in _voiceRows)
            {
                rows.Add(Row(new Ctl(CtlKind.Minus, 3 + row), new Ctl(CtlKind.Slider, 3 + row), new Ctl(CtlKind.Plus, 3 + row), new Ctl(CtlKind.Mute, 3 + row), new Ctl(CtlKind.Test, 1 + row)));
            }
        }

        else if (_tab == 2)
        {
            rows.Add(Row(new Ctl(CtlKind.Prompt, 0), new Ctl(CtlKind.Prompt, 1), new Ctl(CtlKind.Prompt, 2), new Ctl(CtlKind.Prompt, 3)));
        }

        return rows;
    }

    /// <summary>The cursor grid of the open tab: the controls of each row left to right (the tab bar sits above row 0).</summary>
    public IReadOnlyList<IReadOnlyList<Ctl>> GridRows()
    {
        List<(Ctl Ctl, float X, float Y)> layout = Layout;
        return [.. Rows(layout).Select(r => (IReadOnlyList<Ctl>)[.. r.Select(i => layout[i].Ctl)])];
    }

    private (int Row, int Col)? Position(List<List<int>> rows, int cursor)
    {
        for (int r = 0; r < rows.Count; r++)
        {
            int c = rows[r].IndexOf(cursor);
            if (c >= 0)
            {
                return (r, c);
            }
        }

        return null;
    }

    private void Nav(int dx, int dy, bool decideHeld)
    {
        List<(Ctl Ctl, float X, float Y)> layout = Layout;
        List<List<int>> rows = Rows(layout);
        (int Row, int Col)? pos = Position(rows, _cursor);
        int before = _cursor;
        if (pos is null)
        {
            int bar = _cursor is >= 0 and < 4 ? _cursor : _bar;
            if (dx < 0)
            {
                if (bar > 0)
                {
                    _cursor = bar - 1;
                }
                else if (Side.Menu is not null)
                {
                    _bar = 0;
                    _cursor = 0;
                    Side.Enter();
                    return;
                }
            }
            else if (dx > 0)
            {
                _cursor = Math.Min(bar + 1, 3);
            }
            else if (dy > 0 && rows.Count > 0 && rows[0].Count > 0)
            {
                _cursor = rows[0][0];
            }
            else
            {
                _cursor = bar;
            }

            if (_cursor is >= 0 and < 4)
            {
                _bar = _cursor;
            }
        }
        else
        {
            (int r, int c) = pos.Value;
            if (dx != 0 && decideHeld && layout[rows[r][c]].Ctl.Kind == CtlKind.Slider)
            {
                Change(layout[rows[r][c]].Ctl, dx);
                return;
            }

            if (dx != 0 && layout[rows[r][c]].Ctl.Kind == CtlKind.Opacity)
            {
                // the port's slider is alone in its row: Left and Right step its value (the side menu is reached from the rows above)
                Change(layout[rows[r][c]].Ctl, dx);
                return;
            }

            if (dy < 0)
            {
                _cursor = r == 0 ? _bar : rows[r - 1][Math.Min(c, rows[r - 1].Count - 1)];
            }
            else if (dy > 0 && r + 1 < rows.Count)
            {
                _cursor = rows[r + 1][Math.Min(c, rows[r + 1].Count - 1)];
            }
            else if (dx < 0)
            {
                if (c > 0)
                {
                    _cursor = rows[r][c - 1];
                }
                else if (Side.Menu is not null)
                {
                    Side.Enter();
                    return;
                }
            }
            else if (dx > 0 && c + 1 < rows[r].Count)
            {
                _cursor = rows[r][c + 1];
            }
        }

        if (_cursor != before)
        {
            Ctx.Sound(UiSound.Cursor);
        }

        FollowCursor();
    }

    // sub_29930: keep the selected control in view. With y its unscrolled position, the scroll goes to y - 260 when that is above it, else to y - 870 when below
    // it; voice volume knobs use y - 152 / y - 772.
    private void FollowCursor()
    {
        List<(Ctl Ctl, float X, float Y)> at0 = LayoutAt(0f);
        if (_cursor < 0 || _cursor >= at0.Count)
        {
            return;
        }

        (Ctl c, _, float y) = at0[_cursor];
        if (c.Kind is CtlKind.Tab or CtlKind.Default)
        {
            return;
        }

        bool knob = _tab == 1 && c.Kind == CtlKind.Slider && c.Index >= 3;
        float top = knob ? y - 260f + 108f : y - 260f;
        float bottom = knob ? y - 870f + 98f : y - 870f;
        float cur = _target[_tab];
        if (top < cur)
        {
            ScrollTo(top);
        }
        else if (bottom > cur)
        {
            ScrollTo(bottom);
        }
    }

    // ---- frame ----------------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override void OnOpened()
    {
        _voiceRows = BuildVoiceRows();
        _scroll = _target[_tab];
        _tween = ScrollFrames;
        _cursor = _remember[_tab] is { } rc ? CursorOf(rc) : _tab;
        _bar = _tab;
        if (FromTitle)
        {
            Ctx.Backdrop.Fade(0.02f);
        }
    }

    private bool FromTitle => Root.Stack.Count > 0 && Root.Stack[0] is TitleScreen;

    private int CursorOf((int Row, int Col) rc)
    {
        List<List<int>> rows = Rows(Layout);
        if (rc.Row >= rows.Count || rows[rc.Row].Count == 0)
        {
            return _tab;
        }

        return rows[rc.Row][Math.Min(rc.Col, rows[rc.Row].Count - 1)];
    }

    /// <inheritdoc/>
    public override void OnClosed()
    {
        Ctx.Services.StopTests();
        if (_dirty)
        {
            Ctx.Services.CommitSystemData();
            _dirty = false;
        }
    }

    private bool Busy => Closing || _nextTab >= 0 || _content < 1f || _langCool > 0 || Root.Top != this;

    /// <inheritdoc/>
    public override void StepBackground() => StepTick();

    /// <inheritdoc/>
    public override void Step(in UiInput input)
    {
        StepTick();
        if (Closing || Root.Top != this)
        {
            return;
        }

        if (Side.Step(input, Ctx))
        {
            return;
        }

        if (_nextTab >= 0 || _content < 1f || _langCool > 0)
        {
            return;
        }

        if (input.Cancel)
        {
            Ctx.Sound(UiSound.Buzzer);
            Close();
            return;
        }

        if (Side.ClickOnTab(input, Ctx))
        {
            return;
        }

        List<(Ctl Ctl, float X, float Y)> layout = Layout;
        if (input.L1 || input.R1)
        {
            SwitchTab((_tab + (input.R1 ? 1 : 2)) % 3);
            return;
        }

        // the scroll bar: drag the thumb, click the track for a page (package K12)
        float max = MaxScroll(_tab);
        if (StepScrollBar(input, max))
        {
            return;
        }

        // a slider the left button has hold of follows the pointer wherever it goes
        if (_dragCtl is { } dragged)
        {
            if (input.PointerHeld)
            {
                int at = layout.FindIndex(t => t.Ctl == dragged);
                if (at >= 0 && dragged.Kind == CtlKind.Opacity)
                {
                    OpacityTo(layout[at].X, input.PointerX - _dragOffset);
                }
                else if (at >= 0)
                {
                    SliderTo(dragged, layout[at].X, input.PointerX - _dragOffset, snap: false);
                }

                return;
            }

            _dragCtl = null;
        }

        int dx = input.Left ? -1 : input.Right ? 1 : 0;
        int dy = input.Up ? -1 : input.Down ? 1 : 0;
        if (dx != 0 || dy != 0)
        {
            Nav(dx, dy, input.DecideHeld);
        }

        if (input.Wheel != 0 && MaxScroll(_tab) > 0f)
        {
            ScrollTo(_target[_tab] - (input.Wheel * 60f));
        }

        if (input.PointerMoved)
        {
            for (int i = layout.Count - 1; i >= 0; i--)
            {
                RectF r = ContentRect(layout, i);
                if (r.Contains(input.PointerX, input.PointerY))
                {
                    if (_cursor != i)
                    {
                        _cursor = i;
                        Ctx.Sound(UiSound.Cursor);
                    }

                    break;
                }
            }
        }

        int hit = -1;
        for (int i = layout.Count - 1; i >= 0; i--)
        {
            if (ContentRect(layout, i).Contains(input.PointerX, input.PointerY))
            {
                hit = i;
                break;
            }
        }

        if (input.Click && hit >= 0)
        {
            _cursor = hit;
            _hold = 20;
            Ctl clicked = layout[hit].Ctl;
            if (clicked.Kind == CtlKind.Slider)
            {
                BeginSliderDrag(clicked, layout[hit].X, input.PointerX);
            }
            else if (clicked.Kind == CtlKind.Opacity)
            {
                // the knob jumps under the pointer and follows it while the button stays down
                _dragOffset = 0f;
                OpacityTo(layout[hit].X, input.PointerX);
                _dragCtl = clicked;
            }
            else
            {
                Change(clicked, 0, input.PointerX);
            }
        }
        else if (input.Decide && !input.Click && _cursor >= 0 && _cursor < layout.Count)
        {
            Ctl c = layout[_cursor].Ctl;
            if (c.Kind != CtlKind.Slider)
            {
                _hold = 20;
                Change(c, 0);
            }
        }

        // a held - / + (decide on the cursor, or the pointer) repeats after 20 frames; a slider under a held pointer follows it
        Ctl? held = null;
        if (input.PointerHeld && hit >= 0)
        {
            held = layout[hit].Ctl;
        }
        else if (input.DecideHeld && _cursor >= 0 && _cursor < layout.Count)
        {
            held = layout[_cursor].Ctl;
        }

        if (held is { Kind: CtlKind.Minus or CtlKind.Plus } hc && !input.Decide && !input.Click)
        {
            if (_hold > 0)
            {
                _hold--;
            }
            else if (_hold == 0)
            {
                Change(hc, 0);
            }
        }

        if (!input.Decide && !input.Click && !input.DecideHeld && !input.PointerHeld)
        {
            _hold = -1;
        }

        if (_cursor >= 0 && _nextTab < 0)
        {
            _remember[_tab] = Position(Rows(layout), _cursor) ?? _remember[_tab];
        }
    }

    // ---- the mouse on the scroll bar and the sliders (package K12) ----------------------------------------------------------------

    private const float SliderTrack = 508f;       // the knob's travel: its centre runs from x + 468 for 0 to x + 976 for the maximum
    private const float SliderCentre = 468f;
    private const float DotPitch = SliderTrack / 4f;     // the five step dots of the track
    private const float DotSnap = 10f;
    private const float PageScroll = 758f;        // a click on the track pages by the view's height less a few rows

    private bool StepScrollBar(in UiInput input, float max)
    {
        ScrollBarUse use = _scrollBar.Step(input, max > 0f ? _scroll / max : 0f, 608f, max > 0f);
        if (use.Fraction is { } f)
        {
            ScrollNow(f * max);
        }
        else if (use.Page != 0)
        {
            ScrollTo(_target[_tab] + (use.Page * PageScroll));
        }

        return use.Consumed;
    }

    // the left button went down on a slider's track: on the knob it grabs it where it is, elsewhere the knob jumps under the pointer (to a step dot when one is close)
    private void BeginSliderDrag(Ctl c, float x, float pointerX)
    {
        (float v, float max, _) = Slider(c.Index);
        float centre = x + SliderCentre + (max > 0f ? v / max * SliderTrack : 0f);
        if (MathF.Abs(pointerX - centre) <= 34.5f)
        {
            _dragOffset = pointerX - centre;
        }
        else
        {
            _dragOffset = 0f;
            SliderTo(c, x, pointerX, snap: true);
        }

        _dragCtl = c;
    }

    // moves slider c's knob so that its centre is at canvas x <paramref name="centreX"/>; auto mode times stay on their 50 ms steps
    private void SliderTo(Ctl c, float x, float centreX, bool snap)
    {
        (float v, float max, float step) = Slider(c.Index);
        float t = (centreX - (x + SliderCentre)) / SliderTrack;
        if (snap)
        {
            for (int k = 0; k < 5; k++)
            {
                if (MathF.Abs(centreX - (x + SliderCentre + (k * DotPitch))) <= DotSnap)
                {
                    t = k / 4f;
                    break;
                }
            }
        }

        float nv = Math.Clamp(t, 0f, 1f) * max;
        nv = step > 1f ? MathF.Round(nv / step) * step : MathF.Round(nv);
        if (nv != v)
        {
            SetSlider(c.Index, nv);
        }
    }

    private RectF ContentRect(List<(Ctl Ctl, float X, float Y)> layout, int i)
    {
        (Ctl c, float x, float y) = layout[i];
        RectF r = HitRect(c, x, y);
        if (c.Kind == CtlKind.Slider)
        {
            // the whole track answers the pointer (a click on a step dot or anywhere on the line moves the knob there), not only the knob (package K12)
            r = new RectF(r.X, r.Y, SliderTrack + r.W, r.H);
        }

        if (c.Kind is not (CtlKind.Tab or CtlKind.Default))
        {
            r = r.Intersect(View);
        }

        return r;
    }

    private void StepTick()
    {
        if (_langCool > 0)
        {
            _langCool--;
        }

        if (_tween < ScrollFrames)
        {
            _tween = MathF.Min(ScrollFrames, _tween + 1f);
            float t = _tween / ScrollFrames;
            _scroll = _from + ((_target[_tab] - _from) * -(t * (t - 2f)));
        }

        if (_nextTab >= 0)
        {
            _content -= 0.05f;
            if (_content <= 0f)
            {
                _content = 0f;
                _tab = _nextTab;
                _nextTab = -1;
                _scroll = _target[_tab];
                _tween = ScrollFrames;
                _cursor = _remember[_tab] is { } rc ? CursorOf(rc) : _bar;
            }
        }
        else
        {
            _content = MathF.Min(1f, _content + 0.05f);
        }

        if (_tab == 0)
        {
            StepPreview();
        }

        if (_hold == 0 && _dirty && Root.Top == this)
        {
            // the file is written once the hold ends (sub_2B930); the services decide when to flush
        }
    }

    // sub_2D570: the unread / read message speed samples type out at the configured speed; once ten characters are fully shown they stay for the auto-mode
    // delay, then start over.
    private void StepPreview()
    {
        float hold = Cfg.AutoModeWait * 60f / 1000f;
        for (int k = 0; k < 2; k++)
        {
            float ms = k == 0 ? Cfg.UnseenTextSpeed : Cfg.SeenTextSpeed;
            float[] s = _preview[k];
            float speed = ms < 1f ? 768f : 16.667f / ms;
            float edge = speed >= 768f ? 0f : speed * MathF.Min((ms * 0.1f) / 35f, 0.5f) * 60f;
            bool full;
            if (speed >= 768f)
            {
                s[0] = 768f;
                s[1] = 768f;
                full = true;
            }
            else
            {
                s[0] = MathF.Min(s[0] + speed, 768f);
                s[1] = MathF.Max(s[1], s[0] - edge);
                full = s[1] >= 10f;
            }

            if (full)
            {
                s[2] += 1f;
                if (s[2] > hold)
                {
                    s[0] = 0f;
                    s[1] = 0f;
                    s[2] = 0f;
                }
            }
        }
    }

    // ---- drawing --------------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override void Draw(UiPainter p)
    {
        using UiPainter.AlphaScope scope = p.WithAlpha(Fade);
        p.Sprite(UiSheets.ConfPanel, new RectF(0, 0, 1560, 1080), new RectF(360, 0, 1560, 1080));
        p.Sprite(UiSheets.ConfHeader, new RectF(0, 0, 1416, 72), new RectF(400, 96, 1416, 72));
        List<(Ctl Ctl, float X, float Y)> layout = Layout;
        using (p.WithClip(View))
        using (p.WithAlpha(_content))
        {
            DrawPage(p);
            DrawNumbers(p);
            if (_tab == 0)
            {
                DrawPreview(p);
            }

            for (int i = 0; i < layout.Count; i++)
            {
                if (layout[i].Ctl.Kind is CtlKind.Tab or CtlKind.Default)
                {
                    continue;
                }

                DrawControl(p, layout[i].Ctl, layout[i].X, layout[i].Y, i);
            }
        }

        for (int i = 0; i < layout.Count; i++)
        {
            if (layout[i].Ctl.Kind is CtlKind.Tab or CtlKind.Default)
            {
                DrawControl(p, layout[i].Ctl, layout[i].X, layout[i].Y, i);
            }
        }

        float max = MaxScroll(_tab);
        if (max > 0f)
        {
            UiWidgets.ScrollBar(p, _scroll / max, 608f, _content, _scrollBar);
        }

        UiWidgets.Guide(p, UiWidgets.GuideConfig, decideIsCross: Ctx.Services.DecideIsCross);
    }

    private (float X, float Y) U(float x, float y) => (x * 3f, (y * UiMetrics.UnitY) - _scroll);

    // a heading of the band at the sheet's label x, centred on the row; a two-line one (^) a little smaller
    private static void DrawHeading(UiPainter p, string text, float centre) =>
        UiPrompts.DrawText(p, text, DisplayLabelX, centre, 1f, text.Contains('^', StringComparison.Ordinal) ? TwoLineHeadingScale : 0.4);

    private void DrawPage(UiPainter p)
    {
        if (_tab == 0)
        {
            (float x, float y) = U(128.667f, 68.03f);
            (int w, int h) = p.SizeOf(UiSheets.ConfSystemPage);
            if (h > DisplaySplitRow)
            {
                // the page with the port's band inserted (see the constants): top, the stretched flat slice, the bottom edge moved down, the group rule, the headings
                IReadOnlyList<PortRow> rows = SystemBandRows();
                float band = BandFor(rows.Count);
                p.Sprite(UiSheets.ConfSystemPage, new RectF(0, 0, w, DisplaySplitRow), new RectF(x, y, w, DisplaySplitRow));
                p.Sprite(UiSheets.ConfSystemPage, new RectF(0, DisplayFillerTop, w, DisplaySplitRow - DisplayFillerTop), new RectF(x, y + DisplaySplitRow, w, band));
                p.Sprite(UiSheets.ConfSystemPage, new RectF(0, DisplaySplitRow, w, h - DisplaySplitRow), new RectF(x, y + DisplaySplitRow + band, w, h - DisplaySplitRow));
                float top = y + DisplaySplitRow;
                p.Sprite(UiSheets.ConfSystemPage, new RectF(0, DisplayRuleRow, w, 1), new RectF(x, top + DisplayRuleDy, w, 1));
                for (int i = 0; i < rows.Count; i++)
                {
                    float centre = top + DisplayRowDy + (DisplayRowPitch * i) + (UiPrompts.PillHeight / 2f) + 2f;
                    switch (rows[i])
                    {
                        case PortRow.DisplayMode:
                            UiPrompts.DrawText(p, UiPrompts.DisplayHeading(Ctx.Language), DisplayLabelX, centre, 1f);
                            break;
                        case PortRow.WindowSize:
                            UiPrompts.DrawText(p, UiPrompts.SizeHeading(Ctx.Language), DisplayLabelX, centre, Display is { Fullscreen: true } ? DimmedSizeRow : 1f);
                            break;
                        case PortRow.ScreenshotNotice:
                            DrawHeading(p, UiPrompts.ScreenshotNoticeHeading(Ctx.Language), centre);
                            break;
                        case PortRow.CopyText:
                            DrawHeading(p, UiPrompts.CopyTextHeading(Ctx.Language), centre);
                            break;
                        case PortRow.TextHooker:
                            DrawHeading(p, UiPrompts.TextHookerHeading(Ctx.Language), centre);
                            break;
                        case PortRow.TextWebSocket:
                            DrawHeading(p, UiPrompts.WebSocketHeading(Ctx.Language), centre);
                            break;
                        default:
                            DrawHeading(p, UiPrompts.OpacityHeading(Ctx.Language), centre);
                            break;
                    }
                }
            }
            else
            {
                p.Sprite(UiSheets.ConfSystemPage, new RectF(0, 0, w, h), new RectF(x, y, w, h));
            }
        }
        else if (_tab == 1)
        {
            (float x, float y) = U(128.667f, 68.03f);
            float band = SoundBand;
            if (band > 0f)
            {
                // the Sound Settings box with the Mute in Background band inserted below its last slider (see the constants); the heading sits at the sheet's own label x
                p.Sprite(UiSheets.ConfSoundPage, new RectF(0, 0, 1432, SoundSplitRow), new RectF(x, y, 1432, SoundSplitRow));
                p.Sprite(UiSheets.ConfSoundPage, new RectF(0, SoundFillerTop, 1432, SoundSplitRow - SoundFillerTop), new RectF(x, y + SoundSplitRow, 1432, band));
                p.Sprite(UiSheets.ConfSoundPage, new RectF(0, SoundSplitRow, 1432, 328 - SoundSplitRow), new RectF(x, y + SoundSplitRow + band, 1432, 328 - SoundSplitRow));
                UiPrompts.DrawText(p, UiPrompts.MuteHeading(Ctx.Language), DisplayLabelX, y + SoundSplitRow + SoundRowDy + (UiPrompts.PillHeight / 2f) + 2f, 1f);
            }
            else
            {
                p.Sprite(UiSheets.ConfSoundPage, new RectF(0, 0, 1432, 328), new RectF(x, y, 1432, 328));
            }

            (x, y) = U(128.667f, 199.94f);
            p.Sprite(UiSheets.ConfVoicePage, new RectF(0, 0, 1432, 386), new RectF(x, y + band, 1432, 386));
            IReadOnlyList<SheetCell> faces = Ctx.Assets.Tables.VoiceFaceCells;
            IReadOnlyList<SheetCell> names = Ctx.Assets.Tables.VoiceNameCells(Ctx.Language);
            for (int i = 0; i < _voiceRows.Count; i++)
            {
                float n = i * 50.607f;
                (x, y) = U(128.667f, 360.06f + n);
                p.Sprite(UiSheets.ConfVoicePage, new RectF(0, 386, 1432, 122), new RectF(x, y + band, 1432, 122));
                (_, int face, int name) = _voiceRows[i];
                SheetCell f = faces[face];
                (x, y) = U(144f, 365.04f + n);
                p.Sprite(UiSheets.ConfFaces, RectF.FromCorners(f.X0, f.Y0, f.X1, f.Y1), new RectF(x, y + band, f.Width, f.Height));
                SheetCell nm = names[name];
                (x, y) = U(144f, 394.9f + n);
                p.Sprite(UiSheets.ConfNames, RectF.FromCorners(nm.X0, nm.Y0, nm.X1, nm.Y1), new RectF(x, y + band, nm.Width, nm.Height));
            }

            (x, y) = U(128.667f, 360.06f + (_voiceRows.Count * 50.607f));
            p.Sprite(UiSheets.ConfVoicePage, new RectF(0, 508, 1432, 19), new RectF(x, y + band, 1432, 19));
        }
        else
        {
            DrawControlsPage(p);
        }
    }

    // The controls page: the PS4 picture (the circle-decide or the cross-decide variant, as the guide icons are), the heading of the prompts setting in its blank
    // header band, and, when the prompts are not the PlayStation icons, the icon column of the controller rows replaced by key caps or Xbox buttons. The picture is
    // drawn in pieces that leave a box around each icon out; the box is filled with thin slices of the page's own band taken just above it (same x, so the
    // band's horizontal gradient matches; the page is a translucent band, so the icons cannot be painted over). The game's labels, row lines and the mouse
    // rows below stay.
    private void DrawControlsPage(UiPainter p)
    {
        UiSheet sheet = Ctx.Services.DecideIsCross ? UiSheets.ConfControlsPadCross : UiSheets.ConfControlsPad;
        (float x, float y) = U(133.333f, 73.007f);
        (int w, int h) = p.SizeOf(sheet);
        PromptStyle style = p.PromptStyle;
        if (style == PromptStyle.PlayStation || w < 1200)
        {
            p.Sprite(sheet, new RectF(0, 0, w, h), new RectF(x, y, w, h));
        }
        else
        {
            const float colX = UiPrompts.ControlColumnX - 16f;      // the box that takes the icons: page x 960 .. 1100
            const float colW = 140f;
            const float half = 24f;                                 // a box is 48 rows tall around the icon centre
            const float slice = 6f;
            float cursor = 0f;
            void Strip(float y0, float y1)
            {
                if (y1 > y0)
                {
                    p.Sprite(sheet, new RectF(0, y0, w, y1 - y0), new RectF(x, y + y0, w, y1 - y0));
                }
            }

            int rowIndex = 0;
            foreach (float centre in UiPrompts.ControlRowCentres)
            {
                float top = MathF.Floor(centre) - half;
                // the slice: 6 rows of flat band above the icon (page rows c-27 .. c-21), for the first row below it (the header band's edge is above)
                float sliceY = rowIndex++ == 0 ? top + half + 21f : top + half - 27f;
                Strip(cursor, top);
                p.Sprite(sheet, new RectF(0, top, colX, 2f * half), new RectF(x, y + top, colX, 2f * half));
                p.Sprite(sheet, new RectF(colX + colW, top, w - colX - colW, 2f * half), new RectF(x + colX + colW, y + top, w - colX - colW, 2f * half));
                for (int k = 0; k < (int)(2f * half / slice); k++)
                {
                    p.Sprite(sheet, new RectF(colX, sliceY, colW, slice), new RectF(x + colX, y + top + (k * slice), colW, slice));
                }

                cursor = top + (2f * half);
            }

            Strip(cursor, h);
            for (int row = 0; row < UiPrompts.ControlRowCentres.Length; row++)
            {
                PromptItem[] items = UiPrompts.ControlRow(row, style);
                UiPrompts.Draw(p, items, x + UiPrompts.ControlColumnX, y + UiPrompts.ControlRowCentres[row] + (UiPrompts.CapHeight / 2f), 1f);
            }
        }

        UiPrompts.DrawText(p, UiPrompts.SettingHeading(Ctx.Language), x + 28f, y + 26f, 1f);
    }

    private void DrawNumbers(UiPainter p)
    {
        if (_tab == 0)
        {
            float[] ys = [131.5f, 205.75f, 271.29f, 330.61f];
            for (int i = 0; i < 4; i++)
            {
                (float x, float y) = U(450f, ys[i]);
                UiWidgets.Number(p, UiSheets.ConfDigits, (int)MathF.Round(Slider(i).Value), x, y);
            }
        }
        else if (_tab == 1)
        {
            float[] ys = [107.44f, 134.81f, 163.02f];
            for (int i = 0; i < 3; i++)
            {
                (float x, float y) = U(538.67f, ys[i]);
                UiWidgets.Number(p, UiSheets.ConfDigits, (int)MathF.Round(Slider(i).Value), x, y);
            }

            float band = SoundBand;
            for (int n = 0; n < _voiceRows.Count; n++)
            {
                (float x, float y) = U(538.33f, 372.5f + (50.607f * n));
                UiWidgets.Number(p, UiSheets.ConfDigits, (int)MathF.Round(Slider(3 + _voiceRows[n].Row).Value), x, y + band);
            }
        }
    }

    /// <summary>The left edge of the two text speed samples in canvas pixels (252.333 units).</summary>
    internal const float SampleLeft = 252.333f * 3f;

    /// <summary>The right edge of the dark box behind the samples in the System page less a 4 px margin (measured on the sheet: the box ends at x 1267 and the value box starts at 1298).</summary>
    internal const float SampleBoxRight = 1264f;

    /// <summary>
    /// The layout of a sample message (PS4 0x40C30 = knife sub_2B460: <c>Text_Layout(0x3A + k, ui_text 138 + k, width 200, height 20, spacing 0, leading 0, white, flags 0;
    /// sx 0.74, sy 0.9176)</c>, drawn at x 252.333 and y 126.93 / 201.19 of 640 x 448, clipped by the page's scroll view only; the function has no language branch).
    /// <paramref name="scale"/> shrinks both glyph scales (see <see cref="SampleScale"/>).
    /// </summary>
    internal static TextStyle SampleStyle(float scale = 1f) =>
        new(640, 448, 0.74 * scale, 0.9176 * scale, 200, 20, 0, 0, TextStyle.White, BlockDraw.Always | BlockDraw.Outline);

    /// <summary>
    /// The factor on both glyph scales of the samples. The PS4's own call (<see cref="SampleStyle"/> at 1) puts the English "Previously read text." 567 px wide, 57 px past
    /// the box and over the "15 ms" value, and the Chinese second sample 38 px past it; the Japanese strings (10 characters, ink 500 px) fit. The overseas Switch scales every
    /// English text slot by 0.9 (knife <c>glyph_scale</c>, sub_A7790), and with that the English sample ends at x 1267.8, on the box's right edge: so English takes 0.9 or less,
    /// and any language whose longer sample still ends past the box margin is scaled to end on it (English 0.893, Chinese 0.922). No PS4 capture was available to confirm either.
    /// </summary>
    internal float SampleScale
    {
        get
        {
            if (_sampleLanguage != Ctx.Language)
            {
                float ink = 0f;
                for (int k = 0; k < 2; k++)
                {
                    string text = Ctx.Message(138 + k);
                    if (!string.IsNullOrEmpty(text))
                    {
                        ink = MathF.Max(ink, Ctx.Text.InkSpan(text, SampleStyle()).Right);
                    }
                }

                float fit = ink > 0f ? (SampleBoxRight - SampleLeft) / ink : 1f;
                _sampleScale = MathF.Min(English ? 0.9f : 1f, fit);
                _sampleLanguage = Ctx.Language;
            }

            return _sampleScale;
        }
    }

    // sub_2B460: the two sample messages (ui_text 138 / 139) at 0.74, typed out at the unread / read speed
    private void DrawPreview(UiPainter p)
    {
        float scale = SampleScale;
        TextStyle style = SampleStyle(scale);
        for (int k = 0; k < 2; k++)
        {
            string text = Ctx.Message(138 + k);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            float[] s = _preview[k];
            float rowY = k == 0 ? 126.93f : 201.19f;
            (float x, float y) = U(252.333f, rowY);

            // smaller glyphs hang from the same top edge; lowering them by half of what they lost keeps them centred in the box as the full-size ones are
            Ctx.Text.Draw(p, text, style, x / UiMetrics.UnitX, (y + ((1f - scale) * 26f)) / UiMetrics.UnitY, 1f, s[0], s[1]);
        }
    }

    private void DrawControl(UiPainter p, Ctl c, float x, float y, int index)
    {
        bool hover = index == _cursor && !Side.Active;
        if (c.Kind == CtlKind.Prompt)
        {
            UiPrompts.DrawPill(p, UiPrompts.SettingLabel(Ctx.Language, c.Index), HitRect(c, x, y), IsOn(c), hover, 1f, translated: c.Index < 2 && Ctx.Language != Language.En);
            return;
        }

        if (c.Kind == CtlKind.Display)
        {
            UiPrompts.DrawPill(p, UiPrompts.DisplayLabel(Ctx.Language, c.Index), HitRect(c, x, y), IsOn(c), hover, 1f, translated: Ctx.Language != Language.En);
            return;
        }

        if (c.Kind == CtlKind.WindowSize)
        {
            if (Display is { } display && c.Index < display.SizeChoices.Count)
            {
                (int w, int h) = display.SizeChoices[c.Index];
                UiPrompts.DrawPill(p, UiPrompts.SizeLabel(w, h), HitRect(c, x, y), IsOn(c), hover, display.Fullscreen && !hover ? DimmedSizeRow : 1f);
            }

            return;
        }

        if (IsSwitch(c.Kind))
        {
            UiPrompts.DrawPill(p, UiPrompts.SwitchLabel(Ctx.Language, c.Index), HitRect(c, x, y), IsOn(c), hover, 1f, translated: Ctx.Language != Language.En);
            return;
        }

        if (c.Kind == CtlKind.Opacity)
        {
            DrawOpacity(p, x, y, hover);
            return;
        }

        int col = IsOn(c) ? 2 : hover ? 1 : 0;
        DrawControlSprite(p, c, x, y, col, hover, 1f, UiBlend.Alpha);
        // sub_2B010 flags every config object 0x880: the focused one gets an additive copy pulsing 0..1 at 0.02 per frame
        bool focused = hover || (_cursor < 0 && !Side.Active && c.Kind == CtlKind.Tab && c.Index == _tab);
        if (focused)
        {
            float pulse = UiEase.Triangle((Ctx.Frame / 60f) * 1.2f);
            DrawControlSprite(p, c, x, y, col, hover, pulse, UiBlend.PsAdd);
        }
    }

    // The port's slider (package K21), in the palette of the pills: a thin track with the five step dots of the PS4's sliders, the part up to the knob filled, a knob of
    // the pill's height, and the value to the right of the track.
    private void DrawOpacity(UiPainter p, float x, float y, bool hover)
    {
        float cy = y + (UiPrompts.PillHeight / 2f);
        int pct = WindowOpacityPercent;
        float kx = x + (OpacityTrack * pct / 100f);
        float edge = hover ? 1f : 0.72f;
        p.FillRounded(new RectF(x, cy - 3f, OpacityTrack, 6f), 3f, 0.62f * edge, 0.86f * edge, 1f * edge, 0.45f, smooth: false);
        if (kx > x)
        {
            p.FillRounded(new RectF(x, cy - 3f, kx - x, 6f), 3f, 0.12f, 0.42f, 0.86f, 0.96f, smooth: false);
        }

        for (int k = 0; k <= 4; k++)
        {
            float dx = x + (OpacityTrack * k / 4f);
            p.FillRounded(new RectF(dx - 4f, cy - 4f, 8f, 8f), 4f, 0.62f * edge, 0.86f * edge, 1f * edge, 0.9f, smooth: false);
        }

        var knob = new RectF(kx - (OpacityKnobWidth / 2f), y, OpacityKnobWidth, UiPrompts.PillHeight);
        p.FillRounded(knob, 9f, 0.62f * edge, 0.86f * edge, 1f * edge, hover ? 0.98f : 0.8f);
        p.FillRounded(new RectF(knob.X + 2f, knob.Y + 2f, knob.W - 4f, knob.H - 4f), 7f, 0.12f, 0.42f, 0.86f, 0.96f, smooth: false);
        UiPrompts.DrawText(p, UiPrompts.PercentLabel(pct), x + OpacityTrack + 28f, cy + 2f, 1f, translated: false);
    }

    private void DrawControlSprite(UiPainter p, Ctl c, float x, float y, int col, bool hover, float alpha, UiBlend blend)
    {
        switch (c.Kind)
        {
            case CtlKind.Tab:
                p.Sprite(UiSheets.ConfTabs, new RectF(8f + (col * 200f), 8f + (c.Index * 88f), 192, 80), new RectF(x, y, 192, 80), alpha, blend);
                break;
            case CtlKind.Default:
                p.Sprite(UiSheets.ConfDefault, new RectF(32, 24f + ((hover ? 1 : 0) * 96f), 288, 72), new RectF(x, y, 288, 72), alpha, blend);
                break;
            case CtlKind.Minus or CtlKind.Plus:
            {
                float sx = c.Kind == CtlKind.Plus ? 88f : 0f;
                float sy = hover ? 49f : 0f;
                p.Sprite(UiSheets.ConfStep, new RectF(sx, sy, 80, 40), new RectF(x, y, 80, 40), alpha, blend);
                break;
            }

            case CtlKind.Slider:
            {
                (float v, float max, _) = Slider(c.Index);
                float kx = x + (max > 0f ? v / max * 508f : 0f);
                RectF src = hover ? new RectF(0, 0, 936, 248) : new RectF(0, 256, 936, 248);
                p.Sprite(UiSheets.ConfKnob, src, new RectF(kx, y, 936, 248), alpha, blend);
                break;
            }

            case CtlKind.Toggle:
            {
                SheetCell cell = ToggleCell(c.Index, col);
                p.Sprite(UiSheets.ConfToggle, RectF.FromCorners(cell.X0, cell.Y0, cell.X1, cell.Y1), new RectF(x, y, cell.Width, cell.Height), alpha, blend);
                break;
            }

            case CtlKind.Radio:
            {
                SheetCell cell = RadioCell(c.Index, c.First, col);
                p.Sprite(UiSheets.ConfRadio, RectF.FromCorners(cell.X0, cell.Y0, cell.X1, cell.Y1), new RectF(x, y, cell.Width, cell.Height), alpha, blend);
                break;
            }

            case CtlKind.Lang:
                p.Sprite(UiSheets.ConfLanguage, new RectF(col * 256f, 88f * c.Index, 248, 80), new RectF(x, y, 248, 80), alpha, blend);
                break;
            case CtlKind.Mute:
                p.Sprite(UiSheets.ConfMute, new RectF(32f + (col * 152f), 32, 136, 64), new RectF(x, y, 136, 64), alpha, blend);
                break;
            case CtlKind.Test:
                p.Sprite(UiSheets.ConfTest, new RectF(32f + ((hover ? 1 : 0) * 120f), 32, 104, 56), new RectF(x, y, 104, 56), alpha, blend);
                break;
        }
    }
}

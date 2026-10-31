// Button prompts for the PC port (package K9): the menus' guide line and the controls page show the game's own PlayStation icons, or key caps for the keyboard, or
// Xbox glyphs, depending on the device the player used last (or the config screen's setting). The caps and glyphs are drawn here from fills and the game's glyph
// pages; no third-party art is used.
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Ui;

/// <summary>Which button prompts a screen draws.</summary>
public enum PromptStyle
{
    /// <summary>Key caps (Enter, Esc, arrows, the mouse wheel).</summary>
    Keyboard = 0,

    /// <summary>The game's own PlayStation icons (the PS4 pictures).</summary>
    PlayStation = 1,

    /// <summary>Drawn Xbox glyphs: coloured A, B, X, Y circles, bumpers, triggers, sticks.</summary>
    Xbox = 2,
}

/// <summary>The config screen's choice of button prompts.</summary>
public enum PromptSetting
{
    /// <summary>Follow the device used last: the keyboard and the mouse show key caps, a PlayStation pad the PlayStation icons, any other pad the Xbox glyphs.</summary>
    Auto = 0,

    /// <summary>Always key caps.</summary>
    Keyboard = 1,

    /// <summary>Always the PlayStation icons.</summary>
    PlayStation = 2,

    /// <summary>Always the Xbox glyphs.</summary>
    Xbox = 3,
}

/// <summary>Helpers for <see cref="PromptSetting"/> and <see cref="PromptStyle"/>.</summary>
public static class PromptStyles
{
    /// <summary>The style to draw: the setting itself, or for <see cref="PromptSetting.Auto"/> the style of the device used last.</summary>
    public static PromptStyle Resolve(PromptSetting setting, PromptStyle lastDevice) => setting switch
    {
        PromptSetting.Keyboard => PromptStyle.Keyboard,
        PromptSetting.PlayStation => PromptStyle.PlayStation,
        PromptSetting.Xbox => PromptStyle.Xbox,
        _ => lastDevice,
    };

    /// <summary>The file and command-line name of a setting (lower case).</summary>
    public static string Name(PromptSetting setting) => setting switch
    {
        PromptSetting.Keyboard => "keyboard",
        PromptSetting.PlayStation => "playstation",
        PromptSetting.Xbox => "xbox",
        _ => "auto",
    };

    /// <summary>Reads a setting name (case-insensitive; <c>ps</c>, <c>ps4</c>, <c>ps5</c> and <c>keys</c> are accepted); false when it is none.</summary>
    public static bool TryParse(string? text, out PromptSetting setting)
    {
        setting = PromptSetting.Auto;
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "auto":
                return true;
            case "keyboard" or "keys" or "key":
                setting = PromptSetting.Keyboard;
                return true;
            case "playstation" or "ps" or "ps4" or "ps5":
                setting = PromptSetting.PlayStation;
                return true;
            case "xbox":
                setting = PromptSetting.Xbox;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>What a button prompt asks the player to do, in the terms of the keyboard bindings (<see cref="PromptBindings"/>).</summary>
public enum PromptAction
{
    /// <summary>Decide, confirm, advance the text.</summary>
    Confirm,

    /// <summary>Cancel, go back.</summary>
    Back,

    /// <summary>Move the cursor up.</summary>
    Up,

    /// <summary>Move the cursor down.</summary>
    Down,

    /// <summary>Move the cursor left.</summary>
    Left,

    /// <summary>Move the cursor right.</summary>
    Right,

    /// <summary>L1: the previous page or tab, auto mode.</summary>
    PreviousPage,

    /// <summary>R1: the next page or tab, skip mode.</summary>
    NextPage,

    /// <summary>Scroll a list (the wheel and the page keys).</summary>
    Scroll,

    /// <summary>Delete the slot under the cursor in the save list.</summary>
    DeleteData,

    /// <summary>Open the system menu.</summary>
    SystemMenu,

    /// <summary>Open the backlog.</summary>
    Backlog,

    /// <summary>Toggle latched skip.</summary>
    SkipToggle,

    /// <summary>Toggle auto mode.</summary>
    AutoToggle,

    /// <summary>Fast-forward while held.</summary>
    FastForward,

    /// <summary>Rewind while held.</summary>
    Rewind,

    /// <summary>Quick save.</summary>
    QuickSave,
}

/// <summary>The kinds of drawn prompt pieces.</summary>
internal enum PromptItemKind
{
    /// <summary>A key cap with a text.</summary>
    Key,

    /// <summary>One key cap with arrows: <see cref="PromptItem.Mask"/> says which (1 up, 2 down, 4 left, 8 right).</summary>
    Arrows,

    /// <summary>A mouse with the wheel lit.</summary>
    Mouse,

    /// <summary>An Xbox face button: the letter in <see cref="PromptItem.Text"/>, the colour index in <see cref="PromptItem.Mask"/> (0 A, 1 B, 2 X, 3 Y).</summary>
    Face,

    /// <summary>A pill with a label (bumpers, triggers, sticks, menu).</summary>
    Pill,

    /// <summary>The Xbox d-pad with the arms of <see cref="PromptItem.Mask"/> lit.</summary>
    Dpad,
}

/// <summary>One piece of a prompt: a key cap, a mouse, an Xbox glyph.</summary>
internal readonly record struct PromptItem(PromptItemKind Kind, string Text = "", int Mask = 0)
{
    public const int Up = 1;
    public const int Down = 2;
    public const int Left = 4;
    public const int Right = 8;

    public static PromptItem Key(string text) => new(PromptItemKind.Key, text);

    public static PromptItem Pill(string text) => new(PromptItemKind.Pill, text);
}

/// <summary>
/// Which key, mouse gesture or Xbox button each guide icon stands for. The icons of the PS4 guide are cells of DISP_PARTS child 16 (<c>help_key</c>); they sit by
/// position (the east icon belongs to the decide word, the south icon to the back word). The key table is checked against the input mapper by a test, so the
/// prompts cannot drift from the bindings.
/// </summary>
public static class PromptBindings
{
    // the 23 icon cells of the guide sheet: 0..6 d-pad (plain, left-right, up-down, up, down, right, left), 7 east, 8 west, 9 north, 10 south, 11 left stick,
    // 12 right stick, 13 / 16 L1, 14 / 17 R1, 15 L1 over R1, 18 / 22 OPTIONS, 19 L2, 20 R2, 21 R3 (read from the sheet by scratch/K9 "guide-cells")
    internal const int CellEast = 7;
    internal const int CellWest = 8;
    internal const int CellNorth = 9;
    internal const int CellSouth = 10;
    internal const int CellLeftStick = 11;
    internal const int CellRightStick = 12;

    // the guide words that choose between two meanings of one icon
    internal const int WordDeleteData = 18;
    internal const int WordNavigate = 25;
    internal const int WordScroll = 26;
    internal const int WordZoom = 24;

    /// <summary>
    /// The keys of each action (their names as the key caps show them), in the order the caps are drawn; the mouse wheel is not a key. W, S, A and D (package K14) are the
    /// arrow keys' aliases <b>in the menus only</b>: in the story A toggles auto mode and W / S / D do nothing.
    /// </summary>
    public static IReadOnlyList<(PromptAction Action, string[] Keys)> Keys { get; } =
    [
        (PromptAction.Confirm, ["Enter", "Space", "Z"]),
        (PromptAction.Back, ["Esc", "X"]),
        (PromptAction.Up, ["Up", "W"]),
        (PromptAction.Down, ["Down", "S"]),
        (PromptAction.Left, ["Left", "A"]),
        (PromptAction.Right, ["Right", "D"]),
        (PromptAction.PreviousPage, ["Q"]),
        (PromptAction.NextPage, ["E"]),
        (PromptAction.Scroll, ["PgUp", "PgDn"]),
        (PromptAction.DeleteData, ["Del"]),
        (PromptAction.SystemMenu, ["Esc", "M"]),
        (PromptAction.Backlog, ["PgUp", "I"]),
        (PromptAction.SkipToggle, ["Tab"]),
        (PromptAction.AutoToggle, ["A"]),
        (PromptAction.FastForward, ["Ctrl"]),
        (PromptAction.Rewind, ["Backspace"]),
        (PromptAction.QuickSave, ["F5"]),
    ];

    /// <summary>The key names of <paramref name="action"/>.</summary>
    public static IReadOnlyList<string> KeysOf(PromptAction action)
    {
        foreach ((PromptAction a, string[] keys) in Keys)
        {
            if (a == action)
            {
                return keys;
            }
        }

        return [];
    }

    /// <summary>
    /// The keyboard prompt of a guide icon: the key caps that do what the guide word beside it says. An icon without a keyboard binding gives no items (the guide
    /// then shows its word alone).
    /// </summary>
    /// <param name="cell">The icon cell of the guide sheet (before the decide / back swap).</param>
    /// <param name="word">The guide word cell that goes with the icon.</param>
    /// <param name="menu">The guide belongs to a menu: its cursor icons name the letter keys W S A D beside the arrow cap (package K14). The story's yes / no prompt is not a menu: there the letters do nothing.</param>
    internal static PromptItem[] Keyboard(int cell, int word, bool menu = true) => cell switch
    {
        0 => Cursor(PromptItem.Up | PromptItem.Down | PromptItem.Left | PromptItem.Right, "WASD", menu),
        1 => Cursor(PromptItem.Left | PromptItem.Right, "A D", menu),
        2 => Cursor(PromptItem.Up | PromptItem.Down, "W S", menu),
        3 => Cursor(PromptItem.Up, "W", menu),
        4 => Cursor(PromptItem.Down, "S", menu),
        5 => Cursor(PromptItem.Right, "D", menu),
        6 => Cursor(PromptItem.Left, "A", menu),
        CellEast => [PromptItem.Key(KeysOf(PromptAction.Confirm)[0])],
        CellSouth => [PromptItem.Key(KeysOf(PromptAction.Back)[0])],
        CellNorth when word == WordDeleteData => [PromptItem.Key(KeysOf(PromptAction.DeleteData)[0])],
        CellLeftStick when word == WordNavigate => [PromptItem.Key("Drag")],
        CellLeftStick when word == WordScroll => [new(PromptItemKind.Mouse), PromptItem.Key(KeysOf(PromptAction.Scroll)[0]), PromptItem.Key(KeysOf(PromptAction.Scroll)[1])],
        CellLeftStick or CellRightStick => [new(PromptItemKind.Mouse)],
        13 or 16 => [PromptItem.Key(KeysOf(PromptAction.PreviousPage)[0])],
        14 or 17 => [PromptItem.Key(KeysOf(PromptAction.NextPage)[0])],
        15 => [PromptItem.Key(KeysOf(PromptAction.PreviousPage)[0]), PromptItem.Key(KeysOf(PromptAction.NextPage)[0])],
        18 or 22 => [PromptItem.Key(KeysOf(PromptAction.QuickSave)[0])],
        19 => [PromptItem.Key(KeysOf(PromptAction.Rewind)[0])],
        20 => [PromptItem.Key(KeysOf(PromptAction.FastForward)[0])],
        _ => [],
    };

    private static PromptItem[] Cursor(int arrows, string letters, bool menu) =>
        menu ? [new(PromptItemKind.Arrows, "", arrows), PromptItem.Key(letters)] : [new(PromptItemKind.Arrows, "", arrows)];

    /// <summary>The Xbox prompt of a guide icon, by position: south is A (decide on every PC pad), east B, west X, north Y, shoulders LB / RB, triggers LT / RT.</summary>
    internal static PromptItem[] Xbox(int cell) => cell switch
    {
        0 => [new(PromptItemKind.Dpad, "", PromptItem.Up | PromptItem.Down | PromptItem.Left | PromptItem.Right)],
        1 => [new(PromptItemKind.Dpad, "", PromptItem.Left | PromptItem.Right)],
        2 => [new(PromptItemKind.Dpad, "", PromptItem.Up | PromptItem.Down)],
        3 => [new(PromptItemKind.Dpad, "", PromptItem.Up)],
        4 => [new(PromptItemKind.Dpad, "", PromptItem.Down)],
        5 => [new(PromptItemKind.Dpad, "", PromptItem.Right)],
        6 => [new(PromptItemKind.Dpad, "", PromptItem.Left)],
        CellEast => [new(PromptItemKind.Face, "A", 0)],
        CellSouth => [new(PromptItemKind.Face, "B", 1)],
        CellWest => [new(PromptItemKind.Face, "X", 2)],
        CellNorth => [new(PromptItemKind.Face, "Y", 3)],
        CellLeftStick => [PromptItem.Pill("LS")],
        CellRightStick or 21 => [PromptItem.Pill("RS")],
        13 or 16 => [PromptItem.Pill("LB")],
        14 or 17 => [PromptItem.Pill("RB")],
        15 => [PromptItem.Pill("LB"), PromptItem.Pill("RB")],
        18 or 22 => [PromptItem.Pill("Menu")],
        19 => [PromptItem.Pill("LT")],
        20 => [PromptItem.Pill("RT")],
        _ => [],
    };
}

/// <summary>Draws prompt items: key caps, the mouse, Xbox glyphs. Everything sits on a common baseline and is <see cref="CapHeight"/> pixels tall.</summary>
internal static class UiPrompts
{
    /// <summary>Height of a cap in canvas pixels (the PlayStation round icons are about 30 px).</summary>
    public const float CapHeight = 30f;

    /// <summary>Space between two caps of one prompt.</summary>
    public const float Gap = 4f;

    private const float Border = 2f;
    private const float Radius = 6f;
    private const float PadX = 9f;
    private const float Fudge = 0.5f;     // the glyph box sits a little high in the line: calibrated against the screenshots

    // the key text: the game's menu font at a size whose capitals are about 17 px tall (the height of the guide words); no outline (it would clog the 2 px border)
    private static readonly TextStyle KeyStyle = TextStyle.Menu(0.32) with { Width = 600, Height = 80, Leading = 0, DrawFlags = BlockDraw.Always };

    private static readonly (int R, int G, int B)[] FaceColours =
    [
        (126, 206, 58),     // A green
        (238, 72, 64),      // B red
        (72, 148, 244),     // X blue
        (252, 196, 40),     // Y yellow
    ];

    /// <summary>Draws the items from <paramref name="x"/> with their bottom edge at <paramref name="bottom"/>; returns the width drawn.</summary>
    public static float Draw(UiPainter p, IReadOnlyList<PromptItem> items, float x, float bottom, float alpha = 1f)
    {
        float start = x;
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                x += Gap;
            }

            x += DrawItem(p, items[i], x, bottom, alpha);
        }

        return x - start;
    }

    /// <summary>The width of one item.</summary>
    public static float ItemWidth(UiPainter p, in PromptItem item) => item.Kind switch
    {
        PromptItemKind.Key or PromptItemKind.Pill => CapWidth(p, item.Text),
        PromptItemKind.Arrows => (BitCount(item.Mask) * 22f) + 12f,
        PromptItemKind.Mouse => 24f,
        PromptItemKind.Face => CapHeight,
        PromptItemKind.Dpad => CapHeight,
        _ => 0f,
    };

    /// <summary>Draws one item and returns its width.</summary>
    public static float DrawItem(UiPainter p, in PromptItem item, float x, float bottom, float alpha = 1f)
    {
        float w = ItemWidth(p, item);
        switch (item.Kind)
        {
            case PromptItemKind.Key:
            case PromptItemKind.Pill:
                DrawCap(p, item.Text, x, bottom, w, alpha);
                break;
            case PromptItemKind.Arrows:
                DrawArrows(p, item.Mask, x, bottom, w, alpha);
                break;
            case PromptItemKind.Mouse:
                DrawMouse(p, x, bottom, alpha);
                break;
            case PromptItemKind.Face:
                DrawFace(p, item, x, bottom, alpha);
                break;
            case PromptItemKind.Dpad:
                DrawDpad(p, item.Mask, x, bottom, alpha);
                break;
        }

        return w;
    }

    // ---- the controls page and its setting row ----------------------------------------------------------------------------------------------------

    /// <summary>The 12 controller rows of the controls page (CONF_PARTS 16 / 17): the centres of the icon column in page pixels, measured from the sheets (en, ja, zc and zt agree).</summary>
    public static readonly float[] ControlRowCentres = [85.5f, 151.5f, 214.5f, 280.5f, 344.5f, 410.5f, 476f, 540f, 605.5f, 670.5f, 736.5f, 803.5f];

    /// <summary>Where the icon column of the controls page starts, in page pixels.</summary>
    public const float ControlColumnX = 976f;

    /// <summary>The first page pixel row of the controller rows (a header band above it is blank).</summary>
    public const float ControlRowsTop = 46f;

    /// <summary>The page row where the controller rows end (the mouse rows start below the second header band).</summary>
    public const float ControlRowsBottom = 846f;

    /// <summary>The prompt of controls row <paramref name="row"/> (0..11, top to bottom) in a style other than PlayStation: the keys, or the Xbox buttons.</summary>
    public static PromptItem[] ControlRow(int row, PromptStyle style)
    {
        if (style == PromptStyle.Xbox)
        {
            // the pad's positions: south decides, east cancels; the picture's rows: confirm, cancel, system menu, log, control panel (up), advance (down), skip (R1),
            // auto (L1), fast-forward (R2), rewind (L2), scroll (left stick), quicksave (OPTIONS)
            int[] cells = [PromptBindings.CellEast, PromptBindings.CellSouth, PromptBindings.CellWest, PromptBindings.CellNorth, 3, 4, 17, 16, 20, 19, PromptBindings.CellLeftStick, 22];
            return PromptBindings.Xbox(cells[Math.Clamp(row, 0, 11)]);
        }

        static PromptItem[] Keys(params PromptAction[] actions)
        {
            var items = new List<PromptItem>();
            foreach (PromptAction a in actions)
            {
                switch (a)
                {
                    case PromptAction.Up:
                        items.Add(new PromptItem(PromptItemKind.Arrows, "", PromptItem.Up));
                        break;
                    case PromptAction.Down:
                        items.Add(new PromptItem(PromptItemKind.Arrows, "", PromptItem.Down));
                        break;
                    case PromptAction.Scroll:
                        items.Add(new PromptItem(PromptItemKind.Mouse));
                        items.AddRange(PromptBindings.KeysOf(a).Select(PromptItem.Key));
                        break;
                    default:
                        items.Add(PromptItem.Key(PromptBindings.KeysOf(a)[0]));
                        break;
                }
            }

            return [.. items];
        }

        return row switch
        {
            0 => [.. PromptBindings.KeysOf(PromptAction.Confirm).Select(PromptItem.Key)],
            1 => [PromptItem.Key("X")],
            2 => [.. PromptBindings.KeysOf(PromptAction.SystemMenu).Select(PromptItem.Key)],
            3 => [.. PromptBindings.KeysOf(PromptAction.Backlog).Select(PromptItem.Key)],
            4 => Keys(PromptAction.Up),
            5 => Keys(PromptAction.Down),
            6 => Keys(PromptAction.SkipToggle, PromptAction.NextPage),
            7 => Keys(PromptAction.AutoToggle, PromptAction.PreviousPage),
            8 => Keys(PromptAction.FastForward),
            9 => Keys(PromptAction.Rewind),
            10 => Keys(PromptAction.Scroll),
            _ => Keys(PromptAction.QuickSave),
        };
    }

    /// <summary>The widths of the four setting pills (Auto, Keyboard, PlayStation, Xbox) of the controls tab, in pixels.</summary>
    public static float PillWidth(int index) => index switch { 0 => 100f, 1 => 156f, 2 => 178f, _ => 100f };

    /// <summary>The height of a setting pill.</summary>
    public const float PillHeight = 34f;

    /// <summary>The words of the setting row: the four options (the names of devices stay in English in every language).</summary>
    public static string SettingLabel(Language language, int index) => index switch
    {
        0 => language switch { Language.Ja => "自動", Language.Zc => "自动", Language.Zt => "自動", _ => "Auto" },
        1 => language switch { Language.Ja => "キーボード", Language.Zc => "键盘", Language.Zt => "鍵盤", _ => "Keyboard" },
        2 => "PlayStation",
        _ => "Xbox",
    };

    /// <summary>The heading of the setting row.</summary>
    public static string SettingHeading(Language language) => language switch
    {
        Language.Ja => "ボタン表示",
        Language.Zc => "按键提示",
        Language.Zt => "按鍵提示",
        _ => "Button Prompts",
    };

    // ---- the display rows of the System tab (package K19) ----------------------------------------------------------------------------------------------

    /// <summary>The heading of the Display Mode row.</summary>
    public static string DisplayHeading(Language language) => language switch
    {
        Language.Ja => "画面モード",
        Language.Zc => "显示模式",
        Language.Zt => "顯示模式",
        _ => "Display Mode",
    };

    /// <summary>The two options of the Display Mode row: 0 windowed, 1 fullscreen (translated in every language).</summary>
    public static string DisplayLabel(Language language, int index) => index == 0
        ? language switch { Language.Ja => "ウィンドウ", Language.Zc => "窗口", Language.Zt => "視窗", _ => "Windowed" }
        : language switch { Language.Ja => "フルスクリーン", Language.Zc => "全屏", Language.Zt => "全螢幕", _ => "Fullscreen" };

    /// <summary>The widths of the two Display Mode pills.</summary>
    public static float DisplayPillWidth(int index) => index == 0 ? 156f : 178f;

    /// <summary>The heading of the Window Size row.</summary>
    public static string SizeHeading(Language language) => language switch
    {
        Language.Ja => "ウィンドウサイズ",
        Language.Zc => "窗口大小",
        Language.Zt => "視窗大小",
        _ => "Window Size",
    };

    /// <summary>The label of a Window Size pill: "1920x1080" (digits and the x of the English pages in every language).</summary>
    public static string SizeLabel(int width, int height) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{width}x{height}");

    /// <summary>The width of a Window Size pill.</summary>
    public const float SizePillWidth = 150f;

    // ---- the port's own rows (package K21) --------------------------------------------------------------------------------------------------------------

    /// <summary>The heading of the Screenshot Notice row (System tab, package K24): the short "Screenshot saved" toast after F12. Two lines in Japanese (the one-line word ran up to the Off pill).</summary>
    public static string ScreenshotNoticeHeading(Language language) => language switch
    {
        Language.Ja => "スクリーンショット^通知",
        Language.Zc => "截图提示",
        Language.Zt => "螢幕截圖通知",
        _ => "Screenshot Notice",
    };

    /// <summary>The heading of the Copy Text to Clipboard row (System tab); the English one takes two lines (<c>^</c>), as the sheet's own long labels do, so that it ends before the pills at x 728.</summary>
    public static string CopyTextHeading(Language language) => language switch
    {
        Language.Ja => "テキストをコピー",
        Language.Zc => "复制文本到剪贴板",
        Language.Zt => "複製文字",                     // the Traditional Chinese glyph pages have no 簿 (of 剪貼簿): the shorter "copy text"
        _ => "Copy Text to^Clipboard",
    };

    /// <summary>The heading of the Text Hooker Output row (System tab, package K23); two lines in English.</summary>
    public static string TextHookerHeading(Language language) => language switch
    {
        Language.Ja => "テキストフッカー出力",
        Language.Zc => "文本钩子输出",
        Language.Zt => "文字鉤子輸出",
        _ => "Text Hooker^Output",
    };

    /// <summary>The heading of the Text WebSocket row (System tab, package K23); the Latin word in every language, as the tools call it.</summary>
    public static string WebSocketHeading(Language language) => language switch
    {
        Language.Ja => "テキスト WebSocket",
        Language.Zc => "文本 WebSocket",
        Language.Zt => "文字 WebSocket",
        _ => "Text WebSocket",
    };

    /// <summary>The heading of the Text Window Opacity row (System tab); two lines in English (see <see cref="CopyTextHeading"/>).</summary>
    public static string OpacityHeading(Language language) => language switch
    {
        Language.Ja => "ウィンドウの不透明度",
        Language.Zc => "文本框不透明度",
        Language.Zt => "文字框不透明度",
        _ => "Text Window^Opacity",
    };

    /// <summary>The heading of the Mute in Background row (Sound tab).</summary>
    public static string MuteHeading(Language language) => language switch
    {
        Language.Ja => "バックグラウンドで消音",
        Language.Zc => "后台静音",
        Language.Zt => "背景靜音",
        _ => "Mute in Background",
    };

    /// <summary>The two options of a switch row: Off (0) and On (1), translated in every language.</summary>
    public static string SwitchLabel(Language language, int index) => index == 0
        ? language switch { Language.Ja => "オフ", Language.Zc => "关", Language.Zt => "關", _ => "Off" }
        : language switch { Language.Ja => "オン", Language.Zc => "开", Language.Zt => "開", _ => "On" };

    /// <summary>The width of a switch pill.</summary>
    public const float SwitchPillWidth = 100f;

    /// <summary>The label of the opacity slider's value: "100%" (digits of the English pages in every language).</summary>
    public static string PercentLabel(int percent) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{percent}%");

    /// <summary>The width in canvas pixels of <paramref name="text"/> drawn by <see cref="DrawText"/> with the same arguments.</summary>
    public static float TextWidth(UiPainter p, string text, double scale = 0.4, bool translated = true)
    {
        ArgumentNullException.ThrowIfNull(p);
        UiText drawer = translated ? p.LabelText : p.KeyText;
        TextStyle style = KeyStyle with { Sx = scale, Sy = scale * (448.0 / 360.0), Colour = TextStyle.White };
        return drawer.InkSpan(text, style).Right;
    }

    /// <summary>Draws one option of the setting row as a pill: selected options are filled, the hovered one has a brighter border.</summary>
    public static void DrawPill(UiPainter p, string text, RectF r, bool selected, bool hover, float alpha, bool translated = false)
    {
        float edge = hover ? 1f : 0.72f;
        p.FillRounded(r, 9f, 0.62f * edge, 0.86f * edge, 1f * edge, (hover || selected ? 0.98f : 0.8f) * alpha);
        RectF inner = new(r.X + Border, r.Y + Border, r.W - (2f * Border), r.H - (2f * Border));
        if (selected)
        {
            p.FillRounded(inner, 7f, 0.12f, 0.42f, 0.86f, 0.96f * alpha, smooth: false);
        }
        else
        {
            p.FillRounded(inner, 7f, 0.03f, 0.07f, 0.16f, (hover ? 0.9f : 0.78f) * alpha, smooth: false);
        }

        DrawLabel(p, text, r.X + (r.W / 2f), r.Y + (r.H / 2f), alpha, TextStyle.White, 0.36, translated);
    }

    /// <summary>Draws a label whose left edge is <paramref name="x"/> and whose vertical centre is <paramref name="cy"/>.</summary>
    public static void DrawText(UiPainter p, string text, float x, float cy, float alpha, double scale = 0.4, bool translated = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        UiText drawer = translated ? p.LabelText : p.KeyText;
        TextStyle style = KeyStyle with { Sx = scale, Sy = scale * (448.0 / 360.0), Colour = TextStyle.White };
        float th = drawer.Height(text, style) * UiMetrics.UnitY;
        drawer.Draw(p, text, style, x / UiMetrics.UnitX, (cy - (th / 2f) + Fudge) / UiMetrics.UnitY, alpha);
    }

    /// <summary>
    /// PRESS ANY KEY: the title's call to action in the keyboard style, drawn centred where the PS4's PRESS ANY BUTTON picture (472 x 88 at (724, 814), a bitmap in
    /// every language) sits, in the same pale blue with a wide letter spacing. The picture says BUTTON, which a keyboard player would not press.
    /// </summary>
    public static void DrawPressAnyKey(UiPainter p, float alpha)
    {
        const string text = "PRESS ANY KEY";
        TextStyle style = TextStyle.Menu(0.46) with { Width = 600, Height = 80, Leading = 0, Spacing = 4, Colour = TextStyle.Rgb565(184, 198, 222) };
        float w = p.KeyText.Width(text, style) * UiMetrics.UnitX;
        float h = p.KeyText.Height(text, style) * UiMetrics.UnitY;
        p.KeyText.Draw(p, text, style, (960f - (w / 2f)) / UiMetrics.UnitX, (858f - (h / 2f) + Fudge) / UiMetrics.UnitY, alpha);
    }

    private static int BitCount(int mask) => System.Numerics.BitOperations.PopCount((uint)mask);

    private static float TextWidth(UiPainter p, string text) => p.KeyText.Width(text, KeyStyle) * UiMetrics.UnitX;

    private static float CapWidth(UiPainter p, string text) => MathF.Max(CapHeight, MathF.Ceiling(TextWidth(p, text)) + (2f * PadX));

    // a rounded cap: a light 2 px border, a dark fill, the label centred in it
    private static void DrawCap(UiPainter p, string text, float x, float bottom, float w, float alpha)
    {
        float top = bottom - CapHeight;
        p.FillRounded(new RectF(x, top, w, CapHeight), Radius, 0.93f, 0.95f, 1f, 0.95f * alpha);
        p.FillRounded(new RectF(x + Border, top + Border, w - (2f * Border), CapHeight - (2f * Border)), Radius - Border, 0.03f, 0.07f, 0.16f, 0.92f * alpha, smooth: false);
        DrawLabel(p, text, x + (w / 2f), top + (CapHeight / 2f), alpha, TextStyle.White);
    }

    // the label of a cap centred on (cx, cy)
    private static void DrawLabel(UiPainter p, string text, float cx, float cy, float alpha, int colour, double scale = 0.32, bool translated = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        UiText drawer = translated ? p.LabelText : p.KeyText;
        TextStyle style = KeyStyle with { Colour = colour, Sx = scale, Sy = scale * (448.0 / 360.0) };
        float tw = drawer.Width(text, style) * UiMetrics.UnitX;
        float th = drawer.Height(text, style) * UiMetrics.UnitY;
        drawer.Draw(p, text, style, (cx - (tw / 2f)) / UiMetrics.UnitX, (cy - (th / 2f) + Fudge) / UiMetrics.UnitY, alpha);
    }

    // a cap with one filled triangle per arrow, in the order up, down, left, right
    private static void DrawArrows(UiPainter p, int mask, float x, float bottom, float w, float alpha)
    {
        float top = bottom - CapHeight;
        p.FillRounded(new RectF(x, top, w, CapHeight), Radius, 0.93f, 0.95f, 1f, 0.95f * alpha);
        p.FillRounded(new RectF(x + Border, top + Border, w - (2f * Border), CapHeight - (2f * Border)), Radius - Border, 0.03f, 0.07f, 0.16f, 0.92f * alpha, smooth: false);
        int[] order = [PromptItem.Left, PromptItem.Up, PromptItem.Down, PromptItem.Right];
        int n = BitCount(mask);
        float slot = (w - 12f) / MathF.Max(1, n);
        float cx = x + 6f + (slot / 2f);
        float cy = top + (CapHeight / 2f);
        foreach (int dir in order)
        {
            if ((mask & dir) == 0)
            {
                continue;
            }

            Arrow(p, dir, cx, cy, alpha);
            cx += slot;
        }
    }

    // a pixel-row triangle, 13 px across its base and 7 px high, pointing in the given direction, centred on (cx, cy)
    private static void Arrow(UiPainter p, int dir, float cx, float cy, float alpha)
    {
        const int rows = 7;
        for (int i = 0; i < rows; i++)
        {
            float run = 1f + (2f * i);                  // 1, 3, 5, ... 13 px
            switch (dir)
            {
                case PromptItem.Up:
                    p.Fill(new RectF(cx - (run / 2f), cy - 4f + i, run, 1f), 1f, 1f, 1f, alpha);
                    break;
                case PromptItem.Down:
                    p.Fill(new RectF(cx - (run / 2f), cy + 3f - i, run, 1f), 1f, 1f, 1f, alpha);
                    break;
                case PromptItem.Left:
                    p.Fill(new RectF(cx - 4f + i, cy - (run / 2f), 1f, run), 1f, 1f, 1f, alpha);
                    break;
                default:
                    p.Fill(new RectF(cx + 3f - i, cy - (run / 2f), 1f, run), 1f, 1f, 1f, alpha);
                    break;
            }
        }
    }

    // a mouse seen from above: a rounded body, the split between the buttons, the wheel lit
    private static void DrawMouse(UiPainter p, float x, float bottom, float alpha)
    {
        float w = 24f;
        float top = bottom - CapHeight;
        p.FillRounded(new RectF(x, top, w, CapHeight), 10f, 0.93f, 0.95f, 1f, 0.95f * alpha);
        p.FillRounded(new RectF(x + Border, top + Border, w - (2f * Border), CapHeight - (2f * Border)), 8f, 0.03f, 0.07f, 0.16f, 0.92f * alpha, smooth: false);
        p.Fill(new RectF(x + Border, top + 13f, w - (2f * Border), 1f), 0.93f, 0.95f, 1f, 0.9f * alpha);                 // the split under the buttons
        p.FillRounded(new RectF(x + (w / 2f) - 2f, top + 5f, 4f, 9f), 2f, 0.49f, 0.84f, 1f, alpha);                       // the wheel
    }

    // an Xbox face button: a dark disc with a light ring and the letter in the button's colour
    private static void DrawFace(UiPainter p, in PromptItem item, float x, float bottom, float alpha)
    {
        float top = bottom - CapHeight;
        (int r, int g, int b) = FaceColours[Math.Clamp(item.Mask, 0, 3)];
        p.FillRounded(new RectF(x, top, CapHeight, CapHeight), CapHeight / 2f, 0.93f, 0.95f, 1f, 0.95f * alpha);
        p.FillRounded(new RectF(x + Border, top + Border, CapHeight - (2f * Border), CapHeight - (2f * Border)), (CapHeight / 2f) - Border, 0.03f, 0.07f, 0.16f, 0.92f * alpha, smooth: false);
        DrawLabel(p, item.Text, x + (CapHeight / 2f), top + (CapHeight / 2f), alpha, TextStyle.Rgb565(r, g, b));
    }

    // the Xbox d-pad: a plus of two bars with the lit arms in the accent colour
    private static void DrawDpad(UiPainter p, int mask, float x, float bottom, float alpha)
    {
        float top = bottom - CapHeight;
        float cx = x + (CapHeight / 2f);
        float cy = top + (CapHeight / 2f);
        const float arm = 11f;
        const float half = 5f;
        // the dark outline first, then the body, so the plus reads on any background
        p.FillRounded(new RectF(cx - arm - 1f, cy - half - 1f, (2f * arm) + 2f, (2f * half) + 2f), 3f, 0.03f, 0.07f, 0.16f, 0.9f * alpha);
        p.FillRounded(new RectF(cx - half - 1f, cy - arm - 1f, (2f * half) + 2f, (2f * arm) + 2f), 3f, 0.03f, 0.07f, 0.16f, 0.9f * alpha);
        p.FillRounded(new RectF(cx - arm, cy - half, 2f * arm, 2f * half), 2f, 0.93f, 0.95f, 1f, alpha);
        p.FillRounded(new RectF(cx - half, cy - arm, 2f * half, 2f * arm), 2f, 0.93f, 0.95f, 1f, alpha);
        void Lit(int dir, RectF r)
        {
            if ((mask & dir) != 0 && mask != (PromptItem.Up | PromptItem.Down | PromptItem.Left | PromptItem.Right))
            {
                p.FillRounded(r, 2f, 0.38f, 0.74f, 1f, alpha);
            }
        }

        Lit(PromptItem.Up, new RectF(cx - half + 1f, cy - arm + 1f, (2f * half) - 2f, arm - half));
        Lit(PromptItem.Down, new RectF(cx - half + 1f, cy + half, (2f * half) - 2f, arm - half - 1f));
        Lit(PromptItem.Left, new RectF(cx - arm + 1f, cy - half + 1f, arm - half, (2f * half) - 2f));
        Lit(PromptItem.Right, new RectF(cx + half, cy - half + 1f, arm - half - 1f, (2f * half) - 2f));
    }
}

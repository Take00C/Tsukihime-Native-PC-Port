// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (GUIDE_SEQ, draw_guide, draw_scroll_bar, draw_dialog_win, draw_digits)
namespace Tsuki.Ui;

/// <summary>What a click on a word of the button guide line does (package K12).</summary>
public enum GuideAction
{
    /// <summary>Nothing: the click is taken by the guide line (Move Cursor, Scroll).</summary>
    None,

    /// <summary>The Back word: cancel.</summary>
    Back,

    /// <summary>The Confirm word: decide on the item under the cursor.</summary>
    Confirm,

    /// <summary>The Delete Data word of the save list.</summary>
    DeleteData,

    /// <summary>A word with the shoulder buttons: the left half of it is L1, the right half R1.</summary>
    Shoulders,
}

/// <summary>A word of a guide line and its icons as the pointer can hit them: the word, what it does, where it is.</summary>
/// <param name="Word">The index into <c>IUiTables.GuideWordCells</c>.</param>
/// <param name="Action">What a click on it does.</param>
/// <param name="Rect">The rectangle of the word and its icon in canvas pixels, from the top of the guide bar to the bottom of the screen.</param>
public readonly record struct GuideHit(int Word, GuideAction Action, RectF Rect);

/// <summary>Widgets the screens share: the button guide line, the scroll bar, the dialog window, digit strings.</summary>
public static class UiWidgets
{
    // Button guide: per guide index a list of (kind, cell) pairs, kind 1 = help_txt word, kind 0 = help_key icon, the order the PS4 draws them. The PS4 builds
    // them inline in function 0x6A3C0 (docs/research/knife_tables.md 5.14: no table); the pairs below are read from the 12 cases of that function by
    // scratch/K5a/guide_decode.py (rodata chunks 0x209BE0..0x209FC0 and the immediates). Case 10's tail repeats the first pairs and is cut. The cells are indices
    // into IUiTables.GuideWordCells / GuideIconCells; the pad icons sit by position (cell 7 east, 10 south, 8 west, 9 north).
    private static readonly (byte Kind, byte Cell)[][] GuideSeq =
    [
        [(1, 0), (0, 10), (1, 1), (0, 7), (1, 2), (0, 0), (1, 26), (0, 11)],
        [(1, 0), (0, 10), (1, 1), (0, 7), (1, 12), (0, 0), (1, 13), (0, 15), (1, 26), (0, 11)],
        [(1, 0), (0, 10), (1, 1), (0, 7), (1, 12), (0, 0), (1, 13), (0, 15), (1, 26), (0, 11)],
        [(1, 0), (0, 10), (1, 11), (0, 7), (1, 2), (0, 0)],
        [(1, 0), (0, 10), (1, 1), (0, 7), (1, 2), (0, 0)],
        [(1, 0), (0, 10), (1, 1), (0, 7), (1, 2), (0, 0)],
        [(1, 0), (0, 10), (1, 1), (0, 7), (1, 21), (0, 8), (1, 22), (0, 9), (1, 13), (0, 17), (0, 16), (1, 2), (0, 0), (1, 26), (0, 11)],
        [(1, 0), (0, 10), (1, 1), (0, 7), (1, 13), (0, 14), (0, 13), (1, 2), (0, 0), (1, 26), (0, 11)],
        [(1, 0), (0, 10), (1, 1), (0, 7), (1, 13), (0, 14), (0, 13), (1, 2)],
        [(1, 0), (0, 10), (1, 23), (0, 15), (1, 25), (0, 11), (1, 24), (0, 12)],
        [(1, 0), (0, 10), (1, 1), (0, 7), (1, 2), (0, 0)],
        [(1, 0), (0, 10), (1, 1), (0, 7), (1, 18), (0, 9), (1, 2), (0, 0), (1, 26), (0, 11)],

        // 12 (package K13, not one of the PS4's twelve): the story's yes / no prompt, which has no Back: Confirm and Move Cursor, from the same words and icons
        [(1, 1), (0, 7), (1, 2), (0, 0)],
    ];

    /// <summary>The guide of the story's yes / no prompts: Confirm and Move Cursor (the prompt ignores the cancel button).</summary>
    public const int GuidePrompt = 12;

    /// <summary>The guide used by the save and load list.</summary>
    public const int GuideList = 0;

    /// <summary>The guide of the config pages (back, confirm, move cursor, switch page, scroll).</summary>
    public const int GuideConfig = 2;

    /// <summary>The guide of the backlog.</summary>
    public const int GuideBacklog = 3;

    /// <summary>The guide of the save list: adds the north button, Delete Data (the PS4's case 11: the word beside the TRIANGLE icon is word 18).</summary>
    public const int GuideSaveList = 11;

    /// <summary>
    /// Draws the button guide line (knife <c>draw_guide</c>, sub_4A8F0): a navy bar over the bottom of the screen (y 424.36 .. 449 of 448) and the icons and
    /// words from x 55, bottom-aligned, each advancing by its width plus a gap. <paramref name="dy"/> moves the line down in units of 448; <paramref name="decideIsCross"/>
    /// swaps the confirm and back icons.
    /// </summary>
    public static void Guide(UiPainter p, int guide, float dy = 0f, float alpha = 1f, bool decideIsCross = false)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (guide < 0 || guide >= GuideSeq.Length)
        {
            return;
        }

        using UiPainter.AlphaScope scope = p.WithAlpha(alpha);
        float k = UiMetrics.UnitY;
        p.Fill(new RectF(0, (424.36f + dy) * k, UiMetrics.Width, (449f - 424.36f) * k), 0.0039f, 0.2353f, 0.3961f, 0.5f);
        UiAssets assets = p.Assets;
        IReadOnlyList<Tsuki.Engine.Contracts.SheetCell> icons = assets.Tables.GuideIconCells;
        IReadOnlyList<Tsuki.Engine.Contracts.SheetCell> words = assets.Tables.GuideWordCells(assets.Language);
        PromptStyle style = p.PromptStyle;
        (byte Kind, byte Cell)[] seq = GuideSeq[guide];
        float x = 55f;
        int word = -1;
        float groupX = x;
        int groupWord = -1;
        bool shoulders = false;
        float barTop = (424.36f + dy) * k;

        // the word and the icons after it are one target for the mouse (package K12); a guide that is fading out takes no clicks
        void Flush(float end)
        {
            if (groupWord < 0 || alpha < 0.5f)
            {
                return;
            }

            GuideAction action = shoulders ? GuideAction.Shoulders : groupWord switch { 0 => GuideAction.Back, 1 or 11 => GuideAction.Confirm, 18 => GuideAction.DeleteData, _ => GuideAction.None };
            p.AddGuideHit(new GuideHit(groupWord, action, new RectF(groupX - 6f, barTop, end - groupX + 6f, UiMetrics.Height - barTop)));
        }

        for (int i = 0; i < seq.Length; i++)
        {
            (byte kind, byte cell) = seq[i];
            if (kind == 1)
            {
                Flush(x);
                groupWord = cell;
                groupX = x;
                shoulders = false;
            }
            else if (cell is >= 13 and <= 17)
            {
                shoulders = true;
            }

            Tsuki.Engine.Contracts.SheetCell c;
            UiSheet sheet;
            float bottom;
            float pad;
            if (kind == 0)
            {
                if (cell >= icons.Count)
                {
                    continue;
                }

                if (style != PromptStyle.PlayStation)
                {
                    // key caps or Xbox glyphs: drawn on the icons' baseline; the pair of shoulder icons (R1, L1 in the PS4 order) becomes one prompt in L1, R1 order
                    int next = i + 1 < seq.Length && seq[i + 1].Kind == 0 ? seq[i + 1].Cell : -1;
                    int use = cell;
                    if ((cell == 14 && next == 13) || (cell == 17 && next == 16))
                    {
                        use = 15;
                        i++;
                    }

                    PromptItem[] items = style == PromptStyle.Keyboard ? PromptBindings.Keyboard(use, word, guide != GuidePrompt) : PromptBindings.Xbox(use);
                    if (items.Length > 0)
                    {
                        x += UiPrompts.Draw(p, items, x, ((445f + dy) * k) - 2f, 1f) + 10f;
                    }

                    continue;
                }

                // the pad icons are laid out by position: cell 7 is the east button (CIRCLE), cell 10 the south one (CROSS); with CROSS as decide they swap
                c = icons[decideIsCross && cell is 7 or 10 ? 17 - cell : cell];
                sheet = UiSheets.GuideIcons;
                bottom = 445f;
                pad = 6f;
            }
            else
            {
                if (cell >= words.Count)
                {
                    continue;
                }

                word = cell;
                c = words[cell];
                sheet = UiSheets.GuideWords;
                bottom = 444f;
                pad = 4f;
            }

            var src = RectF.FromCorners(c.X0, c.Y0, c.X1, c.Y1);
            p.Sprite(sheet, src, new RectF(x, ((bottom + dy) * k) - src.H, src.W, src.H));
            x += src.W + pad + 4f;
        }

        Flush(x);
    }

    /// <summary>
    /// The scroll bar of the lists (knife <c>draw_scroll_bar</c>, sub_9B0C4): the track at (1847, 105) and the slider at y 99 + <paramref name="travel"/> *
    /// <paramref name="fraction"/>. With the screen's <paramref name="bar"/> the thumb answers the mouse (package K12): the sheet (DISP_PARTS child 15, 208 x 336) holds
    /// the grey thumb at x 48 and the lit one at x 128, so a hovered thumb takes the lit one at half strength and a grabbed one (or a held track click) at full strength.
    /// </summary>
    public static void ScrollBar(UiPainter p, float fraction, float travel = 608f, float alpha = 1f, UiScrollBar? bar = null)
    {
        ArgumentNullException.ThrowIfNull(p);
        using UiPainter.AlphaScope scope = p.WithAlpha(alpha);
        p.Sprite(UiSheets.ScrollBar, new RectF(0, 0, 16, 888), new RectF(1847, 105, 16, 888));
        float y = UiScrollBar.ThumbTop(fraction, travel);
        var dst = new RectF(1839, y, 32, 288);
        p.Sprite(UiSheets.ScrollSlider, new RectF(48, 24, 32, 288), dst);
        if (bar is null)
        {
            return;
        }

        if (bar.Pressed)
        {
            p.Sprite(UiSheets.ScrollSlider, new RectF(128, 24, 32, 288), dst);
        }
        else if (bar.Hover)
        {
            p.Sprite(UiSheets.ScrollSlider, new RectF(128, 24, 32, 288), dst, 0.5f);
        }
    }

    /// <summary>The dialog window (DISP_PARTS child 20, 824 x 416) centred on the screen and scaled by <paramref name="scale"/> about the centre.</summary>
    public static void DialogWindow(UiPainter p, float alpha, float scale)
    {
        ArgumentNullException.ThrowIfNull(p);
        float w = 824f * scale;
        float h = 416f * scale;
        p.Sprite(UiSheets.DialogWindow, new RectF(0, 0, 824, 416), new RectF((UiMetrics.Width - w) / 2f, (UiMetrics.Height - h) / 2f, w, h), alpha);
    }

    /// <summary>
    /// Draws a number with its last digit's left edge at <paramref name="x"/>, digits 24 px apart going left, from a 24 x 32 digit sheet whose cells sit at
    /// (<paramref name="cell0"/> + <paramref name="pitch"/> d, <paramref name="cellY"/>) (the config numbers: knife <c>conf_number</c>).
    /// </summary>
    public static void Number(UiPainter p, UiSheet digits, int n, float x, float y, float cell0 = 2f, float pitch = 32f, float cellY = 0f, float step = 24f)
    {
        ArgumentNullException.ThrowIfNull(p);
        n = Math.Max(n, 0);
        do
        {
            int d = n % 10;
            p.Sprite(digits, new RectF(cell0 + (pitch * d), cellY, 24, 32), new RectF(x, y, 24, 32));
            n /= 10;
            x -= step;
        }
        while (n > 0);
    }
}

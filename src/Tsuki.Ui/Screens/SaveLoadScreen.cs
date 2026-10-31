// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (Screen::SaveLoad: save_list, save_frame, draw, key_nav, save / load / delete flows, INFO_ROUTE)
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Ui.Screens;

/// <summary>
/// The save and load list (docs/formats/adv_state.md 7; knife <c>Screen::SaveLoad</c>, sub_4F240 / sub_50FC0): four slot frames at a time with thumbnail, date,
/// chapter title and scene title, the route card and notes of the slot under the cursor, a scroll bar, the button guide. Save offers a blank slot first, then the
/// used ones newest first; Load lists every used slot newest first (the auto saves carry the AUTO SAVE tag). Used slots ask before overwriting and before
/// loading; X deletes a save. It is opened from the system menu (and keeps it drawn underneath) or from the title.
/// </summary>
public sealed class SaveLoadScreen : UiScreen, ISubMenu
{
    private const int Visible = 4;

    // route of phases 11..36 for the info panel: 0 Arcueid, 1 Ciel, else neutral (knife INFO_ROUTE; the PS4 table is not found, knife_tables.md 5.10)
    private static readonly byte[] InfoRoute = [2, 1, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 0, 1, 0, 1, 0, 1, 0, 1, 1, 1, 1, 1];

    private readonly UiItemList _items = new();
    private readonly UiScrollBar _bar = new();
    private List<int> _list = [];
    private int _scroll;
    private int _row;
    private float _flash;
    private MessagePopup? _popup;
    private float _saveWait = -1f;
    private float _deleteWait = -1f;
    private int _savedSlot = -1;
    private int _loadSlot = -1;
    private bool _loadClosing;

    /// <summary>Creates the list for saving (true) or loading.</summary>
    public SaveLoadScreen(bool save)
    {
        Save = save;
        Side = new SideFocus(this, save ? 0 : 1);
        StartHidden();
    }

    /// <summary>True for the save list, false for the load list.</summary>
    public bool Save { get; }

    /// <inheritdoc/>
    public int Tab => Save ? 0 : 1;

    /// <inheritdoc/>
    public SideFocus Side { get; }

    /// <summary>The slot numbers of the list in display order.</summary>
    public IReadOnlyList<int> List => _list;

    /// <summary>The first visible list entry.</summary>
    public int Scroll => _scroll;

    /// <summary>The slot under the cursor, or -1.</summary>
    public int CursorSlot => _row + _scroll < _list.Count ? _list[_row + _scroll] : -1;

    /// <summary>True while the list was opened from the title screen.</summary>
    public bool FromTitle => Root.Stack.Count > 0 && Root.Stack[0] is TitleScreen;

    /// <inheritdoc/>
    protected override float FadeInStep => 0.024f;

    /// <inheritdoc/>
    protected override float FadeOutStep => 0.048f;

    /// <summary>Canvas position of list frame <paramref name="i"/> (knife <c>save_frame</c>): x 133.667 units, y 36.919 units + 93.748 units per frame.</summary>
    public static (float X, float Y) FramePosition(int i) => (133.667f * 3f, (36.919f * UiMetrics.UnitY) + (93.748f * UiMetrics.UnitY * i));

    /// <inheritdoc/>
    public override void OnOpened()
    {
        Rebuild(resetCursor: true);
        if (FromTitle)
        {
            Ctx.Backdrop.Fade(0.02f);
        }

        // the save list starts on the blank slot; the load list on the newest save
        _row = 0;
        _scroll = 0;
    }

    /// <summary>Rebuilds the list from the slots (knife <c>save_list</c>).</summary>
    public void Rebuild(bool resetCursor)
    {
        IReadOnlyList<UiSlot> slots = Ctx.Services.Slots;
        var used = slots.Where(s => s.Used && s.Index < (Save ? 100 : 139)).OrderByDescending(s => s.Date).ThenByDescending(s => s.Index).Select(s => s.Index).ToList();
        _list = [];
        if (Save)
        {
            int free = slots.Where(s => s.Index < 100 && !s.Used).Select(s => s.Index).DefaultIfEmpty(-1).Min();
            if (free >= 0)
            {
                _list.Add(free);
            }
        }

        _list.AddRange(used);
        int max = Math.Max(0, _list.Count - Visible);
        _scroll = Math.Min(_scroll, max);
        _row = Math.Min(_row, Math.Max(0, Math.Min(_list.Count - _scroll, Visible) - 1));
        if (resetCursor)
        {
            _row = 0;
            _scroll = 0;
        }

        BuildItems();
    }

    private void BuildItems()
    {
        _items.Clear();
        for (int i = 0; i < Visible && _scroll + i < _list.Count; i++)
        {
            (float x, float y) = FramePosition(i);
            _items.Add(new RectF(x + 32f, y + 28f, 898f, 212f), _list[_scroll + i]);
        }

        if (FromTitle)
        {
            _items.Add(new RectF(0, 0, 360f, UiMetrics.Height), -1);
        }

        _items.Cursor = Math.Min(_row, Math.Max(0, Math.Min(_list.Count - _scroll, Visible) - 1));
    }

    private bool Busy => _popup is not null || _saveWait >= 0f || _deleteWait >= 0f || _loadSlot >= 0 || Closing || Root.Top is ConfirmDialog || Root.Top is MessagePopup;

    /// <inheritdoc/>
    public override void StepBackground()
    {
        _flash = MathF.Max(0f, _flash - 0.2f);
        StepFlows();
    }

    /// <inheritdoc/>
    public override void Step(in UiInput input)
    {
        _flash = MathF.Max(0f, _flash - 0.2f);
        StepFlows();
        if (Closing || Root.Top != this)
        {
            return;
        }

        if (Side.Step(input, Ctx))
        {
            return;
        }

        if (Busy)
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

        // the scroll bar: drag the thumb, click the track for a page (package K12)
        int range = Math.Max(0, _list.Count - Visible);
        ScrollBarUse use = _bar.Step(input, range > 0 ? _scroll / (float)range : 0f, 608f, range >= 1);
        if (use.Fraction is { } dragged)
        {
            SetScroll((int)MathF.Round(dragged * range));
        }
        else if (use.Page != 0)
        {
            SetScroll(_scroll + (use.Page * Visible));
        }

        if (use.Consumed)
        {
            return;
        }

        if (input.Left && Side.Menu is not null)
        {
            Side.Enter();
            return;
        }

        int count = _list.Count;
        int cur = _row + _scroll;
        int next = cur;
        if ((input.Up || input.Down) && count > 0)
        {
            int dy = input.Up ? -1 : 1;
            next = cur + dy >= 0 && cur + dy < count ? cur + dy : (input.UpPressed || input.DownPressed) ? (dy < 0 ? count - 1 : 0) : cur;
        }

        if (input.Wheel != 0 && count > 0)
        {
            next = Math.Clamp(cur - Math.Clamp(input.Wheel, -50, 50), 0, count - 1);
        }

        if (input.PointerMoved)
        {
            int hit = _items.HitTest(input.PointerX, input.PointerY);
            if (hit >= 0 && _items.Items[hit].Id >= 0)
            {
                next = _scroll + hit;
            }
        }

        if (next != cur)
        {
            _scroll = next < _scroll ? next : next >= _scroll + Visible ? next + 1 - Visible : _scroll;
            _row = next - _scroll;
            _flash = 1f;
            Ctx.Sound(UiSound.Cursor);
            BuildItems();
        }

        // the guide's north button is "Delete Data" (the PS4 guide sequence 11, word 18 beside the TRIANGLE icon); Delete on the keyboard does the same. Copy Data has
        // no word in any guide sequence: it stays on SQUARE.
        if (input.Triangle || input.DeleteData)
        {
            DeleteKey();
            return;
        }

        if (input.Square)
        {
            CopyKey();
            return;
        }

        int clicked = _items.Clicked(input);
        if (clicked >= 0)
        {
            int id = _items.Items[clicked].Id;
            if (id < 0)
            {
                Ctx.Sound(UiSound.Buzzer);
                Close();
                return;
            }

            _row = clicked;
            Choose(id);
        }
        else if (input.Decide && !input.Click)
        {
            ChooseCursor();
        }
    }

    // the window of four slots moves to start at list entry <paramref name="to"/>; the cursor stays on its row of the window
    private void SetScroll(int to)
    {
        int max = Math.Max(0, _list.Count - Visible);
        to = Math.Clamp(to, 0, max);
        if (to == _scroll)
        {
            return;
        }

        _scroll = to;
        _row = Math.Min(_row, Math.Max(0, Math.Min(_list.Count - _scroll, Visible) - 1));
        _flash = 1f;
        BuildItems();
    }

    private void ChooseCursor()
    {
        if (_list.Count == 0)
        {
            if (!Save)
            {
                Ctx.Sound(UiSound.Buzzer);
                Popup(Ctx.Sysmes(193), 480f);
            }

            return;
        }

        Choose(_list[_scroll + _row]);
    }

    private UiSlot Slot(int index) => Ctx.Services.Slots[index];

    private void Choose(int slot)
    {
        if (Save)
        {
            if (Slot(slot).Used)
            {
                Root.Push(new ConfirmDialog(ConfirmKind.Overwrite, yes =>
                {
                    if (yes)
                    {
                        DoSave(slot);
                    }
                }));
            }
            else
            {
                DoSave(slot);
            }
        }
        else if (Slot(slot).Used)
        {
            Root.Push(new ConfirmDialog(ConfirmKind.Load, yes =>
            {
                if (yes)
                {
                    _popup = Root.Push(new MessagePopup(Ctx.Sysmes(184), held: true));
                    _loadSlot = slot;
                }
            }));
        }
    }

    private void Popup(string text, float frames) => Root.Push(new MessagePopup(text, frames));

    private void DoSave(int slot)
    {
        // "Saving..." (183) is held until the write is done and 30 frames have passed; 189 on failure
        if (Ctx.Services.Save(slot))
        {
            _popup = Root.Push(new MessagePopup(Ctx.Sysmes(183), held: true, minimumFrames: 30f));
            _saveWait = 0f;
            _savedSlot = slot;
            _popup.Release();
        }
        else
        {
            Popup(Ctx.Sysmes(189), 480f);
            Rebuild(resetCursor: false);
        }
    }

    // Copy Data (dialog 24 "Do you want to copy this data?"): SQUARE; the guide has no entry for it
    private void CopyKey()
    {
        int slot = CursorSlot;
        if (slot < 0 || !Slot(slot).Used)
        {
            Ctx.Sound(UiSound.Buzzer);
            return;
        }

        Root.Push(new ConfirmDialog(ConfirmKind.Copy, yes =>
        {
            if (!yes)
            {
                return;
            }

            if (Ctx.Services.Copy(slot))
            {
                Ctx.Sound(UiSound.Done);
                Rebuild(resetCursor: false);
            }
            else
            {
                Ctx.Sound(UiSound.Buzzer);
                Popup(Ctx.Sysmes(189), 480f);
            }
        }));
    }

    private void DeleteKey()
    {
        if (!Save && _list.Count == 0)
        {
            return;
        }

        int slot = CursorSlot;
        if (slot < 0)
        {
            return;
        }

        if (!Slot(slot).Used)
        {
            Popup(Ctx.Sysmes(193), 480f);
            return;
        }

        Root.Push(new ConfirmDialog(ConfirmKind.Delete, yes =>
        {
            if (yes)
            {
                _popup = Root.Push(new MessagePopup(Ctx.Sysmes(188), held: true));
                Ctx.Services.Delete(slot);
                _deleteWait = 0f;
            }
        }));
    }

    private void StepFlows()
    {
        if (_popup is { Closing: true } or { Finished: true })
        {
            _popup = null;
        }

        if (_saveWait >= 0f)
        {
            float n = _saveWait + 1f;
            if (_saveWait < 30f && n >= 30f)
            {
                Ctx.Sound(UiSound.Done);
            }

            if (n >= 30f + 41f)
            {
                _saveWait = -1f;
                Rebuild(resetCursor: false);
                SelectSlot(_savedSlot);
            }
            else
            {
                _saveWait = n;
            }
        }

        if (_deleteWait >= 0f)
        {
            float n = _deleteWait + 1f;
            if (_deleteWait < 1f && n >= 1f)
            {
                Ctx.Sound(UiSound.Done);
            }

            if (n >= 17f)
            {
                _deleteWait = -1f;
                _popup?.Release();
                Rebuild(resetCursor: false);
                Popup(Ctx.Message(217), 300f);
            }
            else
            {
                _deleteWait = n;
            }
        }

        if (_loadSlot >= 0 && _popup is { FullyShown: true })
        {
            int slot = _loadSlot;
            _loadSlot = -1;
            _popup.Release();
            if (FromTitle)
            {
                _loadClosing = true;
                Close();
                if (Root.Stack[0] is TitleScreen t)
                {
                    t.BeginLoad(slot);
                }
            }
            else if (Ctx.Services.Load(slot))
            {
                Root.Clear();
            }
            else
            {
                Popup(Ctx.Sysmes(190), 480f);
            }
        }
    }

    private void SelectSlot(int slot)
    {
        int i = _list.IndexOf(slot);
        if (i < 0)
        {
            return;
        }

        _scroll = Math.Clamp(i, 0, Math.Max(0, _list.Count - Visible));
        _row = Math.Min(i - _scroll, Visible - 1);
        BuildItems();
    }

    /// <summary>True when the load closed over the title to hand the slot to the title.</summary>
    public bool LoadHandedToTitle => _loadClosing;

    /// <inheritdoc/>
    public override void OnClosed()
    {
        _popup?.Release();
    }

    /// <inheritdoc/>
    public override void Draw(UiPainter p)
    {
        using UiPainter.AlphaScope scope = p.WithAlpha(Fade);
        p.Sprite(UiSheets.SavePanel, new RectF(0, 0, 1560, 1080), new RectF(360, 0, 1560, 1080));
        UiSlot? selected = null;
        int cursorRow = Math.Min(_row, Math.Max(0, Math.Min(_list.Count - _scroll, Visible) - 1));
        for (int i = 0; i < Visible && _scroll + i < _list.Count; i++)
        {
            int slot = _list[_scroll + i];
            (float fx, float fy) = FramePosition(i);
            bool hover = i == cursorRow && !(Side.Active);
            RectF win = hover ? new RectF(31, 336, 960, 272) : new RectF(32, 32, 960, 272);
            p.Sprite(UiSheets.SaveFrame, win, new RectF(fx, fy, 960, 272));
            if (hover && _flash > 0f)
            {
                p.Sprite(UiSheets.SaveFrame, new RectF(31, 336, 960, 272), new RectF(fx, fy, 960, 272), _flash, UiBlend.PsAdd);
            }

            UiSlot d = Slot(slot);
            if (!d.Used)
            {
                continue;
            }

            if (hover)
            {
                selected = d;
            }

            DrawEntry(p, d, fx, fy);
        }

        if (selected is { } sel)
        {
            DrawInfo(p, sel);
        }

        int range = Math.Max(0, _list.Count - Visible);
        if (range >= 1)
        {
            UiWidgets.ScrollBar(p, _scroll / (float)range, 608f, 1f, _bar);
        }

        UiWidgets.Guide(p, UiWidgets.GuideSaveList, decideIsCross: Ctx.Services.DecideIsCross);
    }

    private void DrawEntry(UiPainter p, UiSlot d, float fx, float fy)
    {
        UiThumbnail? thumb = Ctx.Services.Thumbnail(d.Index);
        if (thumb is not null)
        {
            // sub_4F240: 100 * 0.827 x 53 * 1.092 units at (151, y + 23.23), scaled 1.15 about its top left
            p.Thumbnail(d.Index, thumb.Version, new RectF(fx + 52f, fy + 56f, 300f * 0.827f * 1.15f, 53f * 1.092f * 1.15f * UiMetrics.UnitY));
        }

        p.Sprite(UiSheets.SaveSceneTag, new RectF(0, 0, 64, 24), new RectF(fx + 359f, fy + 192f, 64, 24));
        string stamp = d.Date.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);
        float gy = fy + 60f;
        float[] dateX = [118f, 123.5f, 129f, 134.5f, 145.5f, 151f, 162f, 167.5f];
        float[] timeX = [182.333f, 187.833f, 198.833f, 204.333f];
        for (int k = 0; k < 8; k++)
        {
            Digit(p, UiSheets.SaveDateDigits, stamp[k] - '0', fx + (dateX[k] * 3f), gy);
        }

        Digit(p, UiSheets.SaveDateDigits, 10, fx + (140f * 3f), gy);
        Digit(p, UiSheets.SaveDateDigits, 10, fx + (156.5f * 3f), gy);
        for (int k = 0; k < 4; k++)
        {
            Digit(p, UiSheets.SaveTimeDigits, stamp[8 + k] - '0', fx + (timeX[k] * 3f), gy);
        }

        Digit(p, UiSheets.SaveTimeDigits, 10, fx + (193.333f * 3f), gy);
        if (d.Index >= 100)
        {
            p.Sprite(UiSheets.SaveAuto, new RectF(0, 0, 192, 32), new RectF(fx + 744f, fy + 26f, 192, 32));
        }

        IReadOnlyList<SheetCell> titles = Ctx.Assets.Tables.SaveTitleCells(Ctx.Language);
        int phase = Math.Clamp(d.Phase, 0, titles.Count - 1);
        SheetCell c = titles[phase];
        var src = RectF.FromCorners(c.X0, c.Y0, c.X1, c.Y1);
        p.Sprite(UiSheets.SaveTitles, src, new RectF(fx + 359f, fy + 121f, src.W, src.H));
        string scene = Ctx.Message(396 + d.SceneTitle);
        if (!string.IsNullOrWhiteSpace(scene))
        {
            // sub_50B10: slots 34..37 at 0.35, black; sub_4F240 places them at (274.667, y + 79.644) of 640 x 448
            var style = TextStyle.Menu(0.35) with { Width = 190, Colour = TextStyle.Black, DrawFlags = BlockDraw.Always };
            Ctx.Text.Draw(p, scene.Replace("^", string.Empty, StringComparison.Ordinal), style, (fx + 423f) / UiMetrics.UnitX, (fy + 188f) / UiMetrics.UnitY);
        }
    }

    private static void Digit(UiPainter p, UiSheet sheet, int d, float x, float y) =>
        p.Sprite(sheet, new RectF(16 + (32 * d), 16, 24, 32), new RectF(x, y, 24, 32));

    private void DrawInfo(UiPainter p, UiSlot d)
    {
        int route = d.Phase - 11 >= 0 && d.Phase - 11 < InfoRoute.Length ? InfoRoute[d.Phase - 11] : 2;
        UiSheet card = route switch { 0 => UiSheets.SaveInfoArc, 1 => UiSheets.SaveInfoCiel, _ => UiSheets.SaveInfoUnknown };
        p.Sprite(card, new RectF(0, 0, 472, 712), new RectF(1360, 95, 472, 712));
        if (!string.IsNullOrWhiteSpace(d.Notes))
        {
            // sub_50B10: the notes slot at 0.4, a box 130 units wide, black, 4 lines; placed at (463.333, 259.259)
            var style = TextStyle.Menu(0.4) with { Width = 130, Height = 80, Leading = 4, Colour = TextStyle.Black, DrawFlags = BlockDraw.Always };
            Ctx.Text.Draw(p, d.Notes, style, 463.333f, 259.259f);
        }
    }
}

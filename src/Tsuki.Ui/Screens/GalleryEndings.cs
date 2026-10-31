// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (cGalleryEnding: Screen::Endings draw, ending_move, Action::Ending, Confirm::Ending)
using Tsuki.Engine.Contracts;

namespace Tsuki.Ui.Screens;

/// <summary>
/// The ending list (docs/formats/gallery.md 8; knife <c>cGalleryEnding</c>): the three main endings as large cards, the bad ends as a grid of plates (eight to a
/// row), and the last entry (the 25th "Teach Me, Miss Ciel" scene) as a button that appears once every other ending is reached. A reached ending has a play
/// button; it asks "Play ...?" and then replays the scene through the gallery scene (<see cref="IUiGalleryServices.ReplayScene"/>). Entries 0 to 2 are the main
/// endings, 3 to 23 the bad ends, 24 the last scene.
/// </summary>
internal sealed class GalleryEndingsPanel : GalleryPanel
{
    private bool[] _ok = [];
    private bool _all;
    private int _cur = -1;

    /// <inheritdoc/>
    public override GalleryMode Mode => GalleryMode.Endings;

    /// <inheritdoc/>
    public override bool Selectable => _ok.Any(x => x);

    /// <summary>The entry under the cursor, or -1.</summary>
    public int Cursor => _cur;

    /// <summary>The reached state of every entry.</summary>
    public IReadOnlyList<bool> Reached => _ok;

    /// <summary>The (column, row) of entry <paramref name="i"/> in the navigation grid: row 0 the three main endings, rows 1 to 3 the bad ends eight to a row.</summary>
    public static (int Col, int Row) Position(int i) => i < 3 ? (i, 0) : ((i - 3) % 8, ((i - 3) / 8) + 1);

    /// <summary>The entry at (<paramref name="c"/>, <paramref name="r"/>).</summary>
    public static int EntryAt(int c, int r) => r == 0 ? c : 3 + c + (8 * (r - 1));

    /// <summary>The canvas rectangle of the play button of entry <paramref name="i"/> (empty for an entry that is not reached).</summary>
    public RectF ButtonRect(int i)
    {
        if (i >= _ok.Length || !_ok[i])
        {
            return default;
        }

        if (i < 3)
        {
            return new RectF(720f + (i * 480f), 128f, 112f, 112f);
        }

        if (i == 24)
        {
            return new RectF(1702f, 894f, 112f, 112f);
        }

        int b = i - 3;
        return new RectF(((b & 7) * 180f) + 384f, ((b >> 3) * 96f) + 680f, 88f, 88f);
    }

    /// <inheritdoc/>
    public override void Show()
    {
        int n = Host.Model.EndingCount;
        _ok = new bool[n];
        for (int i = 0; i < n; i++)
        {
            _ok[i] = Host.Model.EndingUnlocked(i, Host.Services);
        }

        _all = n > 0 && Host.Model.AllEndings(Host.Services);
        _cur = -1;
    }

    /// <summary>Puts the cursor on the entry of the scene a replay started from (the gallery comes back on it).</summary>
    public void Focus(int scene)
    {
        for (int i = 0; i < _ok.Length; i++)
        {
            if (Host.Model.Tables.Endings[i].ScriptScene == scene && _ok[i])
            {
                _cur = i;
                return;
            }
        }
    }

    /// <inheritdoc/>
    public override bool TakeFocus()
    {
        if (_cur >= 0 && _cur < _ok.Length && _ok[_cur])
        {
            return true;
        }

        int first = Array.IndexOf(_ok, true);
        if (first < 0)
        {
            return false;
        }

        _cur = first;
        return true;
    }

    /// <inheritdoc/>
    public override void LoseFocus() => _cur = -1;

    /// <inheritdoc/>
    public override void Tick()
    {
    }

    /// <inheritdoc/>
    public override bool PointerOver(float x, float y)
    {
        for (int i = 0; i < _ok.Length; i++)
        {
            if (ButtonRect(i).Contains(x, y))
            {
                if (i != _cur)
                {
                    _cur = i;
                    Host.Sound(UiSound.Cursor);
                }

                return true;
            }
        }

        return false;
    }

    private bool Ok(int c, int r)
    {
        bool valid = r == 0 ? c < 3 : r <= 3 && c < 8 && (r < 3 || c <= 5);
        if (!valid)
        {
            return false;
        }

        int e = EntryAt(c, r);
        return e < _ok.Length && _ok[e];
    }

    /// <summary>
    /// The cursor move of the list (sub_F470 left, sub_F5A4 right, sub_F6F8 up and down): only reached entries take the cursor. Returns the entry moved to, -1 to
    /// give the focus to the mode buttons (left past the first entry), or null when the cursor stays.
    /// </summary>
    public int? NextEntry(int i, int dx, int dy)
    {
        (int col, int row) = Position(i);
        if (dx < 0)
        {
            for (int c = col - 1; c >= 0; c--)
            {
                if (Ok(c, row))
                {
                    return EntryAt(c, row);
                }
            }

            return -1;
        }

        if (dx > 0)
        {
            int last = row == 0 ? 2 : 7;
            for (int c = col + 1; c <= last; c++)
            {
                if (Ok(c, row))
                {
                    return EntryAt(c, row);
                }
            }

            return null;
        }

        bool qa = _all;
        int? Nearest(int r)
        {
            if (r == 0)
            {
                int want = row == 0 ? col : col < 3 ? 0 : col < 6 ? 1 : 2;
                (int C, int D)? best0 = null;
                for (int c = 0; c < 3; c++)
                {
                    // ties keep the lower column
                    if (Ok(c, 0) && (best0 is null || Math.Abs(want - c) < best0.Value.D))
                    {
                        best0 = (c, Math.Abs(want - c));
                    }
                }

                return best0?.C;
            }

            int from = row == 0 ? new[] { 2, 5, 7 }[col] : col;

            // coming down from columns 6 / 7 lands on the QA button; going up from it, on column 7
            if (qa && r == 3 && dy > 0 && (col & ~1) == 6 && row > 0)
            {
                return 5;
            }

            if (qa && r == 2 && dy < 0 && row == 3 && col == 5)
            {
                return 7;
            }

            int cols = r == 3 ? 5 : 8;
            (int C, int D)? best = null;
            for (int c = 0; c < cols; c++)
            {
                // ties take the later column
                if (Ok(c, r) && (best is null || Math.Abs(from - c) <= best.Value.D))
                {
                    best = (c, Math.Abs(from - c));
                }
            }

            return best?.C;
        }

        IEnumerable<int> rows = dy < 0 ? Enumerable.Range(0, row).Reverse() : Enumerable.Range(row + 1, 3 - row);
        foreach (int r in rows)
        {
            if (Nearest(r) is { } c)
            {
                return EntryAt(c, r);
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public override void Move(int dx, int dy, bool fresh)
    {
        if (_cur < 0)
        {
            TakeFocus();
            return;
        }

        int? next = NextEntry(_cur, dx, dx != 0 ? 0 : dy);
        if (next is null)
        {
            return;
        }

        if (next < 0)
        {
            Host.ToSide();
            return;
        }

        _cur = next.Value;
        Host.Sound(UiSound.Cursor);
    }

    /// <inheritdoc/>
    public override void Activate()
    {
        if (_cur < 0 || _cur >= _ok.Length || !_ok[_cur])
        {
            return;
        }

        // sound 1, then the dialog "Play ...?" (the cursor starts on No); Yes closes the gallery and replays the QA scene
        Host.Sound(UiSound.Decide);
        GalleryEndingRow row = Host.Model.Tables.Endings[_cur];
        Host.Root.Push(new ConfirmDialog(row.MessageUi, true, yes =>
        {
            if (yes)
            {
                Host.Replay(row.ScriptScene, -1);
            }
        }));
    }

    /// <inheritdoc/>
    public override void Draw(UiPainter p, float alpha, bool focused)
    {
        for (int e = 0; e < Math.Min(_ok.Length, 24); e++)
        {
            bool ok = _ok[e];
            bool hov = focused && _cur == e;
            if (e < 3)
            {
                p.SpriteAt(ok ? GallerySheets.EndingCard(e) : GallerySheets.EndingCardLocked, 416f + (e * 480f), 120f);
                if (ok)
                {
                    p.SpriteAt(GallerySheets.EndingButtons, new RectF(hov ? 128f : 8f, 8f, 112f, 112f), 720f + (e * 480f), 128f);
                }

                continue;
            }

            // the plates: reached ones are lit (rows 296 + 96 r), the others dark (rows 8 + 96 r)
            int b = e - 3;
            float x = ((b & 7) * 180f) + 384f;
            float y = ((b >> 3) * 96f) + 680f;
            float y0 = ok ? 296f : 8f;
            p.SpriteAt(GallerySheets.EndingPlates, new RectF(((b & 7) * 180f) + 8f, ((b >> 3) * 96f) + y0, 180f, 88f), x, y);
            if (ok)
            {
                p.SpriteAt(GallerySheets.EndingPlateButtons, new RectF(hov ? 104f : 8f, 8f, 88f, 88f), x, y);
            }
        }

        if (_all && _ok.Length == 25)
        {
            p.SpriteAt(GallerySheets.EndingButtons, new RectF(focused && _cur == 24 ? 128f : 8f, 8f, 112f, 112f), 1702f, 894f);
        }
    }
}

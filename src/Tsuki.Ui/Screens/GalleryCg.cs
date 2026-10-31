// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (cGalleryCGMain / cGalleryCGSub grid: cg_cell, cg_show, gal_follow, gal_keep_cursor, draw_cg_grid; cGalleryCGPhoto viewer: cg_geom, cg_step, cg_viewed)
using Tsuki.Engine.Contracts;

namespace Tsuki.Ui.Screens;

/// <summary>
/// The CG gallery's content (docs/formats/gallery.md 4.5; knife <c>cGalleryCGMain</c> and <c>cGalleryCGSub</c>): a grid of 6 columns of the groups with at least one
/// unlocked picture, or, after a group with several pictures is picked, the grid of its variations. A group with one picture opens in the viewer at once.
/// </summary>
internal sealed class GalleryCgPanel : GalleryPanel
{
    /// <summary>Grid columns.</summary>
    public const int Cols = 6;

    private const float ClipTop = 109f;
    private const float ClipBottom = 1014f;
    private const float FadeFrames = 30f;

    private readonly ListScroll _scroll = new();
    private readonly UiScrollBar _bar = new();
    private int[][] _unlocked = [];
    private int[] _list = [];
    private int? _sub;
    private int _cur = -1;
    private int _mem = -1;
    private int _pickMain = -1;
    private float _pickMainScroll;
    private float _pickSubScroll;
    private float _flash;
    private float _in = FadeFrames;
    private bool _track;
    private OldGrid? _old;

    private sealed record OldGrid(int[] List, int? Sub, float Scroll);

    /// <inheritdoc/>
    public override GalleryMode Mode => GalleryMode.Cg;

    /// <inheritdoc/>
    public override bool Selectable => _list.Length > 0;

    /// <inheritdoc/>
    public override int GuideIndex => ScrollMax > 0f ? 0 : 10;

    /// <summary>The unlocked picture indices of every group.</summary>
    public IReadOnlyList<int[]> Unlocked => _unlocked;

    /// <summary>The group whose variations grid shows, or null for the group grid.</summary>
    public int? Sub => _sub;

    /// <summary>The cursor position in the shown grid, or -1.</summary>
    public int Cursor => _cur;

    /// <summary>The number of entries in the shown grid.</summary>
    public int Count => _list.Length;

    /// <summary>The scroll position.</summary>
    public float Scroll => _scroll.Pos;

    /// <summary>The scroll range (sub_AF78: (rows - 6) * 139 - 69.5).</summary>
    public float ScrollMax => ((int)MathF.Ceiling(_list.Length / (float)Cols) - 6) * 139f - 69.5f;

    /// <summary>The unlocked CG at (<paramref name="group"/>, <paramref name="pos"/>).</summary>
    public GalleryCg Entry(int group, int pos) => Host.Model.Groups[group][_unlocked[group][pos]];

    /// <summary>Every unlocked picture in group order: the viewer steps through it.</summary>
    public List<(int Group, int Pos)> Flat()
    {
        var flat = new List<(int, int)>();
        for (int g = 0; g < _unlocked.Length; g++)
        {
            for (int k = 0; k < _unlocked[g].Length; k++)
            {
                flat.Add((g, k));
            }
        }

        return flat;
    }

    private void Rebuild() =>
        _list = _sub is { } g ? [.. Enumerable.Range(0, _unlocked[g].Length)] : [.. Enumerable.Range(0, _unlocked.Length).Where(i => _unlocked[i].Length > 0)];

    /// <summary>The thumbnail rectangle of grid entry <paramref name="k"/> (a 231 x 130.5 cell, 6 columns).</summary>
    public static RectF Cell(int k, float scroll) => new(392f + ((k % Cols) * 240f), 109f + ((k / Cols) * 139f) - MathF.Truncate(scroll), 231f, 130.5f);

    /// <inheritdoc/>
    public override void Show()
    {
        _unlocked = Host.Model.Unlocked(Host.Services);
        _sub = null;
        _old = null;
        Rebuild();
        _scroll.Set(0f);
        _cur = -1;
        _mem = -1;
        _in = FadeFrames;
        _track = false;
    }

    /// <inheritdoc/>
    public override bool TakeFocus()
    {
        if (_list.Length == 0)
        {
            return false;
        }

        // giving the content up drops the column (sub_ACA0 -> -1), taking it back puts column 0 of the same row back
        int a = _mem >= 0 && _mem < _list.Length ? _mem - (_mem % Cols) : 0;
        Follow(a, null, animate: false);
        _cur = a;
        _flash = 0f;
        return true;
    }

    /// <inheritdoc/>
    public override void LoseFocus()
    {
        _mem = _cur >= 0 ? _cur : _mem;
        _cur = -1;
    }

    /// <summary>Keeps a row in view after a cursor move from row <paramref name="prev"/> (null: either way; knife <c>gal_follow</c>).</summary>
    private void Follow(int k, int? prev, bool animate)
    {
        _track = false;
        float max = ScrollMax;
        float s = _scroll.Target;
        int row = k / Cols;
        int last = ((_list.Length + Cols - 1) / Cols) - 1;
        float y = 109f + (row * 139f) - s;
        if ((prev is null || row > prev) && y > 804f)
        {
            float v = (row - 5) * 139f;
            if (row == last)
            {
                v -= 69.5f;
            }

            _scroll.To(v, max, animate);
        }
        else if ((prev is null || row < prev) && y < 178.5f)
        {
            _scroll.To(row == 0 ? 0f : ((row - 1) * 139f) + 69.5f, max, animate);
        }
    }

    /// <summary>
    /// Makes the grid of <paramref name="sub"/> (null: the group grid) with the cursor on entry <paramref name="curPos"/> (knife <c>cg_show</c>): the saved
    /// scroll is cut to whole rows, then moved a row at a time until the cursor is inside the view; the grid fades in over half a second while the old one,
    /// when <paramref name="fadeOld"/>, fades out.
    /// </summary>
    private void ShowGrid(int? sub, int curPos, float scroll, bool fadeOld)
    {
        _old = fadeOld ? new OldGrid(_list, _sub, _scroll.Pos) : null;
        _sub = sub;
        Rebuild();
        _in = 0f;
        int row = Math.Max(curPos, 0) / Cols;
        int k = ((int)(MathF.Ceiling(scroll + 0.001f) + 0.001f)) / 139;
        float y = 103f + (row * 139f);
        while (y - (k * 139f) < 109f)
        {
            k--;
        }

        while (y - (k * 139f) >= 804f)
        {
            k++;
        }

        _scroll.To(k * 139f, ScrollMax, animate: false);
        _mem = curPos;
        if (_cur >= 0 || !Host.SideFocus)
        {
            _cur = Math.Clamp(curPos, 0, Math.Max(_list.Length - 1, 0));
            _flash = 0f;
        }
    }

    /// <summary>The viewer closed on picture <paramref name="pos"/> of <paramref name="group"/> (knife <c>cg_viewed</c>): the picked group's own grid comes back.</summary>
    public void ViewerClosed(int group, int pos)
    {
        bool sub = _pickMain == group ? _sub is not null : _unlocked[group].Length > 1;
        if (sub)
        {
            ShowGrid(group, pos, _pickSubScroll, fadeOld: false);
        }
        else
        {
            int[] main = [.. Enumerable.Range(0, _unlocked.Length).Where(i => _unlocked[i].Length > 0)];
            ShowGrid(null, Math.Max(Array.IndexOf(main, group), 0), _pickMainScroll, fadeOld: false);
        }

        _pickMain = group;
        _in = FadeFrames;
        _old = null;
    }

    /// <inheritdoc/>
    public override void Tick()
    {
        _scroll.Step();
        _flash++;
        if (_in < FadeFrames)
        {
            _in++;
        }
        else
        {
            _old = null;
        }

        if (_track && _cur >= 0)
        {
            KeepCursor();
        }
    }

    // after a manual scroll the cursor row is pulled back into view: the cursor y (cell - 6) inside 103..943 (sub_9CBC)
    private void KeepCursor()
    {
        float s = MathF.Truncate(_scroll.Pos);
        int col = _cur % Cols;
        int row = _cur / Cols;
        float y = 103f + (row * 139f) - s;
        while (y < 103f)
        {
            y += 139f;
            row++;
        }

        while (y >= 943f && row > 0)
        {
            y -= 139f;
            row--;
        }

        int k = col + (row * Cols);
        if (k < _list.Length && k != _cur)
        {
            _cur = k;
            _mem = k;
            _flash = 0f;
            Host.Sound(UiSound.Cursor);
        }
    }

    /// <inheritdoc/>
    public override bool PointerOver(float x, float y)
    {
        if (y < ClipTop || y >= ClipBottom)
        {
            return false;
        }

        for (int k = 0; k < _list.Length; k++)
        {
            if (Cell(k, _scroll.Pos).Contains(x, y))
            {
                if (k != _cur)
                {
                    _cur = k;
                    _flash = 0f;
                    Host.Sound(UiSound.Cursor);
                }

                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public override void Move(int dx, int dy, bool fresh)
    {
        if (_list.Length == 0)
        {
            return;
        }

        if (_cur < 0)
        {
            _cur = 0;
            return;
        }

        int n = _list.Length;
        int k = _cur;
        int col = k % Cols;
        int row = k / Cols;
        int next;
        if (dx < 0)
        {
            if (col == 0)
            {
                Host.ToSide();
                return;
            }

            next = k - 1;
        }
        else if (dx > 0)
        {
            if (col >= Cols - 1 || k + 1 >= n)
            {
                return;
            }

            next = k + 1;
        }
        else if (dy < 0)
        {
            if (row == 0)
            {
                return;
            }

            next = k - Cols;
        }
        else
        {
            if (k + Cols >= n)
            {
                return;
            }

            next = k + Cols;
        }

        Follow(next, row, animate: dy != 0);
        _cur = next;
        _mem = next;
        _flash = 0f;
        Host.Sound(UiSound.Cursor);
    }

    /// <inheritdoc/>
    public override void Wheel(int steps)
    {
        if (steps == 0 || ScrollMax <= 0f)
        {
            return;
        }

        _scroll.To(_scroll.Target - (steps * 139f), ScrollMax, animate: true);
        _track = true;
    }

    /// <summary>A click on the scroll bar's track scrolls five rows (sub_AF78's view is six rows tall).</summary>
    private const float PageScroll = 5f * 139f;

    /// <inheritdoc/>
    public override bool StepPointer(in UiInput input)
    {
        float max = ScrollMax;
        ScrollBarUse use = _bar.Step(input, max > 0f ? _scroll.Pos / max : 0f, 610f, max > 0f);
        if (use.Fraction is { } dragged)
        {
            _scroll.Over(dragged * max, max, 6f);
            _track = true;
        }
        else if (use.Page != 0)
        {
            _scroll.To(_scroll.Target + (use.Page * PageScroll), max, animate: true);
            _track = true;
        }

        return use.Consumed;
    }

    /// <inheritdoc/>
    public override void Activate()
    {
        if (_cur < 0 || _cur >= _list.Length)
        {
            return;
        }

        Host.Sound(UiSound.Decide);
        if (_sub is { } g)
        {
            _pickSubScroll = _scroll.Pos;
            Host.OpenViewer(this, g, _cur);
            return;
        }

        int group = _list[_cur];
        _pickMain = group;
        _pickMainScroll = _scroll.Pos;
        if (_unlocked[group].Length > 1)
        {
            ShowGrid(group, 0, 0f, fadeOld: true);
            _cur = 0;
            _flash = 0f;
        }
        else
        {
            Host.OpenViewer(this, group, 0);
        }
    }

    /// <inheritdoc/>
    public override bool Cancel()
    {
        if (_sub is null)
        {
            return false;
        }

        // sub_8D08: B in a variations grid makes the group grid again on the picked group
        int[] main = [.. Enumerable.Range(0, _unlocked.Length).Where(i => _unlocked[i].Length > 0)];
        ShowGrid(null, Math.Max(Array.IndexOf(main, _pickMain), 0), _pickMainScroll, fadeOld: true);
        _cur = _mem;
        _flash = 0f;
        return true;
    }

    /// <summary>The thumbnail frame: the first frame of the sheet (231 x 130; four-frame sheets stack their frames).</summary>
    private static RectF ThumbSource => new(0, 0, 231, 130);

    /// <inheritdoc/>
    public override void Draw(UiPainter p, float alpha, bool focused)
    {
        using (p.WithClip(new RectF(0, ClipTop, 1920, ClipBottom - ClipTop)))
        {
            float u = Math.Clamp(_in / FadeFrames, 0f, 1f);
            if (_old is { } old && _in < FadeFrames)
            {
                using (p.WithAlpha((1f - u) * (1f - u)))
                {
                    DrawGrid(p, old.List, old.Sub, old.Scroll, -1, 0f);
                }
            }

            using (p.WithAlpha(_in < FadeFrames ? u * u : 1f))
            {
                DrawGrid(p, _list, _sub, _scroll.Pos, focused ? _cur : -1, _flash);
            }
        }

        if (ScrollMax > 0f)
        {
            UiWidgets.ScrollBar(p, _scroll.Pos / ScrollMax, 610f, 1f, _bar);
        }
    }

    private void DrawGrid(UiPainter p, int[] list, int? sub, float scroll, int cursor, float flash)
    {
        for (int k = 0; k < list.Length; k++)
        {
            RectF rc = Cell(k, scroll);
            if (rc.Bottom < ClipTop || rc.Y > ClipBottom)
            {
                continue;
            }

            GalleryCg cg = sub is { } g ? Host.Model.Groups[g][_unlocked[g][list[k]]] : Host.Model.Groups[list[k]][_unlocked[list[k]][0]];
            p.Sprite(GallerySheets.CgThumb(cg.Row.Thumbnail), ThumbSource, rc);
            if (k == cursor)
            {
                var at = new RectF(rc.X - 8f, rc.Y - 6f, 248f, 144f);
                p.Sprite(GallerySheets.CgCursor, new RectF(8, 8, 248, 144), at);
                float a = 1f - (flash / 15f);
                if (a > 0f)
                {
                    p.Sprite(GallerySheets.CgCursor, new RectF(264, 8, 248, 144), at, a);
                }
            }
        }
    }
}

/// <summary>
/// The CG viewer (knife <c>cGalleryCGPhoto</c>, docs/formats/gallery.md 4.5): a full-screen light grey layer under the picture at the fit-to-screen scale, which
/// can be enlarged up to 1:1 and moved; Left / Right (and L1 / R1) step through every unlocked picture of the gallery with a crossfade; Cancel closes. The
/// button guide shows for two seconds after each change. The PC input: Up / Down and the wheel zoom, dragging with the left button moves the picture.
/// </summary>
internal sealed class GalleryCgViewer : UiScreen
{
    private const float Frames = 30f;

    private readonly GalleryCgPanel _panel;
    private readonly List<(int Group, int Pos)> _flat;
    private readonly Dictionary<(int, int), UiPicture?> _pictures = [];
    private int _group;
    private int _pos;
    private float _zoom;
    private (float X, float Y) _pan;
    private (int Group, int Pos, float Zoom, (float X, float Y) Pan)? _previous;
    private float _switchT = Frames;
    private int _guidePhase = 1;
    private float _guideT;
    private (float X, float Y)? _drag;
    private (float X, float Y)? _press;
    private bool _pressMoved;
    private bool _notified;

    /// <summary>Creates the viewer on picture <paramref name="pos"/> of <paramref name="group"/>.</summary>
    public GalleryCgViewer(GalleryCgPanel panel, int group, int pos)
    {
        _panel = panel;
        _flat = panel.Flat();
        _group = group;
        _pos = pos;
        StartHidden();
    }

    /// <summary>The group on view.</summary>
    public int Group => _group;

    /// <summary>The picture on view (position in its group's unlocked list).</summary>
    public int Pos => _pos;

    /// <summary>The zoom (0: fit).</summary>
    public float Zoom => _zoom;

    /// <inheritdoc/>
    public override bool Opaque => Fade >= 1f && !Closing;

    /// <inheritdoc/>
    protected override float FadeInStep => 1f / Frames;

    /// <inheritdoc/>
    protected override float FadeOutStep => 1f / Frames;

    private GalleryCg Current => _panel.Entry(_group, _pos);

    /// <inheritdoc/>
    public override void Close()
    {
        if (Closing)
        {
            return;
        }

        if (!_notified)
        {
            _notified = true;
            _panel.ViewerClosed(_group, _pos);
        }

        _guidePhase = _guidePhase is 1 or 2 ? 3 : _guidePhase;
        _guideT = 0f;
        base.Close();
    }

    /// <summary>The picture rectangle of a <paramref name="w"/> x <paramref name="h"/> picture at <paramref name="zoom"/> moved by <paramref name="pan"/> (sub_BE84 / sub_D270).</summary>
    public static (RectF Rect, float Zoom, (float X, float Y) Pan) Geometry(float w, float h, float zoom, (float X, float Y) pan)
    {
        float fit = MathF.Min(UiMetrics.Width / w, UiMetrics.Height / h);
        float max = MathF.Max(fit, 1f);
        float s = zoom <= 0f ? fit : Math.Clamp(zoom, fit, max);
        float dw = w * s;
        float dh = h * s;
        float mx = MathF.Abs((dw - UiMetrics.Width) / 2f);
        float my = MathF.Abs((dh - UiMetrics.Height) / 2f);
        (float X, float Y) clamped = (Math.Clamp(pan.X, -mx, mx), Math.Clamp(pan.Y, -my, my));
        return (new RectF(((UiMetrics.Width - dw) / 2f) + clamped.X, ((UiMetrics.Height - dh) / 2f) + clamped.Y, dw, dh), s, clamped);
    }

    private void Switch(int d)
    {
        if (_flat.Count < 2 || _switchT < Frames)
        {
            return;
        }

        int i = _flat.IndexOf((_group, _pos));
        if (i < 0)
        {
            return;
        }

        (int g2, int p2) = _flat[(((i + d) % _flat.Count) + _flat.Count) % _flat.Count];
        Ctx.Sound(UiSound.Cursor);
        _previous = (_group, _pos, _zoom, _pan);
        _switchT = 0f;
        (GalleryCg a, GalleryCg b) = (_panel.Entry(_group, _pos), _panel.Entry(g2, p2));
        _group = g2;
        _pos = p2;
        if (a.Row.Width != b.Row.Width || a.Row.Height != b.Row.Height)
        {
            _zoom = 0f;
            _pan = (0f, 0f);
        }

        ShowGuide();
    }

    private void ShowGuide()
    {
        if (_guidePhase == 2)
        {
            _guideT = 0f;
        }
        else if (_guidePhase != 1)
        {
            _guidePhase = 1;
            _guideT = 0f;
        }
    }

    /// <inheritdoc/>
    public override void Step(in UiInput input)
    {
        _guideT += 1f / 60f;
        switch (_guidePhase)
        {
            case 1 when _guideT >= 0.5f:
                _guidePhase = 2;
                _guideT = 0f;
                break;
            case 2 when _guideT >= 2f:
                _guidePhase = 3;
                _guideT = 0f;
                break;
            case 3 when _guideT >= 0.5f:
                _guidePhase = 0;
                _guideT = 0f;
                break;
        }

        if (_switchT < Frames)
        {
            _switchT++;
        }
        else
        {
            _previous = null;
        }

        if (Closing || Fade < 1f)
        {
            return;
        }

        if (input.Cancel)
        {
            Ctx.Sound(UiSound.Buzzer);
            Close();
            return;
        }

        bool horizontal = (input.LeftPressed || input.RightPressed || input.L1 || input.R1) && !input.UpPressed && !input.DownPressed;
        if (horizontal)
        {
            Switch(input.LeftPressed || input.L1 ? -1 : 1);
            return;
        }

        float zoomStep = input.Up ? 1.12f : input.Down ? 1f / 1.12f : input.Wheel > 0 ? 1.12f : input.Wheel < 0 ? 1f / 1.12f : 1f;
        GalleryCg cg = Current;
        (float w, float h) = (Math.Max(cg.Row.Width, 1), Math.Max(cg.Row.Height, 1));
        (_, float zoom, (float X, float Y) pan) = Geometry(w, h, _zoom, _pan);
        if (zoomStep != 1f)
        {
            zoom *= zoomStep;
        }

        if (input.PointerHeld && input.PointerActive)
        {
            if (_drag is { } last && input.PointerMoved)
            {
                pan = (pan.X + (input.PointerX - last.X), pan.Y + (input.PointerY - last.Y));
            }

            if (_press is { } start && MathF.Abs(input.PointerX - start.X) + MathF.Abs(input.PointerY - start.Y) >= 16f)
            {
                _pressMoved = true;
            }

            if (input.Click)
            {
                _press = (input.PointerX, input.PointerY);
                _pressMoved = false;
            }

            _drag = (input.PointerX, input.PointerY);
        }
        else
        {
            _drag = null;
            if (_press is { } start)
            {
                // package K12: a click that did not drag the picture goes to the next one (the left quarter of the screen: the previous one)
                _press = null;
                if (!_pressMoved && input.PointerActive && !input.Cancel)
                {
                    Switch(start.X < UiMetrics.Width / 4f ? -1 : 1);
                    return;
                }
            }
        }

        (_, _zoom, _pan) = Geometry(w, h, zoom, pan);
    }

    private UiPicture? PictureOf(GalleryCg cg)
    {
        (int, int) key = (cg.Group, cg.Index);
        if (!_pictures.TryGetValue(key, out UiPicture? pic))
        {
            (int Archive, int Entry)? found = Ctx.Assets.Tables.FindPicture(cg.Row.Name);
            pic = found is { } f ? new UiPicture(f.Archive, f.Entry) : null;
            _pictures[key] = pic;
        }

        return pic;
    }

    private void DrawPicture(UiPainter p, int group, int pos, float zoom, (float X, float Y) pan, float alpha)
    {
        GalleryCg cg = _panel.Entry(group, pos);
        if (PictureOf(cg) is not { } pic)
        {
            return;
        }

        (float w, float h) = (Math.Max(cg.Row.Width, 1), Math.Max(cg.Row.Height, 1));
        (RectF rect, _, _) = Geometry(w, h, zoom, pan);
        float strip = cg.LanguageStrip ? (int)Ctx.Language : 0;
        p.Picture(pic, new RectF(cg.Row.X, cg.Row.Y + (cg.Row.Height * strip), w, h), rect, alpha);
    }

    /// <inheritdoc/>
    public override void Draw(UiPainter p)
    {
        float a = Fade * Fade;
        using (p.WithAlpha(a))
        {
            p.Cover(0.85f, 0.85f);
            DrawPicture(p, _group, _pos, _zoom, _pan, 1f);
            if (_previous is { } prev && _switchT < Frames)
            {
                DrawPicture(p, prev.Group, prev.Pos, prev.Zoom, prev.Pan, 1f - (_switchT / Frames));
            }
        }

        float ga = _guidePhase switch
        {
            1 => (_guideT / 0.5f) * (_guideT / 0.5f),
            2 => 1f,
            3 => (1f - (_guideT / 0.5f)) * (1f - (_guideT / 0.5f)),
            _ => 0f,
        };
        if (ga > 0f)
        {
            UiWidgets.Guide(p, 9, (1f - ga) * 25f, ga * a, Ctx.Services.DecideIsCross);
        }
    }
}

// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (cGalleryMusic: MUSIC_CTL, music rows, panel, play_track, stop_track, Action::MusicCtl, gal_nav music arms, music clock)
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Ui.Screens;

/// <summary>Text on the gallery screens, positioned in canvas pixels (the layouts of knife's text slots).</summary>
internal static class GalleryText
{
    /// <summary>Draws <paramref name="text"/> with its first line's top-left at (<paramref name="x"/>, <paramref name="y"/>) pixels in a box <paramref name="width"/> pixels wide.</summary>
    /// <param name="p">The painter.</param>
    /// <param name="ctx">The context.</param>
    /// <param name="text">The text (<c>^</c> breaks lines).</param>
    /// <param name="a">Glyph scale across (knife's SIZE / 60).</param>
    /// <param name="b">Glyph scale down.</param>
    /// <param name="x">Left edge in pixels (the box start).</param>
    /// <param name="y">Top edge in pixels.</param>
    /// <param name="width">Box width in pixels.</param>
    /// <param name="alpha">Alpha.</param>
    /// <param name="flags">Block draw flags.</param>
    /// <param name="colour">RGB565 colour.</param>
    /// <param name="leading">Extra space between lines in text units, or -1 for the layout's default.</param>
    public static void Draw(
        UiPainter p, UiContext ctx, string text, double a, double b, float x, float y, float width, float alpha = 1f,
        int flags = BlockDraw.Always | BlockDraw.Outline, int colour = TextStyle.White, int leading = -1)
    {
        TextStyle style = TextStyle.Slot(a, b) with { Width = (int)(width / UiMetrics.UnitX), DrawFlags = flags, Colour = colour };
        if (leading >= 0)
        {
            style = style with { Leading = leading };
        }

        ctx.Text.Draw(p, text, style, x / UiMetrics.UnitX, y / UiMetrics.UnitY, alpha);
    }
}

/// <summary>
/// The music player (docs/formats/gallery.md 6; knife <c>cGalleryMusic</c>): 95 tracks in a list (a track shows its title once its BGM was heard, "???" before),
/// and a panel with play, pause, stop and repeat, previous and next, the elapsed time with a slider, the volume with a slider, and the title and credits of the
/// track that plays. The BGM plays through <see cref="IUiGalleryServices"/>; the UI keeps the player's own clock and ends or repeats a track by the loop points.
/// </summary>
internal sealed class GalleryMusicPanel : GalleryPanel
{
    /// <summary>Rows visible at once is 15; the list scrolls 4,720 px.</summary>
    public const float ScrollRange = 4720f;

    private const float ClipTop = 109f;
    private const float ClipBottom = 1014f;
    private static readonly (float X, float Y)[] Controls = [(1432f, 576f), (1512f, 576f), (1592f, 576f), (1672f, 576f), (1512f, 656f), (1592f, 656f)];

    private readonly ListScroll _scroll = new();
    private readonly UiScrollBar _bar = new();
    private bool _volumeDrag;
    private bool[] _ok = [];
    private Zone _zone = Zone.Row;
    private int _row;
    private int _btn;
    private int _volFrom;
    private int _percent = 100;
    private int _playing = -1;
    private int _last = -1;
    private bool _paused;
    private bool _repeat;
    private float _clock;
    private float? _fade;
    private float _bgOffset;
    private float _pointerX;
    private float _blink;

    private enum Zone
    {
        Row,
        Control,
        Volume,
    }

    /// <inheritdoc/>
    public override GalleryMode Mode => GalleryMode.Music;

    /// <inheritdoc/>
    public override bool Selectable => _ok.Length > 0;

    /// <inheritdoc/>
    public override int GuideIndex => _zone == Zone.Row ? 0 : 10;

    /// <summary>The track that plays (list index), or -1.</summary>
    public int Playing => _playing;

    /// <summary>The track is paused.</summary>
    public bool Paused => _paused;

    /// <summary>Repeat is on.</summary>
    public bool Repeat => _repeat;

    /// <summary>The list cursor row.</summary>
    public int Row => _row;

    /// <summary>The list's scroll position.</summary>
    public float Scroll => _scroll.Pos;

    /// <summary>The scroll bar of the list (its hover and held state).</summary>
    internal UiScrollBar Bar => _bar;

    /// <summary>The player's volume 0..1.</summary>
    public float Volume => _percent / 100f;

    /// <summary>The player's clock in seconds.</summary>
    public float Clock => _clock;

    /// <summary>The unlock state of every track.</summary>
    public IReadOnlyList<bool> Unlocked => _ok;

    /// <summary>The cursor is on a player control: 0 play, 1 pause, 2 stop, 3 repeat, 4 previous, 5 next; -1 when it is on the list or the volume.</summary>
    public int ControlCursor => _zone == Zone.Control ? _btn : -1;

    /// <summary>The cursor is on the volume slider.</summary>
    public bool OnVolume => _zone == Zone.Volume;

    private IReadOnlyList<GalleryTrackRow> Tracks => Host.Model.Tables.Tracks;

    private float Gain => _percent / 100f;

    /// <inheritdoc/>
    public override void Show()
    {
        IGameServices s = Host.Services;

        // entering the player first marks the BGM that plays heard (sub_64F60: tracks up to 0x417), then lists what is unlocked, then stops the BGM at once
        int bgm = s.PlayingBgm;
        if (bgm is > 0 and <= 0x417)
        {
            s.MarkBgmHeard(bgm);
        }

        _ok = Host.Model.TracksUnlocked(s, s);
        s.StopMusic();
        _scroll.Set(0f);
        _bgOffset = 0f;
        _row = 0;
        _btn = 0;
        _zone = Zone.Row;
        _playing = -1;
        _paused = false;
    }

    /// <inheritdoc/>
    public override bool TakeFocus()
    {
        if (_ok.Length == 0)
        {
            return false;
        }

        if (_zone != Zone.Row)
        {
            _zone = Zone.Control;
        }
        else
        {
            Follow(_row, null, animate: false);
        }

        return true;
    }

    /// <inheritdoc/>
    public override void LoseFocus()
    {
    }

    /// <inheritdoc/>
    public override void Leave()
    {
        if (_playing >= 0)
        {
            Stop();
        }
    }

    private void Play(int i)
    {
        _paused = false;
        _fade = null;
        Host.Services.PlayMusic(Tracks[i].Bgm, Gain);
        _playing = i;
        _last = i;
        _clock = 0f;
    }

    private void Stop()
    {
        _paused = false;
        _fade = null;
        _clock = 0f;
        if (_playing >= 0)
        {
            _playing = -1;
            Host.Services.StopMusic();
        }
    }

    // the loop of a track in seconds (the table holds 48 kHz samples; the player counts whole milliseconds)
    private (float Start, float End) Loop(int i) => (Tracks[i].LoopStart / 48 / 1000f, Tracks[i].LoopEnd / 48 / 1000f);

    /// <inheritdoc/>
    public override void Tick()
    {
        _scroll.Step();
        _blink += 1f / 60f;
        float o = -MathF.Truncate(_scroll.Pos);
        if (o >= -200f)
        {
            _bgOffset = o;
        }
        else if (o < -4465f)
        {
            _bgOffset = o + 4265f - 326f;
        }

        if (_playing < 0 || _paused)
        {
            return;
        }

        // sub_1D314: the BGM loops between the loop points; with repeat the clock wraps back by the loop's length, without it the clock holds at the end and the
        // track fades out over 1.5 s from the end (1.5 s before it when the table has no length), then the player stops
        _clock += 1f / 60f;
        (float start, float end) = Loop(_playing);
        if (_repeat)
        {
            if (end > start)
            {
                while (_clock > end)
                {
                    _clock -= end - start;
                }
            }
        }
        else
        {
            float fadeAt = Tracks[_playing].LengthMs == 0 ? end - 1.5f : end;
            if (_fade is null && _clock > fadeAt)
            {
                Host.Services.FadeOutBgm(1.5f);
                _fade = 1.5f;
            }
        }

        if (_fade is { } f)
        {
            _fade = f - (1f / 60f);
            if (_fade <= 0f)
            {
                Stop();
                return;
            }
        }

        _clock = MathF.Min(_clock, end);
    }

    // ---- scrolling --------------------------------------------------------------------------------------------------------------------------------

    private void Follow(int row, int? prev, bool animate)
    {
        float s = _scroll.Target;
        float y = 173f + (row * 59f) - s;
        if ((prev is null || row > prev) && y > 940f)
        {
            _scroll.To((row - 13) * 59f, ScrollRange, animate);
        }
        else if ((prev is null || row < prev) && y < 232f)
        {
            _scroll.To(row == 0 ? 0f : (row - 1) * 59f, ScrollRange, animate);
        }
    }

    /// <inheritdoc/>
    public override void Wheel(int steps)
    {
        if (steps == 0)
        {
            return;
        }

        _scroll.To(_scroll.Target - (steps * 59f * 3f), ScrollRange, animate: true);
    }

    // ---- input ------------------------------------------------------------------------------------------------------------------------------------

    private static readonly RectF VolumeRect = new(1376f, 768f, 448f, 72f);

    /// <summary>A click on the scroll bar's track scrolls 13 rows (15 are in view).</summary>
    private const float PageScroll = 13f * 59f;

    // the volume slider's knob follows the pointer from 1412 to 1776 in steps of 5 % (the keys step it by 1 %)
    private void VolumeFrom(float x)
    {
        float v = Math.Clamp((x - 1412f) / 364f, 0f, 1f);
        int percent = (int)MathF.Round(v * 20f) * 5;
        if (percent != _percent)
        {
            _percent = percent;
            Host.Services.SetMusicVolume(Gain);
        }
    }

    /// <inheritdoc/>
    public override bool StepPointer(in UiInput input)
    {
        ScrollBarUse use = _bar.Step(input, _scroll.Pos / ScrollRange, 610f, true);
        if (use.Fraction is { } dragged)
        {
            _scroll.Over(dragged * ScrollRange, ScrollRange, 6f);
        }
        else if (use.Page != 0)
        {
            _scroll.To(_scroll.Target + (use.Page * PageScroll), ScrollRange, animate: true);
        }

        if (use.Consumed)
        {
            _volumeDrag = false;
            return true;
        }

        if (_volumeDrag)
        {
            if (input.PointerHeld)
            {
                VolumeFrom(input.PointerX);
                return true;
            }

            _volumeDrag = false;
        }

        if (input.Click && input.Over(VolumeRect))
        {
            _volumeDrag = true;
            _pointerX = input.PointerX;
            Set(Zone.Volume, _btn);
            VolumeFrom(input.PointerX);
            return true;
        }

        return false;
    }

    private static RectF RowRect(int i, float scroll)
    {
        float y = 131f + (i * 59f) - MathF.Truncate(scroll);
        float y0 = Math.Clamp(y, ClipTop, ClipBottom);
        float y1 = Math.Clamp(y + 44f, y0, ClipBottom);
        return new RectF(438f, y0, 860f, y1 - y0);
    }

    /// <inheritdoc/>
    public override bool PointerOver(float x, float y)
    {
        _pointerX = x;
        for (int k = 0; k < Controls.Length; k++)
        {
            if (new RectF(Controls[k].X, Controls[k].Y, 88f, 80f).Contains(x, y))
            {
                Set(Zone.Control, k);
                return true;
            }
        }

        if (VolumeRect.Contains(x, y))
        {
            Set(Zone.Volume, _btn);
            return true;
        }

        for (int i = 0; i < _ok.Length; i++)
        {
            if (RowRect(i, _scroll.Pos).Contains(x, y))
            {
                Set(Zone.Row, i);
                return true;
            }
        }

        return false;
    }

    private void Set(Zone zone, int index)
    {
        bool changed = zone != _zone || (zone == Zone.Row && index != _row) || (zone == Zone.Control && index != _btn);
        _zone = zone;
        if (zone == Zone.Row)
        {
            _row = index;
        }
        else if (zone == Zone.Control)
        {
            _btn = index;
        }

        if (changed)
        {
            Host.Sound(UiSound.Cursor);
        }
    }

    /// <inheritdoc/>
    public override void Move(int dx, int dy, bool fresh)
    {
        switch (_zone)
        {
            case Zone.Row:
                MoveRow(dx, dy, fresh);
                break;
            case Zone.Control:
                MoveControl(dx, dy);
                break;
            default:
                if (dx != 0)
                {
                    _percent = Math.Clamp(_percent + dx, 0, 100);
                    Host.Services.SetMusicVolume(Gain);
                }
                else if (dy < 0)
                {
                    Set(Zone.Control, _volFrom);
                }

                break;
        }
    }

    private void MoveRow(int dx, int dy, bool fresh)
    {
        if (dx < 0)
        {
            Host.ToSide();
            return;
        }

        if (dx > 0)
        {
            // to the player panel, on the button it had
            Set(Zone.Control, _btn);
            return;
        }

        int n = _ok.Length;
        int t = _row + dy;
        if (t < 0 || t >= n)
        {
            if (!fresh)
            {
                return;
            }
        }

        int next = ((t % n) + n) % n;
        Follow(next, _row, animate: true);
        Set(Zone.Row, next);
    }

    private void MoveControl(int dx, int dy)
    {
        int k = _btn;
        if (dx < 0 && (k == 0 || k == 4))
        {
            _btn = 0;
            Follow(_row, null, animate: false);
            Set(Zone.Row, _row);
            return;
        }

        int next = k;
        if (dx < 0)
        {
            next = k - 1;
        }
        else if (dx > 0 && (k <= 2 || k == 4))
        {
            next = k + 1;
        }
        else if (dx == 0 && dy < 0 && k >= 4)
        {
            next = k - 3;
        }
        else if (dx == 0 && dy > 0)
        {
            if (k <= 1)
            {
                next = 4;
            }
            else if (k <= 3)
            {
                next = 5;
            }
            else
            {
                // down again from previous / next: the volume slider
                _volFrom = k;
                Set(Zone.Volume, k);
                return;
            }
        }

        if (next != k)
        {
            Set(Zone.Control, next);
        }
    }

    /// <inheritdoc/>
    public override void Activate()
    {
        switch (_zone)
        {
            case Zone.Row:
                // A on a row plays it from the start; on the track already playing (or paused), or on a locked row, the player stops
                Host.Sound(UiSound.Decide);
                if (_playing != _row && _ok[_row])
                {
                    Play(_row);
                }
                else
                {
                    Stop();
                }

                break;
            case Zone.Control:
                Host.Sound(UiSound.Decide);
                Control(_btn);
                break;
            default:
                // a click on the slider sets the volume to the position (a press of A on it does nothing)
                if (_pointerX > 0f)
                {
                    float v = Math.Clamp((_pointerX - 1412f) / 364f, 0f, 1f);
                    _percent = (int)MathF.Round(v * 20f) * 5;
                    Host.Services.SetMusicVolume(Gain);
                }

                break;
        }
    }

    // the search stops short of the track it started from (no replay of a lone unlocked track)
    private int? Step(int from, int d)
    {
        int n = _ok.Length;
        for (int j = 1; j < n; j++)
        {
            int t = ((from + (d * j) % n) + n) % n;
            if (_ok[t])
            {
                return t;
            }
        }

        return null;
    }

    private void Control(int k)
    {
        int cur = _playing >= 0 ? _playing : _last >= 0 ? _last : 0;
        int? target = null;
        switch (k)
        {
            case 0:
                // play: the list cursor's track from the start, whatever plays
                target = _row;
                break;
            case 1 when _playing >= 0:
                _paused = !_paused;
                Host.Services.PauseMusic(_paused);
                break;
            case 2:
                Stop();
                break;
            case 3:
                _repeat = !_repeat;
                break;
            case 4:
                target = Step(cur, -1);
                break;
            case 5:
                target = Step(cur, 1);
                break;
            default:
                break;
        }

        if (target is { } t)
        {
            if (_ok[t])
            {
                Play(t);
            }
            else
            {
                Stop();
            }
        }
    }

    // ---- draw -------------------------------------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override void Draw(UiPainter p, float alpha, bool focused)
    {
        UiContext ctx = Host.Context;
        int listRow = focused && _zone == Zone.Row ? _row : -1;
        using (p.WithClip(new RectF(0, ClipTop, 1920, ClipBottom - ClipTop)))
        {
            p.SpriteAt(GallerySheets.MusicList, 392f, 106f + _bgOffset);
            float scroll = MathF.Truncate(_scroll.Pos);
            for (int i = 0; i < _ok.Length; i++)
            {
                float y = 109f + (i * 59f) - scroll;
                if (y + 64f < ClipTop || y > ClipBottom)
                {
                    continue;
                }

                p.SpriteAt(GallerySheets.MusicLine, 408f, y + 64f);
                string title = ctx.Message(_ok[i] ? Tracks[i].TitleUi : 245).Replace("^", string.Empty, StringComparison.Ordinal);
                GalleryText.Draw(p, ctx, title, 0.44444, 0.49778, 438f, y + 22f, 860f, listRow == i ? 1f : 0.5f);
            }
        }

        UiWidgets.ScrollBar(p, _scroll.Pos / ScrollRange, 610f, 1f, _bar);
        DrawPanel(p, ctx, focused);
    }

    private void DrawPanel(UiPainter p, UiContext ctx, bool focused)
    {
        p.SpriteAt(GallerySheets.MusicPanel, 1368f, 104f);
        if (_playing >= 0)
        {
            DrawCredits(p, ctx, Tracks[_playing]);
        }

        for (int k = 0; k < Controls.Length; k++)
        {
            bool active = (k == 0 && _playing >= 0 && !_paused) || (k == 1 && _paused) || (k == 3 && _repeat);
            bool hover = focused && _zone == Zone.Control && _btn == k;
            float sy = active ? 184f : hover ? 96f : 8f;
            p.SpriteAt(GallerySheets.MusicButtons, new RectF(8f + (96f * k), sy, 88f, 80f), Controls[k].X, Controls[k].Y);
        }

        float length = _playing >= 0 ? Loop(_playing).End : 0f;
        float elapsed = _playing >= 0 ? _clock : 0f;
        int secs = (int)elapsed;
        GalleryText.Draw(p, ctx, $"{secs / 60,2}:{secs % 60:00}", 0.41667, 0.41481, 1548f, 503f, 400f);
        GalleryText.Draw(p, ctx, $"{_percent,3}", 0.41667, 0.41481, 1562f, 878f, 400f);
        float blink = 0.5f + (0.5f * MathF.Abs(MathF.Sin(_blink * 3f)));
        foreach ((float y, float frac) in new[] { (424f, length > 0f ? MathF.Min(elapsed / length, 1f) : 0f), (792f, _percent / 100f) })
        {
            p.SpriteAt(GallerySheets.MusicSlider, 1392f, y);
            float kx = 1376f + (frac * 364f);
            p.SpriteAt(GallerySheets.MusicKnob, new RectF(8, 8, 72, 72), kx, y - 24f);
            if (y > 500f && focused && _zone == Zone.Volume)
            {
                p.Sprite(GallerySheets.MusicKnob, new RectF(168, 8, 72, 72), new RectF(kx, y - 24f, 72, 72), blink, UiBlend.Add);
            }
        }
    }

    // sub_1DAB8: the title and the credit lines (vocals, lyrics, composition, arrangement) centred in the panel, from 260 - 32 * (lines / 2) - 16
    private void DrawCredits(UiPainter p, UiContext ctx, GalleryTrackRow track)
    {
        bool english = ctx.Language == Language.En;
        int[] credits = [track.VocalUi, track.LyricsUi, track.ComposerUi, track.ArrangerUi];
        int lines = 1 + credits.Select((c, k) => (c, k)).Where(t => t.c >= 0).Sum(t => english && t.k > 0 ? 2 : 1);
        float y = 260f - (32f * (lines / 2)) - 16f;
        const float Pitch = 30f;
        void Centred(string text, float top) =>
            GalleryText.Draw(p, ctx, text, 0.4, 0.49778, 1368f, top, 448f, 1f, BlockDraw.Always | BlockDraw.Outline | BlockDraw.Center);

        string[] title = ctx.Message(track.TitleUi).Split('^');
        for (int i = 0; i < title.Length; i++)
        {
            Centred(title[i], y + (i * Pitch));
        }

        y += (Pitch * (title.Length - 1)) + 58f;
        for (int k = 0; k < credits.Length; k++)
        {
            if (credits[k] < 0)
            {
                continue;
            }

            string label = ctx.Message(246 + k);
            string name = ctx.Message(credits[k]);
            string sep = english && k > 0 ? "^" : " ";
            string[] parts = $"{label}{sep}{name}".Split('^');
            for (int n = 0; n < parts.Length; n++)
            {
                Centred(parts[n], y + (n * Pitch));
            }

            y += 32f;
            if (english)
            {
                y += k > 0 ? 32f : 4f;
            }
        }
    }
}

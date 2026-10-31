// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (Screen::Flowchart: open_flow_at, flow_follow, flow_scroll_*, flow_press, flow_keep_cursor, draw of the chart, the cursor and the scene panel)
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Ui.Screens;

/// <summary>
/// The flowchart (docs/formats/gallery.md 5; knife <c>cFlowchart</c> and <c>cFlowchartScene</c>): the node chart of the scenes the progress has opened, scrolling
/// in a 1920 x 1022 view, a cursor over its nodes, and on the right the card of the selected scene (its preview picture, title and synopsis). Used from the
/// gallery (every finished node can be replayed: <see cref="IUiGalleryServices.ReplayScene"/>) and from the system menu in game (a node can be jumped to: the
/// playthrough is rewound to it, <see cref="IUiGalleryServices.FlowchartJump"/>).
/// </summary>
public sealed class FlowchartScreen : UiScreen
{
    private const float ScrollFrames = ListScroll.Frames;
    private const float PageScroll = 840f;          // a click on the scroll bar's track: five wheel steps
    private const float PanThreshold = 16f;         // canvas pixels the pointer must travel with the button down before a press becomes a pan (knife DRAG_MIN, 7 units)
    private readonly UiScrollBar _bar = new();
    private (float X, float Y, float Scroll, int? Node)? _press;
    private bool _panning;
    private readonly bool _gallery;
    private readonly int _openNode;
    private readonly ListScroll _scroll = new();
    private FlowchartChart _chart = null!;
    private ChartCell _cell;
    private float _curY;
    private float _ref;
    private bool _manual;
    private int _shown = -1;
    private int _old = -1;
    private float _since = float.PositiveInfinity;
    private bool _instant;
    private bool _replayed;

    /// <summary>Creates the chart.</summary>
    /// <param name="gallery">Opened from the title's gallery (no current playthrough: finished nodes replay); false: from the system menu in game (nodes of this playthrough jump).</param>
    /// <param name="node">The node the cursor starts on when opened from the gallery (-1 for the first record); in game the current node F110 is used.</param>
    public FlowchartScreen(bool gallery, int node = -1)
    {
        _gallery = gallery;
        _openNode = node;
        StartHidden();
    }

    /// <summary>Opened from the gallery.</summary>
    public bool Gallery => _gallery;

    /// <summary>The chart.</summary>
    public FlowchartChart Chart => _chart;

    /// <summary>The cursor cell.</summary>
    public ChartCell Cell => _cell;

    /// <summary>The shown scroll.</summary>
    public float Scroll => _scroll.Pos;

    /// <summary>Scrolls the chart to <paramref name="v"/> at once (the tests and the screenshot tool).</summary>
    internal void ScrollNow(float v)
    {
        _ref = MathF.Truncate(Math.Clamp(v, 0f, _chart.ScrollMax));
        _scroll.Set(_ref);
    }

    /// <summary>The selectable node under the cursor (an index into <see cref="FlowchartChart.Nodes"/>), or -1.</summary>
    public int Selected => _chart.NodeAt(_cell);

    /// <inheritdoc/>
    public override bool Opaque => true;

    /// <inheritdoc/>
    protected override float FadeInStep => 1f / 30f;

    /// <inheritdoc/>
    protected override float FadeOutStep => _instant ? 1f : 1f / 30f;

    /// <summary>Whether the flowchart can be opened: the gallery needs scene 1 started, in game scene 1 entered in this playthrough and the current node outside the ending list.</summary>
    public static bool Available(UiContext ctx, bool gallery)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        IGameServices s = ctx.Services;
        IUiGalleryTables? tables = ctx.Assets.Gallery;
        if (tables is null)
        {
            return false;
        }

        if (gallery)
        {
            return s.SceneSeen(1);
        }

        return s.RouteScene(1) && !GalleryModel.For(tables).IsEndingNode(s.CurrentNode);
    }

    /// <inheritdoc/>
    public override void OnOpened()
    {
        IUiGalleryTables tables = Ctx.Assets.Gallery ?? throw new InvalidOperationException("the data has no gallery tables");
        _chart = FlowchartChart.Build(tables, Ctx.Language, Ctx.Services, _gallery);
        _scroll.Set(0f);
        _ref = 0f;
        int id = _gallery ? _openNode : Ctx.Services.CurrentNode;
        var placed = new List<ChartCell>();
        _cell = _chart.OpenCursor(id, placed.Add);
        foreach (ChartCell c in placed)
        {
            _cell = c;
            Place(animate: false);
        }

        _shown = Selected;
    }

    /// <inheritdoc/>
    public override void OnClosed()
    {
        if (_gallery && !_replayed && Ctx.Gallery.Previous != (int)GalleryMode.Music)
        {
            // back in the gallery in a mode other than the music player, the title's BGM plays (sub_7624)
            Ctx.Services.PlayBgm(Ctx.Services.Global(2) == 5 ? 72 : 2);
        }
    }

    // sub_10BA4 + sub_10D5C: the cursor sprite moves onto the cursor's node and the view follows it
    private void Place(bool animate)
    {
        int n = _chart.NodeAt(_cell);
        if (n >= 0)
        {
            _curY = _chart.Nodes[n].Y;
        }

        Follow(animate);
    }

    // sub_10D5C: when the cursor node is above y 2 the row before it goes to y 4; below y 966 the row after it goes to y 966
    private void Follow(bool animate)
    {
        if (_manual || _chart.RowY.Count == 0 || _cell.Row < 0)
        {
            return;
        }

        float y = _curY - _ref;
        float target;
        if (y < 2f)
        {
            target = _chart.RowY[Math.Max(_cell.Row, 1) - 1] - 4f;
        }
        else if (y > 966f)
        {
            target = _chart.RowY[Math.Min(_cell.Row + 1, _chart.RowY.Count - 1)] - 966f;
        }
        else
        {
            return;
        }

        _ref = MathF.Truncate(target);
        _scroll.To(_ref, _chart.ScrollMax, animate);
    }

    private void SetCell(ChartCell cell)
    {
        _cell = cell;
        Place(animate: true);
        int n = _chart.NodeAt(cell);
        if (n != _shown)
        {
            _old = _shown;
            _shown = n;
            _since = 0f;
        }
    }

    /// <inheritdoc/>
    public override void Step(in UiInput input)
    {
        _scroll.Step();
        _since++;
        if (Closing || Fade < 1f)
        {
            return;
        }

        if (_manual)
        {
            StepManual();
        }

        if (input.Cancel)
        {
            Ctx.Sound(UiSound.Buzzer);
            Close();
            return;
        }

        if (input.Wheel != 0 && _chart.ScrollMax > 0f)
        {
            _ref = MathF.Truncate(Math.Clamp(_scroll.Target - (input.Wheel * 168f), 0f, _chart.ScrollMax));
            _scroll.To(_ref, _chart.ScrollMax, animate: true);
            _manual = true;
        }

        // the mouse (package K12): the scroll bar, a drag on the chart pans it, a click on a node (the button released over the node it went down on, without
        // a pan in between) presses it
        if (StepPointer(input))
        {
            return;
        }

        ChartCell? next = null;
        if (input.Left)
        {
            next = _chart.MoveLeft(_cell);
        }
        else if (input.Right)
        {
            next = _chart.MoveRight(_cell);
        }
        else if (input.Up)
        {
            next = _chart.MoveUp(_cell);
        }
        else if (input.Down)
        {
            next = _chart.MoveDown(_cell);
        }

        if (next is { } to)
        {
            _manual = false;
            Ctx.Sound(UiSound.Cursor);
            SetCell(to);
            return;
        }

        if (input.Decide && !input.Click)
        {
            Press();
        }
    }

    private bool StepPointer(in UiInput input)
    {
        float max = _chart.ScrollMax;
        ScrollBarUse use = _bar.Step(input, max > 0f ? _scroll.Pos / max : 0f, 610f, max > 0f);
        if (use.Fraction is { } dragged)
        {
            _ref = MathF.Truncate(dragged * max);
            _scroll.Over(_ref, max, 6f);
            _manual = true;
        }
        else if (use.Page != 0)
        {
            _ref = MathF.Truncate(Math.Clamp(_scroll.Target + (use.Page * PageScroll), 0f, max));
            _scroll.To(_ref, max, animate: true);
            _manual = true;
        }

        if (use.Consumed)
        {
            _press = null;
            _panning = false;
            return false;
        }

        if (input.Click && input.PointerActive && input.PointerY < FlowchartChart.ViewHeight)
        {
            _press = (input.PointerX, input.PointerY, _scroll.Target, NodeAtPointer(input));
            _panning = false;
        }

        if (_press is { } pr)
        {
            if (input.PointerHeld)
            {
                if (!_panning && max > 0f && MathF.Abs(input.PointerX - pr.X) + MathF.Abs(input.PointerY - pr.Y) >= PanThreshold)
                {
                    _panning = true;
                }

                if (_panning)
                {
                    _ref = MathF.Truncate(Math.Clamp(pr.Scroll - (input.PointerY - pr.Y), 0f, max));
                    _scroll.Over(_ref, max, 0f);
                    _manual = true;
                }

                return false;
            }

            _press = null;
            bool pan = _panning;
            _panning = false;
            if (!pan && pr.Node is { } node && NodeAtPointer(input) == node && _chart.CellOf(node) is { } cell)
            {
                SetCell(cell);
                Press();
                return true;
            }

            return false;
        }

        if (input.PointerMoved && NodeAtPointer(input) is { } hit && _chart.CellOf(hit) is { } c && c != _cell)
        {
            Ctx.Sound(UiSound.Cursor);
            SetCell(c);
        }

        return false;
    }

    // sub_12D1C: during a manual scroll the cursor moves row by row until its node is inside y 4..994 of the view at the shown scroll
    private void StepManual()
    {
        if (_chart.ScrollMax <= 0f)
        {
            _manual = false;
            return;
        }

        if (!_scroll.Moving)
        {
            _manual = false;
        }

        float at = _ref;
        if (_chart.KeepInView(_cell, i => _chart.Nodes[i].Y - at) is { } moved)
        {
            _cell = moved;
            int n = _chart.NodeAt(moved);
            if (n >= 0)
            {
                _curY = _chart.Nodes[n].Y;
            }

            Ctx.Sound(UiSound.Cursor);
            if (n != _shown)
            {
                _old = _shown;
                _shown = n;
                _since = 0f;
            }
        }
    }

    private int? NodeAtPointer(in UiInput input)
    {
        if (!input.PointerActive || input.PointerY >= FlowchartChart.ViewHeight)
        {
            return null;
        }

        for (int i = 0; i < _chart.Nodes.Count; i++)
        {
            ChartNode n = _chart.Nodes[i];
            if (new RectF(n.X, n.Y - _scroll.Pos, FlowchartChart.NodeW, FlowchartChart.NodeH).Contains(input.PointerX, input.PointerY))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// A on a cell (sub_11418, sound 1): in game the rewind dialog for its record; in the gallery the record replays at once, a filler record (scene -1) closing the
    /// gallery to the title.
    /// </summary>
    private void Press()
    {
        if (_chart.RecordAt(_cell) is not { } rec)
        {
            return;
        }

        Ctx.Sound(UiSound.Decide);
        if (_gallery)
        {
            _replayed = rec.ScriptScene >= 0;
            _instant = true;
            Root.Find<GalleryScreen>()?.ReplayFromChart(rec.ScriptScene, rec.Node);
            Close();
            return;
        }

        // "rewind to this scene?" (ui 1498, Yes first)
        Root.Push(new ConfirmDialog(1498, false, yes =>
        {
            if (!yes)
            {
                return;
            }

            Ctx.Services.CommitSystemData();
            if (Ctx.Services.FlowchartJump(rec.Column, rec.Node, rec.ScriptScene))
            {
                Root.Clear();
            }
            else
            {
                Close();
            }
        }));
    }

    // ---- draw -------------------------------------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override void Draw(UiPainter p)
    {
        if (_gallery)
        {
            p.Cover(1f);
        }

        Ctx.Backdrop.Draw(p);
        float s = Fade;
        using (p.WithAlpha(s * s))
        {
            DrawChartFrame(p);
        }

        int guide = _chart.ScrollMax > 0f ? 0 : 10;
        UiWidgets.Guide(p, guide, 0f, s, Ctx.Services.DecideIsCross);
    }

    private void DrawChartFrame(UiPainter p)
    {
        // flow_bg has the chart's scissor too; flow_infobase is the card of the selected scene
        p.Sprite(GallerySheets.ChartBackground, new RectF(0, 0, 1920, FlowchartChart.ViewHeight), new RectF(0, 0, 1920, FlowchartChart.ViewHeight));
        p.SpriteAt(GallerySheets.ChartCard, 1352f, 96f);
        float scroll = _scroll.Pos;
        using (p.WithClip(new RectF(0, 0, 1920, FlowchartChart.ViewHeight)))
        {
            foreach (ChartSprite sp in _chart.Sprites)
            {
                RectF d = sp.Dst;
                if (d.Y - scroll > FlowchartChart.ViewHeight || d.Y + d.H - scroll < 0f)
                {
                    continue;
                }

                p.Sprite(GallerySheets.Of(sp.Sheet), sp.Src, new RectF(d.X, d.Y - scroll, d.W, d.H));
            }

            if (!Closing)
            {
                DrawTitles(p, scroll);
            }

            DrawCursor(p, scroll);
        }

        if (_chart.ScrollMax > 0f)
        {
            UiWidgets.ScrollBar(p, scroll / _chart.ScrollMax, 610f, 1f, _bar);
        }

        DrawCard(p);
    }

    // sub_11EB4: the phase titles are pinned at y 8 while their section scrolls past, pushed up by the upper divider of the next section change
    private void DrawTitles(UiPainter p, float scroll)
    {
        float off = -MathF.Truncate(scroll);
        for (int phase = 0; phase < _chart.Titles.Count; phase++)
        {
            IReadOnlyList<ChartSprite> ts = _chart.Titles[phase];
            if (ts.Count == 0)
            {
                continue;
            }

            float o = off;
            if (ts[0].Dst.Y + o < 8f)
            {
                o = 8f - ts[0].Dst.Y;
            }

            float bottom = ts[^1].Dst.Y + o + 32f;
            if (phase < _chart.Limits.Count && _chart.Limits[phase] + off < bottom)
            {
                o -= bottom - (_chart.Limits[phase] + off);
            }

            foreach (ChartSprite t in ts)
            {
                p.Sprite(GallerySheets.Of(t.Sheet), t.Src, new RectF(t.Dst.X, t.Dst.Y + o, t.Dst.W, t.Dst.H));
            }
        }
    }

    private void DrawCursor(UiPainter p, float scroll)
    {
        int n = Selected;
        if (n < 0)
        {
            return;
        }

        ChartNode node = _chart.Nodes[n];
        var dst = new RectF(node.X, node.Y - scroll, FlowchartChart.NodeW, FlowchartChart.NodeH);
        p.Sprite(GallerySheets.ChartCursor, new RectF(8, 8, 104, 64), dst);

        // a new node: the glow runs down from 1 and is shown for 15 frames
        float a = 1f - (2f * _since / 30f);
        if (a > 0f)
        {
            p.Sprite(GallerySheets.ChartCursor, new RectF(120, 8, 104, 64), dst, a);
        }
    }

    // sub_16E00: on a new node the old preview, title and synopsis fade out and the new ones in over 5 frames (ease-out quad); the new preview shows at once
    private void DrawCard(UiPainter p)
    {
        float t = Math.Clamp(_since / 5f, 0f, 1f);
        float e = t * (2f - t);
        if (_old >= 0 && e < 1f)
        {
            DrawScene(p, _old, 1f - e, 1f - e);
        }

        if (_shown >= 0)
        {
            DrawScene(p, _shown, e, 1f);
            p.SpriteAt(GallerySheets.ChartTag, 1672f, 120f);
        }
    }

    private void DrawScene(UiPainter p, int node, float textAlpha, float previewAlpha)
    {
        ChartNode n = _chart.Nodes[node];
        UiContext ctx = Ctx;

        // the four scenes whose previews stack one 243-high band per language
        int lang = (int)ctx.Language;
        bool stacked = n.Preview is 107 or 114 or 188 or 481;
        float y0 = stacked ? lang * 243f : 0f;
        p.Sprite(GallerySheets.ChartPreview(n.Preview), new RectF(0, y0, 430, 243), new RectF(1380, 120, 430, 243), previewAlpha);

        CardText card = CardTextOf(ctx.Language);
        string title = ctx.Message(n.TitleUi).Replace("^", string.Empty, StringComparison.Ordinal);
        string synopsis = ctx.Message(n.SynopsisUi);
        ctx.Text.Draw(p, title, TitleStyle(ctx.Text, card, title), card.X / UiMetrics.UnitX, card.TitleY / UiMetrics.UnitY, textAlpha);
        ctx.Text.Draw(p, synopsis, card.Synopsis, card.X / UiMetrics.UnitX, card.SynopsisY / UiMetrics.UnitY, textAlpha);
    }

    // ---- the scene card's text (package R1) --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The bottom of the scene card's white area: flow_infobase (488 x 496 at (1352, 96) in every language of the PS4; the Switch's English card is 64 px taller) is
    /// white from y 378 to 567 under the preview and fades into the chart below that (measured on the rendered card). The Traditional Chinese synopses, laid out at
    /// knife's numbers, end at 566.
    /// </summary>
    internal const float CardTextBottom = 567f;

    /// <summary>
    /// The right limit of the card's text ink: the card's white area spans x 1379..1810 on the rendered card and the text starts 11 px inside it, so its ink keeps the
    /// same 11 px from the right edge.
    /// </summary>
    internal const float CardTextRight = 1799f;

    /// <summary>The text of the scene card: where it starts in canvas pixels and the styles of its title and its synopsis.</summary>
    /// <param name="X">The left edge of both texts.</param>
    /// <param name="Title">The title's style (one line; <see cref="TitleStyle"/> fits a long title).</param>
    /// <param name="TitleY">The title's top.</param>
    /// <param name="TitleLimit">Where the title's ink has to end (<see cref="CardTextRight"/> for English; the other languages keep knife's layout, whose widest Japanese title ends 13 px past the white area).</param>
    /// <param name="Synopsis">The synopsis' style (<c>^</c> breaks lines, the box wraps the rest).</param>
    /// <param name="SynopsisY">The synopsis' top.</param>
    internal readonly record struct CardText(float X, TextStyle Title, float TitleY, float TitleLimit, TextStyle Synopsis, float SynopsisY);

    /// <summary>
    /// The card's text (sub_16E00: two text widgets at (1392, 404) and (1392, 452), 0.4 x 0.41481 of the base glyph, the title in a 200-unit box, the synopsis in 150;
    /// overseas English at x 1390 with a 136-unit synopsis box). The PS4 card is 496 px tall in every language while the Switch's English card is 560, and at those numbers
    /// 226 of the 495 English synopses ran below the card and three titles wrapped onto the synopsis: English takes 0.75 of the glyph scale and starts the synopsis at
    /// 436 (the title's line ends by 425), with which every synopsis ends by <see cref="CardTextBottom"/> (the longest, six lines, at 565), its ink stays inside the white
    /// area and no title wraps; the other languages keep knife's numbers, which fit.
    /// </summary>
    internal static CardText CardTextOf(Language language)
    {
        bool english = language == Language.En;
        double k = english ? 0.75 : 1.0;
        TextStyle title = TextStyle.Slot(0.4 * k, 0.41481 * k) with { Width = 200, DrawFlags = BlockDraw.Always, Colour = TextStyle.Rgb565(66, 65, 132) };
        TextStyle synopsis = title with { Width = english ? 136 : 150, Colour = TextStyle.Black, Leading = 0 };
        return new CardText(english ? 1390f : 1392f, title, 404f, english ? CardTextRight : float.PositiveInfinity, synopsis, english ? 436f : 452f);
    }

    /// <summary>
    /// The style <paramref name="title"/> is drawn with: the card's, taken down so the title's ink ends by the card's <see cref="CardText.TitleLimit"/> when it would
    /// run past it (the title box is 600 px wide on a 488 px card: at 0.75 the English "...The Back Alleys of the Shopping District" still ends 54 px past the limit).
    /// </summary>
    internal static TextStyle TitleStyle(UiText text, in CardText card, string title)
    {
        ArgumentNullException.ThrowIfNull(text);
        float right = text.InkSpan(title, card.Title).Right;
        if (card.X + right <= card.TitleLimit)
        {
            return card.Title;
        }

        double k = (card.TitleLimit - card.X) / right;
        return card.Title with { Sx = card.Title.Sx * k, Sy = card.Title.Sy * k };
    }
}

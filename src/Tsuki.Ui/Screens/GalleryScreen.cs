// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (cGallery hub: hub_items, draw_hub_bg, draw_hub_menu, gal_nav, enter_mode, gal_replay, gallery_back, guide_for, gal_guide_tick)
using Tsuki.Engine.Contracts;

namespace Tsuki.Ui.Screens;

/// <summary>
/// One mode of the gallery (the content next to the mode buttons): CG grid, music player, movie cards or ending list. The hub (<see cref="GalleryScreen"/>)
/// owns the focus between the buttons and the content and hands the content the input while it has the focus.
/// </summary>
internal abstract class GalleryPanel
{
    /// <summary>The hub the panel sits in.</summary>
    protected GalleryScreen Host { get; private set; } = null!;

    /// <summary>The mode.</summary>
    public abstract GalleryMode Mode { get; }

    /// <summary>The guide index while the content has the focus (<see cref="UiWidgets.Guide"/>).</summary>
    public virtual int GuideIndex => 10;

    /// <summary>True when the content has something to select (its first entry takes the cursor).</summary>
    public abstract bool Selectable { get; }

    /// <summary>Binds the panel to its hub.</summary>
    public void Attach(GalleryScreen host) => Host = host;

    /// <summary>The mode came on screen: its list starts at the top.</summary>
    public abstract void Show();

    /// <summary>The content takes the focus; false when there is nothing to select.</summary>
    public abstract bool TakeFocus();

    /// <summary>The content gives the focus up (the cursor row stays where the mode keeps it).</summary>
    public abstract void LoseFocus();

    /// <summary>The mode goes away (a track stops).</summary>
    public virtual void Leave()
    {
    }

    /// <summary>One 60 Hz frame, with or without the focus.</summary>
    public abstract void Tick();

    /// <summary>The pointer moved or clicked at (x, y): puts the cursor on the entry there and returns true, or false when no entry is there.</summary>
    public abstract bool PointerOver(float x, float y);

    /// <summary>
    /// The pointer's own controls of the panel (package K12): the scroll bar's thumb and track, a slider held by the left button. Called every frame before the hub
    /// looks at the pointer; true when the panel used it (the hub then does not hover or click what lies under the pointer).
    /// </summary>
    public virtual bool StepPointer(in UiInput input) => false;

    /// <summary>An arrow (<paramref name="fresh"/>: a new press, not a repeat).</summary>
    public abstract void Move(int dx, int dy, bool fresh);

    /// <summary>Decide on the entry under the cursor.</summary>
    public abstract void Activate();

    /// <summary>Cancel inside the content; false lets the hub take the focus back to the buttons.</summary>
    public virtual bool Cancel() => false;

    /// <summary>The wheel (steps, positive = up).</summary>
    public virtual void Wheel(int steps)
    {
    }

    /// <summary>Draws the content (the hub applies the content alpha).</summary>
    public abstract void Draw(UiPainter p, float alpha, bool focused);
}

/// <summary>
/// The gallery (docs/formats/gallery.md 1; knife <c>cGallery</c>): the title's GALLERY screen, a hub with six buttons (CG Gallery, Music Player, Flowchart, Movie,
/// Ending List, Title) on the left and the content of the chosen mode on the right. The CG viewer, the flowchart, the movie player and the confirmations are
/// screens pushed over it. The hub fades over the title's backdrop; closing returns to the title, a replay (an ending, a flowchart node) closes it and asks the game
/// to run the scene through <see cref="IUiGalleryServices.ReplayScene"/>, after which the host reopens it with <see cref="UiRoot.ReturnToGallery"/>.
/// </summary>
public sealed class GalleryScreen : UiScreen
{
    private const float ContentFrames = 30f;
    private readonly bool _afterReplay;
    private readonly RectF[] _buttons = new RectF[6];
    private GalleryPanel _panel = null!;
    private GalleryPanel? _next;
    private bool _switching;
    private bool _nextFocus;
    private float _switch;
    private bool _switchingOut;
    private bool _side = true;
    private int _tab;
    private (int Scene, int Node)? _replay;
    private float _blink;
    private int _guide = 10;
    private int _guideNext = 10;
    private int _guideDir;
    private float _guideT;

    /// <summary>Creates the gallery.</summary>
    /// <param name="afterReplay">A replay has just ended: the gallery opens on the mode and the entry it started from, with the content focused.</param>
    public GalleryScreen(bool afterReplay = false)
    {
        _afterReplay = afterReplay;
        StartHidden();
        for (int i = 0; i < _buttons.Length; i++)
        {
            _buttons[i] = new RectF(80, 160 + (i * 80), 256, 96);
        }
    }

    /// <inheritdoc/>
    public override bool Opaque => true;

    /// <inheritdoc/>
    protected override float FadeInStep => 0.04f;

    /// <inheritdoc/>
    protected override float FadeOutStep => 0.04f;

    /// <summary>The mode shown.</summary>
    public GalleryMode Mode => _panel.Mode;

    /// <summary>The focus is on the mode buttons (otherwise on the content).</summary>
    public bool SideFocus => _side;

    /// <summary>The button under the cursor (0 CG, 1 Music, 2 Flowchart, 3 Movie, 4 Ending, 5 Title).</summary>
    public int Tab => _tab;

    /// <summary>True while the content changes between two modes.</summary>
    public bool Switching => _switching;

    /// <summary>The panel shown (tests read its state through the <see cref="GalleryScreen"/> members they know).</summary>
    internal GalleryPanel Panel => _panel;

    /// <summary>The model of the unlock rules.</summary>
    internal GalleryModel Model { get; private set; } = null!;

    /// <summary>The context (the panels draw text through it).</summary>
    internal UiContext Context => Ctx;

    /// <summary>The game services.</summary>
    internal IGameServices Services => Ctx.Services;

    /// <inheritdoc/>
    public override void OnOpened()
    {
        Model = GalleryModel.For(Ctx.Assets.Gallery ?? throw new InvalidOperationException("the data has no gallery tables"));
        Ctx.Backdrop.Fade(0.04f);
        GalleryMemory memory = Ctx.Gallery;
        int first = memory.Mode;
        int node = _afterReplay ? memory.Node : -1;
        memory.Mode = -1;
        GalleryMode start = first is 1 or 3 or 4 ? (GalleryMode)first : GalleryMode.Cg;
        _panel = Create(start);
        _panel.Show();
        _tab = (int)start;
        if (start == GalleryMode.Endings && _afterReplay && node >= 0 && _panel is GalleryEndingsPanel endings)
        {
            endings.Focus(node);
        }

        if (_afterReplay && node >= 0 && first != 2 && _panel.TakeFocus())
        {
            _side = false;
        }

        memory.Node = -1;
        if (first == 2)
        {
            // the chart was the mode the gallery closed with: it opens at once over the hub (on the node a replay started from)
            Root.Push(new FlowchartScreen(gallery: true, _afterReplay ? node : -1));
        }
    }

    /// <inheritdoc/>
    public override void OnClosed()
    {
        _panel.Leave();
        Ctx.Backdrop.Fade(0f);
        if (_replay is { } r)
        {
            _replay = null;
            Ctx.Services.ReplayScene(r.Scene, r.Node);
            Root.Clear();
            return;
        }

        Root.Find<TitleScreen>()?.ReturnFromGallery();
    }

    /// <inheritdoc/>
    public override void Close()
    {
        if (Closing)
        {
            return;
        }

        Ctx.Gallery.Mode = (int)_panel.Mode;
        _panel.Leave();
        Ctx.Backdrop.Fade(-0.04f);
        base.Close();
    }

    private GalleryPanel Create(GalleryMode mode)
    {
        GalleryPanel p = mode switch
        {
            GalleryMode.Music => new GalleryMusicPanel(),
            GalleryMode.Movies => new GalleryMoviesPanel(),
            GalleryMode.Endings => new GalleryEndingsPanel(),
            _ => new GalleryCgPanel(),
        };
        p.Attach(this);
        return p;
    }

    // ---- services the panels use ------------------------------------------------------------------------------------------------------------------

    internal void Sound(UiSound sound) => Ctx.Sound(sound);

    /// <summary>The content gives the focus back to the mode buttons.</summary>
    internal void ToSide()
    {
        _panel.LoseFocus();
        _side = true;
        _tab = (int)_panel.Mode;
    }

    /// <summary>Opens the CG viewer on picture <paramref name="pos"/> of group <paramref name="group"/>.</summary>
    internal void OpenViewer(GalleryCgPanel cg, int group, int pos) => Root.Push(new GalleryCgViewer(cg, group, pos));

    /// <summary>Asks to replay a scene: the gallery fades out, then the game runs it (docs/formats/gallery.md 3.2); a negative scene only closes the gallery.</summary>
    internal void Replay(int scene, int node)
    {
        Ctx.Gallery.Node = node >= 0 ? node : scene;
        if (scene >= 0)
        {
            _replay = (scene, node);
        }

        Close();
    }

    /// <summary>A replay picked in the flowchart: the chart is closed, the hub remembers the chart as its mode.</summary>
    internal void ReplayFromChart(int scene, int node)
    {
        Ctx.Gallery.Mode = (int)GalleryMode.Flowchart;
        Ctx.Gallery.Node = node >= 0 ? node : scene;
        if (scene >= 0)
        {
            _replay = (scene, node);
        }

        _panel.Leave();
        Ctx.Backdrop.Fade(-0.04f);
        base.Close();
    }

    // ---- frame ------------------------------------------------------------------------------------------------------------------------------------

    private bool Busy => Fade < 1f || Closing || _switching;

    /// <summary>The guide index of the current focus (knife <c>guide_for</c>).</summary>
    private int WantedGuide => _side ? 10 : _panel.GuideIndex;

    /// <inheritdoc/>
    public override void Step(in UiInput input)
    {
        _blink += 1f / 60f;
        StepGuide();
        _panel.Tick();
        if (_switching)
        {
            StepSwitch();
            return;
        }

        if (Busy)
        {
            return;
        }

        if (_panel.StepPointer(input))
        {
            _side = false;
            return;
        }

        int button = ButtonAt(input);
        if (input.PointerMoved)
        {
            if (button >= 0)
            {
                _tab = button;
                _side = true;
            }
            else if (_panel.PointerOver(input.PointerX, input.PointerY))
            {
                _side = false;
            }
        }

        if (input.Click)
        {
            if (button >= 0)
            {
                _tab = button;
                _side = true;
                Act(button);
                return;
            }

            if (_panel.PointerOver(input.PointerX, input.PointerY))
            {
                _side = false;
                _panel.Activate();
                return;
            }
        }

        if (input.Wheel != 0)
        {
            _panel.Wheel(input.Wheel);
        }

        int dx = (input.Left ? -1 : 0) + (input.Right ? 1 : 0);
        int dy = (input.Up ? -1 : 0) + (input.Down ? 1 : 0);
        bool fresh = input.UpPressed || input.DownPressed || input.LeftPressed || input.RightPressed;
        if (_side)
        {
            StepSide(input, dx, dy, fresh);
            return;
        }

        if (input.Cancel)
        {
            Sound(UiSound.Buzzer);
            if (!_panel.Cancel())
            {
                ToSide();
            }

            return;
        }

        if (dx != 0 || dy != 0)
        {
            _panel.Move(dx, dy, fresh);
        }
        else if (input.Decide && !input.Click)
        {
            _panel.Activate();
        }
    }

    private int ButtonAt(in UiInput input)
    {
        if (!input.PointerActive)
        {
            return -1;
        }

        for (int i = 0; i < _buttons.Length; i++)
        {
            if (_buttons[i].Contains(input.PointerX, input.PointerY))
            {
                return i;
            }
        }

        return -1;
    }

    private void StepSide(in UiInput input, int dx, int dy, bool fresh)
    {
        if (input.Cancel)
        {
            Sound(UiSound.Buzzer);
            Close();
            return;
        }

        int before = _tab;
        if (dy != 0)
        {
            // wraps only on a fresh press
            int t = _tab + dy;
            if (t is >= 0 and < 6 || fresh)
            {
                _tab = ((t % 6) + 6) % 6;
            }
        }
        else if (dx > 0)
        {
            _tab = (int)_panel.Mode;
            if (_panel.TakeFocus())
            {
                _side = false;
            }

            return;
        }
        else if (input.Decide)
        {
            Act(_tab);
            return;
        }

        if (_tab != before)
        {
            Sound(UiSound.Cursor);
        }
    }

    /// <summary>A mode button was decided.</summary>
    private void Act(int tab)
    {
        switch (tab)
        {
            case 5:
                Sound(UiSound.Buzzer);
                Close();
                break;
            case 2:
                OpenChart();
                break;
            default:
                if (tab == (int)_panel.Mode)
                {
                    if (_panel.TakeFocus())
                    {
                        _side = false;
                        Sound(UiSound.Decide);
                    }
                }
                else
                {
                    Sound(UiSound.Decide);
                    BeginSwitch((GalleryMode)tab);
                }

                break;
        }
    }

    private void OpenChart()
    {
        // the chart needs the first scene started (gallery.md 2.3); else message 1497
        if (!Services.SceneSeen(1))
        {
            Sound(UiSound.Cursor);
            Root.Push(new MessagePopup(Ctx.Message(1497), 480f));
            return;
        }

        Sound(UiSound.Decide);
        Ctx.Gallery.Previous = (int)_panel.Mode;
        _panel.Leave();
        Root.Push(new FlowchartScreen(gallery: true, -1));
    }

    private void BeginSwitch(GalleryMode mode)
    {
        _next = Create(mode);
        _nextFocus = true;
        _switching = true;
        _switchingOut = true;
        _switch = 0f;
    }

    private void StepSwitch()
    {
        _switch++;
        if (_switchingOut)
        {
            if (_switch >= ContentFrames)
            {
                _panel.Leave();
                _panel = _next!;
                _next = null;
                _panel.Show();
                _side = true;
                _tab = (int)_panel.Mode;
                _switchingOut = false;
                _switch = 0f;
            }

            return;
        }

        if (_switch >= ContentFrames)
        {
            _switching = false;
            _switch = 0f;
            if (_nextFocus && _panel.TakeFocus())
            {
                _side = false;
            }
        }
    }

    private float ContentAlpha
    {
        get
        {
            if (!_switching)
            {
                return 1f;
            }

            float u = Math.Clamp(_switch / ContentFrames, 0f, 1f);
            return _switchingOut ? (1f - u) * (1f - u) : u * u;
        }
    }

    // ---- the guide (cGalleryGuide: a new index fades the shown one out over half a second, then the new one in) -----------------------------------

    private void StepGuide()
    {
        int want = WantedGuide;
        if (_guideDir < 0)
        {
            _guideNext = want;
        }
        else if (want != _guide)
        {
            _guideDir = -1;
            _guideT = 0f;
            _guideNext = want;
        }

        if (_guideDir != 0)
        {
            _guideT += 1f / 30f;
            if (_guideT >= 1f)
            {
                _guideT = 0f;
                if (_guideDir < 0)
                {
                    _guide = _guideNext;
                    _guideDir = 1;
                }
                else
                {
                    _guideDir = 0;
                }
            }
        }
    }

    private float GuideAlpha => _guideDir switch
    {
        < 0 => (1f - _guideT) * (1f - _guideT),
        > 0 => _guideT * _guideT,
        _ => 1f,
    };

    // ---- draw -------------------------------------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override void Draw(UiPainter p)
    {
        p.Cover(1f);
        Ctx.Backdrop.Draw(p);
        DrawFrame(p, Fade);
        using (p.WithAlpha(Fade * ContentAlpha))
        {
            DrawBanner(p, _panel.Mode);
            _panel.Draw(p, 1f, !_side);
        }

        DrawButtons(p, Fade);
        DrawGuide(p, Fade);
    }

    /// <summary>The frame every mode shares (draw_hub_bg): the panel background and the underline.</summary>
    internal void DrawFrame(UiPainter p, float alpha)
    {
        using UiPainter.AlphaScope a = p.WithAlpha(alpha);
        p.Sprite(GallerySheets.Panel, new RectF(0, 0, 1560, 1080), new RectF(365, 0, 1560, 1080));
        p.Sprite(GallerySheets.Underline, new RectF(0, 0, 1920, 16), new RectF(0, 72, 1920, 16));
    }

    private void DrawBanner(UiPainter p, GalleryMode mode)
    {
        if (GallerySheets.HasTitle(mode, Ctx.Language))
        {
            p.SpriteAt(GallerySheets.TitleOf(mode), 64, 0);
        }
    }

    /// <summary>The window and the six mode buttons (draw_hub_menu): the active mode shows its pressed column, the cursor's button the hover column and, while the buttons have the focus, the cursor line and light.</summary>
    internal void DrawButtons(UiPainter p, float alpha)
    {
        using UiPainter.AlphaScope a = p.WithAlpha(alpha);
        p.Sprite(GallerySheets.MenuWindow, new RectF(0, 0, 312, 1080), new RectF(56, 0, 312, 1080));
        int active = (int)_panel.Mode;
        for (int i = 0; i < 6; i++)
        {
            float sx = active == i ? 536f : _tab == i ? 272f : 8f;
            p.Sprite(GallerySheets.MenuButtons, new RectF(sx, 8 + (104 * i), 256, 96), new RectF(80, 160 + (i * 80), 256, 96));
        }

        if (_side)
        {
            float blink = 0.5f + (0.5f * MathF.Abs(MathF.Sin(_blink * 3f)));
            p.Sprite(GallerySheets.MenuLight, new RectF(0, 0, 528, 240), new RectF(0, 96 + (_tab * 80), 528, 240), blink, UiBlend.PsAdd);
            p.Sprite(GallerySheets.MenuLine, new RectF(0, 0, 304, 56), new RectF(80, 192 + (_tab * 80), 304, 56), 1f, UiBlend.PsAdd);
        }
    }

    private void DrawGuide(UiPainter p, float alpha) =>
        UiWidgets.Guide(p, _guide, (1f - GuideAlpha) * 25f, alpha * GuideAlpha, Ctx.Services.DecideIsCross);
}

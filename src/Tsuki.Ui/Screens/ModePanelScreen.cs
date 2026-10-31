// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui/touch.rs (Panel, panel_y, panel_button, panel_press, draw_touch_game)
using Tsuki.Engine.Contracts;

namespace Tsuki.Ui.Screens;

/// <summary>
/// The mode panel (docs/formats/system_flow.md system menu, process 0x12 at 0x6E810; knife <c>Panel</c>, sub_4E170): a bar that slides down from the top of the
/// story with eight buttons: previous scene, rewind (acts while held), auto, skip (acts while held), next scene, quick save, quick load, replay voice. Up opens
/// it from the story; left and right walk the buttons (wrapping on a fresh press), decide presses, down, cancel or the system menu button close it; a click
/// below the bar closes it. Previous scene, next scene and quick load ask first.
/// </summary>
public sealed class ModePanelScreen : UiScreen
{
    /// <summary>The first button's x in units of 640 x 448 (widget 0xCB at x = 25.333 + 73.667 i, system_flow.md).</summary>
    public const float X0 = 25.333f;

    /// <summary>The distance between buttons in units.</summary>
    public const float Dx = 73.667f;

    /// <summary>The buttons' y below the bar's top in units.</summary>
    public const float ButtonY = 6.637f;

    private const float ButtonW = 224f;
    private const float ButtonH = 64f;

    private readonly float[] _flash = new float[8];
    private float _t;
    private bool _closing;
    private int _key = 3;
    private int _hover = -1;
    private int _held = -1;
    private bool _opened;

    /// <summary>The button the pad has (the last one picked; the panel starts on skip, sub_4DCC0).</summary>
    public int Key => _key;

    /// <summary>The button under the pointer, or -1.</summary>
    public int Hover => _hover;

    /// <summary>The slide 0..1.</summary>
    public float Slide => _t;

    /// <summary>True once the bar has slid fully open.</summary>
    public bool IsOpen => _opened && !_closing;

    /// <summary>The bar's top in units: -43.14 hidden, 0 open (sub_4DE10: quad-out over the slide).</summary>
    public float PanelY => -43.14f - (43.14f * _t * (_t - 2f));

    /// <summary>Hit rectangle of button <paramref name="i"/> in canvas pixels.</summary>
    public RectF ButtonRect(int i) => new((X0 + (Dx * i)) * UiMetrics.UnitX, (PanelY + ButtonY) * UiMetrics.UnitY, ButtonW, ButtonH);

    /// <summary>Whether button <paramref name="i"/> is unavailable (drawn in the first row of its column).</summary>
    public bool Disabled(int i)
    {
        IGameServices s = Ctx.Services;
        return i switch
        {
            0 or 4 => !s.SceneJumpEnabled,
            5 => s.SavingDisabled,
            6 => s.SavingDisabled || !s.HasQuickSave,
            _ => false,
        };
    }

    /// <inheritdoc/>
    public override void OnOpened() => Ctx.Sound(UiSound.Cursor);

    /// <inheritdoc/>
    public override void Close()
    {
        _closing = true;
        ReleaseHeld();
    }

    private void ReleaseHeld()
    {
        if (_held == 1)
        {
            Ctx.Services.Rewind(false);
        }
        else if (_held == 3)
        {
            Ctx.Services.FastForward(false);
        }

        _held = -1;
    }

    /// <inheritdoc/>
    public override void StepBackground() => Animate();

    // the flashes, the slide in and the slide out; they go on under a dialog
    private void Animate()
    {
        for (int i = 0; i < _flash.Length; i++)
        {
            _flash[i] = MathF.Max(0f, _flash[i] - 1f);
        }

        if (_closing)
        {
            _t -= 0.05f;
            if (_t <= 0f)
            {
                _t = 0f;
                base.Close();
            }

            return;
        }

        if (!_opened)
        {
            _t += 0.05f;
            if (_t >= 1f)
            {
                _t = 1f;
                _opened = true;
            }
        }
    }

    /// <inheritdoc/>
    public override void Step(in UiInput input)
    {
        Animate();
        if (_closing || !_opened)
        {
            return;
        }

        if (Root.Top != this)
        {
            return;
        }

        if (input.Down || input.Cancel || input.Square || input.Triangle)
        {
            Close();
            return;
        }

        int key = _key;
        if (input.Right)
        {
            key = key < 7 ? key + 1 : input.RightPressed ? 0 : key;
        }
        else if (input.Left)
        {
            key = key > 0 ? key - 1 : input.LeftPressed ? 7 : key;
        }

        if (key != _key)
        {
            _key = key;
            _hover = -1;
            Ctx.Sound(UiSound.Cursor);
        }

        if (input.PointerMoved)
        {
            int hit = ButtonAt(input.PointerX, input.PointerY);
            if (hit != _hover && hit >= 0)
            {
                Ctx.Sound(UiSound.Cursor);
                _key = hit;
            }

            _hover = hit;
        }

        // buttons 1 (rewind) and 3 (skip) act while held (sub_40ED0)
        int heldNow = -1;
        if (input.PointerHeld && _hover is 1 or 3)
        {
            heldNow = _hover;
        }
        else if (input.DecideHeld && _hover < 0 && _key is 1 or 3)
        {
            heldNow = _key;
        }

        if (heldNow != _held)
        {
            ReleaseHeld();
            _held = heldNow;
            if (heldNow == 1)
            {
                Ctx.Services.Rewind(true);
            }
            else if (heldNow == 3)
            {
                Ctx.Services.FastForward(true);
            }
        }

        if (input.Click)
        {
            int hit = ButtonAt(input.PointerX, input.PointerY);
            if (hit >= 0)
            {
                _key = hit;
                Press(hit);
            }
            else if (input.PointerY > (PanelY + 43.14f) * UiMetrics.UnitY)
            {
                // a click below the bar closes it; the text does not advance
                Close();
            }
        }
        else if (input.Decide && _key is not (1 or 3))
        {
            Press(_key);
        }
    }

    private int ButtonAt(float x, float y)
    {
        for (int i = 0; i < 8; i++)
        {
            if (ButtonRect(i).Contains(x, y))
            {
                return i;
            }
        }

        return -1;
    }

    private void Press(int b)
    {
        _flash[b] = 10f;
        IGameServices s = Ctx.Services;
        if (Disabled(b))
        {
            Ctx.Sound(UiSound.Buzzer);
            return;
        }

        switch (b)
        {
            case 0:
                Ctx.Sound(UiSound.Decide);
                Root.Push(new ConfirmDialog(ConfirmKind.PrevScene, yes => Jump(yes, previous: true)));
                break;
            case 2:
                Ctx.Sound(UiSound.Decide);
                s.ToggleAuto();
                if (s.AutoMode)
                {
                    Root.ShowBanner(BannerKind.Auto);
                }

                break;
            case 4:
                Ctx.Sound(UiSound.Decide);
                Root.Push(new ConfirmDialog(ConfirmKind.NextScene, yes => Jump(yes, previous: false)));
                break;
            case 5:
                Ctx.Sound(UiSound.Decide);
                if (s.QuickSave())
                {
                    Root.ShowBanner(BannerKind.QuickSave);
                }

                break;
            case 6:
                Ctx.Sound(UiSound.Decide);
                Root.Push(new ConfirmDialog(ConfirmKind.QuickLoad, yes =>
                {
                    if (yes && s.QuickLoad())
                    {
                        Root.Clear();
                    }
                }, thumbnailSlot: 139));
                break;
            case 7:
                s.ReplayVoice();
                break;
        }
    }

    private void Jump(bool yes, bool previous)
    {
        if (!yes)
        {
            return;
        }

        if (previous)
        {
            Ctx.Services.PreviousScene();
        }
        else
        {
            Ctx.Services.NextScene();
        }

        Close();
    }

    /// <inheritdoc/>
    public override void Draw(UiPainter p)
    {
        if (_t <= 0f)
        {
            return;
        }

        float y = PanelY;
        p.Sprite(UiSheets.ModeBar, new RectF(0, 0, 1920, 96), new RectF(0, y * UiMetrics.UnitY, 1920, 96));
        float top = (y + ButtonY) * UiMetrics.UnitY;
        bool dialog = Root.Top != this;
        int focus = dialog ? -1 : _hover >= 0 ? _hover : _key;
        for (int i = 0; i < 8; i++)
        {
            // rows: disabled, normal, hover, pressed (sprite defs 0x591CF8: 224 x 64 from (8 + 232 column, 8 + 72 row)); an unavailable button is dark, auto lit
            int row;
            if (i != 2 && Disabled(i))
            {
                row = 0;
            }
            else if (_flash[i] > 0f || (i == 2 && Ctx.Services.AutoMode) || _held == i)
            {
                row = 3;
            }
            else if (focus == i)
            {
                row = 2;
            }
            else
            {
                row = 1;
            }

            p.Sprite(UiSheets.ModeButtons, new RectF(8f + (i * 232f), 8f + (row * 72f), ButtonW, ButtonH), new RectF((X0 + (Dx * i)) * UiMetrics.UnitX, top, ButtonW, ButtonH));
        }
    }
}

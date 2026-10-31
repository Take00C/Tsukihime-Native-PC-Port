// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (draw_menu_base, menu items and input)
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Ui.Screens;

/// <summary>The five tabs of the system menu.</summary>
public enum MenuTab
{
    /// <summary>Save.</summary>
    Save = 0,

    /// <summary>Load.</summary>
    Load = 1,

    /// <summary>Config.</summary>
    Config = 2,

    /// <summary>FlowChart.</summary>
    FlowChart = 3,

    /// <summary>Title.</summary>
    Title = 4,
}

/// <summary>
/// The system menu (TRIANGLE in the story; docs/formats/system_flow.md "System menu", knife <c>Screen::Menu</c>): over the blurred scene, the chapter and scene
/// title bands and five tabs on the left. Save, Load and Config open as screens on top of it and keep the menu drawn under them; FlowChart is package K5b's;
/// Title leaves the story after an auto save.
/// </summary>
public sealed class SystemMenuScreen : UiScreen
{
    private readonly UiItemList _items = new();
    private bool _focus;
    private bool _toTitle;
    private float _titleWait;

    /// <summary>Creates the menu.</summary>
    public SystemMenuScreen()
    {
        StartHidden();
    }

    /// <summary>The screen opened from this menu that is still on show (not closing), or null.</summary>
    private ISubMenu? Sub => Root.Stack.OfType<ISubMenu>().LastOrDefault(s => s is UiScreen { Closing: false });

    /// <summary>The tab a screen opened from this menu belongs to, or -1 (the menu draws it as open).</summary>
    public int OpenTab => Sub?.Tab ?? -1;

    /// <summary>The cursor line and light show on the remembered tab (the tabs have the focus while a submenu is open).</summary>
    public bool SideFocus => Sub?.Side.Active ?? false;

    /// <inheritdoc/>
    public override bool Finished => base.Finished && !_toTitle;

    /// <inheritdoc/>
    protected override float FadeInStep => 1f / 18f;

    /// <inheritdoc/>
    protected override float FadeOutStep => 1f / 30f;

    /// <summary>The hit rectangle of tab <paramref name="i"/> (objects 197..201: touch rect (0, 0)-(74, 29) at (26.667, 43.556 + 31.111 i), whole units inclusive).</summary>
    public static RectF TabRect(int i)
    {
        float x = MathF.Truncate(26.667f);
        float y = MathF.Truncate(43.556f + (i * 31.111f));
        return new RectF(x * UiMetrics.UnitX, y * UiMetrics.UnitY, 75f * UiMetrics.UnitX, 30f * UiMetrics.UnitY);
    }

    /// <inheritdoc/>
    public override void OnOpened()
    {
        for (int i = 0; i < 5; i++)
        {
            _items.Add(TabRect(i), i);
        }

        _items.Cursor = Ctx.MenuTab;
        Ctx.Sound(UiSound.Open);
        Ctx.Backdrop.Fade(0.0555f);
    }

    /// <inheritdoc/>
    public override void Close()
    {
        if (Closing)
        {
            return;
        }

        base.Close();
        if (Root.Stack.Count <= 1)
        {
            Ctx.Backdrop.Fade(-1f / 30f);
        }
    }

    /// <inheritdoc/>
    public override void OnClosed()
    {
        if (!Root.Active)
        {
            Ctx.Backdrop.Fade(0f);
        }
    }

    /// <inheritdoc/>
    public override void Step(in UiInput input)
    {
        if (_toTitle)
        {
            // the Auto Save banner plays while the menu fades; the title follows once both are gone (sub_4D7E0)
            _titleWait++;
            if (Root.Banner is null && Fade <= 0f)
            {
                _toTitle = false;
                Ctx.Services.ReturnToTitle();
                Root.Clear();
            }

            return;
        }

        if (Closing)
        {
            return;
        }

        if (input.Cancel || input.Square || input.L2)
        {
            Ctx.Sound(UiSound.Buzzer);
            Close();
            return;
        }

        int before = Ctx.MenuTab;
        if (input.Up || input.Down)
        {
            Ctx.MenuTab = (Ctx.MenuTab + (input.Up ? 4 : 1)) % 5;
            _focus = true;
        }

        if (input.PointerMoved)
        {
            int hit = _items.HitTest(input.PointerX, input.PointerY);
            if (hit >= 0)
            {
                Ctx.MenuTab = hit;
                _focus = true;
            }
        }

        if (Ctx.MenuTab != before)
        {
            Ctx.Sound(UiSound.Cursor);
        }

        if (input.Click)
        {
            int hit = _items.HitTest(input.PointerX, input.PointerY);
            if (hit >= 0)
            {
                Ctx.MenuTab = hit;
                _focus = true;
                Act((MenuTab)hit);
            }
        }
        else if (input.Decide)
        {
            Act((MenuTab)Ctx.MenuTab);
        }
    }

    /// <summary>Opens a tab (also called by a submenu whose side focus picked another tab).</summary>
    public void Act(MenuTab tab)
    {
        IGameServices s = Ctx.Services;
        switch (tab)
        {
            case MenuTab.Save:
            case MenuTab.Load:
                if (s.SavingDisabled)
                {
                    Ctx.Sound(UiSound.Buzzer);
                    return;
                }

                Ctx.Sound(UiSound.Decide);
                Root.Push(new SaveLoadScreen(tab == MenuTab.Save));
                break;
            case MenuTab.Config:
                Ctx.Sound(UiSound.Decide);
                Root.Push(new ConfigScreen());
                break;
            case MenuTab.FlowChart:
                Ctx.Sound(UiSound.Decide);
                s.OpenFlowchart();
                OpenChart(s);
                break;
            case MenuTab.Title:
                Ctx.Sound(UiSound.Decide);
                if (s.AutoSaveForTitle())
                {
                    Root.ShowBanner(BannerKind.AutoSave);
                }

                _toTitle = true;
                Ctx.Backdrop.Fade(-1f / 30f);
                base.Close();
                break;
        }
    }

    // package K5b: during a replay picked in the gallery's flowchart the tab asks to end the replay (dialog 12); otherwise it opens the chart when it is available
    private void OpenChart(IGameServices s)
    {
        if (s.ReplayActive && Ctx.Gallery.Mode == (int)GalleryMode.Flowchart)
        {
            Root.Push(new ConfirmDialog(ConfirmKind.EndReplay, yes =>
            {
                if (yes)
                {
                    s.EndReplay();
                }
            }));
            return;
        }

        if (FlowchartScreen.Available(Ctx, gallery: false))
        {
            Root.Push(new FlowchartScreen(gallery: false));
        }
        else
        {
            // cFlowchartTask state 3 (sub_17530, knife ui.rs: popup 1496 in game = PS4 ui_text 1497): "You cannot use the flowchart yet." for 480 frames over the menu,
            // the message the gallery's button shows (gallery.md 2.3); the port buzzed here until package R1
            Root.Push(new MessagePopup(Ctx.Message(1497), 480f));
        }
    }

    /// <inheritdoc/>
    public override void Draw(UiPainter p)
    {
        Ctx.Backdrop.Draw(p);
        using UiPainter.AlphaScope a = p.WithAlpha(Fade);
        DrawBase(p, Ctx, OpenTab >= 0 ? OpenTab : -1, SideFocus || OpenTab < 0 ? Ctx.MenuTab : -1, _focus || SideFocus ? Ctx.MenuTab : -1);
    }

    /// <summary>
    /// The menu body (knife <c>draw_menu_base</c>, sub_4C370): title band, scene band, underline, the chapter title cell by phase F023, the left window, the five
    /// buttons (columns normal / hover / selected), the cursor line and light, and the scene title text of F022.
    /// </summary>
    /// <param name="p">The painter.</param>
    /// <param name="ctx">The context.</param>
    /// <param name="open">The tab of the open submenu (selected column), or -1.</param>
    /// <param name="cursor">The tab under the cursor line and light, or -1.</param>
    /// <param name="focus">The tab highlighted (hover column), or -1.</param>
    public static void DrawBase(UiPainter p, UiContext ctx, int open, int cursor, int focus)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(ctx);
        IGameServices s = ctx.Services;
        p.Sprite(UiSheets.TitleBand, new RectF(0, 0, UiMetrics.Width, 392));
        p.Sprite(UiSheets.SceneBand, new RectF(752, 0, 1168, 80));
        p.Sprite(UiSheets.Underline, new RectF(0, 72, UiMetrics.Width, 16));
        IReadOnlyList<SheetCell> phases = ctx.Assets.Tables.PhaseTitleCells(ctx.Language);
        int phase = s.Phase;
        if (phase >= 0 && phase < phases.Count)
        {
            SheetCell c = phases[phase];
            var src = RectF.FromCorners(c.X0, c.Y0, c.X1, c.Y1);
            p.Sprite(UiSheets.PhaseTitles, src, new RectF(8, 0, src.W, src.H));
        }

        // the scene title: ui_text 396 + F022, black, right-aligned to x 608 (help line block 0x3C, flags 0x200, at (110, 19) in 640 x 448 units)
        string title = ctx.Message(396 + s.SceneTitle);
        if (!string.IsNullOrWhiteSpace(title))
        {
            var style = TextStyle.Menu(0.36) with { Width = 498, Colour = TextStyle.Black, DrawFlags = BlockDraw.Always | BlockDraw.Right };
            ctx.Text.Draw(p, title.Replace("^", string.Empty, StringComparison.Ordinal), style, 110f, 19f);
        }

        p.Sprite(UiSheets.MenuWindow, new RectF(0, 0, 312, UiMetrics.Height), new RectF(56, 3, 312, UiMetrics.Height));
        bool locked = s.SavingDisabled;
        for (int i = 0; i < 5; i++)
        {
            var dst = new RectF(80, 105 + (i * 75), 224, 88);
            if (locked && i < 2)
            {
                p.Sprite(UiSheets.MenuButtons, new RectF(24, 24 + (i * 104), 224, 88), dst, 143f / 255f);
                continue;
            }

            int col = open == i ? 2 : focus == i ? 1 : 0;
            p.Sprite(UiSheets.MenuButtons, new RectF(24 + (col * 240), 24 + (i * 104), 224, 88), dst);
        }

        if (cursor >= 0)
        {
            float blink = 0.5f + (0.5f * MathF.Abs(MathF.Sin(ctx.Frame / 60f * 3f)));
            p.Sprite(UiSheets.CursorLight, new RectF(0, 0, 528, 240), new RectF(0, 32 + (cursor * 75), 528, 240), blink, UiBlend.PsAdd);
            p.Sprite(UiSheets.CursorLine, new RectF(0, 0, 224, 56), new RectF(86, 136 + (cursor * 75), 224, 56), 1f, UiBlend.PsAdd);
        }
    }
}

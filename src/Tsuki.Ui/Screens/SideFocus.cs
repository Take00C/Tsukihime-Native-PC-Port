// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (enter_side, leave_side, side_nav)
namespace Tsuki.Ui.Screens;

/// <summary>A screen opened from a tab of the system menu.</summary>
public interface ISubMenu
{
    /// <summary>The tab the screen belongs to.</summary>
    int Tab { get; }

    /// <summary>The focus helper.</summary>
    SideFocus Side { get; }
}

/// <summary>
/// The focus of a screen opened from the system menu that sits on the menu's tabs (knife <c>side</c>): left from the page's first column hands the cursor to the
/// tabs, up and down move it, decide on another tab opens that tab (the submenu closes), decide on its own tab or right returns to the page, cancel closes
/// the submenu (sub_4CEA0 while a submenu is open).
/// </summary>
public sealed class SideFocus
{
    private readonly UiScreen _owner;
    private readonly int _ownTab;

    /// <summary>Creates the helper for <paramref name="owner"/>, the submenu of system menu tab <paramref name="ownTab"/>.</summary>
    public SideFocus(UiScreen owner, int ownTab)
    {
        _owner = owner;
        _ownTab = ownTab;
    }

    /// <summary>True while the tabs have the focus.</summary>
    public bool Active { get; private set; }

    /// <summary>The system menu under the submenu, or null when the screen was opened from the title.</summary>
    public SystemMenuScreen? Menu => _owner.Root.Stack.OfType<SystemMenuScreen>().FirstOrDefault();

    /// <summary>Hands the focus to the tabs (false when there is no menu below).</summary>
    public bool Enter()
    {
        SystemMenuScreen? m = Menu;
        if (m is null)
        {
            return false;
        }

        Active = true;
        return true;
    }

    /// <summary>Gives the focus back to the page.</summary>
    public void Leave()
    {
        Active = false;
    }

    /// <summary>Canvas x from which the submenu's page starts (the system menu's window ends at 368).</summary>
    private const float PageLeft = 372f;

    private static bool OverTab(in UiInput input)
    {
        for (int i = 0; i < 5; i++)
        {
            if (SystemMenuScreen.TabRect(i).Contains(input.PointerX, input.PointerY))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Steps the tab focus. Returns true when the input was used (the page must ignore it).
    /// </summary>
    public bool Step(in UiInput input, UiContext ctx)
    {
        if (!Active || Menu is not { } m)
        {
            return false;
        }

        if (input.Cancel)
        {
            Leave();
            ctx.Sound(UiSound.Buzzer);
            _owner.Close();
            m.Close();
            return true;
        }

        // the mouse takes the focus back: a pointer that moves onto the page, or clicks on it, leaves the tabs (package K12)
        if ((input.PointerMoved || input.Click) && input.PointerActive && input.PointerX >= PageLeft && !OverTab(input))
        {
            Leave();
            return false;
        }

        int before = ctx.MenuTab;
        if (input.Up || input.Down)
        {
            ctx.MenuTab = (ctx.MenuTab + (input.Up ? 4 : 1)) % 5;
        }

        if (input.PointerMoved)
        {
            for (int i = 0; i < 5; i++)
            {
                if (SystemMenuScreen.TabRect(i).Contains(input.PointerX, input.PointerY))
                {
                    ctx.MenuTab = i;
                }
            }
        }

        if (ctx.MenuTab != before)
        {
            ctx.Sound(UiSound.Cursor);
        }

        if (input.Right)
        {
            Leave();
            return true;
        }

        int click = -1;
        if (input.Click)
        {
            for (int i = 0; i < 5; i++)
            {
                if (SystemMenuScreen.TabRect(i).Contains(input.PointerX, input.PointerY))
                {
                    click = i;
                }
            }
        }

        if (input.Decide || click >= 0)
        {
            if (click >= 0)
            {
                ctx.MenuTab = click;
            }

            if (ctx.MenuTab == _ownTab)
            {
                Leave();
            }
            else
            {
                Leave();
                _owner.Close();
                m.Act((MenuTab)ctx.MenuTab);
            }
        }

        return true;
    }

    /// <summary>A click on a tab of the menu below while the page has the focus (the tabs stay tappable under the submenu).</summary>
    public bool ClickOnTab(in UiInput input, UiContext ctx)
    {
        if (!input.Click || Menu is not { } m)
        {
            return false;
        }

        for (int i = 0; i < 5; i++)
        {
            if (SystemMenuScreen.TabRect(i).Contains(input.PointerX, input.PointerY))
            {
                ctx.MenuTab = i;
                if (i != _ownTab)
                {
                    _owner.Close();
                    m.Act((MenuTab)i);
                }

                return true;
            }
        }

        return false;
    }
}

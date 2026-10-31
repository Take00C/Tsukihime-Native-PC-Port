// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (Screen::Backlog: key_nav, shoulder, backlog_to, draw), knife/src/game.rs (backlog_quads)
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Ui.Screens;

/// <summary>
/// The backlog (docs/formats/adv_state.md 5.4 and 5.5; knife <c>Screen::Backlog</c>, sub_27D10 / sub_28A90): the history ring one page at a time over the
/// frozen, blurred scene darkened to 75 %, each page re-laid out with the message layout, a scroll bar, and a quick jump back to the page. Up and down step to
/// an older or newer page (a fresh press past either end wraps), left and right go to the oldest and newest, L1 and R1 likewise; decide asks for the jump;
/// the click on a message that has a voice replays it. Cancel, TRIANGLE and L2 close it.
/// </summary>
public sealed class BacklogScreen : UiScreen
{
    private TextBlock[] _blocks = [];
    private (double X, double Y)[] _positions = [];
    private int _blocksFor = -1;
    private readonly UiScrollBar _bar = new();
    private int _page;
    private bool _jump;
    private int _jumpPage;

    /// <summary>Creates the backlog on the newest page.</summary>
    public BacklogScreen()
    {
        StartHidden();
    }

    /// <summary>The page shown: 0 is the newest.</summary>
    public int Page => _page;

    /// <summary>Number of pages.</summary>
    public int Count => Ctx.Services.Backlog.Count;

    /// <inheritdoc/>
    protected override float FadeInStep => 0.05f;

    /// <inheritdoc/>
    protected override float FadeOutStep => 0.05f;

    private static TextStyle Style => new(640, 344.7, 0.74, 0.74, 474, 220, 9, 0, TextStyle.White, BlockDraw.Always | BlockDraw.Outline);

    /// <inheritdoc/>
    public override void OnOpened() => Ctx.Sound(UiSound.Open);

    /// <inheritdoc/>
    public override void OnClosed()
    {
        if (_jump)
        {
            Ctx.Services.BacklogJump(_jumpPage);
            Root.Clear();
        }
    }

    private void GoTo(int next)
    {
        next = Math.Clamp(next, 0, Math.Max(0, Count - 1));
        if (next != _page)
        {
            _page = next;
            Ctx.Sound(UiSound.Cursor);
        }
    }

    /// <inheritdoc/>
    public override void Step(in UiInput input)
    {
        if (Closing || Root.Top != this)
        {
            return;
        }

        int max = Math.Max(0, Count - 1);
        // the key that opens the backlog in the story (I) closes it again; the wheel and Page Up also set that shell action but scroll, so they carry wheel steps
        bool toggle = (input.Shell & ShellActions.Backlog) != 0 && input.Wheel == 0;
        if (input.Cancel || input.Triangle || input.L2 || toggle)
        {
            Ctx.Sound(UiSound.Buzzer);
            Close();
            return;
        }

        // the scroll bar (package K12): the thumb at the top is the oldest page; a click on the track steps five pages like Page Up and Page Down
        ScrollBarUse use = _bar.Step(input, max > 0 ? 1f - (_page / (float)max) : 0f, 608f, max > 0);
        if (use.Fraction is { } dragged)
        {
            GoTo((int)MathF.Round((1f - dragged) * max));
        }
        else if (use.Page != 0)
        {
            GoTo(_page - (use.Page * UiInput.PageSteps));
        }

        if (use.Consumed)
        {
            return;
        }

        if (input.Up)
        {
            GoTo(_page < max ? _page + 1 : input.UpPressed ? 0 : _page);
        }
        else if (input.Down)
        {
            GoTo(_page > 0 ? _page - 1 : input.DownPressed ? max : _page);
        }
        else if (input.LeftPressed || input.L1)
        {
            GoTo(max);
        }
        else if (input.RightPressed || input.R1)
        {
            GoTo(0);
        }

        if (input.Wheel > 0)
        {
            GoTo(_page + 1);
        }
        else if (input.Wheel < 0)
        {
            GoTo(_page - 1);
        }

        if ((input.Shell & ShellActions.ReplayVoice) != 0)
        {
            ReplayVoice(-1);
        }

        if (input.Click && input.PointerActive && input.PointerX < 543f * 3f)
        {
            int msg = MessageAt(input.PointerY);
            if (msg >= 0 && Ctx.Services.Backlog.Count > _page && Ctx.Services.Backlog[_page].Messages[msg].VoiceId >= 0)
            {
                ReplayVoice(msg);
            }
            else
            {
                AskJump();
            }
        }
        else if (input.Decide && !input.Click)
        {
            AskJump();
        }
    }

    private void ReplayVoice(int message)
    {
        IReadOnlyList<BacklogPage> pages = Ctx.Services.Backlog;
        if (_page >= pages.Count)
        {
            return;
        }

        IReadOnlyList<BacklogMessage> msgs = pages[_page].Messages;
        int voice = message >= 0 ? msgs[message].VoiceId : msgs.Select(m => m.VoiceId).FirstOrDefault(v => v >= 0, -1);
        if (voice >= 0)
        {
            Ctx.Services.PlayBacklogVoice(voice);
        }
    }

    private void AskJump()
    {
        IReadOnlyList<BacklogPage> pages = Ctx.Services.Backlog;
        if (_page >= pages.Count || !pages[_page].Jumpable)
        {
            Ctx.Sound(UiSound.Buzzer);
            return;
        }

        int page = _page;
        Root.Push(new ConfirmDialog(ConfirmKind.BacklogJump, yes =>
        {
            if (yes)
            {
                _jump = true;
                _jumpPage = page;
                Close();
            }
        }));
    }

    private int MessageAt(float py)
    {
        Layout();
        float ty = py / (UiMetrics.Height / 344.7f);
        for (int i = 0; i < _blocks.Length; i++)
        {
            float top = (float)_positions[i].Y;
            float bottom = top + _blocks[i].LineHeight.Take(Math.Max(1, _blocks[i].NLines)).Sum() + (_blocks[i].Leading * Math.Max(0, _blocks[i].NLines - 1));
            if (ty >= top && ty < bottom)
            {
                return i;
            }
        }

        return -1;
    }

    private void Layout()
    {
        if (_blocksFor == _page && _blocks.Length > 0)
        {
            return;
        }

        IReadOnlyList<BacklogPage> pages = Ctx.Services.Backlog;
        _blocksFor = _page;
        if (_page >= pages.Count)
        {
            _blocks = [];
            _positions = [];
            return;
        }

        IReadOnlyList<BacklogMessage> msgs = pages[_page].Messages;
        _blocks = [.. msgs.Select((m, j) => Ctx.Text.Layout(m.Text, Style, 79 + Math.Min(j, 10)))];
        _positions = MessageWindow.PagePositions(_blocks, Ctx.Language, [.. msgs.Select(m => m.BlankBefore)]);
    }

    /// <inheritdoc/>
    public override void Draw(UiPainter p)
    {
        using UiPainter.AlphaScope scope = p.WithAlpha(Fade);
        p.Cover(0.75f);
        Layout();
        TextStyle style = Style;
        for (int i = 0; i < _blocks.Length; i++)
        {
            Ctx.Text.DrawBlock(p, _blocks[i], style, (float)_positions[i].X, (float)_positions[i].Y);
        }

        int n = Count;
        if (n > 1)
        {
            UiWidgets.ScrollBar(p, 1f - (_page / (float)(n - 1)), 608f, 1f, _bar);
        }

        UiWidgets.Guide(p, UiWidgets.GuideBacklog, decideIsCross: Ctx.Services.DecideIsCross);
    }
}

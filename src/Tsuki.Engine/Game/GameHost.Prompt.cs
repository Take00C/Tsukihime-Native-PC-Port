using Tsuki.Engine.Contracts;
using Tsuki.Engine.Vm.Variables;

namespace Tsuki.Engine.Game;

// The story's two-button prompts (package K13): _SELS (mode 2, the yes / no system prompt of the QACALL menu) and _SELA (mode 3, the chapter-start "Do you want to save
// your progress?"). The eboot runs them in the choice code: update 0xA6940 / 0xA6AD0, input 0xA7AA0 / 0xA7FD0, setup 0xA5DF0 (docs/status/knife/K13.md has the
// disassembly notes). The host owns the state machine, the stored value and the timing; the pictures are the UI layer's (Tsuki.Ui draws the dialog from
// UiStoryPrompt, because the dialog window and the buttons are UI sheets and the message is a ui_text string).
//
// phases (u16 0x17506A0): 0 the dialog fades in (alpha + 0.1 per frame), 2 the player answers, 3 the dialog fades out (alpha - 0.1 per frame; the choice ends when the
// alpha reaches 0). SELA's Yes goes 4 (fade out) -> 5 (ask for the save screen) -> 6 (wait until it is closed) -> 7 -> 3 (the alpha is spent, so the choice ends at once).
// Button 0 is No, button 1 is Yes. Nothing but the button index of a SELS is stored; a SELA stores nothing.
public sealed partial class GameHost
{
    private bool _promptSaveDone;
    private (short X, short Y)? _promptPointer;

    /// <summary>True while a two-button prompt (<c>_SELS</c> or <c>_SELA</c>) is open.</summary>
    public bool PromptOpen => ChoiceOpen && VarState.ChoiceMode is ChoiceMode.TwoButtons or ChoiceMode.SavePrompt;

    /// <summary>The <c>ui_text</c> id of the open prompt's message: 216 for a <c>_SELA</c>, 1446 + F27 for a <c>_SELS</c>.</summary>
    public int PromptMessageId => VarState.ChoiceMode == ChoiceMode.SavePrompt ? 216 : 1446 + F(27);

    /// <summary>The window's size factor now: 1 + 0.05 (1 - alpha) while the prompt opens, 1 - 0.05 (1 - alpha) while it closes after an answer, else 1.</summary>
    public float PromptScale
    {
        get
        {
            float a = Math.Clamp(_choiceAlpha, 0f, 1f);
            return _choicePhase switch
            {
                0 => 1f + ((1f - a) * PromptLayout.Pop),
                3 => 1f + ((1f - a) * -PromptLayout.Pop),
                _ => 1f,
            };
        }
    }

    /// <summary>
    /// Tells the host that the save screen it asked for (<see cref="SavePromptAccepted"/>) has closed, saved or not. The host then ends the prompt and the script goes on.
    /// A call with no screen open is ignored.
    /// </summary>
    public void SavePromptScreenClosed() => _promptSaveDone = true;

    private void OpenPrompt()
    {
        EndSceneSkip();
        Wait.Bits |= WaitBits.Choice;
        _choicePhase = 0;
        _cursor = -1;
        _previousCursor = -1;
        _choiceAlpha = 0f;
        _choiceFlash = 0f;
        _choiceHighlight = 0f;
        _decided = -1;
        _promptSaveDone = false;
        _promptPointer = null;

        // 0xA5DF0: the page is cleared (0x75CD0(1), a new page), F823 = 0 and F1103 = 1 (the message layer hides)
        NewPage();
        SetF(823, 0);
        SetF(1103, 1);
    }

    private void NewPage()
    {
        NewMessageSlot();
        _page.Clear();
        _current = null;
    }

    private bool ChoosePrompt(int index)
    {
        if (_choicePhase < 2)
        {
            // the player's decide while the dialog fades in completes the fade; a policy may answer in the same frame
            _choiceAlpha = 1f;
            _choicePhase = 2;
        }

        if (_choicePhase != 2 || index is < 0 or > 1)
        {
            return false;
        }

        SetPromptCursor(index, sound: false);
        DecidePrompt();
        return true;
    }

    private void SetPromptCursor(int c, bool sound)
    {
        if (c != _cursor)
        {
            _previousCursor = _cursor;
            _cursor = c;
            if (sound)
            {
                Sound.SystemSePlay(2);
            }
        }
    }

    /// <summary>One frame of the prompt (0xA6940 for mode 2, 0xA6AD0 for mode 3; <paramref name="dt"/> is the frame count of the step).</summary>
    private void UpdatePrompt(in InputFrame input, int dt)
    {
        switch (_choicePhase)
        {
            case 0:
                // a pointer that rests where the dialog appears has not moved when the answer phase starts, so it selects nothing
                _promptPointer = input.Pointer.Active ? (input.Pointer.X, input.Pointer.Y) : null;
                _choiceAlpha = (dt * PromptLayout.FadeStep) + _choiceAlpha;
                if (!(_choiceAlpha < 1f))
                {
                    _choiceAlpha = 1f;
                    _choicePhase = 2;
                }

                break;
            case 2:
                PromptInput(input);
                break;
            case 3:
                _choiceAlpha = (dt * -PromptLayout.FadeStep) + _choiceAlpha;
                if (!(0f < _choiceAlpha))
                {
                    _choiceAlpha = 0f;
                    CloseChoice();
                    NewPage();
                }

                break;
            case 4:
                // SELA, Yes: the dialog fades out first (the alpha is left where it falls, below 0)
                _choiceAlpha = (dt * -PromptLayout.FadeStep) + _choiceAlpha;
                if (!(0f < _choiceAlpha))
                {
                    _choicePhase = 5;
                }

                break;
            case 5:
                _choicePhase = 6;
                _promptSaveDone = SavePromptAccepted is null;       // nobody to show a save screen (a headless run): nothing to wait for
                SavePromptAccepted?.Invoke();
                break;
            case 6:
                if (_promptSaveDone)
                {
                    _choicePhase = 7;
                }

                break;
            case 7:
                _choicePhase = 3;
                break;
        }
    }

    /// <summary>
    /// The input of the answer phase (0xA7AA0 / 0xA7FD0): a cancel press, or the pointer's right button, makes the frame void; the pointer over a button selects it
    /// (SE 2); decide or a left click answers the selected button (SE 0), and nothing happens while no button is selected; left, right, up and down move the selection.
    /// </summary>
    private void PromptInput(in InputFrame input)
    {
        PointerState ptr = input.Pointer;
        if ((input.Pressed & PadButtons.Cancel) != 0 || (ptr.Pressed & PointerButtons.Right) != 0)
        {
            return;
        }

        bool click = (ptr.Pressed & PointerButtons.Left) != 0;
        int hovered = -1;
        if (ptr.Active)
        {
            // the pointer selects a button when it moved (or came into the game area), or when a button went down; one that rests where the dialog appeared selects nothing
            bool moved = _promptPointer is not { } last || last.X != ptr.X || last.Y != ptr.Y;
            _promptPointer = (ptr.X, ptr.Y);
            if (moved || ptr.Pressed != PointerButtons.None)
            {
                hovered = PromptLayout.ButtonAt(ptr.X, ptr.Y);
                if (hovered >= 0)
                {
                    SetPromptCursor(hovered, sound: true);
                }
            }
        }
        else
        {
            _promptPointer = null;
        }

        if ((input.Pressed & PadButtons.Decide) != 0 || click)
        {
            // a click beside both buttons, while the pointer is in use, is void (0xA7B85: [pointer + 0xC] > 0 and the left button)
            bool beside = click && ptr.Active && hovered < 0;
            if (!beside && _cursor is 0 or 1)
            {
                DecidePrompt();
            }

            return;
        }

        // the eboot reads the d-pad bits; the port's left stick (the pad's own bits) does the same
        const PadButtons left = PadButtons.Left | PadButtons.StickLeft;
        const PadButtons right = PadButtons.Right | PadButtons.StickRight;
        const PadButtons up = PadButtons.Up | PadButtons.StickUp;
        const PadButtons down = PadButtons.Down | PadButtons.StickDown;
        PadButtons rep = input.Repeated;
        PadButtons fresh = input.Pressed;
        if ((rep & left) != 0)
        {
            // from the left button (or none) a fresh press wraps to Yes, a repeat stays
            SetPromptCursor(_cursor > 0 ? _cursor - 1 : (fresh & left) != 0 ? 1 : 0, sound: true);
        }
        else if ((rep & right) != 0)
        {
            // from none to No, from No to Yes, from Yes a fresh press wraps to No
            SetPromptCursor(_cursor <= 0 ? _cursor + 1 : (fresh & right) != 0 ? 0 : 1, sound: true);
        }
        else if ((rep & up) != 0)
        {
            SetPromptCursor(0, sound: true);
        }
        else if ((rep & down) != 0)
        {
            SetPromptCursor(1, sound: true);
        }
    }

    /// <summary>The answer (cursor 0 or 1): a SELS stores the button index, a SELA stores nothing and goes to phase 3 (No) or 4 (Yes).</summary>
    private void DecidePrompt()
    {
        int cursor = _cursor;
        ChoiceKind kind = (ChoiceKind)VarState.ChoiceMode;
        if (kind == ChoiceKind.TwoButtons)
        {
            VarCommands.ChoiceStore(VarState, cursor);
        }

        Sound.SystemSePlay(0);
        _decided = cursor;
        _choicePhase = kind == ChoiceKind.SavePrompt && cursor == 1 ? 4 : 3;
        ChoiceDecided?.Invoke(kind, VarState.ChoiceId, cursor, cursor);
    }
}

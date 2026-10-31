using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game.Effects;
using Tsuki.Engine.Text;
using Tsuki.Engine.Vm;
using Tsuki.Engine.Vm.Variables;

namespace Tsuki.Engine.Game;

/// <summary>One line of the choice menu (<c>_SELR</c>).</summary>
public sealed class ChoiceLine
{
    /// <summary>The value the line stores in the result variable (argument 0 of <c>_SELR</c>).</summary>
    public int Value { get; set; }

    /// <summary>Argument 2 of <c>_SELR</c> was 1: the line is greyed out and cannot be selected.</summary>
    public bool Locked { get; set; }

    /// <summary>The slot text: the number prefix line followed by the argument (script.md 5.1 of system_flow.md).</summary>
    public byte[] Text { get; set; } = [];

    /// <summary>The laid-out block of the line.</summary>
    public TextBlock? Block { get; set; }
}

/// <summary>What the choice machine shows (the Ui layer draws from it).</summary>
public enum ChoiceKind : byte
{
    /// <summary>No choice.</summary>
    None = 0,

    /// <summary>The list of <c>_SELR</c> lines (<c>_SELD</c>).</summary>
    List = 1,

    /// <summary>Two buttons (<c>_SELS</c>, the QA hint prompt).</summary>
    TwoButtons = 2,

    /// <summary>"Do you want to save your progress?" (<c>_SELA</c>).</summary>
    SavePrompt = 3,
}

// The choice menu (docs/formats/system_flow.md 5; knife game/choice.rs for the layout): _SELR adds a line, _SELD / _SELS / _SELA open the menu and
// yield, the choice code (VA 0xA6180) runs from tick_reveal while wait bit 0x10 is set and clears it at the end of phase 3.
public sealed partial class GameHost
{
    private const float ChoiceFadeIn = 0.04f;
    private const float ChoiceFadeOut = 0.028f;
    private const float ChoiceFlashStep = 0.02f;

    private readonly List<ChoiceLine> _lines = [];
    private int _choicePhase;
    private int _cursor = -1;
    private int _previousCursor = -1;
    private float _choiceAlpha;
    private float _choiceFlash;
    private float _choiceHighlight;
    private float _choiceHead;
    private int _decided = -1;
    private ChoiceGlow _glow = null!;
    private readonly Dictionary<(int Line, int Colour), TextBlock> _glowText = [];
    private readonly Dictionary<int, (int W, int H)> _selectSizes = [];

    private void InitChoice()
    {
        _glow = new ChoiceGlow(SelectSheetSize);
    }

    /// <summary>The texel size of a child of SELECT_PARTS (the choice decoration, system_flow.md 5.3), decoded once.</summary>
    private (int W, int H) SelectSheetSize(int child)
    {
        if (!_selectSizes.TryGetValue(child, out (int W, int H) size))
        {
            try
            {
                size = _data.UiSheetSize(UiSet.SelectParts, _language, child);
            }
            catch (Exception e) when (e is FileNotFoundException or ArgumentOutOfRangeException or NotSupportedException)
            {
                size = (0, 0);          // a source without SELECT_PARTS (the golden slice): the decoration is empty
            }

            _selectSizes[child] = size;
        }

        return size;
    }

    /// <summary>The width argument of the choice texts' <c>Text_Layout</c> (VA 0x9B6F9 / 0xA71DC: 0x1DA, 0x23E in English); the layout does not widen it for these blocks.</summary>
    private int ChoiceWidth() => _language == Language.En ? 574 : 474;

    private void ResetChoice()
    {
        _lines.Clear();
        _choicePhase = 0;
        _cursor = -1;
        _previousCursor = -1;
        _choiceAlpha = 0;
        _choiceFlash = 0;
        _decided = -1;
        _glow.Reset();
        _glowText.Clear();
    }

    /// <summary>The kind of choice that is open (or none).</summary>
    public ChoiceKind ChoiceKind => ChoiceOpen ? (ChoiceKind)VarState.ChoiceMode : ChoiceKind.None;

    /// <summary>The lines of the open choice.</summary>
    public IReadOnlyList<ChoiceLine> ChoiceLines => _lines;

    /// <summary>The highlighted line (-1: none yet); for a two-button prompt the selected button (0 No, 1 Yes).</summary>
    public int ChoiceCursor => _cursor;

    /// <summary>
    /// The choice machine's phase: 0 fade-in, 1 text reveal, 2 selecting, 3 decided. The two-button prompts (<see cref="PromptOpen"/>) have no text reveal; a <c>_SELA</c>'s Yes
    /// goes 4 (fade out), 5 (ask for the save screen), 6 (wait for it), 7 (then 3).
    /// </summary>
    public int ChoicePhase => _choicePhase;

    /// <summary>The line the cursor was on before its last move (u16 0x17506A4), -1 none.</summary>
    public int ChoicePreviousCursor => _previousCursor;

    /// <summary>The choice alpha (u32 0x17506A8): 0 to 1 while fading in, 1 while selecting, falling after the decision.</summary>
    public float ChoiceAlpha => _choiceAlpha;

    /// <summary>The highlight fade (0x17506B4): 0 after a cursor move, +0.1 per frame up to 1.</summary>
    public float ChoiceHighlight => _choiceHighlight;

    /// <summary>The decision flash (0x17506AC): 1 at the decision, -0.02 per frame; the decided phase ends at 0.</summary>
    public float ChoiceFlash => _choiceFlash;

    /// <summary>The flare pulse (0x17506B8) between 0.333 and 1.</summary>
    public float ChoicePulse => _glow.Pulse;

    /// <summary>The choice point number (<c>_SELD</c> argument 1), -1 when absent.</summary>
    public int ChoicePoint => VarState.ChoiceId;

    /// <summary>True while the choice accepts a decision.</summary>
    public bool ChoiceSelectable => ChoiceOpen && _choicePhase == 2;

    /// <summary>
    /// Selects line <paramref name="index"/> (list) or button <paramref name="index"/> (the two-button prompts: 0 No, 1 Yes) and decides it, as if the player had moved the
    /// cursor there and pressed decide. Returns false when the choice is not selectable, the line is locked or the index is out of range.
    /// </summary>
    public bool Choose(int index)
    {
        if (!ChoiceOpen)
        {
            return false;
        }

        if (PromptOpen)
        {
            return ChoosePrompt(index);
        }

        if (_choicePhase < 2)
        {
            // the player's decide in phase 0 / 1 completes the reveal first; a policy may decide in the same frame
            _choiceAlpha = 1f;
            _choicePhase = 2;
        }

        if (_choicePhase != 2 || index < 0 || index >= Math.Max(VarState.ChoiceCount, _lines.Count))
        {
            return false;
        }

        if (index < _lines.Count && _lines[index].Locked)
        {
            return false;
        }

        SetCursor(index);
        Decide();
        return true;
    }

    private void SetCursor(int c)
    {
        if (c != _cursor)
        {
            _previousCursor = _cursor;
            _cursor = c;
            _choiceHighlight = 0f;
            _glow.OnCursorChange();
        }
    }

    private StepResult CmdSelr(in Statement st)
    {
        // VA 0x9936D: the variable side ran in the VM; the host keeps the line text. The first line of a group pushes kind 13.
        int k = VarState.ChoiceCount - 1;
        if (k == 0)
        {
            _lines.Clear();
            Push(HistoryKind.ChoiceLine, st);
        }

        while (_lines.Count < VarState.ChoiceCount)
        {
            _lines.Add(new ChoiceLine());
        }

        if (k >= 0 && k < _lines.Count)
        {
            ChoiceLine line = _lines[k];
            line.Value = k < VarState.ChoiceValues.Count ? (short)VarState.ChoiceValues[k] : 0;
            line.Locked = k < VarState.ChoiceLocked.Count && VarState.ChoiceLocked[k];
            string arg = ArgText(st, 1);
            // "$" + 6 digits of the prefix line (43960 + k for k < 9, 43959 for the tenth) followed by the argument
            int prefix = k < 9 ? 43960 + k : 43959;
            byte[] source = System.Text.Encoding.Latin1.GetBytes($"${prefix:D6}{arg}");
            line.Text = ExpandMessage(source);
        }

        return StepResult.Continue;
    }

    private void OpenChoice(in Statement st)
    {
        EndSceneSkip();
        Wait.Bits |= WaitBits.Choice;
        _choicePhase = 0;
        _cursor = -1;
        _previousCursor = -1;
        _choiceAlpha = 0f;
        _choiceFlash = 0f;
        _choiceHighlight = 0f;
        _choiceHead = 0f;
        _decided = -1;
        _glow.Reset();
        _glowText.Clear();
        if (VarState.ChoiceMode == ChoiceMode.List)
        {
            Wait.WindowTarget = 1;
            // layout of the lines (VA 0x9B6F2 / 0x55051: block 0x14 + k, width 574 en / 474, height 220, leading 9, scale 0.74, bit 20 in a bubble window)
            int window = WindowIndex();
            for (int k = 0; k < _lines.Count; k++)
            {
                var p = new LayoutParams(Width: ChoiceWidth(), Block: 0x14 + k, Window: window, Flags: window < 3 ? 0 : 1 << 20);
                _lines[k].Block = TextLayout.Layout(_lines[k].Text, _language, _fonts, p);
            }

            int n = VarState.ChoiceId;
            bool seen = n >= 0 && n < SystemBlock.ChoiceTakenCount && SystemData.GetChoiceTaken(n) != 0;
            Wait.ReadSel = seen ? 1 : 0;
            if (!seen && Mode.SkipActive != 0 && Config.FastForwardReadOnly != 0)
            {
                // a choice point the player has not seen stops skipping (SELD: 0x56150(1, seen))
                _modeWord &= ~2;
                SkipStoppedByReadRule = true;
            }

            Push(HistoryKind.ChoiceStart, st);
            Sink.UpdateStaging();
            // F1103 is not set for a list: the PS4's list setup (VA 0xA5F0D..0xA5FD1) does not write it (only the two prompt setups at 0xA5E91 / 0xA5EE9 do) and the question
            // stays on the page above the lines; knife sets it for the Wik and two-button kinds only (package K15: it used to be set here, which hides the page now that the draw obeys it)
        }
    }

    private StepResult CmdSeld(in Statement st)
    {
        // scan mode (the re-execution after a load) opens nothing and changes no choice state (vm_variables.md 4.5)
        if (!VarState.ScanMode)
        {
            OpenChoice(st);
        }

        return StepResult.Yield;
    }

    private StepResult CmdSels(in Statement st)
    {
        // scan mode and a spent F1118 leave the mode at None (VarCommands): nothing opens, the script goes on after the yield
        if (VarState.ChoiceMode == ChoiceMode.TwoButtons)
        {
            OpenPrompt();
        }

        return StepResult.Yield;
    }

    private StepResult CmdSela(in Statement st)
    {
        if (VarState.ChoiceMode == ChoiceMode.SavePrompt)
        {
            OpenPrompt();
        }

        return StepResult.Yield;
    }

    /// <summary>
    /// Raised when the save prompt of <c>_SELA</c> was answered Yes and its dialog has faded out: the owner opens the save screen over the story and calls
    /// <see cref="SavePromptScreenClosed"/> when it is closed (saved or not). With no listener the host does not wait for a screen.
    /// </summary>
    public event Action? SavePromptAccepted;

    /// <summary>Raised when a choice is decided: kind, choice point, option (the line's value), cursor.</summary>
    public event Action<ChoiceKind, int, int, int>? ChoiceDecided;

    private void Decide()
    {
        int cursor = _cursor;
        ChoiceKind kind = (ChoiceKind)VarState.ChoiceMode;
        int value = 0;
        if (cursor >= 0 && cursor < VarState.ChoiceValues.Count)
        {
            value = (short)VarState.ChoiceValues[cursor];
        }

        VarCommands.ChoiceStore(VarState, cursor);
        int n = VarState.ChoiceId;
        if (n >= 0 && n < SystemBlock.ChoiceTakenCount)
        {
            SystemData.SetChoiceTaken(n, (byte)(SystemData.GetChoiceTaken(n) | (1 << (value & 7))));
            Sink.OnChoiceTaken(n, value & 7);
        }

        Sound.SystemSePlay(3);
        _decided = cursor;
        _choicePhase = 3;
        _choiceFlash = 1f;
        ChoiceDecided?.Invoke(kind, n, value, cursor);
    }

    /// <summary>The choice code of one frame (VA 0xA6180 / 0xA6940; system_flow.md 5.2).</summary>
    private void UpdateChoice(in InputFrame input, int dt)
    {
        if (PromptOpen)
        {
            UpdatePrompt(input, dt);
            return;
        }

        _choiceHighlight = MathF.Min(1f, _choiceHighlight + 0.1f);
        _glow.Tick(dt);
        switch (_choicePhase)
        {
            case 0:
                _choiceAlpha += ChoiceFadeIn * dt;
                if (_choiceAlpha >= 1f)
                {
                    _choiceAlpha = 1f;
                    _choicePhase = VarState.ChoiceMode == ChoiceMode.List ? 1 : 2;
                }

                break;
            case 1:
            {
                WaitConfig cfg = WaitConfig.From(Config);
                float speed = WaitModel.RevealSpeed(cfg, Wait.ReadSel);
                _choiceHead = speed >= WaitState.Instant ? WaitState.Instant : _choiceHead + (speed * dt);
                bool allDrawn = true;
                foreach (ChoiceLine l in _lines)
                {
                    int last = l.Block is { Records.Length: > 0 } b ? b.Records[^1].Ctr : 0;
                    allDrawn &= _choiceHead >= last + 3f;
                }

                if (allDrawn || (input.Pressed & PadButtons.Decide) != 0 || (input.Pointer.Pressed & PointerButtons.Left) != 0)
                {
                    _choiceHead = WaitState.Instant;
                    _choicePhase = 2;
                }

                break;
            }

            case 2:
                SelectInput(input);
                break;
            case 3:
                _choiceAlpha = MathF.Max(0f, _choiceAlpha - (ChoiceFadeOut * dt));
                _choiceFlash -= ChoiceFlashStep * dt;
                if (_choiceFlash <= 0f)
                {
                    CloseChoice();
                }

                break;
        }
    }

    private void SelectInput(in InputFrame input)
    {
        int count = Math.Max(VarState.ChoiceCount, _lines.Count);
        bool up = (input.Repeated & PadButtons.Up) != 0 || input.Pointer.Wheel > 0;
        bool down = (input.Repeated & PadButtons.Down) != 0 || input.Pointer.Wheel < 0;
        if (up || down)
        {
            int c = _cursor;
            for (int tries = 0; tries < count; tries++)
            {
                c = up ? (c <= 0 ? count - 1 : c - 1) : (c >= count - 1 ? 0 : c + 1);
                if (VarState.ChoiceMode != ChoiceMode.List || !_lines[c].Locked)
                {
                    SetCursor(c);
                    break;
                }
            }
        }

        if (_cursor >= 0 && ((input.Pressed & PadButtons.Decide) != 0 || (input.Pointer.Pressed & PointerButtons.Left) != 0))
        {
            Decide();
        }
    }

    private void CloseChoice()
    {
        _choiceFlash = 0f;
        Wait.Bits &= ~WaitBits.Choice;
        VarState.ChoiceMode = ChoiceMode.None;
        VarState.ChoiceCount = 0;
        VarState.ChoiceValues.Clear();
        VarState.ChoiceLocked.Clear();
        _lines.Clear();
    }
}

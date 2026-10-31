using Tsuki.Engine.Contracts;
using Tsuki.Engine.Flow;

namespace Tsuki.Engine.Game;

/// <summary>What one <see cref="GameHost.Step"/> did (for traces and the headless runner).</summary>
public readonly record struct FrameReport(long Frame, bool ScriptRan, StepEnd? ScriptEnd, string? Advanced, int Statements);

public sealed partial class GameHost
{
    private int _modeWord;
    /// <summary>True once the read-only rule or an unseen choice stopped skipping.</summary>
    public bool SkipStoppedByReadRule { get; private set; }
    private InputFrame _input;
    private long _statementsAtFrameStart;

    /// <summary>The ADV mode words of the current frame (auto, latched skip, the step).</summary>
    public AdvMode Mode { get; private set; } = AdvMode.FromModeWord(0);

    /// <summary>Auto mode is on (bit 0 of the mode word).</summary>
    public bool AutoMode => (_modeWord & 1) != 0;

    /// <summary>The skip latch is on (bit 1 of the mode word).</summary>
    public bool SkipLatch => (_modeWord & 2) != 0;

    /// <summary>The input of the frame being stepped.</summary>
    public InputFrame Input => _input;

    /// <summary>The effects of the last Adv_Update (for traces).</summary>
    public WaitEffects LastUpdate { get; private set; }

    /// <summary>The effects of the last tick_reveal.</summary>
    public WaitEffects LastReveal { get; private set; }

    /// <summary>Sets the auto mode (the mode panel and the shell use it).</summary>
    public void SetAutoMode(bool on) => _modeWord = on ? _modeWord | 1 : _modeWord & ~1;

    /// <summary>Sets the skip latch.</summary>
    public void SetSkipLatch(bool on) => _modeWord = on ? _modeWord | 2 : _modeWord & ~2;

    /// <summary>True while a choice menu or prompt is open (wait bit 0x10).</summary>
    public bool ChoiceOpen => (Wait.Bits & WaitBits.Choice) != 0;

    /// <summary>
    /// Updates the mode words from the frame's input (0x60710, adv_state.md 6 and 8): toggles, the fast-forward hold, the read-only rule
    /// before the frame. Returns the mode of the frame.
    /// </summary>
    private AdvMode ComputeMode(in InputFrame input, WaitConfig cfg)
    {
        PadButtons pressed = input.Pressed;
        ShellActions shell = input.ShellPressed;
        if ((pressed & PadButtons.L1) != 0 || (shell & ShellActions.AutoToggle) != 0)
        {
            _modeWord ^= 1;
        }

        if ((pressed & PadButtons.R1) != 0 || (shell & ShellActions.SkipToggle) != 0)
        {
            _modeWord ^= 2;
        }

        if ((pressed & PadButtons.Cancel) != 0 || (input.Pointer.Pressed & PointerButtons.Right) != 0)
        {
            _modeWord &= ~2;
        }

        // action 25 (cancel, or the shell's H): hide / show the message window (VA 0x61CBF); a movie and a choice use the button themselves
        if (!MovieActive && !ChoiceOpen && ((shell & ShellActions.HideWindow) != 0 || (pressed & PadButtons.Cancel) != 0))
        {
            _windowHidden = !_windowHidden;
        }

        bool ffHold = (input.Held & PadButtons.R2) != 0 || (input.ShellHeld & ShellActions.FastForward) != 0;
        AdvMode mode;
        if (ffHold && !ChoiceOpen && !MovieActive)
        {
            mode = new AdvMode { ModeWord = _modeWord & 0xFFFF, SkipActive = 1, Step = 64, Vsync = 1 };
        }
        else
        {
            mode = AdvMode.FromModeWord(_modeWord);
        }

        if (MovieActive)
        {
            // _TOMP clears skip active; the movie state's enter callback saved and cleared the latch (CmdTomp) and its exit brings it back (EndMovie), wait_model.md 9
            mode.SkipActive = 0;
            mode.Step = 1;
        }

        // the read-only rule before the frame (0x612D0)
        bool r2 = ffHold && ((input.Held & PadButtons.R2) != 0 ? input.AnalogR2 != 0 : true);
        if (mode.SkipActive != 0 && cfg.FastForwardReadOnly != 0 && !LineRead && !r2)
        {
            _modeWord &= ~2;
            mode = AdvMode.FromModeWord(_modeWord);
            SkipStoppedByReadRule = true;
        }

        return mode;
    }

    /// <summary>The "already read" state of the current line (VA 0x75C90).</summary>
    public bool LineRead => Markers.Seen && Markers.IsRead;

    /// <summary>The F words and the model state the wait functions read.</summary>
    private void SyncSound(in AdvMode mode)
    {
        Sound.Skip = mode.SkipActive != 0;
        Sound.FastForward = SkipScene;
    }

    /// <summary>
    /// Runs one 60 Hz frame in the eboot's order (wait_model.md 13): the mode words, Adv_Update (and Script_Step when nothing waits), the
    /// read-only rule, tick_reveal, the layer and transition update, the choice, movie and sound updates, the window and text fades; then
    /// draws into <paramref name="list"/> when it is not null.
    /// </summary>
    public FrameReport Step(in InputFrame input, DisplayList? list = null)
    {
        _frame++;
        _input = input;
        _statementsAtFrameStart = DispatchCount;
        bool scriptRan = false;
        StepEnd? scriptEnd = null;
        string? advanced = null;
        if (Phase == GamePhase.Running)
        {
            WaitConfig cfg = WaitConfig.From(Config);
            WaitInput wi = WaitInput.From(input);
            Wait.AdvKey = wi.AdvKey;
            SyncWaitFromVars();
            Wait.VoiceState = Sound.VoiceState;
            if (SkipScene)
            {
                RunSceneSkip();
            }

            AdvMode mode = ComputeMode(input, cfg);
            Mode = mode;
            SyncSound(mode);

            // 1. Adv_Update, and Script_Step when nothing waits
            if (mode.LogShown == 0)
            {
                WaitEffects eff = WaitModel.AdvUpdate(Wait, wi, cfg, mode, StcfPending);
                LastUpdate = eff;
                advanced = eff.Advanced;
                if (eff.StopVoice > 0)
                {
                    Sound.StopVoiceOnAdvance(F(1123));
                    Wait.VoiceState = Sound.VoiceState;
                }

                if (eff.ScriptStep)
                {
                    scriptRan = true;
                    scriptEnd = RunScript();
                    Wait.VoiceState = Sound.VoiceState;
                }
            }

            // 2. the read-only rule after the update, then tick_reveal
            AdvMode modeR = mode;
            int? n = null;
            bool r2Held = (input.Held & PadButtons.R2) != 0 && input.AnalogR2 != 0;
            if (mode.SkipActive != 0 && cfg.FastForwardReadOnly != 0 && !LineRead && !r2Held && (input.ShellHeld & ShellActions.FastForward) == 0)
            {
                modeR.SkipActive = 0;
                n = Math.Max(0, Math.Min(mode.Vsync, 3));
                _modeWord &= ~2;
                SkipStoppedByReadRule = true;
            }

            SyncWaitFromVars();
            LastReveal = WaitModel.TickReveal(Wait, wi, cfg, modeR, RevealDoneValue, n);
            if (LastReveal.RevealClicked)
            {
                int k = F(823);
                for (int j = 0; j < k - 1 && j < 12; j++)
                {
                    SetF(859 + j, -1);
                }

                foreach (PageMessage m in _page)
                {
                    m.DimTimer = -1;
                }
            }

            // 3. layers and transitions (VA 0x57980), choice, movie, sound, fades
            int step = mode.Step;
            Stage.SkipActive = SkipScene;
            Stage.Update(step);
            if ((Wait.Bits & WaitBits.Fads) != 0 && !Stage.TransitionActive && !Stage.CommitPending)
            {
                Wait.Bits &= ~(WaitBits.Fads | WaitBits.System);
            }

            if (ChoiceOpen)
            {
                UpdateChoice(input, step);
            }

            UpdateMovie(input, step);
            UpdateSound();
            UpdateWindowFades(Math.Max(1, mode.Step));
            UpdateEffects(step);

            // 4. the HUD process (0x53CF0, priority 0x50) runs after the ADV update: the click-wait icon's alpha and anchor (package K34)
            UpdateClickIcon();
        }

        if (list is not null)
        {
            Draw(list);
        }

        return new FrameReport(_frame, scriptRan, scriptEnd, advanced, (int)(DispatchCount - _statementsAtFrameStart));
    }

    private StepEnd RunScript()
    {
        StepEnd end = _vm.Step(this);
        if (end == StepEnd.EndOfScript && Phase == GamePhase.Running)
        {
            Phase = GamePhase.Ended;
            EndReason = EndReason.EndOfScript;
            Sink.OnScriptEnd(EndReason.EndOfScript);
        }
        else if (end == StepEnd.Halted && Phase == GamePhase.Running)
        {
            // a halt without END_ / RTM_ comes from a jump to a missing scene
            Phase = GamePhase.Ended;
            if (EndReason == EndReason.None)
            {
                EndReason = EndReason.EndOfScript;
            }
        }

        return end;
    }

    /// <summary>True while the next-scene skip runs (adv_state.md 5.2, word 0x120A6D2).</summary>
    public bool SkipScene { get; private set; }

    /// <summary>
    /// The "next scene" action (adv_state.md 5.2) after its dialog: the script runs at full speed inside the next <see cref="Step"/> with every wait
    /// cleared, until a scene start (<c>_ATSV</c>), an unread message, a jump, the end of the story, a movie or a choice ends it. Not while a choice is open.
    /// </summary>
    public bool RequestNextScene()
    {
        if (Phase != GamePhase.Running || ChoiceOpen)
        {
            return false;
        }

        SkipScene = true;
        return true;
    }

    /// <param name="resync">
    /// The end sites of the skip (_ATSV 0x96CBD, JUMP 0x9AA49, RDED / RTMN / RTM_ 0x9AA7A, TOMP 0x9ABC4, END_ 0x9B08C, SELD 0x9B20C, SELS / SELA through
    /// 0xB82E0 / 0xB8400) call 0x7E620: the BGM and the loop SEs the skipped handlers only noted in the script-side state start now (adv_state.md 2.5, 5.2;
    /// vm_variables.md 16.1), and the frame's sound update drains the commands. False for a restore, whose own resync (0x7E4E0) follows on a new sound model.
    /// </param>
    private void EndSceneSkip(bool resync = true)
    {
        if (SkipScene)
        {
            SkipScene = false;
            Sound.FastForward = false;
            if (resync)
            {
                Sound.ResyncScriptState();
            }

            SceneSkipEnded?.Invoke();
        }
    }

    /// <summary>Raised when a next-scene skip ends, after the sound model restarted the BGM and the looping effects from the script state.</summary>
    public event Action? SceneSkipEnded;

    /// <summary>The skip loop: waits cleared, the script run again and again, the layer update completing what it started.</summary>
    private void RunSceneSkip()
    {
        Sound.FastForward = true;
        for (int i = 0; i < 200_000 && SkipScene && Phase == GamePhase.Running; i++)
        {
            Wait.Bits = 0;
            Wait.Flags = 0;
            Wait.Timer = 0;
            Wait.Auto = 0;
            Wait.StcfLayers = 0;
            RunScript();
            Stage.Update(64);
            Stage.Update(64);
        }

        EndSceneSkip();
    }

    private void UpdateSound()
    {
        if (!_options.Sound)
        {
            Sound.TakeCommands();
            return;
        }

        // the library's is_playing is true from the play call on: the commands the script produced go to the virtual clock before the update asks
        DrainSound();
        Sound.Vsyncs = 1;
        Sound.Update(1);
        Sound.DuckUpdate();
        DrainSound();
        _audio.Tick();
        Wait.VoiceState = Sound.VoiceState;
    }

    private void DrainSound()
    {
        AudioCommand[] cmds = Sound.TakeCommands();
        if (cmds.Length > 0)
        {
            _audioOut.AddRange(cmds);
            _audio.Process(cmds, _services.ClipFrames, _options.DefaultVoiceFrames);
        }
    }
}

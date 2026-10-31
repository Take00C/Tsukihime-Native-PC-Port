// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/game/focus.rs (Focus::start), knife/src/game/ghost.rs, knife/src/game.rs (FCLN, FCSP, FLNT, FLOP, FLSP, FADE arms)
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game.Effects;
using Tsuki.Engine.Stage;

namespace Tsuki.Engine.Game;

/// <summary>The mode of the focus-line / falling-note effect (<c>_FCLN</c>, <c>_FCSP</c>).</summary>
public enum FocusMode : byte
{
    /// <summary>Off.</summary>
    Off = 0,

    /// <summary>Fading in.</summary>
    FadingIn = 1,

    /// <summary>On.</summary>
    On = 2,

    /// <summary>Fading out.</summary>
    FadingOut = 3,
}

// The screen effects of the interpreter (script.md 4.2, 4.3; knife game/focus.rs and ghost.rs): the focus effect FCLN / FCSP / FLNT / FLOP / FLSP / FCCL
// (manga focus lines 4..33, falling notes 1..3) and the ghosting effect FADE(40) / FADE(41). The state machines, their particles and their draw are in
// Game/Effects (FocusEffect, GhostEffect, package K7); this file keeps the command arms and the F words the effects own.
public sealed partial class GameHost
{
    private FocusEffect _focus = null!;
    private GhostEffect _ghost = null!;

    /// <summary>The focus-line / falling-note effect (state, particles and draw).</summary>
    public FocusEffect Focus => _focus;

    /// <summary>The ghosting (channel split) effect of <c>_FADE(40)</c>.</summary>
    public GhostEffect Ghost => _ghost;

    private void ResetEffects()
    {
        _focus.Reset();
        _ghost.Reset();
    }

    /// <summary>The focus effect's mode.</summary>
    public FocusMode FocusState => _focus.Mode;

    /// <summary>The running focus effect id (0 none): 1..3 falling notes, 4..33 focus lines.</summary>
    public int FocusEffect => _focus.Effect;

    /// <summary>The focus effect's fade level 0..1.</summary>
    public float FocusLevel => _focus.Level;

    /// <summary>The ghosting effect: 4 on, 5 fading out, 0 off.</summary>
    public int GhostMode => _ghost.Mode;

    /// <summary>The ghosting effect's alpha 0..1.</summary>
    public float GhostAlpha => _ghost.Alpha;

    /// <summary>sub_86470(effect, frames): starts <paramref name="effect"/> (frames 0) or stops it fading out over <paramref name="frames"/>.</summary>
    private void StartFocus(int effect, int frames) => _focus.Start(effect, frames);

    private StepResult CmdFocus(in Statement st, Cmd cmd)
    {
        var a = new StageArgs(st);
        NoteLayerChange(st);
        (int effect, int frames) = cmd switch
        {
            Cmd.Fcln => (a.Has(5) ? a.Expr(5) : 0, 0),
            Cmd.Fcsp => (0, 60 * (a.Has(0) ? a.Expr(0) : 0) / 1000),
            Cmd.Flnt => (1, 0),
            Cmd.Flop => (a.Has(0) && a.Expr(0) == 5 ? 2 : 3, 0),
            Cmd.Flsp => (0, 48),
            _ => (0, 0),                                              // FCCL
        };

        // the handlers (0xB7720..0xB79C0) keep the effect in the scene byte +0x19A2, which a restore re-applies (adv_state.md 3.1; knife "stage byte 6569")
        Stage.Scene.Effect19A2 = unchecked((byte)effect);
        StartFocus(effect, frames);
        return StepResult.Continue;
    }

    /// <summary>
    /// History_Restore's common part (adv_state.md 3.1) re-applies the screen effects from the restored state: 0x42DB0(F1100) the FADE mode (knife
    /// game.rs restore_with: <c>ghost.set_mode(F1100, F1120 - 127, F1121)</c> with the wobble marked as wrapped) and 0xA88B0(scene byte +0x19A2, 0) the
    /// focus effect. Called after the variables and the stage are in place; <see cref="ResetEffects"/> has cleared both before.
    /// </summary>
    private void RestoreEffects()
    {
        if (F(1100) == 4)
        {
            _ghost.Restore(F(1120) - 127, F(1121));
        }

        StartFocus(Stage.Scene.Effect19A2, 0);
    }

    private StepResult CmdFade(in Statement st)
    {
        // FADE(40, p, q): F1100 = 4, F1120 = p + 127, F1121 = q, the channel-split effect fades in; FADE(41): F1100 = 0, it fades out
        var a = new StageArgs(st);
        NoteLayerChange(st);
        int mode = a.Has(0) ? a.Expr(0) : 0;
        if (mode == 40)
        {
            bool reset = F(1100) == 0;
            int p = a.Has(1) ? a.Expr(1) : 0;
            int q = a.Has(2) ? a.Expr(2) : 0;
            SetF(1100, 4);
            SetF(1120, p + 127);
            SetF(1121, q);
            _ghost.Start(p, q, reset);
        }
        else if (mode == 41)
        {
            SetF(1100, 0);
            _ghost.SetMode(5, 0, 0);
        }

        return StepResult.Continue;
    }

    private void UpdateEffects(int step)
    {
        int frames = Math.Max(1, step);
        _focus.Step(frames);
        _ghost.Step(frames, F(1120) - 127, F(1121));
    }

    /// <summary>
    /// The screen effects drawn over the composed stage and under a running transition's capture and the message window (knife: the ghost, then the
    /// focus lines unless F963 = 1; the transition's capture sprite lies over both and shows the frame as it was, effects included).
    /// </summary>
    private void DrawScreenEffects(DisplayList list)
    {
        if (_ghost.Active)
        {
            _ghost.Draw(list, Stage.TransitionActive ? 1f - Stage.TransitionProgress : null);
        }

        if (_focus.Active && F(963) != 1)
        {
            _focus.Draw(list);
        }
    }
}

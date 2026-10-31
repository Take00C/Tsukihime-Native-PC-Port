using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;

namespace Tsuki.Engine.Game;

// All stage-command arms of the interpreter (package K3). The layer model itself is StageRuntime (package K2); this file only adds what belongs
// to the interpreter: the history batch bookkeeping (F991), the waits that the layer commands start (_FADS, _STCF), the text-hiding word F1103 and
// the gallery marks of the picture commands.
public sealed partial class GameHost
{
    private static readonly HashSet<string> LayerChangeNames =
    [
        "STCH", "SCH2", "STCC", "STBR", "STCL", "STCP", "STRT", "STZ4", "STRR", "STGS", "STRA", "STNS", "STEC", "SNX2", "SNX3",
        "SQK2", "SQS2", "SHK2", "SHS2", "SSW2", "SSS2", "STMA", "SPMA",
    ];

    private StepResult DispatchStage(in Statement st)
    {
        string name = st.Name.ToString();
        if (LayerChangeNames.Contains(name))
        {
            NoteLayerChange(st);
        }

        switch (name)
        {
            case "FADS":
            {
                // 0x81520: pushes kind 1 unconditionally, then commits
                Push(HistoryKind.LayerBatchStart, st);
                SetF(996, 0);
                SetF(997, 0);
                SetF(985, 0);
                var a = new StageArgs(st);
                int ms = a.Has(1) ? a.Expr(1) : 0;
                int hide = a.Has(3) ? a.Expr(3) : -1;
                if (hide == 1 || (hide == 2 && ms >= 2 && Wait.TextFadeStep >= 0f))
                {
                    SetF(1103, 1);
                }

                Stage.Execute(st);
                CopyCommitted();
                SetF(LayerBatchFlagVar, 0);
                Wait.Bits &= ~WaitBits.System;
                Wait.Bits |= WaitBits.Fads;
                MarkDisplayedPictures();
                return StepResult.Yield;
            }

            case "STCF":
            {
                // wait_model.md 10.2: bit l of the one word 0x1653E30, byte 0x1653E40 + l = mask (absent 0x3F), bit 0x20, a yield. Only this statement's
                // layer is registered: Adv_Update clears the bits itself as the conditions end, so a layer an earlier _STCF named is not waited for again
                // (R1-engine 1: the accumulated set made _STCF(0,8) wait for other layers' minute-long motions)
                var a = new StageArgs(st);
                int l = a.Has(0) ? a.Expr(0) : 0;
                if (l is >= 0 and <= 39)
                {
                    Wait.StcfLayers |= 1UL << l;
                    Wait.StcfMask[l] = (byte)(a.Has(1) ? a.Expr(1) : 0x3F);
                }

                Wait.Bits |= WaitBits.Stcf;
                return StepResult.Yield;
            }

            case "STMA":
            {
                Stage.Execute(st);
                if (Stage.MotionWaitRequested)
                {
                    Stage.MotionWaitRequested = false;
                    SetF(1103, 1);
                }

                return StepResult.Continue;
            }

            case "MSQK" or "SHOK" or "SHAF" or "STPV":
                Stage.Execute(st);
                return StepResult.Continue;

            case "SPFC" or "SPRD" or "SPRC":
                // preload / expression-variant commands: they only queue pictures for loading (script.md 4.3); nothing to do without a loader
                return StepResult.Continue;
        }

        if (!Stage.Execute(st))
        {
            return CmdUnknown(st);
        }

        return StepResult.Continue;
    }

    /// <summary>The 0x4AB80 predicate of <c>_STCF</c>: whether layer <paramref name="layer"/> still has a tween (or motion) selected by <paramref name="mask"/>.</summary>
    private bool StcfPending(int layer, int mask)
    {
        LayerRecord r = Stage.Drawn[layer];
        if ((mask & 0x01) != 0 && r.TwMove.Counter > 0)
        {
            return true;
        }

        if ((mask & 0x02) != 0 && r.TwScale.Counter > 0)
        {
            return true;
        }

        if ((mask & 0x04) != 0 && r.TwRot.Counter > 0)
        {
            return true;
        }

        if ((mask & 0x08) != 0 && r.TwAlpha.Counter > 0)
        {
            return true;
        }

        if ((mask & 0x20) != 0 && r.MotionId > 0 && r.MotionPause == 0)
        {
            MotionRef? m = Stage.Motions.Get(r.MotionId);
            if (m is { } mr && mr.End > mr.Start && mr.Duration > 0 && Stage.Motions.Loop(r.MotionId) == 0
                && MotionMath.MotionTimeMs(r.MotionFrames, r.MotionDivisor) < mr.Duration)
            {
                return true;
            }
        }

        return false;
    }

    // -- the gallery marks of the picture commands ----------------------------------------------------------------------------------

    private StepResult CmdStcg(in Statement st)
    {
        // script.md 4.3: marks a picture as seen without showing it
        string name = ArgText(st, 0);
        int pic = Stage.Catalog.Lookup(name);
        MarkPictureSeen(pic);
        return StepResult.Continue;
    }

    /// <summary>The layer commit marks every displayed picture as seen (gallery.md 2.2; VA 0x7E0F1).</summary>
    private void MarkDisplayedPictures()
    {
        for (int l = 0; l < StageRuntime.LayerCount; l++)
        {
            int pic = Stage.Scene.GetPic(l);
            if (pic >= 0)
            {
                MarkPictureSeen(pic);
            }
        }
    }

    private void MarkPictureSeen(int pic)
    {
        int seen = _flow.PictureSeenIndex(pic);
        if (seen >= 0 && seen < SystemBlock.PictureSeenBits)
        {
            SystemData.SetPictureSeen(seen, true);
        }
    }
}

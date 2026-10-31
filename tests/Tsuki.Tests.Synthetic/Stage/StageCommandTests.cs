using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;

namespace Tsuki.Tests.Synthetic.Stage;

/// <summary>
/// The layer commands one family at a time, with values worked out from docs/formats/layers.md and quake.md (the scene-state fields, the live effects on the
/// drawn record, tweens, motions, shakes). The random comparisons with the oracle and the engine are in <see cref="StageOracleTests"/> and the data tests.
/// </summary>
public sealed class StageCommandTests
{
    private static StageRuntime Stage(IMotionTables? motions = null)
    {
        var rt = new StageRuntime(StageKit.Catalog(), motions ?? StageKit.NoMotions());
        StageKit.Run(rt, "_STCC(-1);");
        return rt;
    }

    private static void Show(StageRuntime rt, string stch)
    {
        StageKit.Run(rt, stch);
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
    }

    [Fact]
    public void StchFillsTheSceneStateAndTheCommitMovesItToTheDrawnRecord()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(7,IMG0001,240,136,500,128,518,,CC);");
        SceneState s = rt.Scene;
        Assert.Equal(2, s.GetPic(7));
        Assert.Equal((320, 181), (s.GetX(7), s.GetY(7)));                // 240 * 640 / 480, 136 * 480 / 360
        Assert.Equal((345, 206, 500, 128, 518), (s.GetAnchorX(7), s.GetAnchorY(7), s.GetScale(7), s.GetAlpha(7), s.GetZ(7)));
        Assert.Equal(-1, rt.Drawn[7].Pic);                               // nothing is visible before the commit
        StageKit.Run(rt, "_FADS(1,0,0);");
        Assert.Equal(2, rt.Committed[7].Pic);
        Assert.Equal(0, rt.Drawn[7].Used);
        rt.Update(1);
        LayerRecord a = rt.Drawn[7];
        Assert.Equal((1, 2, 320f, 181f, 500), (a.Used, a.Pic, a.X, a.Y, (int)a.Scale));
        Assert.Equal(128 * (1f / 255f), a.Alpha, 4);
        Assert.Equal((-345f, -206f), (a.OffX, a.OffY));
        Assert.Equal(518, a.ZOrder);
    }

    [Fact]
    public void AnEmptyPictureNameClearsTheLayerAndAPictureVariantNeedsBothDigits()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0143,0,0,1000,255,5,,CC,1,1);");
        Assert.Equal(7, rt.Scene.GetPic(3));                              // IMG0143_01_01
        StageKit.Run(rt, "_STCH(3,IMG0143,0,0,1000,255,5,,CC,11,11);");
        Assert.Equal(8, rt.Scene.GetPic(3));
        StageKit.Run(rt, "_STCH(3,IMG0143,0,0,1000,255,5,,CC,5,5);");
        Assert.Equal(-1, rt.Scene.GetPic(3));                             // no such variant
        StageKit.Run(rt, "_STCH(3,,0,0);");
        Assert.Equal((-1, 1000, 0), ((int)rt.Scene.GetPic(3), (int)rt.Scene.GetScale(3), (int)rt.Scene.GetAnchorX(3)));
    }

    [Fact]
    public void StgsRunsAnAlphaTweenToItsEndValue()
    {
        StageRuntime rt = Stage();
        Show(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_STGS(3,0,255,1000,0);");
        LayerRecord a = rt.Drawn[3];
        Assert.Equal(1f, a.Alpha);
        Assert.Equal(-1f, a.AlphaDelta);
        Assert.Equal(new Tween(0, 0x1000, -68), a.TwAlpha);                // 1000 ms = 60 frames, 4096 / 60 = 68
        Assert.Equal(255, rt.Scene.GetAlpha(3));
        for (int i = 0; i < 30; i++)
        {
            rt.Update(1);
        }

        float p = (4096 - (68 * 30)) / 4096f;
        Assert.Equal(p, a.TwAlpha.Counter / 4096f, 5);
        Assert.Equal(a.Alpha + a.AlphaCur, 1f - p, 3);                     // linear: the drawn alpha climbs from 0 to 1
        for (int i = 0; i < 40; i++)
        {
            rt.Update(1);
        }

        Assert.Equal(0, a.TwAlpha.Counter);
        Assert.Equal(1f, a.Alpha + a.AlphaCur);
    }

    [Fact]
    public void Stz4AndStrtAndStclSetTheEndValueAtOnceAndTweenTheOffset()
    {
        StageRuntime rt = Stage();
        Show(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_STZ4(3,600,700,1400,1300,500,3,1);");
        LayerRecord a = rt.Drawn[3];
        Assert.Equal((1400, 1300, 1), (a.ZoomX, a.ZoomY, (int)a.ZoomPivot));
        Assert.Equal(new Tween(3, 0x1000, -136), a.TwZoom);                // 500 ms = 30 frames
        Assert.Equal(-0.8000000715255737, a.ZoomDx, 12);                   // (600 - 1400) / 1000 as the engine rounds it
        Assert.Equal(1400, rt.Scene.GetZoomX(3));
        (float zx, _) = Projection.ZoomTween(a);
        Assert.Equal(600f, zx, 1);

        StageKit.Run(rt, "_STRT(3,0,9000,500,2);");
        Assert.Equal((9000L, (short)-9000, (short)-9000), (a.Rot, a.RotDelta, a.RotCur));
        Assert.Equal(new Tween(2, 0x1000, -136), a.TwRot);
        Assert.Equal(9000L, rt.Scene.GetRot(3));

        StageKit.Run(rt, "_STCL(0,3,40,30,400,250,500,6);");
        Assert.Equal((533f, 333f, -480f, -293f), (a.X, a.Y, a.MoveDx, a.MoveDy));
        Assert.Equal((533, 333), (rt.Scene.GetX(3), rt.Scene.GetY(3)));
        for (int i = 0; i < 200; i++)
        {
            rt.Update(1);
        }

        Assert.Equal((0, 0, 0), (a.TwZoom.Counter, a.TwRot.Counter, a.TwMove.Counter));
        Assert.Equal(1400f, Projection.ZoomTween(a).X);
    }

    [Fact]
    public void AZeroDurationEndsTheTweenAtOnce()
    {
        StageRuntime rt = Stage();
        Show(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_STGS(3,255,40,0,3);");
        Assert.Equal(40 / 255f, rt.Drawn[3].Alpha, 6);
        Assert.Equal((0f, 0f, 0), (rt.Drawn[3].AlphaDelta, rt.Drawn[3].AlphaCur, (int)rt.Drawn[3].TwAlpha.Counter));
    }

    [Fact]
    public void StccClearsTheSceneAndTheDrawnRecordsMotionAngleCropAndSttr()
    {
        StageRuntime rt = Stage();
        Show(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_STRR(3,1500);");
        StageKit.Run(rt, "_STTR(3,4,1);");
        StageKit.Run(rt, "_SBAS(3,2);");
        Assert.Equal((short)(3 | (1 << 12)), rt.Drawn[3].Sttr == -1 ? (short)-1 : (short)rt.Drawn[3].Sttr);
        Assert.Equal(1500, rt.Scene.GetAngle(3));
        Assert.Equal(0, rt.Drawn[3].Angle);                                // STRR does not touch the drawn record
        StageKit.Run(rt, "_STCC(3);");
        Assert.Equal((-1, 1000, 255, -1, 0), ((int)rt.Scene.GetPic(3), (int)rt.Scene.GetScale(3), (int)rt.Scene.GetAlpha(3), rt.Scene.GetParent(3), (int)rt.Scene.GetAngle(3)));
        Assert.Equal(-1, rt.Drawn[3].Sttr);
        Assert.Equal(2, rt.Drawn[3].Pic);                                  // the picture stays until the next commit
    }

    [Fact]
    public void QuakesStartAtOnceAndStchAndSch2StopThem()
    {
        StageRuntime rt = Stage();
        Show(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_SQK2(3,5,5,,-1);");
        StageKit.Run(rt, "_SSW2(3,7,15,1000,2000,1,-1);");
        Assert.Equal((1, 3), (rt.Drawn[3].Slots[0].Kind, rt.Drawn[3].Slots[1].Kind));
        Assert.Equal((1, 3), ((int)rt.Scene.GetShakeKind(3, 0), (int)rt.Scene.GetShakeKind(3, 1)));
        for (int i = 0; i < 5; i++)
        {
            rt.Update(1);
        }

        Assert.NotEqual(0, rt.Drawn[3].Slots[0].Elapsed);
        StageKit.Run(rt, "_SCH2(3,IMG0002,,,,,,,CC);");                    // the review's finding: SCH2 resets the drawn slots too
        Assert.Equal((0, 0, 0), (rt.Drawn[3].Slots[0].Kind, rt.Drawn[3].Slots[1].Kind, rt.Drawn[3].Slots[0].Elapsed));

        StageKit.Run(rt, "_SHK2(3,5,5,300,-1);");
        Assert.Equal(2, rt.Drawn[3].Slots[1].Kind);
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136);");
        Assert.Equal(0, rt.Drawn[3].Slots[1].Kind);
        StageKit.Run(rt, "_SQK2(3,5,5,,-1);");
        StageKit.Run(rt, "_STCC(3);");                                       // _STCC clears the scene copy only
        Assert.Equal(1, rt.Drawn[3].Slots[0].Kind);
        Assert.Equal(0, rt.Scene.GetShakeKind(3, 0));
        StageKit.Run(rt, "_SQS2(-1);");
        Assert.Equal(0, rt.Drawn[3].Slots[0].Kind);
    }

    [Fact]
    public void AllLayerQuakesTargetUsedLayersWithZAboveZeroOrLayersWithAScenePicture()
    {
        StageRuntime rt = Stage();
        Show(rt, "_STCH(0,BLACK,240,136,1000,255,2,,CC);");
        Show(rt, "_STCH(2,IMG0001,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_STCH(5,IMG0002,240,136,1000,255,60,,CC);");     // a scene picture, not committed
        StageKit.Run(rt, "_SQK2(-1,3,3,,-1);");
        int[] hit = Enumerable.Range(0, 40).Where(l => rt.Drawn[l].Slots[0].Kind == 1).ToArray();
        Assert.Equal([0, 2, 5], hit);
        StageKit.Run(rt, "_SHK2(-1,3,3,,-1);");
        Assert.Equal([0, 2, 5], Enumerable.Range(0, 40).Where(l => rt.Drawn[l].Slots[1].Kind == 2).ToArray());
    }

    [Fact]
    public void ScanModeSuppressesTimedShakesAndShocksButEndlessOnesStart()
    {
        StageRuntime rt = Stage();
        rt.ScanMode = true;
        StageKit.Run(rt, "_SQK2(3,5,5,,600);");
        StageKit.Run(rt, "_SHOK(0,3,100,10,0);");
        Assert.Equal(0, rt.Drawn[3].Slots[0].Kind);
        rt.Update(1);
        Assert.Equal(0f, rt.Shock.Y);
        StageKit.Run(rt, "_SQK2(3,5,5,,-1);");
        Assert.Equal(1, rt.Drawn[3].Slots[0].Kind);
    }

    [Fact]
    public void StraStartsFadesAndEndsTheHazeCounters()
    {
        StageRuntime rt = Stage();
        Show(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_STRA(3,5,500);");
        LayerRecord a = rt.Drawn[3];
        Assert.Equal((5, 0, 30), (a.Stra, (int)a.StraPhase, (int)a.StraFadeIn));
        for (int i = 0; i < 29; i++)
        {
            rt.Update(1);
        }

        Assert.Equal(30, a.StraFadeIn);
        rt.Update(1);
        Assert.Equal((0, 30), ((int)a.StraFadeIn, (int)a.StraPhase));
        StageKit.Run(rt, "_STRA(3,0,300);");                                  // fade-out: the effect id stays until it ends
        Assert.Equal((5, 18, 18), (a.Stra, (int)a.StraFadeTotal, (int)a.StraFadeLeft));
        for (int i = 0; i < 18; i++)
        {
            rt.Update(1);
        }

        Assert.Equal((0, 0), (a.Stra, (int)a.StraFadeTotal));
    }

    [Fact]
    public void SttrSetsTheRuleOfAUsedLayerAndLoadsTheRuleSlot()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STTR(3,4,1);");
        Assert.Equal((short)(3 | (1 << 12)), rt.Scene.GetSttr(3));
        Assert.Equal(-1, rt.Drawn[3].Sttr);                                  // the layer is not in use yet
        Assert.Equal(3, rt.RuleSlot);
        Show(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");              // STCH resets the layer's rule
        Assert.Equal(-1, rt.Scene.GetSttr(3));
        StageKit.Run(rt, "_STTR(3,26);");
        Assert.Equal(25, rt.Drawn[3].Sttr);
        StageKit.Run(rt, "_STTR(3,0);");
        Assert.Equal(-1, rt.Drawn[3].Sttr);
        Assert.Equal(-1, rt.RuleSlot);
    }

    [Fact]
    public void MotionRecordsFollowStmaAndSpma()
    {
        StageRuntime rt = Stage(StageKit.Motions());
        StageKit.Run(rt, "_STMA(3,2,,3);");
        Assert.Equal((2, 3, 0, 0), (rt.Scene.GetMotionId(3), rt.Scene.GetMotionDivisor(3), (int)rt.Scene.GetMotionFrames(3), (int)rt.Scene.GetMotionPause(3)));
        Assert.Equal((2, 3), (rt.Drawn[3].MotionId, rt.Drawn[3].MotionDivisor));
        Assert.True(rt.MotionWaitRequested);                                  // w absent and the configuration byte is 0
        StageKit.Run(rt, "_SPMA(1);");
        Assert.Equal((1, 1), ((int)rt.Scene.GetMotionPause(3), (int)rt.Drawn[3].MotionPause));
        StageKit.Run(rt, "_SPMA(2);");
        Assert.Equal(0, rt.Scene.GetMotionPause(3));
        StageKit.Run(rt, "_STMA(3,0,,5);");                                    // id 0 stops the motion and keeps the divisor
        Assert.Equal((0, 3), (rt.Drawn[3].MotionId, rt.Drawn[3].MotionDivisor));
        StageKit.Run(rt, "_STMA(3,101960);");                                  // above 32767: the i16 id is negative, no motion
        Assert.Equal(-29112, rt.Drawn[3].MotionId);
    }

    [Fact]
    public void SgmaWritesTheDrawnStateIntoAKeyframeOfTheSessionTable()
    {
        StageRuntime rt = Stage(StageKit.Motions());
        Show(rt, "_STCH(3,IMG0001,240,136,1000,128,50,,CC);");
        MotionRef m = rt.Motions.Get(3)!.Value;
        StageKit.Run(rt, "_SGMA(3,4,1);");                                     // motion 4 uses the keys after the end of motion 3
        int key = m.End + 1;
        Assert.Equal(240, rt.Motions.Val(key, 3));                             // x 320 * 480 / 640
        Assert.Equal(136, rt.Motions.Val(key, 4));
        Assert.Equal(128, rt.Motions.Val(key, 5));
        Assert.Equal(1000, rt.Motions.Val(key, 9));
        Assert.True(rt.SceneDirty);
    }

    [Fact]
    public void ASourceLayerCopiesItsStateAndTakesOverItsReferences()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,100,50,800,200,70,,CC);");
        StageKit.Run(rt, "_SBAS(6,3);");
        StageKit.Run(rt, "_STCH(4,IMG0002,0,0,1000,255,5,,CC,,,3);");     // layer 4 masked by 3
        StageKit.Run(rt, "_SCH2(5,IMG0003,,,,,,,CC,,,,,,,,,,3);");          // source layer 3
        SceneState s = rt.Scene;
        Assert.Equal((133, 66, 800, 200, 70), (s.GetX(5), s.GetY(5), s.GetScale(5), s.GetAlpha(5), s.GetZ(5)));
        Assert.Equal(5, s.GetParent(6));                                      // the children of the source follow the new layer
        Assert.Equal(5, s.GetMask(4));
        Assert.Equal(4, s.GetPic(5));
    }

    [Fact]
    public void TheMessageQuakeAndTheShockRunInsideTheUpdate()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_MSQK(300,5,5);");
        rt.Update(1);
        Assert.True(rt.MessageQuake.Active);
        Assert.NotEqual(0f, rt.MessageQuake.X);
        for (int i = 0; i < 20; i++)
        {
            rt.Update(1);
        }

        Assert.False(rt.MessageQuake.Active);
        StageKit.Run(rt, "_SHOK(0,3,100,10,0);");
        rt.Update(1);
        Assert.NotEqual(0f, rt.Shock.Y);
        StageKit.Run(rt, "_SHAF();");
        rt.Update(1);
        Assert.Equal(0f, rt.Shock.Y);
    }

    [Fact]
    public void StcfIsAcceptedAsAStageCommandButChangesNothingOnTheStage()
    {
        // the wait word is the interpreter's (GameHost.Stage.cs; ChoiceStageSoundTests): the stage only has to know the name
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCF(3,8);");
        StageKit.Run(rt, "_STCF(40,8);");
        Assert.Equal(0, rt.Drawn[3].TwAlpha.Counter);
    }
}

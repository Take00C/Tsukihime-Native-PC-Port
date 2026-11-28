using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;

namespace Tsuki.Tests.Synthetic.Stage;

/// <summary>
/// The display list the stage builds: draw order, quad geometry from the layer matrix, effect word and vertex alpha, the colour-grade key, clip windows and
/// mask offsets, the shake and shock translations, and the <c>_FADS</c> start and frame commands (docs/formats/layers.md 7, 9; compositing.md 5, 6, 7).
/// </summary>
public sealed class StageDrawTests
{
    /// <summary>Every picture is one 1920 x 1080 tile of archive 0, entry = picture id.</summary>
    private sealed class FullScreen : IPictureLayoutSource
    {
        public PictureLayout? Get(int pic, int language) =>
            new(1920, 1080, [new PictureFileLayout(0, pic, 0, 0, [new TileQuad(0, 0, 0, 1920, 1080, 0, 0, 1, 1)])]);
    }

    private static StageRuntime Stage()
    {
        var rt = new StageRuntime(StageKit.Catalog(), StageKit.NoMotions()) { Layouts = new FullScreen() };
        StageKit.Run(rt, "_STCC(-1);");
        StageKit.Run(rt, "_STCH(0,BLACK,240,136,1000,255,2,,CC,,,);");
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        return rt;
    }

    private static DrawCmd[] Frame(StageRuntime rt, out StageDrawInfo info)
    {
        var list = new DisplayList();
        list.Reset(0);
        info = rt.Draw(list);
        return list.Commands.ToArray();
    }

    private static int PicOf(DrawCmd c) => c.Texture.B;

    [Fact]
    public void ABackgroundCoversTheFrame()
    {
        StageRuntime rt = Stage();
        DrawCmd[] cmds = Frame(rt, out StageDrawInfo info);
        DrawCmd q = Assert.Single(cmds, c => c.Kind == DrawKind.TexturedQuad);
        Assert.Equal(1, info.LayersDrawn);
        Assert.Equal(TextureKind.PictureTile, q.Texture.Kind);
        Assert.Equal((0, 0, 0), (q.Texture.A, q.Texture.B, q.Texture.C));
        Assert.InRange(q.V0.X, -0.01f, 0.01f);
        Assert.InRange(q.V0.Y, -0.01f, 0.01f);
        Assert.InRange(q.V3.X, 1919.99f, 1920.01f);
        Assert.InRange(q.V3.Y, 1079.99f, 1080.01f);
        Assert.Equal((0f, 0f, 1f, 1f), (q.V0.U, q.V0.V, q.V3.U, q.V3.V));
        Assert.Equal(0xFFFFFFFFu, q.V0.Rgba);
        Assert.Equal(0, q.EffectWord);
        Assert.Equal(1, q.BlendState);
    }

    [Fact]
    public void LayersDrawBackToFrontByZThenLayerNumber()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_STCH(2,IMG0002,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_STCH(4,IMG0003,240,136,1000,255,10,,CC);");
        StageKit.Run(rt, "_STCH(5,IMG0004,240,136,1000,255,0,,CC);");
        StageKit.Run(rt, "_STCH(6,IMG0143,240,136,1000,0,100,,CC);");
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        int[] order = Frame(rt, out _).Where(c => c.Kind == DrawKind.TexturedQuad).Select(PicOf).ToArray();
        // layer 0 (BLACK, z 2) is picture 0; z 10: IMG0003 = 4; z 50 ties by layer number: layer 2 = IMG0002 = 3, layer 3 = IMG0001 = 2; z 0 and alpha 0 are not drawn
        Assert.Equal([0, 4, 3, 2], order);
        Assert.Equal([0, 4, 2, 3], rt.DrawnLayers);
    }

    [Fact]
    public void AMaskedLayerIsDrawnInsideItsMaskLayersPassAndOnlyWhenItComesAfterIt()
    {
        // layer 2 is masked by layer 3 (STCH a12)
        StageRuntime above = Stage();
        StageKit.Run(above, "_STCH(3,IMG0002,240,136,1000,255,10,,CC);");
        StageKit.Run(above, "_STCH(2,IMG0001,240,136,1000,255,50,,CC,,,3);");
        StageKit.Run(above, "_STCH(4,IMG0003,240,136,1000,255,30,,CC);");
        StageKit.Run(above, "_FADS(1,0,0);");
        above.Update(1);
        Frame(above, out _);
        // the mask layer (z 10) is drawn, then the layers it masks (z 50, drawn in its pass), the layer at z 30 stays between them in z order but the masked one follows its mask layer
        Assert.Equal([0, 3, 2, 4], above.DrawnLayers);

        StageRuntime below = Stage();
        StageKit.Run(below, "_STCH(3,IMG0002,240,136,1000,255,60,,CC);");
        StageKit.Run(below, "_STCH(2,IMG0001,240,136,1000,255,50,,CC,,,3);");
        StageKit.Run(below, "_FADS(1,0,0);");
        below.Update(1);
        Frame(below, out _);
        // the masked layer lies below its mask layer in z order: the engine looks for masked layers after the mask layer only, so it is never drawn (0x5A8E1)
        Assert.Equal([0, 3], below.DrawnLayers);
    }

    [Fact]
    public void TheEffectWordAndTheVertexAlphaFollowStbrAndStgs()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,128,50,,CC);");
        StageKit.Run(rt, "_STBR(3,11,,1);");
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        DrawCmd q = Frame(rt, out _).Single(c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == 2);
        Assert.Equal(0x10B, q.EffectWord);
        Assert.Equal(EffectBlendState(0x10B), q.BlendState);
        Assert.InRange(q.V0.Rgba >> 24, 127u, 128u);
        Assert.Equal(0xFFFFFFu, q.V0.Rgba & 0xFFFFFF);

        // a timed fade-in: half way the alpha is between the start and the end value
        StageKit.Run(rt, "_STGS(3,0,255,1000,0);");
        for (int i = 0; i < 30; i++)
        {
            rt.Update(1);
        }

        q = Frame(rt, out _).Single(c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == 2);
        Assert.InRange(q.V0.Rgba >> 24, 120u, 135u);
    }

    private static int EffectBlendState(int word)
    {
        // the state of effect 11 with the 0x100 flag: effect 11 is not in the flag set, so blending stays on (state 1)
        return word == 0x10B ? 1 : 0;
    }

    [Fact]
    public void TheColourGradeKeyFollowsStecAndSnx()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_STEC(3,23);");
        StageKit.Run(rt, "_SNX2(3,-40);");
        StageKit.Run(rt, "_STCH(4,IMG0002,240,136,1000,255,60,,CC);");
        StageKit.Run(rt, "_STEC(4,1,0x123456);");
        StageKit.Run(rt, "_STCH(5,IMG0003,240,136,1000,255,70,,CC);");
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        DrawCmd[] cmds = Frame(rt, out _).Where(c => c.Kind == DrawKind.TexturedQuad).ToArray();
        Assert.Equal(0, cmds.Single(c => PicOf(c) == 0).Texture.D);
        Assert.Equal(new ColourGrade(23, -40, 0, 0), GradeKeys.Resolve(cmds.Single(c => PicOf(c) == 2).Texture.D));
        Assert.Equal(new ColourGrade(1, 0, 0, 0x123456), GradeKeys.Resolve(cmds.Single(c => PicOf(c) == 3).Texture.D));
        Assert.Equal(0, cmds.Single(c => PicOf(c) == 4).Texture.D);
        Assert.Equal(GradeKeys.Intern(new ColourGrade(23, -40, 0, 0)), cmds.Single(c => PicOf(c) == 2).Texture.D);
    }

    [Fact]
    public void AGradeChangeOnAShownLayerWaitsForTheNextPictureLoadUnlessRegradingIsOn()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        StageKit.Run(rt, "_STEC(3,44);");
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        Assert.Equal(0, Frame(rt, out _).Single(c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == 2).Texture.D);
        rt.RegradeOnCommit = true;
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        Assert.Equal(44, GradeKeys.Resolve(Frame(rt, out _).Single(c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == 2).Texture.D).Mode);
    }

    [Fact]
    public void ACropRectangleIsAClipWindowForItsLayerAndForTheLayersMaskedByIt()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,100,50,600,400);");
        StageKit.Run(rt, "_STCH(4,IMG0002,0,0,1000,255,60,,CC,,,3);");
        StageKit.Run(rt, "_STCH(5,IMG0003,0,0,1000,255,70,,CC);");
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        DrawCmd[] cmds = Frame(rt, out _).Where(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0).ToArray();     // target 1 holds the window's mask (package K7)
        DrawCmd window = cmds.Single(c => PicOf(c) == 2);
        DrawCmd masked = cmds.Single(c => PicOf(c) == 3);
        Assert.Equal(new PixelRect(100, 50, 600, 400), window.Clip);
        Assert.Equal(window.Clip, masked.Clip);
        Assert.Null(cmds.Single(c => PicOf(c) == 0).Clip);
        Assert.Null(cmds.Single(c => PicOf(c) == 4).Clip);

        // the masked layer sits at the window's origin plus its own position: the mask term (crop x * 640 / 1920, crop y * 360 / 1080) shifts its position
        Affine m = Projection.FrameMatrix(rt.Drawn, 4, StageKit.Catalog(), (0f, 0f), (100 * 640f / 1920f, 50 * 360f / 1080f));
        (double ex, double ey) = m.Apply(0, 0);
        Assert.Equal(ex, masked.V0.X, 2);
        Assert.Equal(ey, masked.V0.Y, 2);
    }

    private static DrawCmd[] OnTarget(DrawCmd[] cmds, byte target) => cmds.Where(c => c.Target == target && c.Kind != DrawKind.Clear).ToArray();

    [Fact]
    public void AnInsetOnAWindowFadesItsFourEdgesThroughTheGradationTexture()
    {
        // crop (100, 50, 600, 400), a16 = 60 (the inset), a17 = 0: knife ClipRect::soft. r = 60 / 2 = 30 (960 units) = 60 px on every side (the window is inside the screen),
        // the radius class is the first of 10, 32, 40, 64, 128, 256 above 30: 32, BORDER_PARTS child 1 (64 x 64 texels, 32 per quadrant)
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,100,50,600,400,60,0);");
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        DrawCmd[] cmds = Frame(rt, out _);
        DrawCmd window = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 2);
        Assert.Equal(new PixelRect(100, 50, 600, 400), window.Clip);               // the scissor is not shrunk in this mode
        Assert.Equal(StageRuntime.SoftTarget, window.Fx.Mask);

        DrawCmd[] soft = OnTarget(cmds, StageRuntime.SoftTarget);
        Assert.Equal(1, cmds.Count(c => c.Kind == DrawKind.Clear && c.Target == StageRuntime.SoftTarget));
        Assert.Equal(8, soft.Count(c => c.Kind == DrawKind.TexturedQuad));
        Assert.Equal(1, soft.Count(c => c.Kind == DrawKind.FillQuad));
        Assert.All(soft.Where(c => c.Kind == DrawKind.TexturedQuad), c => Assert.Equal(TextureRef.ForUiTile(UiSet.BorderParts, Tsuki.Engine.Contracts.Language.Ja, 1, -1), c.Texture));
        Assert.All(soft, c => Assert.Equal(58, c.EffectWord));                      // the quads set the mask's alpha
        DrawCmd topLeft = soft[0];
        Assert.Equal((100f, 50f, 160f, 110f), (topLeft.V0.X, topLeft.V0.Y, topLeft.V3.X, topLeft.V3.Y));
        Assert.Equal((0f, 0f, 0.5f, 0.5f), (topLeft.V0.U, topLeft.V0.V, topLeft.V3.U, topLeft.V3.V));
        DrawCmd fill = soft.Single(c => c.Kind == DrawKind.FillQuad);
        Assert.Equal((160f, 110f, 640f, 390f), (fill.V0.X, fill.V0.Y, fill.V3.X, fill.V3.Y));
        DrawCmd topEdge = soft[4];
        Assert.Equal((160f, 50f, 640f, 110f), (topEdge.V0.X, topEdge.V0.Y, topEdge.V3.X, topEdge.V3.Y));
        Assert.Equal(31f / 64f, topEdge.V0.U, 5);                                    // the 1-texel strip beside the centre column, stretched along the edge
        Assert.Equal(0.5f, topEdge.V3.U, 5);
    }

    [Fact]
    public void ASideOffTheScreenOrAWindowAsWideAsTheScreenGetsNoFadeThere()
    {
        // the window starts above the screen (y = -300): the top fade lies off screen, the other three sides fade; r = 90 / 2 = 45 -> 90 px, radius class 64
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,160,-300,888,1650,90,0);");
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        DrawCmd[] soft = OnTarget(Frame(rt, out _), StageRuntime.SoftTarget);
        DrawCmd fill = soft.Single(c => c.Kind == DrawKind.FillQuad);
        // the bottom (y + h = 1350) is below the screen too: only the left and right edges fade
        Assert.Equal((250f, -300f, 958f, 1350f), (fill.V0.X, fill.V0.Y, fill.V3.X, fill.V3.Y));
        Assert.Equal(2, soft.Count(c => c.Kind == DrawKind.TexturedQuad));
        Assert.All(soft.Where(c => c.Kind == DrawKind.TexturedQuad), c => Assert.Equal(3, c.Texture.C));        // child 3: the 128-texel texture of the 64-unit class

        StageRuntime full = Stage();
        StageKit.Run(full, "_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,0,0,1920,1080,30,0);");
        StageKit.Run(full, "_FADS(1,0,0);");
        full.Update(1);
        Assert.Empty(OnTarget(Frame(full, out _), StageRuntime.SoftTarget));
    }

    [Fact]
    public void TheRidgeModesShrinkTheScissorAndDrawAColouredRidgeInsteadOfAMask()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,100,50,600,400,20,2);");
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        DrawCmd[] cmds = Frame(rt, out _);
        DrawCmd window = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 2);
        Assert.Equal(new PixelRect(120, 70, 560, 360), window.Clip);               // shrunk by the inset on every side whose extent is under the screen
        Assert.Equal(0, window.Fx.Mask);
        Assert.Empty(OnTarget(cmds, StageRuntime.SoftTarget));
        DrawCmd[] ridge = cmds.Where(c => c.Target == 0 && c.Clip == window.Clip && (c.Kind == DrawKind.FillQuad || c.Texture.A == (int)UiSet.BorderParts)).ToArray();
        Assert.NotEmpty(ridge);
        Assert.Contains(ridge, c => c.Kind == DrawKind.TexturedQuad && c.V0.Rgba == 0xFFFFFFFFu);                // mode 2: white
        Assert.All(ridge, c => Assert.Equal(1, c.BlendState));
    }

    [Fact]
    public void AnInvisibleCropWindowLeavesItsLayersAnEmptyMask()
    {
        // issue #16 review: the PS4 lays a window's group over the screen through the destination alpha that only the window's own mask pass writes
        // (0x5A5E0 -> 0x51060 with esi = 1, effect 58), at the window's alpha; at alpha 0 that pass draws nothing, so the group shows nothing. A crop rectangle
        // takes the same path: the crop twin of StageEffectTests.AWindowThatFadedOutLeavesItsLayersAnEmptyMask
        DrawCmd[] Held(int alpha)
        {
            StageRuntime rt = Stage();
            StageKit.Run(rt, $"_STCH(3,IMG0001,240,136,1000,{alpha},50,,CC,,,,0,0,1920,1080);");
            StageKit.Run(rt, "_STCH(4,IMG0002,0,0,1000,255,60,,CC,,,3);");
            StageKit.Run(rt, "_FADS(1,0,0);");
            rt.Update(1);
            return Frame(rt, out _);
        }

        DrawCmd[] cmds = Held(0);
        int clear = Array.FindIndex(cmds, c => c.Kind == DrawKind.Clear && c.Target == StageRuntime.MaskTarget);
        Assert.True(clear >= 0, "the mask target is cleared for the layers the window holds");
        Assert.Empty(OnTarget(cmds, StageRuntime.MaskTarget));                                       // no silhouette goes into it
        Assert.DoesNotContain(cmds, c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == 2);           // nor is the window drawn
        int held = Array.FindIndex(cmds, c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == 3);
        Assert.True(held > clear);
        Assert.Equal(StageRuntime.MaskTarget, cmds[held].Fx.Mask);                                   // the held layer is masked by the empty target

        // down to the last step above 0 the silhouette carries the window's alpha: no jump between 1 / 255 and 0
        DrawCmd faint = Assert.Single(OnTarget(Held(1), StageRuntime.MaskTarget), c => PicOf(c) == 2);
        Assert.Equal((58, 0x01FFFFFFu), (faint.EffectWord, faint.V0.Rgba));
    }

    [Fact]
    public void AnInvisibleRidgeWindowDrawsNoRidge()
    {
        // the PS4 draws a window's ridge pieces into the group's offscreen list, before its capture (0x5A5E0): they reach the screen through the same
        // destination alpha as the layers the window holds, so a window at alpha 0 shows no ridge either
        DrawCmd[] Ridge(int alpha, bool holds)
        {
            StageRuntime rt = Stage();
            StageKit.Run(rt, $"_STCH(3,IMG0001,240,136,1000,{alpha},50,,CC,,,,100,50,600,400,20,2);");
            if (holds)
            {
                StageKit.Run(rt, "_STCH(4,IMG0002,0,0,1000,255,60,,CC,,,3);");
            }

            StageKit.Run(rt, "_FADS(1,0,0);");
            rt.Update(1);
            var scissor = new PixelRect(120, 70, 560, 360);
            return Frame(rt, out _).Where(c => c.Target == 0 && c.Clip == scissor && (c.Kind == DrawKind.FillQuad || c.Texture.Kind == TextureKind.UiTile)).ToArray();
        }

        Assert.NotEmpty(Ridge(255, holds: true));
        Assert.NotEmpty(Ridge(1, holds: true));
        Assert.Empty(Ridge(0, holds: true));
        Assert.Empty(Ridge(0, holds: false));
    }

    [Fact]
    public void ALayerHeldByASoftWindowIsMaskedByTheSilhouetteTimesTheWindowEdge()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,100,50,600,400,60,0);");
        StageKit.Run(rt, "_STCH(4,IMG0002,0,0,1000,255,60,,CC,,,3);");
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        DrawCmd[] cmds = Frame(rt, out _);
        DrawCmd held = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 3);
        Assert.Equal(StageRuntime.MaskTarget, held.Fx.Mask);
        // target 1: cleared, the window's silhouette, then the nine pieces of the soft mask that narrow it
        DrawCmd[] mask = OnTarget(cmds, StageRuntime.MaskTarget);
        Assert.Equal(1 + 9, mask.Length);
        Assert.Equal(58, mask[0].EffectWord);
        Assert.All(mask.Skip(1), c => Assert.Equal(0x80, c.BlendState));          // BlendStates.MaskMultiply
    }

    // --- turned crop windows (STCH a19 / STRR angle, +0x15C): the PS4's 0x4C720 and 0x4DC60 turn the window's mask and ridge pieces about the crop centre ---

    private static void Near((float X, float Y) want, Vertex v, float tol = 0.15f)
    {
        Assert.InRange(v.X, want.X - tol, want.X + tol);
        Assert.InRange(v.Y, want.Y - tol, want.Y + tol);
    }

    // p turned about c by g / 65536 of a turn, clockwise on the screen for positive g (0xDEC90 with flags 0x20)
    private static (float X, float Y) TurnAbout((float X, float Y) p, (float X, float Y) c, int g)
    {
        double t = g * 2 * Math.PI / 65536;
        double dx = p.X - c.X, dy = p.Y - c.Y;
        return ((float)(c.X + (dx * Math.Cos(t)) - (dy * Math.Sin(t))), (float)(c.Y + (dx * Math.Sin(t)) + (dy * Math.Cos(t))));
    }

    private static (float X, float Y) Unturn(Vertex v, (float X, float Y) c, int g) => TurnAbout((v.X, v.Y), c, -g);

    private static DrawCmd[] TurnedWindow(string window, string? held = null)
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, window);
        if (held is not null)
        {
            StageKit.Run(rt, held);
        }

        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        return Frame(rt, out _);
    }

    // scene 300's window 33 (issue #16): crop (580, -181, 1006, 1473), inset 15, ridge mode 2, angle 1153 (+11.525 deg, g = 1153 * 65535 / 36000 = 2098)
    private const string Window33 = "_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,580,-181,1006,1473,15,2,,1153);";

    [Fact]
    public void ATurnedRidgeWindowIsCutByItsInnerRectangleTurnedAboutTheCropCentreNotByAStraightScissor()
    {
        // the PS4's engine trace of scene 300 at frame 800 logs the inner piece (the crop shrunk by the inset on every side) of window 33 drawn through 0xEB9C0
        // with flags 0x34 at these corners (scratch K53 judge-trace-f800); the scissor is only the turned crop's bounds cut to the screen
        DrawCmd[] cmds = TurnedWindow(Window33);
        DrawCmd window = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 2);
        Assert.Equal(new PixelRect(442, 0, 1724 - 442, 1080), window.Clip);
        Assert.Equal(StageRuntime.SoftTarget, window.Fx.Mask);
        DrawCmd[] inner = OnTarget(cmds, StageRuntime.SoftTarget);                   // nine cells on the ridge's grid, row by row
        Assert.Equal(9, inner.Length);
        Assert.All(inner, c => Assert.Equal((DrawKind.FillQuad, (short)58), (c.Kind, c.EffectWord)));
        Near((749.0f, -249.0f), inner[0].V0);
        Near((1705.3f, -54.0f), inner[2].V1);
        Near((460.7f, 1165.0f), inner[6].V2);
        Near((1417.0f, 1360.0f), inner[8].V3);
    }

    [Fact]
    public void ANegativeAngleTurnsTheWindowTheOtherWay()
    {
        // scene 300's window 28: crop (1273, -255, 980, 1587), angle -1386 (-13.87 deg): a' = 34614, g = 63011; the trace's inner corners
        DrawCmd[] cmds = TurnedWindow("_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,1273,-255,980,1587,15,2,,-1386);");
        DrawCmd[] inner = OnTarget(cmds, StageRuntime.SoftTarget);
        Assert.Equal(9, inner.Length);
        Near((1115.2f, -103.4f), inner[0].V0);
        Near((2037.5f, -331.2f), inner[2].V1);
        Near((1488.5f, 1408.2f), inner[6].V2);
        Near((2410.8f, 1180.4f), inner[8].V3);
    }

    [Fact]
    public void AnAngleOfZeroOrAFullTurnKeepsTheStraightScissor()
    {
        // a' == 0 takes the straight branch (0x4CB5F -> 0x4D7A5); a19 = 36000 wraps to 0
        foreach (string a in new[] { "0", "36000" })
        {
            DrawCmd[] cmds = TurnedWindow($"_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,100,50,600,400,20,2,,{a});");
            DrawCmd window = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 2);
            Assert.Equal(new PixelRect(120, 70, 560, 360), window.Clip);
            Assert.Equal(0, window.Fx.Mask);
            Assert.Empty(OnTarget(cmds, StageRuntime.SoftTarget));
        }
    }

    [Fact]
    public void TheLayersATurnedWindowHoldsAreCutByTheSameTurnedEdgeAndTheirOwnAngleIsIgnored()
    {
        // 0x5218F re-masks a held layer through 0x51060 of the window: the window's angle governs; the held layer's own a19 (9000) turns nothing
        DrawCmd[] cmds = TurnedWindow(Window33, "_STCH(4,IMG0002,0,0,1000,255,60,,CC,,,3,,,,,,,,9000);");
        DrawCmd window = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 2);
        DrawCmd held = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 3);
        Assert.Equal(StageRuntime.MaskTarget, held.Fx.Mask);
        Assert.Equal(window.Clip, held.Clip);
        DrawCmd[] mask = OnTarget(cmds, StageRuntime.MaskTarget);
        // the window's silhouette, the turned inner rectangle (nine cells) that narrows it, and turned band cells of effect 55 (alpha := 0) around it: the bounds
        // scissor lets the silhouette through outside the turned edge, so the PS4's way of clearing the rest of the window's alpha is needed here as well
        Assert.Equal(1 + 9 + 16, mask.Length);
        Assert.Equal(58, mask[0].EffectWord);
        Assert.All(mask[1..10], c => Assert.Equal((DrawKind.FillQuad, (byte)0x80), (c.Kind, c.BlendState)));
        Near((749.0f, -249.0f), mask[1].V0);
        Near((1417.0f, 1360.0f), mask[9].V3);
        Assert.All(mask[10..], c => Assert.Equal((DrawKind.FillQuad, (short)55, 0xFFu), (c.Kind, c.EffectWord, c.V0.Rgba >> 24)));

        // a straight window keeps its scissor for the held layer, whatever the held layer's own angle
        DrawCmd[] straight = TurnedWindow("_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,100,50,600,400,20,2);", "_STCH(4,IMG0002,0,0,1000,255,60,,CC,,,3,,,,,,,,9000);");
        DrawCmd sheld = straight.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 3);
        Assert.Equal(new PixelRect(120, 70, 560, 360), sheld.Clip);
        Assert.Single(OnTarget(straight, StageRuntime.MaskTarget));
    }

    [Fact]
    public void AngleMinus32768FollowsTheWindowsOwnRotationAndItsParentsButNotTheRotationTween()
    {
        // 0x4C944..0x4C9BD: a = trunc(rot (+0x58) + 0x4C3E0(parent)), wrapped; the tween offset +0x7C is not read. _STRR(l, -99999) stores -32768
        int Turned(bool parent, out (float X, float Y) v0)
        {
            StageRuntime rt = Stage();
            StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,580,-181,1006,1473,15,2);");
            StageKit.Run(rt, "_STRR(3,-99999);");
            if (parent)
            {
                StageKit.Run(rt, "_STCH(5,IMG0003,240,136,1000,255,10,,CC);");
                StageKit.Run(rt, "_STRT(5,,-587,0,0);");
                StageKit.Run(rt, "_SBAS(3,5);");
            }

            StageKit.Run(rt, "_FADS(1,0,0);");
            rt.Update(1);
            StageKit.Run(rt, "_STRT(3,0,-532,1000,0);");                                // rot -532 after the commit, its tween still running when the frame is drawn
            DrawCmd[] cmds = Frame(rt, out _);
            Assert.Equal(-32768, rt.Drawn[3].Angle);
            Assert.NotEqual(0, rt.Drawn[3].RotCur);
            DrawCmd inner = OnTarget(cmds, StageRuntime.SoftTarget)[0];
            v0 = (inner.V0.X, inner.V0.Y);
            return 0;
        }

        Turned(parent: false, out (float X, float Y) own);
        (float X, float Y) want = TurnAbout((595f, -166f), (1083f, 555.5f), 64566);       // a' = 35468: g = 64566
        Assert.InRange(own.X, want.X - 0.15f, want.X + 0.15f);
        Assert.InRange(own.Y, want.Y - 0.15f, want.Y + 0.15f);

        Turned(parent: true, out (float X, float Y) chained);
        want = TurnAbout((595f, -166f), (1083f, 555.5f), 63497);                           // -532 - 587 = -1119: g = 63497
        Assert.InRange(chained.X, want.X - 0.15f, want.X + 0.15f);
        Assert.InRange(chained.Y, want.Y - 0.15f, want.Y + 0.15f);
    }

    [Fact]
    public void TheRidgeOfATurnedWindowIsTurnedAboutTheCropCentreAndCutByItsInnerRectangle()
    {
        // 0x4DC60 (a' != 0): every ridge piece through 0xEB9C0 with flags 0x34 at the window's angle, on all four sides. The port keeps what lies inside the
        // inner rectangle (the straight branch's scissor cut): the four inner corner pieces and the four gradation strips; the solid outer halves fall outside
        DrawCmd[] cmds = TurnedWindow(Window33);
        DrawCmd window = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 2);
        DrawCmd[] ridge = [.. cmds.SkipWhile(c => !c.Equals(window)).Skip(1).Where(c => c.Target == 0 && (c.Kind == DrawKind.FillQuad || c.Texture.A == (int)UiSet.BorderParts))];
        Assert.Equal(8, ridge.Length);
        Assert.All(ridge, c => Assert.Equal((DrawKind.TexturedQuad, (byte)1, window.Clip), (c.Kind, c.BlendState, c.Clip)));
        (float X, float Y) centre = (1083f, 555.5f);
        foreach (DrawCmd c in ridge)
        {
            foreach (Vertex v in new[] { c.V0, c.V1, c.V2, c.V3 })
            {
                (float x, float y) = Unturn(v, centre, 2098);
                Assert.InRange(x, 595f - 0.01f, 1571f + 0.01f);
                Assert.InRange(y, -166f - 0.01f, 1277f + 0.01f);
            }
        }

        // the left gradation strip: x 595..610, y -151..1262 before the turn
        Assert.Contains(ridge, c => Math.Abs(Unturn(c.V0, centre, 2098).X - 595f) < 0.01f && Math.Abs(Unturn(c.V3, centre, 2098).X - 610f) < 0.01f
            && Math.Abs(Unturn(c.V0, centre, 2098).Y + 151f) < 0.01f && Math.Abs(Unturn(c.V3, centre, 2098).Y - 1262f) < 0.01f);
    }

    [Fact]
    public void ATurnedMaskAndItsBandsMeetOnlyAtSharedVerticesSoNoPixelOnTheEdgeIsMissed()
    {
        // review of 1641bac: the four long effect-55 bands met the feather pieces at T-junctions on the turned edge; once the GPU snapped the vertices a few
        // pixels were covered by neither, and the held layer showed there at full strength (scene 432 steps 1705 and 3883). Every edge on the outline must be
        // drawn by exactly two quads with the same two end points: one piece inside, one band cell outside
        foreach (string window in new[]
        {
            "_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,-258,-242,984,1642,99,0,,-1917);",       // scene 432: soft edge
            Window33,                                                                              // scene 300: ridge
            "_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,398,446,225,54,,,,1257);",                // scene 742: bare crop
        })
        {
            DrawCmd[] cmds = TurnedWindow(window, "_STCH(4,IMG0002,0,0,1000,255,60,,CC,,,3);");
            DrawCmd[] mask = OnTarget(cmds, StageRuntime.MaskTarget)[1..];                        // after the silhouette
            Vertex[] inside = [.. mask.Where(c => c.BlendState == 0x80).SelectMany(c => new[] { c.V0, c.V1, c.V2, c.V3 })];
            (float cx, float cy) = (inside.Average(v => v.X), inside.Average(v => v.Y));
            float reach = inside.Max(v => MathF.Sqrt(((v.X - cx) * (v.X - cx)) + ((v.Y - cy) * (v.Y - cy)))) + 1f;
            var edges = new Dictionary<((float, float), (float, float)), int>();
            foreach (DrawCmd q in mask)
            {
                foreach ((Vertex a, Vertex z) in new[] { (q.V0, q.V1), (q.V1, q.V3), (q.V3, q.V2), (q.V2, q.V0) })
                {
                    ((float, float) p, (float, float) r) = ((a.X, a.Y), (z.X, z.Y));
                    var key = p.CompareTo(r) < 0 ? (p, r) : (r, p);
                    edges[key] = edges.GetValueOrDefault(key) + 1;
                }
            }

            // the edges that do not run out to the far side of a band: the outline and the pieces' own seams
            bool Near2((float X, float Y) v) => MathF.Sqrt(((v.X - cx) * (v.X - cx)) + ((v.Y - cy) * (v.Y - cy))) <= reach;
            KeyValuePair<((float, float), (float, float)), int>[] near = [.. edges.Where(e => Near2(e.Key.Item1) && Near2(e.Key.Item2))];
            Assert.NotEmpty(near);
            Assert.All(near, e => Assert.True(e.Value == 2, $"{window}: edge {e.Key} is drawn by {e.Value} quad(s)"));
        }
    }

    [Fact]
    public void ATurnedRidgeWindowsMaskCellsAreItsRidgePieces()
    {
        // the window's mask and its ridge rasterise the turned edge alike only with the same vertices: each ridge piece kept inside the inner rectangle (four
        // inner corner pieces, four strips) is one of the mask's outer cells (scene 742 step 5071 showed four picture pixels with no ridge over them before)
        DrawCmd[] cmds = TurnedWindow(Window33);
        DrawCmd window = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 2);
        DrawCmd[] ridge = [.. cmds.SkipWhile(c => !c.Equals(window)).Skip(1).Where(c => c.Target == 0 && c.Texture.A == (int)UiSet.BorderParts)];
        DrawCmd[] cells = OnTarget(cmds, StageRuntime.SoftTarget);
        Assert.Equal(8, ridge.Length);
        foreach (DrawCmd r in ridge)
        {
            Assert.Contains(cells, c => (c.V0.X, c.V0.Y, c.V1.X, c.V1.Y, c.V2.X, c.V2.Y, c.V3.X, c.V3.Y) == (r.V0.X, r.V0.Y, r.V1.X, r.V1.Y, r.V2.X, r.V2.Y, r.V3.X, r.V3.Y));
        }
    }

    [Fact]
    public void WithTheEffectPassesOffATurnedWindowKeepsItsStraightScissor()
    {
        // EffectPasses = false reproduces K2's plain emission (no masks): a turned window then keeps the straight scissor it had, not the bounds of its turn
        StageRuntime rt = Stage();
        rt.EffectPasses = false;
        StageKit.Run(rt, Window33);
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        DrawCmd[] cmds = Frame(rt, out _);
        DrawCmd window = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 2);
        Assert.Equal(new PixelRect(595, 0, 976, 1080), window.Clip);
        Assert.Equal(0, window.Fx.Mask);
        Assert.Empty(OnTarget(cmds, StageRuntime.SoftTarget));
    }

    [Fact]
    public void ATurnedWindowAtAlphaZeroShowsNothingOfItsGroup()
    {
        // the alpha-0 rule comes first (0x51060 draws nothing at alpha 0): no turned mask, no ridge, the held layers see an empty mask
        DrawCmd[] cmds = TurnedWindow("_STCH(3,IMG0001,240,136,1000,0,50,,CC,,,,580,-181,1006,1473,15,2,,1153);", "_STCH(4,IMG0002,0,0,1000,255,60,,CC,,,3);");
        Assert.Empty(OnTarget(cmds, StageRuntime.MaskTarget));
        Assert.Empty(OnTarget(cmds, StageRuntime.SoftTarget));
        Assert.DoesNotContain(cmds, c => c.Target == 0 && c.Texture.A == (int)UiSet.BorderParts && c.Texture.Kind == TextureKind.UiTile);
        Assert.Equal(StageRuntime.MaskTarget, cmds.Single(c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == 3).Fx.Mask);
    }

    [Fact]
    public void ATurnedSoftWindowFeathersAllFourSides()
    {
        // scene 334's layer 38: crop (-404, -1492, 1120, 3100), inset 154, mode 0, angle 9000 (g = 16383, a quarter turn): the tall strip becomes a band across
        // the top of the screen (trace s334: g 16383 at 0x4CBBA). r = 77 (radius class 128, child 4) on every side, the turn moves the sides on screen
        DrawCmd[] cmds = TurnedWindow("_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,-404,-1492,1120,3100,154,0,,9000);");
        DrawCmd window = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 2);
        Assert.Equal(new PixelRect(0, 0, 1707, 619), window.Clip);
        Assert.Equal(StageRuntime.SoftTarget, window.Fx.Mask);
        DrawCmd[] soft = OnTarget(cmds, StageRuntime.SoftTarget);
        Assert.Equal(8, soft.Count(c => c.Kind == DrawKind.TexturedQuad));
        Assert.All(soft.Where(c => c.Kind == DrawKind.TexturedQuad), c => Assert.Equal(4, c.Texture.C));
        DrawCmd fill = soft.Single(c => c.Kind == DrawKind.FillQuad);
        Near((1552f, -348f), fill.V0, 0.2f);
        Near((1552f, 464f), fill.V1, 0.2f);
        Near((-1240f, -348f), fill.V2, 0.2f);
        Near((-1240f, 464f), fill.V3, 0.2f);
        DrawCmd topLeft = soft[0];                                                     // the outer corner of the crop's top-left quadrant, turned to the top right
        Near(TurnAbout((-404f, -1492f), (156f, 58f), 16383), topLeft.V0, 0.2f);
    }

    [Fact]
    public void ATurnedWindowWithARuleFadeIsCutByTheRuleTimesTheTurnedEdge()
    {
        // no scissor holds a turned window: its rule mask (STTR) is narrowed by the turned inner rectangle as well (no retail script turns a rule window)
        StageRuntime rt = Stage();
        StageKit.Run(rt, Window33);
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        StageKit.Run(rt, "_STTR(3,4,1);");
        Assert.True(rt.Drawn[3].Sttr >= 0, $"sttr {rt.Drawn[3].Sttr}");
        DrawCmd[] cmds = Frame(rt, out _);
        DrawCmd window = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 2);
        Assert.Equal(StageRuntime.MaskTarget, window.Fx.Mask);
        DrawCmd[] turned = [.. OnTarget(cmds, StageRuntime.MaskTarget).Where(c => c.Kind == DrawKind.FillQuad && c.BlendState == 0x80)];
        Assert.Equal(9, turned.Length);
        Near((749.0f, -249.0f), turned[0].V0);
        Assert.True(Array.IndexOf(cmds, turned[^1]) < Array.IndexOf(cmds, window));
        Assert.Equal(16, OnTarget(cmds, StageRuntime.MaskTarget).Count(c => c.Kind == DrawKind.FillQuad && c.EffectWord == 55));      // the rule outside the window: 0
    }

    [Fact]
    public void TheLookAheadNamesThePicturesOfTheNextCommitsWithTheGradeTheirLayersWillHold()
    {
        StageRuntime rt = Stage();
        StageRuntime.LookAheadScan scan = rt.BeginLookAhead();
        Assert.False(scan.Feed(StageKit.Parse("_STCH(3,IMG0001,240,136,1000,255,50,,CC);")));
        Assert.False(scan.Feed(StageKit.Parse("_STEC(3,44);")));
        Assert.False(scan.Feed(StageKit.Parse("_SNX2(3,5);")));
        Assert.False(scan.Feed(StageKit.Parse("_STCH(4,IMG0002,240,136,1000,255,60,,CC);")));
        Assert.False(scan.Feed(StageKit.Parse("_STCH(5,IMG0003,240,136,1000,255,70,,CC);")));
        Assert.False(scan.Feed(StageKit.Parse("_STCC(5);")));                                     // cleared before the commit: not named
        Assert.True(scan.Feed(StageKit.Parse("_FADS(1,0,0);")));
        var tiles = new List<TextureRef>();
        var done = new HashSet<(int Pic, int Key)>();
        scan.Flush(tiles, done);

        int graded = GradeKeys.Intern(ColourGrade.From(44, 5, 0, 0xFFFFFF));
        Assert.NotEqual(0, graded);
        Assert.Equal([TextureRef.ForPictureTile(0, 2, 0, graded), TextureRef.ForPictureTile(0, 3, 0, 0)], tiles);          // layer 3 (IMG0001 = 2) graded, layer 4 (IMG0002 = 3) plain

        // what the scene state holds is what the stage then draws: the same keys come out of the real commit
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_STEC(3,44);");
        StageKit.Run(rt, "_SNX2(3,5);");
        StageKit.Run(rt, "_STCH(4,IMG0002,240,136,1000,255,60,,CC);");
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        TextureRef[] drawn = Frame(rt, out _).Where(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0).Select(c => c.Texture).ToArray();
        Assert.Contains(TextureRef.ForPictureTile(0, 2, 0, graded), drawn);
        Assert.Contains(TextureRef.ForPictureTile(0, 3, 0, 0), drawn);

        // a pair that was named is not named again
        var again = new List<TextureRef>();
        StageRuntime.LookAheadScan second = rt.BeginLookAhead();
        second.Feed(StageKit.Parse("_STCH(3,IMG0001,240,136,1000,255,50,,CC);"));
        second.Feed(StageKit.Parse("_STEC(3,44);"));
        second.Feed(StageKit.Parse("_SNX2(3,5);"));
        second.Flush(again, done);
        Assert.Empty(again);
    }

    [Fact]
    public void AQuakeAndTheScreenShockTranslateTheQuads()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        DrawCmd before = Frame(rt, out _).Single(c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == 2);
        StageKit.Run(rt, "_SQK2(3,6,6,,-1);");
        rt.Update(2);
        (float sx, float sy) = rt.Shakes.SlotOffset(3);
        Assert.NotEqual(0f, sx);
        DrawCmd after = Frame(rt, out _).Single(c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == 2);
        Assert.Equal(before.V0.X + (sx * 3f), after.V0.X, 2);
        Assert.Equal(before.V0.Y + (sy * (1080f / (float)Projection.CanvasH)), after.V0.Y, 2);

        StageKit.Run(rt, "_SQS2(3);");
        StageKit.Run(rt, "_SHOK(0,3,100,10,0);");
        rt.Update(1);
        Assert.NotEqual(0f, rt.Shock.Y);
        DrawCmd shocked = Frame(rt, out _).Single(c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == 2);
        Assert.Equal(before.V0.Y + (rt.Shock.Y * (1080f / (float)Projection.CanvasH)), shocked.V0.Y, 2);

        // the seven colour fills (BLACK here) ignore the screen shock
        DrawCmd fill = Frame(rt, out _).Single(c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == 0);
        Assert.InRange(fill.V0.Y, -0.01f, 0.01f);
    }

    // the stage with the window statements committed and a screen shock (_SHOK(4,6,300,10)) running for a few updates, so that its y is several canvas units
    private static StageRuntime Shocked(string window, string? held = null, bool shock = true)
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, window);
        if (held is not null)
        {
            StageKit.Run(rt, held);
        }

        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
        if (shock)
        {
            StageKit.Run(rt, "_SHOK(4,6,300,10,0);");
        }

        for (int i = 0; i < 4; i++)
        {
            rt.Update(1);
        }

        return rt;
    }

    // the crop origin (x, y) moved by the screen shock as 0x49A60 adds it while +0x124 == 1: x * 3.0, y * [0x1A6DBC] = 1080 / 448
    private static (int X, int Y) ShockedOrigin(StageRuntime rt, float x, float y) =>
        ((int)MathF.Round(x + (rt.Shock.X * 3.0f)), (int)MathF.Round(y + (rt.Shock.Y * Ps4Math.ShockYMask)));

    private static PixelRect Overlap(PixelRect a, PixelRect b)
    {
        int x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
        int x1 = Math.Min(a.X + a.Width, b.X + b.Width), y1 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return new PixelRect(x0, y0, Math.Max(x1 - x0, 0), Math.Max(y1 - y0, 0));
    }

    [Fact]
    public void DuringAScreenShockAWindowsOwnCutMovesByTheShockTimes3InXAnd1080Over448InY()
    {
        // 0x49A60 adds the shock to the crop origin only while the window is drawn into its own target (+0x124 == 1: its pass at 0x5AA7F and the mask pass
        // that lays its group over the screen at 0x5AD54), y times [0x1A6DBC] = 1080 / 448 where the shake slots and the picture take 1080 / 362; the engine
        // probe (scratch K53/shock) moves the pieces of crop (400, 200, 800, 600) by (5.442, 6.559) px for a shock of (1.814, 2.7209)
        StageRuntime rt = Shocked("_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,100,50,600,400,60,0);");
        Assert.True(MathF.Abs(rt.Shock.Y) * (Ps4Math.YPixels - Ps4Math.ShockYMask) > 1.5f, "the shock tells the two y factors apart");
        DrawCmd[] cmds = Frame(rt, out _);
        DrawCmd window = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 2);
        (int x0, int y0) = ShockedOrigin(rt, 100f, 50f);
        Assert.Equal(new PixelRect(x0, y0, 600, 400), window.Clip);
        Assert.Equal(new PixelRect(115, 69, 600, 400), window.Clip);       // this run's shock (5.136, 7.703): 100 + 15.41, 50 + 18.57 (1080 / 362 would give 73)
        DrawCmd topLeft = OnTarget(cmds, StageRuntime.SoftTarget)[0];
        Assert.Equal(((float)x0, (float)y0, x0 + 60f, y0 + 60f), (topLeft.V0.X, topLeft.V0.Y, topLeft.V3.X, topLeft.V3.Y));
    }

    [Fact]
    public void DuringAScreenShockTheLayersAWindowHoldsAndItsRidgeAreCutWhereTheWindowIsWithoutTheShock()
    {
        // 0x5A5E0 sets the window's +0x124 back to -1 after its own pass (0x5AAE1), and 0x51060 forces it to -1, with the inset +0x13A = 0, around the re-mask of a
        // held layer by its window (0x5216F..0x5218F): the held layers' bare crop and the ridge (0x4DC60 at 0x5ABB4) take no shock, and reach the screen through
        // the window's mask pass, which does (0x5AD54). The engine probe (scratch K53/shock) keeps the re-mask and the ridge pieces where they are without it
        StageRuntime rt = Shocked("_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,100,50,600,400);", "_STCH(4,IMG0002,0,0,1000,255,60,,CC,,,3);");
        DrawCmd[] cmds = Frame(rt, out _);
        DrawCmd window = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 2);
        DrawCmd held = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 3);
        (int x0, int y0) = ShockedOrigin(rt, 100f, 50f);
        Assert.Equal(new PixelRect(x0, y0, 600, 400), window.Clip);
        Assert.Equal(Overlap(window.Clip!.Value, new PixelRect(100, 50, 600, 400)), held.Clip);
        Assert.NotEqual(window.Clip, held.Clip);

        // the ridge: its pieces stay where the window is without the shock, under the moved scissor of the mask pass
        DrawCmd[] Ridge(StageRuntime r, out PixelRect? clip)
        {
            DrawCmd[] c = Frame(r, out _);
            DrawCmd w = c.Single(x => x.Kind == DrawKind.TexturedQuad && x.Target == 0 && PicOf(x) == 2);
            clip = w.Clip;
            return [.. c.SkipWhile(x => !x.Equals(w)).Skip(1).Where(x => x.Target == 0 && (x.Kind == DrawKind.FillQuad || x.Texture.A == (int)UiSet.BorderParts))];
        }

        const string Band = "_STCH(3,IMG0001,240,136,1000,255,50,,CC,,,,100,50,600,400,20,2);";
        const string Held = "_STCH(4,IMG0002,0,0,1000,255,60,,CC,,,3);";
        StageRuntime shocked = Shocked(Band, Held);
        DrawCmd[] moved = Ridge(shocked, out PixelRect? scissor);
        DrawCmd[] still = Ridge(Shocked(Band, Held, shock: false), out _);
        (int bx, int by) = ShockedOrigin(shocked, 100f, 50f);
        Assert.Equal(new PixelRect(bx + 20, by + 20, 560, 360), scissor);

        // the held layer of the ridge window: the re-mask is the bare crop (inset +0x13A = 0 at 0x52184), so it is cut by the moved inner scissor and by the whole
        // crop without the shock, not by the crop's inner rectangle without it
        DrawCmd bandHeld = Frame(shocked, out _).Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 3);
        Assert.Equal(Overlap(new PixelRect(bx + 20, by + 20, 560, 360), new PixelRect(100, 50, 600, 400)), bandHeld.Clip);
        Assert.NotEqual(Overlap(new PixelRect(bx + 20, by + 20, 560, 360), new PixelRect(120, 70, 560, 360)), bandHeld.Clip);
        Assert.NotEmpty(still);
        Assert.Equal(still.Length, moved.Length);
        for (int i = 0; i < still.Length; i++)
        {
            Assert.Equal((still[i].V0.X, still[i].V0.Y, still[i].V3.X, still[i].V3.Y), (moved[i].V0.X, moved[i].V0.Y, moved[i].V3.X, moved[i].V3.Y));
            Assert.Equal(scissor, moved[i].Clip);
        }

        // a turned window: the held layers' mask is narrowed once more by the bare turned crop where it is without the shock (one fill, 8 band cells around it)
        StageRuntime turned = Shocked(Window33, Held);
        DrawCmd[] tc = Frame(turned, out _);
        DrawCmd[] mask = OnTarget(tc, StageRuntime.MaskTarget);
        Assert.Equal(1 + 9 + 16 + 1 + 8, mask.Length);
        DrawCmd bare = mask[26];
        Assert.Equal((DrawKind.FillQuad, (byte)0x80), (bare.Kind, bare.BlendState));
        Near(TurnAbout((580f, -181f), (1083f, 555.5f), 2098), bare.V0);
        Near(TurnAbout((1586f, 1292f), (1083f, 555.5f), 2098), bare.V3);
        DrawCmd twindow = tc.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 2);
        DrawCmd theld = tc.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 3);
        DrawCmd unshocked = Frame(Shocked(Window33, Held, shock: false), out _).Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 3);
        Assert.Equal(Overlap(twindow.Clip!.Value, unshocked.Clip!.Value), theld.Clip);
    }

    [Fact]
    public void DuringAScreenShockATurnedRidgeKeepsInsideTheInnerEdgesOfBothCuts()
    {
        // the ridge is drawn at the crop without the shock (0x4DC60 with +0x124 = -1) and reaches the screen through the window's mask pass, which has it
        // (0x5AD54): for a turned window, whose scissor is only its bounds, the ridge must keep inside the turned inner rectangle of both, as a straight
        // window's ridge keeps inside its moved scissor
        StageRuntime rt = Shocked(Window33);
        DrawCmd[] cmds = Frame(rt, out _);
        DrawCmd window = cmds.Single(c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && PicOf(c) == 2);
        DrawCmd[] ridge = [.. cmds.SkipWhile(c => !c.Equals(window)).Skip(1).Where(c => c.Target == 0 && (c.Kind == DrawKind.FillQuad || c.Texture.A == (int)UiSet.BorderParts))];
        Assert.NotEmpty(ridge);
        (int sx, int sy) = ShockedOrigin(rt, 580f, -181f);
        Assert.True(Math.Abs(sx - 580) + Math.Abs(sy + 181) >= 10, "the shock moves the window by several pixels");
        foreach (DrawCmd c in ridge)
        {
            foreach (Vertex v in new[] { c.V0, c.V1, c.V2, c.V3 })
            {
                (float x, float y) = Unturn(v, (1083f, 555.5f), 2098);                   // the inner rectangle without the shock: (595, -166) .. (1571, 1277)
                Assert.InRange(x, 595f - 0.05f, 1571f + 0.05f);
                Assert.InRange(y, -166f - 0.05f, 1277f + 0.05f);
                (x, y) = Unturn(v, (sx + 503f, sy + 736.5f), 2098);                    // and with it
                Assert.InRange(x, sx + 15f - 0.05f, sx + 991f + 0.05f);
                Assert.InRange(y, sy + 15f - 0.05f, sy + 1458f + 0.05f);
            }
        }
    }

    [Fact]
    public void ACrossFadeCapturesTheFrameOnceAndFadesItOutOverTheDuration()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_FADS(0,500,0);");
        rt.Update(1);
        Assert.True(rt.TransitionActive);
        DrawCmd[] first = Frame(rt, out _);
        Assert.Equal(DrawKind.FillQuad, first[0].Kind);
        Assert.Equal(56, first[0].EffectWord);
        Assert.Equal(11, first[0].BlendState);
        Assert.Equal(DrawKind.Capture, first[1].Kind);
        Assert.Equal(0, first[1].Texture.A);
        Assert.Equal(DrawKind.TexturedQuad, first[^1].Kind);
        Assert.Equal(TextureKind.Capture, first[^1].Texture.Kind);
        Assert.Equal(255u, first[^1].V0.Rgba >> 24);

        // the capture is taken once; 500 ms are 30 frames
        int frames = 0;
        while (rt.TransitionActive)
        {
            rt.Update(1);
            frames++;
            DrawCmd[] cmds = Frame(rt, out _);
            Assert.DoesNotContain(cmds, c => c.Kind == DrawKind.Capture);
            if (rt.TransitionActive)
            {
                DrawCmd cap = cmds[^1];
                Assert.Equal(TextureKind.Capture, cap.Texture.Kind);
                Assert.Equal((uint)(int)(rt.TransitionProgress * 255f), cap.V0.Rgba >> 24);
            }
        }

        Assert.InRange(frames, 29, 31);
    }

    [Fact]
    public void AnImmediateFadsDrawsNoTransitionFrameAndARuleWipeUsesTheRulePicture()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_FADS(1,200,0);");
        rt.Update(1);
        DrawCmd[] cmds = Frame(rt, out StageDrawInfo info);
        Assert.Equal(DrawKind.Capture, cmds[1].Kind);
        Assert.DoesNotContain(cmds, c => c.Texture.Kind == TextureKind.Capture && c.Kind == DrawKind.TexturedQuad);
        Assert.Null(info.RuleTexture);

        StageKit.Run(rt, "_FADS(2,300,5);");
        rt.Update(1);
        cmds = Frame(rt, out info);
        Assert.Equal(DrawKind.Capture, cmds[1].Kind);
        Assert.Equal(15, cmds[2].Texture.A);       // type 2 also captures surface 15
        Assert.Equal(DrawKind.Capture, cmds[2].Kind);
        DrawCmd rule = cmds[^2];
        DrawCmd wipe = cmds[^1];
        Assert.Equal(new TextureRef(TextureKind.PictureTile, 4, 90, 0, 0), rule.Texture);   // rule 5 = allpac3 entry 85 + 5
        Assert.Equal(58, rule.EffectWord);
        Assert.Equal(TextureRef.ForCapture(15), wipe.Texture);
        Assert.Equal(26, wipe.EffectWord);
        Assert.Equal(rule.Texture, info.RuleTexture);
        Assert.Equal(1f, info.RuleProgress);
    }
}

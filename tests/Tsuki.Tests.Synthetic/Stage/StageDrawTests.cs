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

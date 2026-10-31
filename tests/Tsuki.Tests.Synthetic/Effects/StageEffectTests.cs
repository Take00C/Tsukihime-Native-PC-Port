using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;
using Tsuki.Tests.Synthetic.Stage;

namespace Tsuki.Tests.Synthetic.Effects;

/// <summary>
/// The passes K2 left undrawn, as the stage now emits them (package K7; docs/status/knife/K7.md): the offscreen mask of a window layer, the rule fade
/// of <c>_STTR</c>, the STRA haze, the STNS noise, and the _FADS engine screens.
/// </summary>
public sealed class StageEffectTests
{
    private sealed class FullScreen : IPictureLayoutSource
    {
        public PictureLayout? Get(int pic, int language) =>
            new(1920, 1080, [new PictureFileLayout(0, pic, 0, 0, [new TileQuad(0, 0, 0, 1920, 1080, 0, 0, 1, 1)])]);
    }

    private sealed class FixedHaze(float[] entry) : IHazeTable
    {
        public bool TryGetEntry(int straId, out float[] e)
        {
            e = entry;
            return straId > 0;
        }
    }

    private static StageRuntime Stage()
    {
        var rt = new StageRuntime(StageKit.Catalog(), StageKit.NoMotions()) { Layouts = new FullScreen() };
        StageKit.Run(rt, "_STCC(-1);");
        StageKit.Run(rt, "_STCH(0,BLACK,240,136,1000,255,2,,CC,,,);");
        Commit(rt);
        return rt;
    }

    private static void Commit(StageRuntime rt)
    {
        StageKit.Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
    }

    private static DrawCmd[] Frame(StageRuntime rt)
    {
        var list = new DisplayList();
        list.Reset(0);
        rt.Draw(list);
        return list.Commands.ToArray();
    }

    // IMG0001 and IMG0002 of the synthetic catalog (BLACK is 0)
    private const int P1 = 2;
    private const int P2 = 3;

    private static int PicOf(DrawCmd c) => c.Texture.B;

    [Fact]
    public void AWindowLayerBuildsTheMaskOfTheLayersThatNameIt()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_STCH(4,IMG0002,240,136,1000,255,60,,CC,,,3);");
        Commit(rt);
        DrawCmd[] cmds = Frame(rt);

        int clear = Array.FindIndex(cmds, c => c.Kind == DrawKind.Clear);
        Assert.True(clear > 0, "the mask target is cleared before the group");
        Assert.Equal(StageRuntime.MaskTarget, cmds[clear].Target);
        DrawCmd silhouette = cmds[clear + 1];
        Assert.Equal((StageRuntime.MaskTarget, P1, 58, 4), (silhouette.Target, PicOf(silhouette), silhouette.EffectWord, (int)silhouette.BlendState));
        DrawCmd window = cmds[clear + 2];
        Assert.Equal((0, P1, 0), ((int)window.Target, PicOf(window), (int)window.Fx.Mask));
        DrawCmd masked = cmds[clear + 3];
        Assert.Equal((0, P2, (int)StageRuntime.MaskTarget), ((int)masked.Target, PicOf(masked), (int)masked.Fx.Mask));
        Assert.Equal(clear + 4, cmds.Length);
    }

    [Fact]
    public void AWindowThatFadedOutLeavesItsLayersAnEmptyMask()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,0,50,,CC);");
        StageKit.Run(rt, "_STCH(4,IMG0002,240,136,1000,255,60,,CC,,,3);");
        Commit(rt);
        DrawCmd[] cmds = Frame(rt);
        int clear = Array.FindIndex(cmds, c => c.Kind == DrawKind.Clear);
        Assert.True(clear > 0);
        DrawCmd masked = Assert.Single(cmds, c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == P2);
        Assert.Equal(StageRuntime.MaskTarget, masked.Fx.Mask);
        Assert.DoesNotContain(cmds, c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == P1);
    }

    [Fact]
    public void ARuleFadeWipesTheLayerThroughItsRulePicture()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,128,50,,CC);");
        Commit(rt);
        StageKit.Run(rt, "_STTR(3,26,1);");
        DrawCmd[] cmds = Frame(rt);
        int clear = Array.FindIndex(cmds, c => c.Kind == DrawKind.Clear);
        Assert.True(clear > 0);
        DrawCmd rule = cmds[clear + 1];
        Assert.Equal(TextureRef.ForPictureTile(4, 85 + 26, 0), rule.Texture);
        Assert.Equal((58, QuadPass.Rule, StageRuntime.MaskTarget), (rule.EffectWord, rule.Fx.Pass, rule.Target));
        Assert.Equal(1f, rule.Fx.A2);
        // alpha 128 / 255: remaining progress 1 - alpha; flip bit 0 mirrors the rule in x
        float alpha = StageRuntime_Alpha(rt);
        (float lo, float hi) = TransitionScreens.RuleWindow(1f - alpha);
        Assert.Equal((lo, hi), (rule.Fx.A0, rule.Fx.A1));
        Assert.Equal((1f, 0f), (rule.V0.U, rule.V1.U));
        DrawCmd layer = cmds[clear + 2];
        Assert.Equal((P1, StageRuntime.MaskTarget), (PicOf(layer), layer.Fx.Mask));
        Assert.Equal(clear + 3, cmds.Length);
    }

    private static float StageRuntime_Alpha(StageRuntime rt) => MathF.Min(1f, rt.Drawn[3].Alpha + rt.Drawn[3].AlphaCur);

    [Fact]
    public void ARuleFadeThatIsAlsoAWindowMultipliesItsSilhouetteIntoTheMask()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,200,50,,CC);");
        StageKit.Run(rt, "_STCH(4,IMG0002,240,136,1000,255,60,,CC,,,3);");
        Commit(rt);
        StageKit.Run(rt, "_STTR(3,5);");
        DrawCmd[] cmds = Frame(rt);
        int clear = Array.FindIndex(cmds, c => c.Kind == DrawKind.Clear);
        Assert.Equal(QuadPass.Rule, cmds[clear + 1].Fx.Pass);
        Assert.Equal(StageRuntime.MaskTarget, cmds[clear + 2].Fx.Mask);              // the window itself, through the rule mask
        DrawCmd multiply = cmds[clear + 3];                                          // then its silhouette narrows the mask for the layers it holds
        Assert.Equal((StageRuntime.MaskTarget, 0x80, 0), (multiply.Target, (int)multiply.BlendState, (int)multiply.EffectWord));
        Assert.Equal(0xFF000000u, multiply.V0.Rgba & 0xFF000000u);
        DrawCmd masked = cmds[clear + 4];
        Assert.Equal((P2, StageRuntime.MaskTarget), (PicOf(masked), masked.Fx.Mask));
    }

    [Fact]
    public void TheNoiseReplacesThePictureOfAStnsLayerAndMovesEveryFrame()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        Commit(rt);
        DrawCmd plain = Assert.Single(Frame(rt), c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == P1);
        StageKit.Run(rt, "_STNS(3,11);");
        Commit(rt);
        DrawCmd a = Assert.Single(Frame(rt), c => c.Kind == DrawKind.TexturedQuad && c.Texture.Kind == TextureKind.UiTile);
        DrawCmd b = Assert.Single(Frame(rt), c => c.Kind == DrawKind.TexturedQuad && c.Texture.Kind == TextureKind.UiTile);
        Assert.Equal((UiSet.NoiseParts, 0, -1), ((UiSet)a.Texture.A, a.Texture.C, a.Texture.D));        // stns 11: noise picture 0, whole sheet
        Assert.Equal(1f / 1.2f, MathF.Abs(a.V1.U - a.V0.U), 3);                                           // the 1.2 x scaled noise: 1 / 1.2 of it covers the layer
        Assert.NotEqual((a.V0.U, a.V0.V), (b.V0.U, b.V0.V));                                              // a fresh offset each frame (stns 3 and above)
        Assert.Equal((plain.V0.X, plain.V0.Y, plain.V3.X, plain.V3.Y), (a.V0.X, a.V0.Y, a.V3.X, a.V3.Y)); // the layer keeps its own rectangle
        // stns 12 uses the second noise picture
        StageKit.Run(rt, "_STNS(3,12);");
        Commit(rt);
        DrawCmd c12 = Assert.Single(Frame(rt), c => c.Kind == DrawKind.TexturedQuad && c.Texture.Kind == TextureKind.UiTile);
        Assert.Equal(1, c12.Texture.C);
    }

    [Fact]
    public void TheHazeCarriesTheWavesOfTheTableEntry()
    {
        StageRuntime rt = Stage();
        rt.HazeTable = new FixedHaze([2f, 0f, 1f, 3f, 0f, 1f, 4f, 0f, 1f, 1000f]);
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        StageKit.Run(rt, "_STRA(3,7,0);");
        Commit(rt);
        rt.Update(60);
        DrawCmd q = Assert.Single(Frame(rt), c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == P1);
        Assert.Equal(QuadPass.Haze, q.Fx.Pass);
        // amplitude: 1920 * 0.00097656 * 0.001 * 1000 * e6 * e2 pixels = 7.5 px of a 1920 px tile, in uv; waves: (e0 + 0.1 e7) * h / 1080 * 0.5
        Assert.Equal(1920f * 0.00097656f * 4f * 1f / 1920f, q.Fx.A0, 4);
        Assert.Equal(1f, q.Fx.A1, 4);
        Assert.Equal(1920f * 0.00097656f * 4f * 1f / 1080f, q.Fx.B0, 4);
        Assert.Equal(3f * (1920f / 1920f) * 0.5f, q.Fx.B1, 4);
        Assert.Equal(1f, q.Fx.B3, 6);                                                                     // v scale: (picture rows per uv) / picture height = 1080 / 1080
        // no table entry, no haze
        rt.HazeTable = null;
        Assert.Equal(QuadPass.None, Assert.Single(Frame(rt), c => c.Kind == DrawKind.TexturedQuad && PicOf(c) == P1).Fx.Pass);
    }

    [Fact]
    public void ABackdropLayerAndALayerWithoutEffectsAreUnchanged()
    {
        StageRuntime rt = Stage();
        StageKit.Run(rt, "_STCH(3,IMG0001,240,136,1000,255,50,,CC);");
        Commit(rt);
        DrawCmd[] cmds = Frame(rt);
        Assert.DoesNotContain(cmds, c => c.Kind == DrawKind.Clear);
        Assert.All(cmds.Where(c => c.Kind == DrawKind.TexturedQuad), c => Assert.Equal(default, c.Fx));
        rt.EffectPasses = false;
        StageKit.Run(rt, "_STNS(3,11);");
        Commit(rt);
        Assert.All(Frame(rt).Where(c => c.Kind == DrawKind.TexturedQuad), c => Assert.Equal(TextureKind.PictureTile, c.Texture.Kind));
    }

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    public void ASlideMovesTheOldFrameAndTheNewSceneByOppositeDistances(int kind)
    {
        var list = new DisplayList();
        list.Reset(0);
        TransitionScreens.Frame(list, kind, 0.5f, null, () => 0);
        DrawCmd[] cmds = list.Commands.ToArray();
        Assert.Equal(DrawKind.Capture, cmds[0].Kind);                     // the new scene is captured into surface 0 first
        Assert.Equal(0, cmds[0].Texture.A);
        DrawCmd old = cmds[1], current = cmds[2];
        Assert.Equal((15, 0), (old.Texture.A, current.Texture.A));
        // 960 x 544 texels of the capture, one texel per pixel (compositing.md 7.4: the engine ported the Vita rectangle unchanged)
        Assert.Equal(960f, old.V1.X - old.V0.X);
        Assert.Equal(544f, old.V2.Y - old.V0.Y);
        Assert.Equal((0.5f, 544f / 1080f), (old.V3.U, old.V3.V));
        float h = 480f * 0.5f;
        float v = 272f * 0.5f;
        float px = 4f, py = 1080f / 272f;
        (float ox, float oy, float nx, float ny) = kind switch
        {
            21 => (0f, -v, 0f, v),
            22 => (0f, v, 0f, -v),
            23 => (h, 0f, -h, 0f),
            _ => (-h, 0f, h, 0f),
        };
        Assert.Equal((ox * px, oy * py), (old.V0.X, old.V0.Y));
        Assert.Equal((nx * px, ny * py), (current.V0.X, current.V0.Y));
        Assert.Equal(0xFFFFFFFFu, old.V0.Rgba);
    }

    [Fact]
    public void TheMosaicShrinksTheNewSceneAndMagnifiesItWithNearestTexels()
    {
        foreach ((float p, int tile) in new[] { (1.0f, 2), (0.5f, 14), (0.1f, 3), (0.02f, 2) })
        {
            var list = new DisplayList();
            list.Reset(0);
            TransitionScreens.Frame(list, 18, p, null, () => 0);
            DrawCmd[] cmds = list.Commands.ToArray();
            Assert.Equal(DrawKind.Capture, cmds[0].Kind);
            Assert.Equal(DrawKind.Capture, cmds[2].Kind);
            Assert.Equal(1280f / tile, cmds[1].V1.X, 1);                                                  // the shrunk picture: 1920 / (6 N) units wide, 4 px a unit
            DrawCmd big = cmds[^1];
            Assert.True(big.Fx.Nearest);
            Assert.Equal((int)(1280f / tile) * tile, (int)big.V1.X);
            Assert.Equal((int)(896f / tile) * tile, (int)big.V2.Y);
        }
    }

    [Fact]
    public void TheScanLinesDrawFiveHundredAndFortyFourStripsFromRand()
    {
        var list = new DisplayList();
        list.Reset(0);
        int calls = 0;
        TransitionScreens.Frame(list, 20, 0.9f, null, () => calls++);
        DrawCmd[] cmds = list.Commands.ToArray();
        Assert.Equal(544, calls);
        Assert.Equal(TextureRef.ForCapture(0), cmds[0].Texture);
        Assert.Equal((byte)(int)(0.9f * 255f), (byte)(cmds[0].V0.Rgba >> 24));                          // the cross-fade weight trunc(255 p)
        Assert.Equal(DrawKind.Capture, cmds[1].Kind);
        Assert.Equal(DrawKind.FillQuad, cmds[2].Kind);
        DrawCmd[] strips = cmds[3..];
        Assert.Equal(544, strips.Length);
        Assert.All(strips, s => Assert.Equal(960f, s.V1.X - s.V0.X));
        Assert.Equal(1f, strips[7].V2.Y - strips[7].V0.Y);
        // offset of strip i: trunc((rand & 31) - 12) * min(1, p * 1.1111) units; rand() here counts 0, 1, 2, ...
        Assert.Equal((int)((0 - 12) * MathF.Min(1f, 0.9f * 1.1111f)) * 4f, strips[0].V0.X);
        Assert.Equal((int)(300 * 0.5f) * (1080f / 272f), strips[300].V0.Y, 3);
    }

    [Fact]
    public void ThePartlyDeadScreensDrawLittleOrNothing()
    {
        var list = new DisplayList();
        list.Reset(0);
        TransitionScreens.Frame(list, 68, 0.5f, null, () => 0);
        Assert.Equal(0, list.Count);                                       // the shatter table is never filled
        TransitionScreens.Frame(list, 69, 0.6f, TextureRef.ForPictureTile(4, 90, 0), () => 0);
        Assert.Equal(2, list.Count);                                       // capture and the white-out: the rule dissolve starts at p = 0.25
        list.Reset(0);
        TransitionScreens.Frame(list, 69, 0.1f, TextureRef.ForPictureTile(4, 90, 0), () => 0);
        DrawCmd[] cmds = list.Commands.ToArray();
        Assert.Equal(4, cmds.Length);
        (float lo, float hi) = TransitionScreens.RuleWindow(1f - (4f * 0.1f));
        Assert.Equal((58, QuadPass.Rule, lo, hi), (cmds[2].EffectWord, cmds[2].Fx.Pass, cmds[2].Fx.A0, cmds[2].Fx.A1));
        Assert.Equal((26, 0), (cmds[3].EffectWord, cmds[3].Texture.A));
        list.Reset(0);
        TransitionScreens.Frame(list, 70, 0.5f, null, () => 0);
        DrawCmd zoom = Assert.Single(list.Commands.ToArray());
        float angle = (int)((1f - 0.5f) * 32768f * 8f) * 9.5873e-5f;
        float w = 960f * ((0.1f * MathF.Cos(angle)) + 0.5f);
        Assert.Equal(960f - (w / 2f), zoom.V0.X, 2);
        Assert.Equal(15, zoom.Texture.A);
    }
}

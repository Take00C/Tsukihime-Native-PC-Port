using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;
using Tsuki.Engine.Vm;
using Tsuki.Render.Compositor;
using Tsuki.Render.Textures;
using Tsuki.Tests.Render.Compositor;
using Tsuki.Tests.Render.Smoke;

namespace Tsuki.Tests.Render.Effects;

/// <summary>
/// The stage passes and the engine transition screens on the GPU (package K7): a real <see cref="StageRuntime"/> builds the display list, the compositor
/// draws it, and the pixels are checked. Synthetic pictures (solid and half-transparent tiles) stand for the game's; a hidden context, no timing.
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class StageGpuTests
{
    private const int W = 1920;
    private const int H = 1080;

    private sealed class FullScreen : IPictureLayoutSource
    {
        public PictureLayout? Get(int pic, int language) =>
            new(W, H, [new PictureFileLayout(0, pic, 0, 0, [new TileQuad(0, 0, 0, W, H, 0, 0, 1, 1)])]);
    }

    private static readonly string[] Names = ["BLACK", "WIN", "KID", "RULEPIC"];

    private static StageRuntime NewStage()
    {
        PictureInfo[] info = [.. Names.Select(_ => new PictureInfo(320, 181, 0, 3, false))];
        var rt = new StageRuntime(new ArrayPictureCatalog(Names, info), new ArrayMotionTables([], [], [], [], [], [])) { Layouts = new FullScreen() };
        Run(rt, "_STCC(-1);");
        Run(rt, "_STCH(0,BLACK,240,136,1000,255,2,,CC);");
        return rt;
    }

    private static void Run(StageRuntime rt, string text)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        ScriptSegment seg = ScriptSegmenter.Read(bytes, 0);
        Assert.True(rt.Execute(new Statement(0, seg.Start, seg.End, CommandName.FromBytes(bytes.AsSpan(seg.NameStart, 4)), bytes.AsMemory(seg.ArgsStart, seg.End - seg.ArgsStart))), text);
    }

    private static void Commit(StageRuntime rt)
    {
        Run(rt, "_FADS(1,0,0);");
        rt.Update(1);
    }

    private static byte[] Solid(int w, int h, byte r, byte g, byte b, byte a, Func<int, int, byte>? alpha = null)
    {
        byte[] px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * 4;
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = (r, g, b, alpha?.Invoke(x, y) ?? a);
            }
        }

        return px;
    }

    private static (int R, int G, int B) At(byte[] frame, int x, int y) => (frame[((y * W) + x) * 4], frame[(((y * W) + x) * 4) + 1], frame[(((y * W) + x) * 4) + 2]);

    private static TextureRef Pic(int pic) => TextureRef.ForPictureTile(0, pic, 0);

    [Fact]
    public void ALayerNamedByAWindowIsVisibleOnlyInsideTheWindowsPicture()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var compositor = new GpuCompositor(ctx.Device.Gl, W, H);
        using var cache = new TextureCache(ctx.Device.Gl);
        var executor = new DisplayListExecutor(compositor, cache);
        cache.Upload(Pic(0), 4, 4, Solid(4, 4, 0, 0, 0, 255));
        cache.Upload(Pic(1), 8, 4, Solid(8, 4, 255, 255, 255, 255, (x, _) => (byte)(x < 4 ? 255 : 0)));      // WIN: opaque left half, transparent right half
        cache.Upload(Pic(2), 4, 4, Solid(4, 4, 255, 0, 0, 255));                                              // KID: solid red

        StageRuntime rt = NewStage();
        Run(rt, "_STCH(3,WIN,240,136,1000,255,50,,CC);");
        Run(rt, "_STCH(4,KID,240,136,1000,255,60,,CC,,,3);");
        Commit(rt);
        var list = new DisplayList();
        list.Reset(0);
        rt.Draw(list);
        list.Seal();
        compositor.Clear(0, 0, 0, 0, 255);
        executor.Execute(list);
        byte[] frame = compositor.Read(compositor.Frame);
        Assert.Equal((255, 0, 0), At(frame, 400, 500));       // inside the window's picture: the masked layer shows
        Assert.Equal((0, 0, 0), At(frame, 1500, 500));        // outside: black background only
        Assert.Equal((255, 0, 0), At(frame, 100, 100));
        Assert.Equal((0, 0, 0), At(frame, 1900, 1000));

        // without the passes the child covers the whole frame (K2's plain emission)
        rt.EffectPasses = false;
        list.Reset(1);
        rt.Draw(list);
        list.Seal();
        compositor.Clear(0, 0, 0, 0, 255);
        executor.Execute(list);
        Assert.Equal((255, 0, 0), At(compositor.Read(compositor.Frame), 1500, 500));
    }

    [Fact]
    public void AnInsetWindowFadesTheLayerOutAtItsEdgesAndTheRidgeModeShrinksItsScissor()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var compositor = new GpuCompositor(ctx.Device.Gl, W, H);
        using var cache = new TextureCache(ctx.Device.Gl);
        var executor = new DisplayListExecutor(compositor, cache);
        cache.Upload(Pic(0), 4, 4, Solid(4, 4, 0, 0, 0, 255));
        cache.Upload(Pic(1), 4, 4, Solid(4, 4, 255, 0, 0, 255));                                              // WIN: solid red
        // BORDER_PARTS child 1 (the 32-unit radius class: 64 x 64 texels): a stand-in whose alpha rises linearly from the border to the centre
        cache.Upload(TextureRef.ForUiTile(UiSet.BorderParts, Tsuki.Engine.Contracts.Language.Ja, 1, -1), 64, 64,
            Solid(64, 64, 255, 255, 255, 0, (x, y) => (byte)Math.Min(255, (Math.Min(Math.Min(x, y), Math.Min(63 - x, 63 - y)) + 1) * 255 / 32)));

        StageRuntime rt = NewStage();
        Run(rt, "_STCH(3,WIN,240,136,1000,255,50,,CC,,,,100,50,600,400,60,0);");              // a16 = 60: a fade of 60 px on every side
        Commit(rt);
        var list = new DisplayList();
        list.Reset(0);
        rt.Draw(list);
        list.Seal();
        compositor.Clear(0, 0, 0, 0, 255);
        executor.Execute(list);
        byte[] frame = compositor.Read(compositor.Frame);
        int RedAt(int x, int y) => At(frame, x, y).R;
        Assert.Equal(0, RedAt(50, 250));                                // outside the window
        Assert.InRange(RedAt(101, 250), 0, 24);                         // the very edge: nearly transparent
        int mid = RedAt(130, 250);                                      // half way through the 60 px band
        Assert.InRange(mid, 100, 160);
        Assert.True(RedAt(110, 250) < mid && mid < RedAt(150, 250), "the fade rises from the edge");
        Assert.Equal(255, RedAt(170, 250));                             // past the band: the whole layer
        Assert.Equal(255, RedAt(400, 200));
        Assert.InRange(RedAt(400, 52), 0, 24);                          // the top edge fades the same way
        Assert.Equal(255, RedAt(400, 120));
        Assert.InRange(RedAt(698, 250), 0, 24);                         // the right edge (x = 699)

        // the ridge mode: the scissor shrinks by the inset (a16 = 20) and no mask is built; the layer is solid right up to the shrunk edge
        StageRuntime ridge = NewStage();
        Run(ridge, "_STCH(3,WIN,240,136,1000,255,50,,CC,,,,100,50,600,400,20,3);");
        Commit(ridge);
        list.Reset(1);
        ridge.Draw(list);
        list.Seal();
        compositor.Clear(0, 0, 0, 0, 255);
        executor.Execute(list);
        frame = compositor.Read(compositor.Frame);
        Assert.Equal(0, RedAt(110, 250));                               // inside the original window but cut by the shrunk scissor (starts at 120)
        Assert.Equal(255, RedAt(600, 250));
    }

    [Fact]
    public void ARuleFadeShowsTheLayerWhereTheRuleIsDark()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var compositor = new GpuCompositor(ctx.Device.Gl, W, H);
        using var cache = new TextureCache(ctx.Device.Gl);
        var executor = new DisplayListExecutor(compositor, cache);
        cache.Upload(Pic(0), 4, 4, Solid(4, 4, 0, 0, 0, 255));
        cache.Upload(Pic(2), 4, 4, Solid(4, 4, 255, 0, 0, 255));
        // RULE_26 (allpac3 entry 85 + 26): a horizontal ramp, dark on the left
        byte[] ramp = new byte[256 * 4 * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                int i = ((y * 256) + x) * 4;
                (ramp[i], ramp[i + 1], ramp[i + 2], ramp[i + 3]) = ((byte)x, (byte)x, (byte)x, 255);
            }
        }

        cache.Upload(TextureRef.ForPictureTile(4, 85 + 26, 0), 256, 4, ramp);

        StageRuntime rt = NewStage();
        Run(rt, "_STCH(3,KID,240,136,1000,128,50,,CC);");          // alpha 128 / 255: half way through the wipe
        Commit(rt);
        Run(rt, "_STTR(3,26);");
        var list = new DisplayList();
        list.Reset(0);
        rt.Draw(list);
        list.Seal();
        compositor.Clear(0, 0, 0, 0, 255);
        executor.Execute(list);
        byte[] frame = compositor.Read(compositor.Frame);
        int left = At(frame, 60, 500).R;                     // rule value 0.03: the layer is fully there (its own alpha 0.5 still scales it)
        int right = At(frame, 1860, 500).R;                  // rule value 0.97: not there yet
        Assert.InRange(left, 120, 136);
        Assert.InRange(right, 0, 8);
        Assert.True(At(frame, 700, 500).R >= At(frame, 1300, 500).R, "the wipe is monotone along the ramp");
    }

    [Fact]
    public void TheNoiseLayerShowsTheNoiseSheetWithTheLayersRectangle()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var compositor = new GpuCompositor(ctx.Device.Gl, W, H);
        using var cache = new TextureCache(ctx.Device.Gl);
        var executor = new DisplayListExecutor(compositor, cache);
        cache.Upload(Pic(0), 4, 4, Solid(4, 4, 0, 0, 0, 255));
        cache.Upload(Pic(2), 4, 4, Solid(4, 4, 255, 255, 255, 255));
        // a recognisable noise sheet: left half 200, right half 50
        byte[] noise = Solid(16, 8, 200, 200, 200, 255);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 8; x < 16; x++)
            {
                int i = ((y * 16) + x) * 4;
                (noise[i], noise[i + 1], noise[i + 2]) = (50, 50, 50);
            }
        }

        cache.Upload(TextureRef.ForUiTile(UiSet.NoiseParts, Language.Ja, 0, -1), 16, 8, noise);
        StageRuntime rt = NewStage();
        Run(rt, "_STCH(3,KID,240,136,1000,255,50,,CC);");
        Run(rt, "_STNS(3,11);");
        Commit(rt);
        var list = new DisplayList();
        list.Reset(0);
        rt.Draw(list);
        list.Seal();
        compositor.Clear(0, 0, 0, 0, 255);
        executor.Execute(list);
        Assert.Equal(0, executor.Execute(list).MissingTextures);
        byte[] frame = compositor.Read(compositor.Frame);
        // the sampled part of the sheet is a window of 1 / 1.2 of it starting near the origin, possibly mirrored: both halves of the sheet show
        int a = At(frame, 200, 500).R, b = At(frame, 1700, 500).R;
        Assert.InRange(a + b, 190, 310);
        Assert.NotEqual(a, b);
    }

    private static (GlContext Ctx, GpuCompositor C, TextureCache Cache) Frame(Action<GpuCompositor> setup)
    {
        GlContext ctx = GlContext.CreateHidden();
        var c = new GpuCompositor(ctx.Device.Gl, W, H) { Filter = SamplingFilter.Nearest };
        setup(c);
        return (ctx, c, new TextureCache(ctx.Device.Gl));
    }

    // a ramp: red grows with x (1 per 8 px), green with y (1 per 5 px)
    private static byte[] Ramp()
    {
        byte[] ramp = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = ((y * W) + x) * 4;
                (ramp[i], ramp[i + 1], ramp[i + 2], ramp[i + 3]) = ((byte)(x / 8), (byte)(y / 5), 0, 255);
            }
        }

        return ramp;
    }

    // the render target holds the new scene (the engine screens capture it first: here the ramp or blue), capture 15 the old frame (red)
    private static byte[] RunScreen(int kind, float progress, TextureRef? rule = null, byte[]? ruleTexels = null, int ruleW = 0, int ruleH = 0, bool ramp = false)
    {
        var (ctx, compositor, cache) = Frame(c =>
        {
            c.Load(c.Frame, ramp ? Ramp() : Solid(W, H, 0, 0, 255, 255));
            c.Load(c.CaptureSurface(15), Solid(W, H, 255, 0, 0, 255, null));
        });
        using (ctx)
        using (compositor)
        using (cache)
        {
            if (rule is { } r && ruleTexels is not null)
            {
                cache.Upload(r, ruleW, ruleH, ruleTexels);
            }

            var list = new DisplayList();
            list.Reset(0);
            TransitionScreens.Frame(list, kind, progress, rule, new Ps4Rand().Next);
            list.Seal();
            new DisplayListExecutor(compositor, cache).Execute(list);
            return compositor.Read(compositor.Frame);
        }
    }

    [Fact]
    public void ASlideShowsTheOldFrameAndTheNewSceneAsMovingWindows()
    {
        // type 23 at p = 0.5: the old frame (red) sits at x = +960 px, the new scene at -960 px (off screen); both are 960 x 544 px
        byte[] f = RunScreen(23, 0.5f);
        Assert.Equal((255, 0, 0), At(f, 1000, 100));
        Assert.Equal((255, 0, 0), At(f, 1900, 540));
        Assert.Equal((0, 0, 255), At(f, 1000, 600));         // below the 544-pixel window: still the frame
        Assert.Equal((0, 0, 255), At(f, 400, 100));          // left of it: the frame
        // type 21 at p = 0.25: the new scene (the ramp, captured) has moved 272 * 0.25 = 68 units = 270 px down: pixel (400, 400) shows ramp (400, 130)
        f = RunScreen(21, 0.25f, ramp: true);
        (int r, int g, _) = At(f, 400, 400);
        Assert.InRange(g, 130 / 5 - 1, (130 / 5) + 1);
        Assert.InRange(r, (400 / 8) - 1, (400 / 8) + 1);
        Assert.InRange(At(f, 1100, 400).G, (400 / 5) - 1, (400 / 5) + 1);   // right of the 960-pixel window: the ramp itself
    }

    [Fact]
    public void TheMosaicMagnifiesATinyPictureWithHardEdges()
    {
        // the target holds a ramp; at p = 0.5 the tile is 14: the corner region of 1280 x 896 pixels shows blocks of 14 x 14 pixels
        byte[] f = RunScreen(18, 0.5f, ramp: true);
        Assert.Equal(At(f, 3, 3), At(f, 13, 13));            // one block
        Assert.NotEqual(At(f, 3, 3), At(f, 300, 300));       // another block, another colour
        Assert.Equal(At(f, 14 * 20, 14 * 20), At(f, (14 * 20) + 13, (14 * 20) + 13));
        Assert.Equal((1500 / 8, 1000 / 5, 0), At(f, 1500, 1000));   // outside the 1280 x 896 region: untouched
    }

    [Fact]
    public void TheWhiteOutDissolvesTheNewSceneInThroughTheRuleInTheLastQuarter()
    {
        var rule = TextureRef.ForPictureTile(4, 90, 0);
        byte[] ramp = new byte[256 * 4 * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                int i = ((y * 256) + x) * 4;
                (ramp[i], ramp[i + 1], ramp[i + 2], ramp[i + 3]) = ((byte)x, (byte)x, (byte)x, 255);
            }
        }

        // 0.6 left: all white; 0.125 left (rule progress 0.5, window 0..1): the new scene (the capture of the frame = blue here) shows where the rule is high
        byte[] white = RunScreen(69, 0.6f, rule, ramp, 256, 4);
        Assert.Equal((255, 255, 255), At(white, 1000, 500));
        byte[] late = RunScreen(69, 0.125f, rule, ramp, 256, 4);
        (int r, int g, int b) = At(late, 1800, 500);                 // bright rule: the new scene (blue)
        Assert.True(b >= 250 && r < 40, $"expected blue, got {(r, g, b)}");
        (r, g, b) = At(late, 20, 500);                               // dark rule: still white
        Assert.True(r > 200 && g > 200, $"expected white, got {(r, g, b)}");
    }

    [Fact]
    public void TheZoomShrinksTheOldFrameTowardsTheCentre()
    {
        byte[] f = RunScreen(70, 0.5f);
        // 960 * X wide: X = 0.1 cos(theta) + 0.5, theta = 4 pi at p = 0.5, so X = 0.6, Y = 0.5: 576 x 272 px around (960, 540)
        Assert.Equal((255, 0, 0), At(f, 960, 540));
        Assert.Equal((255, 0, 0), At(f, 700, 440));
        Assert.Equal((0, 0, 255), At(f, 300, 540));
        Assert.Equal((0, 0, 255), At(f, 960, 200));
    }
}

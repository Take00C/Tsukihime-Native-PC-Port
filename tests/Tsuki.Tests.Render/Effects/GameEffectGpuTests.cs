using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game.Effects;
using Tsuki.Engine.Stage;
using Tsuki.Render.Compositor;
using Tsuki.Render.Textures;
using Tsuki.Tests.Render.Compositor;
using Tsuki.Tests.Render.Smoke;

namespace Tsuki.Tests.Render.Effects;

/// <summary>The game screen effects on the GPU (package K7): the ghost channel split, the focus lines and the choice decoration, with synthetic textures.</summary>
[Collection(SdlCollection.Name)]
public sealed class GameEffectGpuTests
{
    private const int W = 1920;
    private const int H = 1080;

    private sealed class Tables : IEffectTables
    {
        public int FocusEffects => 30;

        public IReadOnlyList<FocusNote> FocusNotes => [];

        // effect 18 (index 14, two keys like the eboot's): 40..79 lines, full alpha, re-rolled every 20 ms, centred on the screen
        public FocusKey FocusKey(int effect, int key) => key switch
        {
            0 => new FocusKey(0, 640, 360, 200, 500, 40, 80, 255, 20),
            1 => new FocusKey(200, 640, 360, 200, 500, 40, 80, 255, 20),
            _ => new FocusKey(-1, -1, -1, -1, -1, -1, -1, -1, -1),
        };
    }

    private static byte[] Frame(Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        byte[] px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = ((y * W) + x) * 4;
                (px[i], px[i + 1], px[i + 2]) = pixel(x, y);
                px[i + 3] = 255;
            }
        }

        return px;
    }

    [Fact]
    public void TheGhostSplitsTheChannelsAlongTheSwingingDirection()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var compositor = new GpuCompositor(ctx.Device.Gl, W, H) { Filter = SamplingFilter.Nearest };
        using var cache = new TextureCache(ctx.Device.Gl);
        var executor = new DisplayListExecutor(compositor, cache);
        // mid grey with a lighter square: the overlay of the shifted channels shows only on mid tones, so the ground is 128 and the square 200
        byte[] frame = Frame((x, y) => x is >= 800 and < 1100 && y is >= 400 and < 700 ? ((byte)200, (byte)200, (byte)200) : ((byte)128, (byte)128, (byte)128));
        var ghost = new GhostEffect();
        ghost.Start(0, 0, reset: true);
        ghost.Step(60, 0, 0);
        Assert.True(ghost.Alpha > 0.9f);
        float k = ghost.Amplitude * 2f;
        Assert.True(k > 2f, $"amplitude {k}");

        compositor.Load(compositor.Frame, frame);
        var list = new DisplayList();
        list.Reset(0);
        ghost.Draw(list, null);
        list.Seal();
        Assert.True(list.Count > 10);
        executor.Execute(list);
        byte[] outFrame = compositor.Read(compositor.Frame);
        int changed = 0;
        int fringes = 0;
        for (int y = 380; y < 720; y++)
        {
            for (int x = 780; x < 1120; x++)
            {
                int i = ((y * W) + x) * 4;
                if (outFrame[i] != frame[i] || outFrame[i + 1] != frame[i + 1] || outFrame[i + 2] != frame[i + 2])
                {
                    changed++;
                }

                if (Math.Abs(outFrame[i] - outFrame[i + 2]) > 8)
                {
                    fringes++;                                  // red and blue differ: the channels were shifted apart
                }
            }
        }

        Assert.True(changed > 1000, $"{changed} pixels changed");
        Assert.True(fringes > 100, $"{fringes} fringe pixels");
        // far from the edge the flat ground and the flat square keep their grey (the overlay of a flat image over itself)
        Assert.InRange(outFrame[((100 * W) + 100) * 4], 100, 160);
    }

    [Fact]
    public void TheFocusLinesAreDrawnAsAlphaBlendedStrips()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var compositor = new GpuCompositor(ctx.Device.Gl, W, H);
        using var cache = new TextureCache(ctx.Device.Gl);
        var executor = new DisplayListExecutor(compositor, cache);
        byte[] strip = new byte[1920 * 128 * 4];
        Array.Fill(strip, (byte)255);                           // an all-white strip: every line is a white bar
        cache.Upload(TextureRef.ForUiTile(UiSet.EffectParts, Language.Ja, 3, -1), 1920, 128, strip);
        var focus = new FocusEffect(new Tables(), new Ps4Rand(3));
        focus.Start(18, 0);
        for (int i = 0; i < 12; i++)
        {
            focus.Step(1);
        }

        Assert.True(focus.ParticleCount >= 40);
        var list = new DisplayList();
        list.Reset(0);
        focus.Draw(list);
        list.Seal();
        compositor.Clear(0, 0, 0, 0, 255);
        ExecuteStats stats = executor.Execute(list);
        Assert.Equal(0, stats.MissingTextures);
        byte[] f = compositor.Read(compositor.Frame);
        int lit = 0;
        for (int i = 0; i < f.Length; i += 4)
        {
            if (f[i] > 40)
            {
                lit++;
            }
        }

        // each of 40+ lines is 2 px thick and hundreds of px long: thousands of lit pixels, far from the whole frame
        Assert.InRange(lit, 2_000, 600_000);
    }

    [Fact]
    public void TheChoiceDecorationDrawsAdditiveQuadsThatBrightenTheScene()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var compositor = new GpuCompositor(ctx.Device.Gl, W, H);
        using var cache = new TextureCache(ctx.Device.Gl);
        var executor = new DisplayListExecutor(compositor, cache);
        for (int child = 0; child < 6; child++)
        {
            var white = new byte[64 * 16 * 4];
            Array.Fill(white, (byte)255);
            cache.Upload(TextureRef.ForUiTile(UiSet.SelectParts, Language.Ja, child, -1), 64, 16, white);
        }

        var glow = new ChoiceGlow(_ => (64, 16));
        var list = new DisplayList();
        list.Reset(0);
        var view = new ChoiceView(3, Cursor: 0, Previous: -1, Alpha: 1f, Highlight: 1f, Flash: 0.4f, X0: 30, Y0: 100, UnitX: 3, UnitY: 3);
        glow.DrawLine(list, view, 0, (_, _) => null, (_, _, _, _) => { });
        glow.DrawDecided(list, view);
        list.Seal();
        compositor.Load(compositor.Frame, Frame((_, _) => (60, 60, 60)));
        ExecuteStats stats = executor.Execute(list);
        Assert.Equal(0, stats.MissingTextures);
        byte[] f = compositor.Read(compositor.Frame);
        int brighter = 0;
        for (int i = 0; i < f.Length; i += 4)
        {
            if (f[i] > 62)
            {
                brighter++;
            }
        }

        Assert.True(brighter > 100, $"{brighter} pixels brighter than the scene");
        // the vignette of a choice dims (not additive): drawn in effect 0 over the whole frame
        var dim = new DisplayList();
        dim.Reset(0);
        glow.DrawVignette(dim, view with { Alpha = 0.5f });
        dim.Seal();
        var black = new byte[64 * 16 * 4];
        for (int i = 3; i < black.Length; i += 4)
        {
            black[i] = 255;
        }

        cache.Upload(TextureRef.ForUiTile(UiSet.SelectParts, Language.Ja, 5, -1), 64, 16, black);
        compositor.Load(compositor.Frame, Frame((_, _) => (200, 200, 200)));
        executor.Execute(dim);
        int v = compositor.Read(compositor.Frame)[((8 * W) + 30) * 4];
        Assert.InRange(v, 95, 106);                                   // 200 * (1 - 0.5): the vignette is a 64 x 16 patch here
    }
}

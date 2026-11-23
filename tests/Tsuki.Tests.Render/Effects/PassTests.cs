using Tsuki.Engine.Contracts;
using Tsuki.Render.Compositor;
using Tsuki.Render.Textures;
using Tsuki.Tests.Render.Compositor;
using Tsuki.Tests.Render.Smoke;

namespace Tsuki.Tests.Render.Effects;

/// <summary>
/// The shader passes the stage effects need (package K7): the Clear command, the offscreen alpha mask (<see cref="QuadFx.Mask"/>), the
/// multiply-into-mask blend, the per-quad rule parameters, the STRA haze displacement and the nearest filter. Functional pixel tests on a hidden
/// context (no timing, no gpu.lock).
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class PassTests
{
    private const int Size = 64;

    [Fact]
    public void ClearFillsATargetWithAColour()
    {
        using Rig rig = new();
        rig.Run(
            new DrawCmd { Kind = DrawKind.Clear, Target = 0, V0 = new Vertex(0, 0, 0, 0, Quads.Rgba(10, 20, 30, 255)) },
            new DrawCmd { Kind = DrawKind.Clear, Target = 3, V0 = new Vertex(0, 0, 0, 0, Quads.Rgba(200, 100, 50, 25)) });
        Assert.Equal((10, 20, 30, 255), rig.Pixel(rig.Compositor.Frame, 5, 7));
        Assert.Equal((200, 100, 50, 25), rig.Pixel(rig.Compositor.Target(3), 63, 63));
    }

    [Fact]
    public void AQuadIsMultipliedByTheAlphaOfTheMaskTarget()
    {
        using Rig rig = new();
        var cmds = new List<DrawCmd>
        {
            Clear(0, 0, 0, 0, 255),
            Clear(1, 0, 0, 0, 0),
            // the mask: alpha 1 on the left half of target 1 (effect 58 = alpha := source alpha, colour kept)
            Quads.Fill(0, 0, 32, Size, Quads.White(), DrawEffect.AlphaSet, target: 1),
            // a white quad over the whole frame, through the mask
            Quads.Fill(0, 0, Size, Size, Quads.White(), 0) with { Fx = QuadFx.Masked(1) },
        };
        rig.Run([.. cmds]);
        Assert.Equal((255, 255, 255), RGB(rig.Pixel(rig.Compositor.Frame, 10, 10)));
        Assert.Equal((0, 0, 0), RGB(rig.Pixel(rig.Compositor.Frame, 50, 10)));
    }

    [Fact]
    public void ThePartialAlphaOfTheMaskScalesTheQuad()
    {
        using Rig rig = new();
        rig.Run(
            Clear(0, 0, 0, 0, 255),
            Clear(1, 0, 0, 0, 0),
            Quads.Fill(0, 0, Size, Size, Quads.White(128), DrawEffect.AlphaSet, target: 1),
            Quads.Fill(0, 0, Size, Size, Quads.White(), 0) with { Fx = QuadFx.Masked(1) });
        int v = RGB(rig.Pixel(rig.Compositor.Frame, 20, 20)).R;
        Assert.InRange(v, 126, 130);
    }

    [Fact]
    public void TheMultiplyStateNarrowsTheMaskAndKeepsItsColour()
    {
        using Rig rig = new();
        rig.Run(
            Clear(1, 9, 8, 7, 255),
            Quads.Fill(0, 0, 32, Size, Quads.White(128), 0, BlendStates.MaskMultiply, target: 1));
        (int r, int g, int b, int a) = rig.Pixel(rig.Compositor.Target(1), 10, 10);
        Assert.Equal((9, 8, 7), (r, g, b));
        Assert.InRange(a, 126, 130);                                  // 255 * (128 / 255)
        Assert.Equal(255, rig.Pixel(rig.Compositor.Target(1), 50, 10).A);   // outside the quad nothing is multiplied
    }

    [Fact]
    public void TheRuleParametersOfAQuadEqualThoseOfItsTexture()
    {
        byte[] gradient = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int i = ((y * Size) + x) * 4;
                gradient[i] = gradient[i + 1] = gradient[i + 2] = (byte)(x * 4);
                gradient[i + 3] = 255;
            }
        }

        TransitionParams tp = TransitionParams.ForRule(0.4f) with { Invert = 1f };
        byte[] viaCache, viaFx;
        using (Rig a = new())
        {
            a.Cache.Upload(Rule, Size, Size, gradient);
            a.Cache.SetTransitionParams(Rule, tp);
            a.Run(Clear(0, 0, 0, 0, 0), Quads.Textured(Rule, 0, 0, Size, Size, 0, 0, 1, 1, Quads.White(), DrawEffect.AlphaSet));
            viaCache = a.Compositor.Read(a.Compositor.Frame);
        }

        using (Rig b = new())
        {
            b.Cache.Upload(Rule, Size, Size, gradient);
            b.Run(Clear(0, 0, 0, 0, 0), Quads.Textured(Rule, 0, 0, Size, Size, 0, 0, 1, 1, Quads.White(), DrawEffect.AlphaSet) with { Fx = QuadFx.ForRule(tp.Lo, tp.Hi, tp.Invert) });
            viaFx = b.Compositor.Read(b.Compositor.Frame);
        }

        Assert.Equal(viaCache, viaFx);
        // inverted: dark rule values keep the layer (alpha 1), bright ones drop it
        Assert.Equal(255, viaFx[(10 * Size * 4) + 3]);
        Assert.InRange(viaFx[(10 * Size * 4) + ((Size - 1) * 4) + 3], 0, 40);
    }

    [Fact]
    public void TheHazeDisplacesTheTextureAlongTheWave()
    {
        // a picture whose texel x is its own value: a horizontal displacement shows as a shifted value
        byte[] ramp = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int i = ((y * Size) + x) * 4;
                ramp[i] = (byte)(x * 3);
                ramp[i + 1] = (byte)(y * 3);
                ramp[i + 2] = 0;
                ramp[i + 3] = 255;
            }
        }

        TextureRef pic = TextureRef.ForPictureTile(0, 1, 0);
        const float amp = 0.1f;                                        // uv: 6.4 texels
        const float waves = 2f;
        const float phase = 0.25f;
        var fx = new QuadFx(QuadPass.Haze, 0, true, amp, waves, phase, 0f, 0f, 1f, 0f, 1f);   // no y wave
        byte[] plain, hazy;
        using (Rig a = new() { Nearest = true })
        {
            a.Cache.Upload(pic, Size, Size, ramp);
            a.Run(Clear(0, 0, 0, 0, 255), Quads.Textured(pic, 0, 0, Size, Size, 0, 0, 1, 1, Quads.White(), 0));
            plain = a.Compositor.Read(a.Compositor.Frame);
        }

        using (Rig b = new() { Nearest = true })
        {
            b.Cache.Upload(pic, Size, Size, ramp);
            b.Run(Clear(0, 0, 0, 0, 255), Quads.Textured(pic, 0, 0, Size, Size, 0, 0, 1, 1, Quads.White(), 0) with { Fx = fx });
            hazy = b.Compositor.Read(b.Compositor.Frame);
        }

        Assert.Equal(ramp[((20 * Size) + 30) * 4], plain[((20 * Size) + 30) * 4]);
        int moved = 0;
        for (int y = 2; y < Size - 2; y++)
        {
            float v = (y + 0.5f) / Size;
            float dx = MathF.Sin(MathF.Tau * (Fract(v * waves) + Fract(phase))) * amp;
            int x = 30;
            int want = (int)MathF.Floor((((x + 0.5f) / Size) + dx) * Size);
            want = Math.Clamp(want, 0, Size - 1);
            int got = hazy[((y * Size) + x) * 4] / 3;
            Assert.InRange(got, want - 1, want + 1);
            if (got != x)
            {
                moved++;
            }

            Assert.Equal(y, hazy[((y * Size) + x) * 4 + 1] / 3);             // no y displacement
        }

        Assert.True(moved > 30, $"only {moved} rows were displaced");
    }

    [Fact]
    public void ABlurPassMixesTheTexelsAlongItsAxisByTheirGaussianWeights()
    {
        // package K16: the system menu's blurred backdrop is two QuadPass.Blur passes through a capture surface (docs/design/knife_port.md: knife-render fs_blur)
        const float sigma = 2f;
        const int n = 6;
        double Weight(int k) => Math.Exp(-(k * k) / (2.0 * sigma * sigma));
        double sum = Enumerable.Range(-n, (2 * n) + 1).Sum(Weight);
        int Expect(int firstWhiteTapOffset, int lastWhiteTapOffset) =>
            (int)Math.Round(255.0 * Enumerable.Range(firstWhiteTapOffset, lastWhiteTapOffset - firstWhiteTapOffset + 1).Sum(Weight) / sum);

        // white on the left half of the frame, black on the right; blurred along x into target 3
        using (Rig rig = new())
        {
            var fx = new QuadFx(QuadPass.Blur, 0, true, 1f / Size, 0f, sigma, n);
            rig.Run(
                Clear(0, 0, 0, 0, 255),
                Quads.Fill(0, 0, 32, Size, Quads.White(), 0),
                Quads.Capture(4),
                Quads.Textured(TextureRef.ForCapture(4), 0, 0, Size, Size, 0, 0, 1, 1, Quads.White(), 0, target: 3) with { Fx = fx });
            RenderTarget t = rig.Compositor.Target(3);
            Assert.InRange(rig.Pixel(t, 31, 10).R, Expect(-n, 0) - 2, Expect(-n, 0) + 2);        // the last white texel: its taps k <= 0 are white
            Assert.InRange(rig.Pixel(t, 32, 10).R, Expect(-n, -1) - 2, Expect(-n, -1) + 2);      // the first black texel: its taps k <= -1 are white
            Assert.Equal(255, rig.Pixel(t, 6, 10).R);                                              // far from the edge nothing changes
            Assert.Equal(0, rig.Pixel(t, 57, 10).R);
            Assert.Equal(rig.Pixel(t, 31, 3).R, rig.Pixel(t, 31, 60).R);                          // along x only: every row alike
        }

        // white on the top half, blurred along y, then blended over the frame at half strength (the fade of the blur)
        using (Rig rig = new())
        {
            var fx = new QuadFx(QuadPass.Blur, 0, true, 0f, 1f / Size, sigma, n);
            rig.Run(
                Clear(0, 0, 0, 0, 255),
                Quads.Fill(0, 0, Size, 32, Quads.White(), 0),
                Quads.Capture(4),
                Quads.Textured(TextureRef.ForCapture(4), 0, 0, Size, Size, 0, 0, 1, 1, Quads.White(128), 0, blendState: 1) with { Fx = fx });
            RenderTarget f = rig.Compositor.Frame;
            int edge = Expect(-n, 0);
            int want = (int)Math.Round((255 + edge) / 2.0);                                         // over the unblurred 255 at alpha 128 / 255
            Assert.InRange(rig.Pixel(f, 10, 31).R, want - 3, want + 3);
            Assert.Equal(255, rig.Pixel(f, 10, 5).R);
            Assert.InRange(rig.Pixel(f, 10, 58).R, 0, 1);
        }
    }

    [Fact]
    public void TheNearestFilterOfAQuadKeepsTheTexelsHard()
    {
        byte[] two = [255, 0, 0, 255, 0, 0, 255, 255];                       // 2 x 1: red, blue
        TextureRef pic = TextureRef.ForPictureTile(0, 2, 0);
        byte[] linear, nearest;
        using (Rig a = new())
        {
            a.Cache.Upload(pic, 2, 1, two);
            a.Run(Clear(0, 0, 0, 0, 255), Quads.Textured(pic, 0, 0, Size, Size, 0, 0, 1, 1, Quads.White(), 0));
            linear = a.Compositor.Read(a.Compositor.Frame);
        }

        using (Rig b = new())
        {
            b.Cache.Upload(pic, 2, 1, two);
            b.Run(Clear(0, 0, 0, 0, 255), Quads.Textured(pic, 0, 0, Size, Size, 0, 0, 1, 1, Quads.White(), 0) with { Fx = new QuadFx(Nearest: true) });
            nearest = b.Compositor.Read(b.Compositor.Frame);
        }

        int mid = ((10 * Size) + 31) * 4;
        Assert.True(linear[mid] is > 0 and < 255 || linear[mid + 2] is > 0 and < 255, "the linear filter should blend the two texels at the seam");
        Assert.Contains(nearest[mid], new byte[] { 0, 255 });
    }

    // issue #14: a glow like IMG2230, one colour all over, whose alpha falls to 0 while the colour goes on: texel 0 opaque, texel 1 half transparent, texels 2 and 3 transparent
    private static readonly byte[] Glow = [40, 140, 220, 255, 40, 140, 220, 128, 40, 140, 220, 0, 40, 140, 220, 0];

    private static readonly (int R, int G, int B) Backdrop = (90, 110, 130);

    /// <summary>The frame after the glow is drawn with <paramref name="word"/> over the backdrop through a full mask (target 1), held or not; nearest filtering, 16 px per texel.</summary>
    private static byte[] DrawGlow(int word, bool held)
    {
        TextureRef pic = TextureRef.ForPictureTile(0, 3, 0);
        using Rig rig = new() { Nearest = true };
        rig.Cache.Upload(pic, 4, 1, Glow);
        rig.Run(
            Clear(0, (byte)Backdrop.R, (byte)Backdrop.G, (byte)Backdrop.B, 255),
            Clear(1, 0, 0, 0, 255),
            Quads.Textured(pic, 0, 0, Size, Size, 0, 0, 1, 1, Quads.White(), word) with { Fx = QuadFx.Masked(1, held) });
        return rig.Compositor.Read(rig.Compositor.Frame);
    }

    private static (int R, int G, int B) At(byte[] frame, int x) => (frame[((20 * Size) + x) * 4], frame[(((20 * Size) + x) * 4) + 1], frame[(((20 * Size) + x) * 4) + 2]);

    [Theory]
    [InlineData(1)]         // add
    [InlineData(4)]         // darken
    [InlineData(5)]         // dodge
    [InlineData(6)]         // lighten
    [InlineData(7)]         // multiplicative
    [InlineData(8)]         // opaque
    [InlineData(9)]         // screen
    [InlineData(10)]        // sub
    [InlineData(0x10A)]     // sub with the _STBR flag: blending off, the colour still ignores the alpha
    public void AHeldQuadLeavesTheBackdropWhereItsPictureIsTransparent(int word)
    {
        byte[] plain = DrawGlow(word, held: false);
        byte[] held = DrawGlow(word, held: true);
        Assert.NotEqual(Backdrop, At(plain, 40));                  // not held: the colour under alpha 0 lands on the frame (compositing.md 2.3), the hard disc of issue #14
        Assert.Equal(Backdrop, At(held, 40));                      // held: alpha 0 leaves the backdrop alone
        Assert.Equal(Backdrop, At(held, 56));
        Assert.Equal(At(plain, 8), At(held, 8));                   // an opaque texel keeps its value exactly
        (int R, int G, int B) full = At(plain, 24);                // the half-transparent texel: half way from the backdrop to the plain result
        (int R, int G, int B) half = At(held, 24);
        const double a = 128 / 255.0;
        Assert.InRange(half.R, (int)(Backdrop.R + (a * (full.R - Backdrop.R))) - 2, (int)(Backdrop.R + (a * (full.R - Backdrop.R))) + 2);
        Assert.InRange(half.G, (int)(Backdrop.G + (a * (full.G - Backdrop.G))) - 2, (int)(Backdrop.G + (a * (full.G - Backdrop.G))) + 2);
        Assert.InRange(half.B, (int)(Backdrop.B + (a * (full.B - Backdrop.B))) - 2, (int)(Backdrop.B + (a * (full.B - Backdrop.B))) + 2);
    }

    [Theory]
    [InlineData(0)]         // basic over: weighted by the alpha through the blend state
    [InlineData(2)]         // addalpha: never held in the scripts, left as it is
    [InlineData(0x105)]     // dodge, multiplicative and screen with the _STBR flag: k = alpha a already
    [InlineData(0x107)]
    [InlineData(0x109)]
    [InlineData(11)]        // the PS family: weighted by the alpha through the blend state
    [InlineData(15)]        // diff5 and dodge5 use alpha a
    [InlineData(17)]
    [InlineData(21)]
    [InlineData(23)]
    public void HeldChangesNothingInTheOtherModes(int word) =>
        Assert.Equal(DrawGlow(word, held: false), DrawGlow(word, held: true));

    private static readonly TextureRef Rule = TextureRef.ForPictureTile(4, 99, 0);

    private static float Fract(float x) => x - MathF.Floor(x);

    private static (int R, int G, int B) RGB((int R, int G, int B, int A) p) => (p.R, p.G, p.B);

    private static DrawCmd Clear(byte target, byte r, byte g, byte b, byte a) =>
        new() { Kind = DrawKind.Clear, Target = target, V0 = new Vertex(0, 0, 0, 0, Quads.Rgba(r, g, b, a)) };

    private sealed class Rig : IDisposable
    {
        private readonly GlContext _ctx = GlContext.CreateHidden();

        public Rig()
        {
            Compositor = new GpuCompositor(_ctx.Device.Gl, Size, Size);
            Cache = new TextureCache(_ctx.Device.Gl);
            Executor = new DisplayListExecutor(Compositor, Cache);
        }

        public GpuCompositor Compositor { get; }

        public TextureCache Cache { get; }

        public DisplayListExecutor Executor { get; }

        public bool Nearest
        {
            init => Compositor.Filter = value ? SamplingFilter.Nearest : SamplingFilter.Linear;
        }

        public void Run(params DrawCmd[] commands)
        {
            var list = new DisplayList();
            list.Reset(0);
            foreach (DrawCmd c in commands)
            {
                list.Add(c);
            }

            Executor.Execute(list);
        }

        public (int R, int G, int B, int A) Pixel(RenderTarget target, int x, int y)
        {
            byte[] px = Compositor.Read(target);
            int i = ((y * target.Width) + x) * 4;
            return (px[i], px[i + 1], px[i + 2], px[i + 3]);
        }

        public void Dispose()
        {
            Cache.Dispose();
            Compositor.Dispose();
            _ctx.Dispose();
        }
    }
}

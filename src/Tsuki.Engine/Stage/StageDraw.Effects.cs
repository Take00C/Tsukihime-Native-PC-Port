// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/stage.rs (Haze::params, Stage::noise_pass)
using Tsuki.Engine.Contracts;
using static Tsuki.Engine.Stage.StageMath;

namespace Tsuki.Engine.Stage;

// The passes K2 left undrawn (docs/status/knife/K2.md open issue 3), as display-list commands: the offscreen alpha mask of a window layer and of a
// rule fade (STCH mask, STTR), the STRA haze and the STNS noise. The PS4 documents give the state (layers.md 4.2, 9) but not the picture, so the
// shapes follow knife's stage.rs (Haze::params, noise_pass, the group / rule paths of Stage::draws) and the PS4 numbers where we have them
// (compositing.md 2.4: the rule window; layers.md 9: the group pass draws the window layer at alpha 1 and masks the layers that name it).
public sealed partial class StageRuntime
{
    /// <summary>The render target that holds the alpha mask of the group being drawn (cleared at the start of each group; targets 2..7 belong to the screen effects).</summary>
    public const byte MaskTarget = 1;

    private readonly List<DrawCmd> _quads = [];
    private bool _maskBuilt;
    private long _noiseFrame;

    /// <summary>True when the group pass (offscreen masks, rule fades), the haze and the noise are drawn. On by default; off reproduces K2's plain emission.</summary>
    public bool EffectPasses { get; set; } = true;

    /// <summary>The rule number (1..49) and the mirror bits (1 = x, 2 = y) of a layer's <c>_STTR</c>, or null when it has none.</summary>
    private static (int Rule, int Flip)? RuleOf(LayerRecord r)
    {
        if (r.Sttr < 0)
        {
            return null;
        }

        int n = (r.Sttr & 0xFFF) + 1;
        return n is >= 1 and <= 49 ? (n, (r.Sttr >> 12) & 3) : null;
    }

    private static DrawCmd ClearTarget(byte target) => new() { Kind = DrawKind.Clear, Target = target, V0 = new Vertex(0, 0, 0, 0, 0u) };

    /// <summary>
    /// Appends the mask of a layer that has a rule fade: the rule picture drawn full screen into the mask target with the rule window of the layer's
    /// alpha (the layer fades by wiping through the rule, dark rule values first; effect 58 = alpha := the quad's alpha, the engine's own mask pass).
    /// </summary>
    private void AddRuleMask(DisplayList list, (int Rule, int Flip) rule, float alpha)
    {
        (float lo, float hi) = TransitionScreens.RuleWindow(1f - alpha);
        float u0 = (rule.Flip & 1) != 0 ? 1f : 0f, u1 = 1f - u0;
        float v0 = (rule.Flip & 2) != 0 ? 1f : 0f, v1 = 1f - v0;
        DrawCmd q = Textured(TextureRef.ForPictureTile(4, 85 + rule.Rule, 0), 0, 0, 1920, 1080, u0, v0, u1, v1, White(), 58) with
        {
            Target = MaskTarget,
            Fx = QuadFx.ForRule(lo, hi, 1f),
        };
        list.Add(q);
    }

    /// <summary>The silhouette of a layer's quads in the mask target: replaces the mask's alpha (state 4) or multiplies it (extension state).</summary>
    private void AddSilhouette(DisplayList list, bool multiply)
    {
        foreach (DrawCmd q in _quads)
        {
            DrawCmd s = q with { Target = MaskTarget, Fx = default };
            if (multiply)
            {
                s = s with { EffectWord = 0, BlendState = 0x80 };           // BlendStates.MaskMultiply: the target alpha times the quad alpha, colour kept
                s = s with { V0 = s.V0 with { Rgba = s.V0.Rgba | 0xFF000000u }, V1 = s.V1 with { Rgba = s.V1.Rgba | 0xFF000000u }, V2 = s.V2 with { Rgba = s.V2.Rgba | 0xFF000000u }, V3 = s.V3 with { Rgba = s.V3.Rgba | 0xFF000000u } };
            }
            else
            {
                s = s with { EffectWord = 58, BlendState = (byte)EffectTables.BlendStateOf(58) };
            }

            list.Add(s);
        }
    }

    /// <summary>
    /// The STRA haze of a layer as the quad inputs of one tile (knife <c>Haze::params</c>, the PS4 table values): two waves along the picture's v axis
    /// displace the texture coordinates, x by a sine, y by a cosine. The wave coordinate is the picture-space v, so tiles of one picture agree.
    /// </summary>
    private static QuadFx HazeFx(HazeState h, PictureLayout layout, PictureFileLayout file, TileQuad t, byte mask)
    {
        float[] e = h.Entry;
        float pictureW = Math.Max(1, layout.Width), pictureH = Math.Max(1, layout.Height);
        float seconds = h.Phase / 60f;
        float amplitude;
        if (h.FadeIn != 0 && e[9] > 0f)
        {
            int ms = Math.Min(1000 * h.Phase / 60, (int)e[9]);
            amplitude = (int)(ms * 1000f / e[9]);
        }
        else if (h.FadeTotal >= 1)
        {
            amplitude = (int)(h.FadeLeft * 1000f / h.FadeTotal);
        }
        else
        {
            amplitude = 1000f;
        }

        float scale = 1920f * 0.00097656f * 0.001f * amplitude;
        float ax = scale * e[6] * e[2];
        float ay = scale * e[6] * e[5];
        float waveX = (e[0] + (e[7] * 0.1f)) * (pictureH / 1080f) * 0.5f;
        float waveY = (e[3] + (e[7] * 0.1f)) * (pictureW / 1920f) * 0.5f;
        float phaseX = Fract(e[8] * 0.33333f * (seconds + e[1]));
        float phaseY = Fract(e[8] * 0.33333f * (seconds + e[4]));
        float tileW = t.X1 - t.X0, tileH = t.Y1 - t.Y0;
        float du = t.U1 - t.U0, dv = t.V1 - t.V0;
        float axUv = tileW > 0f ? ax * du / tileW : 0f;
        float ayUv = tileH > 0f ? ay * dv / tileH : 0f;
        float rowsPerV = Math.Abs(dv) > 1e-9f ? tileH / dv : 0f;
        float sScale = rowsPerV / pictureH;
        float sOffset = (file.OffsetY + t.Y0 - (t.V0 * rowsPerV)) / pictureH;
        return new QuadFx(QuadPass.Haze, mask, false, axUv, waveX, phaseX, sOffset, ayUv, waveY, phaseY, sScale);
    }

    private static float Fract(float x) => x - MathF.Floor(x);

    /// <summary>
    /// The STNS noise of a layer (knife <c>noise_pass</c>): the layer shows the NOISE_PARTS picture (a 1920 x 1080 grey grain, two pictures) instead of
    /// its own, scaled 1.2 and shifted by a fresh random offset every frame (every third frame for <c>stns</c> 1 and 2), mirrored on alternate ticks.
    /// </summary>
    private DrawCmd NoiseQuad(LayerRecord r, Affine m, PictureLayout layout, uint rgba, int word, PixelRect? window, QuadFx fx)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (PictureFileLayout file in layout.Files)
        {
            foreach (TileQuad t in file.Tiles)
            {
                minX = Math.Min(minX, t.X0 + file.OffsetX);
                minY = Math.Min(minY, t.Y0 + file.OffsetY);
                maxX = Math.Max(maxX, t.X1 + file.OffsetX);
                maxY = Math.Max(maxY, t.Y1 + file.OffsetY);
            }
        }

        (double ax, double ay) = m.Apply(minX, minY);
        (double bx, double by) = m.Apply(maxX, minY);
        (double cx, double cy) = m.Apply(minX, maxY);
        (double dx, double dy) = m.Apply(maxX, maxY);
        int period = r.Stns > 2 ? 1 : 3;
        long tick = _noiseFrame / period;
        uint seed = unchecked((uint)_noiseFrame);
        float fx0 = NoiseHash(seed, 1) * 160f / 2304f;                // the offset is up to -160 x -90 px of the 1.2 x scaled picture
        float fy0 = NoiseHash(seed, 2) * 90f / 1296f;
        float span = 1f / 1.2f;
        float u0 = fx0, u1 = fx0 + span;
        if ((tick & 1) == 1)
        {
            (u0, u1) = (u1, u0);
        }

        TextureRef sheet = TextureRef.ForUiTile(UiSet.NoiseParts, Tsuki.Engine.Contracts.Language.Ja, r.Stns % 10 == 2 ? 1 : 0, -1);
        return new DrawCmd
        {
            Kind = DrawKind.TexturedQuad,
            Texture = sheet,
            V0 = new Vertex((float)ax, (float)ay, u0, fy0, rgba),
            V1 = new Vertex((float)bx, (float)by, u1, fy0, rgba),
            V2 = new Vertex((float)cx, (float)cy, u0, fy0 + span, rgba),
            V3 = new Vertex((float)dx, (float)dy, u1, fy0 + span, rgba),
            EffectWord = unchecked((short)word),
            BlendState = (byte)EffectTables.BlendStateOf(word),
            Clip = window,
            Fx = fx,
        };
    }

    private static float NoiseHash(uint seed, uint k)
    {
        uint v = (seed * 0x9E3779B1u) + (k * 0x85EBCA77u);
        v ^= v >> 15;
        v *= 0x2C1B3C6Du;
        v ^= v >> 12;
        v *= 0x297A2D39u;
        v ^= v >> 15;
        return (v & 0xFFFF) / 65535f;
    }
}

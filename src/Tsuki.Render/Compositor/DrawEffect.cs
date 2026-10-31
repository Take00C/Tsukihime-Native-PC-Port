// Draw-mode word decoding, the transition parameters and the _FADS numbers (docs/formats/compositing.md
// 3, 5.1, 7.2, 7.4), lifted from spike S3 Effects.cs and ported from tools/tsuki/compose.py (effect,
// rule_params, fads_step, fads_vertex_alpha, select_shader).
using Tsuki.Render.Shaders;

namespace Tsuki.Render.Compositor;

/// <summary>
/// What the quad draw 0xD2BE0 and DisplaySurface_Draw 0xF12F0 derive from a draw-mode word
/// (<c>compose.effect</c>): effect id, 0x100 flag, shader mode (+0x608), strength (+0x60C), blend
/// state (+0x518) and P2 (+0x604).
/// </summary>
/// <param name="E">Draw effect id 0..67 (<c>word &amp; 0xFF</c>, 0 when it is 0x44 or more).</param>
/// <param name="Flag">The 0x100 bit was set and <see cref="E"/> is in the flag set 0xA1A800286A0 (blending off).</param>
/// <param name="ShaderMode">Surface blend mode 0..25 (0 = normal shader selection).</param>
/// <param name="Strength">P1 = strength; P0 = 1 - strength.</param>
/// <param name="BlendState">Blend state 0..13 of table 0x2D8030.</param>
/// <param name="P2">P2 of the blend shaders (0 for modes 5, 7, 9, 10 with blending off).</param>
public readonly record struct DrawEffect(int E, bool Flag, int ShaderMode, float Strength, int BlendState, float P2)
{
    // 0x2D5978, index e-1 (shader mode)
    private static readonly int[] EffectMode =
    [
        1, 2, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25,
        0, 1, 2, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25,
    ];

    // 0x2D55E0, index e (blend state): 26 x 1, 6, 1, 1, 6, 22 x 1, then effects 52..67
    private static readonly int[] EffectBlendState = BuildBlendState();

    // 0xA1A800286A0: effects whose 0x100 flag turns blending off (0xD9FB9)
    private const long FlagSet = 0xA1A800286A0;

    /// <summary>Effect 0: normal alpha-over (state 1).</summary>
    public const int Normal = 0;

    /// <summary>Effect 26: basic shader through the target alpha (state 6), the rule wipe's capture pass.</summary>
    public const int ThroughDstAlpha = 26;

    /// <summary>Effect 52: additive (state 2).</summary>
    public const int Add = 52;

    /// <summary>Effect 55: alpha := 0 (state 10).</summary>
    public const int AlphaClear = 55;

    /// <summary>Effect 56: alpha := max(sa, da) (state 11), the transition start fill.</summary>
    public const int AlphaUnion = 56;

    /// <summary>Effect 57: alpha := min(sa, da) (state 5).</summary>
    public const int AlphaMin = 57;

    /// <summary>Effect 58: alpha := sa (state 4), the rule mask pass.</summary>
    public const int AlphaSet = 58;

    /// <summary>True for the blend shaders, which read the backdrop D (every shader mode except 0 and 3).</summary>
    public bool ReadsBackdrop => CompositorShaders.ReadsBackdrop(ShaderMode);

    /// <summary><c>compose.effect(word)</c>.</summary>
    public static DrawEffect Decode(int word)
    {
        int low = word & 0xFF;
        int e = low < 0x44 ? low : 0;
        int rest = word & unchecked((int)0xFFFFFEFF);
        bool flag = (word & 0x100) != 0 && rest >= 0 && rest <= 0x2B && ((FlagSet >> rest) & 1) != 0;
        int mode = 0;
        float strength = 0f;
        if (e is >= 1 and <= 51)
        {
            mode = EffectMode[e - 1];
            strength = e >= 27 && e != 29 ? 1f : 0f;   // 0x2D59AC: 26 x 0, then 1, 1, 0, then 22 x 1
        }

        int state = flag ? 0 : EffectBlendState[e];
        return new DrawEffect(e, flag, mode, strength, state, P2For(mode, state));
    }

    /// <summary>The blend state of a word (<c>compose.effect(word).blend_state</c>).</summary>
    public static int BlendStateOf(int word) => Decode(word).BlendState;

    /// <summary>
    /// P2 for the blend state actually used: 0 for modes 5, 7, 9, 10 when blending is off (0xF146F..0xF1490
    /// test the surface's +0x518), else 1.
    /// </summary>
    public static float P2For(int mode, int blendState) => mode is 5 or 7 or 9 or 10 && blendState == 0 ? 0f : 1f;

    /// <summary>
    /// SelectShader 0xF0E70 (compositing 3) for the shaders the game reaches: <see cref="CompositorShaders.ModeSolid"/>
    /// without a texture; mode 0 with transition parameters <see cref="CompositorShaders.ModeTransition"/>, else
    /// <see cref="CompositorShaders.ModeBasic"/>; mode 3 or above 25 basic; otherwise the blend shader of the mode.
    /// </summary>
    public static int SelectShader(bool textured, int shaderMode, bool hasTransitionParams)
    {
        if (!textured)
        {
            return CompositorShaders.ModeSolid;
        }

        if (shaderMode == 0)
        {
            return hasTransitionParams ? CompositorShaders.ModeTransition : CompositorShaders.ModeBasic;
        }

        return shaderMode == 3 || shaderMode > 25 ? CompositorShaders.ModeBasic : shaderMode;
    }

    private static int[] BuildBlendState()
    {
        var b = new List<int>();
        b.AddRange(Enumerable.Repeat(1, 26));
        b.AddRange([6, 1, 1, 6]);
        b.AddRange(Enumerable.Repeat(1, 22));
        b.AddRange([2, 7, 1, 10, 11, 5, 4, 9, 1, 1, 1, 2, 1, 13, 1, 1]);
        return [.. b];
    }
}

/// <summary>
/// Transition parameters of a texture (compositing 2.2, 3: texture +0xF0..+0xF8 = (lo, hi, inv), flag +0xFC):
/// a texture that carries them is drawn with PS #0 (transition 2DTex) in mode 0.
/// </summary>
/// <param name="Lo">Smoothstep lower edge.</param>
/// <param name="Hi">Smoothstep upper edge.</param>
/// <param name="Invert">0 = plain, 1 = inverted.</param>
public readonly record struct TransitionParams(float Lo, float Hi, float Invert = 0f)
{
    /// <summary>
    /// <c>compose.rule_params</c> for remaining progress <paramref name="progress"/>, generalised to a vague
    /// width b (0xD73B0(tex, a, b); the engine passes b = 256): lo = max(-1, a/128 - b/256), hi = min(2, a/128),
    /// a = 256 - trunc(p * 256).
    /// </summary>
    public static TransitionParams ForRule(float progress, int vague = 256)
    {
        int a = 256 - (int)(progress * 256f);
        float x0 = a * 0.0078125f;
        float x1 = x0 + (vague * -0.00390625f);
        return new TransitionParams(MathF.Max(-1f, x1), MathF.Min(2f, x0));
    }
}

/// <summary>The _FADS numbers of compositing 7.2 and 7.4.</summary>
public static class Fads
{
    /// <summary><c>compose.fads_step</c>: per-frame progress increment of a transition of <paramref name="ms"/> milliseconds (kinds 12 and 49 add 100 ms).</summary>
    public static float Step(int ms, int kind = 0)
    {
        if (kind is 12 or 49 && ms > 0)
        {
            ms += 100;
        }

        return ms <= 0 ? -1f : -1f / (ms / 16.666f);
    }

    /// <summary><c>compose.fads_vertex_alpha</c> as the vertex-colour byte of the cross-fade: trunc(max(0, p) * 255).</summary>
    public static byte CrossfadeAlphaByte(float progress) => (byte)Math.Min(255, (int)(MathF.Max(0f, progress) * 255f));
}

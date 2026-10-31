// Spike S3: draw-mode word decoding and the fixed-function blend states (docs/formats/compositing.md
// sections 4.1, 5.1, 7.2, 7.4), ported from tools/tsuki/compose.py (effect, BLEND_STATES,
// rule_params, fads_step, fads_vertex_alpha).
namespace Tsuki.Spikes.S3;

/// <summary>What the quad draw 0xD2BE0 and DisplaySurface_Draw 0xF12F0 derive from a draw-mode word
/// (compose.Effect): shader mode +0x608, strength +0x60C, blend state +0x518 and P2 +0x604.</summary>
internal readonly record struct Effect(int E, bool Flag, int ShaderMode, float Strength, int BlendState, float P2)
{
    /// <summary>The blend shaders read the backdrop D (every shader mode except 0; mode 3 maps to 0).</summary>
    public bool ReadsBackdrop => ShaderMode != 0;
}

internal static class Effects
{
    // 0x2D5978, index e-1 (shader mode)
    private static readonly int[] EffectMode =
    [
        1, 2, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25,
        0, 1, 2, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25,
    ];

    // 0x2D59AC, index e-1 (strength): 26 x 0, then 1, 1, 0, then 22 x 1
    private static readonly float[] EffectStrength = BuildStrength();

    // 0x2D55E0, index e (blend state)
    private static readonly int[] EffectBlendState = BuildBlendState();

    // 0xA1A800286A0: effects whose 0x100 flag turns blending off (0xD9FB9)
    private const long FlagSet = 0xA1A800286A0;

    private static float[] BuildStrength()
    {
        var s = new float[51];
        for (int i = 26; i < 51; i++)
            s[i] = 1f;
        s[28] = 0f;
        return s;
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

    /// <summary>compose.effect(word).</summary>
    public static Effect Decode(int word)
    {
        int low = word & 0xFF;
        int e = low < 0x44 ? low : 0;
        int rest = word & unchecked((int)0xFFFFFEFF);
        bool flag = (word & 0x100) != 0 && rest >= 0 && rest <= 0x2B && ((FlagSet >> rest) & 1) != 0;
        int mode = 0;
        float strength = 0f;
        if (e >= 1 && e <= 51)
        {
            mode = EffectMode[e - 1];
            strength = EffectStrength[e - 1];
        }
        int state = flag ? 0 : EffectBlendState[e];
        float p2 = (mode is 5 or 7 or 9 or 10) && state == 0 ? 0f : 1f;
        return new Effect(e, flag, mode, strength, state, p2);
    }

    /// <summary>compose.rule_params generalised to a vague width b (0xD73B0(tex, a, b); the engine
    /// passes b = 256): lo = max(-1, a/128 - b/256), hi = min(2, a/128), a = 256 - trunc(p * 256).</summary>
    public static (float Lo, float Hi) RuleParams(float progress, int vague)
    {
        int a = 256 - (int)(progress * 256f);
        float x0 = a * 0.0078125f;
        float x1 = x0 + vague * -0.00390625f;
        return (MathF.Max(-1f, x1), MathF.Min(2f, x0));
    }

    /// <summary>compose.fads_step: per-frame progress increment of a transition of ms milliseconds.</summary>
    public static float FadsStep(int ms) => ms <= 0 ? -1f : -1f / (ms / 16.666f);

    /// <summary>compose.fads_vertex_alpha as the vertex-colour byte: trunc(max(0, p) * 255).</summary>
    public static int FadsAlphaByte(float progress) => (int)(MathF.Max(0f, progress) * 255f);
}

// The 14 fixed-function blend states of table 0x2D8030 (docs/formats/compositing.md 4.1;
// compose.BLEND_STATES / apply_blend_state) as glBlendFuncSeparate / glBlendEquationSeparate, lifted
// from spike S3 Compositor.SetBlendState.
using Silk.NET.OpenGL;

namespace Tsuki.Render.Compositor;

/// <summary>One blend state as GL factors and equations.</summary>
/// <param name="Enabled">False for state 0 (blending off).</param>
/// <param name="ColorSrc">Colour source factor.</param>
/// <param name="ColorDst">Colour destination factor.</param>
/// <param name="ColorEquation">Colour equation.</param>
/// <param name="AlphaSrc">Alpha source factor.</param>
/// <param name="AlphaDst">Alpha destination factor.</param>
/// <param name="AlphaEquation">Alpha equation.</param>
public readonly record struct GlBlend(bool Enabled, GLEnum ColorSrc, GLEnum ColorDst, GLEnum ColorEquation, GLEnum AlphaSrc, GLEnum AlphaDst, GLEnum AlphaEquation);

/// <summary>
/// The 14 records of table 0x2D8030. GCN applies the factors for MIN / MAX, GL ignores them
/// (compositing 4.1), so the states that use MIN / MAX with factors other than ONE (4 and 10) are
/// written as the equivalent ADD forms; the results equal <c>compose.apply_blend_state</c> for inputs in
/// [0, 1]. States 5 and 11 use MIN / MAX with factors ONE, where ignoring the factors changes nothing.
/// </summary>
public static class BlendStates
{
    /// <summary>Number of records in the table.</summary>
    public const int Count = 14;

    private static readonly GlBlend[] Table = Build();

    /// <summary>
    /// Extension state (not a PS4 record, package K7): colour kept, destination alpha multiplied by the source alpha. The stage uses it to narrow an
    /// offscreen mask (the silhouette of a window layer times its rule fade).
    /// </summary>
    public const int MaskMultiply = 0x80;

    private static readonly GlBlend MaskMultiplyRecord = new(true, GLEnum.Zero, GLEnum.One, GLEnum.FuncAdd, GLEnum.Zero, GLEnum.SrcAlpha, GLEnum.FuncAdd);

    /// <summary>The GL form of state <paramref name="state"/>; ids outside 0..13 use record 1 (0xF77E0), except <see cref="MaskMultiply"/>.</summary>
    public static GlBlend Get(int state) => state == MaskMultiply ? MaskMultiplyRecord : Table[state is < 0 or >= Count ? 1 : state];

    /// <summary>Sets the GL blend state.</summary>
    public static void Apply(GL gl, int state)
    {
        ArgumentNullException.ThrowIfNull(gl);
        GlBlend b = Get(state);
        if (!b.Enabled)
        {
            gl.Disable(EnableCap.Blend);
            return;
        }

        gl.Enable(EnableCap.Blend);
        gl.BlendFuncSeparate(b.ColorSrc, b.ColorDst, b.AlphaSrc, b.AlphaDst);
        gl.BlendEquationSeparate(b.ColorEquation, b.AlphaEquation);
    }

    private static GlBlend[] Build()
    {
        const GLEnum Add = GLEnum.FuncAdd;
        static GlBlend On(GLEnum cs, GLEnum cd, GLEnum cf, GLEnum @as, GLEnum ad, GLEnum af) => new(true, cs, cd, cf, @as, ad, af);
        return
        [
            new GlBlend(false, GLEnum.One, GLEnum.Zero, Add, GLEnum.One, GLEnum.Zero, Add),                                     // 0 replace
            On(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha, Add, GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha, Add),                // 1 over
            On(GLEnum.SrcAlpha, GLEnum.One, Add, GLEnum.SrcAlpha, GLEnum.One, Add),                                          // 2 additive
            On(GLEnum.Zero, GLEnum.OneMinusSrcAlpha, Add, GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha, Add),                    // 3 erase
            On(GLEnum.Zero, GLEnum.One, Add, GLEnum.One, GLEnum.Zero, Add),                                                   // 4 alpha := max(sa*1, da*0) = sa
            On(GLEnum.Zero, GLEnum.One, Add, GLEnum.One, GLEnum.One, GLEnum.Min),                                             // 5 alpha := min(sa, da)
            On(GLEnum.DstAlpha, GLEnum.OneMinusDstAlpha, Add, GLEnum.Zero, GLEnum.One, Add),                                  // 6 through dst alpha
            On(GLEnum.DstAlpha, GLEnum.One, Add, GLEnum.Zero, GLEnum.One, Add),                                               // 7 add through dst alpha
            On(GLEnum.SrcAlpha, GLEnum.One, GLEnum.FuncSubtract, GLEnum.SrcAlpha, GLEnum.One, GLEnum.FuncSubtract),           // 8 subtract
            On(GLEnum.OneMinusDstColor, GLEnum.Zero, Add, GLEnum.Zero, GLEnum.One, Add),                                      // 9 s * (1 - d)
            On(GLEnum.Zero, GLEnum.One, Add, GLEnum.Zero, GLEnum.Zero, Add),                                                  // 10 alpha := min(0, 0) = 0
            On(GLEnum.Zero, GLEnum.One, Add, GLEnum.One, GLEnum.One, GLEnum.Max),                                             // 11 alpha := max(sa, da)
            On(GLEnum.Zero, GLEnum.One, Add, GLEnum.SrcAlpha, GLEnum.Zero, Add),                                              // 12 alpha := sa * sa
            On(GLEnum.DstAlpha, GLEnum.OneMinusDstAlpha, Add, GLEnum.SrcAlpha, GLEnum.OneMinusDstAlpha, Add),                 // 13
        ];
    }
}

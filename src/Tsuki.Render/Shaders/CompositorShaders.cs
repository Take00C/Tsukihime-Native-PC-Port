// The engine's pixel shaders in GLSL (docs/formats/compositing.md 2.3, 3; ARCHITECTURE 6.3, 6.5),
// lifted from spike S3 Shaders.cs and written from tools/tsuki/compose.py. One source, compiled once
// per mode with "#define MODE n" through the H1 seam (Gl/ShaderCompiler); every program compiles as
// GLSL 3.30 core and as GLSL ES 3.00.
//
// MODE 0   PS #2  basic 2DTex / capture        S * C
// MODE 100 PS #3  basic 2D (fills)             C
// MODE 101 PS #0  transition 2DTex (rule)      C * sat(|P.z - smoothstep(P.x, P.y, S.r)|)
// MODE 1..25 (not 3) the 24 blend shaders      rgb per mode, alpha per family (nexl / PS)
// Every mode's result goes through exportMrt0, the fp16 round-toward-zero MRT0 export (compositing.md 2.6).
using Silk.NET.OpenGL;
using Tsuki.Render.Gl;

namespace Tsuki.Render.Shaders;

/// <summary>GLSL sources and mode numbers of the compositor programs.</summary>
public static class CompositorShaders
{
    /// <summary>PS #2, basic 2DTex: <c>S * C</c> (pictures, captures).</summary>
    public const int ModeBasic = 0;

    /// <summary>PS #3, basic 2D: the vertex colour (untextured fills).</summary>
    public const int ModeSolid = 100;

    /// <summary>PS #0, transition 2DTex: the rule mask of a texture that carries transition parameters.</summary>
    public const int ModeTransition = 101;

    /// <summary>
    /// Vertex shader. Positions are pixels of the target with the origin at the top-left; the frame's
    /// row 0 is drawn into GL row 0, so a target texture holds the frame top-down like every picture
    /// texture (a capture is sampled with v = 0 at its top, as a picture is).
    /// </summary>
    public const string VertexBody = """
        layout(location = 0) in vec2 a_pos;     // target pixels, origin top-left
        layout(location = 1) in vec2 a_uv;
        layout(location = 2) in vec4 a_color;   // vertex colour bytes / 255 (0xD2BE0)
        uniform vec2 u_viewport;
        out vec2 v_uv;
        out vec4 v_color;
        void main()
        {
            v_uv = a_uv;
            v_color = a_color;
            gl_Position = vec4(a_pos.x * (2.0 / u_viewport.x) - 1.0, a_pos.y * (2.0 / u_viewport.y) - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// Fragment shader for every mode (select with <c>#define MODE n</c>). Every mode ends in <c>exportMrt0</c>, the
    /// PS4's fp16 round-toward-zero colour export (compositing.md 2.6), so the output is driver-independent.
    /// </summary>
    public const string FragmentBody = """
        uniform sampler2D u_tex;       // S: the picture (slot 0, m_texcoord)
        uniform sampler2D u_backdrop;  // D: copy of the render target (slot 1, m_transition = screen position)
        uniform vec4 u_params;         // blend: (1 - strength, strength, P2, 0); transition: (lo, hi, inv, 0)
        uniform sampler2D u_mask;      // the offscreen mask target of QuadFx.Mask (unit 2), read at the pixel's position
        uniform vec4 u_fx;             // (haze enabled, mask enabled, blur enabled, 0)
        uniform vec4 u_blur;           // QuadPass.Blur: (du, dv, sigma, n)
        uniform vec4 u_hazeA;          // STRA haze, x: (amplitude, waves, phase, v offset)
        uniform vec4 u_hazeB;          // y: (amplitude, waves, phase, v scale)
        in vec2 v_uv;
        in vec4 v_color;
        layout(location = 0) out vec4 o_color;

        // The MRT0 colour export of the PS4 (compositing.md 2.6, compose.export_mrt0): NaN -> 0, clamp to
        // [0, 1], then IEEE binary16 rounded toward zero (v_cvt_pkrtz_f16_f32 + exp mrt0 compr). Done here
        // explicitly so the result does not depend on how a driver converts the float32 output for an
        // RGBA8 target: some (AMD) export through fp16 RTZ themselves, others convert float32 straight to
        // UNORM8, which is compose "f32". The values returned are exact fp16 values, so a driver's own fp16
        // export leaves them unchanged. NaN is found from the bits (no reliance on isnan under fast math).
        vec4 exportMrt0(vec4 x)
        {
            bvec4 isNan = greaterThan(floatBitsToUint(x) & uvec4(0x7FFFFFFFu), uvec4(0x7F800000u));
            x = clamp(mix(x, vec4(0.0), isNan), 0.0, 1.0);
            vec4 normal = uintBitsToFloat(floatBitsToUint(x) & uvec4(0xFFFFE000u));   // drop 13 of 23 mantissa bits
            vec4 subnormal = floor(x * 16777216.0) * (1.0 / 16777216.0);             // fp16 subnormal step 2^-24
            return mix(normal, subnormal, lessThan(x, vec4(6.103515625e-05)));       // below 2^-14
        }

        // The STRA haze (layers.md 4.2; knife shader.wgsl haze_uv): two waves along the picture's v axis displace the texture coordinates.
        vec2 hazeUv(vec2 uv)
        {
            if (u_fx.x < 0.5)
            {
                return uv;
            }

            const float tau = 6.2831853;
            float s = u_hazeA.w + u_hazeB.w * uv.y;
            float dx = sin(tau * (fract(s * u_hazeA.y) + fract(u_hazeA.z))) * u_hazeA.x;
            float dy = cos(tau * (fract(s * u_hazeB.y) + fract(u_hazeB.z))) * u_hazeB.x;
            return uv + vec2(dx, dy);
        }

        // One separable gaussian pass (QuadPass.Blur; knife-render fs_blur, mode Uv): taps (du, dv) apart in uv, weights exp(-k^2 / 2 sigma^2), normalised.
        vec4 blurTaps(vec2 uv)
        {
            float sigma = max(u_blur.z, 0.01);
            int n = int(u_blur.w + 0.5);
            vec4 acc = vec4(0.0);
            float sum = 0.0;
            for (int k = -n; k <= n; k++)
            {
                float x = float(k);
                float w = exp(-x * x / (2.0 * sigma * sigma));
                acc += texture(u_tex, uv + u_blur.xy * x) * w;
                sum += w;
            }

            return acc / sum;
        }

        void main()
        {
            vec4 C = v_color;
            if (u_fx.y > 0.5)
            {
                C.a *= texelFetch(u_mask, ivec2(gl_FragCoord.xy), 0).a;
            }

            vec2 uv = hazeUv(v_uv);
            vec4 r;
        #if MODE == 0
            r = (u_fx.z > 0.5 ? blurTaps(uv) : texture(u_tex, uv)) * C;
        #elif MODE == 100
            r = C;
        #elif MODE == 101
            float m = texture(u_tex, uv).r;
            float t = clamp((m - u_params.x) * (1.0 / (u_params.y - u_params.x)), 0.0, 1.0);
            float f = clamp(abs(u_params.z - t * t * (3.0 - 2.0 * t)), 0.0, 1.0);
            r = C * f;
        #else
            vec4 S = texture(u_tex, uv);
            vec4 D = texelFetch(u_backdrop, ivec2(gl_FragCoord.xy), 0);
            vec3 s = S.rgb;
            vec3 d = D.rgb;
            float ca = C.a;
            float sa = S.a;
            float p1 = u_params.y;
            float p2 = u_params.z;
            float dodgeBlack = uintBitsToFloat(0x3C40C0C1u);   // 3/255, compared with v_cmp_gt_f32
            vec3 rgb;
        #if MODE == 1          // add
            rgb = min(vec3(1.0), d + ca * s);
        #elif MODE == 2        // addalpha
            rgb = min(vec3(1.0), ca * s + d * (1.0 - ca * sa));
        #elif MODE == 4        // darken
            rgb = d + ca * (min(d, s) - d);
        #elif MODE == 5 || MODE == 17   // dodge / ps_dodge5 (PsColorDodge5)
        #if MODE == 5
            float k = ca * max(p2, sa);
        #else
            float k = ca * sa;
        #endif
            vec3 den = 1.0 - s * k;
            // v_min_f32(d * rcp(den), d < 3/255 ? 0 : 1): den = 0 gives +inf (or NaN for d = 0, where
            // v_min returns the other operand); both selected explicitly so no NaN reaches min().
            vec3 q = mix(d * (1.0 / den), vec3(1.0), lessThanEqual(den, vec3(0.0)));
            rgb = mix(min(q, vec3(1.0)), vec3(0.0), lessThan(d, vec3(dodgeBlack)));
        #elif MODE == 6        // lighten
            rgb = d + ca * (max(d, s) - d);
        #elif MODE == 7        // multiplicative
            float k = ca * max(p2, sa);
            rgb = d + k * (s * d - d);
        #elif MODE == 8        // opaque
            rgb = d + ca * (s - d);
        #elif MODE == 9        // screen
            float k = ca * max(p2, sa);
            rgb = 1.0 - (1.0 - d) * (1.0 - s * k);
        #elif MODE == 10       // sub (subtracts the inverse of S)
            rgb = max(vec3(0.0), d + ca * (s - 1.0));
        #elif MODE == 11       // ps_add (vertex colour rgb, not alpha)
            rgb = min(vec3(1.0), d + C.rgb * s);
        #elif MODE == 12       // ps_burn: s = 0 gives -inf or NaN, both 0 after v_max
            rgb = mix(max(vec3(0.0), (1.0 / s) * (d - 1.0) + 1.0), vec3(0.0), lessThanEqual(s, vec3(0.0)));
        #elif MODE == 13       // ps_darken
            rgb = min(d, s);
        #elif MODE == 14       // ps_diff
            rgb = abs(d - s);
        #elif MODE == 15       // ps_diff5
            rgb = abs(d - s * (ca * sa));
        #elif MODE == 16       // ps_dodge: s = 1 gives +inf or NaN, both 1 after v_min
            rgb = mix(min(vec3(1.0), d * (1.0 / (1.0 - s))), vec3(1.0), greaterThanEqual(s, vec3(1.0)));
        #elif MODE == 18       // ps_exclusion
            rgb = s + d - 2.0 * d * s;
        #elif MODE == 19       // ps_hardlight on X = C.rgb * S
            vec3 x = C.rgb * s;
            rgb = mix(2.0 * x * d, 1.0 - 2.0 * (1.0 - d) * (1.0 - x), greaterThanEqual(x, vec3(0.5)));
        #elif MODE == 20       // ps_lighten
            rgb = max(d, s);
        #elif MODE == 21       // ps_multi
            rgb = d * s;
        #elif MODE == 22       // ps_overlay
            rgb = mix(2.0 * d * s, 1.0 - 2.0 * (1.0 - d) * (1.0 - s), greaterThanEqual(d, vec3(0.5)));
        #elif MODE == 23       // ps_screen
            rgb = s + d - d * s;
        #elif MODE == 24       // ps_softlight: |d| ^ e, e = s >= 0.5 ? 1 / (2 s) : 2 (1 - s)
            vec3 e = mix(2.0 * (1.0 - s), 1.0 / (2.0 * s), greaterThanEqual(s, vec3(0.5)));
            vec3 ad = abs(d);
            rgb = mix(exp2(log2(max(ad, vec3(1e-30))) * e), vec3(0.0), lessThanEqual(ad, vec3(0.0)));
        #elif MODE == 25       // ps_sub (linear burn)
            rgb = max(vec3(0.0), d + s - 1.0);
        #else
        #error unknown blend mode
        #endif
        #if MODE == 1 || MODE == 2 || (MODE >= 4 && MODE <= 10) || MODE == 15 || MODE == 17
            float a = 1.0 + p1 * (D.a - 1.0);           // "nexl" family
        #else
            float kk = ca * sa;
            float a = kk + p1 * (D.a - kk);              // "PS" family
        #endif
            r = vec4(rgb, a);
        #endif
            o_color = exportMrt0(r);
        }
        """;

    /// <summary>Every fragment-shader mode the compositor uses: basic, solid, transition and the 24 blend modes (27 programs).</summary>
    public static IReadOnlyList<int> AllModes { get; } = BuildModes();

    /// <summary>True for the 24 blend shaders, which read the backdrop D.</summary>
    public static bool ReadsBackdrop(int mode) => mode is >= 1 and <= 25 && mode != 3;

    /// <summary>Compiles and links the program of one mode.</summary>
    /// <exception cref="ShaderCompileException">The driver rejected the source.</exception>
    public static ShaderProgram Compile(GL gl, GlslDialect dialect, int mode) =>
        ShaderCompiler.CompileProgram(gl, dialect, VertexBody, FragmentBody, [$"MODE {mode}"]);

    private static int[] BuildModes()
    {
        var modes = new List<int> { ModeBasic, ModeSolid, ModeTransition };
        for (int m = 1; m <= 25; m++)
        {
            if (m != 3)
            {
                modes.Add(m);
            }
        }

        return [.. modes];
    }
}

/// <summary>One linked compositor program and its uniform locations.</summary>
/// <param name="Mode">The shader mode.</param>
/// <param name="Program">The linked program.</param>
/// <param name="Viewport">Location of <c>u_viewport</c>.</param>
/// <param name="Params">Location of <c>u_params</c>, or -1 when the mode reads none.</param>
/// <param name="Fx">Location of <c>u_fx</c> (haze / mask switches).</param>
/// <param name="HazeA">Location of <c>u_hazeA</c>.</param>
/// <param name="HazeB">Location of <c>u_hazeB</c>.</param>
/// <param name="Blur">Location of <c>u_blur</c> (mode 0 only).</param>
public sealed record CompositorProgram(int Mode, ShaderProgram Program, int Viewport, int Params, int Fx = -1, int HazeA = -1, int HazeB = -1, int Blur = -1);

/// <summary>The 27 compositor programs of one GLSL dialect, compiled once per context.</summary>
public sealed class CompositorProgramSet : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<int, CompositorProgram> _programs = [];

    /// <summary>Compiles every mode of <see cref="CompositorShaders.AllModes"/>.</summary>
    /// <exception cref="ShaderCompileException">A program did not compile or link.</exception>
    public CompositorProgramSet(GL gl, GlslDialect dialect = GlslDialect.Glsl330Core)
    {
        ArgumentNullException.ThrowIfNull(gl);
        _gl = gl;
        Dialect = dialect;
        try
        {
            foreach (int mode in CompositorShaders.AllModes)
            {
                ShaderProgram p = CompositorShaders.Compile(gl, dialect, mode);
                uint h = p.Handle;
                gl.UseProgram(h);
                gl.Uniform1(gl.GetUniformLocation(h, "u_tex"), 0);
                int backdrop = gl.GetUniformLocation(h, "u_backdrop");
                if (backdrop >= 0)
                {
                    gl.Uniform1(backdrop, 1);
                }

                int maskSampler = gl.GetUniformLocation(h, "u_mask");
                if (maskSampler >= 0)
                {
                    gl.Uniform1(maskSampler, 2);
                }

                _programs[mode] = new CompositorProgram(
                    mode, p, gl.GetUniformLocation(h, "u_viewport"), gl.GetUniformLocation(h, "u_params"),
                    gl.GetUniformLocation(h, "u_fx"), gl.GetUniformLocation(h, "u_hazeA"), gl.GetUniformLocation(h, "u_hazeB"), gl.GetUniformLocation(h, "u_blur"));
            }
        }
        catch
        {
            Dispose();
            throw;
        }

        gl.UseProgram(0);
    }

    /// <summary>The dialect the programs were compiled as.</summary>
    public GlslDialect Dialect { get; }

    /// <summary>Number of programs (27).</summary>
    public int Count => _programs.Count;

    /// <summary>The program of a mode.</summary>
    public CompositorProgram this[int mode] => _programs[mode];

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (CompositorProgram p in _programs.Values)
        {
            p.Program.Delete(_gl);
        }

        _programs.Clear();
    }
}

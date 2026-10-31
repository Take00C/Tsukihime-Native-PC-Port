// Spike S3: the engine's pixel shaders rewritten in GLSL from tools/tsuki/compose.py
// (docs/formats/compositing.md 2.3; ARCHITECTURE 6.3, 6.5). One source, compiled once per mode
// with "#define MODE n"; the subset compiles both as GLSL 3.30 core and as GLSL ES 3.00.
//
// MODE 0   PS #2  basic 2DTex / capture        S * C
// MODE 100 PS #3  basic 2D (fills)             C
// MODE 101 PS #0  transition 2DTex (rule)      C * sat(|P.z - smoothstep(P.x, P.y, S.r)|)
// MODE 1..25 (not 3) the 24 blend shaders      rgb per mode, alpha per family (nexl / PS)
using Silk.NET.OpenGL;

namespace Tsuki.Spikes.S3;

internal static class Shaders
{
    public const int ModeBasic = 0;
    public const int ModeSolid = 100;
    public const int ModeTransition = 101;

    public const string GlHeader = "#version 330 core\n";
    public const string EsHeader = "#version 300 es\nprecision highp float;\nprecision highp int;\nprecision highp sampler2D;\n";

    public const string Vertex = """
        layout(location = 0) in vec2 a_pos;     // frame pixels, origin top-left
        layout(location = 1) in vec2 a_uv;
        layout(location = 2) in vec4 a_color;   // vertex colour bytes / 255 (0xD2BE0)
        uniform vec2 u_viewport;
        out vec2 v_uv;
        out vec4 v_color;
        void main()
        {
            v_uv = a_uv;
            v_color = a_color;
            gl_Position = vec4(a_pos.x * (2.0 / u_viewport.x) - 1.0, 1.0 - a_pos.y * (2.0 / u_viewport.y), 0.0, 1.0);
        }
        """;

    public const string Fragment = """
        uniform sampler2D u_tex;       // S: the picture (slot 0, m_texcoord)
        uniform sampler2D u_backdrop;  // D: copy of the render target (slot 1, m_transition = screen position)
        uniform vec4 u_params;         // blend: (1 - strength, strength, P2, 0); transition: (lo, hi, inv, 0)
        in vec2 v_uv;
        in vec4 v_color;
        layout(location = 0) out vec4 o_color;

        void main()
        {
            vec4 C = v_color;
        #if MODE == 0
            o_color = texture(u_tex, v_uv) * C;
        #elif MODE == 100
            o_color = C;
        #elif MODE == 101
            float m = texture(u_tex, v_uv).r;
            float t = clamp((m - u_params.x) * (1.0 / (u_params.y - u_params.x)), 0.0, 1.0);
            float f = clamp(abs(u_params.z - t * t * (3.0 - 2.0 * t)), 0.0, 1.0);
            o_color = C * f;
        #else
            vec4 S = texture(u_tex, v_uv);
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
            o_color = vec4(rgb, a);
        #endif
        }
        """;

    /// <summary>Every fragment-shader mode the compositor uses.</summary>
    public static IEnumerable<int> AllModes()
    {
        yield return ModeBasic;
        yield return ModeSolid;
        yield return ModeTransition;
        for (int m = 1; m <= 25; m++)
            if (m != 3)
                yield return m;
    }

    public static string Source(string header, int mode, bool fragment) =>
        fragment ? header + $"#define MODE {mode}\n" + Fragment : header + Vertex;

    public static uint Compile(GL gl, ShaderType type, string source, out string log)
    {
        uint id = gl.CreateShader(type);
        gl.ShaderSource(id, source);
        gl.CompileShader(id);
        gl.GetShader(id, ShaderParameterName.CompileStatus, out int ok);
        log = gl.GetShaderInfoLog(id);
        if (ok == 0)
        {
            gl.DeleteShader(id);
            return 0;
        }
        return id;
    }

    public static uint Link(GL gl, string header, int mode, out string log)
    {
        uint vs = Compile(gl, ShaderType.VertexShader, Source(header, mode, false), out string vlog);
        uint fs = Compile(gl, ShaderType.FragmentShader, Source(header, mode, true), out string flog);
        if (vs == 0 || fs == 0)
        {
            log = $"compile failed (mode {mode}): {vlog} {flog}";
            if (vs != 0) gl.DeleteShader(vs);
            if (fs != 0) gl.DeleteShader(fs);
            return 0;
        }
        uint p = gl.CreateProgram();
        gl.AttachShader(p, vs);
        gl.AttachShader(p, fs);
        gl.LinkProgram(p);
        gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int ok);
        log = gl.GetProgramInfoLog(p);
        gl.DetachShader(p, vs);
        gl.DetachShader(p, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        if (ok == 0)
        {
            gl.DeleteProgram(p);
            log = $"link failed (mode {mode}): {log}";
            return 0;
        }
        return p;
    }
}

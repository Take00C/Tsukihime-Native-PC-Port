// The one shader of M0 acceptance 3 / PACE 7.2 #1 (H1): compiled and linked as GLSL 3.30 core and
// as GLSL ES 3.00, then drawn into a small offscreen target and read back, so the check proves the
// program runs and both dialects produce the same pixels (ARCHITECTURE 6.5).
using Silk.NET.OpenGL;

namespace Tsuki.Render.Gl;

/// <summary>The result of drawing the smoke shader in one dialect.</summary>
/// <param name="Dialect">The dialect the program was compiled as.</param>
/// <param name="Pixels">RGBA8 read back from the target, bottom row first (glReadPixels order).</param>
/// <param name="Mismatches">Bytes that differ from <see cref="SmokeShader.Expected"/>.</param>
/// <param name="Log">Compile and link logs.</param>
public sealed record SmokeRender(GlslDialect Dialect, byte[] Pixels, int Mismatches, string Log);

/// <summary>A full-screen triangle whose colour is a function of the pixel position.</summary>
public static class SmokeShader
{
    /// <summary>Target width in pixels.</summary>
    public const int Width = 8;

    /// <summary>Target height in pixels.</summary>
    public const int Height = 4;

    /// <summary>The blue channel the tint uniform sets (0.6 = 153 / 255 exactly).</summary>
    public const float TintBlue = 0.6f;

    /// <summary>The vertex body: a full-screen triangle from gl_VertexID, no attributes.</summary>
    public const string VertexBody = """
        void main()
        {
            vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// The fragment body: red = (x + 0.5) / 8, green = (y + 0.5) / 4, blue and alpha from the uniform.
    /// No value falls on a rounding tie when converted to 8 bits.
    /// </summary>
    public const string FragmentBody = """
        uniform vec4 u_tint;
        layout(location = 0) out vec4 o_color;
        void main()
        {
            vec2 p = floor(gl_FragCoord.xy);
            o_color = vec4((p.x + 0.5) / 8.0, (p.y + 0.5) / 4.0, u_tint.b, u_tint.a);
        }
        """;

    /// <summary>The expected RGBA8 target, bottom row first.</summary>
    public static byte[] Expected()
    {
        var px = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = ((y * Width) + x) * 4;
                px[i] = ToUnorm8((x + 0.5) / Width);
                px[i + 1] = ToUnorm8((y + 0.5) / Height);
                px[i + 2] = ToUnorm8(TintBlue);
                px[i + 3] = 255;
            }
        }

        return px;
    }

    /// <summary>Compiles, links and draws the shader in one dialect; throws <see cref="ShaderCompileException"/> on a compile failure.</summary>
    public static SmokeRender Render(GlDevice device, GlslDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(device);
        GL gl = device.Gl;
        ShaderProgram program = ShaderCompiler.CompileProgram(gl, dialect, VertexBody, FragmentBody);
        uint vao = 0, fbo = 0, tex = 0;
        try
        {
            tex = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, tex);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, Width, Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            fbo = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
            GLEnum status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
            {
                throw new InvalidOperationException($"smoke framebuffer incomplete: {status}");
            }

            vao = gl.GenVertexArray();
            gl.BindVertexArray(vao);
            gl.Viewport(0, 0, Width, Height);
            gl.Disable(EnableCap.Blend);
            gl.Disable(EnableCap.ScissorTest);
            gl.ClearColor(0f, 0f, 0f, 0f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            gl.UseProgram(program.Handle);
            gl.Uniform4(gl.GetUniformLocation(program.Handle, "u_tint"), 0f, 0f, TintBlue, 1f);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            var pixels = new byte[Width * Height * 4];
            gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
            gl.ReadPixels(0, 0, Width, Height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            GLEnum err = gl.GetError();
            if (err != GLEnum.NoError)
            {
                throw new InvalidOperationException($"GL error after the smoke draw ({dialect}): {err}");
            }

            byte[] expected = Expected();
            int mismatches = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i] != expected[i])
                {
                    mismatches++;
                }
            }

            return new SmokeRender(dialect, pixels, mismatches, program.Log);
        }
        finally
        {
            gl.UseProgram(0);
            gl.BindVertexArray(0);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.BindTexture(TextureTarget.Texture2D, 0);
            if (vao != 0)
            {
                gl.DeleteVertexArray(vao);
            }

            if (fbo != 0)
            {
                gl.DeleteFramebuffer(fbo);
            }

            if (tex != 0)
            {
                gl.DeleteTexture(tex);
            }

            program.Delete(gl);
        }
    }

    private static byte ToUnorm8(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);
}

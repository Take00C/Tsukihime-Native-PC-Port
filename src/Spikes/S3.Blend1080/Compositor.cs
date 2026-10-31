// Spike S3 compositor: executes textured quads the way the PS4 display-list executor does
// (docs/formats/compositing.md 3, 4, 5, 7; ARCHITECTURE 6.3): shader selected from the draw-mode
// word, the backdrop copied before every backdrop-reading quad (glBlitFramebuffer, full target or
// the quad bounds), fixed-function blend state from the 14-record table, straight alpha, RGBA8
// target, no sRGB. Transitions: capture with alpha := 1 (effect 56), rule pass (PS #0, effect 58,
// state 4) and capture pass (effect 26, state 6), or the quantised cross-fade.
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace Tsuki.Spikes.S3;

[StructLayout(LayoutKind.Sequential)]
internal struct Vertex
{
    public float X, Y, U, V, R, G, B, A;

    public Vertex(float x, float y, float u, float v, float r, float g, float b, float a)
    {
        X = x; Y = y; U = u; V = v; R = r; G = g; B = b; A = a;
    }
}

internal enum BlitMode
{
    /// <summary>Copy the whole target before each backdrop-reading quad (spike baseline).</summary>
    Full,
    /// <summary>Copy only the quad's pixel bounds (fallback 1 of the spike).</summary>
    Bounds,
}

/// <summary>Receives a GPU timestamp request before and after the timed operations (bench breakdown).</summary>
internal interface IGpuMarks
{
    void Mark(string label);
}

internal sealed class RenderTarget
{
    public uint Texture;
    public uint Fbo;
    public int Width;
    public int Height;
}

internal sealed unsafe class Compositor : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<int, (uint Program, int Viewport, int Params)> _programs = [];
    private readonly uint _vao;
    private readonly uint _vbo;
    private const int VboVertices = 8192;
    private int _vboCursor;
    private readonly uint _samplerLinear;
    private readonly uint _samplerNearest;

    public int Width { get; }
    public int Height { get; }
    public RenderTarget Target { get; }
    public RenderTarget Backdrop { get; }
    public RenderTarget Capture { get; }
    public BlitMode Blit { get; set; } = BlitMode.Full;
    public bool Nearest { get; set; }
    public IGpuMarks? Marks { get; set; }
    public int BlitCount { get; private set; }
    public long BlitPixels { get; private set; }
    public int DrawCount { get; private set; }
    public List<string> CompileLog { get; } = [];

    public Compositor(GL gl, int width, int height)
    {
        _gl = gl;
        Width = width;
        Height = height;
        Target = CreateTarget(width, height);
        Backdrop = CreateTarget(width, height);
        Capture = CreateTarget(width, height);
        foreach (int mode in Shaders.AllModes())
        {
            uint p = Shaders.Link(gl, Shaders.GlHeader, mode, out string log);
            if (p == 0)
                throw new InvalidOperationException(log);
            if (log.Trim().Length > 0)
                CompileLog.Add($"mode {mode}: {log.Trim()}");
            gl.UseProgram(p);
            gl.Uniform1(gl.GetUniformLocation(p, "u_tex"), 0);
            int bd = gl.GetUniformLocation(p, "u_backdrop");
            if (bd >= 0)
                gl.Uniform1(bd, 1);
            _programs[mode] = (p, gl.GetUniformLocation(p, "u_viewport"), gl.GetUniformLocation(p, "u_params"));
        }
        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(VboVertices * sizeof(Vertex)), null, BufferUsageARB.StreamDraw);
        uint stride = (uint)sizeof(Vertex);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)8);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)16);
        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);
        gl.EnableVertexAttribArray(2);
        _samplerLinear = CreateSampler(TextureMinFilter.Linear, TextureMagFilter.Linear);
        _samplerNearest = CreateSampler(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.FramebufferSrgb);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
        BindTarget();
    }

    private uint CreateSampler(TextureMinFilter min, TextureMagFilter mag)
    {
        uint s = _gl.GenSampler();
        _gl.SamplerParameter(s, SamplerParameterI.MinFilter, (int)min);
        _gl.SamplerParameter(s, SamplerParameterI.MagFilter, (int)mag);
        _gl.SamplerParameter(s, SamplerParameterI.WrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.SamplerParameter(s, SamplerParameterI.WrapT, (int)TextureWrapMode.ClampToEdge);
        return s;
    }

    private RenderTarget CreateTarget(int w, int h)
    {
        var t = new RenderTarget { Width = w, Height = h, Texture = CreateTexture(w, h, null) };
        t.Fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, t.Fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, t.Texture, 0);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"framebuffer incomplete: {status}");
        return t;
    }

    /// <summary>An RGBA8 texture; <paramref name="rgba"/> rows in GL order (first row = v 0).</summary>
    public uint CreateTexture(int w, int h, byte[]? rgba)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        if (rgba is null)
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        }
        else
        {
            if (rgba.Length != w * h * 4)
                throw new ArgumentException($"texture data {rgba.Length} bytes, expected {w * h * 4}");
            fixed (byte* p = rgba)
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        return tex;
    }

    public void DeleteTexture(uint tex) => _gl.DeleteTexture(tex);

    public void BindTarget()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, Target.Fbo);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
    }

    public void BeginFrame()
    {
        BindTarget();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(VboVertices * sizeof(Vertex)), null, BufferUsageARB.StreamDraw);
        _vboCursor = 0;
        BlitCount = 0;
        BlitPixels = 0;
        DrawCount = 0;
    }

    public void Clear(float r, float g, float b, float a)
    {
        BindTarget();
        _gl.ClearColor(r, g, b, a);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
    }

    /// <summary>Draw one quad (triangle strip TL, TR, BL, BR) with the draw-mode word.</summary>
    /// <param name="texture">picture texture, 0 for an untextured fill (basic 2D)</param>
    /// <param name="v">four vertices in frame pixels</param>
    /// <param name="word">draw-mode word (record type 2)</param>
    /// <param name="rule">transition parameters (lo, hi) when the texture carries them (rule pictures)</param>
    /// <param name="stateOverride">blend state instead of the effect's (verification only)</param>
    public void DrawQuad(uint texture, ReadOnlySpan<Vertex> v, int word, (float Lo, float Hi)? rule = null, int? stateOverride = null)
    {
        Effect e = Effects.Decode(word);
        int mode = texture == 0 ? Shaders.ModeSolid
                 : e.ShaderMode == 0 ? (rule.HasValue ? Shaders.ModeTransition : Shaders.ModeBasic)
                 : e.ShaderMode;
        int state = stateOverride ?? e.BlendState;
        float minX = MathF.Min(MathF.Min(v[0].X, v[1].X), MathF.Min(v[2].X, v[3].X));
        float maxX = MathF.Max(MathF.Max(v[0].X, v[1].X), MathF.Max(v[2].X, v[3].X));
        float minY = MathF.Min(MathF.Min(v[0].Y, v[1].Y), MathF.Min(v[2].Y, v[3].Y));
        float maxY = MathF.Max(MathF.Max(v[0].Y, v[1].Y), MathF.Max(v[2].Y, v[3].Y));
        int x0 = Math.Clamp((int)MathF.Floor(minX), 0, Width);
        int x1 = Math.Clamp((int)MathF.Ceiling(maxX), 0, Width);
        int y0 = Math.Clamp((int)MathF.Floor(minY), 0, Height);
        int y1 = Math.Clamp((int)MathF.Ceiling(maxY), 0, Height);
        if (x1 <= x0 || y1 <= y0)
            return;                                      // off screen: nothing to draw
        if (mode is >= 1 and <= 25)
            CopyBackdrop(x0, y0, x1, y1);

        if (_vboCursor + 4 > VboVertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(VboVertices * sizeof(Vertex)), null, BufferUsageARB.StreamDraw);
            _vboCursor = 0;
        }
        fixed (Vertex* p = v)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, _vboCursor * sizeof(Vertex), (nuint)(4 * sizeof(Vertex)), p);

        var prog = _programs[mode];
        _gl.UseProgram(prog.Program);
        _gl.Uniform2(prog.Viewport, (float)Width, (float)Height);
        if (prog.Params >= 0)
        {
            if (mode == Shaders.ModeTransition)
                _gl.Uniform4(prog.Params, rule!.Value.Lo, rule.Value.Hi, 0f, 0f);
            else
                _gl.Uniform4(prog.Params, 1f - e.Strength, e.Strength, e.P2, 0f);
        }
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.BindSampler(0, Nearest ? _samplerNearest : _samplerLinear);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, Backdrop.Texture);
        _gl.BindSampler(1, _samplerNearest);
        SetBlendState(state);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, _vboCursor, 4);
        _vboCursor += 4;
        DrawCount++;
    }

    private void CopyBackdrop(int x0, int y0, int x1, int y1)
    {
        int gx0, gy0, gx1, gy1;
        if (Blit == BlitMode.Full)
        {
            gx0 = 0; gy0 = 0; gx1 = Width; gy1 = Height;
        }
        else
        {
            gx0 = x0; gx1 = x1; gy0 = Height - y1; gy1 = Height - y0;   // frame rows are top-down, GL rows bottom-up
        }
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, Target.Fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, Backdrop.Fbo);
        Marks?.Mark("blit+");
        _gl.BlitFramebuffer(gx0, gy0, gx1, gy1, gx0, gy0, gx1, gy1, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        Marks?.Mark("blit-");
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, Target.Fbo);
        BlitCount++;
        BlitPixels += (long)(gx1 - gx0) * (gy1 - gy0);
    }

    /// <summary>The 14 records of table 0x2D8030 (compose.BLEND_STATES). GL ignores the factors of
    /// MIN / MAX, so the states that use them with factors (4, 10) are written as the equivalent
    /// ADD forms; the result equals compose.apply_blend_state for inputs in [0, 1].</summary>
    private void SetBlendState(int state)
    {
        if (state is < 0 or >= 14)
            state = 1;                                   // 0xF77E0: id >= 14 -> record 1
        if (state == 0)
        {
            _gl.Disable(EnableCap.Blend);
            return;
        }
        _gl.Enable(EnableCap.Blend);
        GLEnum add = GLEnum.FuncAdd;
        switch (state)
        {
            case 1:  Func(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha, add, GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha, add); break;
            case 2:  Func(GLEnum.SrcAlpha, GLEnum.One, add, GLEnum.SrcAlpha, GLEnum.One, add); break;
            case 3:  Func(GLEnum.Zero, GLEnum.OneMinusSrcAlpha, add, GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha, add); break;
            case 4:  Func(GLEnum.Zero, GLEnum.One, add, GLEnum.One, GLEnum.Zero, add); break;            // alpha := max(sa*1, da*0) = sa
            case 5:  Func(GLEnum.Zero, GLEnum.One, add, GLEnum.One, GLEnum.One, GLEnum.Min); break;      // alpha := min(sa, da)
            case 6:  Func(GLEnum.DstAlpha, GLEnum.OneMinusDstAlpha, add, GLEnum.Zero, GLEnum.One, add); break;
            case 7:  Func(GLEnum.DstAlpha, GLEnum.One, add, GLEnum.Zero, GLEnum.One, add); break;
            case 8:  Func(GLEnum.SrcAlpha, GLEnum.One, GLEnum.FuncSubtract, GLEnum.SrcAlpha, GLEnum.One, GLEnum.FuncSubtract); break;
            case 9:  Func(GLEnum.OneMinusDstColor, GLEnum.Zero, add, GLEnum.Zero, GLEnum.One, add); break;
            case 10: Func(GLEnum.Zero, GLEnum.One, add, GLEnum.Zero, GLEnum.Zero, add); break;           // alpha := min(0, 0) = 0
            case 11: Func(GLEnum.Zero, GLEnum.One, add, GLEnum.One, GLEnum.One, GLEnum.Max); break;      // alpha := max(sa, da)
            case 12: Func(GLEnum.Zero, GLEnum.One, add, GLEnum.SrcAlpha, GLEnum.Zero, add); break;
            case 13: Func(GLEnum.DstAlpha, GLEnum.OneMinusDstAlpha, add, GLEnum.SrcAlpha, GLEnum.OneMinusDstAlpha, add); break;
        }
    }

    private void Func(GLEnum cs, GLEnum cd, GLEnum cf, GLEnum @as, GLEnum ad, GLEnum af)
    {
        _gl.BlendFuncSeparate(cs, cd, @as, ad);
        _gl.BlendEquationSeparate(cf, af);
    }

    public static Vertex[] Rect(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, float r, float g, float b, float a) =>
    [
        new(x0, y0, u0, v0, r, g, b, a), new(x1, y0, u1, v0, r, g, b, a),
        new(x0, y1, u0, v1, r, g, b, a), new(x1, y1, u1, v1, r, g, b, a),
    ];

    /// <summary>Full-screen quad for a texture holding a copy of the target (GL rows: v flipped).</summary>
    private Vertex[] CaptureQuad(float alpha) => Rect(0, 0, Width, Height, 0, 1, 1, 0, 1, 1, 1, alpha);

    /// <summary>Transition start (0x6B910): effect 56 fill (state 11, alpha := 1), then copy the target
    /// into the capture surface (display-list record type 6). Run before the new scene is drawn.</summary>
    public void BeginTransition()
    {
        DrawQuad(0, Rect(0, 0, Width, Height, 0, 0, 1, 1, 1, 1, 1, 1), 56);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, Target.Fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, Capture.Fbo);
        _gl.BlitFramebuffer(0, 0, Width, Height, 0, 0, Width, Height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, Target.Fbo);
    }

    /// <summary>_FADS type 2 frame (0x67958): rule quad (PS #0, effect 58: RT.alpha := f), then the
    /// captured old frame through dst alpha (effect 26: rgb = old f + new (1 - f)).</summary>
    public void DrawRuleWipe(uint ruleTexture, float progress, int vague)
    {
        var rp = Effects.RuleParams(progress, vague);
        Marks?.Mark("rule+");
        DrawQuad(ruleTexture, Rect(0, 0, Width, Height, 0, 0, 1, 1, 1, 1, 1, 1), 58, rp);
        DrawQuad(Capture.Texture, CaptureQuad(1f), 26);
        Marks?.Mark("rule-");
    }

    /// <summary>_FADS type 0 frame (0x67A30): the capture with vertex alpha trunc(255 p) / 255, effect 0.</summary>
    public void DrawCrossfade(float progress) =>
        DrawQuad(Capture.Texture, CaptureQuad(Effects.FadsAlphaByte(progress) / 255f), 0);

    /// <summary>Scale the frame into the window with 16:9 letterboxing (ARCHITECTURE 6.2).</summary>
    public void Present(int winW, int winH)
    {
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        _gl.Viewport(0, 0, (uint)winW, (uint)winH);
        _gl.ClearColor(0, 0, 0, 1);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        float s = MathF.Min(winW / (float)Width, winH / (float)Height);
        int w = (int)(Width * s), h = (int)(Height * s);
        int x = (winW - w) / 2, y = (winH - h) / 2;
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, Target.Fbo);
        _gl.BlitFramebuffer(0, 0, Width, Height, x, y, x + w, y + h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
        BindTarget();
    }

    /// <summary>Upload top-down RGBA rows into a target texture (frame row 0 = top).</summary>
    public void Upload(RenderTarget t, byte[] topDown)
    {
        byte[] gl = FlipRows(topDown, t.Width, t.Height);
        _gl.BindTexture(TextureTarget.Texture2D, t.Texture);
        fixed (byte* p = gl)
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)t.Width, (uint)t.Height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
    }

    /// <summary>Read a target back as top-down RGBA rows.</summary>
    public byte[] Read(RenderTarget t)
    {
        var buf = new byte[t.Width * t.Height * 4];
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, t.Fbo);
        fixed (byte* p = buf)
            _gl.ReadPixels(0, 0, (uint)t.Width, (uint)t.Height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        BindTarget();
        return FlipRows(buf, t.Width, t.Height);
    }

    public static byte[] FlipRows(byte[] src, int w, int h)
    {
        var dst = new byte[src.Length];
        int stride = w * 4;
        for (int y = 0; y < h; y++)
            System.Buffer.BlockCopy(src, y * stride, dst, (h - 1 - y) * stride, stride);
        return dst;
    }

    public void Dispose()
    {
        foreach (var p in _programs.Values)
            _gl.DeleteProgram(p.Program);
        foreach (var t in new[] { Target, Backdrop, Capture })
        {
            _gl.DeleteFramebuffer(t.Fbo);
            _gl.DeleteTexture(t.Texture);
        }
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteSampler(_samplerLinear);
        _gl.DeleteSampler(_samplerNearest);
    }
}

// The GPU side of the display-list executor (docs/formats/compositing.md 3-7; ARCHITECTURE 6.3),
// lifted from spike S3 Compositor.cs: the shader is chosen from the draw-mode word as SelectShader does,
// the backdrop is copied before every backdrop-reading quad (glBlitFramebuffer of the quad bounds, or
// of the whole target as the reference), the fixed-function blend state is one of the 14 records, the
// targets are RGBA8 with straight alpha and no sRGB, and there are 16 capture surfaces.
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using Tsuki.Engine.Contracts;
using Tsuki.Render.Gl;
using Tsuki.Render.Shaders;

namespace Tsuki.Render.Compositor;

/// <summary>How much of the target is copied into the backdrop before a backdrop-reading quad.</summary>
public enum BackdropBlit
{
    /// <summary>Only the quad's pixel bounds (the default; S3 measured a byte-identical frame).</summary>
    Bounds,

    /// <summary>The whole target (the S3 reference).</summary>
    Full,
}

/// <summary>Texture filter for the source texture of textured quads.</summary>
public enum SamplingFilter
{
    /// <summary>Bilinear, no mipmaps (the runtime default).</summary>
    Linear,

    /// <summary>Nearest texel (aligned test frames).</summary>
    Nearest,
}

/// <summary>An RGBA8 texture with its framebuffer. Row 0 of the texture is the top row of the frame.</summary>
public sealed class RenderTarget
{
    internal RenderTarget(uint texture, uint fbo, int width, int height)
    {
        Texture = texture;
        Fbo = fbo;
        Width = width;
        Height = height;
    }

    /// <summary>The GL texture.</summary>
    public uint Texture { get; }

    /// <summary>The GL framebuffer with the texture as colour attachment 0.</summary>
    public uint Fbo { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }
}

/// <summary>Per-frame counters of the compositor.</summary>
public sealed class CompositorCounters
{
    /// <summary>Quads drawn.</summary>
    public int Draws { get; internal set; }

    /// <summary>Quads skipped because every vertex alpha is 0 (surface flag k = 4, 0xF0AE0).</summary>
    public int Culled { get; internal set; }

    /// <summary>Quads skipped because they lie outside the target.</summary>
    public int Offscreen { get; internal set; }

    /// <summary>Backdrop copies.</summary>
    public int Blits { get; internal set; }

    /// <summary>Pixels copied into the backdrop.</summary>
    public long BlitPixels { get; internal set; }

    /// <summary>Capture copies (display-list record type 6).</summary>
    public int Captures { get; internal set; }

    internal void Reset()
    {
        Draws = 0;
        Culled = 0;
        Offscreen = 0;
        Blits = 0;
        BlitPixels = 0;
        Captures = 0;
    }
}

/// <summary>
/// Draws quads into RGBA8 targets the way the PS4 display-list executor does. Every call needs the GL
/// context current on the calling thread.
/// </summary>
public sealed unsafe class GpuCompositor : IDisposable
{
    /// <summary>Number of capture surfaces (nexl_Capture[0..15], compositing 6).</summary>
    public const int CaptureSurfaceCount = 16;

    private const int VboVertices = 16384;

    private readonly GL _gl;
    private readonly CompositorProgramSet _programs;
    private readonly Dictionary<byte, RenderTarget> _targets = [];
    private readonly RenderTarget?[] _captures = new RenderTarget?[CaptureSurfaceCount];
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _samplerLinear;
    private readonly uint _samplerNearest;
    private int _vboCursor;
    private bool _disposed;

    /// <summary>Creates the frame target, the backdrop and the 27 programs.</summary>
    /// <param name="gl">Bindings of the current context.</param>
    /// <param name="width">Target width (1920 for the game frame).</param>
    /// <param name="height">Target height (1080).</param>
    /// <param name="dialect">GLSL dialect of the programs.</param>
    public GpuCompositor(GL gl, int width = 1920, int height = 1080, GlslDialect dialect = GlslDialect.Glsl330Core)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        _gl = gl;
        Width = width;
        Height = height;
        _programs = new CompositorProgramSet(gl, dialect);
        Backdrop = CreateTarget(width, height);
        _targets[0] = CreateTarget(width, height);

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(VboVertices * sizeof(GpuVertex)), null, BufferUsageARB.StreamDraw);
        uint stride = (uint)sizeof(GpuVertex);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)8);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, (void*)16);
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
        Bind(Frame);
    }

    /// <summary>Target width.</summary>
    public int Width { get; }

    /// <summary>Target height.</summary>
    public int Height { get; }

    /// <summary>Target 0, the frame.</summary>
    public RenderTarget Frame => _targets[0];

    /// <summary>The backdrop copy D of the blend shaders.</summary>
    public RenderTarget Backdrop { get; }

    /// <summary>The compiled programs.</summary>
    public CompositorProgramSet Programs => _programs;

    /// <summary>Backdrop copy extent (default <see cref="BackdropBlit.Bounds"/>).</summary>
    public BackdropBlit Blit { get; set; } = BackdropBlit.Bounds;

    /// <summary>Source texture filter (default <see cref="SamplingFilter.Linear"/>).</summary>
    public SamplingFilter Filter { get; set; } = SamplingFilter.Linear;

    /// <summary>Counters since the last <see cref="BeginFrame"/>.</summary>
    public CompositorCounters Counters { get; } = new();

    /// <summary>The GL bindings.</summary>
    public GL Gl => _gl;

    /// <summary>Render target <paramref name="id"/> (0 = the frame; others are offscreen surfaces, created on first use).</summary>
    public RenderTarget Target(byte id)
    {
        if (!_targets.TryGetValue(id, out RenderTarget? t))
        {
            t = CreateTarget(Width, Height);
            _targets[id] = t;
            ClearTarget(t, 0, 0, 0, 0);
        }

        return t;
    }

    /// <summary>Capture surface <paramref name="surface"/> (0..15), created on first use, cleared to 0.</summary>
    public RenderTarget CaptureSurface(int surface)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(surface);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(surface, CaptureSurfaceCount);
        if (_captures[surface] is not { } t)
        {
            t = CreateTarget(Width, Height);
            _captures[surface] = t;
            ClearTarget(t, 0, 0, 0, 0);
        }

        return t;
    }

    /// <summary>Starts a frame: resets the counters and the vertex stream.</summary>
    public void BeginFrame()
    {
        Counters.Reset();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(VboVertices * sizeof(GpuVertex)), null, BufferUsageARB.StreamDraw);
        _vboCursor = 0;
        Bind(Frame);
    }

    /// <summary>Clears a target to a colour (bytes / 255).</summary>
    public void Clear(byte target, byte r, byte g, byte b, byte a) => ClearTarget(Target(target), r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>
    /// Draws one quad. <paramref name="texture"/> = 0 draws an untextured fill (PS #3). The shader comes from the
    /// draw-mode word (<see cref="DrawEffect.SelectShader"/>), the blend state is <paramref name="blendState"/>.
    /// </summary>
    /// <param name="target">Render target id.</param>
    /// <param name="texture">Source texture, or 0 for a fill.</param>
    /// <param name="quad">The four vertices (strip order TL, TR, BL, BR or cyclic order; both are accepted).</param>
    /// <param name="word">Draw-mode word (compositing 5.1).</param>
    /// <param name="blendState">Blend state 0..13 (ids outside use record 1).</param>
    /// <param name="transition">Transition parameters of the texture, which select PS #0 in mode 0.</param>
    /// <param name="clip">Scissor rectangle in target pixels, or null.</param>
    /// <param name="fx">The optional shader inputs (STRA haze, a per-quad rule mask, an offscreen alpha mask, nearest filtering; package K7).</param>
    public void DrawQuad(byte target, uint texture, ReadOnlySpan<Vertex> quad, int word, int blendState, TransitionParams? transition = null, PixelRect? clip = null, in QuadFx fx = default)
    {
        if (quad.Length != 4)
        {
            throw new ArgumentException("a quad has 4 vertices", nameof(quad));
        }

        if ((quad[0].Rgba >> 24) == 0 && (quad[1].Rgba >> 24) == 0 && (quad[2].Rgba >> 24) == 0 && (quad[3].Rgba >> 24) == 0)
        {
            Counters.Culled++;                           // every vertex alpha <= 0: flag k = 4 (0xF0AE0)
            return;
        }

        if (fx.Mask != 0 && fx.Mask == target)
        {
            throw new InvalidOperationException($"a quad cannot be masked by its own target {target}");
        }

        if (fx.Pass == QuadPass.Rule)
        {
            transition = new TransitionParams(fx.A0, fx.A1, fx.A2);
        }

        RenderTarget rt = Target(target);
        DrawEffect e = DrawEffect.Decode(word);
        int mode = DrawEffect.SelectShader(texture != 0, e.ShaderMode, transition.HasValue);
        float minX = MathF.Min(MathF.Min(quad[0].X, quad[1].X), MathF.Min(quad[2].X, quad[3].X));
        float maxX = MathF.Max(MathF.Max(quad[0].X, quad[1].X), MathF.Max(quad[2].X, quad[3].X));
        float minY = MathF.Min(MathF.Min(quad[0].Y, quad[1].Y), MathF.Min(quad[2].Y, quad[3].Y));
        float maxY = MathF.Max(MathF.Max(quad[0].Y, quad[1].Y), MathF.Max(quad[2].Y, quad[3].Y));
        int x0 = Math.Clamp((int)MathF.Floor(minX), 0, rt.Width);
        int x1 = Math.Clamp((int)MathF.Ceiling(maxX), 0, rt.Width);
        int y0 = Math.Clamp((int)MathF.Floor(minY), 0, rt.Height);
        int y1 = Math.Clamp((int)MathF.Ceiling(maxY), 0, rt.Height);
        if (clip is { } c)
        {
            x0 = Math.Max(x0, c.X);
            y0 = Math.Max(y0, c.Y);
            x1 = Math.Min(x1, c.X + c.Width);
            y1 = Math.Min(y1, c.Y + c.Height);
        }

        if (x1 <= x0 || y1 <= y0)
        {
            Counters.Offscreen++;
            return;
        }

        if (CompositorShaders.ReadsBackdrop(mode))
        {
            CopyBackdrop(rt, x0, y0, x1, y1);
        }

        Bind(rt);
        if (_vboCursor + 4 > VboVertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(VboVertices * sizeof(GpuVertex)), null, BufferUsageARB.StreamDraw);
            _vboCursor = 0;
        }

        // Strip order needs v0-v3 and v1-v2 as the diagonals; a cyclic (fan) order is reordered.
        bool cyclic = MathF.Sign(Cross(quad[0], quad[1], quad[2])) == MathF.Sign(Cross(quad[0], quad[2], quad[3]));
        GpuVertex* v = stackalloc GpuVertex[4];
        v[0] = new GpuVertex(quad[0]);
        v[1] = new GpuVertex(quad[1]);
        v[2] = new GpuVertex(cyclic ? quad[3] : quad[2]);
        v[3] = new GpuVertex(cyclic ? quad[2] : quad[3]);
        _gl.BufferSubData(BufferTargetARB.ArrayBuffer, _vboCursor * sizeof(GpuVertex), (nuint)(4 * sizeof(GpuVertex)), v);

        CompositorProgram prog = _programs[mode];
        _gl.UseProgram(prog.Program.Handle);
        _gl.Uniform2(prog.Viewport, (float)rt.Width, (float)rt.Height);
        if (prog.Params >= 0)
        {
            if (mode == CompositorShaders.ModeTransition)
            {
                TransitionParams tp = transition!.Value;
                _gl.Uniform4(prog.Params, tp.Lo, tp.Hi, tp.Invert, 0f);
            }
            else
            {
                _gl.Uniform4(prog.Params, 1f - e.Strength, e.Strength, DrawEffect.P2For(e.ShaderMode, blendState), 0f);
            }
        }

        bool haze = fx.Pass == QuadPass.Haze;
        if (prog.Fx >= 0)
        {
            _gl.Uniform4(prog.Fx, haze ? 1f : 0f, fx.Mask != 0 ? 1f : 0f, fx.Pass == QuadPass.Blur ? 1f : 0f, 0f);
        }

        if (fx.Pass == QuadPass.Blur && prog.Blur >= 0)
        {
            _gl.Uniform4(prog.Blur, fx.A0, fx.A1, fx.A2, fx.A3);
        }

        if (haze && prog.HazeA >= 0)
        {
            _gl.Uniform4(prog.HazeA, fx.A0, fx.A1, fx.A2, fx.A3);
            _gl.Uniform4(prog.HazeB, fx.B0, fx.B1, fx.B2, fx.B3);
        }

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.BindSampler(0, Filter == SamplingFilter.Nearest || fx.Nearest ? _samplerNearest : _samplerLinear);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, Backdrop.Texture);
        _gl.BindSampler(1, _samplerNearest);
        if (fx.Mask != 0)
        {
            _gl.ActiveTexture(TextureUnit.Texture2);
            _gl.BindTexture(TextureTarget.Texture2D, Target(fx.Mask).Texture);
            _gl.BindSampler(2, _samplerNearest);
            _gl.ActiveTexture(TextureUnit.Texture0);
        }
        BlendStates.Apply(_gl, blendState);
        if (clip is { } sc)
        {
            _gl.Enable(EnableCap.ScissorTest);
            _gl.Scissor(sc.X, sc.Y, (uint)Math.Max(0, sc.Width), (uint)Math.Max(0, sc.Height));   // frame row 0 = GL row 0
        }

        _gl.DrawArrays(PrimitiveType.TriangleStrip, _vboCursor, 4);
        if (clip is not null)
        {
            _gl.Disable(EnableCap.ScissorTest);
        }

        _vboCursor += 4;
        Counters.Draws++;
    }

    /// <summary>Copies target <paramref name="target"/> into capture surface <paramref name="surface"/> (record type 6); the target stays bound.</summary>
    public void Capture(byte target, int surface)
    {
        RenderTarget src = Target(target);
        RenderTarget dst = CaptureSurface(surface);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, src.Fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, dst.Fbo);
        _gl.BlitFramebuffer(0, 0, src.Width, src.Height, 0, 0, dst.Width, dst.Height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        Bind(src);
        Counters.Captures++;
    }

    /// <summary>Scales the frame into the window's default framebuffer with 16:9 letterboxing (ARCHITECTURE 6.2).</summary>
    public void Present(int windowWidth, int windowHeight)
    {
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        _gl.Viewport(0, 0, (uint)Math.Max(1, windowWidth), (uint)Math.Max(1, windowHeight));
        _gl.Disable(EnableCap.ScissorTest);
        _gl.ClearColor(0, 0, 0, 1);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        float s = MathF.Min(windowWidth / (float)Width, windowHeight / (float)Height);
        int w = (int)(Width * s), h = (int)(Height * s);
        int x = (windowWidth - w) / 2, y = (windowHeight - h) / 2;
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, Frame.Fbo);
        // Frame row 0 is the top, the window's GL row 0 the bottom: flip while scaling.
        _gl.BlitFramebuffer(0, 0, Width, Height, x, y + h, x + w, y, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
        Bind(Frame);
    }

    /// <summary>
    /// Reads the window's back buffer (what <see cref="Present"/> just drew: the letterboxed frame at the window's own size) as RGBA rows, top row first, with the
    /// alpha set to 255 (package K21: the screenshot key). Call it after <see cref="Present"/> and before the swap.
    /// </summary>
    public byte[] ReadWindow(int windowWidth, int windowHeight)
    {
        int w = Math.Max(1, windowWidth);
        int h = Math.Max(1, windowHeight);
        var buf = new byte[w * h * 4];
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        fixed (byte* p = buf)
        {
            _gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        Bind(Frame);
        // GL row 0 is the bottom of the window: flip into top-down rows, and the back buffer has no alpha worth keeping
        int stride = w * 4;
        var row = new byte[stride];
        for (int y = 0; y < h / 2; y++)
        {
            Span<byte> top = buf.AsSpan(y * stride, stride);
            Span<byte> bottom = buf.AsSpan((h - 1 - y) * stride, stride);
            top.CopyTo(row);
            bottom.CopyTo(top);
            row.CopyTo(bottom);
        }

        for (int a = 3; a < buf.Length; a += 4)
        {
            buf[a] = 255;
        }

        return buf;
    }

    /// <summary>Reads a target back as RGBA rows, top row first.</summary>
    public byte[] Read(RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var buf = new byte[target.Width * target.Height * 4];
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, target.Fbo);
        fixed (byte* p = buf)
        {
            _gl.ReadPixels(0, 0, (uint)target.Width, (uint)target.Height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        Bind(Frame);
        return buf;
    }

    /// <summary>Overwrites a target with RGBA rows, top row first (test setup and the old-frame restore).</summary>
    public void Load(RenderTarget target, ReadOnlySpan<byte> rgba)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (rgba.Length != target.Width * target.Height * 4)
        {
            throw new ArgumentException($"{rgba.Length} bytes for a {target.Width} x {target.Height} target", nameof(rgba));
        }

        _gl.BindTexture(TextureTarget.Texture2D, target.Texture);
        fixed (byte* p = rgba)
        {
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)target.Width, (uint)target.Height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
    }

    /// <summary>Creates an RGBA8 texture (rows top-down; first row = v 0) for tests and small sources.</summary>
    public uint CreateTexture(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException($"{rgba.Length} bytes for a {width} x {height} texture", nameof(rgba));
        }

        uint tex = NewTexture(_gl);
        fixed (byte* p = rgba)
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        return tex;
    }

    /// <summary>Deletes a texture made by <see cref="CreateTexture"/>.</summary>
    public void DeleteTexture(uint texture) => _gl.DeleteTexture(texture);

    /// <summary>A texture name with the compositor's parameters (clamp, no mipmaps), bound to unit 0's 2D target.</summary>
    internal static uint NewTexture(GL gl)
    {
        uint tex = gl.GenTexture();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        return tex;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _programs.Dispose();
        foreach (RenderTarget t in _targets.Values.Concat(_captures.OfType<RenderTarget>()).Append(Backdrop))
        {
            _gl.DeleteFramebuffer(t.Fbo);
            _gl.DeleteTexture(t.Texture);
        }

        _targets.Clear();
        Array.Clear(_captures);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteSampler(_samplerLinear);
        _gl.DeleteSampler(_samplerNearest);
    }

    private static float Cross(in Vertex o, in Vertex a, in Vertex b) => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));

    private void Bind(RenderTarget t)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, t.Fbo);
        _gl.Viewport(0, 0, (uint)t.Width, (uint)t.Height);
    }

    private void ClearTarget(RenderTarget t, float r, float g, float b, float a)
    {
        Bind(t);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.ClearColor(r, g, b, a);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
    }

    private void CopyBackdrop(RenderTarget rt, int x0, int y0, int x1, int y1)
    {
        if (Blit == BackdropBlit.Full)
        {
            x0 = 0;
            y0 = 0;
            x1 = rt.Width;
            y1 = rt.Height;
        }

        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, rt.Fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, Backdrop.Fbo);
        _gl.BlitFramebuffer(x0, y0, x1, y1, x0, y0, x1, y1, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        Counters.Blits++;
        Counters.BlitPixels += (long)(x1 - x0) * (y1 - y0);
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
        uint tex = NewTexture(_gl);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        uint fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException($"framebuffer incomplete: {status}");
        }

        return new RenderTarget(tex, fbo, w, h);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct GpuVertex(in Vertex v)
    {
        public readonly float X = v.X;
        public readonly float Y = v.Y;
        public readonly float U = v.U;
        public readonly float V = v.V;
        public readonly uint Rgba = v.Rgba;
    }
}

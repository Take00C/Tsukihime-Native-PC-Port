// Spike S2: plane upload + the PS #4 "basic YCbCr" shader (compositing 2.3; tsuki.compose.ps_ycbcr) on GL 3.3 core.
using System.Diagnostics;
using Silk.NET.OpenGL;

namespace S2.MovieSync;

/// <summary>Uploads Y / Cb / Cr (or Y / CbCr) planes into textures and draws them letterboxed with the BT.709 limited-range shader.</summary>
internal sealed unsafe class YuvRenderer : IDisposable
{
    // The constants are the float32 bit patterns of tsuki.compose (YUV_Y_MUL, YUV_Y_ADD, YUV[...]), so the
    // GLSL arithmetic matches ps_ycbcr operation for operation. The source compiles as GLSL 3.30 and ES 3.00.
    private const string FragmentBody = """
        in vec2 v_uv;
        out vec4 o_color;
        uniform sampler2D u_y;
        uniform sampler2D u_c0;
        uniform sampler2D u_c1;
        uniform int u_nv12;
        void main()
        {
            float Y = texture(u_y, v_uv).r;
            float cb;
            float cr;
            if (u_nv12 != 0) { vec2 c = texture(u_c0, v_uv).rg; cb = c.r; cr = c.g; }
            else { cb = texture(u_c0, v_uv).r; cr = texture(u_c1, v_uv).r; }
            float y = Y * uintBitsToFloat(0x3F950B0Fu) + uintBitsToFloat(0xBD950B0Fu);
            float g = y + (cb * uintBitsToFloat(0xBE5A6B51u) + uintBitsToFloat(0x3DDA6B51u))
                        + (cr * uintBitsToFloat(0xBF086C22u) + uintBitsToFloat(0x3E886C22u));
            float r = y + (cr * uintBitsToFloat(0x3FE57732u) + uintBitsToFloat(0xBF657732u));
            float b = y + (cb * uintBitsToFloat(0x40073190u) + uintBitsToFloat(0xBF873190u));
            o_color = vec4(r, g, b, 1.0);
        }
        """;

    // Full-screen triangle from gl_VertexID; uv (0,0) is the first uploaded row (top of the picture).
    private const string VertexBody = """
        out vec2 v_uv;
        void main()
        {
            vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            v_uv = vec2(p.x, 1.0 - p.y);
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private readonly GL _gl;
    private readonly int _w;
    private readonly int _h;
    private readonly uint _program;
    private readonly uint _vao;
    private readonly uint _texY;
    private readonly uint _texC0;
    private readonly uint _texC1;
    private readonly uint _texNv12;
    private readonly int _locNv12;
    private readonly bool _usePbo;
    private readonly uint[] _pbo = new uint[3];
    private int _pboNext;
    private readonly uint[] _uploadQueries = new uint[8];
    private readonly uint[] _drawQueries = new uint[8];
    private readonly bool[] _uploadPending = new bool[8];
    private readonly bool[] _drawPending = new bool[8];
    private int _uploadSlot;
    private int _drawSlot;
    private PlaneLayout _layout = PlaneLayout.Yuv420P;

    public YuvRenderer(GL gl, int width, int height, bool usePbo)
    {
        _gl = gl;
        _w = width;
        _h = height;
        _usePbo = usePbo;
        _program = Link(Compile(ShaderType.VertexShader, "#version 330 core\n" + VertexBody),
                        Compile(ShaderType.FragmentShader, "#version 330 core\n" + FragmentBody));
        _vao = gl.GenVertexArray();
        _texY = MakeTexture(InternalFormat.R8, width, height);
        _texC0 = MakeTexture(InternalFormat.R8, width / 2, height / 2);
        _texC1 = MakeTexture(InternalFormat.R8, width / 2, height / 2);
        _texNv12 = MakeTexture(InternalFormat.RG8, width / 2, height / 2);
        gl.UseProgram(_program);
        gl.Uniform1(gl.GetUniformLocation(_program, "u_y"), 0);
        gl.Uniform1(gl.GetUniformLocation(_program, "u_c0"), 1);
        gl.Uniform1(gl.GetUniformLocation(_program, "u_c1"), 2);
        _locNv12 = gl.GetUniformLocation(_program, "u_nv12");
        gl.GenQueries(_uploadQueries);
        gl.GenQueries(_drawQueries);
        if (usePbo)
        {
            gl.GenBuffers(_pbo);
            nuint size = (nuint)(width * height * 3 / 2);
            foreach (uint b in _pbo)
            {
                gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, b);
                gl.BufferData(BufferTargetARB.PixelUnpackBuffer, size, null, BufferUsageARB.StreamDraw);
            }

            gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
        }

        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
    }

    /// <summary>CPU time of the glTexSubImage2D calls (plus PBO map/copy), per uploaded frame.</summary>
    public Series UploadCpuMs { get; } = new("ms");

    /// <summary>GPU time of the upload commands (GL_TIME_ELAPSED), per uploaded frame.</summary>
    public Series UploadGpuMs { get; } = new("ms");

    /// <summary>GPU time of the YCbCr full-screen draw, per presented frame.</summary>
    public Series DrawGpuMs { get; } = new("ms");

    /// <summary>Also checks the fragment shader compiles as GLSL ES 3.00 text (ARCHITECTURE 6.5); returns the info log or "".</summary>
    public string EsSourceCheck()
    {
        // A desktop GL 3.3 context cannot compile "#version 300 es" unless it supports ARB_ES3_compatibility.
        uint s = _gl.CreateShader(ShaderType.FragmentShader);
        _gl.ShaderSource(s, "#version 300 es\nprecision highp float;\nprecision highp int;\n" + FragmentBody);
        _gl.CompileShader(s);
        _gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        string log = ok != 0 ? "" : _gl.GetShaderInfoLog(s);
        _gl.DeleteShader(s);
        return ok != 0 ? "ok" : "failed: " + log.Trim();
    }

    public void Upload(VideoFrame f)
    {
        CollectQueries();
        long t0 = Stopwatch.GetTimestamp();
        uint q = _uploadQueries[_uploadSlot];
        bool timed = !_uploadPending[_uploadSlot];
        if (timed)
        {
            _gl.BeginQuery(QueryTarget.TimeElapsed, q);
        }

        _layout = f.Layout;
        int cw = _w / 2, ch = _h / 2;
        if (_usePbo)
        {
            uint b = _pbo[_pboNext];
            _pboNext = (_pboNext + 1) % _pbo.Length;
            _gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, b);
            int ySize = _w * _h, cSize = cw * ch;
            nuint total = (nuint)(ySize + 2 * cSize);
            byte* dst = (byte*)_gl.MapBufferRange(BufferTargetARB.PixelUnpackBuffer, 0, total,
                (uint)(MapBufferAccessMask.WriteBit | MapBufferAccessMask.InvalidateBufferBit));
            fixed (byte* y = f.Y, c0 = f.C0, c1 = f.C1)
            {
                System.Buffer.MemoryCopy(y, dst, ySize, ySize);
                if (f.Layout == PlaneLayout.Nv12)
                {
                    System.Buffer.MemoryCopy(c0, dst + ySize, 2 * cSize, 2 * cSize);
                }
                else
                {
                    System.Buffer.MemoryCopy(c0, dst + ySize, cSize, cSize);
                    System.Buffer.MemoryCopy(c1, dst + ySize + cSize, cSize, cSize);
                }
            }

            _gl.UnmapBuffer(BufferTargetARB.PixelUnpackBuffer);
            Sub(_texY, _w, _h, PixelFormat.Red, (void*)0);
            if (f.Layout == PlaneLayout.Nv12)
            {
                Sub(_texNv12, cw, ch, PixelFormat.RG, (void*)ySize);
            }
            else
            {
                Sub(_texC0, cw, ch, PixelFormat.Red, (void*)ySize);
                Sub(_texC1, cw, ch, PixelFormat.Red, (void*)(ySize + cSize));
            }

            _gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
        }
        else
        {
            fixed (byte* y = f.Y, c0 = f.C0, c1 = f.C1)
            {
                Sub(_texY, _w, _h, PixelFormat.Red, y);
                if (f.Layout == PlaneLayout.Nv12)
                {
                    Sub(_texNv12, cw, ch, PixelFormat.RG, c0);
                }
                else
                {
                    Sub(_texC0, cw, ch, PixelFormat.Red, c0);
                    Sub(_texC1, cw, ch, PixelFormat.Red, c1);
                }
            }
        }

        if (timed)
        {
            _gl.EndQuery(QueryTarget.TimeElapsed);
            _uploadPending[_uploadSlot] = true;
            _uploadSlot = (_uploadSlot + 1) % _uploadQueries.Length;
        }

        UploadCpuMs.Add(Report.Ms(Stopwatch.GetTimestamp() - t0));
    }

    /// <summary>Draws the current planes into the bound framebuffer, letterboxed into <paramref name="fbW"/> x <paramref name="fbH"/>.</summary>
    public void Draw(int fbW, int fbH, bool timed)
    {
        int vw = fbW, vh = fbW * _h / _w;
        if (vh > fbH)
        {
            vh = fbH;
            vw = fbH * _w / _h;
        }

        _gl.Viewport(0, 0, (uint)fbW, (uint)fbH);
        _gl.ClearColor(0, 0, 0, 1);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _gl.Viewport((fbW - vw) / 2, (fbH - vh) / 2, (uint)vw, (uint)vh);
        uint q = _drawQueries[_drawSlot];
        bool doTime = timed && !_drawPending[_drawSlot];
        if (doTime)
        {
            _gl.BeginQuery(QueryTarget.TimeElapsed, q);
        }

        _gl.UseProgram(_program);
        _gl.Uniform1(_locNv12, _layout == PlaneLayout.Nv12 ? 1 : 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _texY);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _layout == PlaneLayout.Nv12 ? _texNv12 : _texC0);
        _gl.ActiveTexture(TextureUnit.Texture2);
        _gl.BindTexture(TextureTarget.Texture2D, _texC1);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        if (doTime)
        {
            _gl.EndQuery(QueryTarget.TimeElapsed);
            _drawPending[_drawSlot] = true;
            _drawSlot = (_drawSlot + 1) % _drawQueries.Length;
        }
    }

    /// <summary>Renders the current planes at 1920 x 1080 into an offscreen RGBA8 target and returns top-down RGB24.</summary>
    public byte[] ReadBackRgb()
    {
        uint tex = MakeTexture(InternalFormat.Rgba8, _w, _h);
        uint fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
        Draw(_w, _h, timed: false);
        var rgba = new byte[_w * _h * 4];
        fixed (byte* p = rgba)
        {
            _gl.ReadPixels(0, 0, (uint)_w, (uint)_h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.DeleteFramebuffer(fbo);
        _gl.DeleteTexture(tex);
        var rgb = new byte[_w * _h * 3];
        for (int y = 0; y < _h; y++)
        {
            int src = (_h - 1 - y) * _w * 4; // GL rows are bottom-up
            int dst = y * _w * 3;
            for (int x = 0; x < _w; x++)
            {
                rgb[dst + x * 3] = rgba[src + x * 4];
                rgb[dst + x * 3 + 1] = rgba[src + x * 4 + 1];
                rgb[dst + x * 3 + 2] = rgba[src + x * 4 + 2];
            }
        }

        return rgb;
    }

    /// <summary>Drops the samples gathered so far (after the warm-up frame), waiting for pending queries.</summary>
    public void ResetStats()
    {
        _gl.Finish();
        CollectQueries();
        UploadCpuMs.Clear();
        UploadGpuMs.Clear();
        DrawGpuMs.Clear();
    }

    /// <summary>Reads finished timer queries without stalling.</summary>
    public void CollectQueries()
    {
        Collect(_uploadQueries, _uploadPending, UploadGpuMs);
        Collect(_drawQueries, _drawPending, DrawGpuMs);
    }

    private void Collect(uint[] queries, bool[] pending, Series into)
    {
        for (int i = 0; i < queries.Length; i++)
        {
            if (!pending[i])
            {
                continue;
            }

            _gl.GetQueryObject(queries[i], QueryObjectParameterName.ResultAvailable, out int avail);
            if (avail != 0)
            {
                _gl.GetQueryObject(queries[i], QueryObjectParameterName.Result, out ulong ns);
                into.Add(ns / 1e6);
                pending[i] = false;
            }
        }
    }

    private void Sub(uint tex, int w, int h, PixelFormat fmt, void* data)
    {
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)w, (uint)h, fmt, PixelType.UnsignedByte, data);
    }

    private uint MakeTexture(InternalFormat fmt, int w, int h)
    {
        uint t = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, t);
        PixelFormat pf = fmt switch
        {
            InternalFormat.R8 => PixelFormat.Red,
            InternalFormat.RG8 => PixelFormat.RG,
            _ => PixelFormat.Rgba,
        };
        _gl.TexImage2D(TextureTarget.Texture2D, 0, fmt, (uint)w, (uint)h, 0, pf, PixelType.UnsignedByte, (void*)0);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return t;
    }

    private uint Compile(ShaderType type, string src)
    {
        uint s = _gl.CreateShader(type);
        _gl.ShaderSource(s, src);
        _gl.CompileShader(s);
        _gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            throw new InvalidOperationException($"{type} compile failed: {_gl.GetShaderInfoLog(s)}");
        }

        return s;
    }

    private uint Link(uint vs, uint fs)
    {
        uint p = _gl.CreateProgram();
        _gl.AttachShader(p, vs);
        _gl.AttachShader(p, fs);
        _gl.LinkProgram(p);
        _gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0)
        {
            throw new InvalidOperationException("link failed: " + _gl.GetProgramInfoLog(p));
        }

        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        return p;
    }

    public void Dispose()
    {
        _gl.DeleteProgram(_program);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteTextures([_texY, _texC0, _texC1, _texNv12]);
        _gl.DeleteQueries(_uploadQueries);
        _gl.DeleteQueries(_drawQueries);
        if (_usePbo)
        {
            _gl.DeleteBuffers(_pbo);
        }
    }
}

// Shader compile seam (ARCHITECTURE 6.5; PACE 7.2 #1, H1): every shader is written once in the
// GLSL subset that compiles both as "#version 330 core" (the main path) and as "#version 300 es"
// (the ANGLE fallback). Lifted from spike S3 Shaders.Compile / Link.
using System.Text;
using Silk.NET.OpenGL;

namespace Tsuki.Render.Gl;

/// <summary>The two GLSL dialects every shader must compile as (ARCHITECTURE 6.5).</summary>
public enum GlslDialect
{
    /// <summary><c>#version 330 core</c>, the OpenGL 3.3 core path.</summary>
    Glsl330Core,

    /// <summary><c>#version 300 es</c>, the GLES 3 path (ANGLE).</summary>
    GlslEs300,
}

/// <summary>A compile or link failure, with the driver's log.</summary>
public sealed class ShaderCompileException(string message, GlslDialect dialect, string log) : Exception(message + ": " + log.Trim())
{
    /// <summary>The dialect that failed.</summary>
    public GlslDialect Dialect { get; } = dialect;

    /// <summary>The driver's info log.</summary>
    public string Log { get; } = log;
}

/// <summary>A linked program. Delete it with <see cref="Delete"/> while its context is current.</summary>
public sealed class ShaderProgram
{
    internal ShaderProgram(uint handle, GlslDialect dialect, string log)
    {
        Handle = handle;
        Dialect = dialect;
        Log = log;
    }

    /// <summary>The GL program name.</summary>
    public uint Handle { get; private set; }

    /// <summary>The dialect it was compiled as.</summary>
    public GlslDialect Dialect { get; }

    /// <summary>The compile and link logs (usually empty).</summary>
    public string Log { get; }

    /// <summary>Deletes the program.</summary>
    public void Delete(GL gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        if (Handle != 0)
        {
            gl.DeleteProgram(Handle);
            Handle = 0;
        }
    }
}

/// <summary>Compiles shader bodies (no <c>#version</c> line) as either GLSL dialect.</summary>
public static class ShaderCompiler
{
    /// <summary>The header of <c>#version 330 core</c> sources.</summary>
    public const string Glsl330CoreHeader = "#version 330 core\n";

    /// <summary>The header of <c>#version 300 es</c> sources: ES needs explicit default precisions.</summary>
    public const string GlslEs300Header = "#version 300 es\nprecision highp float;\nprecision highp int;\nprecision highp sampler2D;\n";

    /// <summary>Both dialects, main path first.</summary>
    public static IReadOnlyList<GlslDialect> AllDialects { get; } = [GlslDialect.Glsl330Core, GlslDialect.GlslEs300];

    /// <summary>The <c>#version</c> header of a dialect.</summary>
    public static string Header(GlslDialect dialect) => dialect switch
    {
        GlslDialect.Glsl330Core => Glsl330CoreHeader,
        GlslDialect.GlslEs300 => GlslEs300Header,
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
    };

    /// <summary>The full source: header, one <c>#define</c> per entry, then the body.</summary>
    public static string Source(GlslDialect dialect, string body, IEnumerable<string>? defines = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        var sb = new StringBuilder(Header(dialect));
        foreach (string d in defines ?? [])
        {
            sb.Append("#define ").Append(d).Append('\n');
        }

        return sb.Append(body).ToString();
    }

    /// <summary>Compiles one stage. Returns the shader name, or 0 with the log on failure.</summary>
    public static uint TryCompile(GL gl, ShaderType stage, string source, out string log)
    {
        ArgumentNullException.ThrowIfNull(gl);
        uint id = gl.CreateShader(stage);
        gl.ShaderSource(id, source);
        gl.CompileShader(id);
        gl.GetShader(id, ShaderParameterName.CompileStatus, out int ok);
        log = gl.GetShaderInfoLog(id) ?? "";
        if (ok == 0)
        {
            gl.DeleteShader(id);
            return 0;
        }

        return id;
    }

    /// <summary>Compiles and links a vertex + fragment program in one dialect; throws on failure.</summary>
    public static ShaderProgram CompileProgram(GL gl, GlslDialect dialect, string vertexBody, string fragmentBody, IEnumerable<string>? defines = null)
    {
        ArgumentNullException.ThrowIfNull(gl);
        string[] defs = defines?.ToArray() ?? [];
        uint vs = TryCompile(gl, ShaderType.VertexShader, Source(dialect, vertexBody, defs), out string vlog);
        if (vs == 0)
        {
            throw new ShaderCompileException($"vertex shader did not compile as {dialect}", dialect, vlog);
        }

        uint fs = TryCompile(gl, ShaderType.FragmentShader, Source(dialect, fragmentBody, defs), out string flog);
        if (fs == 0)
        {
            gl.DeleteShader(vs);
            throw new ShaderCompileException($"fragment shader did not compile as {dialect}", dialect, flog);
        }

        uint p = gl.CreateProgram();
        gl.AttachShader(p, vs);
        gl.AttachShader(p, fs);
        gl.LinkProgram(p);
        gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int linked);
        string plog = gl.GetProgramInfoLog(p) ?? "";
        gl.DetachShader(p, vs);
        gl.DetachShader(p, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        if (linked == 0)
        {
            gl.DeleteProgram(p);
            throw new ShaderCompileException($"program did not link as {dialect}", dialect, plog);
        }

        return new ShaderProgram(p, dialect, string.Join("\n", new[] { vlog, flog, plog }.Where(s => s.Trim().Length > 0)));
    }
}

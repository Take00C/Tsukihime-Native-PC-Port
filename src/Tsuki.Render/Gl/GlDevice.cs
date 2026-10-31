// The GL seam between Tsuki.Platform (which owns the SDL window and context) and Tsuki.Render
// (which owns Silk.NET.OpenGL): Platform hands over a proc-address loader, Render builds the
// bindings from it (ARCHITECTURE 3.1, 6.5; PACE 7.2 #1, H1). Lifted from spike S3 GlWindow.
using Silk.NET.OpenGL;

namespace Tsuki.Render.Gl;

/// <summary>What the current GL context reports about itself.</summary>
/// <param name="Major">GL_MAJOR_VERSION.</param>
/// <param name="Minor">GL_MINOR_VERSION.</param>
/// <param name="CoreProfile">True when GL_CONTEXT_PROFILE_MASK has the core bit.</param>
/// <param name="Version">GL_VERSION.</param>
/// <param name="Renderer">GL_RENDERER.</param>
/// <param name="Vendor">GL_VENDOR.</param>
/// <param name="ShadingLanguage">GL_SHADING_LANGUAGE_VERSION.</param>
public sealed record GlInfo(int Major, int Minor, bool CoreProfile, string Version, string Renderer, string Vendor, string ShadingLanguage)
{
    /// <summary>True for a 3.3 (or newer) core context, the main path of ARCHITECTURE 6.5.</summary>
    public bool IsGl33Core => CoreProfile && (Major > 3 || (Major == 3 && Minor >= 3));

    /// <inheritdoc/>
    public override string ToString() =>
        $"GL {Major}.{Minor} {(CoreProfile ? "core" : "compatibility")}; GL_VERSION \"{Version}\"; GL_RENDERER \"{Renderer}\"; " +
        $"GL_VENDOR \"{Vendor}\"; GLSL \"{ShadingLanguage}\"";
}

/// <summary>
/// The Silk.NET bindings of one GL context, loaded through the window's proc-address function.
/// The context must be current on the calling thread for every call.
/// </summary>
public sealed class GlDevice
{
    private const int ContextCoreProfileBit = 0x1;

    private GlDevice(GL gl)
    {
        Gl = gl;
        Info = QueryInfo(gl);
    }

    /// <summary>The raw bindings, for the rest of Tsuki.Render.</summary>
    public GL Gl { get; }

    /// <summary>What the context reports.</summary>
    public GlInfo Info { get; }

    /// <summary>Loads the bindings of the context that is current on this thread.</summary>
    /// <param name="getProcAddress">The window's GL loader (SDL_GL_GetProcAddress).</param>
    public static GlDevice Create(Func<string, nint> getProcAddress)
    {
        ArgumentNullException.ThrowIfNull(getProcAddress);
        return new GlDevice(GL.GetApi(name => getProcAddress(name)));
    }

    /// <summary>Clears the default framebuffer of a window of the given pixel size.</summary>
    public void ClearDefaultFramebuffer(int width, int height, float r, float g, float b)
    {
        Gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        Gl.Viewport(0, 0, (uint)Math.Max(width, 1), (uint)Math.Max(height, 1));
        Gl.ClearColor(r, g, b, 1f);
        Gl.Clear(ClearBufferMask.ColorBufferBit);
    }

    /// <summary>Waits until every GL command issued so far has completed.</summary>
    public void Finish() => Gl.Finish();

    /// <summary>The first pending GL error as text, or null when there is none.</summary>
    public string? TakeError()
    {
        GLEnum e = Gl.GetError();
        return e == GLEnum.NoError ? null : e.ToString();
    }

    private static GlInfo QueryInfo(GL gl)
    {
        gl.GetInteger(GetPName.MajorVersion, out int major);
        gl.GetInteger(GetPName.MinorVersion, out int minor);
        gl.GetInteger(GetPName.ContextProfileMask, out int profile);
        return new GlInfo(
            major,
            minor,
            (profile & ContextCoreProfileBit) != 0,
            gl.GetStringS(StringName.Version) ?? "",
            gl.GetStringS(StringName.Renderer) ?? "",
            gl.GetStringS(StringName.Vendor) ?? "",
            gl.GetStringS(StringName.ShadingLanguageVersion) ?? "");
    }
}

using Tsuki.Platform;
using Tsuki.Render.Gl;

namespace Tsuki.Tests.Render.Smoke;

/// <summary>
/// M0 acceptance 3, GPU part (PACE 7.2 #1, H1): a hidden SDL3 window gets a GL 3.3 core context, and
/// the smoke shader compiles, links and draws the expected pixels as GLSL 3.30 core and as GLSL ES
/// 3.00 (ARCHITECTURE 6.5). Needs the local GPU; never skips.
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class GlSmokeTests
{
    [Fact]
    public void HiddenWindowGetsAGl33CoreContext()
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("Tsuki.Tests.Render GL smoke", 320, 180, Hidden: true, Resizable: false));
        GlDevice device = GlDevice.Create(window.GetProcAddress);

        Assert.True(window.Context.CoreProfile, window.Context.ToString());
        Assert.Equal(3, window.Context.Major);
        Assert.True(window.Context.Minor >= 3, window.Context.ToString());
        Assert.True(device.Info.IsGl33Core, device.Info.ToString());
        Assert.NotEmpty(device.Info.Renderer);
        Assert.Null(device.TakeError());
    }

    [Fact]
    public void SmokeShaderDrawsTheExpectedPixelsInBothDialects()
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("Tsuki.Tests.Render GL smoke", 320, 180, Hidden: true, Resizable: false));
        GlDevice device = GlDevice.Create(window.GetProcAddress);
        byte[] expected = SmokeShader.Expected();

        SmokeRender core = SmokeShader.Render(device, GlslDialect.Glsl330Core);
        SmokeRender es = SmokeShader.Render(device, GlslDialect.GlslEs300);

        Assert.Equal(SmokeShader.Width * SmokeShader.Height * 4, core.Pixels.Length);
        Assert.Equal(expected, core.Pixels);
        Assert.Equal(expected, es.Pixels);
        Assert.Equal(0, core.Mismatches);
        Assert.Equal(0, es.Mismatches);

        // The pattern is not flat: red steps with x, green with y (16, 48, ... 239 and 32, 96, 159, 223).
        Assert.Equal(new byte[] { 16, 32, 153, 255 }, core.Pixels[..4]);
        int lastPixel = core.Pixels.Length - 4;
        Assert.Equal(new byte[] { 239, 223, 153, 255 }, core.Pixels[lastPixel..]);
        Assert.Null(device.TakeError());
    }

    [Fact]
    public void BrokenShaderFailsWithTheDriverLogInBothDialects()
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("Tsuki.Tests.Render GL smoke", 320, 180, Hidden: true, Resizable: false));
        GlDevice device = GlDevice.Create(window.GetProcAddress);
        const string Broken = "layout(location = 0) out vec4 o_color;\nvoid main() { o_color = undefined_symbol; }\n";

        foreach (GlslDialect dialect in ShaderCompiler.AllDialects)
        {
            var e = Assert.Throws<ShaderCompileException>(() => ShaderCompiler.CompileProgram(device.Gl, dialect, SmokeShader.VertexBody, Broken));
            Assert.Equal(dialect, e.Dialect);
            Assert.False(string.IsNullOrWhiteSpace(e.Log), "the driver gave no compile log");
        }

        Assert.Null(device.TakeError());
    }

    [Fact]
    public void SourcesCarryTheDialectHeaderAndDefines()
    {
        string core = ShaderCompiler.Source(GlslDialect.Glsl330Core, "void main() {}\n", ["MODE 3"]);
        string es = ShaderCompiler.Source(GlslDialect.GlslEs300, "void main() {}\n", ["MODE 3"]);

        Assert.StartsWith("#version 330 core\n#define MODE 3\n", core, StringComparison.Ordinal);
        Assert.StartsWith("#version 300 es\nprecision highp float;\n", es, StringComparison.Ordinal);
        Assert.Contains("#define MODE 3\nvoid main() {}\n", es, StringComparison.Ordinal);
    }
}

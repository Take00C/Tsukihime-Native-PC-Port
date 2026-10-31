using Tsuki.Engine.Contracts;
using Tsuki.Render.Compositor;
using Tsuki.Render.Gl;
using Tsuki.Render.Shaders;
using Tsuki.Tests.Render.Smoke;

namespace Tsuki.Tests.Render.Compositor;

/// <summary>The compositor's tables, command builders and both GLSL dialects (compositing.md 3, 4.1, 5.1, 7).</summary>
[Collection(SdlCollection.Name)]
public sealed class CompositorUnitTests
{
    [Fact]
    public void All27ProgramsCompileAsGlsl330CoreAndAsGlslEs300()
    {
        using GlContext ctx = GlContext.CreateHidden();
        foreach (GlslDialect d in ShaderCompiler.AllDialects)
        {
            using var set = new CompositorProgramSet(ctx.Device.Gl, d);
            Assert.Equal(27, set.Count);
            Assert.All(CompositorShaders.AllModes, m => Assert.NotEqual(0u, set[m].Program.Handle));
        }

        Assert.Equal(27, CompositorShaders.AllModes.Count);
        Assert.Null(ctx.Device.TakeError());
    }

    [Theory]
    [InlineData(0x00, 0, false, 0, 0f, 1)]       // normal layer: basic shader, alpha over
    [InlineData(0x11, 17, false, 17, 0f, 1)]     // _STBR 17 -> ps_dodge5 (strength 0)
    [InlineData(0x111, 17, true, 17, 0f, 0)]     // _STBR(..,17,,1): flag turns blending off
    [InlineData(0x117, 23, false, 23, 0f, 1)]    // 23 is not in the flag set
    [InlineData(0x105, 5, true, 5, 0f, 0)]       // dodge with the flag: P2 = 0
    [InlineData(43, 43, false, 17, 1f, 1)]       // masked second pass of mode 17 (26 + 17): strength 1
    [InlineData(26, 26, false, 0, 0f, 6)]        // through dst alpha (rule wipe capture pass)
    [InlineData(56, 56, false, 0, 0f, 11)]       // alpha := max
    [InlineData(58, 58, false, 0, 0f, 4)]        // alpha := sa (rule mask)
    [InlineData(0x45, 0, false, 0, 0f, 1)]       // low byte >= 0x44 -> effect 0
    public void DrawModeWordsDecodeAsCompose(int word, int e, bool flag, int mode, float strength, int state)
    {
        DrawEffect d = DrawEffect.Decode(word);
        Assert.Equal(e, d.E);
        Assert.Equal(flag, d.Flag);
        Assert.Equal(mode, d.ShaderMode);
        Assert.Equal(strength, d.Strength);
        Assert.Equal(state, d.BlendState);
        Assert.Equal(mode is 5 or 7 or 9 or 10 && state == 0 ? 0f : 1f, d.P2);
    }

    [Fact]
    public void ShaderSelectionFollowsSelectShader()
    {
        Assert.Equal(CompositorShaders.ModeSolid, DrawEffect.SelectShader(false, 17, false));
        Assert.Equal(CompositorShaders.ModeTransition, DrawEffect.SelectShader(true, 0, true));
        Assert.Equal(CompositorShaders.ModeBasic, DrawEffect.SelectShader(true, 0, false));
        Assert.Equal(CompositorShaders.ModeBasic, DrawEffect.SelectShader(true, 3, true));
        Assert.Equal(17, DrawEffect.SelectShader(true, 17, true));
        Assert.Equal(1, BlendStates.Get(14).Enabled ? 1 : 0);              // ids >= 14 use record 1
        Assert.False(BlendStates.Get(0).Enabled);
    }

    [Fact]
    public void RuleParametersAndFadsNumbersMatchCompose()
    {
        // compose.rule_params(0.5) = (0.0, 1.0, 0.0); (0.9) -> a = 256 - 230 = 26.
        Assert.Equal(new TransitionParams(0f, 1f), TransitionParams.ForRule(0.5f));
        Assert.Equal(new TransitionParams(MathF.Max(-1f, (26 * 0.0078125f) - 1f), 26 * 0.0078125f), TransitionParams.ForRule(0.9f));
        Assert.Equal(new TransitionParams(1f, 2f), TransitionParams.ForRule(0f));       // a = 256: done
        Assert.Equal(127, Fads.CrossfadeAlphaByte(0.5f));                 // trunc(255 p): 127/255 at p = 0.5
        Assert.Equal(0, Fads.CrossfadeAlphaByte(-0.1f));
        Assert.Equal(-0.016666f, Fads.Step(1000), 6);
        Assert.Equal(-1f, Fads.Step(0));
    }

    [Fact]
    public void FadsCommandsBuildTheCapturePath()
    {
        var list = new DisplayList();
        list.Reset(0);
        FadsCommands.Begin(list, 0);
        Assert.Equal([DrawKind.FillQuad, DrawKind.Capture], list.Commands.ToArray().Select(c => c.Kind));
        Assert.Equal(11, list.Commands[0].BlendState);
        Assert.Equal(0, list.Commands[1].Texture.A);

        list.Reset(1);
        FadsCommands.Begin(list, 2);
        Assert.Equal([0, 15], list.Commands.ToArray().Where(c => c.Kind == DrawKind.Capture).Select(c => c.Texture.A));

        list.Reset(2);
        Assert.Null(FadsCommands.Frame(list, 1, 0.5f, null));
        Assert.Equal(0, list.Count);
        TextureRef rule = TextureRef.ForPictureTile(4, 28, 0);
        Assert.Equal(TransitionParams.ForRule(0.25f), FadsCommands.Frame(list, 2, 0.25f, rule));
        Assert.Equal([rule, TextureRef.ForCapture(15)], list.Commands.ToArray().Select(c => c.Texture));
        Assert.Equal([4, 6], list.Commands.ToArray().Select(c => (int)c.BlendState));

        list.Reset(3);
        Assert.Null(FadsCommands.Frame(list, 2, 0.5f, null));                // empty rule slot: cross-fade of surface 15
        Assert.Equal(TextureRef.ForCapture(15), list.Commands[0].Texture);
        Assert.Equal(127u, list.Commands[0].V0.Rgba >> 24);
        list.Reset(4);
        FadsCommands.Frame(list, 0, 0.5f, rule);                              // type 0 ignores the rule: surface 0
        Assert.Equal(TextureRef.ForCapture(0), list.Commands[0].Texture);
        Assert.Throws<NotSupportedException>(() => FadsCommands.CaptureSurfaceOf(18));
        Assert.Null(FadsCommands.CaptureSurfaceOf(49));
    }
}

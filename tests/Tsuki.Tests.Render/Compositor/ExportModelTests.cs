using Silk.NET.OpenGL;
using Tsuki.Engine.Contracts;
using Tsuki.Render.Compositor;
using Tsuki.Render.Gl;
using Tsuki.Render.Shaders;
using Tsuki.Tests.Render.Smoke;

namespace Tsuki.Tests.Render.Compositor;

/// <summary>
/// The fp16 round-toward-zero MRT0 export (compositing.md 2.6) is done by the compositor's shaders, not left to the
/// driver. A float texture of 65,536 decisive values (every fp16 value in [0, 1] and its float32 neighbours, the
/// level midpoints, NaN, infinities, out-of-range and subnormal values) is drawn through the basic program (S * 1)
/// into an RGBA32F target, where no driver applies a colour export conversion of its own: the floats read back must
/// equal <c>compose.export_mrt0</c> exactly. The same draw into the RGBA8 frame must give <c>compose.to_u8</c> of it.
/// Both GLSL dialects. Needs the local GPU and the Python oracle (.venv); never skips.
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class ExportModelTests(ITestOutputHelper output)
{
    private const int Side = 128;                       // 128 x 128 RGBA = 65,536 values

    [Fact]
    public void TheShaderExportIsFp16RoundTowardZeroOnAnyTarget()
    {
        float[] values = Values();
        using var dir = new TempFolder("export");
        File.WriteAllBytes(dir.File("in.f32"), Bytes(values));
        output.WriteLine(Oracle.Run(TimeSpan.FromMinutes(2), Path.Combine(Oracle.HelperDir, "oracle_tables.py"), "export", dir.FullName).Trim());
        float[] refF = Floats(File.ReadAllBytes(dir.File("export.f32")));
        byte[] refU8 = File.ReadAllBytes(dir.File("export.u8"));
        Assert.Equal(values.Length, refF.Length);
        Assert.Equal(values.Length, refU8.Length);

        // Live check: the float32 values clamped (compose "f32") differ from the export model on many values.
        int rounded = 0, decisiveU8 = 0;
        for (int i = 0; i < values.Length; i++)
        {
            float x = float.IsNaN(values[i]) ? 0f : Math.Clamp(values[i], 0f, 1f);
            rounded += x != refF[i] ? 1 : 0;
            decisiveU8 += (int)MathF.Round(x * 255f, MidpointRounding.ToEven) != refU8[i] ? 1 : 0;
        }

        output.WriteLine($"{values.Length} values; {rounded} change under the fp16 RTZ export; {decisiveU8} give another UNORM8 level than float32");
        Assert.True(rounded > 30000, $"only {rounded} values exercise the rounding");
        Assert.True(decisiveU8 > 1000, $"only {decisiveU8} values decide the UNORM8 level");

        using GlContext ctx = GlContext.CreateHidden();
        GL gl = ctx.Device.Gl;
        foreach (GlslDialect dialect in ShaderCompiler.AllDialects)
        {
            using var c = new GpuCompositor(gl, Side, Side, dialect) { Filter = SamplingFilter.Nearest };
            uint src = gl.GenTexture();
            try
            {
                gl.BindTexture(TextureTarget.Texture2D, src);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
                gl.TexImage2D<float>(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, Side, Side, 0, PixelFormat.Rgba, PixelType.Float, values);

                // Target 1 re-specified as RGBA32F: the float export reaches the target unconverted.
                RenderTarget f = c.Target(1);
                gl.BindTexture(TextureTarget.Texture2D, f.Texture);
                gl.TexImage2D<float>(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, Side, Side, 0, PixelFormat.Rgba, PixelType.Float, new float[values.Length]);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, f.Fbo);
                Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

                c.BeginFrame();
                Vertex[] quad =
                [
                    new(0, 0, 0, 0, uint.MaxValue), new(Side, 0, 1, 0, uint.MaxValue),
                    new(0, Side, 0, 1, uint.MaxValue), new(Side, Side, 1, 1, uint.MaxValue),
                ];
                c.DrawQuad(1, src, quad, DrawEffect.Normal, blendState: 0);   // PS #2: S * C with C = 1; blending off
                c.DrawQuad(0, src, quad, DrawEffect.Normal, blendState: 0);
                Assert.Equal(2, c.Counters.Draws);

                var gpuF = new float[values.Length];
                gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, f.Fbo);
                gl.ReadPixels<float>(0, 0, Side, Side, PixelFormat.Rgba, PixelType.Float, gpuF);
                byte[] gpuU8 = c.Read(c.Frame);
                Assert.Null(ctx.Device.TakeError());

                int badF = 0, badU8 = 0;
                var firstBad = new List<string>();
                for (int i = 0; i < values.Length; i++)
                {
                    if (!(gpuF[i] == refF[i]))            // NaN on the GPU side counts as a mismatch
                    {
                        badF++;
                        if (firstBad.Count < 8)
                        {
                            firstBad.Add(Report.Fmt($"x {values[i]:R} (0x{BitConverter.SingleToUInt32Bits(values[i]):X8}): GPU {gpuF[i]:R}, compose {refF[i]:R}"));
                        }
                    }

                    badU8 += gpuU8[i] != refU8[i] ? 1 : 0;
                }

                output.WriteLine($"{dialect}: RGBA32F target {values.Length - badF} of {values.Length} equal to export_mrt0; RGBA8 target {values.Length - badU8} equal to to_u8(export_mrt0)");
                Assert.True(badF == 0, $"{dialect}: {badF} float exports differ from compose.export_mrt0:\n" + string.Join("\n", firstBad));
                Assert.Equal(0, badU8);
            }
            finally
            {
                gl.DeleteTexture(src);
            }
        }
    }

    [Fact]
    public void EveryProgramWritesItsColourThroughTheExport()
    {
        // One assignment to the output, and it is the export of the mode's result.
        string body = CompositorShaders.FragmentBody;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"\bo_color\s*="));
        Assert.Contains("o_color = exportMrt0(r);", body, StringComparison.Ordinal);
    }

    private static float[] Values()
    {
        var v = new List<float>();
        foreach (double o in new[] { 0.5, 0.501, 0.51, 0.53, 0.56, 0.6, 0.62, 0.499, 0.49 })
        {
            for (int k = 0; k < 255; k++)
            {
                v.Add((float)((k + o) / 255.0));
            }
        }

        foreach (uint bits in new uint[] { 0x7FC00000, 0xFFC00000, 0x7F800001, 0x7FFFFFFF, 0x7F800000, 0xFF800000, 0x80000000, 0x00000001, 0x00800000, 0x807FFFFF })
        {
            v.Add(BitConverter.UInt32BitsToSingle(bits));     // NaNs, infinities, -0, float32 subnormals
        }

        v.AddRange([0f, -1e-3f, -1f, 1f, 1.0000001f, 1.5f, 2f, 65504f, 65520f, 1e10f, float.MaxValue, 1e-5f, 3e-8f, 6.1e-5f]);
        for (ushort h = 0; h <= 0x3C00; h++)
        {
            float x = (float)BitConverter.UInt16BitsToHalf(h);  // every fp16 value in [0, 1] and its float32 neighbours
            v.Add(x);
            v.Add(MathF.BitIncrement(x));
            if (x > 0)
            {
                v.Add(MathF.BitDecrement(x));
            }
        }

        var rng = new Random(20261025);
        while (v.Count < Side * Side * 4)
        {
            v.Add(v.Count % 2 == 0 ? (float)rng.NextDouble() : BitConverter.Int32BitsToSingle(rng.Next(int.MinValue, int.MaxValue)));
        }

        return [.. v.Take(Side * Side * 4)];
    }

    private static byte[] Bytes(float[] f)
    {
        var b = new byte[f.Length * 4];
        System.Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }

    private static float[] Floats(byte[] b)
    {
        var f = new float[b.Length / 4];
        System.Buffer.BlockCopy(b, 0, f, 0, b.Length);
        return f;
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using Tsuki.Data.Eboot;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;
using Tsuki.Render.Compositor;
using Tsuki.Render.Grade;
using Tsuki.Render.Textures;
using Tsuki.Tests.Render.Compositor;
using Tsuki.Tests.Render.Smoke;

namespace Tsuki.Tests.Render.Effects;

/// <summary>
/// The STEC / SNX2 / SNX3 recolour (compositing.md 9) over the PS4 evaluated LUTs: the kernel against the engine function 0xD36F0 under
/// emulation (336 frozen cases, <c>samples/misc_compositing_transitions_shaders/stec_golden.json</c>), and the texture cache building a graded texture
/// under its key (CPU and decode-worker paths, then drawn through the compositor).
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class GradeTests
{
    [GameDataFact]
    public void TheKernelEqualsTheEngineRecolourOnTheFrozenCases()
    {
        using var tmp = new TempFolder("stec");
        string pattern = tmp.File("pattern.bin");
        Oracle.Run(TimeSpan.FromSeconds(60), Path.Combine(Oracle.RepoRoot, "tests", "Tsuki.Tests.Render", "Effects", "stec_pattern.py"), pattern);
        byte[] bgra = File.ReadAllBytes(pattern);
        Assert.Equal(3072 * 4, bgra.Length);
        StecTables tables = StecTables.Read(EbootReader.Parse(File.ReadAllBytes(Path.Combine(GameFolder.Require(), "eboot.bin"))));
        Assert.Equal(69, Enumerable.Range(1, 81).Count(i => tables.Flags(i) == 0x100));
        Assert.Equal(11, Enumerable.Range(1, 81).Count(i => tables.Flags(i) == 0x200));
        Assert.Equal(0x400, tables.Flags(1));
        Assert.Equal((382.5f, 765.0f), (tables.LumaOffset, tables.LumaDivisor));

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Oracle.RepoRoot, "samples", "misc_compositing_transitions_shaders", "stec_golden.json")));
        JsonElement cases = doc.RootElement.GetProperty("cases");
        Assert.Equal(336, cases.GetArrayLength());
        foreach (JsonElement c in cases.EnumerateArray())
        {
            int id = c.GetProperty("id").GetInt32();
            int bright = c.GetProperty("bright").GetInt32();
            int contrast = c.GetProperty("contrast").GetInt32();
            JsonElement col = c.GetProperty("colour");
            int rgb = (col[0].GetInt32() << 16) | (col[1].GetInt32() << 8) | col[2].GetInt32();
            byte[] px = SwapRb(bgra);
            StecKernel.Apply(px, new ColourGrade(id, bright, contrast, rgb), tables);
            byte[] outBgra = SwapRb(px);
            Assert.Equal(c.GetProperty("sha1").GetString(), Convert.ToHexString(SHA1.HashData(outBgra)).ToLowerInvariant());
            int changed = 0;
            for (int p = 0; p < outBgra.Length; p += 4)
            {
                if (!outBgra.AsSpan(p, 4).SequenceEqual(bgra.AsSpan(p, 4)))
                {
                    changed++;
                }
            }

            Assert.Equal(c.GetProperty("changed").GetInt32(), changed);
        }
    }

    [Fact]
    public void TheKernelFollowsTheDocumentedModes()
    {
        StecTables t = Synthetic();
        byte[] px = [10, 100, 200, 77, 255, 0, 128, 5];
        byte[] a = [.. px];
        StecKernel.Apply(a, default, t);
        Assert.Equal(px, a);                                          // no grade, no change
        a = [.. px];
        StecKernel.Apply(a, new ColourGrade(0, 50, 0, 0), t);
        Assert.Equal(new byte[] { 60, 150, 250, 77, 255, 50, 178, 5 }, a);   // brightness: clamp(i + b), alpha untouched
        a = [.. px];
        StecKernel.Apply(a, new ColourGrade(2, 0, 0, 0), t);              // 0x100: blue inverted, green kept, red halved
        Assert.Equal(new byte[] { 5, 100, 55, 77, 127, 0, 127, 5 }, a);
        a = [.. px];
        StecKernel.Apply(a, new ColourGrade(1, 0, 0, 0x336699), t);       // 0x400: solid colour, alpha kept
        Assert.Equal(new byte[] { 0x33, 0x66, 0x99, 77, 0x33, 0x66, 0x99, 5 }, a);
        a = [255, 255, 255, 9];
        StecKernel.Apply(a, new ColourGrade(3, 0, 0, 0), t);              // 0x200: luma through identity LUTs
        Assert.Equal(new byte[] { 255, 255, 255, 9 }, a);
        a = [200, 100, 50, 9];
        StecKernel.Apply(a, new ColourGrade(3, 0, 0, 0), t);
        int luma = (int)((((50 * 161) + (100 * 0x224) + (200 * 56)) + 382.5f) / 765.0f);
        Assert.Equal(new byte[] { (byte)luma, (byte)luma, (byte)luma, 9 }, a);
        a = [.. px];
        StecKernel.Apply(a, new ColourGrade(82, 0, 0, 0), t);             // an id outside 1..81 recolours nothing
        Assert.Equal(px, a);
        Span<byte> lut = stackalloc byte[256];
        StecKernel.ContrastLut(40, lut);
        Assert.Equal((0, 0, 253, 255), (lut[39], lut[40], lut[214], lut[215]));
        StecKernel.ContrastLut(-40, lut);
        Assert.Equal((40, 215), (lut[0], lut[255]));
    }

    [Fact]
    public void AGradedTextureIsBuiltAndCachedUnderItsKey()
    {
        using GlContext ctx = GlContext.CreateHidden();
        StecTables tables = Synthetic();
        using var cache = new TextureCache(ctx.Device.Gl) { Grader = new TileGrader(tables) };
        byte[] pixels = Ramp(8, 4);
        int key = GradeKeys.Intern(new ColourGrade(2, 0, 0, 0));
        TextureRef plain = TextureRef.ForPictureTile(0, 5, 0);
        TextureRef graded = TextureRef.ForPictureTile(0, 5, 0, key);
        cache.Upload(plain, 8, 4, pixels);
        cache.Upload(graded, 8, 4, pixels);
        Assert.Equal(2, cache.Count);                            // the plain and the graded tile are two entries
        Assert.Equal(pixels, cache.ReadBack(plain));
        byte[] expected = [.. pixels];
        StecKernel.Apply(expected, GradeKeys.Resolve(key), tables);
        Assert.NotEqual(pixels, expected);
        Assert.Equal(expected, cache.ReadBack(graded));

        // the decode worker grades the tile on its own thread; the scheduler must not grade it a second time
        int key2 = GradeKeys.Intern(new ColourGrade(2, 30, 0, 0));
        TextureRef viaWorker = TextureRef.ForPictureTile(0, 6, 0, key2);
        var pool = new StagingPool(1L << 20);
        var sched = new UploadScheduler(cache, pool);
        using (var workers = new DecodeWorkers(sched, 1))
        {
            workers.Enqueue(new DecodeJob(viaWorker, 8, 4, rgba => pixels.CopyTo(rgba)));
            for (int i = 0; i < 2000 && sched.Pending == 0; i++)
            {
                Thread.Sleep(5);
            }
        }

        sched.UploadSlice();
        byte[] once = [.. pixels];
        StecKernel.Apply(once, GradeKeys.Resolve(key2), tables);
        Assert.Equal(once, cache.ReadBack(viaWorker));
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void AGradedQuadIsDrawnWithTheRecolouredTexels()
    {
        using GlContext ctx = GlContext.CreateHidden();
        StecTables tables = Synthetic();
        using var compositor = new GpuCompositor(ctx.Device.Gl, 64, 64) { Filter = SamplingFilter.Nearest };
        using var cache = new TextureCache(ctx.Device.Gl) { Grader = new TileGrader(tables) };
        var executor = new DisplayListExecutor(compositor, cache);
        int key = GradeKeys.Intern(new ColourGrade(2, 0, 0, 0));
        TextureRef graded = TextureRef.ForPictureTile(0, 7, 0, key);
        byte[] pixels = Enumerable.Range(0, 4 * 4 * 4).Select(i => (byte)(i % 4 == 3 ? 255 : 40 + (i * 7 % 200))).ToArray();
        cache.Upload(graded, 4, 4, pixels);
        var list = new DisplayList();
        list.Reset(0);
        list.Add(Quads.Textured(graded, 0, 0, 4, 4, 0, 0, 1, 1, Quads.White(), 0));
        compositor.Clear(0, 0, 0, 0, 255);
        executor.Execute(list);
        byte[] frame = compositor.Read(compositor.Frame);
        byte[] expected = [.. pixels];
        StecKernel.Apply(expected, GradeKeys.Resolve(key), tables);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int got = frame[(((y * 64) + x) * 4) + c];
                    int want = expected[(((y * 4) + x) * 4) + c];
                    Assert.True(Math.Abs(got - want) <= 1, $"pixel ({x},{y}) channel {c}: {got} vs {want}");
                }
            }
        }
    }

    private static byte[] SwapRb(byte[] src)
    {
        byte[] dst = [.. src];
        for (int p = 0; p < dst.Length; p += 4)
        {
            (dst[p], dst[p + 2]) = (dst[p + 2], dst[p]);
        }

        return dst;
    }

    private static byte[] Ramp(int w, int h)
    {
        byte[] b = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            b[4 * i] = (byte)(i * 8);
            b[(4 * i) + 1] = (byte)(255 - (i * 5));
            b[(4 * i) + 2] = (byte)(i * 3);
            b[(4 * i) + 3] = (byte)(100 + i);
        }

        return b;
    }

    // mode 1: solid colour (0x400), mode 2: per-channel LUTs (0x100: blue inverted, green kept, red halved), mode 3: luma (0x200, identity LUTs)
    private static StecTables Synthetic()
    {
        int[] flags = new int[StecTables.Count];
        int[] wb = new int[StecTables.Count];
        int[] wr = new int[StecTables.Count];
        byte[] luts = new byte[StecTables.Count * 3 * 256];
        flags[0] = 0x400;
        flags[1] = 0x100;
        flags[2] = 0x200;
        for (int i = 0; i < 256; i++)
        {
            luts[((1 * 3) + 0) * 256 + i] = (byte)(255 - i);          // byte 0 (blue)
            luts[((1 * 3) + 1) * 256 + i] = (byte)i;                  // byte 1 (green)
            luts[((1 * 3) + 2) * 256 + i] = (byte)(i / 2);            // byte 2 (red)
            for (int c = 0; c < 3; c++)
            {
                luts[(((2 * 3) + c) * 256) + i] = (byte)i;
            }
        }

        wb[2] = 161;
        wr[2] = 56;
        return new StecTables(flags, wb, wr, luts);
    }
}

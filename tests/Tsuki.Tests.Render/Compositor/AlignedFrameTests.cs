using System.Text.Json;
using Tsuki.Engine.Contracts;
using Tsuki.Render.Compositor;
using Tsuki.Tests.Render.Smoke;

namespace Tsuki.Tests.Render.Compositor;

/// <summary>
/// T5 (b) on real pictures (PACE 7.2 #5, D1; S3.md 4.3): the aligned 1920 x 1080 check frame (old frame, transition
/// start, 13 montage layers in modes 0, 24, 17, 11, 19, 23, 22, 13 at integer offsets, then the RULE_29 rule wipe at
/// p = 0.5) through the GPU executor, against <c>tsuki.compose.execute_list</c> with the fp16 RTZ export model. At
/// least 99.9 % of the pixels must be within 2 levels; quad-bounds and full-target backdrop copies must give the
/// same frame. The pictures are decoded by the Python oracle into a temporary folder that is deleted afterwards.
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class AlignedFrameTests(ITestOutputHelper output)
{
    private const int W = 1920, H = 1080;

    [GameDataFact]
    public void TheAlignedCheckFrameIsWithinTwoLevelsOnAtLeast999Permille()
    {
        GameFolder.Require();
        using var dir = new TempFolder("aligned");
        output.WriteLine(Oracle.Run(TimeSpan.FromMinutes(5), Path.Combine(Oracle.HelperDir, "export_pictures.py"), dir.FullName).Trim());
        AlignedSuite suite = BuildCheckFrame(dir);

        byte[] reference = suite.ComposeReference(dir, TimeSpan.FromMinutes(5), out string log);
        output.WriteLine(log);
        suite.Export = "f32";
        byte[] referenceF32 = suite.ComposeReference(dir, TimeSpan.FromMinutes(5), out log);
        output.WriteLine(log);

        byte[] bounds, full;
        int blitsBounds, blitsFull;
        long pxBounds, pxFull;
        using (GlContext ctx = GlContext.CreateHidden())
        using (var c = new GpuCompositor(ctx.Device.Gl, W, H) { Filter = SamplingFilter.Nearest })
        {
            c.Blit = BackdropBlit.Bounds;
            bounds = suite.RenderGpu(c);
            (blitsBounds, pxBounds) = (c.Counters.Blits, c.Counters.BlitPixels);
            c.Blit = BackdropBlit.Full;
            full = suite.RenderGpu(c);
            (blitsFull, pxFull) = (c.Counters.Blits, c.Counters.BlitPixels);
            Assert.Null(ctx.Device.TakeError());
        }

        LevelStats rtz = LevelStats.Compare(bounds, reference);
        LevelStats f32 = LevelStats.Compare(bounds, referenceF32);
        LevelStats fullVsBounds = LevelStats.Compare(full, bounds);
        output.WriteLine($"GPU (bounds blits) vs compose rtz16: {rtz}");
        output.WriteLine($"GPU (bounds blits) vs compose f32:   {f32}");
        output.WriteLine($"full vs bounds blits: {fullVsBounds}; last segment blits {blitsBounds} ({pxBounds / 1e6:F2} Mpx) vs {blitsFull} ({pxFull / 1e6:F2} Mpx)");

        Assert.Equal(0, fullVsBounds.Max);
        Assert.True(rtz.Le2Share >= 0.999, $"within 2 levels: {rtz}");
        Assert.True(rtz.Le2Share > f32.Le2Share, $"rtz16 {rtz} vs f32 {f32}");
    }

    private static AlignedSuite BuildCheckFrame(TempFolder dir)
    {
        var suite = new AlignedSuite(W, H);
        using var man = JsonDocument.Parse(File.ReadAllBytes(dir.File("manifest.json")));
        var anchors = new Dictionary<string, (int X, int Y)>();
        foreach (JsonProperty p in man.RootElement.GetProperty("pictures").EnumerateObject())
        {
            int w = p.Value.GetProperty("width").GetInt32(), h = p.Value.GetProperty("height").GetInt32();
            suite.AddTexture(p.Name, w, h, File.ReadAllBytes(dir.File(p.Value.GetProperty("file").GetString()!)));
            JsonElement a = p.Value.GetProperty("anchor");
            anchors[p.Name] = (a[0].GetInt32(), a[1].GetInt32());
        }

        DrawCmd Layer(string name, int mode, byte alpha = 255, int dx = 0, int dy = 0)
        {
            TestTexture t = suite[name];
            int x = (W / 2) - anchors[name].X + dx, y = (H / 2) - anchors[name].Y + dy;
            return Quads.Textured(t.Ref, x, y, x + t.Width, y + t.Height, 0, 0, 1, 1, Quads.White(alpha), mode);
        }

        AlignedCase k = suite.NewCase("S3 check frame", "aligned").Clear();
        k.Add(Layer("img0597", 0));
        var start = new DisplayList();
        start.Reset(0);
        FadsCommands.Begin(start, 2, W, H);
        k.AddAll(start).Clear();
        foreach (DrawCmd c in new[]
        {
            Layer("img2345", 0), Layer("img2182", 24), Layer("img2183", 17, 200), Layer("img2230", 11),
            Layer("img0813", 17), Layer("img2439", 19), Layer("img0758", 19, 255, 300, 120), Layer("img1685", 23, 160),
            Layer("img0597", 24), Layer("img0784", 22), Layer("img2163", 13, 255, 0, 400), Layer("img2232", 11, 128),
            Layer("img0700_06_06", 17, 255, -200, 0),
        })
        {
            k.Add(c);
        }

        var frame = new DisplayList();
        frame.Reset(1);
        TextureRef rule = suite["rule29"].Ref;
        TransitionParams? tp = FadsCommands.Frame(frame, 2, 0.5f, rule, W, H);
        k.AddAll(frame);
        k.Transition[rule] = tp ?? throw new InvalidOperationException("no rule pass");
        return suite;
    }
}

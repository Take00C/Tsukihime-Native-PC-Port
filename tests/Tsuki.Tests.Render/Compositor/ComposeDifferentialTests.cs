using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Render.Compositor;
using Tsuki.Tests.Render.Smoke;

namespace Tsuki.Tests.Render.Compositor;

/// <summary>
/// T5 (b) on synthetic lists (PACE 7.2 #5, D1): the GPU display-list executor against
/// <c>tsuki.compose.execute_list</c> with the fp16 round-toward-zero export model (compositing.md 2.6), on a
/// 256 x 256 test pattern: every _STBR mode as ordinary, flagged and masked layer, the named effects 52..67, all 14
/// blend states, fills, the rule mask, the two-pass rule wipe, the cross-fade, the _FADS transition start
/// built by <see cref="FadsCommands"/>, scissor clips and reads of a capture surface that was never captured.
/// Every list must be within 1 level everywhere. Only the "state" group passes its blend state to compose; every
/// other quad's state is decoded from the word by compose itself, independently of <see cref="DrawEffect.Decode"/>.
/// Needs the local GPU and the Python oracle (.venv); never skips.
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class ComposeDifferentialTests(ITestOutputHelper output)
{
    private const int Size = 256;

    private static readonly byte[][] Colours =
    [
        [255, 255, 255, 255],
        [255, 255, 255, 144],
        [128, 192, 64, 200],
        [200, 100, 255, 1],
    ];

    private static readonly float[] Progress = [1.0f, 0.9f, 0.75f, 0.6f, 0.5f, 0.3f, 0.25f, 0.1f, 0.02f];

    [Fact]
    public void EveryModeStateAndTransitionIsWithinOneLevelOfCompose()
    {
        AlignedSuite suite = BuildSuite();
        using var dir = new TempFolder("synthetic");
        byte[] reference = suite.ComposeReference(dir, TimeSpan.FromMinutes(5), out string log);
        output.WriteLine(log);

        byte[] gpu;
        using (GlContext ctx = GlContext.CreateHidden())
        using (var c = new GpuCompositor(ctx.Device.Gl, Size, Size) { Filter = SamplingFilter.Nearest })
        {
            gpu = suite.RenderGpu(c);
            Assert.Null(ctx.Device.TakeError());
        }

        int frame = Size * Size * 4;
        var byGroup = new SortedDictionary<string, (int Cases, LevelStats Stats)>(StringComparer.Ordinal);
        var failures = new List<string>();
        for (int i = 0; i < suite.Cases.Count; i++)
        {
            AlignedCase k = suite.Cases[i];
            LevelStats s = LevelStats.Compare(gpu.AsSpan(i * frame, frame), reference.AsSpan(i * frame, frame));
            byGroup[k.Group] = byGroup.TryGetValue(k.Group, out var g) ? (g.Cases + 1, g.Stats.Merge(s)) : (1, s);
            if (s.Max > 1)
            {
                failures.Add($"{k.Id}: {s}");
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{suite.Cases.Count} lists, export {suite.Export}");
        LevelStats all = default;
        foreach (var (group, (cases, stats)) in byGroup)
        {
            sb.AppendLine(Report.Fmt($"{group,-28} {cases,4} lists: {stats}"));
            all = all.Merge(stats);
        }

        sb.AppendLine(Report.Fmt($"{"all",-28} {suite.Cases.Count,4} lists: {all}"));
        output.WriteLine(sb.ToString());
        Assert.True(failures.Count == 0, $"{failures.Count} lists differ by more than 1 level:\n" + string.Join("\n", failures.Take(40)));
        Assert.Equal(1, all.Max);                       // the comparison is live: 1-level rounding ties remain
        Assert.Contains("clip", byGroup.Keys);
        Assert.Contains("uncaptured surface", byGroup.Keys);
    }

    [Fact]
    public void TheF32ModelIsMeasurablyWorseThanTheExportModel()
    {
        // The same blend-state cases against compose without the export model: the GPU follows the fp16 RTZ export,
        // so the f32 model has fewer exact pixels (S3.md 4.2). This keeps the export model in compose honest.
        AlignedSuite rtz = BuildSuite(onlyGroup: "state");
        AlignedSuite f32 = BuildSuite(onlyGroup: "state");
        f32.Export = "f32";
        using var d1 = new TempFolder("rtz");
        using var d2 = new TempFolder("f32");
        byte[] refRtz = rtz.ComposeReference(d1, TimeSpan.FromMinutes(2), out _);
        byte[] refF32 = f32.ComposeReference(d2, TimeSpan.FromMinutes(2), out _);
        byte[] gpu;
        using (GlContext ctx = GlContext.CreateHidden())
        using (var c = new GpuCompositor(ctx.Device.Gl, Size, Size) { Filter = SamplingFilter.Nearest })
        {
            gpu = rtz.RenderGpu(c);
        }

        LevelStats a = LevelStats.Compare(gpu, refRtz), b = LevelStats.Compare(gpu, refF32);
        output.WriteLine($"blend states 0..13, rtz16: {a}");
        output.WriteLine($"blend states 0..13, f32:   {b}");
        Assert.True(a.Exact > b.Exact, $"rtz16 {a} vs f32 {b}");
        Assert.True(a.Max <= 1, a.ToString());
    }

    /// <summary>The synthetic suite (the S3 case set, rebuilt as display lists).</summary>
    internal static AlignedSuite BuildSuite(string? onlyGroup = null)
    {
        var suite = new AlignedSuite(Size, Size);
        (byte[] s, byte[] d, byte[] r) = Pattern(20261025);
        byte[] old = (byte[])s.Clone();
        for (int i = 3; i < old.Length; i += 4)
        {
            old[i] = 255;                                // the captured old frame: alpha 1 after the effect-56 fill
        }

        TextureRef S = suite.AddTexture("S", Size, Size, s).Ref;
        suite.AddTexture("D", Size, Size, d);
        TextureRef R = suite.AddTexture("R", Size, Size, r).Ref;
        suite.AddTexture("OLD", Size, Size, old);
        bool want(string g) => onlyGroup is null || onlyGroup == g;

        if (want("stbr"))
        {
            for (int mode = 0; mode < 26; mode++)
            {
                int[] words = [mode, 0x100 | mode, mode is >= 1 and <= 25 ? 26 + mode : 26];
                foreach (int w in words.Distinct())
                {
                    foreach (byte[] c in Colours)
                    {
                        suite.NewCase($"word 0x{w:X3} colour {string.Join(',', c)}", "stbr" + (w >= 26 && w < 0x100 ? " masked" : w >= 0x100 ? " flagged" : ""))
                            .Load("D").Add(Quad(S, w, c));
                    }
                }
            }
        }

        if (want("effects 52..67"))
        {
            for (int w = 52; w < 68; w++)
            {
                foreach (byte[] c in Colours[..3])
                {
                    suite.NewCase($"effect {w} colour {string.Join(',', c)}", "effects 52..67").Load("D").Add(Quad(S, w, c));
                }
            }
        }

        if (want("state"))
        {
            for (int st = 0; st < 14; st++)
            {
                foreach (byte[] c in Colours[..3])
                {
                    suite.NewCase($"state {st} colour {string.Join(',', c)}", "state").Load("D").Add(Quad(S, 0, c, st), stateOverride: true);
                }
            }
        }

        if (want("fills"))
        {
            foreach (int w in new[] { 0, 1, 17, 26, 52, 53, 54, 55, 56, 57, 58, 59, 63 })
            {
                foreach (byte[] c in Colours[..3])
                {
                    suite.NewCase($"fill word {w} colour {string.Join(',', c)}", "fills").Load("D")
                        .Add(Quads.Fill(0, 0, Size, Size, Quads.Rgba(c[0], c[1], c[2], c[3]), w));
                }
            }
        }

        if (want("culled"))
        {
            suite.NewCase("mode 5 at vertex alpha 0 (culled)", "culled").Load("D").Add(Quad(S, 5, [255, 255, 255, 0]));
            suite.NewCase("fill 58 at vertex alpha 0 (culled)", "culled").Load("D").Add(Quads.Fill(0, 0, Size, Size, Quads.Rgba(9, 9, 9, 0), 58));
        }

        if (want("rule"))
        {
            foreach (int vague in new[] { 256, 64 })
            {
                foreach (float p in Progress)
                {
                    AlignedCase mask = suite.NewCase($"rule mask p {p} vague {vague}", "rule mask (PS #0, state 4)").Load("D")
                        .Add(Quads.Textured(R, 0, 0, Size, Size, 0, 0, 1, 1, Quads.White(), DrawEffect.AlphaSet));
                    mask.Transition[R] = TransitionParams.ForRule(p, vague);

                    AlignedCase wipe = suite.NewCase($"rule wipe p {p} vague {vague}", "rule wipe (2 passes)")
                        .Load("OLD").Add(Quads.Capture(15)).Load("D")
                        .Add(Quads.Textured(R, 0, 0, Size, Size, 0, 0, 1, 1, Quads.White(), DrawEffect.AlphaSet))
                        .Add(Quads.Textured(TextureRef.ForCapture(15), 0, 0, Size, Size, 0, 0, 1, 1, Quads.White(), DrawEffect.ThroughDstAlpha));
                    wipe.Transition[R] = TransitionParams.ForRule(p, vague);
                }
            }

            foreach (float p in Progress)
            {
                suite.NewCase($"cross-fade p {p}", "cross-fade").Load("OLD").Add(Quads.Capture(0)).Load("D")
                    .Add(Quads.Textured(TextureRef.ForCapture(0), 0, 0, Size, Size, 0, 0, 1, 1, Quads.White(Fads.CrossfadeAlphaByte(p)), DrawEffect.Normal));
            }
        }

        if (want("clip"))
        {
            // The scissor path of GpuCompositor.DrawQuad: compose draws only the pixels inside the clip rectangle.
            float o = 16f / Size, e = 144f / Size;
            (string Id, DrawCmd Cmd)[] clipped =
            [
                ("word 0 clip inside", Quad(S, 0, Colours[2], clip: new PixelRect(40, 30, 100, 60))),
                ("dodge5 clip over the left and bottom edge", Quad(S, 17, Colours[0], clip: new PixelRect(-20, 200, 120, 100))),
                ("masked overlay 48 clip one row", Quad(S, 48, Colours[1], clip: new PixelRect(0, 77, 256, 1))),
                ("offset quad, ps_screen, clip across it", Quads.Textured(S, 16, 16, 144, 144, o, o, e, e, Quads.Rgba(128, 192, 64, 200), 23, clip: new PixelRect(0, 50, 256, 20))),
                ("fill 56 clip right half", Quads.Fill(0, 0, Size, Size, Quads.Rgba(9, 99, 199, 77), DrawEffect.AlphaUnion, clip: new PixelRect(128, 0, 128, 256))),
                ("fill 0 clip of width 0 (nothing drawn)", Quads.Fill(0, 0, Size, Size, Quads.White(), 0, clip: new PixelRect(10, 10, 0, 50))),
                ("word 8 clip outside the target (nothing drawn)", Quad(S, 8, Colours[0], clip: new PixelRect(300, 10, 20, 20))),
            ];
            foreach ((string id, DrawCmd cmd) in clipped)
            {
                suite.NewCase(id, "clip").Load("D").Add(cmd);
            }

            AlignedCase mask = suite.NewCase("rule mask p 0.5 clip", "clip").Load("D")
                .Add(Quads.Textured(R, 0, 0, Size, Size, 0, 0, 1, 1, Quads.White(), DrawEffect.AlphaSet, clip: new PixelRect(64, 64, 128, 128)));
            mask.Transition[R] = TransitionParams.ForRule(0.5f);
        }

        if (want("uncaptured"))
        {
            // Surface 9 is captured by no case: the GPU reads its zero-cleared surface, compose its zeros.
            TextureRef nine = TextureRef.ForCapture(9);
            suite.NewCase("capture 9 never captured, opaque mode 8", "uncaptured surface").Load("D")
                .Add(Quads.Textured(nine, 0, 0, Size, Size, 0, 0, 1, 1, Quads.Rgba(255, 255, 255, 200), 8));
            suite.NewCase("capture 9 never captured, state 0 copy", "uncaptured surface").Load("D")
                .Add(Quads.Textured(nine, 32, 32, 224, 224, 0.125f, 0.125f, 0.875f, 0.875f, Quads.White(), 0, blendState: 0), stateOverride: true);
        }

        if (want("fads"))
        {
            // The whole capture path as FadsCommands builds it: old scene, transition start, new scene, one frame.
            foreach (int kind in new[] { 0, 1, 2 })
            {
                foreach (float p in new[] { 0.9f, 0.5f, 0.1f })
                {
                    foreach (bool withRule in kind == 2 ? new[] { true, false } : [false])
                    {
                        var list = new DisplayList();
                        list.Reset(0);
                        FadsCommands.Begin(list, kind, Size, Size);
                        var after = new DisplayList();
                        after.Reset(0);
                        after.Add(Quad(S, 23, Colours[2]));
                        TransitionParams? tp = FadsCommands.Frame(after, kind, p, withRule ? R : null, Size, Size);
                        AlignedCase k = suite.NewCase($"_FADS({kind}) p {p}{(withRule ? " rule R" : kind == 2 ? " empty rule slot" : "")}", "_FADS 0/1/2 capture path")
                            .Load("S").AddAll(list).Clear().Load("D");
                        k.AddAll(after);
                        if (tp is { } t)
                        {
                            k.Transition[R] = t;
                        }
                    }
                }
            }
        }

        return suite;
    }

    private static DrawCmd Quad(TextureRef tex, int word, byte[] c, int? state = null, PixelRect? clip = null) =>
        Quads.Textured(tex, 0, 0, Size, Size, 0, 0, 1, 1, Quads.Rgba(c[0], c[1], c[2], c[3]), word, state, clip: clip);

    /// <summary>
    /// S (layer), D (backdrop) and R (rule), 256 x 256 RGBA8 (the S3 pattern): rows 0..127 a systematic grid (every s
    /// level against every second d level), rows 128..255 random with about 6 % of the channels snapped to the edge
    /// values 0, 1, 2, 3, 4, 127, 128, 254, 255.
    /// </summary>
    internal static (byte[] S, byte[] D, byte[] R) Pattern(int seed)
    {
        var rng = new Random(seed);
        byte[] edges = [0, 1, 2, 3, 4, 127, 128, 254, 255];
        byte[] Random4()
        {
            var a = new byte[Size * Size * 4];
            rng.NextBytes(a);
            for (int i = 0; i < a.Length; i++)
            {
                if (rng.NextDouble() < 0.06)
                {
                    a[i] = edges[rng.Next(edges.Length)];
                }
            }

            return a;
        }

        byte[] s = Random4(), d = Random4(), r = Random4();
        for (int y = 0; y < 128; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int o = ((y * Size) + x) * 4;
                s[o] = (byte)x;
                s[o + 1] = (byte)(255 - x);
                s[o + 2] = (byte)x;
                s[o + 3] = (byte)((x * 7) + (y * 3));
                d[o] = (byte)(2 * y);
                d[o + 1] = (byte)((2 * y) + 1);
                d[o + 2] = (byte)(255 - (2 * y));
                d[o + 3] = (byte)((x * 5) + (y * 11));
                r[o] = r[o + 1] = r[o + 2] = (byte)(x + y);
            }
        }

        for (int y = 128; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int o = ((y * Size) + x) * 4;
                double u = rng.NextDouble();
                s[o + 3] = u < 0.25 ? (byte)255 : u < 0.35 ? (byte)0 : s[o + 3];
            }
        }

        for (int i = 3; i < r.Length; i += 4)
        {
            r[i] = 255;
        }

        return (s, d, r);
    }
}

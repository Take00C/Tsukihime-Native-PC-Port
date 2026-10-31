// Spike S3 verification renders (compared with tools/tsuki/compose.py by tools/verify_s3.py):
//  1. the test-pattern cases of samples/spikes/s3/pattern/cases.json (every _STBR mode as ordinary,
//     flagged and masked layer, the named effects 52..67, all 14 blend states, the rule mask, the
//     two-pass rule wipe and the cross-fade), each read back from a 256 x 256 RGBA8 target;
//  2. the aligned check frame of samples/spikes/s3/check/scene.json, with full-target and
//     quad-bounds backdrop blits;
//  3. a compile check of every shader as GLSL ES 3.00 (ARCHITECTURE 6.5).
using System.Diagnostics;
using System.Text.Json;
using Silk.NET.OpenGL;

namespace Tsuki.Spikes.S3;

internal static class Verify
{
    public static int Run(string dataDir)
    {
        using var win = GlWindow.Create(640, 360, hidden: true, swapInterval: 0);
        GL gl = win.Gl;
        Console.WriteLine($"[verify] {win.Info}");
        var es = CompileAll(gl, Shaders.EsHeader);
        var core = CompileAll(gl, Shaders.GlHeader);
        Console.WriteLine($"[verify] GLSL 3.30 core: {core.Count(x => x.Ok)}/{core.Count} programs link; GLSL ES 3.00: {es.Count(x => x.Ok)}/{es.Count}");
        foreach (var x in es.Where(x => !x.Ok))
            Console.WriteLine($"[verify]   ES mode {x.Mode}: {x.Log}");

        long t0 = Stopwatch.GetTimestamp();
        int ncases = RenderCases(gl, dataDir);
        long t1 = Stopwatch.GetTimestamp();
        Console.WriteLine($"[verify] pattern: {ncases} cases rendered in {Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds:F0} ms");

        using (var c = new Compositor(gl, 1920, 1080) { Nearest = true })
        {
            var pics = PictureSet.Load(c, dataDir, keepPixels: false);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dataDir, "check", "scene.json")));
            foreach (var mode in new[] { BlitMode.Full, BlitMode.Bounds })
            {
                c.Blit = mode;
                RenderCheck(c, pics, doc.RootElement, out int blits, out long px);
                byte[] frame = c.Read(c.Target);
                string f = Path.Combine(dataDir, "check", $"gpu_{mode.ToString().ToLowerInvariant()}.rgba");
                File.WriteAllBytes(f, frame);
                Console.WriteLine($"[verify] check frame ({mode} blits: {blits}, {px / 1e6:F2} Mpx) -> {f}");
            }
        }
        var err = gl.GetError();
        var meta = new
        {
            gl = win.Info,
            gl_error_at_end = err.ToString(),
            glsl_core = core.Select(x => new { mode = x.Mode, ok = x.Ok, log = x.Log }).ToArray(),
            glsl_es = es.Select(x => new { mode = x.Mode, ok = x.Ok, log = x.Log }).ToArray(),
            cases = ncases,
            date_utc = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(Path.Combine(dataDir, "verify", "gpu_meta.json"), JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[verify] GL error at end: {err}");
        return err == GLEnum.NoError && es.All(x => x.Ok) && core.All(x => x.Ok) ? 0 : 1;
    }

    private static List<(int Mode, bool Ok, string Log)> CompileAll(GL gl, string header)
    {
        var r = new List<(int, bool, string)>();
        foreach (int m in Shaders.AllModes())
        {
            uint p = Shaders.Link(gl, header, m, out string log);
            r.Add((m, p != 0, log.Trim()));
            if (p != 0)
                gl.DeleteProgram(p);
        }
        return r;
    }

    private static int RenderCases(GL gl, string dataDir)
    {
        string dir = Path.Combine(dataDir, "pattern");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "cases.json")));
        var root = doc.RootElement;
        int w = root.GetProperty("width").GetInt32(), h = root.GetProperty("height").GetInt32();
        byte[] S = File.ReadAllBytes(Path.Combine(dir, "S.rgba"));
        byte[] D = File.ReadAllBytes(Path.Combine(dir, "D.rgba"));
        byte[] R = File.ReadAllBytes(Path.Combine(dir, "R.rgba"));
        byte[] old = (byte[])S.Clone();                  // the captured old frame: alpha forced to 1
        for (int i = 3; i < old.Length; i += 4)
            old[i] = 255;
        using var c = new Compositor(gl, w, h) { Nearest = true, Blit = BlitMode.Full };
        uint texS = c.CreateTexture(w, h, S), texR = c.CreateTexture(w, h, R);
        string outDir = Path.Combine(dataDir, "verify");
        Directory.CreateDirectory(outDir);
        using var fs = File.Create(Path.Combine(outDir, "gpu_cases.rgba"));
        int n = 0;
        foreach (var cs in root.GetProperty("cases").EnumerateArray())
        {
            var col = cs.GetProperty("color");
            float r = col[0].GetInt32() / 255f, g = col[1].GetInt32() / 255f, b = col[2].GetInt32() / 255f, a = col[3].GetInt32() / 255f;
            var quad = Compositor.Rect(0, 0, w, h, 0, 0, 1, 1, r, g, b, a);
            c.BeginFrame();
            c.Upload(c.Target, D);
            switch (cs.GetProperty("kind").GetString())
            {
                case "draw":
                    c.DrawQuad(texS, quad, cs.GetProperty("word").GetInt32());
                    break;
                case "state":
                    c.DrawQuad(texS, quad, 0, stateOverride: cs.GetProperty("state").GetInt32());
                    break;
                case "rulemask":
                    c.DrawQuad(texR, quad, 58, Effects.RuleParams((float)cs.GetProperty("progress").GetDouble(), cs.GetProperty("vague").GetInt32()));
                    break;
                case "rulewipe":
                    c.Upload(c.Capture, old);
                    c.DrawRuleWipe(texR, (float)cs.GetProperty("progress").GetDouble(), cs.GetProperty("vague").GetInt32());
                    break;
                case "crossfade":
                    c.Upload(c.Capture, old);
                    c.DrawCrossfade((float)cs.GetProperty("progress").GetDouble());
                    break;
                default:
                    throw new InvalidDataException("unknown case kind " + cs.GetProperty("kind").GetString());
            }
            fs.Write(c.Read(c.Target));
            n++;
        }
        c.DeleteTexture(texS);
        c.DeleteTexture(texR);
        return n;
    }

    private static void DrawLayers(Compositor c, PictureSet pics, JsonElement layers)
    {
        foreach (var l in layers.EnumerateArray())
        {
            Picture p = pics[l.GetProperty("picture").GetString()!];
            int x = l.GetProperty("x").GetInt32(), y = l.GetProperty("y").GetInt32();
            var col = l.GetProperty("color");
            c.DrawQuad(p.Texture, Compositor.Rect(x, y, x + p.Width, y + p.Height, 0, 0, 1, 1,
                col[0].GetInt32() / 255f, col[1].GetInt32() / 255f, col[2].GetInt32() / 255f, col[3].GetInt32() / 255f),
                l.GetProperty("word").GetInt32());
        }
    }

    private static void RenderCheck(Compositor c, PictureSet pics, JsonElement scene, out int blits, out long blitPixels)
    {
        c.BeginFrame();
        c.Clear(0, 0, 0, 0);
        DrawLayers(c, pics, scene.GetProperty("old"));
        c.BeginTransition();
        c.Clear(0, 0, 0, 0);
        c.BeginFrame();
        DrawLayers(c, pics, scene.GetProperty("new"));
        blits = c.BlitCount;
        blitPixels = c.BlitPixels;
        var tr = scene.GetProperty("transition");
        c.DrawRuleWipe(pics[tr.GetProperty("rule").GetString()!].Texture, (float)tr.GetProperty("progress").GetDouble(), tr.GetProperty("vague").GetInt32());
    }
}

using System.Globalization;
using System.Text.Json;
using Tsuki.Engine.Contracts;
using Tsuki.Render.Compositor;

namespace Tsuki.Tests.Render.Compositor;

/// <summary>A source texture of an aligned test list: RGBA8 rows top-down.</summary>
internal sealed record TestTexture(string Name, TextureRef Ref, int Width, int Height, byte[] Rgba);

/// <summary>One step of an aligned test list.</summary>
internal abstract record CaseOp;

/// <summary>Clear the frame (test setup; no display-list record).</summary>
internal sealed record ClearOp(byte R, byte G, byte B, byte A) : CaseOp;

/// <summary>Overwrite the frame with a texture of the frame size (test setup).</summary>
internal sealed record LoadOp(string Texture) : CaseOp;

/// <summary>
/// A display-list command. <paramref name="StateOverride"/>: the command's blend state is set on purpose instead of
/// resolved from its word, and lists.json carries it; otherwise compose decodes the word itself, so a wrong
/// <see cref="DrawEffect.Decode"/> blend state in the C# builders shows up as a GPU-vs-compose difference.
/// </summary>
internal sealed record CmdOp(DrawCmd Cmd, bool StateOverride = false) : CaseOp;

/// <summary>One list: the ops and the transition parameters its textures carry.</summary>
internal sealed class AlignedCase(string id, string group)
{
    public string Id { get; } = id;

    public string Group { get; } = group;

    public List<CaseOp> Ops { get; } = [];

    public Dictionary<TextureRef, TransitionParams> Transition { get; } = [];

    public AlignedCase Load(string texture)
    {
        Ops.Add(new LoadOp(texture));
        return this;
    }

    public AlignedCase Clear(byte r = 0, byte g = 0, byte b = 0, byte a = 0)
    {
        Ops.Add(new ClearOp(r, g, b, a));
        return this;
    }

    public AlignedCase Add(in DrawCmd cmd, bool stateOverride = false)
    {
        Ops.Add(new CmdOp(cmd, stateOverride));
        return this;
    }

    public AlignedCase AddAll(DisplayList list)
    {
        foreach (DrawCmd c in list.Commands)
        {
            Ops.Add(new CmdOp(c));
        }

        return this;
    }
}

/// <summary>
/// Aligned display lists run twice: by <c>tsuki.compose.execute_list</c> (Python, with the fp16 RTZ export model)
/// and by the GPU executor (<see cref="DisplayListExecutor"/>), then compared per pixel in UNORM8 levels.
/// </summary>
internal sealed class AlignedSuite(int width, int height)
{
    private readonly Dictionary<string, TestTexture> _textures = [];
    private readonly Dictionary<TextureRef, TestTexture> _byRef = [];

    public int Width { get; } = width;

    public int Height { get; } = height;

    public List<AlignedCase> Cases { get; } = [];

    public string Export { get; set; } = "rtz16";

    public IReadOnlyDictionary<string, TestTexture> Textures => _textures;

    public TestTexture AddTexture(string name, int w, int h, byte[] rgba)
    {
        var t = new TestTexture(name, TextureRef.ForPictureTile(99, _textures.Count, 0), w, h, rgba);
        _textures[name] = t;
        _byRef[t.Ref] = t;
        return t;
    }

    public TestTexture this[string name] => _textures[name];

    public AlignedCase NewCase(string id, string group)
    {
        var c = new AlignedCase(id, group);
        Cases.Add(c);
        return c;
    }

    /// <summary>Writes the textures and lists.json, runs <c>python -m tsuki.compose run</c>, returns the references (one frame per case).</summary>
    public byte[] ComposeReference(TempFolder dir, TimeSpan timeout, out string log)
    {
        foreach (TestTexture t in _textures.Values)
        {
            File.WriteAllBytes(dir.File(t.Name + ".rgba"), t.Rgba);
        }

        File.WriteAllBytes(dir.File("lists.json"), ListsJson());
        log = Oracle.Run(timeout, "-m", "tsuki.compose", "run", dir.FullName).Trim();
        byte[] refs = File.ReadAllBytes(dir.File("ref.rgba"));
        long expected = (long)Cases.Count * Width * Height * 4;
        return refs.LongLength == expected ? refs : throw new InvalidDataException($"ref.rgba has {refs.LongLength} bytes, expected {expected}");
    }

    /// <summary>Runs every case on the GPU (the frame of <paramref name="c"/> must have the suite's size) and returns the frames, concatenated.</summary>
    public byte[] RenderGpu(GpuCompositor c)
    {
        var source = new TestTextureSource();
        foreach (TestTexture t in _textures.Values)
        {
            source.Textures[t.Ref] = c.CreateTexture(t.Width, t.Height, t.Rgba);
        }

        var exec = new DisplayListExecutor(c, source);
        var frames = new byte[(long)Cases.Count * Width * Height * 4];
        var list = new DisplayList(64);
        try
        {
            for (int i = 0; i < Cases.Count; i++)
            {
                AlignedCase k = Cases[i];
                source.Transition = k.Transition;
                list.Reset(i);
                foreach (CaseOp op in k.Ops)
                {
                    switch (op)
                    {
                        case CmdOp cmd:
                            list.Add(cmd.Cmd);
                            break;
                        case ClearOp cl:
                            Flush(exec, list, i);
                            c.Clear(0, cl.R, cl.G, cl.B, cl.A);
                            break;
                        case LoadOp ld:
                            Flush(exec, list, i);
                            c.Load(c.Frame, _textures[ld.Texture].Rgba);
                            break;
                    }
                }

                Flush(exec, list, i);
                c.Read(c.Frame).CopyTo(frames, (long)i * Width * Height * 4);
            }
        }
        finally
        {
            foreach (uint t in source.Textures.Values)
            {
                c.DeleteTexture(t);
            }
        }

        return frames;
    }

    private static void Flush(DisplayListExecutor exec, DisplayList list, long frame)
    {
        if (list.Count > 0)
        {
            list.Seal();
            ExecuteStats s = exec.Execute(list);
            if (s.MissingTextures != 0)
            {
                throw new InvalidOperationException($"{s.MissingTextures} textures missing");
            }
        }

        list.Reset(frame);
    }

    private byte[] ListsJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("width", Width);
            w.WriteNumber("height", Height);
            w.WriteString("export", Export);
            w.WriteStartObject("textures");
            foreach (TestTexture t in _textures.Values)
            {
                w.WriteStartObject(t.Name);
                w.WriteString("file", t.Name + ".rgba");
                w.WriteNumber("width", t.Width);
                w.WriteNumber("height", t.Height);
                w.WriteEndObject();
            }

            w.WriteEndObject();
            w.WriteStartArray("lists");
            foreach (AlignedCase k in Cases)
            {
                w.WriteStartObject();
                w.WriteString("id", k.Id);
                w.WriteStartArray("ops");
                foreach (CaseOp op in k.Ops)
                {
                    WriteOp(w, k, op);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return ms.ToArray();
    }

    private void WriteOp(Utf8JsonWriter w, AlignedCase k, CaseOp op)
    {
        w.WriteStartObject();
        switch (op)
        {
            case ClearOp c:
                w.WriteString("op", "clear");
                WriteBytes(w, "rgba", c.R, c.G, c.B, c.A);
                break;
            case LoadOp l:
                w.WriteString("op", "load");
                w.WriteString("tex", l.Texture);
                break;
            case CmdOp { Cmd.Kind: DrawKind.Capture } cap:
                w.WriteString("op", "capture");
                w.WriteNumber("surface", cap.Cmd.Texture.A);
                break;
            case CmdOp q:
                WriteQuad(w, k, q.Cmd, q.StateOverride);
                break;
        }

        w.WriteEndObject();
    }

    private void WriteQuad(Utf8JsonWriter w, AlignedCase k, in DrawCmd c, bool stateOverride)
    {
        if (c.Target != 0)
        {
            throw new NotSupportedException("aligned lists use target 0");
        }

        int x = Int(c.V0.X), y = Int(c.V0.Y), wd = Int(c.V3.X) - x, ht = Int(c.V3.Y) - y;
        if (c.V1.X != c.V3.X || c.V1.Y != c.V0.Y || c.V2.X != c.V0.X || c.V2.Y != c.V3.Y)
        {
            throw new NotSupportedException("aligned quads are axis-aligned rectangles in strip order");
        }

        uint rgba = c.V0.Rgba;
        if (c.V1.Rgba != rgba || c.V2.Rgba != rgba || c.V3.Rgba != rgba)
        {
            throw new NotSupportedException("aligned quads have one vertex colour");
        }

        w.WriteString("op", "quad");
        if (c.Kind == DrawKind.FillQuad)
        {
            w.WriteNull("tex");
        }
        else
        {
            (string name, int tw, int th) = c.Texture.Kind == TextureKind.Capture
                ? ($"capture:{c.Texture.A}", Width, Height)
                : (_byRef[c.Texture].Name, _byRef[c.Texture].Width, _byRef[c.Texture].Height);
            int sx = Int(c.V0.U * tw), sy = Int(c.V0.V * th);
            if (Int(c.V3.U * tw) - sx != wd || Int(c.V3.V * th) - sy != ht)
            {
                throw new NotSupportedException("aligned quads map texels 1:1 onto pixels");
            }

            w.WriteString("tex", name);
            w.WriteStartArray("src");
            w.WriteNumberValue(sx);
            w.WriteNumberValue(sy);
            w.WriteEndArray();
            if (k.Transition.TryGetValue(c.Texture, out TransitionParams tp))
            {
                w.WriteStartArray("transition");
                w.WriteNumberValue(tp.Lo);
                w.WriteNumberValue(tp.Hi);
                w.WriteNumberValue(tp.Invert);
                w.WriteEndArray();
            }
        }

        w.WriteStartArray("dst");
        w.WriteNumberValue(x);
        w.WriteNumberValue(y);
        w.WriteNumberValue(wd);
        w.WriteNumberValue(ht);
        w.WriteEndArray();
        WriteBytes(w, "color", (byte)rgba, (byte)(rgba >> 8), (byte)(rgba >> 16), (byte)(rgba >> 24));
        w.WriteNumber("word", c.EffectWord);
        if (stateOverride)
        {
            w.WriteNumber("state", c.BlendState);           // otherwise compose takes the state from the word (5.1)
        }

        if (c.Clip is { } clip)
        {
            w.WriteStartArray("clip");
            w.WriteNumberValue(clip.X);
            w.WriteNumberValue(clip.Y);
            w.WriteNumberValue(clip.Width);
            w.WriteNumberValue(clip.Height);
            w.WriteEndArray();
        }
    }

    private static int Int(float v)
    {
        int i = (int)MathF.Round(v);
        return MathF.Abs(v - i) < 1e-4f ? i : throw new NotSupportedException($"{v.ToString(CultureInfo.InvariantCulture)} is not an integer position");
    }

    private static void WriteBytes(Utf8JsonWriter w, string name, params byte[] b)
    {
        w.WriteStartArray(name);
        foreach (byte x in b)
        {
            w.WriteNumberValue(x);
        }

        w.WriteEndArray();
    }
}

/// <summary>Textures of a test, with per-case transition parameters.</summary>
internal sealed class TestTextureSource : ITextureSource
{
    public Dictionary<TextureRef, uint> Textures { get; } = [];

    public Dictionary<TextureRef, TransitionParams> Transition { get; set; } = [];

    public bool TryGetTexture(in TextureRef key, out uint texture, out TransitionParams? transition)
    {
        transition = Transition.TryGetValue(key, out TransitionParams tp) ? tp : null;
        return Textures.TryGetValue(key, out texture);
    }
}

/// <summary>Per-pixel error in UNORM8 levels (max over the channels), like <c>compose.levels_error</c>.</summary>
internal readonly record struct LevelStats(int Max, long Pixels, long Exact, long Le1, long Le2)
{
    public double ExactShare => Pixels == 0 ? 1 : (double)Exact / Pixels;

    public double Le1Share => Pixels == 0 ? 1 : (double)Le1 / Pixels;

    public double Le2Share => Pixels == 0 ? 1 : (double)Le2 / Pixels;

    public static LevelStats Compare(ReadOnlySpan<byte> gpu, ReadOnlySpan<byte> reference)
    {
        if (gpu.Length != reference.Length)
        {
            throw new ArgumentException($"{gpu.Length} vs {reference.Length} bytes");
        }

        int max = 0;
        long exact = 0, le1 = 0, le2 = 0;
        for (int i = 0; i < gpu.Length; i += 4)
        {
            int e = Math.Max(
                Math.Max(Math.Abs(gpu[i] - reference[i]), Math.Abs(gpu[i + 1] - reference[i + 1])),
                Math.Max(Math.Abs(gpu[i + 2] - reference[i + 2]), Math.Abs(gpu[i + 3] - reference[i + 3])));
            max = Math.Max(max, e);
            exact += e == 0 ? 1 : 0;
            le1 += e <= 1 ? 1 : 0;
            le2 += e <= 2 ? 1 : 0;
        }

        return new LevelStats(max, gpu.Length / 4, exact, le1, le2);
    }

    public LevelStats Merge(LevelStats o) => new(Math.Max(Max, o.Max), Pixels + o.Pixels, Exact + o.Exact, Le1 + o.Le1, Le2 + o.Le2);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"max {Max}, exact {ExactShare:P3} ({Exact}), <=1 {Le1Share:P3}, <=2 {Le2Share:P3} ({Le2}) of {Pixels} px");
}

/// <summary>Test output that also reaches the console summary.</summary>
internal static class Report
{
    public static string Lines(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);

    public static string Fmt(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

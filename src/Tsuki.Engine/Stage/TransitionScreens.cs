using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Stage;

/// <summary>
/// The per-frame draw of the <c>_FADS</c> types that only the engine's own screens could start: 18 (mosaic), 20 (scan-line jitter), 21..24 (slides),
/// 68 (shatter, dead code), 69 (white-out then rule dissolve) and 70 (zoom-out of the old frame). They come from the cases 0x67A7C, 0x67AF5, 0x67895,
/// 0x67CB2, 0x67CC4 and 0x67E90 of the draw callback 0x67820 (docs/status/knife/K7.md; recompiled from the disassembly and checked by emulating the
/// callback at five progress values). No script uses them (the census has types 0, 1 and 2 only) and no engine screen of the PS4 build starts
/// them: the only other caller of the type store 0x42480 is a dead timeline player. They are kept so the transition table is complete.
/// </summary>
/// <remarks>
/// The cases were written for the 960 x 544 layout of the Vita build and ported to the 1920 x 1080 captures unchanged: their source rectangles are
/// 960 x 544 texels (one texel is one pixel) at the top-left, so on the PS4 the slides, the zoom and the mosaic act on that crop of the capture only.
/// This class reproduces what the engine draws (<c>docs/status/knife/K7.md</c> lists the geometry of each type); it does not "fix" the crop.
/// All numbers are in the 480 x 272 units of the callback (4 px per unit in x, 1080 / 272 px per unit in y).
/// </remarks>
public static class TransitionScreens
{
    private const float UnitX = 4f;
    private const float UnitY = 1080f / 272f;
    private const float CaptureWidth = 1920f;
    private const float CaptureHeight = 1080f;

    /// <summary>True for the types this class draws.</summary>
    public static bool Handles(int kind) => kind is 18 or (>= 20 and <= 24) or (>= 68 and <= 70);

    /// <summary>
    /// Appends one frame of transition <paramref name="kind"/> at remaining progress <paramref name="progress"/> (1 at the start, 0 at the end) after the
    /// new scene. Type 69 needs the rule picture of the transition (null: the white-out stays); type 20 draws one random offset per scan line from
    /// <paramref name="rand"/> (the shared libc <c>rand()</c>).
    /// </summary>
    public static void Frame(DisplayList list, int kind, float progress, TextureRef? rule, Func<int> rand)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(rand);
        if (progress <= 0f)
        {
            return;
        }

        switch (kind)
        {
            case 18:
                Mosaic(list, progress);
                break;
            case 20:
                ScanLines(list, progress, rand);
                break;
            case >= 21 and <= 24:
                Slide(list, kind, progress);
                break;
            case 69:
                WhiteOut(list, progress, rule);
                break;
            case 70:
                ZoomOut(list, progress);
                break;
            default:
                break;          // 68: the particle table is never filled, nothing is drawn
        }
    }

    // 0x67895: the old frame (capture 15) and the new scene (capture 0, taken here) slide by the same distance in opposite directions
    private static void Slide(DisplayList list, int kind, float p)
    {
        Capture(list, 0);
        const float h = 480f;               // (R - L) / 2 of the full 960 x 544 rectangle
        float old = 1f - p;
        (float dxo, float dyo, float dxn, float dyn) = kind switch
        {
            21 => (0f, -272f * old, 0f, 272f * p),
            22 => (0f, 272f * old, 0f, -272f * p),
            23 => (old * h, 0f, -p * h, 0f),
            _ => (-old * h, 0f, p * h, 0f),
        };
        Rect(list, 15, dxo, dyo, 0, 0, 960, 544, nearest: false);
        Rect(list, 0, dxn, dyn, 0, 0, 960, 544, nearest: false);
    }

    // 0x67AF5: cross-fade the old frame in, capture the result, then redraw it line by line with a random horizontal offset over black
    private static void ScanLines(DisplayList list, float p, Func<int> rand)
    {
        list.Add(Textured(TextureRef.ForCapture(0), 0, 0, 1920, 1080, 0, 0, 1, 1, Fads255(p)));
        Capture(list, 0);
        list.Add(Fill(0, 0, 1920, 1080, Color(0, 0, 0, 255)));
        float amplitude = MathF.Min(1f, p * 1.1111f);
        for (int i = 0; i < 544; i++)
        {
            int x = (int)(((rand() & 0x1F) - 12) * amplitude);
            int y = (int)(i * 0.5f);
            Rect(list, 0, x, y, 0, i, 960, i + 1, nearest: false);
        }
    }

    // 0x67A7C: the new scene is shrunk by N into the corner, captured and magnified back with nearest-texel sampling
    private static void Mosaic(DisplayList list, float p)
    {
        Capture(list, 0);
        float v = p > 0.45f ? ((p - 0.45f) * -2.2222f) + 1f : p * 2.2222f;
        int n = (int)(MathF.Min(1f, v) * 16f);
        int tile = n >= 3 ? n : 2;
        float w = CaptureWidth * (1f / (6f * tile)) * UnitX;
        float h = CaptureHeight * (0.2074074f / tile) * UnitY;
        list.Add(Textured(TextureRef.ForCapture(0), 0, 0, w, h, 0, 0, 1, 1, Color(255, 255, 255, 255)));
        Capture(list, 0);
        int right = (int)(1280f / tile);
        int bottom = (int)(896f / tile);
        list.Add(Textured(
            TextureRef.ForCapture(0), 0, 0, right * tile, bottom * tile, 0, 0, right / CaptureWidth, bottom / CaptureHeight, Color(255, 255, 255, 255), nearest: true));
    }

    // 0x67CC4: the whole screen turns white, then the new scene dissolves in through the rule picture during the last quarter
    private static void WhiteOut(DisplayList list, float p, TextureRef? rule)
    {
        Capture(list, 0);
        list.Add(Fill(0, 0, 1920, 1080, Color(255, 255, 255, 255)));
        if (p > 0.25f || rule is not { } r)
        {
            return;
        }

        (float lo, float hi) = RuleWindow(1f - (4f * p));
        list.Add(Textured(r, 0, 0, 1920, 1080, 0, 0, 1, 1, Color(255, 255, 255, 255), word: 58, fx: QuadFx.ForRule(lo, hi, 0f)));
        list.Add(Textured(TextureRef.ForCapture(0), 0, 0, 1920, 1080, 0, 0, 1, 1, Color(255, 255, 255, 255), word: 26));
    }

    // 0x67E90: the old frame (capture 15, rectangle 960 x 544) shrinks towards the screen centre while turning through 4 full circles in its scale
    private static void ZoomOut(DisplayList list, float p)
    {
        float angle = (int)((1f - p) * 32768f * 8f) * 9.5873e-5f;
        float sx = (0.1f * MathF.Cos(angle)) + p;
        float sy = (0.1f * MathF.Sin(angle)) + p;
        float w = 960f * sx;
        float h = 544f * sy;
        list.Add(Textured(TextureRef.ForCapture(15), 960f - (w / 2f), 540f - (h / 2f), 960f + (w / 2f), 540f + (h / 2f), 0, 0, 960f / CaptureWidth, 544f / CaptureHeight, Color(255, 255, 255, 255)));
    }

    /// <summary>The rule window of 0xD73B0 for a remaining progress (a = 256 - trunc(256 progress), width 256): (lo, hi).</summary>
    public static (float Lo, float Hi) RuleWindow(float progress)
    {
        int a = 256 - (int)(progress * 256f);
        float x0 = a * 0.0078125f;
        return (MathF.Max(-1f, x0 + (256 * -0.00390625f)), MathF.Min(2f, x0));
    }

    private static uint Color(byte r, byte g, byte b, byte a) => r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

    private static uint Fads255(float p) => Color(255, 255, 255, (byte)Math.Min(255, (int)(MathF.Max(0f, p) * 255f)));

    private static void Capture(DisplayList list, int surface) => list.Add(new DrawCmd { Kind = DrawKind.Capture, Texture = TextureRef.ForCapture(surface) });

    // a texel rectangle (l, t, r, b) of a capture at the unit position (ux, uy), one texel per pixel (picture scale 0.25 x 0.2518519)
    private static void Rect(DisplayList list, int surface, float ux, float uy, int l, int t, int r, int b, bool nearest)
    {
        float x = ux * UnitX;
        float y = uy * UnitY;
        list.Add(Textured(
            TextureRef.ForCapture(surface), x, y, x + (r - l), y + (b - t), l / CaptureWidth, t / CaptureHeight, r / CaptureWidth, b / CaptureHeight, Color(255, 255, 255, 255), nearest: nearest));
    }

    private static DrawCmd Fill(float x0, float y0, float x1, float y1, uint rgba) => new()
    {
        Kind = DrawKind.FillQuad,
        V0 = new Vertex(x0, y0, 0, 0, rgba),
        V1 = new Vertex(x1, y0, 0, 0, rgba),
        V2 = new Vertex(x0, y1, 0, 0, rgba),
        V3 = new Vertex(x1, y1, 0, 0, rgba),
        EffectWord = 0,
        BlendState = (byte)EffectTables.BlendStateOf(0),
    };

    private static DrawCmd Textured(TextureRef tex, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, uint rgba, int word = 0, bool nearest = false, QuadFx fx = default) => new()
    {
        Kind = DrawKind.TexturedQuad,
        Texture = tex,
        V0 = new Vertex(x0, y0, u0, v0, rgba),
        V1 = new Vertex(x1, y0, u1, v0, rgba),
        V2 = new Vertex(x0, y1, u0, v1, rgba),
        V3 = new Vertex(x1, y1, u1, v1, rgba),
        EffectWord = unchecked((short)word),
        BlendState = (byte)EffectTables.BlendStateOf(word),
        Fx = nearest ? fx with { Nearest = true } : fx,
    };
}

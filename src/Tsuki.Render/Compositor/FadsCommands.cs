// The _FADS 0/1/2 capture path as display-list commands (docs/formats/compositing.md 6, 7.2, 7.4;
// compose.fads_capture, rule_wipe, crossfade, transition_frame), from spike S3 Compositor
// BeginTransition / DrawRuleWipe / DrawCrossfade: the transition start fills alpha := 1 (effect 56)
// and captures the frame into nexl_Capture[0] (and [15] for the CAPTURE15 types); each frame of
// type 2 draws the rule mask (effect 58, state 4) then the capture through the target alpha
// (effect 26, state 6); type 0 and the type-2 fallback draw the capture with alpha trunc(255 p).
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;

namespace Tsuki.Render.Compositor;

/// <summary>Builders of display-list commands (quads in strip order TL, TR, BL, BR).</summary>
public static class Quads
{
    /// <summary>Packs a vertex colour as the contract stores it (R in bits 0-7 .. A in 24-31).</summary>
    public static uint Rgba(byte r, byte g, byte b, byte a) => r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

    /// <summary>White with alpha <paramref name="a"/>.</summary>
    public static uint White(byte a = 255) => Rgba(255, 255, 255, a);

    /// <summary>A textured quad on the rectangle (x0, y0)-(x1, y1) with texture coordinates (u0, v0)-(u1, v1).</summary>
    public static DrawCmd Textured(TextureRef texture, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, uint rgba, int word, int? blendState = null, byte target = 0, PixelRect? clip = null) => new()
    {
        Kind = DrawKind.TexturedQuad,
        Target = target,
        Texture = texture,
        V0 = new Vertex(x0, y0, u0, v0, rgba),
        V1 = new Vertex(x1, y0, u1, v0, rgba),
        V2 = new Vertex(x0, y1, u0, v1, rgba),
        V3 = new Vertex(x1, y1, u1, v1, rgba),
        EffectWord = unchecked((short)word),
        BlendState = (byte)(blendState ?? DrawEffect.BlendStateOf(word)),
        Clip = clip,
    };

    /// <summary>An untextured fill on the rectangle (x0, y0)-(x1, y1).</summary>
    public static DrawCmd Fill(float x0, float y0, float x1, float y1, uint rgba, int word, int? blendState = null, byte target = 0, PixelRect? clip = null) => new()
    {
        Kind = DrawKind.FillQuad,
        Target = target,
        V0 = new Vertex(x0, y0, 0, 0, rgba),
        V1 = new Vertex(x1, y0, 0, 0, rgba),
        V2 = new Vertex(x0, y1, 0, 0, rgba),
        V3 = new Vertex(x1, y1, 0, 0, rgba),
        EffectWord = unchecked((short)word),
        BlendState = (byte)(blendState ?? DrawEffect.BlendStateOf(word)),
        Clip = clip,
    };

    /// <summary>Copy target <paramref name="target"/> into capture surface <paramref name="surface"/> (record type 6).</summary>
    public static DrawCmd Capture(int surface, byte target = 0) => new()
    {
        Kind = DrawKind.Capture,
        Target = target,
        Texture = TextureRef.ForCapture(surface),
    };
}

/// <summary>The commands of <c>_FADS(type, ms, rule)</c> (compositing 7).</summary>
public static class FadsCommands
{
    // 0x6B910 jump table 0x209FF0: these types also capture into nexl_Capture[15] (compose.CAPTURE15_TYPES).
    private static readonly HashSet<int> Capture15Types = [2, .. Enumerable.Range(12, 13), 29, 30, 31, 32, 46, 49, 52, 53, 70];

    /// <summary>True when the transition start of <paramref name="kind"/> also captures into surface 15.</summary>
    public static bool CapturesSurface15(int kind) => Capture15Types.Contains(kind);

    /// <summary>
    /// <c>compose.fads_capture</c>: the capture surface the per-frame draw of <paramref name="kind"/> reads (0 or 15),
    /// or null when it draws nothing (types 1 and 49).
    /// </summary>
    /// <exception cref="NotSupportedException">Types 18, 20..24 and 68..70 (engine screens: they draw from several surfaces, see <see cref="TransitionScreens"/>).</exception>
    public static int? CaptureSurfaceOf(int kind)
    {
        if (kind is 1 or 49)
        {
            return null;
        }

        if (kind == 2)
        {
            return 15;
        }

        if (kind is 18 or (>= 20 and <= 24) or (>= 68 and <= 70) || kind is < 0 or > 70)
        {
            throw new NotSupportedException($"_FADS type {kind}: an engine screen (TransitionScreens), not a single-surface draw");
        }

        return 0;
    }

    /// <summary>
    /// Transition start (0x6B910): effect 56 fill (state 11: alpha := max(1, da)) over the whole target, then the
    /// capture into surface 0 and, for the CAPTURE15 types, into surface 15. Append it before the new scene.
    /// </summary>
    public static void Begin(DisplayList list, int kind, int width = 1920, int height = 1080)
    {
        ArgumentNullException.ThrowIfNull(list);
        list.Add(Quads.Fill(0, 0, width, height, Quads.White(), DrawEffect.AlphaUnion));
        list.Add(Quads.Capture(0));
        if (CapturesSurface15(kind))
        {
            list.Add(Quads.Capture(15));
        }
    }

    /// <summary>
    /// One frame of the transition at remaining progress <paramref name="progress"/>, appended after the new scene
    /// (<c>compose.transition_frame</c>). Type 1 and progress &lt;= 0 draw nothing. Type 2 with a rule draws the rule
    /// mask (effect 58) and the capture of surface 15 through the target alpha (effect 26); the caller gives the
    /// rule texture <see cref="TransitionParams.ForRule"/>(progress) (texture +0xF0, set by 0xD73B0 every frame).
    /// Otherwise the capture is drawn with effect 0 and vertex alpha trunc(255 p). The engine-screen types 18, 20..24 and 68..70 draw
    /// <see cref="TransitionScreens"/> (type 69 uses <paramref name="rule"/>, type 20 draws <paramref name="rand"/>, by default a fresh libc generator).
    /// </summary>
    /// <returns>The transition parameters the rule texture must carry, or null when no rule pass was added.</returns>
    public static TransitionParams? Frame(DisplayList list, int kind, float progress, TextureRef? rule, int width = 1920, int height = 1080, Func<int>? rand = null)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (kind == 1 || progress <= 0)
        {
            return null;
        }

        if (TransitionScreens.Handles(kind))
        {
            TransitionScreens.Frame(list, kind, progress, rule, rand ?? new Ps4Rand().Next);
            return null;
        }

        int? surface = CaptureSurfaceOf(kind);
        if (surface is null)
        {
            return null;
        }

        TextureRef capture = TextureRef.ForCapture(surface.Value);
        if (kind == 2 && rule is { } r)
        {
            list.Add(Quads.Textured(r, 0, 0, width, height, 0, 0, 1, 1, Quads.White(), DrawEffect.AlphaSet));
            list.Add(Quads.Textured(capture, 0, 0, width, height, 0, 0, 1, 1, Quads.White(), DrawEffect.ThroughDstAlpha));
            return TransitionParams.ForRule(progress);
        }

        list.Add(Quads.Textured(capture, 0, 0, width, height, 0, 0, 1, 1, Quads.White(Fads.CrossfadeAlphaByte(progress)), DrawEffect.Normal));
        return null;
    }
}

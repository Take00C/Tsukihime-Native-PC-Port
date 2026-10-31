// The blurred backdrop of the system menu and the backlog (package K16): the story's frozen scene is blurred behind them. The engine writeup ("In the game") has the
// PS4 / knife rule: the scene is captured once when the menu opens, blurred (two separable gaussian passes), and the blurred picture fades in over 12 frames at 1/12 a
// frame (knife game.rs menu_blur, ui.rs sub_2E000); the menu's own backdrop and widgets are drawn over it.
//
// knife's overseas build blurs a half-size copy with sigma 1.25 taps of 1.567 half-size pixels, i.e. a gaussian of about 3.9 full-size pixels, and scales it up (the
// upscale adds a little softness); the passes here run at full size with sigma 4.4 pixels, which looks the same. The display list carries four commands:
// capture the frame so far, blur it along x into an offscreen target, capture that, blur it along y over the frame at the fade alpha (QuadPass.Blur).
using Tsuki.Engine.Contracts;

namespace Tsuki.Ui;

/// <summary>Appends the blur of the scene drawn so far to a display list.</summary>
public static class UiMenuBlur
{
    /// <summary>The capture surface that holds the unblurred scene (the stage uses 0, 8..10 and 15).</summary>
    public const int SceneSurface = 12;

    /// <summary>The capture surface that holds the horizontally blurred scene.</summary>
    public const int PassSurface = 13;

    /// <summary>The offscreen target of the horizontal pass (the stage's own are 1, 2, 3 and 8).</summary>
    public const byte PassTarget = 40;

    /// <summary>The standard deviation in pixels of the 1920 x 1080 frame.</summary>
    public const float Sigma = 4.4f;

    /// <summary>The taps on each side of the centre.</summary>
    public const int Taps = 13;

    /// <summary>Frames of the fade-in (the blurred picture's alpha rises by 1/12 a frame).</summary>
    public const int FadeFrames = 12;

    /// <summary>
    /// Adds the four commands that blur what the list has drawn so far to the frame, at <paramref name="alpha"/> (0..1) over the unblurred scene. Nothing is added at
    /// an alpha of 0.
    /// </summary>
    /// <param name="list">The list, holding the scene already.</param>
    /// <param name="alpha">The blur's strength: 1 replaces the scene by its blurred copy.</param>
    public static void Append(DisplayList list, float alpha)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (alpha <= 0f)
        {
            return;
        }

        const float w = 1920f;
        const float h = 1080f;
        list.Add(new DrawCmd { Kind = DrawKind.Capture, Target = 0, Texture = TextureRef.ForCapture(SceneSurface) });
        list.Add(Pass(TextureRef.ForCapture(SceneSurface), PassTarget, 255, 0, new QuadFx(QuadPass.Blur, 0, false, 1f / w, 0f, Sigma, Taps)));
        list.Add(new DrawCmd { Kind = DrawKind.Capture, Target = PassTarget, Texture = TextureRef.ForCapture(PassSurface) });
        byte a = (byte)Math.Clamp((int)MathF.Floor(alpha * 255f), 1, 255);
        list.Add(Pass(TextureRef.ForCapture(PassSurface), 0, a, 1, new QuadFx(QuadPass.Blur, 0, false, 0f, 1f / h, Sigma, Taps)));
    }

    private static DrawCmd Pass(TextureRef source, byte target, byte alpha, byte blend, QuadFx fx)
    {
        uint rgba = 0xFFFFFFu | ((uint)alpha << 24);
        return new DrawCmd
        {
            Kind = DrawKind.TexturedQuad,
            Target = target,
            Texture = source,
            V0 = new Vertex(0, 0, 0, 0, rgba),
            V1 = new Vertex(1920, 0, 1, 0, rgba),
            V2 = new Vertex(0, 1080, 0, 1, rgba),
            V3 = new Vertex(1920, 1080, 1, 1, rgba),
            EffectWord = 0,
            BlendState = blend,
            Fx = fx,
        };
    }
}

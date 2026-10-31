using System.Runtime.CompilerServices;

namespace Tsuki.Ui;

/// <summary>A rectangle in canvas pixels (x, y, width, height).</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="W">Width.</param>
/// <param name="H">Height.</param>
public readonly record struct RectF(float X, float Y, float W, float H)
{
    /// <summary>Right edge.</summary>
    public float Right => X + W;

    /// <summary>Bottom edge.</summary>
    public float Bottom => Y + H;

    /// <summary>A rectangle from corners (x0, y0) and (x1, y1).</summary>
    public static RectF FromCorners(float x0, float y0, float x1, float y1) => new(x0, y0, x1 - x0, y1 - y0);

    /// <summary>True when (<paramref name="px"/>, <paramref name="py"/>) is inside (left and top inclusive, right and bottom exclusive).</summary>
    public bool Contains(float px, float py) => px >= X && px < X + W && py >= Y && py < Y + H;

    /// <summary>The rectangle moved by (dx, dy).</summary>
    public RectF Offset(float dx, float dy) => new(X + dx, Y + dy, W, H);

    /// <summary>The overlap with <paramref name="o"/>, or an empty rectangle.</summary>
    public RectF Intersect(RectF o)
    {
        float x0 = MathF.Max(X, o.X);
        float y0 = MathF.Max(Y, o.Y);
        float x1 = MathF.Min(Right, o.Right);
        float y1 = MathF.Min(Bottom, o.Bottom);
        return x1 > x0 && y1 > y0 ? new RectF(x0, y0, x1 - x0, y1 - y0) : new RectF(x0, y0, 0, 0);
    }

    /// <summary>True when the rectangle has no area.</summary>
    public bool IsEmpty => W <= 0 || H <= 0;
}

/// <summary>
/// The UI canvas: the 1920 x 1080 frame. The PS4 screens lay out in 640 x 448 units (3 px per unit across, 1080 / 448 = 2.4107 px down;
/// docs/formats/archives_ui_movies.md 7.5), in 960 x 540 / 960 x 544 units (2 px), or directly in pixels, as knife's ui.rs does.
/// </summary>
public static class UiMetrics
{
    /// <summary>Canvas width in pixels.</summary>
    public const float Width = 1920f;

    /// <summary>Canvas height in pixels.</summary>
    public const float Height = 1080f;

    /// <summary>Pixels per unit across in the 640 x 448 space.</summary>
    public const float UnitX = Width / 640f;

    /// <summary>Pixels per unit down in the 640 x 448 space.</summary>
    public const float UnitY = Height / 448f;

    /// <summary>The 640 x 448 x coordinate in pixels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float X(float units) => units * UnitX;

    /// <summary>The 640 x 448 y coordinate in pixels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Y(float units) => units * UnitY;

    /// <summary>The pixel rectangle of a 640 x 448 rectangle (x, y, w, h in units).</summary>
    public static RectF Rect(float x, float y, float w, float h) => new(x * UnitX, y * UnitY, w * UnitX, h * UnitY);

    /// <summary>The screen rectangle.</summary>
    public static RectF Screen => new(0, 0, Width, Height);
}

/// <summary>How a sprite is combined with what is under it.</summary>
public enum UiBlend : byte
{
    /// <summary>Normal alpha-over (draw effect 0).</summary>
    Alpha = 0,

    /// <summary>The additive surface mode of the menu glows (draw effect 11, <c>ps_add</c>; docs/formats/system_flow.md 2).</summary>
    PsAdd = 11,

    /// <summary>The additive blend state of the title moon glow (draw effect 52; system_flow.md 2).</summary>
    Add = 52,
}

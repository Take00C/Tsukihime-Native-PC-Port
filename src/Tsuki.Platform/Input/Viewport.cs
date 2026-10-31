// The letterboxed game area inside a window (ARCHITECTURE 6.2, 9.4): the 1920 x 1080 canvas scaled by the largest factor that fits, centred. Mouse positions
// in window pixels become canvas pixels and the PS4's 640 x 448 pointer space with it. The rectangle is the one GpuCompositor.Present blits to.
namespace Tsuki.Platform.Input;

/// <summary>The rectangle of the canvas inside a window, in window pixels.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct Viewport(int X, int Y, int Width, int Height)
{
    /// <summary>Canvas width in pixels.</summary>
    public const int CanvasWidth = 1920;

    /// <summary>Canvas height in pixels.</summary>
    public const int CanvasHeight = 1080;

    /// <summary>The PS4 pointer space: 640 x 448 units over the canvas (engine.md 8.5).</summary>
    public const int PointerWidth = 640;

    /// <summary>The PS4 pointer space height.</summary>
    public const int PointerHeight = 448;

    /// <summary>The letterboxed rectangle for a window of the given pixel size (the same arithmetic as <c>GpuCompositor.Present</c>).</summary>
    public static Viewport Fit(int windowWidth, int windowHeight)
    {
        windowWidth = Math.Max(1, windowWidth);
        windowHeight = Math.Max(1, windowHeight);
        float s = MathF.Min(windowWidth / (float)CanvasWidth, windowHeight / (float)CanvasHeight);
        int w = Math.Max(1, (int)(CanvasWidth * s));
        int h = Math.Max(1, (int)(CanvasHeight * s));
        return new Viewport((windowWidth - w) / 2, (windowHeight - h) / 2, w, h);
    }

    /// <summary>True when the window point lies inside the canvas.</summary>
    public bool Contains(float x, float y) => x >= X && y >= Y && x < X + Width && y < Y + Height;

    /// <summary>The canvas pixel under a window point (not clamped).</summary>
    public (float X, float Y) ToCanvas(float x, float y) => ((x - X) * CanvasWidth / Width, (y - Y) * CanvasHeight / Height);

    /// <summary>
    /// The PS4 pointer (0..639, 0..447) under a window point: whether it is inside the canvas, and the clamped unit position (what <c>PointerState</c> carries).
    /// </summary>
    public (bool Inside, short X, short Y) ToPointer(float x, float y)
    {
        (float cx, float cy) = ToCanvas(x, y);
        short px = (short)Math.Clamp((int)(cx * PointerWidth / CanvasWidth), 0, PointerWidth - 1);
        short py = (short)Math.Clamp((int)(cy * PointerHeight / CanvasHeight), 0, PointerHeight - 1);
        return (Contains(x, y), px, py);
    }
}

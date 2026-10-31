// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/stage.rs (ClipRect::inset_rect, ClipRect::soft, ClipRect::band; unrotated branches)
using Tsuki.Engine.Contracts;
using static Tsuki.Engine.Stage.StageMath;

namespace Tsuki.Engine.Stage;

/// <summary>
/// A clip window of a layer (STCH a12..a17) in 1920 x 1080 pixels, before it is cut to the screen: the crop rectangle moved by the shakes, the inset
/// (<c>a16</c>, in pixels) and the mode (<c>a17</c>).
/// </summary>
/// <param name="X">Left.</param>
/// <param name="Y">Top.</param>
/// <param name="W">Width.</param>
/// <param name="H">Height.</param>
/// <param name="Inset">STCH a16: the width of the soft edge (modes 0, 7, 8 and up) or of the ridge and the scissor shrink (modes 1..6).</param>
/// <param name="Mode">STCH a17: 1..6 a ridge of a colour (black, white, red, three tints), 7 a twice as wide soft edge with the sharper textures, else a soft edge.</param>
public readonly record struct ClipWindow(float X, float Y, float W, float H, int Inset, int Mode)
{
    /// <summary>True for the ridge modes 1..6.</summary>
    public bool IsBand => Mode is >= 1 and <= 6;

    /// <summary>
    /// The scissor of the window (knife <c>inset_rect</c>, sub_36520): in the ridge modes it shrinks by the inset on each side whose extent is under the
    /// screen size, which cuts the solid outer half of the ridge away.
    /// </summary>
    public ClipWindow Scissor()
    {
        if (!IsBand)
        {
            return this;
        }

        ClipWindow c = this;
        float d = Inset;
        if (c.W < 1920f)
        {
            c = c with { X = c.X + d, W = c.W - (2f * d) };
        }

        if (c.H < 1080f)
        {
            c = c with { Y = c.Y + d, H = c.H - (2f * d) };
        }

        return c;
    }
}

/// <summary>The soft mask of a window with a fade-out edge (knife <c>SoftClip</c>): the fade widths of the four sides in pixels and the texture that shapes it.</summary>
/// <param name="Window">The window the mask covers.</param>
/// <param name="Left">Fade width at the left edge (0: none).</param>
/// <param name="Right">Fade width at the right edge.</param>
/// <param name="Top">Fade width at the top edge.</param>
/// <param name="Bottom">Fade width at the bottom edge.</param>
/// <param name="Texture">BORDER_PARTS child holding the gradation (0..5 the plain ones, 13..18 the sharper <c>_2</c> ones).</param>
/// <param name="Texels">Texel size of one quadrant of the texture (the radius, 10..256).</param>
public readonly record struct SoftWindow(ClipWindow Window, float Left, float Right, float Top, float Bottom, int Texture, int Texels);

public sealed partial class StageRuntime
{
    /// <summary>The render target that holds the soft mask of the window layer being drawn (rebuilt for every window with a soft edge).</summary>
    public const byte SoftTarget = 8;

    // texel sizes of the BORDER_PARTS gradation quadrants: f32[6] at text VA 0x200590 (K1b: SoftClipRadii). IEbootTables.ClipRadii gives the eboot's copy.
    private static readonly float[] DefaultRadii = [10f, 32f, 40f, 64f, 128f, 256f];

    /// <summary>The six soft-clip radii of the eboot (10, 32, 40, 64, 128, 256), or null for the built-in copy.</summary>
    public IReadOnlyList<float>? ClipRadii { get; set; }

    private IReadOnlyList<float> Radii => ClipRadii is { Count: 6 } r ? r : DefaultRadii;

    private static TextureRef Border(int child) => TextureRef.ForUiTile(UiSet.BorderParts, Tsuki.Engine.Contracts.Language.Ja, child, -1);

    private int RadiusClass(float r)
    {
        IReadOnlyList<float> radii = Radii;
        for (int i = 0; i < radii.Count; i++)
        {
            if (r < radii[i])
            {
                return i;
            }
        }

        return radii.Count - 1;
    }

    /// <summary>
    /// The soft mask of a window (knife <c>ClipRect::soft</c>, sub_32E30, unrotated): with an inset and a mode outside 1..6 the window is masked by a rounded
    /// rectangle whose edges fade out over (inset rounded up to even) pixels, twice that in mode 7, at most half the window's extent. A side that lies off the
    /// screen, or a window that spans the whole width or height, gets no fade there. Null when the window has no soft edge.
    /// </summary>
    internal SoftWindow? SoftOf(ClipWindow c)
    {
        if (c.Inset <= 0 || c.IsBand)
        {
            return null;
        }

        float half = ((c.Inset & 1) + c.Inset) * 0.5f;
        float r0 = c.Mode == 7 ? half * 2f : half;
        (float l, float t) = (c.X * 0.5f, c.Y * 0.5f);
        (float rx, float by) = ((c.X + c.W) * 0.5f, (c.Y + c.H) * 0.5f);
        float r = MathF.Min(r0, MathF.Min((rx - l) * 0.5f, (by - t) * 0.5f));
        if (r <= 0f)
        {
            return null;
        }

        float rl = l + r <= 0f ? 0f : r;
        float rr = rx - r >= 960f ? 0f : r;
        float rt = t + r <= 0f ? 0f : r;
        float rb = by - r >= 540f ? 0f : r;
        if (l == 0f && c.W * 0.5f == 960f)
        {
            rl = rr = 0f;
        }

        if (t == 0f && c.H * 0.5f == 540f)
        {
            rt = rb = 0f;
        }

        if (rl + rr + rt + rb <= 0f)
        {
            return null;            // no side fades: the mask would be the whole window at alpha 1
        }

        int k = RadiusClass(r);
        return new SoftWindow(c, rl * 2f, rr * 2f, rt * 2f, rb * 2f, c.Mode == 7 ? 13 + k : k, (int)Radii[k]);
    }

    // the pieces of an alphagradation texture of 2n x 2n texels (knife qword_572990): the four quadrants, the 1-texel strips beside the centre
    private static (float U0, float V0, float U1, float V1) Piece(int piece, int n)
    {
        float m = n, d = 2f * n;
        return piece switch
        {
            0 => (0f, 0f, m / d, m / d),
            1 => ((m - 1f) / d, 0f, m / d, m / d),
            2 => (m / d, 0f, 1f, m / d),
            3 => (0f, (m - 1f) / d, m / d, m / d),
            4 => (m / d, (m - 1f) / d, 1f, m / d),
            5 => (0f, m / d, m / d, 1f),
            6 => ((m - 1f) / d, m / d, m / d, 1f),
            _ => (m / d, m / d, 1f, 1f),
        };
    }

    /// <summary>
    /// Draws the soft mask into <paramref name="target"/> (sub_32E30's nine-slice): the four corner quadrants and four edge strips of the gradation texture at the
    /// fade widths, the inner rectangle at alpha 1. With <paramref name="multiply"/> the pieces narrow what the target holds (the window layer's silhouette)
    /// instead of replacing it.
    /// </summary>
    private void AddSoftMask(DisplayList list, SoftWindow s, byte target, bool multiply)
    {
        ClipWindow c = s.Window;
        float x0 = c.X, y0 = c.Y, x1 = c.X + c.W, y1 = c.Y + c.H;
        float fl = s.Left, fr = s.Right, ft = s.Top, fb = s.Bottom;
        int n = s.Texels;
        uint white = White();
        int word = multiply ? 0 : 58;

        void Add(DrawCmd q)
        {
            DrawCmd d = q with { Target = target };
            if (multiply)
            {
                d = d with { EffectWord = 0, BlendState = 0x80 };           // BlendStates.MaskMultiply: the target alpha times the quad alpha
            }

            list.Add(d);
        }

        void Slice(int piece, float ax, float ay, float bx, float by)
        {
            if (bx <= ax || by <= ay)
            {
                return;
            }

            (float u0, float v0, float u1, float v1) = Piece(piece, n);
            Add(Textured(Border(s.Texture), ax, ay, bx, by, u0, v0, u1, v1, white, word));
        }

        Slice(0, x0, y0, x0 + fl, y0 + ft);
        Slice(2, x1 - fr, y0, x1, y0 + ft);
        Slice(5, x0, y1 - fb, x0 + fl, y1);
        Slice(7, x1 - fr, y1 - fb, x1, y1);
        Slice(1, x0 + fl, y0, x1 - fr, y0 + ft);
        Slice(6, x0 + fl, y1 - fb, x1 - fr, y1);
        Slice(3, x0, y0 + ft, x0 + fl, y1 - fb);
        Slice(4, x1 - fr, y0 + ft, x1, y1 - fb);
        if (x1 - fr > x0 + fl && y1 - fb > y0 + ft)
        {
            Add(Fill(x0 + fl, y0 + ft, x1 - fr, y1 - fb, white, word));
        }
    }

    /// <summary>
    /// The ridge of a window in the modes 1..6 (knife <c>ClipRect::band</c>, sub_34A70, unrotated): a strip of the mode's colour along each edge, r = inset
    /// pixels wide inside the shrunk scissor, that fades out inward along the gradation texture; the solid outer half lies in the strip the scissor cuts away.
    /// Quads are clipped by the scissor.
    /// </summary>
    private void AddBand(DisplayList list, ClipWindow c, PixelRect? scissor)
    {
        float half = c.Inset * 0.5f;
        if (half < 1f || !c.IsBand)
        {
            return;
        }

        (float l, float t) = (c.X * 0.5f, c.Y * 0.5f);
        (float rx, float by) = ((c.X + c.W) * 0.5f, (c.Y + c.H) * 0.5f);
        (float w, float h) = (rx - l, by - t);
        float r = MathF.Min(half, MathF.Min(w * 0.5f, h * 0.5f));
        if (r <= 0f)
        {
            return;
        }

        int k = RadiusClass(r);
        int n = (int)Radii[k];
        (int cr, int cg, int cb) = c.Mode switch
        {
            2 => (255, 255, 255),
            3 => (255, 0, 0),
            4 => (45, 19, 0),
            5 => (246, 244, 232),
            6 => (255, 232, 210),
            _ => (0, 0, 0),
        };
        uint rgba = (uint)cr | ((uint)cg << 8) | ((uint)cb << 16) | 0xFF000000u;
        const int Over = 0;

        void Solid(float x, float y, float qw, float qh)
        {
            if (qw > 0f && qh > 0f)
            {
                list.Add(Fill(x * 2f, y * 2f, (x + qw) * 2f, (y + qh) * 2f, rgba, Over) with { Clip = scissor, BlendState = 1 });
            }
        }

        void Slice(int child, int piece, float x, float y, float qw, float qh)
        {
            if (qw > 0f && qh > 0f)
            {
                (float u0, float v0, float u1, float v1) = Piece(piece, n);
                list.Add(Textured(Border(child), x * 2f, y * 2f, (x + qw) * 2f, (y + qh) * 2f, u0, v0, u1, v1, rgba, Over) with { Clip = scissor, BlendState = 1 });
            }
        }

        bool fullW = l == 0f && w == 960f;
        bool fullH = t == 0f && h == 540f;
        float rl = l + r <= 0f || fullW ? 0f : r;
        float rr = rx - r >= 960f || fullW ? 0f : r;
        float rt = t + r <= 0f || fullH ? 0f : r;
        float rb = by - r >= 540f || fullH ? 0f : r;
        int gr = k, inv = k + 6;
        (bool On, float X, float Y, int Piece, float Ix, float Iy)[] corners =
        [
            (rt > 0f && rl > 0f, l, t, 0, l + r, t + r),
            (rb > 0f && rl > 0f, l, by - r, 5, l + r, by - (2f * r)),
            (rr > 0f && rt > 0f, rx - r, t, 2, rx - (2f * r), t + r),
            (rb > 0f && rr > 0f, rx - r, by - r, 7, rx - (2f * r), by - (2f * r)),
        ];
        foreach ((bool on, float x, float y, int piece, float ix, float iy) in corners)
        {
            if (on)
            {
                Solid(x, y, r, r);
                Slice(inv, piece, ix, iy, r, r);
            }
        }

        float along = w - (2f * rl) - (2f * rr);
        float down = h - (2f * rt) - (2f * rb);
        if (rt > 0f)
        {
            Solid(l + rl, t, w - rl - rr, rt);
            Slice(gr, 6, l + (2f * rl), t + rt, along, r);
        }

        if (rb > 0f)
        {
            Solid(l + rl, by - rb, w - rl - rr, rb);
            Slice(gr, 1, l + (2f * rl), by - (2f * rb), along, r);
        }

        if (rl > 0f)
        {
            Solid(l, t + rt, rl, h - rt - rb);
            Slice(gr, 4, l + rl, t + (2f * rt), r, down);
        }

        if (rr > 0f)
        {
            Solid(rx - rr, t + rt, rr, h - rt - rb);
            Slice(gr, 3, rx - (2f * rr), t + (2f * rt), r, down);
        }
    }
}

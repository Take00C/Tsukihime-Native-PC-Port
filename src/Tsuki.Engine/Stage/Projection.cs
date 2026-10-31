using static Tsuki.Engine.Stage.StageMath;

namespace Tsuki.Engine.Stage;

/// <summary>A 2 x 3 affine map: <c>x' = a u + b v + c</c>, <c>y' = d u + e v + f</c> (the oracle's <c>Affine</c>).</summary>
public readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    /// <summary>The identity.</summary>
    public static Affine Identity => new(1, 0, 0, 0, 1, 0);

    /// <summary>Composition: <c>this . o</c> (apply <paramref name="o"/> first).</summary>
    public static Affine operator *(Affine l, Affine o) =>
        new(
            (l.A * o.A) + (l.B * o.D), (l.A * o.B) + (l.B * o.E), (l.A * o.C) + (l.B * o.F) + l.C,
            (l.D * o.A) + (l.E * o.D), (l.D * o.B) + (l.E * o.E), (l.D * o.C) + (l.E * o.F) + l.F);

    /// <summary>Maps a point.</summary>
    public (double X, double Y) Apply(double u, double v) => ((A * u) + (B * v) + C, (D * u) + (E * v) + F);

    /// <summary>A translation.</summary>
    public static Affine Translate(double x, double y) => new(1, 0, x, 0, 1, y);

    /// <summary>A scale.</summary>
    public static Affine Scale(double sx, double sy) => new(sx, 0, 0, 0, sy, 0);

    /// <summary>A rotation by <paramref name="rad"/> (clockwise on a y-down screen).</summary>
    public static Affine Rotate(double rad)
    {
        double c = Math.Cos(rad);
        double s = Math.Sin(rad);
        return new Affine(c, -s, 0, s, c, 0);
    }
}

/// <summary>
/// The 2D projection of a layer: picture texels to the 640 x 362.667 scene canvas to the 1920 x 1080 frame (docs/formats/layers.md 7; VA 0x51060
/// -> 0xEB780 -> 0xDD6D0). Port of <c>layer_world</c>, <c>layer_zoom</c>, <c>layer_rot</c>, <c>layer_matrix</c>, <c>sprite_matrix</c> and
/// <c>canvas_to_frame</c> of <c>tsuki.misc_layer_state_and_motions</c>, working on the drawn records.
/// </summary>
public static class Projection
{
    /// <summary>The scene canvas size (VA 0x1A7710).</summary>
    public const double CanvasW = 640.0;

    /// <summary>The scene canvas height, 362.6666564941406.</summary>
    public const double CanvasH = 362.6666564941406;

    /// <summary>Texel to canvas, x (global scale 1/3).</summary>
    public const double PxX = 1.0 / 3.0;

    /// <summary>Texel to canvas, y: the float32 of 362.667 / 1080 (0.3358025).</summary>
    public static readonly double PxY = F(362.6666564941406 / 1080.0);

    private const int MaxDepth = 64;

    /// <summary>The canvas command VA 0xDCBE0(w, h, 0, 0): frame pixel = canvas unit * (1920 / w, 1080 / h).</summary>
    public static Affine CanvasToFrame() => Affine.Scale(1920.0 / CanvasW, 1080.0 / CanvasH);

    private static bool Ok(LayerRecord[] recs, int p) => p >= 0 && p < recs.Length && recs[p].Used != 0;

    private static float ClassFactorOf(IPictureCatalog c, int pic) => pic >= 0 && pic <= 0x39B6 && pic < c.Count ? c.Info(pic).ClassFactor : 1.0f;

    /// <summary>The own zoom (per mille) of a record: end + delta * remaining (VA 0x528C0 / 0x529F0 without the parent factor).</summary>
    public static (float X, float Y) ZoomTween(LayerRecord r)
    {
        float rem = r.TwZoom.Remaining();
        float zx = (r.ZoomDx / 0.001f * rem) + r.ZoomX;
        float zy = (r.ZoomDy / 0.001f * rem) + r.ZoomY;
        return (zx, zy);
    }

    /// <summary>VA 0x528C0 / 0x529F0: zoom per mille including the parents: <c>z * zp / 1000 * class(parent)</c>.</summary>
    public static (float X, float Y) LayerZoom(LayerRecord[] recs, int l, IPictureCatalog cat, int depth = 0)
    {
        (float zx, float zy) = ZoomTween(recs[l]);
        int p = recs[l].Parent;
        if (Ok(recs, p) && depth < MaxDepth)
        {
            (float pzx, float pzy) = LayerZoom(recs, p, cat, depth + 1);
            zx *= pzx * 0.001f;
            zy *= pzy * 0.001f;
            float fac = ClassFactorOf(cat, recs[p].Pic);
            zx *= fac;
            zy *= fac;
        }

        return (zx, zy);
    }

    /// <summary>VA 0x4C3E0: rotation (1/100 degree) summed over the parent chain.</summary>
    public static float LayerRot(LayerRecord[] recs, int l, int depth = 0)
    {
        float r = recs[l].Rot;
        int p = recs[l].Parent;
        return p >= 0 && p < 40 && Ok(recs, p) && depth < MaxDepth ? LayerRot(recs, p, depth + 1) + r : r;
    }

    /// <summary>
    /// VA 0x525B0 / 0x52740: the position of a layer in canvas units. A child's (x, y) is a point in its parent's picture: relative to the parent's
    /// anchor, times the size-class factor of the parent's picture, times the parent's zoom, rotated by the parent's rotation, plus the parent's position.
    /// </summary>
    public static (float X, float Y) LayerWorld(LayerRecord[] recs, int l, IPictureCatalog cat, int depth = 0)
    {
        LayerRecord r = recs[l];
        float x = r.X;
        float y = r.Y;
        int p = r.Parent;
        if (!Ok(recs, p) || depth >= MaxDepth)
        {
            return (x, y);
        }

        (float px, float py) = LayerWorld(recs, p, cat, depth + 1);
        (float zx, float zy) = LayerZoom(recs, p, cat, depth + 1);
        zx *= 0.001f;
        zy *= 0.001f;
        float th = LayerRot(recs, p) * MathF.PI / 180.0f / -100.0f;
        float fac = ClassFactorOf(cat, recs[p].Pic);
        float dx = (x * fac - recs[p].AnchorX) * zx;
        float dy = (recs[p].AnchorY - (y * fac)) * zy;
        double c = Math.Cos(th);
        double s = Math.Sin(th);
        return ((float)(px + (dx * c) - (dy * s)), (float)(py - (dx * s) - (dy * c)));
    }

    /// <summary>The picture offset (+0x50 / +0x54) plus the integer anchor: the part of the offset the draw adds untransformed (VA 0x519FF..0x51A1C).</summary>
    public static (float X, float Y) AnchorResidual(LayerRecord r) => (r.OffX + r.AnchorX, r.OffY + r.AnchorY);

    /// <summary>
    /// Map from picture texels of layer <paramref name="l"/> to scene-canvas units, as the draw (VA 0x51060 -> 0xEB780 -> 0xDD6D0) computes it.
    /// <paramref name="extra"/> (canvas units) is the screen-effect offset and the shake offsets of the slots, added to the translation column
    /// untransformed (and, with the STZ4 pivot flag, before the screen-space zoom); <paramref name="positionShift"/> is the mask-layer term, added
    /// to the layer position (so it also moves the pivot-zoom point P).
    /// </summary>
    public static Affine LayerMatrix(LayerRecord[] recs, int l, IPictureCatalog cat, (float X, float Y) extra = default, (float X, float Y) positionShift = default)
    {
        LayerRecord r = recs[l];
        (float wx, float wy) = LayerWorld(recs, l, cat);
        float tw = r.TwMove.Remaining();
        // the oracle rounds the three-term sums once (float64 sum, one float32 rounding)
        float xx = (float)((double)wx + r.Dx + (r.MoveDx * tw));
        float yy = (float)((double)wy + r.Dy + (r.MoveDy * tw));
        xx += positionShift.X;
        yy += positionShift.Y;
        (float zx, float zy) = LayerZoom(recs, l, cat);
        float s = (float)((r.Scale * (double)0.001f) + r.ScaleCur);

        // 0x51574..0x515F6: the angle adds the DIRECT parent's rotation whenever its index is 0..39, without the in-use test of 0x4C3E0
        int p = r.Parent;
        float own = r.Rot + r.RotCur;
        float rot = p >= 0 && p < recs.Length ? LayerRot(recs, p) + own : own;
        int ang = Cvtt((rot * 32768.0f) / 18000.0f);
        double th = ang * 2 * Math.PI / 65536;
        double soxyX = r.SoxyX * PxX;
        double soxyY = r.SoxyY * F(448.0 / 1080.0);
        Affine local;
        Affine screenZoom;
        if (r.ZoomPivot != 0)
        {
            local = Affine.Scale(s * PxX, s * PxY);
            screenZoom = Affine.Scale(zx * 0.001, zy * 0.001);
        }
        else
        {
            local = Affine.Scale((double)s * zx * 0.001 * PxX, (double)s * zy * 0.001 * PxY);
            screenZoom = Affine.Identity;
        }

        double ax = r.AnchorX;
        double ay = r.AnchorY;
        (float resX, float resY) = AnchorResidual(r);
        Affine off = Affine.Translate(-ax / PxX, -ay / PxY);
        Affine m = Affine.Translate(xx, yy) * Affine.Rotate(th) * local * off;
        double tx = soxyX + resX + extra.X;
        double ty = soxyY + resY + extra.Y;
        if (r.ZoomPivot != 0)
        {
            // 0x51FF5..0x52087: pivot = position + anchor - catalog anchor of the picture
            (int cax, int cay) = r.Pic >= 0 && r.Pic < cat.Count ? (cat.Info(r.Pic).AnchorX, cat.Info(r.Pic).AnchorY) : (0, 0);
            double px = F(xx + ax - cax);
            double py = F(yy + ay - cay);
            return Affine.Translate(px, py) * screenZoom * Affine.Translate(-px + tx, -py + ty) * m;
        }

        return Affine.Translate(tx, ty) * m;
    }

    /// <summary>Picture texels to 1920 x 1080 frame pixels.</summary>
    public static Affine FrameMatrix(LayerRecord[] recs, int l, IPictureCatalog cat, (float X, float Y) extra = default, (float X, float Y) positionShift = default) =>
        CanvasToFrame() * LayerMatrix(recs, l, cat, extra, positionShift);
}

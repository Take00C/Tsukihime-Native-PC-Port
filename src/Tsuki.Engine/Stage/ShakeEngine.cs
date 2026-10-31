using Tsuki.Engine.Contracts;
using static Tsuki.Engine.Stage.StageMath;

namespace Tsuki.Engine.Stage;

/// <summary>Arguments of 0x84670(layer, params, dur_ms, fade_ms, kind).</summary>
/// <param name="Layer">Target layer 0..39.</param>
/// <param name="Kind">1 quake, 2 shake, 3 swing.</param>
/// <param name="Par">The six parameters p0..p5.</param>
/// <param name="DurMs">Duration in ms, -1 until stopped.</param>
/// <param name="FadeMs">Start delay in ms.</param>
public readonly record struct SlotStart(int Layer, int Kind, int[] Par, int DurMs, int FadeMs);

/// <summary>
/// The layer shake slots of the drawn records and their per-frame update (VA 0x84670 start, 0x847F0 stop, 0x4A770 reset, 0x57F4D..0x5889A
/// update, 0x51A22..0x51B71 and 0x516DE..0x51746 draw terms; docs/formats/quake.md 3-5). Port of <c>ShakeLayers</c> of <c>tsuki.quake</c>,
/// working on the <see cref="LayerRecord.Slots"/> of the drawn array. The quake fixes of the review are in: <see cref="ResetLayer"/> runs for
/// <c>_STCH</c> and <c>_SCH2</c> alike, and <see cref="MaskOffset"/> is the second draw path of masked layers through 0x49A60.
/// </summary>
public sealed class ShakeEngine
{
    private readonly LayerRecord[] _a;
    private readonly Func<int, bool> _noShock;
    private readonly SceneState? _scene;

    /// <summary>Creates the engine over the 40 drawn records.</summary>
    /// <param name="drawn">The drawn array A.</param>
    /// <param name="rng">The shared <c>rand()</c>.</param>
    /// <param name="rnd2">The flicker generator.</param>
    /// <param name="scene">The script-side scene state whose shake mirror (kind, delay, time, parameters) the update writes: the delay countdown and the clearing of a finished slot.</param>
    /// <param name="noShock">True for a picture id whose catalog draw_flags has bit 1 (the seven colour fills): they ignore the screen shock.</param>
    public ShakeEngine(LayerRecord[] drawn, Ps4Rand rng, Rnd2 rnd2, Func<int, bool>? noShock = null, SceneState? scene = null)
    {
        ArgumentNullException.ThrowIfNull(drawn);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rnd2);
        _a = drawn;
        Rng = rng;
        Rnd2 = rnd2;
        _noShock = noShock ?? (_ => false);
        _scene = scene;
    }

    /// <summary>The shared <c>rand()</c>.</summary>
    public Ps4Rand Rng { get; }

    /// <summary>The flicker generator.</summary>
    public Rnd2 Rnd2 { get; }

    /// <summary>Word 0x164B998: set by every start.</summary>
    public bool SceneDirty { get; set; }

    private static int SlotOfKind(int kind) => kind == 1 ? 0 : 1;

    /// <summary>0x84670: stores a slot; ignored for a layer outside 0..39 and a kind outside 1..3.</summary>
    public void Start(in SlotStart st)
    {
        if (st.Layer is < 0 or > 39 || st.Kind is < 1 or > 3)
        {
            return;
        }

        ShakeSlot s = _a[st.Layer].Slots[SlotOfKind(st.Kind)];
        s.Kind = st.Kind;
        s.Delay = I16(MsToFrames(st.FadeMs));
        s.Time = st.DurMs >= 0 ? I16(MsToFrames(st.DurMs)) : -1;
        for (int i = 0; i < 6; i++)
        {
            s.Par[i] = I16(st.Par[i]);
        }

        s.Elapsed = 0;
        s.Phase = 0;
        s.X = 0f;
        s.Y = 0f;
        Array.Clear(s.W);
        SceneDirty = true;
    }

    /// <summary>0x847F0(layer, kind): clears the kind's slot; layer below 0 = all 40; above 39 nothing. <c>_SHS2</c> and <c>_SSS2</c> both clear slot 1.</summary>
    public void Stop(int layer, int kind)
    {
        if (layer > 39 || kind is < 1 or > 3)
        {
            return;
        }

        int slot = SlotOfKind(kind);
        if (layer < 0)
        {
            for (int l = 0; l < 40; l++)
            {
                _a[l].Slots[slot].Clear();
            }
        }
        else
        {
            _a[layer].Slots[slot].Clear();
        }
    }

    /// <summary>
    /// Every <c>_STCH</c> and <c>_SCH2</c> with layer 0..39 resets the drawn record at the statement itself: the backend 0x81760 calls
    /// 0x4A770(layer) on both paths (VA 0x81A9C for STCH, 0x81ABB for SCH2), which zeroes both slots (+0x178..+0x1CF).
    /// </summary>
    public void ResetLayer(int layer)
    {
        if (layer is >= 0 and <= 39)
        {
            _a[layer].Slots[0].Clear();
            _a[layer].Slots[1].Clear();
        }
    }

    /// <summary>Layer-array initialisation 0x57590 (ADV-mode entry, scan load): every slot of every layer is cleared.</summary>
    public void AdvStart()
    {
        foreach (LayerRecord r in _a)
        {
            r.Slots[0].Clear();
            r.Slots[1].Clear();
        }
    }

    /// <summary>
    /// B to A transfer of the slots (VA 0x595B2..0x59779, first update after a commit): a committed slot replaces the drawn one only when its
    /// kind is non-zero and differs from the running kind. Returns, per slot, whether it was copied (the engine then clears B's kind).
    /// </summary>
    public void MergeCommitted(int layer, LayerRecord committed)
    {
        for (int s = 0; s < 2; s++)
        {
            ShakeSlot b = committed.Slots[s];
            ShakeSlot a = _a[layer].Slots[s];
            if (b.Kind != a.Kind && b.Kind != 0)
            {
                a.CopyFrom(b);
                b.Kind = 0;
            }
        }
    }

    /// <summary>One call of the layer update with <paramref name="dt"/> frames: layers 0..39 in index order, slot 0 then slot 1.</summary>
    public void Update(int dt)
    {
        float dtf = dt;
        for (int l = 0; l < 40; l++)
        {
            for (int si = 0; si < 2; si++)
            {
                UpdateSlot(l, si, dtf);
            }
        }
    }

    /// <summary>The slot update of one layer (both slots) with <paramref name="dtf"/> frames, as the per-layer loop of 0x57980 runs it.</summary>
    public void UpdateLayer(int l, float dtf)
    {
        for (int si = 0; si < 2; si++)
        {
            UpdateSlot(l, si, dtf);
        }
    }

    private void UpdateSlot(int l, int si, float dtf)
    {
        ShakeSlot s = _a[l].Slots[si];
        if (s.Delay > 0)
        {
            int v = Cvtt(F(s.Delay - dtf));
            s.Delay = I16(v) > 0 ? I16(v) : 0;
            _scene?.SetShakeFade(l, si, (short)s.Delay);
            return;
        }

        if (s.Time == 0)
        {
            return;
        }

        int el = Cvtt(F(s.Elapsed + dtf));
        if (I16(el) >= 30000)
        {
            el = 30000;
        }

        s.Elapsed = I16(el);
        if (s.Time > 0 && s.Time < s.Elapsed)
        {
            s.Clear();
            if (_scene is { } sc)
            {
                sc.SetShakeKind(l, si, 0);
                sc.SetShakeFade(l, si, 0);
                sc.SetShakeTime(l, si, 0);
                for (int i = 0; i < 6; i++)
                {
                    sc.SetShakePar(l, (6 * si) + i, 0);
                }
            }
        }

        switch (s.Kind)
        {
            case 1:
                Quake(l, si, s, dtf);
                break;
            case 2:
                Shake(s, dtf);
                break;
            case 3:
                Swing(s, dtf);
                break;
            default:
                s.X = 0f;
                s.Y = 0f;
                break;
        }
    }

    private void Quake(int l, int si, ShakeSlot s, float dtf)
    {
        short[] p = s.Par;
        s.Phase = I16(Cvtt(F(s.Phase + dtf)));
        if (p[2] > s.Phase)
        {
            return;
        }

        s.Phase = 0;
        if (l >= 2 && p[5] != 0)
        {
            float fx = 0f;
            float fy = 0f;
            for (int k = 0; k < 2; k++)
            {
                LayerRecord lk = _a[k];
                if (lk.Used != 0 && lk.ZOrder != 0)
                {
                    fx = lk.Slots[si].X;
                    fy = lk.Slots[si].Y;
                }
            }

            s.X = fx;
            s.Y = fy;
            return;
        }

        float ax = p[0];
        float ay = p[1];
        float el = s.Elapsed;
        if (p[3] > el)
        {
            float r = el / p[3];
            ax *= r;
            ay *= r;
        }
        else if (s.Time > 0)
        {
            float t0 = s.Time - (float)p[4];
            if (el > t0)
            {
                float r = 1.0f - ((el - t0) / p[4]);
                ax *= r;
                ay *= r;
            }
        }

        int r1 = Rng.Next();
        int r2 = Rng.Next();
        float ux = (float)r1 * Ps4Math.TwoPowMinus30;
        float uy = (float)r2 * Ps4Math.TwoPowMinus30;
        float vx = ax * ux;
        float vy = ay * uy;
        s.X = ((vx + vx) - ax) * Ps4Math.Third;
        s.Y = ((vy + vy) - ay) * Ps4Math.YCanvas;
    }

    private void Shake(ShakeSlot s, float dtf)
    {
        short[] p = s.Par;
        int per = p[2] != 0 ? p[2] : 60;
        int m = per != -32768 ? Math.Abs(per) & 0xFFFF : 32768;
        int e = Cvtt(F(s.Phase + dtf));
        int d;
        do
        {
            d = I16(e);
            e = I32((long)e - m);
        }
        while (m <= d);

        s.Phase = I16(m + e);
        float v = Ps4Math.Sinf((float)s.Phase * Ps4Math.TwoPi / per);
        if (p[4] > 0 && s.Elapsed < p[4])
        {
            v *= (float)s.Elapsed / p[4];
        }

        if (p[3] != 0 && s.Time > 0)
        {
            float t = s.Time;
            float e2 = s.Elapsed;
            v *= (t - (t < e2 ? t : e2)) / t;
        }

        if (p[5] >= 2)
        {
            v = Rnd2.Next() % 5 == 0 ? v * (1.0f / p[5]) : 0f;
        }

        s.X = v * p[0] * Ps4Math.Third;
        s.Y = v * p[1] * Ps4Math.YCanvas;
    }

    private float U() => (float)Rng.Next() * Ps4Math.TwoPowMinus30;

    private void Swing(ShakeSlot s, float dtf)
    {
        short[] p = s.Par;
        short[] w = s.W;
        s.Phase = I16(Cvtt(F(s.Phase + dtf)));
        if (!(w[2] >= s.Phase))
        {
            s.Phase = 0;
            w[0] = I16(Cvtt((U() * (p[1] - p[0])) + p[0]));
            w[2] = I16(Cvtt((U() * (p[3] - p[2])) + p[2]));
            if (w[4] == 0)
            {
                w[4] = U() < 0.5f ? (short)1 : (short)-1;
            }
        }

        float s1 = Ps4Math.Sinf((float)s.Phase * Ps4Math.TwoPi / w[2]);
        w[6] = I16(Cvtt(F(w[6] + dtf)));
        if (!(w[3] >= w[6]))
        {
            w[6] = 0;
            w[1] = I16(Cvtt((U() * (p[1] - p[0])) + p[0]));
            w[3] = I16(Cvtt((U() * (p[3] - p[2])) + p[2]));
            if (w[5] == 0)
            {
                w[5] = U() < 0.5f ? (short)1 : (short)-1;
            }
        }

        float p4 = p[4];
        float a = (float)w[4] * w[0] * p4 * s1;
        float b = (float)w[5] * w[1] * p4;
        float s2 = Ps4Math.Sinf((float)w[6] * Ps4Math.TwoPi / w[3]);
        s.X = a * Ps4Math.Third;
        s.Y = s2 * b * Ps4Math.YCanvas;
    }

    // -- draw terms (0x51060) ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// 0x49A60(A[m] + 0x10, axis), axis 0 = x, 1 = y: layer <paramref name="m"/>'s crop origin plus its shake outputs in 1920 x 1080 units, plus
    /// the screen shock when m has a crop rectangle and is an offscreen target. The shake outputs are read from A[m.mask] when m itself has
    /// a mask, else from m (VA 0x49A8F). <paramref name="shock"/> is (0xDF0720, 0xDF0724) when byte 0xDF07C8 is 0.
    /// </summary>
    public float MaskAxis(int m, int axis, (float X, float Y) shock, bool shakes = true)
    {
        LayerRecord mr = _a[m];
        LayerRecord src = mr.Mask >= 0 ? _a[mr.Mask] : mr;
        float sh = 0f;
        if (shakes)
        {
            foreach (ShakeSlot s in src.Slots)
            {
                if (s.Kind != 0)
                {
                    sh += axis == 0 ? s.X : s.Y;
                }
            }
        }

        sh *= axis == 0 ? 3.0f : Ps4Math.YPixels;
        float v = 0f + (sh + mr.Crop[axis]);
        bool anyCrop = mr.Crop[0] != 0 || mr.Crop[1] != 0 || mr.Crop[2] != 0 || mr.Crop[3] != 0;
        if (anyCrop && mr.Offscreen)
        {
            (float sx, float sy) = _noShock(mr.Pic) ? (0f, 0f) : shock;
            v = axis == 0 ? (sx * 3.0f) + v : (sy * Ps4Math.ShockYMask) + v;
        }

        return v;
    }

    /// <summary>
    /// The mask-layer term of layer <paramref name="l"/>'s draw (VA 0x516DE..0x5174A): when l has a mask and not the <c>_SACL</c> flag,
    /// <c>(0x49A60(mask, 0) * 640 / 1920, 0x49A60(mask, 1) * 360 / 1080)</c> is added to the layer position (so it also moves the pivot-zoom point).
    /// </summary>
    public (float X, float Y) MaskOffset(int l, (float X, float Y) shock, bool shakes = true)
    {
        LayerRecord r = _a[l];
        if (r.Mask < 0 || (r.Flags & 0x40) != 0)
        {
            return (0f, 0f);
        }

        float vx = MaskAxis(r.Mask, 0, shock, shakes);
        float vy = MaskAxis(r.Mask, 1, shock, shakes);
        return (vx * 640.0f / 1920.0f, vy * 360.0f / 1080.0f);
    }

    /// <summary>
    /// The canvas-unit translation the layer draw adds for the two slots (VA 0x51A22..0x51B71; added to the translation column, so the STZ4 pivot
    /// zoom scales it). Slot 0 of a quake follows the outermost quaking ancestor, or else the mask layer's quake.
    /// </summary>
    public (float X, float Y) SlotOffset(int l)
    {
        LayerRecord r = _a[l];
        float dx = 0f;
        float dy = 0f;
        int k0 = r.Slots[0].Kind;
        if (k0 != 0)
        {
            float ox = r.Slots[0].X;
            float oy = r.Slots[0].Y;
            if (k0 == 1)
            {
                int p = r.Parent;
                if (p >= 0)
                {
                    int guard = 0;
                    while (p >= 0 && p < 40 && guard++ < 64)
                    {
                        if (_a[p].Slots[0].Kind != 0)
                        {
                            ox = _a[p].Slots[0].X;
                            oy = _a[p].Slots[0].Y;
                        }

                        p = _a[p].Parent;
                    }
                }
                else if (r.Mask >= 0 && _a[r.Mask].Slots[0].Kind != 0)
                {
                    ox = _a[r.Mask].Slots[0].X;
                    oy = _a[r.Mask].Slots[0].Y;
                }
            }

            dx += ox;
            dy += oy;
        }

        if (r.Slots[1].Kind != 0)
        {
            dx += r.Slots[1].X;
            dy += r.Slots[1].Y;
        }

        return (dx, dy);
    }
}

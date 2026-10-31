using static Tsuki.Engine.Stage.StageMath;

namespace Tsuki.Engine.Stage;

/// <summary>
/// Keyframe interpolation and time handling of the layer motions (VA 0xBDD70 with the spline helper 0xBD5E0, the time rules of 0xBE4C0
/// and the <c>_SGMA</c> edit 0xC23B0; docs/formats/layers.md 8.2, 8.4). Port of <c>motion_value</c>, <c>_spline_ab</c>, <c>motion_frames</c>,
/// <c>motion_keytime</c>, <c>motion_found</c>, <c>edit_motion</c> of <c>tsuki.misc_layer_state_and_motions</c>; all arithmetic is float32 as
/// in the oracle.
/// </summary>
public static class MotionMath
{
    private const long Unset = MotionTable.Unset;
    private const long Hold = MotionTable.Hold;

    /// <summary>Fields 3 / 4 (x / y in script units) snapped to whole engine units (<c>_q</c>).</summary>
    private static double Quant(int f, double v)
    {
        if (f == 3)
        {
            return F(F(I16(Cvtt(F(F(v * 640.0) / 480.0))) * 480.0) / 640.0);
        }

        if (f == 4)
        {
            return F(F(I16(Cvtt(F(F(v * 480.0) / 360.0))) * 360.0) / 480.0);
        }

        return v;
    }

    private static (long Hold, long Unset) Sentinels(int f) => (f & ~8) == 0 ? (MotionTable.Hold8, MotionTable.Unset8) : (Hold, Unset);

    /// <summary>
    /// VA 0xBD5E0: the second-derivative pair (a, b) of the segment [k-1, k] of the spline through the motion's keys, or null. The engine's
    /// forward elimination subtracts <c>diag * (1/diag)</c> (= 1) where the textbook Thomas step subtracts <c>rhs * (1/diag)</c>; the port keeps that.
    /// </summary>
    private static (double A, double B)? SplineAb(MotionTable t, int f, int s, int e, int k, double cur)
    {
        if (s == k || e - s + 1 < 3)
        {
            return null;
        }

        (long hold, long unset) = Sentinels(f);

        double DefinedBack(int i)
        {
            int j = i;
            long v = t.Val(j, f);
            while ((v == hold || v == unset) && j > s)
            {
                j--;
                v = t.Val(j, f);
            }

            return v == hold || v == unset ? cur : v;
        }

        long v0 = t.Val(s, f);
        var y = new List<double> { v0 == hold || v0 == unset ? cur : v0 };
        var kmap = new Dictionary<int, int> { [0] = 0 };
        long prev = v0;
        int i2 = s + 1;
        while (i2 < e)
        {
            long raw = t.Val(i2, f);
            long nxt = t.Val(i2 + 1, f);
            if (raw == hold || (prev == raw && raw == nxt))
            {
                kmap[i2 - s] = y.Count - 1;
            }
            else
            {
                kmap[i2 - s] = y.Count;
                y.Add(DefinedBack(i2));
            }

            prev = raw;
            i2++;
        }

        long rawE = t.Val(e, f);
        if (prev != rawE && rawE != unset)
        {
            kmap[e - s] = y.Count;
            y.Add(DefinedBack(e));
        }

        int n = y.Count;
        if (n < 3)
        {
            return null;
        }

        var d = new double[n];
        for (int j = n - 1; j > 0; j--)
        {
            d[j] = F(y[j] - y[j - 1]);
        }

        var m = new double[n + 1];
        m[1] = F(d[2] - d[1]);
        var diag = new double[n];
        diag[1] = 4.0;
        double x3 = 4.0;
        for (int j = 2; j < n - 1; j++)
        {
            double w = F(1.0 / x3);
            m[j] = F(F(d[j + 1] - d[j]) - F(x3 * w));
            x3 = F(4.0 - w);
            diag[j] = x3;
        }

        m[n - 2] = F(m[n - 2] - m[n - 1]);
        for (int j = n - 1; j > 1; j--)
        {
            m[j - 1] = F(F(m[j - 1] - m[j]) / diag[j - 1]);
        }

        int idx = kmap.TryGetValue(k - s - 1, out int ix) ? ix : 0;
        return (m[idx], m[idx + 1]);
    }

    /// <summary>
    /// VA 0xBDD70: value of keyframe field <paramref name="f"/> at motion time <paramref name="t"/> (ms) for the keys <c>[s, e]</c>
    /// (<paramref name="e"/> inclusive), <paramref name="k"/> the first key with time at least <paramref name="t"/>, <paramref name="cur"/> the
    /// layer's current value (returned where the field is not animated), <paramref name="whole"/> motion 9999 (segment easing forced to linear).
    /// </summary>
    public static double MotionValue(MotionTable table, int f, int s, int e, int k, double t, double cur, bool whole = false)
    {
        (long hold, long unset) = Sentinels(f);
        if ((double)table.Val(s, 0) > t)
        {
            return cur;
        }

        int r = k;
        long vn;
        double tn;
        while (true)
        {
            vn = table.Val(r, f);
            tn = table.Val(r, 0);
            if (vn == hold && r < e)
            {
                r++;
                continue;
            }

            break;
        }

        double tp = 0.0;
        long vp;
        if (k != s && !(t >= tn))
        {
            int j = k;
            while (true)
            {
                j--;
                vp = table.Val(j, f);
                if (vp == hold && j > s)
                {
                    continue;
                }

                break;
            }

            tp = table.Val(j, 0);
            if (vn != unset && vp == unset && j > s)
            {
                int i = j;
                while (true)
                {
                    i--;
                    vp = table.Val(i, f);
                    if ((vp != hold && vp != unset) || i <= s)
                    {
                        break;
                    }
                }
            }
        }
        else if (r < s)
        {
            vp = 0;
        }
        else
        {
            int i = r;
            while (true)
            {
                vp = table.Val(i, f);
                if ((vp != hold && vp != unset) || i <= s)
                {
                    break;
                }

                i--;
            }
        }

        if (vn == unset)
        {
            int j = k;
            long v;
            while (true)
            {
                v = table.Val(j, f);
                if ((v != hold && v != unset) || j <= s)
                {
                    break;
                }

                j--;
            }

            return v == unset || v == hold ? cur : Quant(f, v);
        }

        if (vp == unset)
        {
            return cur;
        }

        if (vn == hold)
        {
            for (int j = e - 1; j >= s; j--)
            {
                long v = table.Val(j, f);
                if (v != hold)
                {
                    return v;
                }
            }

            return cur;
        }

        if (vp == hold)
        {
            return cur;
        }

        double p;
        if (tn > tp)
        {
            long eid;
            if (whole)
            {
                eid = 0;
            }
            else if (k == s)
            {
                eid = table.Val(s, 1);
            }
            else
            {
                eid = Unset;
                int j = k;
                while (j > s)
                {
                    j--;
                    if (table.Val(j, f) == hold)
                    {
                        continue;
                    }

                    eid = table.Val(j, 1);
                    if (eid != Unset && eid != Hold)
                    {
                        break;
                    }
                }
            }

            if (eid == Unset || eid == Hold)
            {
                eid = 0;
            }

            p = F(Easing.Ease((int)eid, F(t - tp), 0.0, 1.0, F(tn - tp)));
        }
        else
        {
            p = 1.0;
        }

        if ((f >= 17 && f <= 27) || f == 15)
        {
            return vp;
        }

        double vnF = Quant(f, vn);
        double vpF = Quant(f, vp);
        long it;
        if (k == s)
        {
            it = table.Val(s, 2);
        }
        else if (k > s)
        {
            int j = k;
            while (true)
            {
                j--;
                it = table.Val(j, 2);
                if ((it != Unset && it != Hold) || j <= s)
                {
                    break;
                }
            }
        }
        else
        {
            it = 0;
        }

        if (it == 2)
        {
            return vpF;
        }

        if (it == 1 && SplineAb(table, f, s, e, k, cur) is { } ab)
        {
            double x = F(F(F(F(ab.B - ab.A) * p) + F(ab.A * 3.0)) * p);
            x = F(x + F(F(vnF - vpF) - F(F(ab.A + ab.A) + ab.B)));
            return F(F(x * p) + vpF);
        }

        return F(F(F(vnF - vpF) * p) + vpF);
    }

    /// <summary>
    /// VA 0xBA8B0: the record's frame counter quantised by the STMA divisor (a3, signed i16): <c>frames - frames % n</c> (C remainder) for
    /// <c>n &gt;= 2</c>, else <c>frames</c>. The motion keeps its speed and advances in steps of n frames.
    /// </summary>
    public static long MotionFrames(long frames, int divisor) => divisor >= 2 ? frames - (frames % divisor) : frames;

    /// <summary>VA 0xBE5A8: motion time in ms, <c>frames * 50 / 3</c> in float32.</summary>
    public static double MotionTimeMs(long frames, int divisor) => F(F((float)MotionFrames(frames, divisor) * 50.0) / 3.0);

    /// <summary>
    /// The time used for the keys (VA 0xBE5D3..0xBE63E): null before the first key (nothing applied); clamped to the duration, or wrapped for
    /// looping motions (<paramref name="loops"/> = VA 0xC1C70(id), 0 = no loop).
    /// </summary>
    public static double? KeyTime(MotionTable table, MotionRef m, double t, int loops)
    {
        if ((double)table.Val(m.Start, 0) > t)
        {
            return null;
        }

        double dur = m.Duration;
        if (t > dur)
        {
            if (loops == 0)
            {
                return dur;
            }

            long span = m.Duration >= 3 || m.Duration > loops ? m.Duration : 2;
            long lo = m.Duration > loops ? loops : 1;
            if (t >= span)
            {
                long it = Cvtt64(t);
                double frac = F(t - it);
                if (lo == 1)
                {
                    return F((it % span) + frac);
                }

                return F((((it - lo) % (span - lo)) + lo) + frac);
            }
        }

        return t;
    }

    /// <summary>
    /// The found-scan (VA 0xBE770..0xBE7D0): true if some key with a non-zero time defines field <paramref name="f"/> (any value but
    /// -32767 / -100001; HOLD counts), or the current key <paramref name="k"/> has time 0.
    /// </summary>
    public static bool Found(MotionTable table, MotionRef m, int k, int f)
    {
        bool found = false;
        for (int i = m.Start; i < m.End; i++)
        {
            if (table.Val(i, 0) == 0)
            {
                found = found || i == k;
                continue;
            }

            long v = table.Val(i, f);
            if (v != MotionTable.Unset && v != MotionTable.Unset8)
            {
                return true;
            }
        }

        return found;
    }

    /// <summary>
    /// <c>_SGMA(l, id, key)</c> (VA 0xC23B0): writes the layer's drawn state into keyframe <paramref name="key"/> of motion <paramref name="id"/> in
    /// the session's table. Returns the keyframe index written, or null when the statement does nothing.
    /// </summary>
    public static int? EditMotion(MotionTable table, LayerRecord rec, int id, int key)
    {
        if (id <= 0)
        {
            return null;
        }

        int k = I16(id - 1);
        int first = k >= 1 && k <= table.MotionCount ? table.Get(k)!.Value.End : 0;
        int i = I32((long)first + key);
        if (i < 0 || i >= table.KeyCount)
        {
            return null;
        }

        static int Away(int v, int d)
        {
            int q = v / d;
            return v - (q * d) == 0 ? q : q + (v >= 0 ? 1 : -1);
        }

        int xi = Cvtt(F(rec.X * 480.0f));
        int yi = Cvtt(F(rec.Y * 360.0f));
        table.SetVal(i, 3, I16(Away(xi, 640)));
        table.SetVal(i, 4, I16(Away(yi, 480)));
        table.SetVal(i, 5, I16(Cvtt(F(F(rec.Alpha + rec.AlphaCur) * 255.0f))));
        table.SetVal(i, 8, rec.Rot);
        table.SetVal(i, 9, I16(rec.ZoomX));
        table.SetVal(i, 10, I16(rec.ZoomY));
        if (rec.Crop[0] != 0)
        {
            for (int j = 0; j < 4; j++)
            {
                table.SetVal(i, 11 + j, I16(rec.Crop[j]));
            }
        }

        return i;
    }
}

using static Tsuki.Engine.Stage.StageMath;

namespace Tsuki.Engine.Stage;

/// <summary>
/// The easing functions of the layer tweens and motions (VA 0xBC460; the tween copy 0x48220 is identical; docs/formats/layers.md 8.5).
/// Port of <c>tsuki.misc_layer_state_and_motions.ease</c>. Ids 2..41 are the Penner families (quad, cubic, quart, quint, sine, circ, expo,
/// elastic, back, bounce) in the variants in, out, in-out, out-in; every other id is linear. The ids in <see cref="Exact"/> follow the
/// engine's float32 / float64 operation order bit for bit; the others use float64 formulas (relative error below 2.2e-6).
/// </summary>
public static class Easing
{
    private static readonly string[] Families = ["quad", "cubic", "quart", "quint", "sine", "circ", "expo", "elastic", "back", "bounce"];

    private static readonly HashSet<int> ExactIds =
        [2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 14, 15, 18, 19, 20, 26, 27, 34, 35];

    /// <summary>The ids ported bit-exactly (and linear, which is every id outside 2..41).</summary>
    public static IReadOnlyCollection<int> Exact => ExactIds;

    /// <summary>Easing function <paramref name="id"/> (Penner form: time t, begin b, change c, duration d).</summary>
    public static double Ease(int id, double t, double b, double c, double d)
    {
        if (id is >= 10 and <= 41 && !ExactIds.Contains(id))
        {
            string fam = Families[(id - 2) / 4];
            return ((id - 2) % 4) switch
            {
                0 => EaseIn(fam, t, b, c, d),
                1 => EaseOut(fam, t, b, c, d),
                2 => EaseInOut(fam, t, b, c, d),
                _ => EaseOutIn(fam, t, b, c, d),
            };
        }

        return EaseExact(id, t, b, c, d);
    }

    private static double BounceOut(double t, double b, double c, double d)
    {
        t /= d;
        if (t < 1 / 2.75)
        {
            return (c * (7.5625 * t * t)) + b;
        }

        if (t < 2 / 2.75)
        {
            t -= 1.5 / 2.75;
            return (c * ((7.5625 * t * t) + 0.75)) + b;
        }

        if (t < 2.5 / 2.75)
        {
            t -= 2.25 / 2.75;
            return (c * ((7.5625 * t * t) + 0.9375)) + b;
        }

        t -= 2.625 / 2.75;
        return (c * ((7.5625 * t * t) + 0.984375)) + b;
    }

    private static double EaseIn(string fam, double t, double b, double c, double d)
    {
        switch (fam)
        {
            case "quad":
                t /= d;
                return (c * t * t) + b;
            case "cubic":
                t /= d;
                return (c * t * t * t) + b;
            case "quart":
                t /= d;
                return (c * Math.Pow(t, 4)) + b;
            case "quint":
                t /= d;
                return (c * Math.Pow(t, 5)) + b;
            case "sine":
                return (-c * Math.Cos(t / d * (Math.PI / 2))) + c + b;
            case "expo":
                return t == 0 ? b : (c * Math.Pow(2, 10 * ((t / d) - 1))) + b - (c * 0.001);
            case "circ":
                t /= d;
                return (-c * (Math.Sqrt(Math.Max(0.0, 1 - (t * t))) - 1)) + b;
            case "elastic":
            {
                if (t == 0)
                {
                    return b;
                }

                t /= d;
                if (t == 1)
                {
                    return b + c;
                }

                double p = d * 0.3;
                double s = p / 4;
                t -= 1;
                return -(c * Math.Pow(2, 10 * t) * Math.Sin((((t * d) - s) * (2 * Math.PI)) / p)) + b;
            }

            case "back":
            {
                double s = 1.70158;
                t /= d;
                return (c * t * t * (((s + 1) * t) - s)) + b;
            }

            case "bounce":
                return c - BounceOut(d - t, 0, c, d) + b;
            default:
                throw new ArgumentException(fam);
        }
    }

    private static double EaseOut(string fam, double t, double b, double c, double d)
    {
        switch (fam)
        {
            case "quad":
                t /= d;
                return (-c * t * (t - 2)) + b;
            case "cubic":
                t = (t / d) - 1;
                return (c * ((t * t * t) + 1)) + b;
            case "quart":
                t = (t / d) - 1;
                return (-c * (Math.Pow(t, 4) - 1)) + b;
            case "quint":
                t = (t / d) - 1;
                return (c * (Math.Pow(t, 5) + 1)) + b;
            case "sine":
                return (c * Math.Sin(t / d * (Math.PI / 2))) + b;
            case "expo":
                return t == d ? b + c : (c * 1.001 * (-Math.Pow(2, -10 * t / d) + 1)) + b;
            case "circ":
                t = (t / d) - 1;
                return (c * Math.Sqrt(Math.Max(0.0, 1 - (t * t)))) + b;
            case "elastic":
            {
                if (t == 0)
                {
                    return b;
                }

                t /= d;
                if (t == 1)
                {
                    return b + c;
                }

                double p = d * 0.3;
                double s = p / 4;
                return (c * Math.Pow(2, -10 * t) * Math.Sin((((t * d) - s) * (2 * Math.PI)) / p)) + c + b;
            }

            case "back":
            {
                double s = 1.70158;
                t = (t / d) - 1;
                return (c * ((t * t * (((s + 1) * t) + s)) + 1)) + b;
            }

            case "bounce":
                return BounceOut(t, b, c, d);
            default:
                throw new ArgumentException(fam);
        }
    }

    private static double EaseInOut(string fam, double t, double b, double c, double d)
    {
        switch (fam)
        {
            case "expo":
                if (t == 0)
                {
                    return b;
                }

                if (t == d)
                {
                    return b + c;
                }

                t /= d / 2;
                if (t < 1)
                {
                    return (c / 2 * Math.Pow(2, 10 * (t - 1))) + b - (c * 0.0005);
                }

                t -= 1;
                return (c / 2 * 1.0005 * (-Math.Pow(2, -10 * t) + 2)) + b;
            case "elastic":
            {
                if (t == 0)
                {
                    return b;
                }

                t /= d / 2;
                if (t == 2)
                {
                    return b + c;
                }

                double p = d * (0.3 * 1.5);
                double s = p / 4;
                t -= 1;
                if (t < 0)
                {
                    return (-0.5 * (c * Math.Pow(2, 10 * t) * Math.Sin((((t * d) - s) * (2 * Math.PI)) / p))) + b;
                }

                return (c * Math.Pow(2, -10 * t) * Math.Sin((((t * d) - s) * (2 * Math.PI)) / p) * 0.5) + c + b;
            }

            case "back":
            {
                double s = 1.70158 * 1.525;
                t /= d / 2;
                if (t < 1)
                {
                    return (c / 2 * (t * t * (((s + 1) * t) - s))) + b;
                }

                t -= 2;
                return (c / 2 * ((t * t * (((s + 1) * t) + s)) + 2)) + b;
            }
        }

        if (t < d / 2)
        {
            return EaseIn(fam, t * 2, b, c / 2, d);
        }

        return EaseOut(fam, (t * 2) - d, b + (c / 2), c / 2, d);
    }

    private static double EaseOutIn(string fam, double t, double b, double c, double d)
    {
        if (t < d / 2)
        {
            return EaseOut(fam, t * 2, b, c / 2, d);
        }

        return EaseIn(fam, (t * 2) - d, b + (c / 2), c / 2, d);
    }

    /// <summary>
    /// Bit-exact port of VA 0xBC460 for linear (every id outside 2..41 except 70, which has its own jump-table case), quad / cubic / quart
    /// (ids 2..11), quint in / out (14, 15), sine in / out / in-out (18..20), expo in / out (26, 27) and back in / out (34, 35), with the
    /// engine's float32 / float64 operation order. Plain C# arithmetic on doubles is float64 like the engine's <c>*sd</c> instructions;
    /// <see cref="F"/> rounds to float32.
    /// </summary>
    public static double EaseExact(int id, double t, double b, double c, double d)
    {
        t = F(t);
        b = F(b);
        c = F(c);
        d = F(d);
        double k1 = F(2.70158);   // 0x22AB2C
        double k2 = F(1.70158);   // 0x22AB38
        double x;
        switch (id)
        {
            case 10:    // 0xBC5DF: quart in
                x = F(t / d);
                return F((Math.Pow(x, 3.0) * F(x * c)) + b);
            case 11:    // 0xBC601: quart out
                x = F(F(t / d) - 1.0);
                return F(b - (((Math.Pow(x, 3.0) * x) - 1.0) * c));
            case 14:    // 0xBC6FD: quint in
                x = F(t / d);
                return F((Math.Pow(x, 4.0) * F(x * c)) + b);
            case 15:    // 0xBC729, 0xBC7CA: quint out
                x = F(F(t / d) - 1.0);
                return F((((Math.Pow(x, 4.0) * x) + 1.0) * c) + b);
            case 18:    // 0xBC7FC, 0xBD08E: sine in
                x = F(t / d);
                return F((c - (c * Math.Cos(x * (Math.PI / 2)))) + b);
            case 19:    // 0xBC80F, 0xBC87B: sine out
                x = F(t / d);
                return F((Math.Sin(x * (Math.PI / 2)) * c) + b);
            case 20:    // 0xBC815: sine in-out
                return F(b + ((Math.Cos(t * Math.PI / d) - 1.0) * (c * -0.5)));
            case 26:    // 0xBC9A3, 0xBD366: expo in
                if (t == 0.0)
                {
                    return b;
                }

                x = F(F(F(t / d) - 1.0) * 10.0);
                return F(((c * Math.Pow(2.0, x)) + b) + (c * -0.001));
            case 27:    // 0xBC9BC, 0xBD30B: expo out
                if (t == d)
                {
                    return F(b + c);
                }

                x = F(F(t * -10.0) / d);
                return F(((1.0 - Math.Pow(2.0, x)) * (c * 1.001)) + b);
            case 34:    // 0xBCC80: back in
                x = F(t / d);
                return F(F(F(x * F(x * c)) * F(F(x * k1) - k2)) + b);
            case 35:    // 0xBCCA1: back out
                x = F(F(t / d) - 1.0);
                return F(F(c * F(F(F(x * x) * F(F(x * k1) + k2)) + 1.0)) + b);
            case 2:     // 0xBC48C
                x = F(t / d);
                return F(F(F(x * c) * x) + b);
            case 3:     // 0xBC499
                x = F(t / d);
                return F(b - F(F(x * c) * F(x - 2.0)));
            case 4:     // 0xBC4B6, 0xBCE58
                x = F(t / (d * 0.5));
                if (1.0 > x)
                {
                    return F(((c * 0.5) * (x * x)) + b);
                }

                x = F(x - 1.0);
                return F(b + ((c * -0.5) * F(F(x * F(x - 2.0)) - 1.0)));
            case 5:     // 0xBC4F2, 0xBCE91
            {
                x = F(t + t);
                if (d * 0.5 > t)
                {
                    x = F(x / d);
                    return F(F(F(F(c * -0.5) * x) * F(x - 2.0)) + b);
                }

                double b2 = F((c * 0.5) + b);
                x = F(F(x - d) / d);
                return F(F(F(F(c * 0.5) * x) * x) + b2);
            }

            case 6:     // 0xBC52D
                x = F(t / d);
                return F((F(x * c) * (x * x)) + b);
            case 7:     // 0xBC546
                x = F(F(t / d) - 1.0);
                return F((c * ((x * (x * x)) + 1.0)) + b);
            case 8:     // 0xBC558, 0xBCEC2
                x = F(t / (d * 0.5));
                if (1.0 > x)
                {
                    return F((Math.Pow(x, 3.0) * (c * 0.5)) + b);
                }

                x = F(x - 2.0);
                return F(((c * 0.5) * ((x * (x * x)) + 2.0)) + b);
            case 9:     // 0xBC590, 0xBCEEF
            {
                x = F(t + t);
                if (d * 0.5 > t)
                {
                    x = F(F(x / d) - 1.0);
                    return F((F(c * 0.5) * ((x * (x * x)) + 1.0)) + b);
                }

                double b2 = F((c * 0.5) + b);
                x = F(F(x - d) / d);
                return F((F(F(c * 0.5) * x) * (x * x)) + b2);
            }

            default:    // 0xBCE1A: linear
                return F(F(F(t * c) / d) + b);
        }
    }
}

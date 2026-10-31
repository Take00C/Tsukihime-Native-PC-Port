using static Tsuki.Engine.Stage.StageMath;

namespace Tsuki.Engine.Stage;

/// <summary>
/// The <c>rand()</c> of the game's own <c>sce_module/libc.prx</c> (rand at libc VA 0x1C290, srand at 0x1C310; docs/formats/quake.md 2.1): one
/// global 64-bit LCG, state 1 at boot. Port of <c>tsuki.quake.Ps4Rand</c>. Every random consumer of the engine (shakes, MSQK, text
/// animations, transitions, SE choice, particles) shares one instance; the title screen seeds it with <see cref="TitleSeed"/>.
/// </summary>
public sealed class Ps4Rand
{
    private const ulong Multiplier = 0x5851F42D4C957F2D;

    /// <summary>Creates the generator with <paramref name="state"/> (1 at boot).</summary>
    public Ps4Rand(ulong state = 1) => State = state;

    /// <summary>The 64-bit state.</summary>
    public ulong State { get; set; }

    /// <summary>Number of <see cref="Next"/> calls so far (for traces).</summary>
    public long Calls { get; private set; }

    /// <summary><c>srand(seed)</c>: the state becomes the zero-extended 32-bit seed.</summary>
    public void Seed(uint seed) => State = seed;

    /// <summary>Seed of the title-screen init (VA 0xAFE80): <c>minute * 256 + second</c> of the local clock.</summary>
    public static uint TitleSeed(int minute, int second) => (uint)(((minute & 0xFF) << 8) | (second & 0xFF));

    /// <summary><c>rand()</c>: 30 bits, <c>RAND_MAX = 2^30 - 1</c>.</summary>
    public int Next()
    {
        State = unchecked((State * Multiplier) + 1);
        Calls++;
        return (int)((State >> 32) & 0x3FFFFFFF);
    }
}

/// <summary>
/// The engine's second generator (VA 0xE8200, state at data VA 0xDC7BF0, initial 0x6F37): <c>s = (s * 0xF131 + 0x11E79431) mod 2^64 &gt;&gt; 1</c>;
/// the result is <c>(i64)(int)rand() + s</c>. Drives the SHK2 flicker. Port of <c>tsuki.quake.Rnd2</c>.
/// </summary>
public sealed class Rnd2
{
    /// <summary>The initial state in the eboot image.</summary>
    public const ulong InitialState = 0x6F37;

    private readonly Ps4Rand _rng;

    /// <summary>Creates the generator over the shared <paramref name="rng"/>.</summary>
    public Rnd2(Ps4Rand rng, ulong state = InitialState)
    {
        ArgumentNullException.ThrowIfNull(rng);
        _rng = rng;
        State = state;
    }

    /// <summary>The generator's own state.</summary>
    public ulong State { get; set; }

    /// <summary>One value (one <c>rand()</c> call).</summary>
    public ulong Next()
    {
        State = unchecked((State * 0xF131UL) + 0x11E79431UL) >> 1;
        return unchecked((ulong)(long)_rng.Next() + State);
    }
}

/// <summary>Helpers built on <c>rand()</c> and the libc sine of the shake code.</summary>
public static class Ps4Math
{
    /// <summary>2 pi as float32 (VA 0x1A72AC).</summary>
    public static readonly float TwoPi = BitConverter.Int32BitsToSingle(0x40C90FDB);

    /// <summary>1920-pixel units to canvas x (VA 0x1A72B0).</summary>
    public static readonly float Third = BitConverter.Int32BitsToSingle(0x3EAAAAAB);

    /// <summary>1080-pixel units to canvas y, 362 / 1080 (VA 0x1A72B4).</summary>
    public static readonly float YCanvas = BitConverter.Int32BitsToSingle(0x3EAB9D65);

    /// <summary>2^-30: rand() to [0, 1) (VA 0x1A72A4).</summary>
    public static readonly float TwoPowMinus30 = BitConverter.Int32BitsToSingle(0x30800000);

    /// <summary>Canvas y to 1080-pixel units, 1080 / 362 (VA 0x1A6DB4).</summary>
    public static readonly float YPixels = BitConverter.Int32BitsToSingle(0x403EF071);

    /// <summary>The screen shock's y factor in the mask term, 1080 / 448 (VA 0x1A6DBC).</summary>
    public static readonly float ShockYMask = BitConverter.Int32BitsToSingle(0x401A4925);

    /// <summary>
    /// 0xC2F90 <c>rnd_range(a, b)</c> (MSQK): <c>lo + (rand() % (trunc((hi - lo) * 2^20) + 1)) * 2^-20</c>, one rand call.
    /// </summary>
    public static float RndRange(Ps4Rand rng, float a, float b)
    {
        float hi = a > b ? a : b;
        float lo = b < a ? b : a;
        int r = rng.Next();
        int n = unchecked(Cvtt(F(hi - lo) * 1048576.0f) + 1);
        if (n == 0)
        {
            throw new DivideByZeroException("0xC2F90 range overflow (idiv by 0)");
        }

        int rem = (int)((long)r % n);
        return lo + (rem * 9.5367431640625e-07f);
    }

    // libc.prx sinf (libc VA 0x4AF10, 787 bytes; NID Q4rRL34CEeE): FreeBSD msun s_sinf.c / k_sinf.c / k_cosf.c / e_rem_pio2f.c (BSD licence,
    // Sun Microsystems 1993 / Bruce D. Evans) with double kernels, checked against the disassembly (quake.md 2.5). Constants are the IEEE
    // doubles read from libc.prx data 0xD9290..0xD9360.
    private static readonly double S1 = BitConverter.Int64BitsToDouble(unchecked((long)0xBFC5555554CBAC77UL));
    private static readonly double S2 = BitConverter.Int64BitsToDouble(0x3F811110896EFBB2L);
    private static readonly double S3 = BitConverter.Int64BitsToDouble(unchecked((long)0xBF2A00F9E2CAE774UL));
    private static readonly double S4 = BitConverter.Int64BitsToDouble(0x3EC6CD878C3B46A7L);
    private static readonly double C0 = BitConverter.Int64BitsToDouble(unchecked((long)0xBFDFFFFFFD0C5E81UL));
    private static readonly double C1 = BitConverter.Int64BitsToDouble(0x3FA55553E1053A42L);
    private static readonly double C2 = BitConverter.Int64BitsToDouble(unchecked((long)0xBF56C087E80F1E27UL));
    private static readonly double C3 = BitConverter.Int64BitsToDouble(0x3EF99342E0EE5069L);
    private static readonly double Pio2One = BitConverter.Int64BitsToDouble(0x3FF921FB54442D18L);
    private static readonly double Pio2Two = BitConverter.Int64BitsToDouble(0x400921FB54442D18L);
    private static readonly double Pio2Three = BitConverter.Int64BitsToDouble(0x4012D97C7F3321D2L);
    private static readonly double Pio2Four = BitConverter.Int64BitsToDouble(0x401921FB54442D18L);
    private static readonly double InvPio2 = BitConverter.Int64BitsToDouble(0x3FE45F306DC9C883L);
    private static readonly double ToInt = BitConverter.Int64BitsToDouble(0x4338000000000000L);
    private static readonly double RPio2One = BitConverter.Int64BitsToDouble(0x3FF921FB50000000L);
    private static readonly double RPio2OneT = BitConverter.Int64BitsToDouble(0x3E5110B4611A6263L);

    private static double KSin(double x)
    {
        double z = x * x;
        double w = z * z;
        double r = S3 + (z * S4);
        double s = z * x;
        return (x + (s * (S1 + (z * S2)))) + ((s * w) * r);
    }

    private static double KCos(double x)
    {
        double z = x * x;
        double w = z * z;
        double r = C2 + (z * C3);
        return ((1.0 + (z * C0)) + (w * C1)) + ((z * w) * r);
    }

    /// <summary>
    /// <c>sinf</c> of the game's libc.prx for a float32 argument, bit-exact (<c>tsuki.quake.ps4_sinf</c>). It differs from a correctly rounded
    /// float sine by one ulp for some arguments, so <c>MathF.Sin</c> is not used. Arguments above 4.2e8 radians (the engine's
    /// <c>__kernel_rem_pio2</c> path; not reachable from any shake) fall back to a correctly rounded sine.
    /// </summary>
    public static float Sinf(float x)
    {
        int hx = BitConverter.SingleToInt32Bits(x);
        int ix = hx & 0x7FFFFFFF;
        if (ix <= 0x3F490FDA)
        {
            if (ix < 0x39800000 && Cvtt(x) == 0)
            {
                return x;
            }

            return (float)KSin(x);
        }

        if (ix <= 0x407B53D1)
        {
            if (ix <= 0x4016CBE3)
            {
                return hx > 0 ? (float)KCos(x - Pio2One) : -(float)KCos(x + Pio2One);
            }

            return (float)KSin((hx > 0 ? Pio2Two : -Pio2Two) - x);
        }

        if (ix <= 0x40E231D5)
        {
            if (ix <= 0x40AFEDDF)
            {
                return hx > 0 ? -(float)KCos(x - Pio2Three) : (float)KCos(x + Pio2Three);
            }

            return (float)KSin(x + (hx > 0 ? -Pio2Four : Pio2Four));
        }

        if (ix >= 0x7F800000)
        {
            return x - x;
        }

        if (ix > 0x4DC90FDA)
        {
            return (float)Math.Sin(x);
        }

        double fn = ((x * InvPio2) + ToInt) - ToInt;
        int n = (int)fn;
        double y = (x + (fn * -RPio2One)) + (fn * -RPio2OneT);
        switch (n & 3)
        {
            case 0:
                return (float)KSin(y);
            case 1:
                return (float)KCos(y);
            case 2:
                return (float)KSin(-y);
            default:
                return -(float)KCos(y);
        }
    }
}

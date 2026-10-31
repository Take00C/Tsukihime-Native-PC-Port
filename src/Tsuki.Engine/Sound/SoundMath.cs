namespace Tsuki.Engine.Sound;

/// <summary>
/// The scalar helpers of the sound code, byte-exact ports of the helpers of <c>tsuki.misc_audio_runtime_semantics</c>: the engine's
/// integer wraps, the SSE <c>cvttss2si</c> / <c>minss</c> / <c>maxss</c> semantics and the ramps. Every float operation is IEEE
/// float32 in the engine's order (no fused multiply-add: .NET never fuses implicitly).
/// </summary>
internal static class SoundMath
{
    /// <summary>Per-frame constants (data VAs 0x175024.., sound_runtime.md 6.2).</summary>
    public const float RampDefault = 0.2f;        // 0x175028: the rate used when a record's rate is <= 0

    /// <summary>Fade rate of the BGM slot a new track replaces (0x175084 / 0x1750A4).</summary>
    public const float BgmReplaceFade = 4.0f;

    /// <summary>1000 (ms per second).</summary>
    public const float MsPerSecond = 1000.0f;

    /// <summary>60 (frames per second).</summary>
    public const float Fps = 60.0f;

    /// <summary>0x175024 = 1666.6666: a rate of 100 volume points in one frame jumps instead of ramping.</summary>
    public static readonly float SeJumpRate = BitConverter.Int32BitsToSingle(0x44D05555);

    /// <summary>The low 16 bits as a signed value.</summary>
    public static int I16(int v) => (short)v;

    /// <summary>The low 8 bits as a signed value.</summary>
    public static int I8(int v) => (sbyte)v;

    /// <summary>Per-frame ramp rate for <paramref name="delta"/> volume points over <paramref name="ms"/> milliseconds at 60 fps.</summary>
    public static float Rate(float delta, float ms)
    {
        float a = delta * MsPerSecond;
        float b = a / Fps;
        return b / ms;
    }

    /// <summary><c>vcvttss2si r32</c>: truncation, the integer indefinite for NaN and overflow.</summary>
    public static int Trunc(float x)
    {
        if (float.IsNaN(x) || float.IsInfinity(x) || !(x >= -2147483648.0f && x < 2147483648.0f))
        {
            return int.MinValue;
        }

        return (int)x;
    }

    /// <summary><c>minss</c>: <c>a &lt; b ? a : b</c> (the second operand wins for NaN).</summary>
    public static float MinSs(float a, float b) => a < b ? a : b;

    /// <summary><c>maxss</c>: <c>a &gt; b ? a : b</c>.</summary>
    public static float MaxSs(float a, float b) => a > b ? a : b;

    /// <summary>Moves <paramref name="cur"/> toward <paramref name="tgt"/> by <paramref name="step"/> (VA 0xAE53..0xAEA6 / 0xB1A1..0xB202).</summary>
    public static float Ramp(float cur, float tgt, float step, bool always = false)
    {
        if (!always && cur == tgt)
        {
            return cur;
        }

        float d = tgt - cur;
        float ad = MaxSs(-d, d);
        if (step > ad)
        {
            return tgt;
        }

        if (cur > tgt)
        {
            return cur - step;
        }

        return step + cur;
    }

    /// <summary>The pan / third-parameter sweep (VA 0xB34D..0xB3B4): the new value and the new rate.</summary>
    public static (float Value, float Rate) Sweep(float cur, float tgt, float rate, float dt)
    {
        float p = (rate * dt) + cur;
        if (rate < 0)
        {
            if (tgt >= p)
            {
                return (tgt, 0f);
            }

            return (p, rate);
        }

        if (p >= tgt)
        {
            return (tgt, 0f);
        }

        return (p, rate);
    }

    /// <summary>The <c>`018</c> pan value (0 left .. 64 centre .. 128 right) as a pan -1000..1000 (VA 0x89970).</summary>
    public static int PanFromTag(int v)
    {
        int x = (v * 1000) - 64000;
        return x < 0 ? x / 64 : x >> 6;
    }

    /// <summary>The bits of a float32.</summary>
    public static int Bits(float v) => BitConverter.SingleToInt32Bits(v);
}

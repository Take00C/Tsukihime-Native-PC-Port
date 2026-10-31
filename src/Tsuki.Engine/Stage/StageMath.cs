using System.Runtime.CompilerServices;

namespace Tsuki.Engine.Stage;

/// <summary>
/// Numeric helpers of the layer model. The PS4 engine computes in SSE float32 with one rounding per operation and converts with
/// <c>cvttss2si</c>; the oracle (<c>tsuki.misc_layer_state_and_motions</c>, <c>tsuki.quake</c>) reproduces that by rounding to float32
/// after every operation. C# <see cref="float"/> arithmetic is IEEE single on x64 (no automatic fused multiply-add), so
/// <c>a * b + c</c> on floats equals the oracle's <c>f32(f32(a * b) + c)</c>; operations the oracle runs in float64 use <see cref="double"/>.
/// </summary>
internal static class StageMath
{
    /// <summary>Rounds to float32 (the oracle's <c>_f32</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float F(double x) => (float)x;

    /// <summary><c>cvttss2si</c> to a 32-bit register: truncate toward zero; NaN and out of range give INT_MIN.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Cvtt(float x) => float.IsNaN(x) || !(x >= -2147483648f && x < 2147483648f) ? int.MinValue : (int)x;

    /// <summary>The same for a double (the oracle's <c>int(x)</c> / <c>cvtt</c> of a Python float).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Cvtt(double x) => double.IsNaN(x) || !(x >= -2147483648.0 && x < 2147483648.0) ? int.MinValue : (int)x;

    /// <summary>Truncation toward zero to 64 bits (<c>cvttss2si</c> with a 64-bit destination).</summary>
    public static long Cvtt64(double x) => double.IsNaN(x) || !(x >= -9223372036854775808.0 && x < 9223372036854775808.0) ? long.MinValue : (long)x;

    /// <summary>Wraps to a signed 16-bit value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short I16(long v) => unchecked((short)v);

    /// <summary>Wraps to a signed 8-bit value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static sbyte I8(long v) => unchecked((sbyte)v);

    /// <summary>Wraps to a signed 32-bit value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int I32(long v) => unchecked((int)v);

    /// <summary>C integer division (truncates toward zero) on 64-bit operands, result wrapped to 32 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CDiv(long a, long b) => unchecked((int)(a / b));

    /// <summary><c>trunc(ms * 60 / 1000)</c> frames of STGS / STZ4 / STRT / STCL (the oracle's <c>_cdiv(ms * 60, 1000)</c>).</summary>
    public static int MsToFrames60(int ms) => CDiv((long)ms * 60, 1000);

    /// <summary><c>trunc(3 * ms / 50)</c> frames of SQK2 / SHK2 / SSW2 / STRA (32-bit multiply, VA 0x846B6).</summary>
    public static int MsToFrames(int ms) => CDiv(unchecked(3 * ms), 50);

    /// <summary>STCH x to engine units: <c>trunc(x * 640 / 480)</c> (VA 0x82941).</summary>
    public static int ScriptX(int x) => CDiv((long)x * 640, 480);

    /// <summary>STCH y to engine units: <c>trunc(y * 480 / 360)</c> (VA 0x829A6).</summary>
    public static int ScriptY(int y) => CDiv((long)y * 480, 360);

    /// <summary>STCH a19 / STRR: <c>v % 36000</c> (C remainder) moved into [-18000, 18000] (VA 0x821DF..0x82215).</summary>
    public static int WrapAngle(int v)
    {
        int r = v % 36000;
        if (r <= -18001)
        {
            r += 36000;
        }
        else if (r >= 18001)
        {
            r -= 36000;
        }

        return r;
    }

    /// <summary>The u8 of a colour channel.</summary>
    public static byte B(int v) => unchecked((byte)v);
}

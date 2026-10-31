// The _STEC / _SNX2 / _SNX3 recolour 0xD36F0 (docs/formats/compositing.md 9; tools/tsuki/misc_compositing_transitions_shaders.py stec_apply_bgra,
// bit-exact against the engine under emulation: 756 cases). It runs on the CPU over the decoded pixels while a picture tile's texture is built;
// here the pixels are RGBA (the engine's call sites swap bytes 0 and 2 around the BGRA kernel). Alpha is never changed.
using Tsuki.Engine.Stage;

namespace Tsuki.Render.Grade;

/// <summary>The PS4 colour-grade kernel.</summary>
public static class StecKernel
{
    /// <summary>The brightness table (SNX2, i16): <c>clamp(i + b, 0, 255)</c>, the identity for 0.</summary>
    public static void BrightnessLut(int brightness, Span<byte> lut)
    {
        short b = unchecked((short)brightness);
        for (int i = 0; i < 256; i++)
        {
            lut[i] = (byte)(b == 0 ? i : Math.Clamp(i + b, 0, 255));
        }
    }

    /// <summary>
    /// The contrast table (SNX3, i8 <c>c</c>): c &gt; 0 is 0 for i &lt; c, <c>(i - c) * 255 / (255 - 2c)</c> up to 255 - c and 255 above; c &lt; 0 is
    /// <c>trunc(i * (255 + 2c) / 255) - c</c> (low byte); the identity for 0.
    /// </summary>
    public static void ContrastLut(int contrast, Span<byte> lut)
    {
        int c = unchecked((sbyte)contrast);
        for (int i = 0; i < 256; i++)
        {
            int v;
            if (c > 0)
            {
                v = i < c ? 0 : i >= 255 - c ? 255 : (255 * (i - c)) / (255 - (2 * c));
            }
            else if (c < 0)
            {
                v = ((i * (255 + (2 * c))) / 255) - c;      // C# integer division truncates toward zero, as the engine's
            }
            else
            {
                v = i;
            }

            lut[i] = (byte)v;
        }
    }

    /// <summary>True when <see cref="Apply"/> would change pixels for <paramref name="grade"/>.</summary>
    public static bool Changes(in ColourGrade grade, StecTables tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return Plan(grade, tables, out _, out _);
    }

    /// <summary>Recolours RGBA pixels in place (<c>stec_apply_bgra</c> on swapped bytes).</summary>
    /// <param name="rgba">The pixels, 4 bytes each.</param>
    /// <param name="grade">The grade (<see cref="GradeKeys.Resolve"/> of a texture key's D).</param>
    /// <param name="tables">The eboot tables.</param>
    public static void Apply(Span<byte> rgba, in ColourGrade grade, StecTables tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (!Plan(grade, tables, out int baseMode, out int sub))
        {
            return;
        }

        int id = grade.Mode;
        Span<byte> lut1 = stackalloc byte[256];
        Span<byte> lut2 = stackalloc byte[256];
        BrightnessLut(grade.Brightness, lut1);
        ContrastLut(grade.Contrast, lut2);
        bool useBright = (sub & 0x10) != 0;
        bool useContrast = (sub & 1) != 0;
        ReadOnlySpan<byte> t0 = default, t1 = default, t2 = default;
        int wb = 0, wr = 0;
        if (baseMode is 0x100 or 0x200)
        {
            t0 = tables.Lut(id, 0);
            t1 = tables.Lut(id, 1);
            t2 = tables.Lut(id, 2);
            wb = tables.WeightBlue(id);
            wr = tables.WeightRed(id);
        }

        byte fillR = (byte)((grade.Rgb >> 16) & 0xFF), fillG = (byte)((grade.Rgb >> 8) & 0xFF), fillB = (byte)(grade.Rgb & 0xFF);
        float offset = tables.LumaOffset, divisor = tables.LumaDivisor;
        for (int p = 0; p + 3 < rgba.Length; p += 4)
        {
            int r = rgba[p], g = rgba[p + 1], b = rgba[p + 2];
            switch (baseMode)
            {
                case 0x100:
                    b = t0[b];
                    g = t1[g];
                    r = t2[r];
                    break;
                case 0x200:
                {
                    int sum = (b * wb) + (g * 0x224) + (r * wr);
                    int y = (int)(((float)sum + offset) / divisor);
                    b = t0[y];
                    g = t1[y];
                    r = t2[y];
                    break;
                }

                case 0x400:
                    (r, g, b) = (fillR, fillG, fillB);
                    break;
                default:
                    break;
            }

            if (useBright)
            {
                (r, g, b) = (lut1[r], lut1[g], lut1[b]);
            }

            if (useContrast)
            {
                (r, g, b) = (lut2[r], lut2[g], lut2[b]);
            }

            rgba[p] = (byte)r;
            rgba[p + 1] = (byte)g;
            rgba[p + 2] = (byte)b;
        }
    }

    // mode = flags | brightness bit | contrast bit; the engine does nothing for any other combination (0xD3C3B)
    private static bool Plan(in ColourGrade grade, StecTables tables, out int baseMode, out int sub)
    {
        int flags = grade.Mode is >= 1 and <= StecTables.Count ? tables.Flags(grade.Mode) : 0;
        int mode = flags | (unchecked((short)grade.Brightness) != 0 ? 0x10 : 0) | (unchecked((sbyte)grade.Contrast) != 0 ? 1 : 0);
        sub = mode & 0xFF;
        baseMode = mode & ~0xFF;
        if (baseMode == 0 && sub is not (1 or 0x10 or 0x11))
        {
            return false;
        }

        return baseMode is 0 or 0x100 or 0x200 or 0x400 && sub is 0 or 1 or 0x10 or 0x11;
    }
}

using System.Globalization;
using System.Text;
using Tsuki.Engine.Stage;

namespace Tsuki.Tests.Data.Stage;

/// <summary>
/// The canonical text of a layer record, the same strings <c>engine_stage.py</c> writes from the engine's memory (record A or B). Floats are the hex of
/// their float32 bits so that two records are equal exactly when every field is bit-identical (NaN excepted).
/// </summary>
internal static class StageTrace
{
    private static string Fb(float x) => float.IsNaN(x) ? "nan" : BitConverter.SingleToUInt32Bits(x).ToString("x8", CultureInfo.InvariantCulture);

    private static string I(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static string T(Tween t) => string.Join(',', I(t.Ease), I(t.Counter), I(t.Step));

    /// <summary>The canonical string of record A (<paramref name="committed"/> false) or B.</summary>
    public static string Canon(LayerRecord f, bool committed)
    {
        var sb = new StringBuilder(512);
        sb.Append("used:").Append(I(f.Used)).Append(" face:").Append(I(f.Face)).Append(" pic:").Append(I(f.Pic)).Append(" blend:").Append(I(f.Blend))
            .Append(" flags:").Append(I(f.Flags)).Append(" fx:").Append(I(f.FxMode)).Append(',').Append(I(f.FxA)).Append(',').Append(I(f.FxB))
            .Append(" state:").Append(I(f.State)).Append(" alpha:").Append(Fb(f.Alpha)).Append(" xy:").Append(Fb(f.X)).Append(',').Append(Fb(f.Y))
            .Append(" off:").Append(Fb(f.OffX)).Append(',').Append(Fb(f.OffY)).Append(" rot:").Append(I(f.Rot)).Append(" scale:").Append(I(f.Scale))
            .Append(" anchor:").Append(I(f.AnchorX)).Append(',').Append(I(f.AnchorY)).Append(" zoom:").Append(I(f.ZoomX)).Append(',').Append(I(f.ZoomY)).Append(',').Append(I(f.ZoomPivot))
            .Append(" mask:").Append(I(f.Mask)).Append(" parent:").Append(I(f.Parent)).Append(" sttr:").Append(I(f.Sttr))
            .Append(" soxy:").Append(I(f.SoxyX)).Append(',').Append(I(f.SoxyY)).Append(" crop:").Append(string.Join(',', f.Crop.Select(c => I(c))))
            .Append(" a:").Append(I(f.A16)).Append(',').Append(I(f.A17)).Append(',').Append(I(f.Angle)).Append(" stns:").Append(I(f.Stns)).Append(',').Append(I(f.StnsB))
            .Append(" motion:").Append(I(f.MotionId)).Append(',').Append(I(f.MotionDivisor)).Append(',').Append(I(f.MotionFrames)).Append(',').Append(I(f.MotionPause))
            .Append(" colour:").Append(Convert.ToHexString(f.Colour).ToLowerInvariant());
        if (!committed)
        {
            sb.Append(" rotd:").Append(I(f.RotDelta)).Append(',').Append(I(f.RotCur))
                .Append(" tw:").Append(T(f.TwMove)).Append(';').Append(T(f.TwScale)).Append(';').Append(T(f.TwRot)).Append(';').Append(T(f.TwAlpha)).Append(';').Append(T(f.TwZoom))
                .Append(" cur:").Append(Fb(f.AlphaCur)).Append(',').Append(Fb(f.ScaleDelta)).Append(',').Append(Fb(f.ScaleCur))
                .Append(" zd:").Append(Fb(f.ZoomDx)).Append(',').Append(Fb(f.ZoomDy))
                .Append(" md:").Append(Fb(f.MoveDx)).Append(',').Append(Fb(f.MoveDy))
                .Append(" ad:").Append(Fb(f.AlphaDelta))
                .Append(" stra:").Append(I(f.Stra)).Append(',').Append(I(f.StraPhase)).Append(',').Append(I(f.StraFadeIn)).Append(',').Append(I(f.StraFadeTotal)).Append(',').Append(I(f.StraFadeLeft));
        }

        for (int s = 0; s < 2; s++)
        {
            ShakeSlot k = f.Slots[s];
            sb.Append(" slot").Append(I(s)).Append(':').Append(I(k.Kind)).Append(',').Append(I(k.Delay)).Append(',').Append(I(k.Time)).Append(',').Append(string.Join(',', k.Par.Select(p => I(p))));
            if (!committed)
            {
                sb.Append(',').Append(I(k.Elapsed)).Append(',').Append(I(k.Phase)).Append(',').Append(Fb(k.X)).Append(',').Append(Fb(k.Y)).Append(',').Append(string.Join(',', k.W.Select(p => I(p))));
            }
        }

        return sb.ToString();
    }

    /// <summary>The first token that differs between two canonical strings, as <c>name: engine / c#</c>.</summary>
    public static string FirstDifference(string engine, string cs)
    {
        string[] a = engine.Split(' ');
        string[] b = cs.Split(' ');
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            string x = i < a.Length ? a[i] : "<none>";
            string y = i < b.Length ? b[i] : "<none>";
            if (x != y)
            {
                return $"{x} (engine) / {y} (c#)";
            }
        }

        return "equal";
    }
}

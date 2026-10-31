using System.Globalization;
using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;
using Tsuki.Engine.Vm;

namespace Tsuki.Tests.Synthetic.Stage;

/// <summary>Helpers of the stage tests: the synthetic catalog, statements from text, and the canonical strings the generator writes.</summary>
internal static class StageKit
{
    /// <summary>The synthetic picture catalog of <see cref="StageOracleCases.Pictures"/>.</summary>
    public static ArrayPictureCatalog Catalog() =>
        new(StageOracleCases.Pictures.Select(p => p.Name).ToArray(),
            StageOracleCases.Pictures.Select(p => new PictureInfo(p.Ax, p.Ay, p.Class, p.Flags, p.Face)).ToArray());

    /// <summary>No motions.</summary>
    public static IMotionTables NoMotions() => new ArrayMotionTables([], [], [], [], [], []);

    /// <summary>The synthetic motion table of <see cref="StageOracleCases.MotionHeader"/> / <see cref="StageOracleCases.MotionKeys"/>.</summary>
    public static ArrayMotionTables Motions()
    {
        int n = StageOracleCases.MotionHeader.Length;
        long[] dur = StageOracleCases.MotionHeader.Select(h => h.Duration).ToArray();
        int[] end = StageOracleCases.MotionHeader.Select(h => h.End).ToArray();
        ulong[] masks = StageOracleCases.MotionHeader.Select(h => h.Mask).ToArray();
        int[] loops = StageOracleCases.MotionHeader.Select(h => h.Loop).ToArray();
        int keys = StageOracleCases.MotionKeys.Length;
        long[] times = StageOracleCases.MotionKeys.Select(k => k.Time).ToArray();
        long[] fields = new long[keys * MotionTable.FieldCount];
        for (int i = 0; i < keys; i++)
        {
            StageOracleCases.MotionKeys[i].Fields.CopyTo(fields, i * MotionTable.FieldCount);
        }

        _ = n;
        return new ArrayMotionTables(dur, end, times, fields, masks, loops);
    }

    /// <summary>A statement from its script text, such as <c>_STCH(7,img0597,240,136);</c>.</summary>
    public static Statement Parse(string text)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        ScriptSegment seg = ScriptSegmenter.Read(bytes, 0);
        if (!seg.HasName)
        {
            throw new ArgumentException($"no statement name in {text}", nameof(text));
        }

        return new Statement(0, seg.Start, seg.End, CommandName.FromBytes(bytes.AsSpan(seg.NameStart, 4)), bytes.AsMemory(seg.ArgsStart, seg.End - seg.ArgsStart));
    }

    /// <summary>Runs a statement and requires that it was a stage command.</summary>
    public static void Run(StageRuntime rt, string text) => Assert.True(rt.Execute(Parse(text)), text);

    private static string Fb(float x) => float.IsNaN(x) ? "nan" : BitConverter.SingleToUInt32Bits(x).ToString("x8", CultureInfo.InvariantCulture);

    private static string I(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>canon_rec</c> of the generator: the subset of a drawn record that the motion tests compare.</summary>
    public static string CanonRec(LayerRecord f) =>
        $"pic:{I(f.Pic)} face:{I(f.Face)} blend:{I(f.Blend)} fx:{I(f.FxMode)},{I(f.FxA)},{I(f.FxB)} alpha:{Fb(f.Alpha)} xy:{Fb(f.X)},{Fb(f.Y)} " +
        $"off:{Fb(f.OffX)},{Fb(f.OffY)} rot:{I(f.Rot)} scale:{I(f.Scale)} anchor:{I(f.AnchorX)},{I(f.AnchorY)} " +
        $"zoom:{I(f.ZoomX)},{I(f.ZoomY)} zd:{Fb(f.ZoomDx)},{Fb(f.ZoomDy)} tz:{I(f.TwZoom.Counter)} crop:{string.Join(',', f.Crop.Select(c => I(c)))} " +
        $"a:{I(f.A16)},{I(f.A17)},{I(f.Angle)} stns:{I(f.Stns)},{I(f.StnsB)} motion:{I(f.MotionId)},{I(f.MotionDivisor)},{I(f.MotionFrames)},{I(f.MotionPause)} " +
        $"colour:{Convert.ToHexString(f.Colour).ToLowerInvariant()} acur:{Fb(f.AlphaCur)} zo:{I(f.ZOrder)}";

    /// <summary><c>canon_commit</c> of the generator: the committed fields of <c>commit()</c>.</summary>
    public static string CanonCommit(LayerRecord c) =>
        $"pic:{I(c.Pic)} blend:{I(c.Blend)} fx:{I(c.FxMode)},{I(c.FxA)},{I(c.FxB)} xy:{Fb(c.X)},{Fb(c.Y)} off:{Fb(c.OffX)},{Fb(c.OffY)} " +
        $"alpha:{Fb(c.Alpha)} state:{I(c.State)} scale:{I(c.Scale)} anchor:{I(c.AnchorX)},{I(c.AnchorY)} rot:{I(c.Rot)} mask:{I(c.Mask)} parent:{I(c.Parent)} " +
        $"sttr:{I(c.Sttr)} soxy:{I(c.SoxyX)},{I(c.SoxyY)} flags:{I(c.Flags)} crop:{string.Join(',', c.Crop.Select(x => I(x)))} " +
        $"a:{I(c.A16)},{I(c.A17)},{I(c.Angle)} stns:{I(c.Stns)},{I(c.StnsB)}";

    /// <summary>Parses a record spec (<c>k=v ...</c>, floats as <c>0xXXXXXXXX</c> bits) of the projection cases.</summary>
    public static LayerRecord RecordFromSpec(string spec)
    {
        var r = new LayerRecord();
        foreach (string token in spec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=', StringComparison.Ordinal);
            string k = token[..eq];
            string v = token[(eq + 1)..];
            float F() => BitConverter.UInt32BitsToSingle(uint.Parse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            int Int() => int.Parse(v, CultureInfo.InvariantCulture);
            Tween Tw()
            {
                string[] p = v.Split(',');
                return new Tween(short.Parse(p[0], CultureInfo.InvariantCulture), short.Parse(p[1], CultureInfo.InvariantCulture), short.Parse(p[2], CultureInfo.InvariantCulture));
            }

            switch (k)
            {
                case "used": r.Used = Int(); break;
                case "pic": r.Pic = Int(); break;
                case "x": r.X = F(); break;
                case "y": r.Y = F(); break;
                case "off_x": r.OffX = F(); break;
                case "off_y": r.OffY = F(); break;
                case "anchor_x": r.AnchorX = (short)Int(); break;
                case "anchor_y": r.AnchorY = (short)Int(); break;
                case "rot": r.Rot = Int(); break;
                case "rot_cur": r.RotCur = (short)Int(); break;
                case "scale": r.Scale = (short)Int(); break;
                case "scale_cur": r.ScaleCur = F(); break;
                case "zoom_x": r.ZoomX = (short)Int(); break;
                case "zoom_y": r.ZoomY = (short)Int(); break;
                case "zoom_pivot": r.ZoomPivot = (byte)Int(); break;
                case "zoom_dx": r.ZoomDx = F(); break;
                case "zoom_dy": r.ZoomDy = F(); break;
                case "move_dx": r.MoveDx = F(); break;
                case "move_dy": r.MoveDy = F(); break;
                case "parent": r.Parent = Int(); break;
                case "soxy_x": r.SoxyX = (sbyte)Int(); break;
                case "soxy_y": r.SoxyY = (sbyte)Int(); break;
                case "tw_zoom": r.TwZoom = Tw(); break;
                case "tw_move": r.TwMove = Tw(); break;
                default: throw new ArgumentException($"unknown record field {k}");
            }
        }

        return r;
    }
}

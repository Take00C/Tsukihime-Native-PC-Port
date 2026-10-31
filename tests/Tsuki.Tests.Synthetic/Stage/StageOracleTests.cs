using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;

namespace Tsuki.Tests.Synthetic.Stage;

/// <summary>
/// The numeric core of the stage against the Python oracle (<c>tsuki.misc_layer_state_and_motions</c>, <c>tsuki.quake</c>), through the vectors of
/// <see cref="StageOracleCases"/> (seeded, synthetic, written by <c>gen_stage_cases.py</c>): easing, the libc <c>sinf</c> and <c>rand</c>, the quake / shake /
/// swing slots, the screen shock and the message quake, the scene-state commands, the commit, the motion interpolation and its application, the projection.
/// </summary>
public sealed class StageOracleTests
{
    [Fact]
    public void EasingEqualsTheOracle()
    {
        foreach ((int id, double t, double b, double c, double d, ulong bits) in StageOracleCases.Ease)
        {
            double got = Easing.Ease(id, t, b, c, d);
            double want = BitConverter.UInt64BitsToDouble(bits);
            if (Easing.Exact.Contains(id) || id is < 2 or > 41)
            {
                Assert.True(BitConverter.DoubleToUInt64Bits(got) == bits || (double.IsNaN(got) && double.IsNaN(want)), $"ease({id}, {t}, {b}, {c}, {d}): {got} / oracle {want}");
            }
            else
            {
                double tol = 1e-9 * Math.Max(1.0, Math.Abs(want));
                Assert.True(Math.Abs(got - want) <= tol || (double.IsNaN(got) && double.IsNaN(want)) || (double.IsInfinity(got) && got == want), $"ease({id}, {t}, {b}, {c}, {d}): {got} / oracle {want}");
            }
        }
    }

    [Fact]
    public void TheLibcSinfIsBitExact()
    {
        foreach ((uint x, uint sin) in StageOracleCases.Sinf)
        {
            float arg = BitConverter.UInt32BitsToSingle(x);
            float got = Ps4Math.Sinf(arg);
            Assert.True(BitConverter.SingleToUInt32Bits(got) == sin, $"sinf({arg:R}): {BitConverter.SingleToUInt32Bits(got):X8} / oracle {sin:X8}");
        }
    }

    [Fact]
    public void TheRandomGeneratorsEqualTheOracle()
    {
        var r = new Ps4Rand();
        Assert.Equal(StageOracleCases.Rand[..6], Enumerable.Range(0, 6).Select(_ => r.Next()).ToArray());
        r.Seed(Ps4Rand.TitleSeed(12, 34));
        Assert.Equal(StageOracleCases.Rand[6..], Enumerable.Range(0, 4).Select(_ => r.Next()).ToArray());
        var r2 = new Rnd2(new Ps4Rand());
        Assert.Equal(StageOracleCases.Rnd2, Enumerable.Range(0, 5).Select(_ => r2.Next()).ToArray());
        var rg = new Ps4Rand();
        var got = new List<uint>();
        for (int i = 0; i < 5; i++)
        {
            got.Add(BitConverter.SingleToUInt32Bits(Ps4Math.RndRange(rg, -2.5f, 2.5f)));
        }

        for (int i = 0; i < 3; i++)
        {
            got.Add(BitConverter.SingleToUInt32Bits(Ps4Math.RndRange(rg, 3.1111112f, -3.1111112f)));
        }

        Assert.Equal(StageOracleCases.RndRange, got.ToArray());
        Assert.Equal(1UL, new Ps4Rand().State);
        Assert.Equal(0xC22u, Ps4Rand.TitleSeed(12, 34));
    }

    [Fact]
    public void QuakeShakeAndSwingSlotsEqualTheOracle()
    {
        foreach ((string stmt, ulong seed, ulong finalRand, var frames) in StageOracleCases.Shakes)
        {
            var rt = new StageRuntime(StageKit.Catalog(), StageKit.NoMotions(), new Ps4Rand(seed));
            StageKit.Run(rt, stmt);
            int n = 0;
            foreach ((int dt, uint x0, uint y0, uint x1, uint y1, int k0, int k1, int e0, int e1) in frames)
            {
                rt.Update(dt);
                ShakeSlot s0 = rt.Drawn[5].Slots[0];
                ShakeSlot s1 = rt.Drawn[5].Slots[1];
                string where = $"{stmt} frame {n++}";
                Assert.True(BitConverter.SingleToUInt32Bits(s0.X) == x0 && BitConverter.SingleToUInt32Bits(s0.Y) == y0, $"{where}: slot 0 {s0.X} {s0.Y}");
                Assert.True(BitConverter.SingleToUInt32Bits(s1.X) == x1 && BitConverter.SingleToUInt32Bits(s1.Y) == y1, $"{where}: slot 1 {s1.X} {s1.Y}");
                Assert.Equal((k0, k1, e0, e1), (s0.Kind, s1.Kind, s0.Elapsed, s1.Elapsed));
            }

            Assert.Equal(finalRand, rt.Rng.State);
        }
    }

    [Fact]
    public void TheScreenShockAndTheMessageQuakeEqualTheOracle()
    {
        foreach ((string stmt, var frames) in StageOracleCases.Shocks)
        {
            var rt = new StageRuntime(StageKit.Catalog(), StageKit.NoMotions());
            StageKit.Run(rt, stmt);
            int n = 0;
            foreach ((uint x, uint y) in frames)
            {
                rt.Update(1);
                Assert.True(BitConverter.SingleToUInt32Bits(rt.Shock.X) == x && BitConverter.SingleToUInt32Bits(rt.Shock.Y) == y, $"{stmt} frame {n}: {rt.Shock.X} {rt.Shock.Y}");
                n++;
            }
        }

        foreach ((string stmt, var frames) in StageOracleCases.Msqk)
        {
            var rt = new StageRuntime(StageKit.Catalog(), StageKit.NoMotions(), new Ps4Rand(7));
            StageKit.Run(rt, stmt);
            int n = 0;
            foreach ((uint x, uint y) in frames)
            {
                rt.Update(1);
                Assert.True(BitConverter.SingleToUInt32Bits(rt.MessageQuake.X) == x && BitConverter.SingleToUInt32Bits(rt.MessageQuake.Y) == y, $"{stmt} frame {n}: {rt.MessageQuake.X} {rt.MessageQuake.Y}");
                n++;
            }
        }
    }

    [Fact]
    public void TheLayerCommandsAndTheCommitEqualTheOracle()
    {
        var rt = new StageRuntime(StageKit.Catalog(), StageKit.NoMotions());
        byte[] expect = rt.Scene.Bytes.ToArray();
        int fads = 0;
        foreach ((string stmt, var patch, string[] committed) in StageOracleCases.Scene)
        {
            StageKit.Run(rt, stmt);
            foreach ((int offset, string hex) in patch)
            {
                Convert.FromHexString(hex).CopyTo(expect, offset);
            }

            if (stmt.StartsWith("_FADS", StringComparison.Ordinal))
            {
                for (int l = 0; l < committed.Length; l++)
                {
                    Assert.Equal(committed[l], StageKit.CanonCommit(rt.Committed[l]));
                }

                fads++;
            }

            ReadOnlySpan<byte> have = rt.Scene.Bytes;
            for (int i = 0; i < expect.Length; i++)
            {
                if (have[i] != expect[i])
                {
                    Assert.Fail($"{stmt}: scene byte 0x{i:X}: oracle {expect[i]:X2} / c# {have[i]:X2}");
                }
            }
        }

        Assert.True(fads > 20);
    }

    [Fact]
    public void MotionInterpolationEqualsTheOracle()
    {
        var table = new MotionTable(StageKit.Motions());
        foreach ((int mid, int f, int k, double t, double cur, ulong bits) in StageOracleCases.MotionValues)
        {
            MotionRef m = table.Get(mid)!.Value;
            double got = MotionMath.MotionValue(table, f, m.Start, m.End - 1, k, t, cur);
            Assert.True(BitConverter.DoubleToUInt64Bits(got) == bits || (double.IsNaN(got) && double.IsNaN(BitConverter.UInt64BitsToDouble(bits))),
                $"motion {mid} field {f} key {k} t {t}: {got} / oracle {BitConverter.UInt64BitsToDouble(bits)}");
        }
    }

    [Fact]
    public void MotionApplicationEqualsTheOracle()
    {
        ArrayPictureCatalog cat = StageKit.Catalog();
        foreach ((int mid, int div, int pic, var steps) in StageOracleCases.MotionRuns)
        {
            var table = new MotionTable(StageKit.Motions());
            SceneState scene = SceneState.CreateDefault();
            scene.SetPic(3, (short)pic);
            scene.SetAnchorX(3, (short)cat.Info(pic).AnchorX);
            scene.SetAnchorY(3, (short)cat.Info(pic).AnchorY);
            var rec = new LayerRecord
            {
                Used = 1, Pic = pic, X = 100f, Y = 80f, Alpha = 1f, AnchorX = (short)cat.Info(pic).AnchorX, AnchorY = (short)cat.Info(pic).AnchorY,
                OffX = -cat.Info(pic).AnchorX, OffY = -cat.Info(pic).AnchorY,
            };
            Array.Clear(rec.Colour);
            rec.Colour[5] = rec.Colour[6] = rec.Colour[7] = 255;
            MotionApply.MotionRecord(rec, mid, div);
            short id = scene.GetMotionId(3);
            short dv = scene.GetMotionDivisor(3);
            long fr = scene.GetMotionFrames(3);
            byte ps = scene.GetMotionPause(3);
            MotionApply.MotionRecord(ref id, ref dv, ref fr, ref ps, mid, div);
            scene.SetMotionId(3, id);
            scene.SetMotionDivisor(3, dv);
            scene.SetMotionFrames(3, fr);
            scene.SetMotionPause(3, ps);
            int n = 0;
            foreach ((int dt, string expected) in steps)
            {
                MotionApply.Apply(table, cat, rec, 3, dt, scene);
                Assert.True(expected == StageKit.CanonRec(rec), $"motion {mid} divisor {div} picture {pic} step {n}: {FirstDifference(expected, StageKit.CanonRec(rec))}");
                n++;
            }
        }
    }

    [Fact]
    public void TheProjectionEqualsTheOracle()
    {
        ArrayPictureCatalog cat = StageKit.Catalog();
        foreach ((string[] specs, int layer, uint ex, uint ey, ulong[] matrix, ulong[] frame) in StageOracleCases.Projection)
        {
            LayerRecord[] recs = new LayerRecord[40];
            for (int i = 0; i < 40; i++)
            {
                recs[i] = i < specs.Length ? StageKit.RecordFromSpec(specs[i]) : new LayerRecord();
            }

            (float X, float Y) extra = (BitConverter.UInt32BitsToSingle(ex), BitConverter.UInt32BitsToSingle(ey));
            Affine m = Projection.LayerMatrix(recs, layer, cat, extra);
            Affine f = Projection.FrameMatrix(recs, layer, cat, extra);
            double[] got = [m.A, m.B, m.C, m.D, m.E, m.F];
            double[] gotF = [f.A, f.B, f.C, f.D, f.E, f.F];
            for (int i = 0; i < 6; i++)
            {
                double want = BitConverter.UInt64BitsToDouble(matrix[i]);
                double wantF = BitConverter.UInt64BitsToDouble(frame[i]);
                Assert.True(Math.Abs(got[i] - want) <= 1e-7 * Math.Max(1.0, Math.Abs(want)), $"layer {layer} matrix[{i}]: {got[i]} / oracle {want}\n{string.Join('\n', specs)}");
                Assert.True(Math.Abs(gotF[i] - wantF) <= 1e-7 * Math.Max(1.0, Math.Abs(wantF)), $"layer {layer} frame[{i}]: {gotF[i]} / oracle {wantF}");
            }
        }
    }

    private static string FirstDifference(string a, string b)
    {
        string[] x = a.Split(' ');
        string[] y = b.Split(' ');
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            string p = i < x.Length ? x[i] : "<none>";
            string q = i < y.Length ? y[i] : "<none>";
            if (p != q)
            {
                return $"{p} (oracle) / {q} (c#)";
            }
        }

        return "equal";
    }
}

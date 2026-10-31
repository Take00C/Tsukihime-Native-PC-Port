using System.Buffers.Binary;
using Tsuki.Render.Compositor;

namespace Tsuki.Tests.Render.Compositor;

/// <summary>
/// The C# word decoding and transition numbers against compose, exhaustively (compositing.md 3, 5.1, 7.2, 7.4):
/// <see cref="DrawEffect.Decode"/> for every 16-bit word, signed and unsigned (the contract's
/// <c>DrawCmd.EffectWord</c> is a short); <see cref="TransitionParams.ForRule"/> and
/// <see cref="Fads.CrossfadeAlphaByte"/> on a progress grid; <see cref="Fads.Step"/>; and the capture tables of
/// <see cref="FadsCommands"/>. The GPU differential tests decode the word in compose on their own as well; this
/// test covers the words they do not draw. Needs the Python oracle (.venv); never skips.
/// </summary>
public sealed class OracleTableTests(ITestOutputHelper output)
{
    [Fact]
    public void WordDecodingRuleParametersAndFadsTablesEqualCompose()
    {
        using var dir = new TempFolder("tables");

        // effects.bin: word, e, flag, mode, state (i32), strength, p2 (f32)
        var eff = new List<byte>();
        for (int word = short.MinValue; word <= ushort.MaxValue; word++)
        {
            DrawEffect d = DrawEffect.Decode(word);
            I32(eff, word);
            I32(eff, d.E);
            I32(eff, d.Flag ? 1 : 0);
            I32(eff, d.ShaderMode);
            I32(eff, d.BlendState);
            F32(eff, d.Strength);
            F32(eff, d.P2);
        }

        File.WriteAllBytes(dir.File("effects.bin"), [.. eff]);

        // rule.bin: p, lo, hi, inv (f32), cross-fade alpha byte (i32); p on a 1/8192 grid over [-0.05, 1] and
        // the float32 neighbours of every 1/256 step (where trunc(p * 256) changes).
        var ps = new SortedSet<float>();
        for (int k = -410; k <= 8192; k++)
        {
            ps.Add(k / 8192f);
        }

        for (int k = 0; k <= 256; k++)
        {
            float p = k / 256f;
            ps.Add(p);
            ps.Add(MathF.BitDecrement(p));
            ps.Add(MathF.BitIncrement(p));
        }

        ps.RemoveWhere(p => p > 1f);
        var rule = new List<byte>();
        foreach (float p in ps)
        {
            TransitionParams t = TransitionParams.ForRule(p);
            F32(rule, p);
            F32(rule, t.Lo);
            F32(rule, t.Hi);
            F32(rule, t.Invert);
            I32(rule, Fads.CrossfadeAlphaByte(p));
        }

        File.WriteAllBytes(dir.File("rule.bin"), [.. rule]);

        // step.bin: kind, ms (i32), step (f32)
        var step = new List<byte>();
        foreach (int kind in new[] { 0, 2, 12, 49 })
        {
            for (int ms = -5; ms <= 10000; ms += ms < 200 ? 1 : 7)
            {
                I32(step, kind);
                I32(step, ms);
                F32(step, Fads.Step(ms, kind));
            }
        }

        File.WriteAllBytes(dir.File("step.bin"), [.. step]);

        // fads.bin: kind, captures surface 15 (0/1), surface read (-1 none, -2 not decoded)
        var fads = new List<byte>();
        for (int kind = -2; kind <= 72; kind++)
        {
            int surface;
            try
            {
                surface = FadsCommands.CaptureSurfaceOf(kind) ?? -1;
            }
            catch (NotSupportedException)
            {
                surface = -2;
            }

            I32(fads, kind);
            I32(fads, FadsCommands.CapturesSurface15(kind) ? 1 : 0);
            I32(fads, surface);
        }

        File.WriteAllBytes(dir.File("fads.bin"), [.. fads]);

        // oracle_tables.py exits non-zero (and Oracle.Run throws with its output) on any mismatch.
        string log = Oracle.Run(TimeSpan.FromMinutes(3), Path.Combine(Oracle.HelperDir, "oracle_tables.py"), "tables", dir.FullName).Trim();
        output.WriteLine(log);
        Assert.Contains("effect words: 98304, mismatches 0", log, StringComparison.Ordinal);
        Assert.Contains($"rule params / cross-fade bytes: {ps.Count} progress values, mismatches 0", log, StringComparison.Ordinal);
    }

    private static void I32(List<byte> b, int v)
    {
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(s, v);
        b.AddRange(s);
    }

    private static void F32(List<byte> b, float v)
    {
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(s, v);
        b.AddRange(s);
    }
}

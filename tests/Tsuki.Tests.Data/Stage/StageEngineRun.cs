using System.Text.Json;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;
using Tsuki.Tests.Data.Flow;

namespace Tsuki.Tests.Data.Stage;

/// <summary>What <see cref="StageEngineRun.Compare"/> compared.</summary>
/// <param name="Steps">Steps (statements and updates) whose state was compared.</param>
/// <param name="Matrices">Layer frame matrices compared with the oracle's projection.</param>
/// <param name="Draws">Scene draws whose layer list was compared with the engine's.</param>
/// <param name="Quads">Picture quads of the scene draws whose corners were compared with the oracle's projection.</param>
/// <param name="Failures">The first differences (at most three), empty when the stage equals the engine.</param>
internal sealed record StageEngineResult(int Steps, int Matrices, int Draws, int Quads, IReadOnlyList<string> Failures)
{
    /// <summary>Why the oracle stopped before the end of the scenario (its emulation has no textures, so a draw can fail), or null.</summary>
    public string? Halted { get; init; }
}

/// <summary>
/// Runs <c>engine_stage.py</c> (a scenario of layer statements and updates through the engine under emulation) and the same scenario through a
/// <see cref="StageRuntime"/>, comparing after every step the scene state, the drawn records A, the committed records B, the draw-order slots and the
/// <c>rand()</c> state; with a catalog also the layer frame matrices against the oracle's projection.
/// </summary>
internal static class StageEngineRun
{
    private const double MatrixTolerance = 1e-7;

    /// <summary>The state both sides start from: the base the oracle's <c>Rig</c> sets up (layer 0 = BLACK, committed, one frame run).</summary>
    public static StageRuntime BaseRuntime(IPictureCatalog catalog, IMotionTables motions)
    {
        var rt = new StageRuntime(catalog, motions);
        foreach (string s in new[] { "_STCC(-1);", "_STCH(0,BLACK,240,136,1000,255,2,,CC,,,);", "_FADS(1,0,0);" })
        {
            Assert.True(rt.Execute(StageData.Parse(s)), s);
        }

        rt.Update(1);
        return rt;
    }

    /// <summary>Runs the oracle with <paramref name="pythonArgs"/> and <paramref name="rt"/> through the scenario it prints.</summary>
    /// <param name="rt">The stage in the base state.</param>
    /// <param name="pythonArgs">Arguments of <c>engine_stage.py</c>.</param>
    /// <param name="layers">Layers 0..layers-1 are compared.</param>
    /// <param name="projectionCatalog">The catalog for the frame matrices of the <c>M</c> lines, or null when the oracle prints none.</param>
    /// <param name="timeout">Time allowed to the oracle.</param>
    /// <param name="layouts">When set (with <paramref name="projectionCatalog"/>), the corners of every picture quad of a scene draw are compared with the oracle's frame matrix applied to the tile rectangle.</param>
    public static StageEngineResult Compare(StageRuntime rt, string pythonArgs, int layers, IPictureCatalog? projectionCatalog, TimeSpan timeout, IPictureLayoutSource? layouts = null)
    {
        string script = Path.Combine(GoldenData.RepoRoot, "tests", "Tsuki.Tests.Data", "Stage", "engine_stage.py");
        var steps = new List<(string Kind, string Arg)>();
        var expA = new string[layers];
        var expB = new string[layers];
        byte[]? expScene = null;
        int[] expZ = new int[40];
        ulong expRand = 1;
        var failures = new List<string>();
        int lineNo = 0;
        int checkedSteps = 0;
        int matrices = 0;
        int draws = 0;
        int quads = 0;
        string? halted = null;
        var oracleMatrix = new Dictionary<int, double[]>();

        OracleProcess.Run(script, pythonArgs, timeout, line =>
        {
            if (failures.Count >= 3)
            {
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("halt", out JsonElement halt))
            {
                halted = halt.GetString();
            }
            else if (lineNo == 0)
            {
                foreach (JsonElement st in root.GetProperty("steps").EnumerateArray())
                {
                    steps.Add((st[0].GetString()!, st[1].ValueKind == JsonValueKind.String ? st[1].GetString()! : st[1].GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
            else if (root.TryGetProperty("init", out JsonElement init))
            {
                foreach (JsonProperty p in init.GetProperty("A").EnumerateObject())
                {
                    expA[int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] = p.Value.GetString()!;
                }

                foreach (JsonProperty p in init.GetProperty("B").EnumerateObject())
                {
                    expB[int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] = p.Value.GetString()!;
                }

                int zi = 0;
                foreach (JsonElement z in init.GetProperty("Z").EnumerateArray())
                {
                    expZ[zi++] = z.GetInt32();
                }

                expScene = rt.Scene.Bytes.ToArray();
                CompareState(rt, layers, expA, expB, expScene, expZ, expRand, "init", failures);
            }
            else if (root.TryGetProperty("i", out JsonElement ie))
            {
                int i = ie.GetInt32();
                (string kind, string arg) = steps[i];
                DisplayList? drawList = null;
                if (kind == "s")
                {
                    rt.Execute(StageData.Parse(arg));
                }
                else if (kind == "d")
                {
                    drawList = new DisplayList();
                    rt.Draw(drawList);
                    draws++;
                }
                else
                {
                    rt.Update(int.Parse(arg, System.Globalization.CultureInfo.InvariantCulture));
                }

                if (root.TryGetProperty("scene", out JsonElement sc))
                {
                    foreach (JsonElement patch in sc.EnumerateArray())
                    {
                        byte[] bytes = Convert.FromHexString(patch[1].GetString()!);
                        bytes.CopyTo(expScene!, patch[0].GetInt32());
                    }
                }

                if (root.TryGetProperty("A", out JsonElement a))
                {
                    foreach (JsonProperty p in a.EnumerateObject())
                    {
                        expA[int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] = p.Value.GetString()!;
                    }
                }

                if (root.TryGetProperty("B", out JsonElement b))
                {
                    foreach (JsonProperty p in b.EnumerateObject())
                    {
                        expB[int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] = p.Value.GetString()!;
                    }
                }

                if (root.TryGetProperty("Z", out JsonElement zs))
                {
                    int zi = 0;
                    foreach (JsonElement z in zs.EnumerateArray())
                    {
                        expZ[zi++] = z.GetInt32();
                    }
                }

                if (root.TryGetProperty("rand", out JsonElement rr))
                {
                    expRand = rr.GetUInt64();
                }

                string where = $"step {i} {kind} {arg}";
                CompareState(rt, layers, expA, expB, expScene!, expZ, expRand, where, failures);
                if (kind == "d")
                {
                    int[] want = [.. root.GetProperty("draw").EnumerateArray().Select(e => e.GetInt32())];
                    int[] have = [.. rt.DrawnLayers.Where(l => rt.Drawn[l].Stra == 0)];
                    if (!want.SequenceEqual(have))
                    {
                        failures.Add($"{where}: the engine draws layers [{string.Join(',', want)}] / c# [{string.Join(',', have)}]");
                    }
                }

                if (projectionCatalog is not null && root.TryGetProperty("M", out JsonElement m))
                {
                    foreach (JsonProperty p in m.EnumerateObject())
                    {
                        matrices++;
                        int l = int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture);
                        CompareMatrix(rt, projectionCatalog, l, p.Value, where, failures);
                        oracleMatrix[l] = [.. p.Value.EnumerateArray().Select(e => BitConverter.UInt64BitsToDouble(ulong.Parse(e.GetString()!, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)))];
                    }
                }

                if (drawList is not null && layouts is not null)
                {
                    quads += CompareQuads(rt, drawList, layouts, oracleMatrix, where, failures);
                }

                checkedSteps++;
            }

            lineNo++;
        });

        return new StageEngineResult(checkedSteps, matrices, draws, quads, failures) { Halted = halted };
    }

    /// <summary>
    /// The picture quads of a scene draw against the oracle: for every drawn layer, the layer's tiles in order, each with the corners of the tile rectangle
    /// (from the layout) mapped by the oracle's frame matrix of the layer. Layers moved by a shake, a screen shock or a mask are skipped (the oracle's
    /// projection does not include those offsets). Returns the number of quads compared.
    /// </summary>
    private static int CompareQuads(StageRuntime rt, DisplayList list, IPictureLayoutSource layouts, Dictionary<int, double[]> oracleMatrix, string where, List<string> failures)
    {
        ReadOnlySpan<DrawCmd> cmds = list.Commands;
        int at = 0;
        while (at < cmds.Length && cmds[at].Texture.Kind != TextureKind.PictureTile)
        {
            at++;
        }

        int compared = 0;
        foreach (int l in rt.DrawnLayers)
        {
            LayerRecord r = rt.Drawn[l];
            PictureLayout layout = layouts.Get(r.Pic, rt.Language) ?? throw new InvalidOperationException($"layer {l} was drawn without a layout");
            int n = layout.Files.Sum(f => f.Tiles.Count);
            (float sx, float sy) = rt.Shakes.SlotOffset(l);
            (float mx, float my) = rt.Shakes.MaskOffset(l, (0f, 0f));
            bool plain = sx == 0f && sy == 0f && mx == 0f && my == 0f && rt.Shock.X == 0f && rt.Shock.Y == 0f;
            if (plain && oracleMatrix.TryGetValue(l, out double[]? m))
            {
                int k = at;
                foreach (PictureFileLayout file in layout.Files)
                {
                    foreach (TileQuad t in file.Tiles)
                    {
                        DrawCmd c = cmds[k++];
                        (float X, float Y)[] corners = [(t.X0, t.Y0), (t.X1, t.Y0), (t.X0, t.Y1), (t.X1, t.Y1)];
                        Vertex[] got = [c.V0, c.V1, c.V2, c.V3];
                        for (int v = 0; v < 4; v++)
                        {
                            double px = file.OffsetX + corners[v].X;
                            double py = file.OffsetY + corners[v].Y;
                            double ex = (m[0] * px) + (m[1] * py) + m[2];
                            double ey = (m[3] * px) + (m[4] * py) + m[5];
                            if (Math.Abs(got[v].X - ex) > 5e-3 || Math.Abs(got[v].Y - ey) > 5e-3)
                            {
                                failures.Add($"{where}: layer {l} tile {t.Tile} corner {v}: oracle ({ex:F4}, {ey:F4}) / c# ({got[v].X:F4}, {got[v].Y:F4})");
                                return compared;
                            }
                        }

                        compared++;
                    }
                }
            }

            at += n;
        }

        return compared;
    }

    private static void CompareMatrix(StageRuntime rt, IPictureCatalog catalog, int layer, JsonElement bits, string where, List<string> failures)
    {
        Affine f = Projection.FrameMatrix(rt.Drawn, layer, catalog, (0f, 0f));
        double[] got = [f.A, f.B, f.C, f.D, f.E, f.F];
        int n = 0;
        foreach (JsonElement e in bits.EnumerateArray())
        {
            double want = BitConverter.UInt64BitsToDouble(ulong.Parse(e.GetString()!, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
            if (!(Math.Abs(got[n] - want) <= MatrixTolerance * Math.Max(1.0, Math.Abs(want))))
            {
                failures.Add($"{where}: frame matrix of layer {layer}[{"abcdef"[n]}] oracle {want:R} / c# {got[n]:R}");
                return;
            }

            n++;
        }
    }

    private static void CompareState(StageRuntime rt, int layers, string[] expA, string[] expB, byte[] expScene, int[] expZ, ulong expRand, string where, List<string> failures)
    {
        for (int l = 0; l < layers; l++)
        {
            string a = StageTrace.Canon(rt.Drawn[l], false);
            if (a != expA[l])
            {
                failures.Add($"{where}: A[{l}] {StageTrace.FirstDifference(expA[l], a)}");
            }

            string b = StageTrace.Canon(rt.Committed[l], true);
            if (b != expB[l])
            {
                failures.Add($"{where}: B[{l}] {StageTrace.FirstDifference(expB[l], b)}");
            }
        }

        ReadOnlySpan<byte> have = rt.Scene.Bytes;
        for (int i = 0; i < expScene.Length; i++)
        {
            if (have[i] != expScene[i])
            {
                failures.Add($"{where}: scene byte 0x{i:X}: engine {expScene[i]:X2} / c# {have[i]:X2}");
                break;
            }
        }

        for (int l = 0; l < 40; l++)
        {
            if (rt.Drawn[l].ZOrder != expZ[l])
            {
                failures.Add($"{where}: z order[{l}] engine {expZ[l]} / c# {rt.Drawn[l].ZOrder}");
                break;
            }
        }

        if (rt.Rng.State != expRand)
        {
            failures.Add($"{where}: rand state engine {expRand:X} / c# {rt.Rng.State:X}");
        }
    }
}

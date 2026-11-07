using System.Text.Json;
using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Tests.Data.Flow;
using Tsuki.Tests.Data.Source;

namespace Tsuki.Tests.Data.Game;

/// <summary>
/// Package K44: <see cref="FlowMarks"/> against the PS4's own mark functions run under unicorn (<c>flow_marks_oracle.py</c>): the node of every scene
/// id (0xB8870), the marks of a jump for every node pair the special-pair switch compares and every edge of the table (0xBA690 after its lookups) on six
/// variable states, and the marks of every choice point (0xB8620). Every F and G word the PS4 changed must change the same way, and nothing else.
/// </summary>
public sealed class FlowMarksOracleTests
{
    private static GameFolderSource Source => SharedSource.Source;

    private static string Script => Path.Combine(GoldenData.RepoRoot, "tests", "Tsuki.Tests.Data", "Game", "flow_marks_oracle.py");

    [GameDataFact]
    public void TheMarksEqualThePs4Functions()
    {
        IGameFlowTables flow = Source.Eboot.GameFlow;
        var states = new List<(int[] F, int[] G)>();
        var failures = new List<string>();
        int jumps = 0, choices = 0, ids = 0;
        JsonElement? summary = null;
        OracleProcess.Run(Script, string.Empty, TimeSpan.FromMinutes(5), line =>
        {
            using var doc = JsonDocument.Parse(line);
            JsonElement e = doc.RootElement;
            if (e.TryGetProperty("state", out _))
            {
                states.Add((Words(e.GetProperty("f"), Vars.FCount), Words(e.GetProperty("g"), 500)));
            }
            else if (e.TryGetProperty("ids", out JsonElement nodes))
            {
                int id = -2;
                foreach (JsonElement n in nodes.EnumerateArray())
                {
                    int ours = flow.ScriptOfSceneId(id) is >= 0 and var k ? flow.NodeOfScript(k) : -1;
                    if (ours != n.GetInt32() && failures.Count < 20)
                    {
                        failures.Add($"scene id {id}: node {ours}, PS4 {n.GetInt32()}");
                    }

                    id++;
                    ids++;
                }
            }
            else if (e.TryGetProperty("j", out JsonElement j))
            {
                int[] a = [.. j.EnumerateArray().Select(x => x.GetInt32())];
                Check($"jump {a[0]} -> {a[1]} pick {a[2]} state {a[3]}", states[a[3]], e, v => FlowMarks.JumpNodes(v, flow.Edges, a[0], a[1], a[2]));
                jumps++;
            }
            else if (e.TryGetProperty("c", out JsonElement c))
            {
                int[] a = [.. c.EnumerateArray().Select(x => x.GetInt32())];
                Check($"choice {a[0]} state {a[1]}", states[a[1]], e, v => FlowMarks.Choice(v, flow.ChoiceEdges, a[0]));
                choices++;
            }
            else if (e.TryGetProperty("summary", out JsonElement s))
            {
                summary = s.Clone();
            }
        });

        Assert.NotNull(summary);
        Assert.Equal(summary!.Value.GetProperty("jumps").GetInt32(), jumps);
        Assert.Equal(summary.Value.GetProperty("choices").GetInt32(), choices);
        Assert.True(jumps > 50_000 && choices > 500 && ids == 10_013, $"{jumps} jumps, {choices} choices, {ids} ids");
        Assert.True(failures.Count == 0, $"{failures.Count} differences from the PS4:\n{string.Join("\n", failures)}");

        void Check(string name, (int[] F, int[] G) state, JsonElement want, Action<Vars> run)
        {
            var v = new Vars();
            for (int i = 0; i < state.F.Length; i++)
            {
                v.SetF(i, (ushort)state.F[i]);
            }

            for (int i = 0; i < state.G.Length; i++)
            {
                v.SetG(i, (ushort)state.G[i]);
            }

            run(v);
            string got = Diff(state.F, i => v.GetF(i)) + " | " + Diff(state.G, i => v.GetG(i));
            string ps4 = string.Join(",", want.GetProperty("f").EnumerateArray().Select(p => $"{p[0].GetInt32()}={p[1].GetInt32()}"))
                + " | " + string.Join(",", want.GetProperty("g").EnumerateArray().Select(p => $"{p[0].GetInt32()}={p[1].GetInt32()}"));
            if (got != ps4 && failures.Count < 20)
            {
                failures.Add($"{name}: port {got}; PS4 {ps4}");
            }
        }
    }

    private static int[] Words(JsonElement pairs, int count)
    {
        int[] w = new int[count];
        foreach (JsonElement p in pairs.EnumerateArray())
        {
            w[p[0].GetInt32()] = p[1].GetInt32();
        }

        return w;
    }

    private static string Diff(int[] before, Func<int, ushort> after) =>
        string.Join(",", Enumerable.Range(0, before.Length).Where(i => after(i) != before[i]).Select(i => $"{i}={after(i)}"));
}

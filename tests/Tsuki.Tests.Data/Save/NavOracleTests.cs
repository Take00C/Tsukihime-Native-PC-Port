using System.Text.Json;
using Tsuki.Engine.Save;

namespace Tsuki.Tests.Data.Save;

/// <summary>
/// Rewind, previous scene and the backlog list against the game's own Adv_Rewind (VA 0xA2020), the previous-scene
/// decision (VA 0xA23F0) and Backlog_Open (VA 0x3BFB0) run under unicorn on random rings (<c>python -m oracle.save --parts nav</c>).
/// </summary>
public sealed class NavOracleTests
{
    private static AdvHistory BuildRing(JsonElement ring, bool withF799)
    {
        var h = new AdvHistory();
        foreach (JsonProperty p in ring.GetProperty("records").EnumerateObject())
        {
            int i = int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture);
            JsonElement[] v = p.Value.EnumerateArray().ToArray();
            Tsuki.Engine.Contracts.HistoryRecord r = h.Record(i);
            r.Serial = v[0].GetInt32();
            r.Kind = (ushort)v[1].GetInt32();
            r.EffectBlockingBits = (ushort)v[2].GetInt32();
            r.Bytes[0x3521] = (byte)v[3].GetInt32();                 // the s8 copy of F823 (list slot 32)
            if (withF799)
            {
                r.Bytes[0x3501 + 9] = (byte)v[4].GetInt32();         // the s8 copy of F799 (list slot 9)
            }
        }

        h.Cur = ring.GetProperty("cur").GetInt32();
        h.Next = ring.GetProperty("next").GetInt32();
        h.Oldest = ring.GetProperty("oldest").GetInt32();
        h.Wrapped = ring.GetProperty("wrapped").GetInt32() != 0;
        return h;
    }

    [GameDataFact]
    public void RewindStepRestoresTheSameRecordsAsAdvRewind()
    {
        using JsonDocument doc = GoldenSave.Json("nav/nav_cases.json");
        var failures = new List<string>();
        int n = 0;
        int stepped = 0;
        foreach (JsonElement c in doc.RootElement.GetProperty("rewind").EnumerateArray())
        {
            n++;
            string name = c.GetProperty("name").GetString()!;
            AdvHistory h = BuildRing(c.GetProperty("ring"), withF799: false);
            h.RewindCursor = c.GetProperty("cursor").GetInt32();
            var live = new AdvLiveState();
            RewindResult r = h.RewindStep(live, _ => new byte[64]);

            var calls = c.GetProperty("calls").EnumerateArray().Select(e => e.EnumerateArray().Select(x => x.GetInt64()).ToArray()).ToList();
            var want = calls.Where(x => x[0] == 0x7C6D0).Select(x => (int)x[1]).ToList();
            var got = r.Restores.Select(x => x.Index).ToList();
            if (!want.SequenceEqual(got))
            {
                failures.Add($"{name}: restored [{string.Join(",", got.Take(8))}..{got.Count}] != [{string.Join(",", want.Take(8))}..{want.Count}]");
                continue;
            }

            bool wantStep = calls.Single(x => x[0] == 0x56730)[1] == 1;
            if (r.Stepped != wantStep)
            {
                failures.Add($"{name}: stepped {r.Stepped} != {wantStep}");
            }

            stepped += wantStep ? 1 : 0;
            if (r.FlushPending != calls.Any(x => x[0] == 0x75CD0))
            {
                failures.Add($"{name}: flush pending {r.FlushPending}");
            }

            if (wantStep != calls.Any(x => x[0] == 0x7E4E0) || wantStep != r.Resync)
            {
                failures.Add($"{name}: scene re-sync");
            }

            if (h.RewindCursor != c.GetProperty("cursor_after").GetInt32())
            {
                failures.Add($"{name}: cursor {h.RewindCursor} != {c.GetProperty("cursor_after").GetInt32()}");
            }
        }

        Assert.True(n >= 40, $"only {n} rewind cases");
        Assert.True(stepped >= 10, $"only {stepped} cases step");
        Assert.True(failures.Count == 0, $"{failures.Count} of {n} rewind cases differ:" + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(20)));
    }

    [GameDataFact]
    public void PreviousSceneDecidesLikeTheGame()
    {
        using JsonDocument doc = GoldenSave.Json("nav/nav_cases.json");
        var failures = new List<string>();
        int restored = 0;
        foreach (JsonElement c in doc.RootElement.GetProperty("previous_scene").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            int top = c.GetProperty("top").GetInt32();
            var h = new AdvHistory { StackTop = top, SceneMessageCounter = c.GetProperty("counter").GetInt32() };
            for (int i = 0; i <= Math.Max(top, 0); i++)
            {
                h.StackRecord(i);
            }

            var live = new AdvLiveState();
            SceneRestoreResult? r = h.PreviousScene(live, (ushort)c.GetProperty("blocking").GetInt32(), c.GetProperty("flag10").GetInt32() != 0, out bool flush);
            var calls = c.GetProperty("calls").EnumerateArray().Select(e => e.EnumerateArray().Select(x => x.GetInt64()).ToArray()).ToList();
            long[]? restore = calls.FirstOrDefault(x => x[0] == 0x7E790);
            if ((restore is not null) != (r is not null))
            {
                failures.Add($"{name}: restored {r is not null}");
                continue;
            }

            if (restore is not null)
            {
                restored++;
                if (h.StackTop != restore[1] - 1)
                {
                    failures.Add($"{name}: stack top {h.StackTop} after restoring scene {restore[1]}");
                }

                if (flush != calls.Any(x => x[0] == 0x75CD0))
                {
                    failures.Add($"{name}: flush {flush}");
                }
            }
            else if (h.StackTop != top)
            {
                failures.Add($"{name}: the stack changed without a restore");
            }
        }

        Assert.True(restored >= 10);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
    }

    [GameDataFact]
    public void BacklogListEqualsBacklogOpen()
    {
        using JsonDocument doc = GoldenSave.Json("nav/nav_cases.json");
        var failures = new List<string>();
        int n = 0;
        int nonEmpty = 0;
        foreach (JsonElement c in doc.RootElement.GetProperty("backlog").EnumerateArray())
        {
            n++;
            string name = c.GetProperty("name").GetString()!;
            AdvHistory h = BuildRing(c.GetProperty("ring"), withF799: true);
            List<int> got = h.BacklogEntries();
            List<int> want = c.GetProperty("entries").EnumerateArray().Select(e => e.GetInt32()).ToList();
            nonEmpty += want.Count > 0 ? 1 : 0;
            if (!got.SequenceEqual(want))
            {
                failures.Add($"{name}: {got.Count} entries [{string.Join(",", got.Take(6))}] != {want.Count} [{string.Join(",", want.Take(6))}]");
            }
        }

        Assert.True(n >= 30 && nonEmpty >= 10, $"{n} cases, {nonEmpty} non-empty");
        Assert.True(failures.Count == 0, $"{failures.Count} of {n} backlog cases differ:" + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(20)));
    }
}

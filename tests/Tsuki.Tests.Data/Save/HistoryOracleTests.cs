using System.Text.Json;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;

namespace Tsuki.Tests.Data.Save;

/// <summary>
/// <see cref="AdvHistory.Push"/> and <see cref="AdvHistory.Restore"/> against the game's own History_Push / History_Restore run
/// under unicorn on random RAM (<c>python -m oracle.save --parts history</c>; docs/formats/adv_state.md 2, 3).
/// </summary>
public sealed class HistoryOracleTests
{
    [GameDataFact]
    public void PushEqualsTheGamesHistoryPush()
    {
        using JsonDocument doc = GoldenSave.Json("history/push_cases.json");
        var failures = new List<string>();
        int n = 0;
        foreach (JsonElement c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            n++;
            string name = c.GetProperty("name").GetString()!;
            int k = c.GetProperty("k").GetInt32();
            int next = k + 1 < AdvHistory.RingCount ? k + 1 : 0;
            JsonElement ib = c.GetProperty("indices_before");
            AdvLiveState live = LiveMap.Build(c.GetProperty("live"));
            live.EffectActiveBits = (ushort)c.GetProperty("effect_active").GetInt32();
            live.EffectBlockingBits = (ushort)c.GetProperty("effect_blocking").GetInt32();
            live.LastMessageOfPage = c.GetProperty("last_message").GetInt32() != 0;

            var h = new AdvHistory { Ps4StaleSeBytes = true };
            h.SerialCounter = (short)ib.GetProperty("serial_counter").GetInt32();
            h.Cur = ib.GetProperty("cur").GetInt32();
            h.Next = ib.GetProperty("next").GetInt32();
            h.Wrapped = ib.GetProperty("wrapped").GetInt32() != 0;
            h.Oldest = ib.GetProperty("oldest").GetInt32();
            h.Record(k).CopyFrom(Convert.FromHexString(c.GetProperty("record_before").GetString()!));
            h.Record(next).CopyFrom(Convert.FromHexString(c.GetProperty("next_before").GetString()!));

            bool pushed = h.Push(live, c.GetProperty("cmd_offset").GetUInt64(), c.GetProperty("resume_offset").GetUInt64(), (ushort)c.GetProperty("kind").GetInt32());

            string recordAfter = c.GetProperty("record_after").GetString()!;
            if (Convert.ToHexStringLower(h.Record(k).Bytes) != recordAfter)
            {
                failures.Add($"{name}: record {k} differs at " + FirstDifference(h.Record(k).Bytes, Convert.FromHexString(recordAfter)));
            }

            if (Convert.ToHexStringLower(h.Record(next).Bytes) != c.GetProperty("next_after").GetString())
            {
                failures.Add($"{name}: record {next} (the next slot) differs");
            }

            JsonElement ia = c.GetProperty("indices_after");
            (string, int)[] indices =
            [
                ("serial_counter", h.SerialCounter), ("cur", h.Cur), ("next", h.Next), ("wrapped", h.Wrapped ? 1 : 0), ("oldest", h.Oldest),
            ];
            foreach ((string field, int value) in indices)
            {
                if (ia.GetProperty(field).GetInt32() != value)
                {
                    failures.Add($"{name}: {field} {value} != {ia.GetProperty(field).GetInt32()}");
                }
            }

            foreach (string bad in LiveMap.Mismatches(live, c.GetProperty("live"), c.GetProperty("live_after")))
            {
                failures.Add($"{name}: live window {bad}");
            }

            bool gated = name is "gate_f929" or "gate_f1102_kind3" or "gate_f1102_kind12" or "gate_scan" or "gate_16541f4";
            if (pushed == gated)
            {
                failures.Add($"{name}: pushed = {pushed}");
            }
        }

        Assert.True(n >= 30, $"only {n} push cases");
        Assert.True(failures.Count == 0, $"{failures.Count} of {n} push cases differ:" + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(20)));
    }

    [GameDataFact]
    public void RestoreEqualsTheGamesHistoryRestore()
    {
        using JsonDocument doc = GoldenSave.Json("history/restore_cases.json");
        var failures = new List<string>();
        int n = 0;
        foreach (JsonElement c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            n++;
            string name = c.GetProperty("name").GetString()!;
            int index = c.GetProperty("index").GetInt32();
            bool fromHistory = c.GetProperty("from_history").GetInt32() != 0;
            AdvLiveState live = LiveMap.Build(c.GetProperty("live"));
            live.EffectActiveBits = (ushort)c.GetProperty("effect_active").GetInt32();
            live.PageEndInstantLatch = c.GetProperty("latch").GetInt32() != 0;
            byte[] script = Convert.FromHexString(c.GetProperty("script").GetString()!);

            var h = new AdvHistory();
            JsonElement ib = c.GetProperty("indices_before");
            h.Cur = ib.GetProperty("cur").GetInt32();
            h.Next = ib.GetProperty("next").GetInt32();
            h.Wrapped = ib.GetProperty("wrapped").GetInt32() != 0;
            JsonElement sb = c.GetProperty("stack_before");
            h.StackTop = sb.GetProperty("top").GetInt32();
            if (h.StackTop >= 0)
            {
                h.StackRecord(h.StackTop).RingIndex = sb.GetProperty("ring_index").GetInt32();
            }

            h.SceneMessageCounter = (int)sb.GetProperty("counter").GetUInt32();
            h.AfterLoad = (int)sb.GetProperty("after_load").GetUInt32();
            h.Record(index).CopyFrom(Convert.FromHexString(c.GetProperty("record").GetString()!));

            RestoreOutcome o = h.Restore(index, fromHistory, live, _ => script);

            foreach (string bad in LiveMap.Mismatches(live, c.GetProperty("live"), c.GetProperty("live_after")))
            {
                failures.Add($"{name}: live window {bad}");
            }

            string wantOffset = c.GetProperty("script_offset_after").GetString()!;
            if (live.ScriptOffset.ToString("x16") != wantOffset)
            {
                failures.Add($"{name}: script offset {live.ScriptOffset:x} != {wantOffset}");
            }

            JsonElement ia = c.GetProperty("indices_after");
            if (ia.GetProperty("cur").GetInt32() != h.Cur || ia.GetProperty("next").GetInt32() != h.Next || (ia.GetProperty("wrapped").GetInt32() != 0) != h.Wrapped)
            {
                failures.Add($"{name}: ring indices cur {h.Cur} next {h.Next} wrapped {h.Wrapped}");
            }

            JsonElement sa = c.GetProperty("stack_after");
            if (sa.GetProperty("top").GetInt32() != h.StackTop || (int)sa.GetProperty("counter").GetUInt32() != h.SceneMessageCounter
                || (int)sa.GetProperty("after_load").GetUInt32() != h.AfterLoad)
            {
                failures.Add($"{name}: scene stack top {h.StackTop} counter {h.SceneMessageCounter} after-load {h.AfterLoad}");
            }

            if (o.ReloadScript != (c.GetProperty("record_script").GetInt32() != c.GetProperty("script_number").GetInt32()))
            {
                failures.Add($"{name}: script reload {o.ReloadScript}");
            }

            CompareCalls(name, c, o, h, failures);
        }

        Assert.True(n >= 100, $"only {n} restore cases");
        Assert.True(failures.Count == 0, $"{failures.Count} of {n} restore cases differ:" + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(25)));
    }

    private static void CompareCalls(string name, JsonElement c, RestoreOutcome o, AdvHistory h, List<string> failures)
    {
        var calls = c.GetProperty("calls").EnumerateArray().Select(e => e.EnumerateArray().ToArray()).ToList();
        long[] Args(JsonElement[] call) => [call[1].GetInt64(), call[2].GetInt64(), call[3].GetInt64(), call[4].GetInt64()];
        List<JsonElement[]> Of(int va) => calls.Where(x => x[0].GetInt32() == va).ToList();

        // music (kind 2)
        BgmRestoreAction wantBgm = Of(0x98C0).Count > 0 ? BgmRestoreAction.Start : Of(0x9A90).Count > 0 ? BgmRestoreAction.Stop : BgmRestoreAction.None;
        if (o.Bgm != wantBgm)
        {
            failures.Add($"{name}: bgm {o.Bgm} != {wantBgm}");
        }
        else if (wantBgm == BgmRestoreAction.Start && o.BgmTrack != (short)Args(Of(0x98C0)[0])[0])
        {
            failures.Add($"{name}: bgm track {o.BgmTrack}");
        }

        // loop SEs (kinds 3..5)
        var restarts = Of(0xAA80).Select(x => (int)Args(x)[0]).ToList();
        var stops = Of(0xAAB0).Select(x => (int)Args(x)[0]).ToList();
        if (o.LoopSe is null)
        {
            if (restarts.Count + stops.Count != 0)
            {
                failures.Add($"{name}: the game restarted loop SEs, the port did not");
            }
        }
        else
        {
            var wantRestart = o.LoopSe.Where(l => l.Restart).Select(l => l.Channel).ToList();
            var wantStop = o.LoopSe.Where(l => !l.Restart).Select(l => l.Channel).ToList();
            if (!restarts.SequenceEqual(wantRestart) || !stops.SequenceEqual(wantStop))
            {
                failures.Add($"{name}: loop SE channels restart [{string.Join(",", restarts)}] stop [{string.Join(",", stops)}] != port [{string.Join(",", wantRestart)}] [{string.Join(",", wantStop)}]");
            }
            else
            {
                var vol = Of(0xAD60);
                var pitch = Of(0xA300);
                var arrC = Of(0xA9E0);
                var arrD = Of(0xAA50);
                for (int i = 0; i < restarts.Count; i++)
                {
                    LoopSeRestore l = o.LoopSe[restarts[i]];
                    if ((sbyte)Args(vol[i])[1] != l.Volume || (short)Args(pitch[i])[2] != l.LoopSeId
                        || (short)Args(arrC[i])[1] != l.ArrC || (short)Args(arrD[i])[1] != l.ArrD
                        || (float)pitch[i][5].GetDouble() != l.Pitch)
                    {
                        failures.Add($"{name}: loop SE channel {restarts[i]} arguments");
                    }
                }
            }
        }

        // other calls
        if ((Of(0x7B790).Count > 0) != (o.ShowRecordText is not null))
        {
            failures.Add($"{name}: kind-7 text call");
        }

        if ((Of(0x75CD0).Count > 0) != o.FlushPending)
        {
            failures.Add($"{name}: flush pending {o.FlushPending}");
        }

        int stopMask = 0;
        foreach (JsonElement[] call in Of(0x5C080))
        {
            stopMask |= 1 << (int)Args(call)[0];
        }

        if (stopMask != o.StopEffectSlots)
        {
            failures.Add($"{name}: effect slots to stop {o.StopEffectSlots:x} != {stopMask:x}");
        }

        if ((Of(0xE98D0).Count == 40) != o.RebuildFromCommitted || (Of(0xE98D0).Count is not (0 or 40)))
        {
            failures.Add($"{name}: stage rebuild ({Of(0xE98D0).Count} layer calls)");
        }

        if ((Of(0x423D0).Count > 0) != o.Config04Hook)
        {
            failures.Add($"{name}: config hook");
        }

        List<JsonElement[]> staging = Of(0x73980);
        if ((staging.Count > 0) != o.StagingUpdated)
        {
            failures.Add($"{name}: staging snapshot {o.StagingUpdated}");
        }
        else if (staging.Count == 1)
        {
            long[] a = Args(staging[0]);
            // n = top + 1 ; state B only when the stack is not empty
            int top = c.GetProperty("stack_before").GetProperty("top").GetInt32();
            if ((int)a[1] != top + 1 || (a[2] != 0) != (top >= 0) || (a[3] != 0) != (top > 0))
            {
                failures.Add($"{name}: staging arguments ({a[1]}, {a[2]:x}, {a[3]:x}) for stack top {top}");
            }

            if (h.Staging.Used != 1 || h.Staging.HasStateB != (top + 1 > 0 ? 1 : top + 1))
            {
                failures.Add($"{name}: staging image");
            }
        }
    }

    private static string FirstDifference(ReadOnlySpan<byte> got, ReadOnlySpan<byte> want)
    {
        for (int i = 0; i < Math.Min(got.Length, want.Length); i++)
        {
            if (got[i] != want[i])
            {
                string field = HistoryRecord.Fields.LastOrDefault(f => f.Offset <= i).Name ?? "?";
                return $"+0x{i:X} ({field}): got {got[i]:X2}, want {want[i]:X2}";
            }
        }

        return "length";
    }
}

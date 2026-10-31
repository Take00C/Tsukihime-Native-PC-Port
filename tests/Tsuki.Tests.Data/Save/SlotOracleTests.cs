using System.Text.Json;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;

namespace Tsuki.Tests.Data.Save;

/// <summary>
/// Save slots against images made by the game's own code (<c>engine_slot_image</c>: History_Push of the first <c>_ZM</c>
/// statement of script 1, Staging_SetSnapshot, Slot_Store / QuickSave_Store, SaveImage_Commit) and the state Slot_Load
/// leaves for them (adv_state.md 7.2, 7.3, 7.6).
/// </summary>
public sealed class SlotOracleTests
{
    [GameDataFact]
    public void StoredSlotsEqualTheImagesTheGameWrote()
    {
        using JsonDocument doc = GoldenSave.Json("slots/slots.json");
        int n = 0;
        foreach (JsonElement c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            n++;
            string name = c.GetProperty("name").GetString()!;
            byte[] record = Convert.FromHexString(c.GetProperty("record").GetString()!);
            HistoryRecord? stackTop = c.GetProperty("record_b").ValueKind == JsonValueKind.String
                ? new HistoryRecord(Convert.FromHexString(c.GetProperty("record_b").GetString()!))
                : null;
            bool quick = c.GetProperty("quick").GetBoolean();
            JsonElement d = c.GetProperty("date");
            var date = new SlotDate(d[0].GetInt32(), d[1].GetInt32(), d[2].GetInt32(), d[3].GetInt32(), d[4].GetInt32(), d[5].GetInt32());

            // the staging as the history keeps it, then the store
            var history = new AdvHistory();
            history.Staging.SetSnapshot(new HistoryRecord(record), c.GetProperty("n").GetInt32(), stackTop);
            var store = new SaveStore();
            int slot = c.GetProperty("slot").GetInt32();
            if (quick)
            {
                Assert.True(store.QuickSave(history.Staging, date), name);
            }
            else
            {
                Assert.True(store.SaveManual(slot, history.Staging, date), name);
            }

            SlotImage stored = store.GetSlot(slot)!;
            string want = c.GetProperty("slot_bytes").GetString()!;
            string got = Convert.ToHexStringLower(stored.Bytes);
            Assert.True(want == got, $"{name}: slot differs at byte 0x{FirstDifference(Convert.FromHexString(want), stored.Bytes):X}");
            Assert.True(stored.IsStored, name);
            Assert.Equal(!quick, stored.StateA.GetFBoolBit(HistoryRecord.FBoolIndices.IndexOf(AdvLiveState.FIndex.SavedMarker)));
        }

        Assert.True(n >= 3);
    }

    [GameDataFact]
    public void LoadingTheGameMadeSlotsLeavesTheStateSlotLoadLeaves()
    {
        using JsonDocument doc = GoldenSave.Json("slots/slots.json");
        int loaded = 0;
        foreach (JsonElement c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (!c.TryGetProperty("load", out JsonElement load))
            {
                continue;
            }

            loaded++;
            string name = c.GetProperty("name").GetString()!;
            var slot = new SlotImage(Convert.FromHexString(load.GetProperty("slot_bytes").GetString()!));
            AdvLiveState live = LiveMap.Build(load.GetProperty("live"));
            var history = new AdvHistory();
            history.Cur = 17;
            history.Next = 40;
            SceneRestoreResult r = history.LoadSlot(slot, live);

            var failures = LiveMap.Mismatches(live, load.GetProperty("live"), load.GetProperty("live_after"));
            Assert.True(failures.Count == 0, $"{name}: live windows differ: " + string.Join(", ", failures));
            Assert.Equal(load.GetProperty("script_offset_after").GetString(), live.ScriptOffset.ToString("x16"));
            Assert.Equal(load.GetProperty("stack_top_after").GetInt32(), history.StackTop);
            Assert.Equal(0, history.Cur);
            Assert.Equal(0, history.Next);
            Assert.False(history.Wrapped);
            Assert.Equal((int)load.GetProperty("counter_after").GetUInt32(), history.SceneMessageCounter);
            Assert.Equal((int)load.GetProperty("after_load_after").GetUInt32(), history.AfterLoad);
            Assert.Equal(load.GetProperty("ring0").GetString(), Convert.ToHexStringLower(history.Record(0).Bytes));
            string stack0 = load.GetProperty("stack0").GetString()!;
            if (slot.HasStateB == 1)
            {
                // state B keeps the first 0x36A8 bytes of the scene-start record; the loader clears its text byte
                const int b = SlotImage.StateBSize;
                Assert.Equal(stack0[..(2 * b)], Convert.ToHexStringLower(history.StackRecord(0).Bytes[..b]));
                Assert.Equal(0, history.StackRecord(0).MessageText[0]);
                Assert.Equal(load.GetProperty("stack1").GetString(), Convert.ToHexStringLower(history.StackRecord(1).Bytes));
            }
            else
            {
                Assert.Equal(stack0, Convert.ToHexStringLower(history.StackRecord(0).Bytes));
            }

            // the saved line is not re-displayed: the page's first message statement runs again (adv_state.md 3.2, 7.3)
            Assert.Equal(slot.StateA.CmdOffset, live.ScriptOffset);
            Assert.Null(r.Restore.Redisplay);
            Assert.Equal(1, live.F(AdvLiveState.FIndex.SavedMarker));
        }

        Assert.True(loaded >= 2);
    }

    [GameDataFact]
    public void ThePs4ImageTheGameWroteImports()
    {
        using JsonDocument doc = GoldenSave.Json("slots/slots.json");
        int n = 0;
        foreach (JsonElement c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            n++;
            string name = c.GetProperty("name").GetString()!;
            int slot = c.GetProperty("slot").GetInt32();
            byte[] image = new byte[SaveFile.Ps4ImageSize];
            Convert.FromHexString(c.GetProperty("system_bytes").GetString()!).CopyTo(image, 0);
            byte[] slotBytes = Convert.FromHexString(c.GetProperty("slot_bytes").GetString()!);
            slotBytes.CopyTo(image, 0x1A00 + (slot * 0x7140));
            SaveStore store = SaveFile.ImportPs4Image(image);

            Assert.Equal([slot], store.StoredSlots().ToArray());
            Assert.Equal(slotBytes, store.GetSlot(slot)!.Bytes);
            Assert.Equal(new SlotDate(2026, 10, 25, 12, 34, 56), store.GetSlot(slot)!.Date);
            foreach (JsonElement rm in c.GetProperty("readmax").EnumerateArray())
            {
                int block = rm[0].GetInt32();
                int line = rm[1].GetInt32();
                Assert.True(store.SystemData.IsRead(block, line), $"{name}: readmax[{block}]");
                Assert.False(store.SystemData.IsRead(block, line + 1), name);
            }

            // the imported image is a save file of ours and loads: the saved line runs again
            SaveStore viaFile = SaveFile.FromBytes(SaveFile.ToBytes(store));
            var history = new AdvHistory();
            var live = new AdvLiveState();
            viaFile.Load(slot, history, live);
            Assert.Equal(c.GetProperty("statement_offset").GetInt32() - 1, (int)live.ScriptOffset);
            Assert.Equal(c.GetProperty("script_no").GetInt32(), live.ScriptNumber);
            Assert.Equal(slot == SaveStore.QuickSlot ? 0 : 1, live.Vars.GetWord(AdvLiveState.FIndex.SavedMarker));
        }

        Assert.Equal(3, n);
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return -1;
    }
}

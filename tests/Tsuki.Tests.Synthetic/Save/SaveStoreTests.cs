using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;

namespace Tsuki.Tests.Synthetic.Save;

/// <summary>Save slots, quick save, the auto-save FIFO, the latest slot and the system data.</summary>
public sealed class SaveStoreTests
{
    private static readonly SlotDate T0 = new(2026, 10, 26, 9, 0, 0);

    /// <summary>A staging with a page-start snapshot at script offset <paramref name="offset"/>.</summary>
    internal static SlotImage Staging(ulong offset, bool withSceneStart = true)
    {
        var h = new AdvHistory();
        var live = new AdvLiveState { ScriptNumber = 3 };
        live.Vars.SetWord(AdvLiveState.FIndex.PageMessage, 1);
        live.Vars.SetWord(22, 7);
        if (withSceneStart)
        {
            h.Push(live, 0x10, 0x20, AdvHistory.Kinds.SceneStart);
            h.PushSceneStack(0);
        }

        live.TextBlock = (int)offset;
        h.Push(live, offset, offset + 8, AdvHistory.Kinds.Message);
        h.UpdateStaging(h.Newest, live);
        return h.Staging;
    }

    [Fact]
    public void AnEmptyStagingCannotBeSaved()
    {
        var store = new SaveStore();
        var empty = new SlotImage();
        Assert.False(store.SaveManual(0, empty, T0));
        Assert.False(store.QuickSave(empty, T0));
        Assert.False(store.AutoSave(empty, T0));
        Assert.Empty(store.StoredSlots());
    }

    [Fact]
    public void ManualSaveStoresTheStagingWithDateAndTheSavedMarker()
    {
        var store = new SaveStore();
        SlotImage staging = Staging(0x40);
        Assert.True(store.SaveManual(7, staging, T0));
        SlotImage slot = store.GetSlot(7)!;
        Assert.True(slot.IsStored);
        Assert.Equal(T0, slot.Date);
        Assert.Equal(0x40UL, slot.StateA.CmdOffset);
        Assert.Equal(1, slot.HasStateB);
        Assert.Equal(7, slot.SceneVariable);
        Assert.True(slot.StateA.GetFBoolBit(HistoryRecord.FBoolIndices.IndexOf(AdvLiveState.FIndex.SavedMarker)));
        Assert.False(staging.StateA.GetFBoolBit(HistoryRecord.FBoolIndices.IndexOf(AdvLiveState.FIndex.SavedMarker)));   // the staging itself is untouched
        Assert.Equal(7, store.SystemData.LatestSlot);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SaveManual(139, staging, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SaveManual(-1, staging, T0));
    }

    [Fact]
    public void QuickSaveHasNoSavedMarkerAndLeavesTheLatestSlot()
    {
        var store = new SaveStore();
        store.SaveManual(2, Staging(0x40), T0);
        SlotImage staging = Staging(0x50, withSceneStart: false);
        Assert.True(store.QuickSave(staging, T0 with { Hour = 10 }, new SlotThumbnail(1, [1, 2, 3])));
        SlotImage q = store.GetSlot(SaveStore.QuickSlot)!;
        Assert.False(q.StateA.GetFBoolBit(HistoryRecord.FBoolIndices.IndexOf(AdvLiveState.FIndex.SavedMarker)));
        Assert.Equal(0, q.HasStateB);
        Assert.Equal(2, store.SystemData.LatestSlot);
        Assert.Equal([1, 2, 3], store.GetThumbnail(SaveStore.QuickSlot)!.Data);
        // identical to the staging except the first dword, the date and the end marker
        byte[] expected = (byte[])staging.Bytes.Clone();
        BitConverter.GetBytes(0x12AF8).CopyTo(expected, 0);
        q.Bytes.AsSpan(SlotImage.DateOffset, 7).CopyTo(expected.AsSpan(SlotImage.DateOffset));
        expected[SlotImage.EndMarkerOffset] = 0xF8;
        expected[SlotImage.EndMarkerOffset + 1] = 0x2A;
        Assert.Equal(expected, q.Bytes);
    }

    [Fact]
    public void AutoSavesShiftDownAsAFifoAndDropTheOldest()
    {
        var store = new SaveStore();
        for (int i = 0; i < 45; i++)
        {
            Assert.True(store.AutoSave(Staging(0x100UL + (ulong)i), T0 with { Minute = i }, new SlotThumbnail(2, [(byte)i])));
        }

        Assert.Equal(100, store.SystemData.LatestSlot);
        Assert.Equal(0x100UL + 44, store.GetSlot(100)!.StateA.CmdOffset);          // newest
        Assert.Equal(0x100UL + 43, store.GetSlot(101)!.StateA.CmdOffset);
        Assert.Equal(0x100UL + 6, store.GetSlot(138)!.StateA.CmdOffset);           // 39 kept: 44 down to 6
        Assert.Equal([6], store.GetThumbnail(138)!.Data);
        Assert.Equal(39, store.StoredSlots().Count());
        Assert.False(store.HasSave(SaveStore.QuickSlot));
    }

    [Fact]
    public void TheLatestSlotIsTheNewestDateAmongManualAndAutoSlots()
    {
        var store = new SaveStore();
        store.SaveManual(10, Staging(1), T0);
        store.SaveManual(3, Staging(2), T0 with { Minute = 5 });
        Assert.Equal(3, store.SystemData.LatestSlot);
        store.AutoSave(Staging(3), T0 with { Minute = 6 });
        Assert.Equal(100, store.SystemData.LatestSlot);
        store.SaveManual(50, Staging(4), T0 with { Minute = 6 });         // same date as the auto save: the lower index wins
        Assert.Equal(50, store.SystemData.LatestSlot);
        store.SaveManual(1, Staging(5), T0 with { Minute = 7 });
        Assert.Equal(1, store.SystemData.LatestSlot);
        store.QuickSave(Staging(6), T0 with { Hour = 23 });                // never the latest slot
        Assert.Equal(1, store.SystemData.LatestSlot);
        store.Delete(1);
        Assert.Equal(50, store.SystemData.LatestSlot);
    }

    [Fact]
    public void ASlotThatIsLoadedIsTheSlotThatWasSaved()
    {
        // play, save, play on, load: the live state is the saved one
        var h = new AdvHistory();
        var live = new AdvLiveState { ScriptNumber = 5 };
        live.Vars.SetWord(AdvLiveState.FIndex.PageMessage, 1);
        h.Push(live, 0x10, 0x20, AdvHistory.Kinds.SceneStart);
        h.PushSceneStack(0);
        live.BgmTrack = 12;
        live.SeVolB[4] = 3;
        live.LoopSeIds[4] = 9;
        live.TextBlock = 77;
        live.TextLine = 5;
        h.Push(live, 0x80, 0x90, AdvHistory.Kinds.Message);
        h.UpdateStaging(h.Newest, live);
        var store = new SaveStore();
        store.SaveManual(0, h.Staging, T0);

        live.BgmTrack = 99;
        live.TextBlock = 1;
        h.Push(live, 0xA0, 0xB0, AdvHistory.Kinds.Message);

        SceneRestoreResult r = store.Load(0, h, live);
        Assert.Equal(12, live.BgmTrack);
        Assert.Equal(77, live.TextBlock);
        Assert.Equal(9, live.LoopSeIds[4]);
        Assert.Equal(0x80UL, live.ScriptOffset);
        Assert.Equal(0, h.Cur);
        Assert.Equal(0, h.StackTop);
        Assert.NotNull(r);
        Assert.Throws<InvalidOperationException>(() => store.Load(1, h, live));
    }

    [Fact]
    public void SystemDataStartsAtTheBootDefaults()
    {
        var sys = new SystemData();
        Assert.Equal(SystemBlock.MagicValue, sys.Block.Magic);
        Assert.Equal(1, sys.Block.Used);
        Assert.Equal(ConfigBlock.CreateBootDefaults().Bytes.ToArray(), sys.Config.Bytes.ToArray());
        Assert.Equal(-1, sys.Config.Language);
        sys.ApplyLanguageFixup(1);
        Assert.Equal(1, sys.Config.Language);
        sys.ApplyLanguageFixup(2);                         // only a negative language is replaced
        Assert.Equal(1, sys.Config.Language);
    }

    [Fact]
    public void ReadTextFollowsTheReadmaxRule()
    {
        var sys = new SystemData();
        Assert.False(sys.IsRead(10, 1));
        sys.MarkRead(10, 5);
        Assert.True(sys.IsRead(10, 1));
        Assert.True(sys.IsRead(10, 5));
        Assert.False(sys.IsRead(10, 6));
        sys.MarkRead(10, 3);                               // never lowers
        Assert.Equal(5, sys.Block.GetReadmax(10));
        sys.MarkRead(10, 300);
        Assert.Equal(255, sys.Block.GetReadmax(10));
        sys.MarkRead(0xFB4, 9);                            // blocks above 0xFB3 are never recorded
        Assert.False(sys.IsRead(0xFB4, 1));
        Assert.False(sys.IsRead(-1, 1));
    }

    [Fact]
    public void GlobalsSyncWithTheInterpretersVariables()
    {
        var sys = new SystemData();
        var vars = new Vars();
        vars.SetG(5, 0x1234);
        vars.SetG(499, 9);
        vars.SetG(500, 7);                                 // not persistent
        sys.CaptureGlobalsFrom(vars);
        Assert.Equal(0x1234, sys.Block.GetG(5));
        var fresh = new Vars();
        sys.LoadGlobalsInto(fresh);
        Assert.Equal(0x1234, fresh.GetG(5));
        Assert.Equal(9, fresh.GetG(499));
        Assert.Equal(0, fresh.GetG(500));
    }

    [Fact]
    public void ProgressFlagsLiveInTheGWords()
    {
        var vars = new Vars();
        Assert.False(SystemData.SceneSeen(vars, 17));
        SystemData.SetSceneSeen(vars, 17);
        Assert.True(SystemData.SceneSeen(vars, 17));
        Assert.Equal(1 << 1, vars.GetG(110 + 1));
        SystemData.SetSceneDone(vars, 0x23F);
        Assert.True(SystemData.SceneDone(vars, 0x23F));
        Assert.False(SystemData.SceneDone(vars, 0x240));
        SystemData.SetEdgeSeen(vars, 564);
        SystemData.SetEdgeSeen(vars, 1267);
        SystemData.SetEdgeSeen(vars, 1268);                 // out of range: ignored
        Assert.True(SystemData.EdgeSeen(vars, 564));
        Assert.True(SystemData.EdgeSeen(vars, 1267));
        Assert.False(SystemData.EdgeSeen(vars, 1268));
        Assert.Equal(1, vars.GetG(400));
        SystemData.SetMovieSeen(vars, 1);
        Assert.True(SystemData.MovieSeen(vars, 1));
        Assert.False(SystemData.MovieSeen(vars, 0));
    }

    [Fact]
    public void UnlockBitsAndChoicesAreMarkedOnTheBlock()
    {
        var sys = new SystemData();
        sys.MarkPictureSeen(3199);
        sys.MarkPictureSeen(3200);                           // outside: ignored
        sys.MarkBgmHeard(0x207);
        sys.MarkChoiceTaken(279, 7);
        sys.MarkChoiceTaken(280, 0);
        Assert.True(sys.Block.IsPictureSeen(3199));
        Assert.False(sys.Block.IsPictureSeen(-1));
        Assert.True(sys.Block.IsBgmHeard(0x207));
        Assert.False(sys.Block.IsBgmHeard(0x208));
        Assert.True(sys.Block.IsChoiceTaken(279, 7));
        Assert.False(sys.Block.IsChoiceTaken(279, 6));
    }
}

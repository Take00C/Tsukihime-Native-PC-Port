using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;

namespace Tsuki.Tests.Synthetic.Save;

/// <summary>
/// Package K45 (issue #10): the save list's delete in the store (save/load screen 0x71C90 state 23). Manual, auto and quick slots are emptied with their
/// thumbnails, the latest slot follows (SaveImage_Commit mode 2), an emptied auto slot moves down the FIFO, and an empty slot is refused.
/// </summary>
public sealed class SaveDeletionStoreTests
{
    private static readonly SlotDate T0 = new(2026, 11, 2, 9, 0, 0);

    private static SaveStore Store()
    {
        var store = new SaveStore();
        store.SaveManual(0, SaveStoreTests.Staging(0x40), T0, new SlotThumbnail(1, [0]));
        store.SaveManual(5, SaveStoreTests.Staging(0x50), T0 with { Minute = 1 }, new SlotThumbnail(1, [5]));
        for (int i = 0; i < 3; i++)
        {
            // 100 is the newest (minute 4), 102 the oldest (minute 2)
            store.AutoSave(SaveStoreTests.Staging(0x60 + (ulong)i), T0 with { Minute = 2 + i }, new SlotThumbnail(2, [(byte)(100 + i)]));
        }

        store.QuickSave(SaveStoreTests.Staging(0x70), T0 with { Hour = 23 }, new SlotThumbnail(1, [139]));
        return store;
    }

    [Fact]
    public void AnAutoSaveIsDeletedWithItsThumbnailAndTheLatestSlotMovesOn()
    {
        SaveStore store = Store();
        Assert.Equal(100, store.SystemData.LatestSlot);
        Assert.True(store.Delete(100));
        Assert.False(store.HasSave(100));
        Assert.Null(store.GetThumbnail(100));
        Assert.Equal(101, store.SystemData.LatestSlot);                         // the next newest among 0..138
        Assert.Equal([0, 5, 101, 102, 139], store.StoredSlots());
    }

    [Fact]
    public void AManualSaveIsDeletedAndTheOthersStay()
    {
        SaveStore store = Store();
        byte[][] before = [.. new[] { 0, 100, 101, 102, 139 }.Select(i => (byte[])store.GetSlot(i)!.Bytes.Clone())];
        Assert.True(store.Delete(5));
        Assert.False(store.HasSave(5));
        Assert.Null(store.GetThumbnail(5));
        Assert.Equal(100, store.SystemData.LatestSlot);
        int k = 0;
        foreach (int i in new[] { 0, 100, 101, 102, 139 })
        {
            Assert.Equal(before[k++], store.GetSlot(i)!.Bytes);
        }
    }

    [Fact]
    public void TheQuickSaveCanBeEmptiedAndNeverMovesTheLatestSlot()
    {
        SaveStore store = Store();
        Assert.True(store.Delete(SaveStore.QuickSlot));
        Assert.False(store.HasSave(SaveStore.QuickSlot));
        Assert.Equal(100, store.SystemData.LatestSlot);
    }

    [Fact]
    public void AnEmptySlotIsRefusedAndNothingChanges()
    {
        SaveStore store = Store();
        Assert.False(store.Delete(7));
        Assert.True(store.Delete(101));
        Assert.False(store.Delete(101));                                        // already gone
        Assert.Equal(100, store.SystemData.LatestSlot);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Delete(140));
    }

    [Fact]
    public void AnEmptiedAutoSlotMovesDownTheFifoAndTheLastOneEmptyLeavesLatestZero()
    {
        SaveStore store = Store();
        Assert.True(store.Delete(101));
        store.AutoSave(SaveStoreTests.Staging(0x90), T0 with { Minute = 9 });
        // 0x73B70 moves every record of 100..137 down one, the empty one too
        Assert.Equal(0x90UL, store.GetSlot(100)!.StateA.CmdOffset);
        Assert.Equal(0x62UL, store.GetSlot(101)!.StateA.CmdOffset);
        Assert.False(store.HasSave(102));
        Assert.Null(store.GetThumbnail(102));
        Assert.Equal(0x60UL, store.GetSlot(103)!.StateA.CmdOffset);

        var single = new SaveStore();
        single.AutoSave(SaveStoreTests.Staging(0x40), T0);
        Assert.Equal(100, single.SystemData.LatestSlot);
        Assert.True(single.Delete(100));
        Assert.Equal(0, single.SystemData.LatestSlot);                          // none stored: 0, as commit mode 2 leaves it
        Assert.Empty(single.StoredSlots());
    }
}

using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package K45 (issue #10): deleting from the save lists through the game's services. Manual and auto saves are deleted and the file is written without
/// them, every other slot and the system data byte for byte as before; the quick save and empty slots are refused; a file that cannot be written is a
/// failed delete.
/// </summary>
public sealed class SaveDeletionAppTests : IDisposable
{
    private static readonly SlotDate T0 = new(2026, 11, 2, 14, 0, 0);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-k45-delete-" + Guid.NewGuid().ToString("N"));

    public SaveDeletionAppTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // manual 0 and 5, auto 100 (newest) .. 102, the quick save, thumbnails and some gallery / flowchart bits
    private static SaveStore Filled()
    {
        SaveStore store = AppRig.NewStore();
        store.SaveManual(0, Save.SaveStoreTests.Staging(0x40), T0, new SlotThumbnail(1, [1, 2, 3]));
        store.SaveManual(5, Save.SaveStoreTests.Staging(0x50), T0 with { Minute = 1 }, new SlotThumbnail(1, [5]));
        for (int i = 0; i < 3; i++)
        {
            store.AutoSave(Save.SaveStoreTests.Staging(0x60 + (ulong)i), T0 with { Minute = 2 + i }, new SlotThumbnail(2, [(byte)(100 + i)]));
        }

        store.QuickSave(Save.SaveStoreTests.Staging(0x70), T0 with { Hour = 23 }, new SlotThumbnail(1, [139]));
        store.SystemData.Block.SetPictureSeen(2000, true);
        store.SystemData.Block.SetBgmHeard(5, true);
        store.SystemData.Block.SetReadmax(100, 42);
        return store;
    }

    private static void AssertSameSlot(SaveStore before, SaveStore after, int slot)
    {
        Assert.Equal(before.GetSlot(slot)!.Bytes, after.GetSlot(slot)!.Bytes);
        Assert.Equal(before.GetThumbnail(slot)?.Format, after.GetThumbnail(slot)?.Format);
        Assert.Equal(before.GetThumbnail(slot)?.Data, after.GetThumbnail(slot)?.Data);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(5)]
    public void TheFileLosesTheDeletedSlotAndKeepsEveryOtherByteForByte(int victim)
    {
        string path = Path.Combine(_dir, "save.dat");
        var rig = new AppRig(savePath: path, store: Filled());
        Assert.True(rig.Services.Commit());
        SaveStore before = SaveFile.FromBytes(File.ReadAllBytes(path));
        Assert.True(before.HasSave(victim));

        Assert.True(rig.Services.Delete(victim));
        SaveStore after = SaveFile.FromBytes(File.ReadAllBytes(path));
        Assert.False(after.HasSave(victim));
        Assert.Equal(before.StoredSlots().Where(i => i != victim), after.StoredSlots());
        foreach (int slot in after.StoredSlots())
        {
            AssertSameSlot(before, after, slot);
        }

        // the newest save (100) is still there: the system block, flowchart and gallery bits with it, is unchanged
        Assert.Equal(before.SystemData.Block.Bytes.ToArray(), after.SystemData.Block.Bytes.ToArray());
        Assert.False(rig.Services.Slots[victim].Used);
        Assert.Null(rig.Services.Thumbnail(victim));
    }

    [Fact]
    public void DeletingTheNewestAutoSaveMovesContinueAndChangesOnlyTheLatestSlotWord()
    {
        string path = Path.Combine(_dir, "save.dat");
        var rig = new AppRig(savePath: path, store: Filled());
        Assert.True(rig.Services.Commit());
        Assert.Equal(100, rig.Services.Title.ContinueSlot);
        byte[] before = SaveFile.FromBytes(File.ReadAllBytes(path)).SystemData.Block.Bytes.ToArray();

        Assert.True(rig.Services.Delete(100));
        SaveStore after = SaveFile.FromBytes(File.ReadAllBytes(path));
        Assert.Equal(101, after.SystemData.LatestSlot);
        Assert.Equal(101, rig.Services.Title.ContinueSlot);
        byte[] now = after.SystemData.Block.Bytes.ToArray();
        const int latest = 0x16EE;
        Assert.Equal(before.AsSpan(0, latest).ToArray(), now.AsSpan(0, latest).ToArray());
        Assert.Equal(before.AsSpan(latest + 2).ToArray(), now.AsSpan(latest + 2).ToArray());
    }

    [Fact]
    public void TheQuickSaveAndEmptySlotsAreRefusedAndKept()
    {
        string path = Path.Combine(_dir, "save.dat");
        var rig = new AppRig(savePath: path, store: Filled());
        Assert.False(rig.Services.Delete(SaveStore.QuickSlot));                  // in no list
        Assert.True(rig.Services.HasQuickSave);
        Assert.False(rig.Services.Delete(7));
        Assert.False(rig.Services.Delete(120));
        Assert.False(rig.Services.Delete(-1));
        Assert.False(rig.Services.Delete(140));
        Assert.False(File.Exists(path));                                        // nothing was written
        Assert.Equal(6, rig.Services.Slots.Count(s => s.Used));
    }

    [Fact]
    public void ADeleteWhoseFileCannotBeWrittenIsAFailedDelete()
    {
        // save.dat as a folder: the atomic write cannot move its temporary file over it
        string path = Path.Combine(_dir, "save.dat");
        Directory.CreateDirectory(path);
        var rig = new AppRig(savePath: path, store: Filled());
        Assert.False(rig.Services.Delete(101));
        Assert.False(rig.Services.Slots[101].Used);                              // gone from memory: the next commit that works writes that
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}

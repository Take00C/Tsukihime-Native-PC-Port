using System.Buffers.Binary;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;

namespace Tsuki.Tests.Synthetic.Save;

/// <summary>The save file: round trips, atomic writes, damaged and truncated files, the PS4 image mapping.</summary>
public sealed class SaveFileTests : IDisposable
{
    private static readonly SlotDate T0 = new(2026, 10, 26, 9, 0, 0);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-savetest-" + Guid.NewGuid().ToString("N"));

    public SaveFileTests()
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

    private static SaveStore SampleStore(bool thumbnails = true)
    {
        var store = new SaveStore();
        store.SaveManual(0, SaveStoreTests.Staging(0x40), T0, thumbnails ? new SlotThumbnail(1, Enumerable.Range(0, 300).Select(i => (byte)i).ToArray()) : null);
        store.SaveManual(5, SaveStoreTests.Staging(0x50, withSceneStart: false), T0 with { Minute = 3 });
        store.AutoSave(SaveStoreTests.Staging(0x60), T0 with { Minute = 4 }, thumbnails ? new SlotThumbnail(7, [9, 9]) : null);
        store.QuickSave(SaveStoreTests.Staging(0x70), T0 with { Minute = 5 });
        SystemBlock sys = store.SystemData.Block;
        sys.SetG(10, 0xABCD);
        sys.SetReadmax(100, 42);
        sys.SetPictureSeen(2000, true);
        sys.SetBgmHeard(5, true);
        sys.SetChoiceTaken(3, 0x81);
        store.SystemData.Config.MusicVolume = 77;
        return store;
    }

    private static void AssertSame(SaveStore a, SaveStore b)
    {
        Assert.Equal(a.SystemData.Block.Bytes.ToArray(), b.SystemData.Block.Bytes.ToArray());
        Assert.Equal(a.StoredSlots().ToArray(), b.StoredSlots().ToArray());
        foreach (int i in a.StoredSlots())
        {
            Assert.Equal(a.GetSlot(i)!.Bytes, b.GetSlot(i)!.Bytes);
            Assert.Equal(a.GetThumbnail(i)?.Format, b.GetThumbnail(i)?.Format);
            Assert.Equal(a.GetThumbnail(i)?.Data, b.GetThumbnail(i)?.Data);
        }
    }

    // -- round trips -----------------------------------------------------------------------------------------------------

    [Fact]
    public void BytesRoundTrip()
    {
        SaveStore store = SampleStore();
        byte[] bytes = SaveFile.ToBytes(store);
        SaveStore back = SaveFile.FromBytes(bytes);
        AssertSame(store, back);
        Assert.Equal(bytes, SaveFile.ToBytes(back));               // deterministic
        Assert.Equal(77, back.SystemData.Config.MusicVolume);
        Assert.Equal(42, back.SystemData.Block.GetReadmax(100));
        Assert.Equal(4, back.StoredSlots().Count());
        Assert.Equal(100, back.SystemData.LatestSlot);
    }

    [Fact]
    public void AnEmptyStoreRoundTrips()
    {
        var store = new SaveStore();
        SaveStore back = SaveFile.FromBytes(SaveFile.ToBytes(store));
        AssertSame(store, back);
        Assert.Empty(back.StoredSlots());
    }

    [Fact]
    public void StreamsRoundTrip()
    {
        SaveStore store = SampleStore();
        using var ms = new MemoryStream();
        SaveFile.Write(ms, store);
        ms.Position = 0;
        AssertSame(store, SaveFile.Read(ms));
    }

    [Fact]
    public void TheLoadedSlotsLoad()
    {
        SaveStore back = SaveFile.FromBytes(SaveFile.ToBytes(SampleStore()));
        var h = new AdvHistory();
        var live = new AdvLiveState();
        back.Load(0, h, live);
        Assert.Equal(0x40UL, live.ScriptOffset);
        Assert.Equal(3, live.ScriptNumber);
    }

    // -- atomic writes ---------------------------------------------------------------------------------------------------

    [Fact]
    public void WriteAtomicLeavesOneFileAndAFallback()
    {
        string path = Path.Combine(_dir, "sub", "save.bin");
        SaveStore first = SampleStore(false);
        SaveFile.WriteAtomic(path, first);
        Assert.True(File.Exists(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));

        SaveStore second = SampleStore(true);
        SaveFile.WriteAtomic(path, second);
        string[] files = Directory.GetFiles(Path.GetDirectoryName(path)!).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal([path, path + ".bak"], files);
        AssertSame(second, SaveFile.FromBytes(File.ReadAllBytes(path)));
        AssertSame(first, SaveFile.FromBytes(File.ReadAllBytes(path + ".bak")));
    }

    [Fact]
    public void AFailedWriteKeepsTheOldFileAndLeavesNoTempFile()
    {
        string path = Path.Combine(_dir, "save.bin");
        SaveStore good = SampleStore();
        SaveFile.WriteAtomic(path, good);
        byte[] before = File.ReadAllBytes(path);

        var bad = new SaveStore();
        bad.SaveManual(1, SaveStoreTests.Staging(1), T0, new SlotThumbnail(0, [1]));     // format 0 with data: refused
        Assert.Throws<ArgumentException>(() => SaveFile.WriteAtomic(path, bad));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    [Fact]
    public void ReadWithFallbackUsesTheBackupOfADamagedFile()
    {
        string path = Path.Combine(_dir, "save.bin");
        Assert.Null(SaveFile.ReadWithFallback(path, out bool usedBackup));
        Assert.False(usedBackup);

        SaveStore first = SampleStore(false);
        SaveFile.WriteAtomic(path, first);
        SaveFile.WriteAtomic(path, SampleStore(true));
        Assert.NotNull(SaveFile.ReadWithFallback(path, out usedBackup));
        Assert.False(usedBackup);

        byte[] damaged = File.ReadAllBytes(path);
        damaged[damaged.Length / 2] ^= 0x40;
        File.WriteAllBytes(path, damaged);
        SaveStore? recovered = SaveFile.ReadWithFallback(path, out usedBackup);
        Assert.True(usedBackup);
        AssertSame(first, recovered!);

        File.WriteAllBytes(path + ".bak", damaged);
        Assert.Throws<SaveFormatException>(() => SaveFile.ReadWithFallback(path, out _));
    }

    // -- damage ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryTruncationIsRejected()
    {
        byte[] file = SaveFile.ToBytes(SampleStore());
        var lengths = new SortedSet<int>(Enumerable.Range(0, 300));
        for (int l = 300; l < file.Length; l += 331)
        {
            lengths.Add(l);
        }

        foreach (int d in new[] { 1, 2, 4, 15, 16, 17, 20, 21, 8, 9 })
        {
            lengths.Add(file.Length - d);
        }

        foreach (int len in lengths)
        {
            Assert.Throws<SaveFormatException>(() => SaveFile.FromBytes(file.AsSpan(0, len)));
        }
    }

    [Fact]
    public void AnyChangedByteIsRejected()
    {
        byte[] file = SaveFile.ToBytes(SampleStore());
        for (int i = 0; i < file.Length; i += 97)
        {
            byte[] copy = (byte[])file.Clone();
            copy[i] ^= 0x01;
            Assert.Throws<SaveFormatException>(() => SaveFile.FromBytes(copy));
        }

        for (int i = file.Length - 40; i < file.Length; i++)
        {
            byte[] copy = (byte[])file.Clone();
            copy[i] ^= 0x80;
            Assert.Throws<SaveFormatException>(() => SaveFile.FromBytes(copy));
        }
    }

    [Fact]
    public void GarbageAndForeignFilesAreRejected()
    {
        Assert.Throws<SaveFormatException>(() => SaveFile.FromBytes([]));
        Assert.Throws<SaveFormatException>(() => SaveFile.FromBytes(new byte[1000]));
        Assert.Throws<SaveFormatException>(() => SaveFile.FromBytes(new Random(5).GetItems<byte>(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), 200_000)));
        Assert.Throws<SaveFormatException>(() => SaveFile.FromBytes("TSUKISAV"u8.ToArray()));
        byte[] wrongMagic = SaveFile.ToBytes(new SaveStore());
        wrongMagic[0] = (byte)'X';
        Assert.Throws<SaveFormatException>(() => SaveFile.FromBytes(wrongMagic));
        using var ms = new MemoryStream(new byte[10]);
        Assert.Throws<SaveFormatException>(() => SaveFile.Read(ms));
    }

    [Fact]
    public void AFileOfANewerVersionIsRefusedWithAnExplanation()
    {
        byte[] file = SaveFile.ToBytes(SampleStore());
        List<(int Start, int Length)> layout = RecordLayout(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), 2);
        FixChecksums(file, layout);
        var ex = Assert.Throws<SaveFormatException>(() => SaveFile.FromBytes(file));
        Assert.Contains("newer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsistentButMeaninglessContentIsRejectedToo()
    {
        // each mutation keeps every checksum valid, so only the structural checks can catch it
        void Expect(string why, Action<byte[]> mutate, string messagePart)
        {
            byte[] file = SaveFile.ToBytes(SampleStore());
            List<(int Start, int Length)> layout = RecordLayout(file);
            mutate(file);
            FixChecksums(file, layout);
            var ex = Assert.Throws<SaveFormatException>(() => SaveFile.FromBytes(file));
            Assert.True(ex.Message.Contains(messagePart, StringComparison.OrdinalIgnoreCase), $"{why}: {ex.Message}");
        }

        int firstSlot = 0x20 + SystemBlock.Size + 4;
        Expect("slot without its end marker", f => f[firstSlot + 8 + SlotImage.EndMarkerOffset] = 0, "complete save");
        Expect("slot without magic", f => f[firstSlot + 8] = 0, "complete save");
        Expect("bad state-B flag", f => f[firstSlot + 8 + 4] = 5, "state-B");
        Expect("implausible date", f => f[firstSlot + 8 + SlotImage.DateOffset + 2] = 13, "date");
        Expect("negative serial", f => BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(firstSlot + 8 + SlotImage.StateAOffset + 4), -1), "serial");
        Expect("slot index out of range", f => BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(firstSlot), 140), "index");
        Expect("slots out of order", f => BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(firstSlot), 9), "index");
        Expect("system block without magic", f => f[0x20] = 0, "magic");
        Expect("latest slot beyond the menu slots", f => BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x20 + 0x16EE), 139), "latest");
        Expect("thumbnail format without data", f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(firstSlot + 4), 0), "thumbnail");
        Expect("thumbnail too large", f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(firstSlot + 4), 0x7FFFFFFF), "thumbnail");
        Expect("too many slot records", f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x18), 141), "slot");
        Expect("different block sizes", f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x14), 0x7000), "sizes");
    }

    /// <summary>The (start, length without the CRC) of every slot record of a file image written by <see cref="SaveFile"/>.</summary>
    private static List<(int Start, int Length)> RecordLayout(byte[] pristine)
    {
        var list = new List<(int, int)>();
        int pos = 0x20 + SystemBlock.Size + 4;
        uint records = BinaryPrimitives.ReadUInt32LittleEndian(pristine.AsSpan(0x18));
        for (uint r = 0; r < records; r++)
        {
            int length = 8 + SlotImage.Size + (int)BinaryPrimitives.ReadUInt32LittleEndian(pristine.AsSpan(pos + 4));
            list.Add((pos, length));
            pos += length + 4;
        }

        return list;
    }

    private static uint Crc(ReadOnlySpan<byte> d)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in d)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>Recomputes the checksums of a file image after a test changed its content; the record layout is the original one.</summary>
    private static void FixChecksums(byte[] f, List<(int Start, int Length)> layout)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x1C), Crc(f.AsSpan(0, 0x1C)));
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x20 + SystemBlock.Size), Crc(f.AsSpan(0x20, SystemBlock.Size)));
        foreach ((int start, int length) in layout)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(start + length), Crc(f.AsSpan(start, length)));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(f.Length - 8), (uint)f.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(f.Length - 4), Crc(f.AsSpan(0, f.Length - 4)));
    }

    // -- PS4 images ------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePs4ImageMappingRoundTrips()
    {
        SaveStore store = SampleStore(false);
        byte[] image = SaveFile.ExportPs4Image(store);
        Assert.Equal(SaveFile.Ps4ImageSize, image.Length);
        Assert.Equal(SlotImage.Magic, BinaryPrimitives.ReadUInt16LittleEndian(image));
        Assert.Equal(SlotImage.Magic, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x1A00 + (3 * 0x7140))));   // an empty slot carries the magic
        SaveStore back = SaveFile.ImportPs4Image(image);
        AssertSame(store, back);
        Assert.Equal(store.GetSlot(139)!.Bytes, back.GetSlot(139)!.Bytes);
        Assert.Throws<SaveFormatException>(() => SaveFile.ImportPs4Image(new byte[100]));
        Assert.Throws<SaveFormatException>(() => SaveFile.ImportPs4Image(new byte[SaveFile.Ps4ImageSize]));   // no system block
    }

    [Fact]
    public void ThePs4ImageSlotOffsetsFollowTheConsoleLayout()
    {
        var store = new SaveStore();
        store.SaveManual(2, SaveStoreTests.Staging(0x44), T0);
        byte[] image = SaveFile.ExportPs4Image(store);
        int off = 0x1A00 + (2 * 0x7140);
        Assert.Equal(0x44UL, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(off + 8 + 8)));     // state A cmd_offset
        Assert.Equal(store.GetSlot(2)!.Bytes, image.AsSpan(off, SlotImage.Size).ToArray());
    }
}

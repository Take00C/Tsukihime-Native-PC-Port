using System.Buffers.Binary;
using System.IO.Compression;
using Tsuki.App.Services;
using Tsuki.Engine.Save;
using Tsuki.Tests.Synthetic.Save;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.App;

public sealed class ThumbnailSecurityTests
{
    private static SlotThumbnail CompressedImage(int width, int height, int? pixelBytes = null)
    {
        using var bytes = new MemoryStream();
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)height);
        bytes.Write(header);
        using (var compressed = new ZLibStream(bytes, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var chunk = new byte[8192];
            for (int remaining = pixelBytes ?? width * height * 4; remaining > 0; remaining -= chunk.Length)
            {
                compressed.Write(chunk.AsSpan(0, Math.Min(remaining, chunk.Length)));
            }
        }

        return new SlotThumbnail(ThumbnailCodec.Format, bytes.ToArray());
    }

    [Fact]
    public void ASmallValidSaveCannotInflateAnOversizedThumbnailIntoTheMenuCache()
    {
        SlotThumbnail thumbnail = CompressedImage(4096, 4096);
        Assert.True(thumbnail.Data.Length < 100_000);
        var store = new SaveStore();
        var date = new SlotDate(2026, 10, 31, 12, 0, 0);
        store.SaveManual(0, SaveStoreTests.Staging(0x40), date, thumbnail);
        byte[] file = SaveFile.ToBytes(store);
        Assert.True(file.Length < 150_000);
        SaveStore loaded = SaveFile.FromBytes(file);
        Assert.True(loaded.HasSave(0));
        var rig = new AppRig(store: loaded);
        Assert.Null(rig.Services.Thumbnail(0));
    }

    [Fact]
    public void RepeatedRequestsForAFailedThumbnailDoNotAllocateAnotherPixelBuffer()
    {
        var store = new SaveStore();
        var date = new SlotDate(2026, 10, 31, 12, 0, 0);
        Assert.True(store.SaveManual(0, SaveStoreTests.Staging(0x40), date, CompressedImage(320, 180, pixelBytes: 4)));
        var rig = new AppRig(store: SaveFile.FromBytes(SaveFile.ToBytes(store)));
        Assert.True(rig.Store.HasSave(0));
        Assert.Null(rig.Services.Thumbnail(0));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            Assert.Null(rig.Services.Thumbnail(0));
        }

        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 64 * 1024);
    }

    [Fact]
    public void ReplacingTheSourceInvalidatesBothFailedAndSuccessfulResults()
    {
        var rig = new AppRig();
        var date = new SlotDate(2026, 10, 31, 12, 0, 0);
        SlotImage staging = SaveStoreTests.Staging(0x40);
        Assert.True(rig.Store.SaveManual(0, staging, date, CompressedImage(320, 180, pixelBytes: 4)));
        Assert.Null(rig.Services.Thumbnail(0));

        SlotThumbnail valid = CompressedImage(8, 8);
        Assert.True(rig.Store.SaveManual(0, staging, date, valid));
        UiThumbnail first = Assert.IsType<UiThumbnail>(rig.Services.Thumbnail(0));
        Assert.Same(first, rig.Services.Thumbnail(0));

        SlotThumbnail replacement = valid with { };
        Assert.Equal(valid, replacement);
        Assert.NotSame(valid, replacement);
        Assert.True(rig.Store.SaveManual(0, staging, date, replacement));
        UiThumbnail second = Assert.IsType<UiThumbnail>(rig.Services.Thumbnail(0));
        Assert.NotSame(first, second);
        Assert.True(second.Version > first.Version);

        Assert.True(rig.Store.SaveManual(0, staging, date, CompressedImage(320, 180, pixelBytes: 4)));
        Assert.Null(rig.Services.Thumbnail(0));
        Assert.True(rig.Store.SaveManual(0, staging, date, valid));
        UiThumbnail recovered = Assert.IsType<UiThumbnail>(rig.Services.Thumbnail(0));
        Assert.True(recovered.Version > second.Version);
        Assert.Same(recovered, rig.Services.Thumbnail(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingAThumbnailEvictsItsCachedImage(bool deleteSlot)
    {
        var rig = new AppRig();
        var date = new SlotDate(2026, 10, 31, 12, 0, 0);
        SlotImage staging = SaveStoreTests.Staging(0x40);
        SlotThumbnail thumbnail = CompressedImage(8, 8);
        Assert.True(rig.Store.SaveManual(0, staging, date, thumbnail));
        UiThumbnail first = Assert.IsType<UiThumbnail>(rig.Services.Thumbnail(0));

        if (deleteSlot)
        {
            Assert.True(rig.Services.Delete(0));
        }
        else
        {
            Assert.True(rig.Store.SaveManual(0, staging, date));
            Assert.Null(rig.Services.Thumbnail(0));
        }

        Assert.True(rig.Store.SaveManual(0, staging, date, thumbnail));
        UiThumbnail restored = Assert.IsType<UiThumbnail>(rig.Services.Thumbnail(0));
        Assert.NotSame(first, restored);
        Assert.True(restored.Version > first.Version);
    }

    [Theory]
    [InlineData(320, 180)]
    [InlineData(8, 8)]
    public void NormalThumbnailsStillDecode(int width, int height)
    {
        var decoded = ThumbnailCodec.Decode(CompressedImage(width, height));
        Assert.NotNull(decoded);
        Assert.Equal(width * height * 4, decoded.Value.Rgba.Length);
    }
}

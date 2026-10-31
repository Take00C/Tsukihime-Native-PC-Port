using Tsuki.Render.Textures;
using Tsuki.Tests.Render.Smoke;

namespace Tsuki.Tests.Render.Compositor;

/// <summary>
/// Package K11 (memory growth): the texture cache holds to its byte caps over many uploads of mixed sizes, and every GL texture it ever created is either resident,
/// in the spare pool or deleted, so a long play cannot accumulate texture objects. Needs the local GPU; never skips.
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class TextureCacheBoundTests
{
    [Fact]
    public void ResidentAndSpareBytesStayUnderTheirCapsAndEveryTextureIsAccountedFor()
    {
        using GlContext ctx = GlContext.CreateHidden();
        const long Capacity = 3L << 20;
        const long Spare = 1L << 20;
        using var cache = new TextureCache(ctx.Device.Gl, Capacity, Spare);
        (int W, int H)[] shapes = [(64, 64), (128, 64), (256, 128), (100, 50), (32, 32), (192, 192)];
        var rng = new Random(9);
        for (int i = 0; i < 3000; i++)
        {
            (int w, int h) = shapes[rng.Next(shapes.Length)];
            int id = rng.Next(0, 400);                                  // revisits replace the texture of the key
            cache.Upload(TextureUploadTests.Key(id), w, h, new byte[w * h * 4]);
            TextureCacheStats s = cache.Stats;
            Assert.True(s.ResidentBytes <= Capacity, $"upload {i}: resident {s.ResidentBytes} bytes over the cap of {Capacity}");
            Assert.True(s.SpareBytes <= Spare, $"upload {i}: spare {s.SpareBytes} bytes over the cap of {Spare}");
            Assert.Equal(cache.Count, s.Resident);
        }

        TextureCacheStats end = cache.Stats;
        long created = end.Allocations;
        Assert.True(end.Evictions > 1000, "the run has to make the cache evict many times");
        Assert.True(end.Recycles > 0, "evicted textures of a size that comes again are reused");
        Assert.True(created < 3000, $"{created} textures were allocated for 3000 uploads: recycling does not work");
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void ATouchedTextureIsTheNewestAndSurvivesTheEvictionsThatFollow()
    {
        // package K15: a tile a look-ahead uploaded and nothing drew yet is at the tail; the frame that needs it touches it before the uploads of the same frame evict the tail
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl, 4L * 64 * 64 * 4, 0);
        for (int i = 0; i < 4; i++)
        {
            cache.Upload(TextureUploadTests.Key(i), 64, 64, new byte[64 * 64 * 4]);
        }

        Assert.True(cache.Touch(TextureUploadTests.Key(0)));             // the oldest becomes the newest
        Assert.False(cache.Touch(TextureUploadTests.Key(99)));           // a missing key is not created
        for (int i = 10; i < 13; i++)
        {
            cache.Upload(TextureUploadTests.Key(i), 64, 64, new byte[64 * 64 * 4]);
        }

        Assert.True(cache.Contains(TextureUploadTests.Key(0)), "the touched texture was evicted");
        Assert.False(cache.Contains(TextureUploadTests.Key(1)));
        Assert.False(cache.Contains(TextureUploadTests.Key(2)));
        Assert.False(cache.Contains(TextureUploadTests.Key(3)));
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void ADisposedCacheDeletesEveryTexture()
    {
        using GlContext ctx = GlContext.CreateHidden();
        var cache = new TextureCache(ctx.Device.Gl, 2L << 20, 1L << 20);
        for (int i = 0; i < 100; i++)
        {
            cache.Upload(TextureUploadTests.Key(i), 64, 64, new byte[64 * 64 * 4]);
        }

        cache.Dispose();
        Assert.Equal(0, cache.Stats.Resident);
        Assert.Equal(0, cache.Stats.ResidentBytes);
        Assert.Equal(0, cache.Stats.SpareBytes);
        Assert.Null(ctx.Device.TakeError());
    }
}

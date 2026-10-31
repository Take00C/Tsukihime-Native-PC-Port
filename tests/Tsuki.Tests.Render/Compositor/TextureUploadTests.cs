using System.Security.Cryptography;
using Tsuki.Engine.Contracts;
using Tsuki.Render.Compositor;
using Tsuki.Render.Textures;
using Tsuki.Tests.Render.Smoke;

namespace Tsuki.Tests.Render.Compositor;

/// <summary>
/// The texture cache, the staging pool, the decode workers and the upload scheduler against S4's five upload rules
/// (ARCHITECTURE 4.3; S4.md 5.3), functionally: no timing, so no gpu.lock. Needs the local GPU; never skips.
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class TextureUploadTests(ITestOutputHelper output)
{
    private const int TileSide = 512;                       // 1 MiB RGBA8 tiles, the IMG1481 tile size
    private const int TileBytes = TileSide * TileSide * 4;

    [Fact]
    public void SlicesAreCappedInBytesAndAlwaysUploadAtLeastOneTile()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl);
        var pool = new StagingPool(1L << 30);
        var sched = new UploadScheduler(cache, pool);
        for (int i = 0; i < 40; i++)
        {
            sched.Enqueue(Tile(pool, i, TileSide, TileSide));
        }

        var slices = new List<UploadSliceStats>();
        while (sched.Pending > 0)
        {
            slices.Add(sched.UploadSlice());
        }

        Assert.Equal([16, 16, 8], slices.Select(s => s.Tiles));
        Assert.All(slices, s => Assert.True(s.Bytes <= UploadScheduler.DefaultMaxBytesPerFrame));
        Assert.Equal(40, slices.Sum(s => s.Allocations));     // rule 4: one allocation per uploaded tile, never ahead

        // A tile larger than the cap goes up alone (rule 2: at least one tile).
        sched.Enqueue(Tile(pool, 100, 3072, 2048));          // 24 MiB
        sched.Enqueue(Tile(pool, 101, TileSide, TileSide));
        UploadSliceStats big = sched.UploadSlice();
        Assert.Equal(1, big.Tiles);
        Assert.Equal(24L << 20, big.Bytes);
        Assert.Equal(1, sched.UploadSlice().Tiles);

        for (int i = 0; i < 40; i += 13)
        {
            Assert.Equal(Pixels(i, TileSide, TileSide), cache.ReadBack(Key(i)));
        }

        Assert.Equal(Pixels(100, 3072, 2048), cache.ReadBack(Key(100)));
        Assert.Equal(0, pool.OutstandingBytes);               // rule 5: buffers go back once on the GPU
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void LookAheadTilesUseTheLeftoverOfASliceAndAPromotedOneJumpsTheQueue()
    {
        // package K8: tiles the look-ahead decoded wait in a background queue (4 MiB of them go up with each slice, behind the urgent tiles) and move to the front
        // of the upload queue when a frame names them
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl);
        var pool = new StagingPool(1L << 30);
        var sched = new UploadScheduler(cache, pool);
        for (int i = 0; i < 12; i++)
        {
            sched.EnqueueBackground(Tile(pool, i, TileSide, TileSide));
        }

        Assert.Equal(12, sched.Pending);
        UploadSliceStats first = sched.UploadSlice();
        Assert.Equal(4, first.Tiles);                               // 4 MiB of look-ahead per slice
        Assert.True(cache.Contains(Key(0)) && cache.Contains(Key(3)) && !cache.Contains(Key(4)));
        sched.EnqueueBackground(Tile(pool, 5, TileSide, TileSide));      // decoded twice while still waiting: the second copy is dropped
        Assert.Equal(8, sched.Pending);

        Assert.True(sched.Promote(Key(11)));
        Assert.False(sched.Promote(Key(11)));
        sched.Enqueue(Tile(pool, 50, TileSide, TileSide));          // an urgent tile
        UploadSliceStats second = sched.UploadSlice();
        Assert.Equal(6, second.Tiles);                              // the promoted one and the urgent one, then 4 MiB of look-ahead
        Assert.True(cache.Contains(Key(11)) && cache.Contains(Key(50)) && cache.Contains(Key(4)) && cache.Contains(Key(7)) && !cache.Contains(Key(8)));
        while (sched.Pending > 0)
        {
            sched.UploadSlice();
        }

        Assert.Equal(0, pool.OutstandingBytes);
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void TheCacheEvictsByBytesAndRecyclesTexturesOfTheSameSize()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl, capacityBytes: 4L * TileBytes, spareCapacityBytes: 2L * TileBytes);
        for (int i = 0; i < 6; i++)
        {
            cache.Upload(Key(i), TileSide, TileSide, Pixels(i, TileSide, TileSide));
            Assert.True(cache.TryGetTexture(Key(0), out _, out _));   // touch 0 so it stays
        }

        TextureCacheStats s = cache.Stats;
        Assert.Equal(5, s.Allocations);                             // the 6th upload reuses the texture evicted by the 5th
        Assert.Equal(1, s.Recycles);
        Assert.Equal(4, s.Resident);
        Assert.Equal(4L * TileBytes, s.ResidentBytes);
        Assert.Equal(2, s.Evictions);
        Assert.True(cache.Contains(Key(0)), "the most recently used tile was evicted");
        Assert.False(cache.Contains(Key(1)));

        // The next two uploads of that size reuse the evicted textures (glTexSubImage2D, rule 3) instead of allocating.
        Assert.False(cache.Upload(Key(10), TileSide, TileSide, Pixels(10, TileSide, TileSide)));
        Assert.False(cache.Upload(Key(11), TileSide, TileSide, Pixels(11, TileSide, TileSide)));
        Assert.Equal(3, cache.Stats.Recycles);
        Assert.Equal(5, cache.Stats.Allocations);
        Assert.Equal(Pixels(11, TileSide, TileSide), cache.ReadBack(Key(11)));

        // Transition parameters live on the texture (texture +0xF0, compositing 3).
        cache.SetTransitionParams(Key(11), TransitionParams.ForRule(0.5f));
        Assert.True(cache.TryGetTexture(Key(11), out _, out TransitionParams? tp));
        Assert.Equal(TransitionParams.ForRule(0.5f), tp);
        cache.SetTransitionParams(Key(11), null);
        Assert.True(cache.TryGetTexture(Key(11), out _, out tp));
        Assert.Null(tp);
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void DecodeWorkersWaitOnTheBoundedStagingPool()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl);
        var pool = new StagingPool(8L * TileBytes);
        var sched = new UploadScheduler(cache, pool, maxBytesPerFrame: 2L * TileBytes);
        const int Tiles = 64;
        using (var workers = new DecodeWorkers(sched))
        {
            Assert.InRange(workers.WorkerCount, 1, CpuTopology.PhysicalCores);          // rule 1
            for (int i = 0; i < Tiles; i++)
            {
                int id = i;
                workers.Enqueue(new DecodeJob(Key(id), TileSide, TileSide, px => Fill(px, id)));
            }

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while ((workers.Pending > 0 || sched.Pending > 0) && DateTime.UtcNow < deadline)
            {
                sched.UploadSlice();                                                       // 2 tiles per "frame"
                Thread.Sleep(2);
            }

            Assert.Null(workers.Fault);
            output.WriteLine($"workers {workers.WorkerCount} (physical cores {CpuTopology.PhysicalCores}); decoded {workers.Decoded}; " +
                $"pool peak {pool.PeakTotalBytes >> 20} MiB of {pool.BudgetBytes >> 20} MiB, waits {pool.Waits}, allocations {pool.Allocations}");
        }

        Assert.Equal(Tiles, cache.Count);
        Assert.True(pool.PeakTotalBytes <= pool.BudgetBytes, $"pool peak {pool.PeakTotalBytes}");   // rule 5
        Assert.True(pool.Waits > 0, "the workers never had to wait: the bound was not exercised");
        Assert.True(pool.Allocations <= 8, $"{pool.Allocations} staging buffers allocated");
        for (int i = 0; i < Tiles; i += 9)
        {
            Assert.Equal(Pixels(i, TileSide, TileSide), cache.ReadBack(Key(i)));
        }

        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void TheExecutorDrawsFromTheCacheAndSkipsTilesThatAreNotResident()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl);
        using var c = new GpuCompositor(ctx.Device.Gl, 64, 64) { Filter = SamplingFilter.Nearest };
        byte[] red = new byte[16 * 16 * 4];
        for (int i = 0; i < red.Length; i += 4)
        {
            red[i] = 255;
            red[i + 3] = 255;
        }

        cache.Upload(Key(1), 16, 16, red);
        var exec = new DisplayListExecutor(c, cache);
        var list = new DisplayList();
        list.Reset(0);
        list.Add(Quads.Fill(0, 0, 64, 64, Quads.Rgba(0, 0, 255, 255), DrawEffect.Normal, blendState: 0));
        list.Add(Quads.Textured(Key(1), 0, 0, 16, 16, 0, 0, 1, 1, Quads.White(), DrawEffect.Normal));
        list.Add(Quads.Textured(Key(2), 16, 0, 32, 16, 0, 0, 1, 1, Quads.White(), DrawEffect.Normal));   // not resident
        // A cyclic (fan-order) quad is drawn like the strip-order one.
        DrawCmd strip = Quads.Textured(Key(1), 32, 32, 48, 48, 0, 0, 1, 1, Quads.White(), DrawEffect.Normal);
        list.Add(strip with { V2 = strip.V3, V3 = strip.V2 });
        list.Seal();
        ExecuteStats s = exec.Execute(list);
        byte[] f = c.Read(c.Frame);

        Assert.Equal(1, s.MissingTextures);
        Assert.Equal(3, s.Draws);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Px(f, 64, 5, 5));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Px(f, 64, 20, 5));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Px(f, 64, 32, 32));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Px(f, 64, 47, 47));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Px(f, 64, 48, 48));
        Assert.Null(ctx.Device.TakeError());
    }

    internal static TextureRef Key(int i) => TextureRef.ForPictureTile(1, 7, i);

    internal static byte[] Pixels(int id, int w, int h)
    {
        var a = new byte[w * h * 4];
        Fill(a, id);
        return a;
    }

    internal static void Fill(Span<byte> px, int id)
    {
        uint x = (uint)(id * 2654435761u) | 1;
        for (int i = 0; i < px.Length; i += 4)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            px[i] = (byte)x;
            px[i + 1] = (byte)(x >> 8);
            px[i + 2] = (byte)(x >> 16);
            px[i + 3] = (byte)(x >> 24);
        }
    }

    private static DecodedTile Tile(StagingPool pool, int id, int w, int h)
    {
        StagingBuffer b = pool.Rent(w * h * 4);
        Fill(b.Span, id);
        return new DecodedTile(Key(id), w, h, b);
    }

    private static byte[] Px(byte[] frame, int width, int x, int y) => frame.AsSpan(((y * width) + x) * 4, 4).ToArray();

    internal static string Sha1(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(SHA1.HashData(b));
}

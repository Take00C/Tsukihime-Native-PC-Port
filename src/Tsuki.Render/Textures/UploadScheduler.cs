// Upload scheduler (ARCHITECTURE 4.3, S4.md 5.3): decoded tiles wait in a queue and the main thread
// uploads a slice of them once per frame, before drawing.
//   rule 2: each slice is capped in bytes (16 MiB) and always holds at least one tile; a CPU-time cap
//           does not work (the driver defers the work to the swap);
//   rule 3: straight from client memory, glTexImage2D for a new texture, glTexSubImage2D for a
//           recycled one; no PBO ring;
//   rule 4: a texture is allocated only when its tile is uploaded (or recycled from the cache), so a
//           frame allocates at most one slice's worth;
//   rule 5: the staging buffer goes back to the bounded pool as soon as the tile is on the GPU.
using System.Collections.Concurrent;
using System.Diagnostics;
using Tsuki.Engine.Contracts;

namespace Tsuki.Render.Textures;

/// <summary>A decoded tile waiting for upload.</summary>
/// <param name="Key">The texture it becomes.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Pixels">RGBA8 rows, top-down (<c>Width * Height * 4</c> bytes), rented from the staging pool.</param>
/// <param name="Graded">True when the decode worker already applied the key's colour grade (<see cref="TextureCache.Grader"/>).</param>
public readonly record struct DecodedTile(TextureRef Key, int Width, int Height, StagingBuffer Pixels, bool Graded = false);

/// <summary>What one <see cref="UploadScheduler.UploadSlice"/> did.</summary>
/// <param name="Tiles">Tiles uploaded.</param>
/// <param name="Bytes">Bytes uploaded.</param>
/// <param name="Allocations">Textures allocated (glTexImage2D).</param>
/// <param name="Recycles">Textures recycled (glTexSubImage2D).</param>
/// <param name="CpuMilliseconds">Time spent in the upload calls.</param>
/// <param name="Pending">Tiles still queued afterwards.</param>
public readonly record struct UploadSliceStats(int Tiles, long Bytes, int Allocations, int Recycles, double CpuMilliseconds, int Pending);

/// <summary>Queues decoded tiles (any thread) and uploads them in byte-capped slices (GL thread).</summary>
public sealed class UploadScheduler
{
    /// <summary>The default slice cap: 16 MiB per frame (S4 rule 2).</summary>
    public const long DefaultMaxBytesPerFrame = 16L << 20;

    /// <summary>Bytes of look-ahead tiles uploaded per slice besides the tiles a frame waits for (package K8): 4 MiB, four tiles.</summary>
    public const long BackgroundBytesPerFrame = 4L << 20;

    private readonly ConcurrentQueue<DecodedTile> _queue = new();
    private readonly ConcurrentDictionary<TextureRef, DecodedTile> _background = new();
    private readonly ConcurrentQueue<TextureRef> _backgroundOrder = new();
    private int _pending;
    private DecodedTile? _carry;

    /// <summary>Creates a scheduler.</summary>
    /// <param name="cache">Where the tiles go.</param>
    /// <param name="pool">Where their staging buffers go back.</param>
    /// <param name="maxBytesPerFrame">Slice cap in bytes (at least one tile is always uploaded).</param>
    public UploadScheduler(TextureCache cache, StagingPool pool, long maxBytesPerFrame = DefaultMaxBytesPerFrame)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytesPerFrame, 1);
        Cache = cache;
        Pool = pool;
        MaxBytesPerFrame = maxBytesPerFrame;
    }

    /// <summary>The texture cache.</summary>
    public TextureCache Cache { get; }

    /// <summary>The staging pool.</summary>
    public StagingPool Pool { get; }

    /// <summary>The slice cap in bytes.</summary>
    public long MaxBytesPerFrame { get; }

    /// <summary>Tiles queued and not uploaded yet.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Tiles uploaded so far.</summary>
    public long UploadedTiles { get; private set; }

    /// <summary>Bytes uploaded so far.</summary>
    public long UploadedBytes { get; private set; }

    /// <summary>Queues a decoded tile (thread-safe).</summary>
    public void Enqueue(in DecodedTile tile)
    {
        if (tile.Pixels.Length != tile.Width * tile.Height * 4)
        {
            throw new ArgumentException($"tile {tile.Key}: {tile.Pixels.Length} bytes for {tile.Width} x {tile.Height}", nameof(tile));
        }

        Interlocked.Increment(ref _pending);
        _queue.Enqueue(tile);
    }

    /// <summary>Queues a decoded tile nobody waits for yet (the look-ahead): it is uploaded with the leftover of a slice, or at once after <see cref="Promote"/>.</summary>
    public void EnqueueBackground(in DecodedTile tile)
    {
        if (tile.Pixels.Length != tile.Width * tile.Height * 4)
        {
            throw new ArgumentException($"tile {tile.Key}: {tile.Pixels.Length} bytes for {tile.Width} x {tile.Height}", nameof(tile));
        }

        Interlocked.Increment(ref _pending);
        if (_background.TryAdd(tile.Key, tile))
        {
            _backgroundOrder.Enqueue(tile.Key);
        }
        else
        {
            Pool.Return(tile.Pixels);                   // the same tile was decoded twice
            Interlocked.Decrement(ref _pending);
        }
    }

    /// <summary>A frame now waits for <paramref name="key"/>: a decoded look-ahead tile moves to the front of the upload queue. True when it was waiting.</summary>
    public bool Promote(in TextureRef key)
    {
        if (_background.TryRemove(key, out DecodedTile tile))
        {
            _queue.Enqueue(tile);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Uploads one slice: tiles in queue order while the slice stays within <see cref="MaxBytesPerFrame"/>, and at
    /// least one tile when any is queued. Call once per frame on the GL thread, before drawing.
    /// </summary>
    public UploadSliceStats UploadSlice()
    {
        long t0 = Stopwatch.GetTimestamp();
        int tiles = 0, allocations = 0, recycles = 0;
        long bytes = 0;
        while (true)
        {
            DecodedTile tile;
            if (_carry is { } c)
            {
                tile = c;
            }
            else if (!_queue.TryDequeue(out tile))
            {
                break;
            }

            long size = tile.Pixels.Length;
            if (tiles > 0 && bytes + size > MaxBytesPerFrame)
            {
                _carry = tile;                           // first tile of the next slice
                break;
            }

            _carry = null;
            if (Cache.Upload(tile.Key, tile.Width, tile.Height, tile.Pixels.Span, tile.Graded))
            {
                allocations++;
            }
            else
            {
                recycles++;
            }

            Pool.Return(tile.Pixels);
            Interlocked.Decrement(ref _pending);
            tiles++;
            bytes += size;
        }

        // the look-ahead tiles use what is left of the frame, at most four per slice
        long background = 0;
        while (background < BackgroundBytesPerFrame && _backgroundOrder.TryDequeue(out TextureRef next))
        {
            if (!_background.TryRemove(next, out DecodedTile tile))
            {
                continue;                                // promoted meanwhile: it is in the urgent queue
            }

            if (Cache.Upload(tile.Key, tile.Width, tile.Height, tile.Pixels.Span, tile.Graded))
            {
                allocations++;
            }
            else
            {
                recycles++;
            }

            Pool.Return(tile.Pixels);
            Interlocked.Decrement(ref _pending);
            tiles++;
            bytes += tile.Pixels.Length;
            background += tile.Pixels.Length;
        }

        UploadedTiles += tiles;
        UploadedBytes += bytes;
        return new UploadSliceStats(tiles, bytes, allocations, recycles, Stopwatch.GetElapsedTime(t0).TotalMilliseconds, Pending);
    }
}

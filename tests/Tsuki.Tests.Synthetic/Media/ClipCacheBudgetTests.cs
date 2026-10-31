using Tsuki.Engine.Contracts;
using Tsuki.Media.Sound;

namespace Tsuki.Tests.Synthetic.Media;

/// <summary>
/// Package K11 (memory growth): the clip cache holds to its byte budget however many clips pass through it, and it asks the collector for a collection after a fixed
/// amount of decoded PCM, so evicted clips (large-object-heap arrays, freed only by a generation-2 collection) do not pile up between the collector's own schedules.
/// </summary>
public sealed class ClipCacheBudgetTests
{
    private static AudioClip Clip(AudioBank bank, int entry, int frames) => new(bank, entry, 2, 48_000, new float[frames * 2], null);

    [Fact]
    public void TheCacheNeverHoldsMoreThanItsBudgetOverManyClipsOfAllSizes()
    {
        const long Budget = 1L << 20;                                  // 1 MiB of float PCM
        var rng = new Random(11);
        var cache = new ClipCache((b, i) => Clip(b, i, 100 + ((i * 7919) % 9000)), Budget, collectEveryBytes: 0);
        long largest = 0;
        for (int i = 0; i < 6000; i++)
        {
            AudioBank bank = (AudioBank)(i % 3);                       // three different banks
            int entry = rng.Next(0, 2000);
            AudioClip clip = cache.Get(bank, entry);
            largest = Math.Max(largest, clip.Samples.Length * (long)sizeof(float));
            Assert.True(cache.Bytes <= Budget, $"after {i + 1} requests the cache holds {cache.Bytes} bytes, over the budget of {Budget}");
        }

        Assert.True(largest < Budget, "the largest clip has to fit the budget for the bound to be exact");
        Assert.True(cache.Decodes > 1000, "the run has to make the cache evict many times");
        Assert.True(cache.Hits > 0);
    }

    [Fact]
    public void TheBudgetHoldsWhenSeveralThreadsRequestClipsAtOnce()
    {
        const long Budget = 512L << 10;
        var cache = new ClipCache((b, i) => Clip(b, i, 500 + ((i * 31) % 2000)), Budget, collectEveryBytes: 0);
        long worst = 0;
        Parallel.For(0, 8, t =>
        {
            var rng = new Random(t);
            for (int i = 0; i < 1500; i++)
            {
                _ = cache.Get(AudioBank.Voice, rng.Next(0, 3000));
                long seen = cache.Bytes;
                long previous;
                while (seen > (previous = Interlocked.Read(ref worst)) && Interlocked.CompareExchange(ref worst, seen, previous) != previous)
                {
                }
            }
        });
        Assert.True(worst <= Budget, $"the cache held {worst} bytes at some point, over the budget of {Budget}");
    }

    [Fact]
    public void ACollectionIsRequestedAfterEveryChunkOfDecodedPcm()
    {
        int collections = 0;
        const int Frames = 1000;                                       // 8,000 bytes per clip
        const long Chunk = 10 * Frames * 2 * sizeof(float);
        var cache = new ClipCache((b, i) => Clip(b, i, Frames), budgetBytes: 4 * Frames * 2 * sizeof(float), collectEveryBytes: Chunk, collect: () => collections++);
        for (int i = 0; i < 95; i++)
        {
            _ = cache.Get(AudioBank.Se, i);
        }

        Assert.Equal(9, collections);                                  // 95 clips = 9.5 chunks
        Assert.Equal(9, cache.Collections);
        for (int i = 0; i < 95; i++)
        {
            _ = cache.Get(AudioBank.Se, i);                            // mostly evicted by now: decoded again
        }

        Assert.True(collections >= 18, $"collections {collections}");
    }

    [Fact]
    public void ACacheHitDecodesNothingAndRequestsNoCollection()
    {
        int collections = 0;
        var cache = new ClipCache((b, i) => Clip(b, i, 1000), budgetBytes: 1L << 20, collectEveryBytes: 8000 * 3, collect: () => collections++);
        for (int i = 0; i < 50; i++)
        {
            _ = cache.Get(AudioBank.Bgm, 1);
        }

        Assert.Equal(1, cache.Decodes);
        Assert.Equal(0, collections);                                  // one 8,000-byte decode is under the 24,000-byte chunk
    }
}

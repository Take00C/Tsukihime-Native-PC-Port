using Tsuki.Platform;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>Package R1 (K11 section 8): the texture cache cap follows the adapter's dedicated memory instead of being 1536 MiB on every card.</summary>
public sealed class VideoMemoryTests
{
    [Theory]
    [InlineData(null, 1536L << 20)]                     // unknown: the fixed cap of before
    [InlineData(0L, 1536L << 20)]
    [InlineData(1L << 30, 512L << 20)]                  // 1 GB: the floor (a third would be 341 MiB; 512 MiB is the least that still plays)
    [InlineData(2L << 30, 683L << 20)]                  // 2 GB: a third
    [InlineData(4L << 30, 1365L << 20)]                 // 4 GB: a third, 1365 MiB (the driver then holds about 2.7 GB, under the card's 4)
    [InlineData(6L << 30, 1536L << 20)]                 // 6 GB and up: the ceiling
    [InlineData(16L << 30, 1536L << 20)]
    public void TheCapIsAThirdOfTheCardBetweenTheFloorAndTheCeiling(long? dedicated, long expected)
    {
        long cap = VideoMemory.CacheCapacity(dedicated);
        Assert.InRange(cap, expected - (1L << 20), expected + (1L << 20));
        Assert.InRange(cap, VideoMemory.MinCacheBytes, VideoMemory.MaxCacheBytes);
    }

    [Fact]
    public void ReadingTheAdapterNeverThrowsAndAnswersSomethingSensibleOnWindows()
    {
        (long Bytes, string Name)? adapter = VideoMemory.Dedicated("some renderer string");
        if (OperatingSystem.IsWindows() && adapter is { } a)
        {
            Assert.True(a.Bytes > 0);
            Assert.False(string.IsNullOrWhiteSpace(a.Name));
        }
        else
        {
            Assert.Null(adapter);
        }
    }
}

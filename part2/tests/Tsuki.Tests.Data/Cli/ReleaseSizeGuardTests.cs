using Tsuki.Cli.Commands;

namespace Tsuki.Tests.Data.Cli;

public sealed class ReleaseSizeGuardTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(512L * 1024 * 1024 + 1, 0)]
    [InlineData(1, 2_147_483_648L)]
    public void ReleaseArchivesRejectNegativePerFileAndOverTotalSizes(long bytes, long total)
    {
        Assert.NotNull(ReleaseRules.UncompressedSizeProblem(bytes, total, "entry"));
    }

    [Fact]
    public void ReleaseArchivesAcceptBothExactSizeLimits()
    {
        Assert.Null(ReleaseRules.UncompressedSizeProblem(512L * 1024 * 1024, 0, "file-at-limit"));
        Assert.Null(ReleaseRules.UncompressedSizeProblem(1, 2_147_483_648L - 1, "total-at-limit"));
    }

    [Fact]
    public void TheLimitsAreOneSetThatTheTestsMayLower()
    {
        Assert.Equal(new ReleaseLimits(ReleaseRules.MaxFileBytes, ReleaseRules.MaxTotalBytes, ReleaseRules.MaxEntries), ReleaseLimits.Release);
        Assert.Equal((512L * 1024 * 1024, 2_147_483_648L, 20_000), (ReleaseRules.MaxFileBytes, ReleaseRules.MaxTotalBytes, ReleaseRules.MaxEntries));
        var small = new ReleaseLimits(10, 15, 3);
        Assert.Null(ReleaseRules.UncompressedSizeProblem(10, 5, "fits", small));
        Assert.Equal("big: exceeds the 10 bytes per-file or 15 bytes total uncompressed-size limit", ReleaseRules.UncompressedSizeProblem(11, 0, "big", small));
        Assert.NotNull(ReleaseRules.UncompressedSizeProblem(6, 10, "over", small));
    }
}

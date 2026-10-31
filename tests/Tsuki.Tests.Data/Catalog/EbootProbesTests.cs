using System.Buffers.Binary;
using Tsuki.Data.Eboot;
using Tsuki.Data.Verification;

namespace Tsuki.Tests.Data.Catalog;

/// <summary>
/// The structural probes of RR 3.3 item 2 that the build check leaves out (<see cref="EbootProbes"/>): they pass on the user's
/// eboot.bin, each fails on its own damage with its own message, and a damaged or foreign file gives failures, never an exception.
/// </summary>
public sealed class EbootProbesTests
{
    private static readonly string[] AllIds =
    [
        "eboot.probe.segments", "eboot.probe.layout", "eboot.probe.catalog", "eboot.probe.names", "eboot.probe.movies",
        "eboot.probe.scenes", "eboot.probe.uitext", "eboot.probe.uitext.table",
    ];

    private static string[] Failed(IReadOnlyList<CheckResult> results) =>
        [.. results.Where(r => !r.Passed).Select(r => r.Id)];

    private static IReadOnlyList<CheckResult> RunMutated(Action<byte[]> damage)
    {
        byte[] copy = (byte[])SharedEboot.Data.Clone();
        damage(copy);
        return EbootProbes.Run(copy);
    }

    [GameDataFact]
    public void EveryProbePassesOnTheUsersEboot()
    {
        var results = EbootProbes.Run(SharedEboot.Data);
        Assert.Equal(AllIds, results.Select(r => r.Id));
        Assert.All(results, r =>
        {
            Assert.True(r.Passed, $"{r.Id}: {r.Message} (expected {r.Expected}, found {r.Actual})");
            Assert.Equal(BuildFailure.None, r.Failure);
            Assert.Equal(string.Empty, r.Message);
            Assert.False(string.IsNullOrWhiteSpace(r.Title));
            Assert.False(string.IsNullOrWhiteSpace(r.Expected));
            Assert.False(string.IsNullOrWhiteSpace(r.Actual));
        });
    }

    [GameDataFact]
    public void TheProbesRunQuickly()
    {
        _ = EbootProbes.Run(SharedEboot.Data); // JIT
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = EbootProbes.Run(SharedEboot.Data);
        sw.Stop();
        Assert.All(results, r => Assert.True(r.Passed));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms");
    }

    [GameDataFact]
    public void ADamagedSegmentTableFailsTheSegmentProbeAlone()
    {
        // the file is no longer readable as SELF: one failure with the parse error, no exception
        var results = RunMutated(d => d[0x40] |= 0x08); // SELF segment 1 (text) flagged as deflated
        var failed = Assert.Single(results);
        Assert.Equal("eboot.probe.segments", failed.Id);
        Assert.Equal(CheckStatus.Fail, failed.Status);
        Assert.Equal(BuildFailure.EbootProbeFailed, failed.Failure);
        Assert.Contains("compressed or encrypted", failed.Message, StringComparison.Ordinal);
    }

    [GameDataFact]
    public void AMovedSegmentFailsTheLayoutProbe()
    {
        // segment 1 holds the text at file 0x2280; the ELF program header still says VA 0, so the mapping moves by one byte
        var results = RunMutated(d => d[0x48]++);
        Assert.Contains("eboot.probe.layout", Failed(results));
        var layout = results.Single(r => r.Id == "eboot.probe.layout");
        Assert.Contains("0x2281", layout.Actual, StringComparison.Ordinal);
        Assert.Contains("0x2280", layout.Expected, StringComparison.Ordinal);
        Assert.Contains("segment layout", layout.Message, StringComparison.Ordinal);
    }

    [GameDataFact]
    public void ADamagedCatalogFailsTheCatalogProbe()
    {
        long first = SharedEboot.DataFile(EbootAddresses.ImageTable) + 8; // name0 of record 0
        var results = RunMutated(d => d[first]++);
        Assert.Equal(["eboot.probe.catalog"], Failed(results));
        var failed = results.Single(r => !r.Passed);
        Assert.Equal(BuildFailure.EbootProbeFailed, failed.Failure);
        Assert.Contains("image table not found", failed.Message, StringComparison.Ordinal);
    }

    [GameDataFact]
    public void ACatalogWithAnotherNumberOfRecordsFailsTheCatalogProbeWithTheCount()
    {
        long slot = SharedEboot.DataFile(EbootAddresses.ImageTable) + (0x48L * 14_743);
        var results = RunMutated(d => Array.Clear(d, (int)slot + 8, 16));
        Assert.Equal(["eboot.probe.catalog"], Failed(results));
        Assert.Contains("14743 records", results.Single(r => !r.Passed).Message, StringComparison.Ordinal);
    }

    [GameDataFact]
    public void ADamagedNameListFailsTheNamesProbe()
    {
        long first = SharedEboot.DataFile(EbootAddresses.ImageNames);
        var results = RunMutated(d => d[first] = (byte)'X');
        Assert.Equal(["eboot.probe.names"], Failed(results));
        Assert.Contains("image name list not found", results.Single(r => !r.Passed).Message, StringComparison.Ordinal);
    }

    [GameDataFact]
    public void ANameListWithoutItsTerminatorIsAnErrorNotAWrongList()
    {
        long end = SharedEboot.DataFile(EbootAddresses.ImageNames + (32L * PictureCount()));
        var results = RunMutated(d => d[end] = 0);
        Assert.Contains("eboot.probe.names", Failed(results));
    }

    private static int PictureCount() => SharedEboot.Catalog.PictureNames.Count;

    [GameDataFact]
    public void ADamagedMovieTableFailsTheMoviesProbe()
    {
        long terminator = SharedEboot.DataFile(EbootAddresses.MovieTerminator);
        var results = RunMutated(d => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan((int)terminator), 134));
        Assert.Equal(["eboot.probe.movies"], Failed(results));
        var failed = results.Single(r => !r.Passed);
        Assert.Equal(BuildFailure.EbootProbeFailed, failed.Failure);
        Assert.False(string.IsNullOrWhiteSpace(failed.Message));
    }

    [GameDataFact]
    public void ADamagedSceneTableFailsTheScenesProbe()
    {
        long scenes = SharedEboot.TextFile(EbootAddresses.SceneIds);
        var results = RunMutated(d => d[scenes] = 111);
        Assert.Equal(["eboot.probe.scenes"], Failed(results));
        var failed = results.Single(r => !r.Passed);
        Assert.Equal("110, 240, 260, 270", failed.Expected);
        Assert.Equal("111, 240, 260, 270", failed.Actual);
    }

    [GameDataFact]
    public void ADamagedUiTextTableFailsTheUiTextProbes()
    {
        // the first string of ja is not "NULL" any more: both the placeholder probe and the whole-table probe object
        long ja0 = SharedEboot.Reader.FileOffset(0xD63F38)!.Value; // the placeholder of ja, the first string of the block
        var results = RunMutated(d => d[ja0] = (byte)'M');
        Assert.Contains("eboot.probe.uitext", Failed(results));
        Assert.Equal("\"MULL\"", results.Single(r => r.Id == "eboot.probe.uitext").Actual);

        // a pointer that disagrees with its relocation fails the whole-table probe only
        long slot = SharedEboot.RelroFile(EbootAddresses.UiText + (8 * 7));
        var relocation = RunMutated(d => d[slot]++);
        Assert.Equal(["eboot.probe.uitext.table"], Failed(relocation));
        Assert.Contains("relocation", relocation.Single(r => !r.Passed).Message, StringComparison.Ordinal);
    }

    [GameDataFact]
    public void EachFailureHasItsOwnMessage()
    {
        var all = new List<CheckResult>();
        all.AddRange(RunMutated(d => d[SharedEboot.DataFile(EbootAddresses.ImageTable) + 8]++).Where(r => !r.Passed));
        all.AddRange(RunMutated(d => d[SharedEboot.DataFile(EbootAddresses.ImageNames)] = (byte)'X').Where(r => !r.Passed));
        all.AddRange(RunMutated(d => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan((int)SharedEboot.DataFile(EbootAddresses.MovieTerminator)), 5)).Where(r => !r.Passed));
        all.AddRange(RunMutated(d => d[SharedEboot.TextFile(EbootAddresses.SceneIds)] = 1).Where(r => !r.Passed));
        Assert.Equal(4, all.Count);
        Assert.Equal(4, all.Select(r => r.Message).Distinct().Count());
        Assert.Equal(4, all.Select(r => r.Id).Distinct().Count());
        Assert.All(all, r => Assert.StartsWith("eboot.probe.", r.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void GarbageEmptyAndTruncatedFilesFailWithoutAnException()
    {
        var garbage = new byte[1 << 20];
        new Random(42).NextBytes(garbage);
        foreach (var data in new[] { garbage, [], new byte[3], new byte[0x30], SyntheticSelf.Build() })
        {
            var results = EbootProbes.Run(data);
            Assert.NotEmpty(results);
            Assert.Contains(results, r => !r.Passed);
            Assert.All(results.Where(r => !r.Passed), r =>
            {
                Assert.Equal(BuildFailure.EbootProbeFailed, r.Failure);
                Assert.False(string.IsNullOrWhiteSpace(r.Message));
            });
        }
    }

    [GameDataFact]
    public void ATruncatedRealFileFailsInsteadOfThrowing()
    {
        foreach (int length in new[] { 0x160, 0x1000, 0x40000, 0x400000 })
        {
            var results = EbootProbes.Run(SharedEboot.Data.AsMemory(0, length));
            Assert.Contains(results, r => !r.Passed);
        }
    }
}

using System.Diagnostics;

namespace Tsuki.Tests.Data;

/// <summary>
/// The name of the xunit collection that runs alone: a test in it measures the memory of the whole process, so no other test class
/// may run at the same moment (xunit v3 runs test classes in parallel otherwise).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialMemoryCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "serial-memory";
}

/// <summary>ROADMAP M1 acceptance 8: no code path reads a whole archive into memory.</summary>
[Collection(SerialMemoryCollection.Name)]
public sealed class GiantArchiveTests
{
    [GameDataFact]
    public void OpeningTheGiantArchiveAndReadingEntriesNeverLoadsItIntoMemory()
    {
        // allpac1.mrg is 24.3 GB. The managed allocation is counted for this thread only (GC.GetTotalAllocatedBytes would also count
        // what other tests allocate on their threads), and the collection runs alone so that the process-wide private memory growth
        // is this test's too.
        long fileLength = new FileInfo(GameImage.Path("allpac1.mrg")).Length;
        Assert.True(fileLength > 20_000_000_000L, "allpac1.mrg is the 24 GB archive");
        GC.Collect();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long privateBefore = Process.GetCurrentProcess().PrivateMemorySize64;
        long touched = 0;
        using (var a = GameImage.Open("allpac1"))
        {
            for (int i = 0; i < a.Count; i += 37)
            {
                var span = a.Span(i);
                touched += span[0] + span[^1];
            }

            Assert.True(a.Count > 200);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long privateGrowth = Process.GetCurrentProcess().PrivateMemorySize64 - privateBefore;
        Assert.True(touched > 0);
        Assert.True(allocated < 256L * 1024 * 1024, $"managed allocation while indexing allpac1: {allocated:N0} bytes");
        Assert.True(privateGrowth < 1024L * 1024 * 1024, $"private memory growth while reading allpac1 entries: {privateGrowth:N0} bytes");
    }
}

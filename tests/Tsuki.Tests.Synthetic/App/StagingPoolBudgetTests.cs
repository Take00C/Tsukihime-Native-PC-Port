using System.Collections.Concurrent;
using Tsuki.Render.Textures;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>Package K11: the staging pool never holds more than its budget, whatever sizes pass through it, and gives every byte back.</summary>
public sealed class StagingPoolBudgetTests
{
    [Fact]
    public void RentedAndFreeBytesNeverExceedTheBudgetWhileRentersOfMixedSizesWaitForReturns()
    {
        // Four renters of random sizes against a small pool, one thread that returns what they rent (like the upload scheduler on the main thread). The renters
        // wait whenever the pool is full, so the budget is what bounds them.
        const long Budget = 1L << 20;
        const int Renters = 4;
        const int RentsEach = 3000;
        var pool = new StagingPool(Budget);
        int[] sizes = [4096, 16384, 65536, 262144, 100_000, 524288];
        var rented = new BlockingCollection<StagingBuffer>();
        int done = 0;
        using var cancel = new CancellationTokenSource();
        Task[] renters = new Task[Renters];
        for (int t = 0; t < Renters; t++)
        {
            int seed = t;
            renters[t] = BackgroundWork.Run(() =>
            {
                var rng = new Random(seed);
                for (int i = 0; i < RentsEach; i++)
                {
                    rented.Add(pool.Rent(sizes[rng.Next(sizes.Length)], cancel.Token));
                }

                if (Interlocked.Increment(ref done) == Renters)
                {
                    rented.CompleteAdding();
                }
            });
        }

        long returned = 0;
        Task returner = BackgroundWork.Run(() =>
        {
            foreach (StagingBuffer b in rented.GetConsumingEnumerable())
            {
                Assert.True(pool.OutstandingBytes <= Budget, $"{pool.OutstandingBytes} bytes rented, budget {Budget}");
                pool.Return(b);
                returned++;
            }
        });

        bool finished = BackgroundWork.Wait(returner, 90_000);
        if (!finished)
        {
            cancel.Cancel();                                            // free the renters so a failure ends the run instead of hanging it
        }

        Assert.True(finished, "the renters and the returner did not finish: a renter waits although the pool could serve it");
        BackgroundWork.WaitAll(renters);
        Assert.Equal(Renters * RentsEach, returned);
        Assert.Equal(0, pool.OutstandingBytes);
        Assert.True(pool.PeakOutstandingBytes <= Budget, $"{pool.PeakOutstandingBytes} bytes were rented at once, budget {Budget}");
        Assert.True(pool.PeakTotalBytes <= Budget, $"the pool's total (rented and free) peaked at {pool.PeakTotalBytes}, over the budget of {Budget}");
    }
}

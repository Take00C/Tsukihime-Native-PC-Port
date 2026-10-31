using Tsuki.Render.Textures;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package K10: the process did not exit after the last frame when decode workers were waiting for a staging buffer. A renter that was woken by the cancel left its
/// wake-up registration while it still held the pool's lock, and disposing the registration waited for the callback that the cancelling thread was running, which
/// needed that lock: <c>DecodeWorkers.Dispose</c> hung for good (seen after a 100,000-step soak: 8.5 GB resident, 17 threads, none of them making progress).
/// </summary>
public sealed class StagingPoolCancelTests
{
    private static StagingBuffer Rent(StagingPool pool) => pool.Rent(4096);

    private static StagingBuffer RentCancelable(StagingPool pool, CancellationToken cancel) => pool.Rent(4096, cancel);

    [Fact]
    public void CancellingWhileSeveralRentersWaitReleasesEveryOneOfThemAndNeverHangs()
    {
        for (int round = 0; round < 200; round++)
        {
            var pool = new StagingPool(2 * 4096);
            StagingBuffer first = Rent(pool);
            StagingBuffer second = Rent(pool);                     // the pool is exhausted: every further renter waits
            using var cts = new CancellationTokenSource();
            int cancelled = 0;
            const int Waiters = 6;
            Thread[] renters = new Thread[Waiters];
            for (int i = 0; i < Waiters; i++)
            {
                renters[i] = new Thread(() =>
                {
                    try
                    {
                        _ = RentCancelable(pool, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref cancelled);
                    }
                })
                { IsBackground = true };
                renters[i].Start();
            }

            Assert.True(SpinWait.SpinUntil(() => pool.Waits >= Waiters, 30_000), "the renters never reached the wait");
            var canceller = new Thread(cts.Cancel) { IsBackground = true };          // its own thread: a hang fails the test instead of hanging it
            canceller.Start();
            Assert.True(canceller.Join(TimeSpan.FromSeconds(30)), $"Cancel never returned (round {round})");
            foreach (Thread t in renters)
            {
                Assert.True(t.Join(TimeSpan.FromSeconds(30)), $"a renter never returned (round {round})");
            }

            Assert.Equal(Waiters, Volatile.Read(ref cancelled));
            pool.Return(first);
            pool.Return(second);
            Assert.Equal(0, pool.OutstandingBytes);
        }
    }

    [Fact]
    public void ARenterThatWaitsWithoutACancelableTokenIsWokenByAReturn()
    {
        var pool = new StagingPool(4096);
        StagingBuffer held = Rent(pool);
        StagingBuffer? got = null;
        var renter = new Thread(() => got = Rent(pool)) { IsBackground = true };
        renter.Start();
        Assert.True(SpinWait.SpinUntil(() => pool.Waits >= 1, 30_000));
        pool.Return(held);
        Assert.True(renter.Join(TimeSpan.FromSeconds(30)));
        Assert.NotNull(got);
    }

    [Fact]
    public void ATokenThatIsAlreadyCancelledStopsARenterBeforeItWaits()
    {
        var pool = new StagingPool(4096);
        StagingBuffer held = Rent(pool);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => RentCancelable(pool, cts.Token));
        pool.Return(held);
    }
}

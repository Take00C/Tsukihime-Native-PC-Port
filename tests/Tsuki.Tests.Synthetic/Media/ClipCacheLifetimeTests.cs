using Tsuki.Engine.Contracts;
using Tsuki.Media.Mixer;
using Tsuki.Media.Sound;

namespace Tsuki.Tests.Synthetic.Media;

/// <summary>
/// Package K10: the clip cache and the player stop their decodes before the data goes away, and a decode that fails is reported and skipped.
/// The crash this guards: quitting while a clip decoded on the thread pool unmapped the archive under the decoder (an access violation that
/// .NET cannot catch).
/// </summary>
public sealed class ClipCacheLifetimeTests
{
    private static AudioClip Clip(AudioBank bank, int entry, int frames = 64) => new(bank, entry, 2, 48_000, new float[frames * 2], null);

    [Fact]
    public void ShutdownWaitsForTheDecodesThatRunAndRefusesTheRest()
    {
        using var release = new ManualResetEventSlim(false);
        int running = 0;
        int started = 0;
        var cache = new ClipCache((b, i) =>
        {
            Interlocked.Increment(ref started);
            Interlocked.Increment(ref running);
            try
            {
                release.Wait(30_000);
                return Clip(b, i);
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        });
        for (int i = 0; i < 4; i++)
        {
            cache.Prefetch(AudioBank.Bgm, i);
        }

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref started) == 4, 10_000));

        Assert.False(cache.Shutdown(TimeSpan.FromMilliseconds(80)), "four decodes are running: the wait runs out");
        int startedBefore = Volatile.Read(ref started);
        cache.Prefetch(AudioBank.Bgm, 99);                          // refused silently
        Assert.Throws<ObjectDisposedException>(() => cache.Get(AudioBank.Bgm, 98));
        Thread.Sleep(30);
        Assert.Equal(startedBefore, Volatile.Read(ref started));

        var closer = BackgroundWork.Run(() => cache.Shutdown(TimeSpan.FromSeconds(20)));
        Thread.Sleep(40);
        Assert.False(closer.IsCompleted, "Shutdown does not return while a decode runs");
        release.Set();
        Assert.True(BackgroundWork.Wait(closer, 10_000));
        Assert.True(BackgroundWork.Result(closer));
        Assert.Equal(0, Volatile.Read(ref running));                // when Shutdown returns, no decode is inside the decode function
        Assert.Equal(startedBefore, Volatile.Read(ref started));
    }

    [Fact]
    public void GetPrefetchEvictionAndAShutdownFromManyThreadsNeverRunTheDecoderAfterTheDataIsGone()
    {
        // the "data" is released once Shutdown returned true; a decode inside the function must never see it released (the unmapped archive of the crash)
        for (int round = 0; round < 12; round++)
        {
            int released = 0;
            int violations = 0;
            int running = 0;
            int wrongClip = 0;
            long decodes = 0;
            var cache = new ClipCache(
                (b, i) =>
                {
                    Interlocked.Increment(ref running);
                    try
                    {
                        if (Volatile.Read(ref released) != 0)
                        {
                            Interlocked.Increment(ref violations);
                        }

                        Interlocked.Increment(ref decodes);
                        Thread.SpinWait(2_000);
                        if (Volatile.Read(ref released) != 0)
                        {
                            Interlocked.Increment(ref violations);
                        }

                        return Clip(b, i, 500);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref running);
                    }
                },
                budgetBytes: 6 * 500 * 2 * sizeof(float));          // room for six clips of thirty-two keys: eviction all the time
            using var stop = new CancellationTokenSource();
            Task[] threads = [.. Enumerable.Range(0, 8).Select(t => BackgroundWork.Run(() =>
            {
                var rng = new Random((round * 31) + t);
                while (!stop.IsCancellationRequested)
                {
                    int key = rng.Next(32);
                    try
                    {
                        if (rng.Next(3) == 0)
                        {
                            cache.Prefetch(AudioBank.Se, key);
                        }
                        else
                        {
                            AudioClip clip = cache.Get(AudioBank.Se, key);
                            if (clip.Index != key || clip.Bank != AudioBank.Se)
                            {
                                Interlocked.Increment(ref wrongClip);
                            }
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        return;                                      // shut down
                    }
                }
            }))];
            Assert.True(SpinWait.SpinUntil(() => Interlocked.Read(ref decodes) >= 8 + round, 20_000), "the workers never got going");
            Thread.Sleep(round % 4);                                 // shut down at a different point of the traffic each round
            Assert.True(cache.Shutdown(TimeSpan.FromSeconds(30)));
            Volatile.Write(ref released, 1);                         // "dispose the data source"
            stop.Cancel();
            BackgroundWork.WaitAll(threads);
            Thread.Sleep(30);                                        // the prefetch tasks that were queued behind the shutdown
            Assert.Equal(0, Volatile.Read(ref violations));
            Assert.Equal(0, Volatile.Read(ref wrongClip));
            Assert.Equal(0, Volatile.Read(ref running));
            Assert.True(Interlocked.Read(ref decodes) > 0);
            Assert.True(cache.Bytes <= (6 * 500 * 2 * sizeof(float)) + (500 * 2 * sizeof(float)), $"the budget held: {cache.Bytes} bytes");
        }
    }

    [Fact]
    public void TheBudgetHoldsWhenAClipFinishesAfterTheClipsRequestedLaterThanIt()
    {
        // A is requested first and decodes slowly; B and C finish before it. When A lands it is the oldest request and the newest completion:
        // the eviction used to stop at it (it is the one it keeps) and leave B and C over the budget
        using var releaseA = new ManualResetEventSlim(false);
        using var startedA = new ManualResetEventSlim(false);
        const int Frames = 500;
        long clipBytes = Frames * 2L * sizeof(float);
        var cache = new ClipCache(
            (b, i) =>
            {
                if (i == 0)
                {
                    startedA.Set();
                    releaseA.Wait(30_000);
                }

                return Clip(b, i, Frames);
            },
            budgetBytes: 2 * clipBytes);
        Task<AudioClip> a = BackgroundWork.Run(() => cache.Get(AudioBank.Se, 0));
        Assert.True(BackgroundWork.Wait(startedA, 10_000));
        _ = cache.Get(AudioBank.Se, 1);
        _ = cache.Get(AudioBank.Se, 2);
        Assert.Equal(2 * clipBytes, cache.Bytes);

        releaseA.Set();
        Assert.True(BackgroundWork.Wait(a, 10_000));
        Assert.Equal(2 * clipBytes, cache.Bytes);                                // B went, A and C stay
        long decodesBefore = cache.Decodes;
        _ = cache.Get(AudioBank.Se, 0);
        _ = cache.Get(AudioBank.Se, 2);
        Assert.Equal(decodesBefore, cache.Decodes);                              // both were still cached
    }

    [Fact]
    public void AFailingDecodeIsNeverCachedAndNeverEscapesAPrefetch()
    {
        int calls = 0;
        var cache = new ClipCache((b, i) =>
        {
            int n = Interlocked.Increment(ref calls);
            return i == 7 && n <= 2 ? throw new InvalidDataException("avcodec_send_packet failed: Invalid data found (-1094995529)") : Clip(b, i);
        });
        cache.Prefetch(AudioBank.Voice, 7);                          // the speculative decode fails on the pool: nothing may come of it
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) >= 1, 10_000));
        Thread.Sleep(50);
        Assert.Equal(0, cache.Bytes);

        Assert.Throws<InvalidDataException>(() => cache.Get(AudioBank.Voice, 7));    // the caller that needs the clip sees the error (a second failure)
        Assert.Equal(2, Volatile.Read(ref calls));
        AudioClip again = cache.Get(AudioBank.Voice, 7);             // and the next try decodes afresh: the failure was not kept
        Assert.Equal(7, again.Index);
    }

    [Fact]
    public void WithAsyncDecodeAClipThatFailsIsReportedSkippedAndTheNextPlayStillWorks()
    {
        var mixer = new AudioMixer();
        var player = new AudioCommandPlayer(
            (b, i) => i == 13 ? throw new InvalidDataException("atrac9 produced 0 frames") : new AudioClip(b, i, 2, 48_000, new float[2000], null),
            mixer,
            new AudioPlayerOptions(AsyncDecode: true));
        player.Perform(AudioCommand.Play(6, AudioBank.Voice, 13, false, 1f));
        Assert.True(SpinWait.SpinUntil(
            () =>
            {
                player.Pump();
                return player.Failures.Count == 1;
            },
            10_000));
        Assert.Equal((AudioBank.Voice, 13), (player.Failures[0].Bank, player.Failures[0].Entry));
        Assert.IsType<InvalidDataException>(player.Failures[0].Error);
        Assert.False(player.IsPlaying(6), "the stream is silent, not stuck");

        player.Perform(AudioCommand.Play(7, AudioBank.Voice, 14, false, 1f));      // the game goes on
        Assert.True(SpinWait.SpinUntil(
            () =>
            {
                player.Pump();
                return player.PlaysPosted == 1;
            },
            10_000));
        Assert.Single(player.Failures);
    }

    [Fact]
    public void ADisposedPlayerDecodesNothingMoreAndWaitsForTheDecodeThatRuns()
    {
        using var release = new ManualResetEventSlim(false);
        int running = 0;
        var mixer = new AudioMixer();
        var player = new AudioCommandPlayer(
            (b, i) =>
            {
                Interlocked.Increment(ref running);
                try
                {
                    release.Wait(30_000);
                    return new AudioClip(b, i, 2, 48_000, new float[2000], null);
                }
                finally
                {
                    Interlocked.Decrement(ref running);
                }
            },
            mixer,
            new AudioPlayerOptions(AsyncDecode: true));
        player.Perform(AudioCommand.Play(0, AudioBank.Bgm, 1, true, 1f));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref running) == 1, 10_000));

        var disposer = BackgroundWork.Run(player.Dispose);
        Thread.Sleep(50);
        Assert.False(disposer.IsCompleted, "Dispose waits for the decode in flight");
        release.Set();
        Assert.True(BackgroundWork.Wait(disposer, 10_000));
        Assert.Equal(0, Volatile.Read(ref running));

        player.Perform(AudioCommand.Play(1, AudioBank.Bgm, 2, true, 1f));      // after the shutdown: reported, not decoded, nothing thrown
        Assert.True(SpinWait.SpinUntil(
            () =>
            {
                player.Pump();
                return player.Failures.Count >= 1;
            },
            10_000));
        Assert.Equal(0, Volatile.Read(ref running));
    }
}

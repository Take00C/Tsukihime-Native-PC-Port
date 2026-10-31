using System.Diagnostics;
using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Media.Audio;
using Tsuki.Media.Ffmpeg;
using Tsuki.Media.Sound;

namespace Tsuki.Tests.Data.Media;

/// <summary>
/// Package K10 (docs/status/knife/K10.md): the crash of the shipped build. Disposing the game source (quitting) unmapped the archives while clips were still
/// being decoded on the thread pool; the decoder's read of an unmapped page is an access violation, which .NET cannot catch: event 1026, the managed stack
/// ending in Atrac9Decoder.DecodeAll, exit code 139 under bash. Before the fix the first test below ended the test host with exactly that fault, every time.
/// </summary>
public sealed class ShutdownRaceTests(ITestOutputHelper output)
{
    /// <summary>The real decoder, with a count of the decodes that are inside it.</summary>
    private sealed class CountingDecoder : IAudioDecoder
    {
        private readonly MediaAudioDecoder _inner = new();
        private int _inside;
        private int _begun;
        private int _ended;

        public int Inside => Volatile.Read(ref _inside);

        public int Begun => Volatile.Read(ref _begun);

        public int Ended => Volatile.Read(ref _ended);

        public AudioClip Decode(AudioBank bank, int index, ReadOnlyMemory<byte> payload)
        {
            Interlocked.Increment(ref _inside);
            Interlocked.Increment(ref _begun);
            try
            {
                return _inner.Decode(bank, index, payload);
            }
            finally
            {
                Interlocked.Increment(ref _ended);
                Interlocked.Decrement(ref _inside);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="clips"/> requests of the cache on threads of their own, as the prefetch tasks and the async plays do on the pool. A thread of its own
    /// because the full parallel run keeps every pool thread busy and the pool adds new ones slowly: these tests are about the order of events, not about the pool.
    /// </summary>
    private static Thread[] Requesters(ClipCache cache, int threads, int clips, AudioBank bank, List<Exception> unexpected)
    {
        Thread[] all = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int first = t;
            all[t] = new Thread(() =>
            {
                for (int i = first; i < clips; i += threads)
                {
                    try
                    {
                        _ = cache.Get(bank, i);
                    }
                    catch (ObjectDisposedException)
                    {
                        // the source or the cache is shut down: a request that arrives late is refused
                    }
                    catch (Exception e)
                    {
                        lock (unexpected)
                        {
                            unexpected.Add(e);
                        }
                    }
                }
            })
            { IsBackground = true, Name = "k10-requester-" + t };
            all[t].Start();
        }

        return all;
    }

    [GameDataFact]
    public void DisposingTheSourceWhileClipsDecodeWaitsForThemAndTheProcessSurvives()
    {
        for (int round = 0; round < 6; round++)
        {
            var decoder = new CountingDecoder();
            var data = GameFolderSource.Open(GameFolder.Require(), decoder);
            int refused = 0;
            var cache = new ClipCache((b, i) =>
            {
                try
                {
                    return data.Audio(b, i);
                }
                catch (ObjectDisposedException)
                {
                    Interlocked.Increment(ref refused);
                    throw;
                }
            });
            var unexpected = new List<Exception>();
            Thread[] requesters = Requesters(cache, 8, 32, AudioBank.Bgm, unexpected);       // 2..5 MB of ATRAC9 each, about 0.1 s of decode

            Assert.True(SpinWait.SpinUntil(() => decoder.Inside >= 2, 30_000), "the decodes never started");
            int insideAtDispose = decoder.Inside;
            data.Dispose();                                          // before the fix: unmapped under the running decodes -> 0xC0000005, process gone
            Assert.Equal(0, decoder.Inside);                         // Dispose returned only after the decodes inside the decoder had left
            Assert.True(decoder.Ended >= insideAtDispose);
            Assert.Throws<ObjectDisposedException>(() => data.Audio(AudioBank.Bgm, 0));
            Assert.Throws<ObjectDisposedException>(() => data.Picture(0, 0));

            // what is requested behind the dispose is refused, never decoded from a mapping that is gone
            foreach (Thread t in requesters)
            {
                Assert.True(t.Join(TimeSpan.FromSeconds(60)), "a requester never finished");
            }

            Assert.Empty(unexpected);
            Assert.Equal(0, decoder.Inside);
            Assert.Equal(32, decoder.Begun + Volatile.Read(ref refused));
            output.WriteLine($"round {round}: {insideAtDispose} decodes inside at Dispose, {decoder.Ended} decoded in all, {refused} refused afterwards");
        }
    }

    [GameDataFact]
    public void TheClipCacheShutdownBeforeTheSourceDisposeLeavesNothingRunning()
    {
        // the order GameApp.Dispose now follows: the player first (its cache waits for the decodes), the source second
        var decoder = new CountingDecoder();
        using var data = GameFolderSource.Open(GameFolder.Require(), decoder);
        var cache = new ClipCache(data.Audio);
        var unexpected = new List<Exception>();
        Thread[] requesters = [.. Requesters(cache, 4, 24, AudioBank.Bgm, unexpected), .. Requesters(cache, 4, 24, AudioBank.Se, unexpected)];

        Assert.True(SpinWait.SpinUntil(() => decoder.Inside >= 2, 30_000), "the decodes never started");
        var sw = Stopwatch.StartNew();
        Assert.True(cache.Shutdown(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, decoder.Inside);
        int endedAtShutdown = decoder.Ended;
        foreach (Thread t in requesters)
        {
            Assert.True(t.Join(TimeSpan.FromSeconds(60)), "a requester never finished");
        }

        Assert.Empty(unexpected);
        Assert.Equal(endedAtShutdown, decoder.Ended);                // nothing started after the shutdown
        output.WriteLine($"shutdown took {sw.ElapsedMilliseconds} ms with {endedAtShutdown} clips decoded");
    }

    [GameDataFact]
    public void TheAtrac9DecoderRefusesABufferThatIsShorterThanItsHeaderSays()
    {
        using GameFolderSource data = GameFolderSource.Open(GameFolder.Require(), new MediaAudioDecoder());
        byte[] bytes = data.AudioPayload(AudioBank.Se, 10).ToArray();
        RiffAtrac9 header = RiffAtrac9.Parse(bytes);
        Assert.True(header.Superframes >= 4, "the entry is too short for the test");
        float[] dst = new float[header.DecodedFrames * header.Channels];
        using var dec = new Atrac9Decoder(header);

        int half = header.Superframes / 2;
        byte[] cut = bytes[..(header.DataOffset + (half * header.BlockAlign))];
        ArgumentException e = Assert.Throws<ArgumentException>(() => dec.Decode(cut, 0, header.Superframes, dst));
        Assert.Contains("stream has", e.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => dec.Decode(bytes, header.Superframes, int.MaxValue, dst));
        Assert.Throws<ArgumentException>(() => dec.Decode(bytes, 0, header.Superframes, dst.AsSpan(0, 100)));    // an output that is too small

        int frames = dec.Decode(cut, 0, half, dst);                  // the superframes that are there still decode
        Assert.Equal(half * header.SamplesPerBlock, frames);
    }

    [GameDataFact]
    public void GarbageStreamsFailAsExceptionsAndNeverAsACrash()
    {
        // "a bad stream alone cannot end the process": noise and flipped bits in the superframes of real entries, header intact
        using GameFolderSource data = GameFolderSource.Open(GameFolder.Require(), new MediaAudioDecoder());
        FfmpegLibrary.Load();
        FFmpeg.AutoGen.ffmpeg.av_log_set_level(FFmpeg.AutoGen.ffmpeg.AV_LOG_QUIET);       // libavcodec complains about every bad block on stderr
        try
        {
            GarbageRound(data);
        }
        finally
        {
            FFmpeg.AutoGen.ffmpeg.av_log_set_level(FFmpeg.AutoGen.ffmpeg.AV_LOG_ERROR);
        }
    }

    private void GarbageRound(GameFolderSource data)
    {
        var rng = new Random(20261026);
        int rejected = 0;
        int decoded = 0;
        foreach (AudioBank bank in new[] { AudioBank.Se, AudioBank.Bgm, AudioBank.Voice })
        {
            int n = data.AudioCount(bank);
            for (int k = 0; k < 30; k++)
            {
                byte[] bytes = data.AudioPayload(bank, rng.Next(n)).ToArray();
                RiffAtrac9 header = RiffAtrac9.Parse(bytes);
                if (k % 2 == 0)
                {
                    rng.NextBytes(bytes.AsSpan(header.DataOffset, header.DataSize));
                }
                else
                {
                    for (int j = 0; j < 40; j++)
                    {
                        bytes[header.DataOffset + rng.Next(header.DataSize)] ^= (byte)(1 << rng.Next(8));
                    }
                }

                try
                {
                    _ = Atrac9Decoder.DecodeAll(header, bytes);
                    decoded++;
                }
                catch (InvalidDataException)
                {
                    rejected++;
                }
            }
        }

        output.WriteLine($"{rejected} garbage streams rejected with InvalidDataException, {decoded} decoded as noise");
        Assert.True(rejected > 0);
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Tsuki.Engine.Contracts;
using Tsuki.Media.Ffmpeg;
using Tsuki.Media.Mixer;
using Tsuki.Media.Movie;

namespace Tsuki.Tests.Data.Media;

/// <summary>
/// The movie thread on real movies (package K1c; ARCHITECTURE 7.5): the decoded frames and the AAC samples against what the
/// ffmpeg CLI makes of the same files (<c>tools/oracle/audio.py</c> part <c>movie</c>), the frame and sample counts of
/// <c>prologue_en.mp4</c> (11,660 frames; 18,655,997 samples per channel), the host's real-time clock on a short movie in SDL-free
/// pull mode, and the title-background loop.
/// </summary>
public sealed class MovieTests(ITestOutputHelper output)
{
    private const int PrologueFrames = 11_660;
    private const long PrologueSamples = 18_655_997;

    private static string MoviePath(string name)
    {
        FfmpegLibrary.Load();
        return Path.Combine(GameFolder.Require(), "image", "movie", name + ".mp4");
    }

    private static JsonElement MovieInfo(JsonDocument doc, string name) => doc.RootElement.GetProperty("movies").GetProperty(name);

    private static string[] FrameHashes(string name) =>
        [.. AudioGolden.Tsv($"movie/frames_{name}.tsv").Select(r => r["md5"])];

    private static string Md5(VideoFrame f)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        h.AppendData(f.Y);
        h.AppendData(f.U);
        h.AppendData(f.V);
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }

    [GameDataFact]
    public void ThePrologueDecodesToTheOracleBitsAndCounts()
    {
        using JsonDocument doc = JsonDocument.Parse(AudioGolden.ReadVerified("movie/movies.json"));
        JsonElement info = MovieInfo(doc, "prologue_en");
        Assert.Equal(PrologueFrames, info.GetProperty("frames").GetInt32());
        Assert.Equal(PrologueSamples, info.GetProperty("audio").GetProperty("samples_per_channel").GetInt64());
        string[] md5 = FrameHashes("prologue_en");
        string audioSha1 = info.GetProperty("audio").GetProperty("sha1").GetString()!;

        var sw = Stopwatch.StartNew();
        using var pipeline = new MoviePipeline(MoviePath("prologue_en"));
        Assert.True(pipeline.HasAudio);
        Assert.Equal(1920, pipeline.Width);
        Assert.Equal(1080, pipeline.Height);
        pipeline.Start();

        using var audioHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        float[] pcm = new float[2 * 8192];
        long samples = 0;
        long frames = 0, checkedFrames = 0;
        double lastPts = -1;
        while (true)
        {
            bool idle = true;
            VideoFrame? f = pipeline.Frames.Peek();
            if (f is not null)
            {
                idle = false;
                pipeline.Frames.Dequeue();
                Assert.Equal(frames, f.Index);
                Assert.True(f.Pts > lastPts, $"frame {f.Index}: pts {f.Pts} does not follow {lastPts}");
                lastPts = f.Pts;
                if (f.Index % 20 == 0 || f.Index == PrologueFrames - 1)
                {
                    Assert.Equal(md5[f.Index], Md5(f));
                    checkedFrames++;
                }

                pipeline.Frames.Return(f);
                frames++;
            }

            int got = pipeline.Pcm.Read(pcm);
            if (got > 0)
            {
                idle = false;
                audioHash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pcm.AsSpan(0, got * 2)));
                samples += got;
            }

            if (idle)
            {
                if (pipeline.Frames.EndOfStream && pipeline.Frames.ReadyCount == 0 && pipeline.Pcm.Completed && pipeline.Pcm.BufferedFrames == 0)
                {
                    break;
                }

                Assert.True(sw.Elapsed.TotalSeconds < 55, "the prologue did not finish decoding within 55 s");
                Thread.Sleep(1);
            }
        }

        double seconds = sw.Elapsed.TotalSeconds;
        output.WriteLine($"prologue_en: {frames} frames ({checkedFrames} MD5-checked), {samples} audio samples per channel, " +
            $"{seconds:F1} s ({frames / seconds:F0} fps), {pipeline.VideoThreadCount} decoder threads");
        Assert.Null(pipeline.Error);
        Assert.Equal(PrologueFrames, frames);
        Assert.Equal(PrologueFrames, pipeline.FramesDecoded);
        Assert.Equal(PrologueSamples, samples);
        Assert.Equal(PrologueSamples, pipeline.AudioSamplesDecoded);
        Assert.Equal(0, pipeline.FirstAudioSample);
        Assert.Equal(audioSha1, Convert.ToHexStringLower(audioHash.GetHashAndReset()));
    }

    [GameDataFact]
    public void TheTitleBackgroundLoopsAtItsLoopPointWithAContinuousTimeline()
    {
        string[] md5 = FrameHashes("titlebg1");
        using var pipeline = new MoviePipeline(MoviePath("titlebg1"), new MoviePipelineOptions(StartSeconds: 118, Loop: true));
        pipeline.Start();
        const long LoopFrame = 241; // 8.0333 s at 30 fps (system_flow.md 4.2)
        var sw = Stopwatch.StartNew();
        long expectedIndex = 3540;  // the start trim drops the frames before 118 s
        int afterWrap = 0;
        while (afterWrap < 58 && sw.Elapsed.TotalSeconds < 30)
        {
            VideoFrame? f = pipeline.Frames.Peek();
            if (f is null)
            {
                Thread.Sleep(1);
                continue;
            }

            pipeline.Frames.Dequeue();
            Assert.Equal(expectedIndex, f.Index);
            Assert.Equal(f.Index / 30.0, f.Pts, 6);
            if (f.Index >= 3600)
            {
                long fileFrame = LoopFrame + (f.Index - 3600);
                Assert.Equal(md5[fileFrame], Md5(f));
                afterWrap++;
            }

            expectedIndex++;
            pipeline.Frames.Return(f);
        }

        Assert.Equal(58, afterWrap);
        Assert.Null(pipeline.Error);
        Assert.True(pipeline.Loops >= 1);
        output.WriteLine($"titlebg1: wrapped at frame 3600 into file frame {LoopFrame}, {afterWrap} frames after the wrap equal the oracle");
    }

    [GameDataFact]
    public void TheHostPlaysTheLogoMovieOnTheAudioClockAndEndsIt()
    {
        // TM_CI.mp4 (movie 38): 180 frames, 6.0 s, an AAC track that is digital silence. Real time: about 6.5 s.
        string path = MoviePath("TM_CI");
        string[] md5 = FrameHashes("TM_CI");
        var mixer = new AudioMixer();
        using var host = new MovieHost(_ => path, mixer);
        host.Perform(MovieCommand.Open(MovieSlotForTest, 38, loop: false, volume: 1f));

        long freq = Stopwatch.Frequency;
        long start = Stopwatch.GetTimestamp();
        long nextTick = start;
        long tickTicks = freq / 60;
        long audioFrames = 0;              // frames rendered so far, paced at 48 kHz against the wall clock
        float[] buf = new float[2 * 480];
        MovieClock last = default;
        long lastIndex = -1;
        int shown = 0, checkedFrames = 0, ticks = 0;
        bool ended = false;
        long prevPos = -1;
        while (!ended && Stopwatch.GetTimestamp() - start < 15 * freq)
        {
            long now = Stopwatch.GetTimestamp();
            while ((now - start) * 48_000 / freq >= audioFrames + 480)
            {
                mixer.Render(buf);          // what the SDL callback does, pulled in 10 ms periods
                audioFrames += 480;
            }

            while (now >= nextTick)
            {
                IReadOnlyList<MovieClock> clocks = host.Step(nextTick);
                nextTick += tickTicks;
                ticks++;
                Assert.Single(clocks);
                last = clocks[0];
                Assert.Equal(MovieSlotForTest, last.Slot);
                Assert.Equal(38, last.MovieIndex);
                Assert.True(last.PositionMicroseconds >= prevPos, "the movie clock ran backwards");
                prevPos = last.PositionMicroseconds;
                if (host.CurrentFrame(MovieSlotForTest) is { } f && f.Index != lastIndex)
                {
                    Assert.True(f.Index > lastIndex);
                    lastIndex = f.Index;
                    shown++;
                    if (f.Index % 10 == 0)
                    {
                        Assert.Equal(md5[f.Index], Md5(f));
                        checkedFrames++;
                    }
                }

                ended = last.Ended;
                if (ended)
                {
                    break;
                }
            }

            Thread.Sleep(1);
        }

        double seconds = (Stopwatch.GetTimestamp() - start) / (double)freq;
        MoviePlayer player = host.Player(MovieSlotForTest)!;
        output.WriteLine($"TM_CI: ended after {seconds:F2} s and {ticks} ticks, {shown} of 180 frames shown ({checkedFrames} MD5-checked), " +
            $"clock {last.PositionMicroseconds / 1e6:F3} s, holds {player.Selector.Holds}, early {player.Selector.EarlySwitches}, dropped {player.Selector.Dropped}");
        Assert.True(ended, "the logo movie did not end");
        Assert.False(last.Playing);
        Assert.True(last.PositionMicroseconds >= 5_950_000 && last.PositionMicroseconds < 6_300_000, $"position {last.PositionMicroseconds}");
        Assert.True(seconds is > 5.9 and < 8.5, $"ended after {seconds} s");
        Assert.False(player.UsesFrameClock, "the audio clock should run this movie");
        Assert.Null(player.Error);
        Assert.Equal(179, lastIndex);                       // the last frame was shown
        Assert.True(shown >= 171, $"only {shown} of 180 frames were shown");
        Assert.True(player.Pipeline.Pcm.ReadFrames >= 280_000, "the mixer pulled the movie's audio");
        host.Perform(MovieCommand.Stop(MovieSlotForTest));
        Assert.Equal(0, host.OpenSlots);
    }

    private const int MovieSlotForTest = 0;

    [GameDataFact]
    public void AMovieWithAudioPlaysOnTheFrameClockToItsEndWhenThereIsNoMixer()
    {
        // prologue_en has 388 s of audio but the ring holds 4 s: with no reader the demuxer must not block on it. Seek to its last
        // seconds through the pipeline options so that the test takes a few seconds.
        string path = MoviePath("prologue_en");
        var options = new MoviePipelineOptions(StartSeconds: 385.0);
        using var player = new MoviePlayer(0, 46, path, volume: 1f, mixer: null, new MoviePlayerOptions(options));
        long start = Stopwatch.GetTimestamp();
        long next = start;
        long tick = Stopwatch.Frequency / 60;
        MovieClock c = default;
        while (!c.Ended && Stopwatch.GetTimestamp() - start < 20 * Stopwatch.Frequency)
        {
            while (Stopwatch.GetTimestamp() >= next)
            {
                c = player.Tick(next);
                next += tick;
            }

            Thread.Sleep(1);
        }

        double seconds = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
        output.WriteLine($"prologue_en from 385 s without a mixer: ended after {seconds:F2} s at {c.PositionMicroseconds / 1e6:F2} s, frame clock {player.UsesFrameClock}");
        Assert.True(c.Ended);
        Assert.True(player.UsesFrameClock);
        Assert.True(player.Pipeline.AudioDiscarded);
        Assert.InRange(seconds, 3.2, 6.0);                   // 388.67 - 385 = 3.67 s of movie
        Assert.True(c.PositionMicroseconds > 388_000_000);
    }

    [GameDataFact]
    public void WhenTheAudioDeviceStallsMidMovieTheFrameClockTakesOver()
    {
        string path = MoviePath("TM_CI");
        var mixer = new AudioMixer();
        using var host = new MovieHost(_ => path, mixer, new MovieHostOptions(AudioClockTimeoutSeconds: 0.5));
        host.Perform(MovieCommand.Open(MovieSlotForTest, 38, loop: false, volume: 1f));
        long freq = Stopwatch.Frequency;
        long start = Stopwatch.GetTimestamp();
        long next = start;
        long audioFrames = 0;
        float[] buf = new float[2 * 480];
        MovieClock c = default;
        while (!c.Ended && Stopwatch.GetTimestamp() - start < 15 * freq)
        {
            long now = Stopwatch.GetTimestamp();
            while ((now - start) * 48_000 / freq >= audioFrames + 480 && now - start < 2 * freq)
            {
                mixer.Render(buf);                           // the device delivers for the first two seconds only
                audioFrames += 480;
            }

            while (now >= next)
            {
                c = host.Step(next)[0];
                next += freq / 60;
            }

            Thread.Sleep(1);
        }

        double seconds = (Stopwatch.GetTimestamp() - start) / (double)freq;
        output.WriteLine($"device stalled after 2 s: ended after {seconds:F2} s at {c.PositionMicroseconds / 1e6:F2} s, frame clock {host.Player(0)!.UsesFrameClock}");
        Assert.True(c.Ended);
        Assert.True(host.Player(0)!.UsesFrameClock);
        Assert.InRange(seconds, 5.8, 9.0);                    // no time is lost beyond the stall detection
        Assert.InRange(c.PositionMicroseconds, 5_900_000, 6_800_000);
    }

    [GameDataFact]
    public void WhenTheMixerNeverPullsTheMovieFallsBackToTheFrameClockInsteadOfFreezing()
    {
        // an audio device that is missing or stalled: the movie has an AAC track, the mixer is never rendered
        string path = MoviePath("TM_CI");
        var mixer = new AudioMixer();
        using var host = new MovieHost(_ => path, mixer, new MovieHostOptions(AudioClockTimeoutSeconds: 0.4));
        host.Perform(MovieCommand.Open(MovieSlotForTest, 38, loop: false, volume: 1f));
        long start = Stopwatch.GetTimestamp();
        long tickTicks = Stopwatch.Frequency / 60;
        MovieClock c = default;
        long next = start;
        while (!c.Ended && Stopwatch.GetTimestamp() - start < 12 * Stopwatch.Frequency)
        {
            while (Stopwatch.GetTimestamp() >= next)
            {
                c = host.Step(next)[0];
                next += tickTicks;
            }

            Thread.Sleep(1);
        }

        double seconds = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
        output.WriteLine($"no mixer pull: ended after {seconds:F2} s, position {c.PositionMicroseconds / 1e6:F2} s");
        Assert.True(c.Ended);
        Assert.True(host.Player(MovieSlotForTest)!.UsesFrameClock);
        Assert.InRange(seconds, 6.0, 9.5);          // 0.4 s of waiting for the audio, then the 6 s movie
        Assert.Equal(0, mixer.FramesRendered);
    }

    [GameDataFact]
    public void ATruncatedMovieEndsInsteadOfHangingTheScript()
    {
        // the first 1.5 MB of the 3.7 MB logo movie: it either fails to open or decodes a part and runs into the end of the file
        string source = MoviePath("TM_CI");
        string copy = Path.Combine(Path.GetTempPath(), $"tsuki-k1c-{Guid.NewGuid():N}.mp4");
        try
        {
            using (FileStream input = File.OpenRead(source))
            using (FileStream output2 = File.Create(copy))
            {
                byte[] part = new byte[1_500_000];
                input.ReadExactly(part);
                output2.Write(part);
            }

            using var host = new MovieHost(_ => copy, mixer: null);
            host.Perform(MovieCommand.Open(0, 38, loop: false, volume: 1f));
            long start = Stopwatch.GetTimestamp();
            MovieClock c = default;
            for (int i = 0; i < 1500 && !c.Ended; i++)
            {
                c = host.Step(start + (i * (Stopwatch.Frequency / 60)))[0];
                Thread.Sleep(10);
            }

            output.WriteLine($"truncated movie: ended {c.Ended}, failures {host.Failures.Count}, dropped packets {host.Player(0)?.Pipeline.PacketErrors}, error [{host.Failures.FirstOrDefault()?.Error.Message}], position {c.PositionMicroseconds / 1e6:F2} s");
            Assert.True(c.Ended, "a truncated movie must end");
            Assert.False(c.Playing);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [GameDataFact]
    public void StoppingAMovieMidPlayJoinsBothThreadsPromptly()
    {
        using var pipeline = new MoviePipeline(MoviePath("prologue_en"));
        pipeline.Start();
        Thread.Sleep(400);                          // the 4 s audio ring and the frame pool are full by now: both threads wait
        var sw = Stopwatch.StartNew();
        pipeline.Stop();
        output.WriteLine($"stopped in {sw.ElapsedMilliseconds} ms after {pipeline.FramesDecoded} frames");
        Assert.True(sw.ElapsedMilliseconds < 1000);
        Assert.True(pipeline.DemuxEnded);
        Assert.True(pipeline.Frames.EndOfStream);
        Assert.Null(pipeline.Error);
        Assert.True(pipeline.Pcm.Completed);
    }

    [GameDataFact]
    public void ALoopingMovieNeverEndsAndAMovieOpenedTwiceReplacesTheFirst()
    {
        string path = MoviePath("titlebg1");
        using var host = new MovieHost(_ => path, mixer: null);
        host.Perform(MovieCommand.Open(1, 0, loop: true, volume: 0f));
        host.Perform(MovieCommand.Open(1, 0, loop: true, volume: 0f)); // same slot: the first closes
        Assert.Equal(1, host.OpenSlots);
        for (var wait = Stopwatch.StartNew(); host.IsOpening(1) && wait.Elapsed < TimeSpan.FromSeconds(20);)
        {
            Thread.Sleep(5);                                  // the open runs on a worker (package K8): the ticks below start once the player is ready
        }

        long freq = Stopwatch.Frequency;
        long start = Stopwatch.GetTimestamp();
        MovieClock c = default;
        for (int i = 0; i < 90; i++)
        {
            c = host.Step(start + (i * (freq / 60)))[0];
            Thread.Sleep(10);
        }

        Assert.True(c.Playing);
        Assert.False(c.Ended);
        Assert.True(host.Player(1)!.UsesFrameClock);
        Assert.NotNull(host.CurrentFrame(1));
    }
}

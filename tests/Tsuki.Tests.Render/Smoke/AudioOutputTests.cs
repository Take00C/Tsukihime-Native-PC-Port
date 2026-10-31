using Tsuki.Platform;

namespace Tsuki.Tests.Render.Smoke;

/// <summary>
/// The audio-stream seam (ARCHITECTURE 3.4, 7.1; PACE 7.2 #6 "H1's audio-stream seam") in pull mode:
/// no device, so the result is deterministic. What the render callback writes must come out of SDL's
/// float32 stereo 48 kHz stream bit for bit, in periods of at most 1024 frames, whatever chunk sizes
/// the caller pulls.
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class AudioOutputTests
{
    [Fact]
    public void PullModeIsSampleExactAndUsesPeriodsOfAtMost1024Frames()
    {
        long next = 0;
        int maxPeriod = 0;
        int periods = 0;
        using var output = AudioOutput.CreatePullOnly(buf =>
        {
            int frames = buf.Length / 2;
            maxPeriod = Math.Max(maxPeriod, frames);
            periods++;
            for (int i = 0; i < frames; i++, next++)
            {
                buf[2 * i] = Sample(next, 0);
                buf[(2 * i) + 1] = Sample(next, 1);
            }
        });

        int[] chunks = [1, 7, 480, 1023, 1024, 1025, 4096, 333];
        const int Total = 48_000;
        var got = new float[Total * 2];
        int frame = 0, c = 0;
        while (frame < Total)
        {
            int want = Math.Min(chunks[c++ % chunks.Length], Total - frame);
            int n = output.Pull(got.AsSpan(frame * 2, want * 2));
            Assert.Equal(want, n);
            frame += n;
        }

        for (int f = 0; f < Total; f++)
        {
            Assert.Equal(Sample(f, 0), got[2 * f]);
            Assert.Equal(Sample(f, 1), got[(2 * f) + 1]);
        }

        Assert.InRange(maxPeriod, 1, AudioOutput.MaxPeriodFrames);
        Assert.True(periods >= Total / AudioOutput.MaxPeriodFrames, $"{periods} periods");
        Assert.True(output.FramesRendered >= Total);
        Assert.Null(output.CallbackError);
        Assert.False(output.IsDevice);
    }

    [Fact]
    public void ThrowingCallbackGivesSilenceAndReportsTheError()
    {
        using var output = AudioOutput.CreatePullOnly(_ => throw new InvalidOperationException("mixer broke"));
        var got = new float[512 * 2];
        got.AsSpan().Fill(1f);

        Assert.Equal(512, output.Pull(got));
        Assert.All(got, s => Assert.Equal(0f, s));
        Assert.Contains("mixer broke", output.CallbackError, StringComparison.Ordinal);
    }

    // distinct, exactly representable float32 values: (2f + c) / 2^20
    private static float Sample(long frame, int channel) => ((2 * frame) + channel) / 1048576f;
}

using Tsuki.Engine.Contracts;
using Tsuki.Engine.Movie;
using Tsuki.Media.Movie;

namespace Tsuki.Tests.Synthetic.Media;

/// <summary>The movie commands without a decoder (package K1c): the language remap, virtual time, failed opens and the queues.</summary>
public sealed class MovieHostTests
{
    [Theory]
    [InlineData(Language.Ja, 4, 4)]
    [InlineData(Language.En, 4, 46)]
    [InlineData(Language.Zc, 4, 47)]
    [InlineData(Language.Zt, 4, 106)]
    [InlineData(Language.Ko, 4, 46)]
    [InlineData(Language.Ja, 5, 5)]
    [InlineData(Language.En, 5, 102)]
    [InlineData(Language.Zc, 5, 104)]
    [InlineData(Language.Zt, 5, 104)]
    [InlineData(Language.En, 6, 103)]
    [InlineData(Language.Zc, 6, 105)]
    [InlineData(Language.Zt, 6, 105)]
    [InlineData(Language.Ko, 6, 103)]
    public void ThePrologueAndTheOpeningsPickTheirLanguageCopy(Language lang, int id, int expected) =>
        Assert.Equal(expected, MovieTable.Remap(id, lang));

    [Fact]
    public void TheTwentySevenChapterCardsHaveEnZcAndZtCopiesAtFixedOffsets()
    {
        for (int id = MovieTable.FirstPhaseTitle; id <= MovieTable.LastPhaseTitle; id++)
        {
            Assert.Equal(id, MovieTable.Remap(id, Language.Ja));
            Assert.Equal(id + 40, MovieTable.Remap(id, Language.En));      // 48..74
            Assert.Equal(id + 67, MovieTable.Remap(id, Language.Zc));      // 75..101
            Assert.Equal(id + 99, MovieTable.Remap(id, Language.Zt));      // 107..133
            Assert.Equal(id + 40, MovieTable.Remap(id, Language.Ko));
        }
    }

    [Fact]
    public void EveryOtherMovieIsSharedByAllLanguagesAndEveryResultIsInTheTable()
    {
        foreach (int id in new[] { 0, 1, 2, 3, 7, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45 })
        {
            foreach (Language l in Enum.GetValues<Language>())
            {
                Assert.Equal(id, MovieTable.Remap(id, l));
            }
        }

        for (int id = 0; id < MovieTable.Count; id++)
        {
            foreach (Language l in Enum.GetValues<Language>())
            {
                int index = MovieTable.Remap(id, l);
                Assert.InRange(index, 0, MovieTable.Count - 1);
            }
        }
    }

    [Fact]
    public void InVirtualTimeAMovieEndsAsSoonAsItIsOpened()
    {
        using var host = new MovieHost(i => throw new InvalidOperationException("virtual time never resolves a path"), mixer: null, new MovieHostOptions(VirtualTime: true));
        host.Perform(MovieCommand.Open(0, 46, loop: false, volume: 1f));
        host.Perform(MovieCommand.Open(1, 0, loop: true, volume: 0f));
        IReadOnlyList<MovieClock> clocks = host.Step(0);
        Assert.Equal(2, clocks.Count);
        Assert.Equal(new MovieClock(0, 46, Playing: false, Ended: true, PositionMicroseconds: 0), clocks[0]);
        Assert.Equal(new MovieClock(1, 0, Playing: false, Ended: true, PositionMicroseconds: 0), clocks[1]);
        host.Perform(MovieCommand.SetVolume(0, 0.5f));
        host.Perform(MovieCommand.Stop(0));
        Assert.Single(host.Step(1));
        host.Perform(MovieCommand.Stop(7));                  // a slot that is not open: nothing happens
        host.Perform(MovieCommand.SetVolume(7, 1f));
        Assert.Empty(host.Failures);
        Assert.Null(host.Player(1));
    }

    [Fact]
    public void AMovieThatCannotBeOpenedIsRecordedAndReportsEndedSoTheScriptNeverHangs()
    {
        string missing = Path.Combine(Path.GetTempPath(), "tsuki-k1c-no-such-movie.mp4");
        using var host = new MovieHost(_ => missing);
        host.Perform(MovieCommand.Open(0, 38, loop: false, volume: 1f));
        MovieClock c = host.Step(0).Single();
        Assert.True(c.Ended);
        Assert.False(c.Playing);
        Assert.Equal(38, c.MovieIndex);
        MovieFailure f = Assert.Single(host.Failures);
        Assert.Equal((0, 38), (f.Slot, f.MovieIndex));
        Assert.NotNull(f.Error);
        Assert.Null(host.CurrentFrame(0));
        host.Perform(MovieCommand.Open(0, 38, loop: false, volume: 1f));    // opening again replaces the failed slot
        Assert.Equal(1, host.OpenSlots);
    }

    [Fact]
    public async Task TheFramePoolRentsFramesInOrderAndClosingWakesTheDecoder()
    {
        var q = new FrameQueue(3, 16, 8);
        Assert.Equal(3, q.Capacity);
        VideoFrame a = q.Rent()!, b = q.Rent()!, c = q.Rent()!;
        Assert.Equal(16 * 8, a.Y.Length);
        Assert.Equal(16 * 8 / 4, a.U.Length);
        a.Index = 0;
        a.Pts = 0;
        b.Index = 1;
        b.Pts = 1 / 30.0;
        q.Push(a);
        q.Push(b);
        Assert.Equal(2, q.ReadyCount);
        Span<FrameStamp> stamps = stackalloc FrameStamp[4];
        Assert.Equal(2, q.PeekStamps(stamps));
        Assert.Equal(new FrameStamp(1, 1 / 30.0), stamps[1]);
        Assert.Same(a, q.Peek());
        Assert.Same(a, q.Dequeue());
        q.Return(a);
        Assert.Same(a, q.Rent());                            // the returned buffer is the next one rented

        var waiting = Task.Run(() => q.Rent(), TestContext.Current.CancellationToken);   // all three buffers are out: the decoder waits
        Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(100, TestContext.Current.CancellationToken)));
        q.Close();
        Assert.Null(await waiting);
        GC.KeepAlive(c);
    }

    [Fact]
    public void AFrameWithOddDimensionsIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new VideoFrame(15, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameQueue(1, 16, 8));
    }

    [Fact]
    public void ThePlayerOptionsKeepTheDesignValues()
    {
        var o = new MoviePlayerOptions();
        Assert.Equal(0.004, o.CadenceLockSeconds);
        Assert.Equal(0.010, o.AudioLatencySeconds);
        var p = new MoviePipelineOptions();
        Assert.Equal(8, p.FrameBuffers);
        Assert.Equal(4 * 48_000, p.PcmRingFrames);
        Assert.Equal(8.0333, p.LoopPointSeconds);
    }
}

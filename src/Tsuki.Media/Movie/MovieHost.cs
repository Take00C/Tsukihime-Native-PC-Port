// The media side of the movie commands (ARCHITECTURE 3.2, 7.5): performs the engine's MovieCommands and reports a MovieClock for
// every open slot once per 60 Hz step. A slot is one open movie; the audio of a slot plays on its mixer voice.
using Tsuki.Engine.Contracts;
using Tsuki.Media.Ffmpeg;
using Tsuki.Media.Mixer;

namespace Tsuki.Media.Movie;

/// <summary>Options of a <see cref="MovieHost"/>.</summary>
/// <param name="VirtualTime">
/// The explorer's mode (ARCHITECTURE 10.3): a movie ends as soon as it is opened. Nothing is decoded and FFmpeg is not loaded.
/// </param>
/// <param name="LoopPointSeconds">Where a looping movie restarts (the title backgrounds, system_flow.md 4.2).</param>
/// <param name="VideoThreads">H.264 decoder threads; 0 = automatic.</param>
/// <param name="AudioLatencySeconds">The device buffer subtracted from the audio clock.</param>
/// <param name="AudioClockTimeoutSeconds">With no mixer pull for this long, a movie falls back to the frame clock.</param>
public sealed record MovieHostOptions(
    bool VirtualTime = false, double LoopPointSeconds = MoviePipelineOptions.TitleLoopPointSeconds, int VideoThreads = 0,
    double AudioLatencySeconds = 0.010, double AudioClockTimeoutSeconds = 2.0);

/// <summary>A movie that could not be opened or died while playing; the slot then reports the movie as ended.</summary>
/// <param name="Slot">The slot.</param>
/// <param name="MovieIndex">The movie table index.</param>
/// <param name="Error">What went wrong.</param>
public sealed record MovieFailure(int Slot, int MovieIndex, Exception Error);

/// <summary>
/// Performs <see cref="MovieCommand"/>s and reports the <see cref="MovieClock"/> of every open slot. All members are called from
/// the engine's step thread (the main thread); the decode threads belong to the players. Opening a movie (container, codecs, the decode threads)
/// takes 50..400 ms, so it runs on a worker (package K8): until it is done the slot reports a movie that is not playing and not ended, which is
/// what a player waiting for its first frame reports too; a movie whose file is missing still fails at once.
/// </summary>
public sealed class MovieHost : IDisposable
{
    private sealed class Slot
    {
        public int Index;
        public MoviePlayer? Player;
        public Task<MoviePlayer>? Opening;
        public float Volume;
        public bool Failed;
        public bool VirtualEnded;
    }

    private readonly Func<int, string> _moviePath;
    private readonly AudioMixer? _mixer;
    private readonly MovieHostOptions _options;
    private readonly SortedDictionary<int, Slot> _slots = [];
    private readonly List<MovieClock> _clocks = [];
    private readonly List<MovieFailure> _failures = [];

    /// <summary>Creates a host that resolves movie table indexes through <paramref name="moviePath"/> (<see cref="IGameData.Movie"/>).</summary>
    /// <param name="moviePath">Full path of movie table record <c>index</c> after the language remap.</param>
    /// <param name="mixer">The mixer that plays the movie audio; null plays the video on the frame clock without sound.</param>
    /// <param name="options">Host options.</param>
    public MovieHost(Func<int, string> moviePath, AudioMixer? mixer = null, MovieHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(moviePath);
        _moviePath = moviePath;
        _mixer = mixer;
        _options = options ?? new MovieHostOptions();
    }

    /// <summary>Creates a host over <paramref name="data"/>.</summary>
    public MovieHost(IGameData data, AudioMixer? mixer = null, MovieHostOptions? options = null)
        : this((data ?? throw new ArgumentNullException(nameof(data))).Movie, mixer, options)
    {
    }

    /// <summary>Movies that failed to open or died, oldest first.</summary>
    public IReadOnlyList<MovieFailure> Failures => _failures;

    /// <summary>Number of open slots.</summary>
    public int OpenSlots => _slots.Count;

    /// <summary>The player of <paramref name="slot"/>, or null (closed, failed, or virtual time).</summary>
    public MoviePlayer? Player(int slot) => _slots.TryGetValue(slot, out Slot? s) ? Adopt(slot, s).Player : null;

    /// <summary>True while a movie of <paramref name="slot"/> is being opened on the worker.</summary>
    public bool IsOpening(int slot) => _slots.TryGetValue(slot, out Slot? s) && s.Opening is { IsCompleted: false };

    // takes the player of a finished open into the slot (or records its failure)
    private Slot Adopt(int number, Slot s)
    {
        if (s.Opening is { IsCompleted: true } task)
        {
            s.Opening = null;
            if (task.IsCompletedSuccessfully)
            {
                s.Player = task.Result;
                s.Player.SetVolume(s.Volume);
            }
            else
            {
                s.Failed = true;
                _failures.Add(new MovieFailure(number, s.Index, task.Exception?.GetBaseException() ?? new InvalidOperationException("the movie did not open")));
            }
        }

        return s;
    }

    /// <summary>The frame to draw for <paramref name="slot"/>, or null before the first frame.</summary>
    public VideoFrame? CurrentFrame(int slot) => Player(slot)?.Current;

    /// <summary>Performs the commands of one step in order.</summary>
    public void Perform(ReadOnlySpan<MovieCommand> commands)
    {
        foreach (MovieCommand c in commands)
        {
            Perform(c);
        }
    }

    /// <summary>Performs one command.</summary>
    public void Perform(in MovieCommand command)
    {
        switch (command.Op)
        {
            case MovieOp.Open:
                Open(command);
                break;
            case MovieOp.Stop:
                Close(command.Slot);
                break;
            case MovieOp.SetVolume:
                if (_slots.TryGetValue(command.Slot, out Slot? s))
                {
                    s.Volume = command.Volume;
                    s.Player?.SetVolume(command.Volume);
                }

                break;
            default:
                throw new InvalidOperationException($"unknown movie op {command.Op}");
        }
    }

    private void Open(in MovieCommand c)
    {
        Close(c.Slot);
        var slot = new Slot { Index = c.MovieIndex, Volume = c.Volume };
        _slots[c.Slot] = slot;
        if (_options.VirtualTime)
        {
            slot.VirtualEnded = true;
            return;
        }

        try
        {
            var pipeline = new MoviePipelineOptions(Loop: c.Loop, LoopPointSeconds: _options.LoopPointSeconds, VideoThreads: _options.VideoThreads);
            var player = new MoviePlayerOptions(pipeline, AudioLatencySeconds: _options.AudioLatencySeconds,
                AudioClockTimeoutSeconds: _options.AudioClockTimeoutSeconds);
            string path = _moviePath(c.MovieIndex);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("the movie file does not exist", path);
            }

            int number = c.Slot;
            int movie = c.MovieIndex;
            float volume = c.Volume;
            AudioMixer? mixer = _mixer;
            // a thread of its own: the open is not queued behind other work of the thread pool (a busy pool delayed it by seconds in the test runs)
            slot.Opening = Task.Factory.StartNew(
                () =>
                {
                    if (!FfmpegLibrary.IsLoaded)
                    {
                        FfmpegLibrary.Load();
                    }

                    return new MoviePlayer(number, movie, path, volume, mixer, player);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            slot.Failed = true;
            _failures.Add(new MovieFailure(c.Slot, c.MovieIndex, e));
        }
    }

    private void Close(int slotNumber)
    {
        if (_slots.Remove(slotNumber, out Slot? s))
        {
            Release(s);
        }
    }

    // a player that is still opening is disposed as soon as it exists
    private static void Release(Slot s)
    {
        s.Player?.Dispose();
        s.Opening?.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>
    /// One 60 Hz step: picks the frame of every open movie at <paramref name="timestamp"/> (the tick's nominal time in
    /// <see cref="System.Diagnostics.Stopwatch"/> ticks, ARCHITECTURE 3.4) and returns one clock per open slot, in slot order. The list
    /// is reused by the next call.
    /// </summary>
    public IReadOnlyList<MovieClock> Step(long timestamp)
    {
        _clocks.Clear();
        foreach ((int number, Slot s) in _slots)
        {
            Adopt(number, s);
            if (s.Opening is not null)
            {
                _clocks.Add(new MovieClock(number, s.Index, Playing: false, Ended: false, PositionMicroseconds: 0));       // still opening: nothing to show, not over
            }
            else if (s.Player is { } p)
            {
                MovieClock clock = p.Tick(timestamp);
                if (p.Error is { } error && _failures.All(f => f.Slot != number || f.MovieIndex != s.Index))
                {
                    _failures.Add(new MovieFailure(number, s.Index, error));
                }

                _clocks.Add(clock);
            }
            else
            {
                // virtual time or a movie that never opened: ended at once so that _TOMW never waits for it
                _clocks.Add(new MovieClock(number, s.Index, Playing: false, Ended: true, PositionMicroseconds: 0));
            }
        }

        return _clocks;
    }

    /// <summary>Closes every movie.</summary>
    public void Dispose()
    {
        foreach (Slot s in _slots.Values)
        {
            Release(s);
        }

        _slots.Clear();
    }
}

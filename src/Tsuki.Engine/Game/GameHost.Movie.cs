using Tsuki.Engine.Contracts;
using Tsuki.Engine.Movie;
using Tsuki.Engine.Stage;
using Tsuki.Engine.Vm;

namespace Tsuki.Engine.Game;

/// <summary>The record of the movie system state (state 8, VA 0x5E530; wait_model.md 11.2).</summary>
public sealed class MovieRecord
{
    /// <summary>+0x18: the language-neutral id passed to <c>_TOMP</c>.</summary>
    public int Id;

    /// <summary>+0x30: phase 0..7.</summary>
    public int Phase;

    /// <summary>+0x34: frames since playback started.</summary>
    public int Frame;

    /// <summary>+0x3C: picture alpha.</summary>
    public float Alpha;

    /// <summary>+0x40: fade-out step after a skip.</summary>
    public float Fade;

    /// <summary>+0x48: skip requested in phase 3.</summary>
    public int Skip;

    /// <summary>+0x4C: second fade.</summary>
    public float Aux;

    /// <summary>0xDF293C: enter countdown; the update does nothing while it is above 1.</summary>
    public int Countdown = 20;

    /// <summary>The movie table index after the language remap.</summary>
    public int TableIndex;

    /// <summary>The ADV mode word 0x17DB230 the state's enter callback saved (0xDF2998); the exit restores its latched-skip bit (wait_model.md 9).</summary>
    public int SavedModeWord;

    /// <summary>The state ended; the exit callback ran.</summary>
    public bool Done;
}

// The script side (_TOMP / _TOMW) and the movie state: the movie runs concurrently with the script; _TOMW waits on F36, which the state's exit
// callback clears (wait_model.md 11). The audio and video are performed by Tsuki.Media from the MovieCommands this class emits; without a movie host
// the state runs on virtual time.
public sealed partial class GameHost
{
    private const float ThirdStep = 0.0104166679084301f;
    private MovieRecord? _movie;
    private readonly List<MovieCommand> _movieOut = [];
    private readonly Dictionary<int, MovieClock> _clocks = [];

    private void InitMovie()
    {
    }

    private void ResetMovie()
    {
        _movie = null;
        _movieOut.Clear();
        _clocks.Clear();
    }

    /// <summary>True while a script movie plays (from <c>_TOMP</c> to the exit of the movie state).</summary>
    public bool MovieActive => _movie is { Done: false };

    /// <summary>The movie state, or null.</summary>
    public MovieRecord? MovieState => _movie;

    /// <summary>The picture alpha of the movie (0 when none).</summary>
    public float MovieAlpha => MovieActive ? _movie!.Alpha : 0f;

    /// <summary>Removes and returns the movie commands produced since the last call.</summary>
    public MovieCommand[] TakeMovieCommands()
    {
        MovieCommand[] a = [.. _movieOut];
        _movieOut.Clear();
        return a;
    }

    /// <summary>The clocks the movie host reported for this frame (call before <see cref="Step"/>).</summary>
    public void SetMovieClocks(ReadOnlySpan<MovieClock> clocks)
    {
        _clocks.Clear();
        foreach (MovieClock c in clocks)
        {
            _clocks[c.Slot] = c;
        }
    }

    /// <summary>Raised when a movie starts: the id of <c>_TOMP</c>.</summary>
    public event Action<int>? MovieStarted;

    /// <summary>Raised when the movie state ends.</summary>
    public event Action<int>? MovieEnded;

    private StepResult CmdTomp(in Statement st)
    {
        // the id is read with the partial strtol getter (VA 0xE3C0): absent = 0
        var a = new StageArgs(st);
        int id = ObjectParser.PartialInt(System.Text.Encoding.Latin1.GetBytes(a.Str(0))) ?? 0;
        if (id != 7 || !SkipScene)
        {
            if (id != 7)
            {
                EndSceneSkip();
            }

            // the movie state's enter callback saves 0x17DB230 and clears the latched-skip bit; its exit restores it (wait_model.md 9: skip resumes after the movie)
            int saved = _modeWord;
            _modeWord &= ~2;
            SetF(1103, 1);
            SetF(36, 1);
            int index = MovieTable.Remap(id, _language);
            _movie = new MovieRecord { Id = id, Phase = 0, TableIndex = index, SavedModeWord = saved };
            float volume = Math.Clamp(Config.MusicVolumeCopy / 100f, 0f, 1f);
            _movieOut.Add(MovieCommand.Open(MovieSlots.FullScreen, index, loop: false, volume: volume));
            MovieStarted?.Invoke(id);
        }

        return StepResult.Yield;
    }

    private StepResult CmdTomw(in Statement st)
    {
        // yields every frame and runs again while F36 != 0
        return F(36) != 0 ? StepResult.YieldAt(st.Position) : StepResult.Yield;
    }

    private static readonly Dictionary<int, int> MovieSeenBit = new() { [4] = 2, [5] = 0, [6] = 1, [35] = 3, [36] = 4, [37] = 5 };

    private static (float FadeIn, float FadeOutStart, float FadeOut) MovieParams(int id, Language lang)
    {
        const float slow = 0.0208333358168602f;
        return id switch
        {
            4 => (1f, lang == Language.En ? 23340f : 23040f, 1f),
            5 => (1f, 6420f, 1f),
            6 => (1f, 6300f, 1f),
            7 => (slow, 312f, slow),
            13 => (1f, 540f, 1f),
            35 => (1f, 18960f, 1f),
            36 => (1f, 8880f, 1f),
            37 => (1f, 15240f, 1f),
            >= 39 and <= 45 => (1f, 618f, 1f),
            _ => (slow, 420f, 1f),
        };
    }

    /// <summary>True when the movie can be skipped (ids 4, 5, 6, 35, 36, 37 only after their movie_seen bit is set).</summary>
    public bool MovieSkippable(int id) => !MovieSeenBit.TryGetValue(id, out int bit) || ((Vars.GetG(5) >> bit) & 1) != 0;

    /// <summary>Phases 1..7 of the movie state update (0x5E530); ported from <c>tsuki.wait_model.movie_update</c>.</summary>
    private void UpdateMovie(in InputFrame input, int step)
    {
        MovieRecord? r = _movie;
        if (r is null || r.Done)
        {
            return;
        }

        if (r.Countdown > 1)
        {
            r.Countdown--;
            return;
        }

        bool ready = true;
        bool playing;
        if (_options.VirtualMovies)
        {
            // virtual time: the file lasts until the fade-out has run (the state, not the player, decides the length)
            playing = r.Frame < 100;
        }
        else if (_clocks.TryGetValue(MovieSlots.FullScreen, out MovieClock clock))
        {
            ready = clock.Playing || clock.Ended || r.Phase != 1;
            playing = clock.Playing && !clock.Ended;
        }
        else
        {
            ready = false;
            playing = false;
        }

        bool skipPressed = (input.Pressed & (PadButtons.Cancel | PadButtons.Options)) != 0;
        bool skippable = r.Id is >= 4 and <= 37 ? MovieSkippable(r.Id) : true;
        (float fin, float fstart, float fout) = MovieParams(r.Id, _language);
        int d = 1;
        r.Frame += _options.VirtualMovies ? 60 * d : d;
        float fd = d;
        string action = string.Empty;
        if (r.Phase == 0)
        {
            r.Phase = 1;
        }

        if (r.Phase == 1)
        {
            if (!ready)
            {
                return;
            }

            r.Frame = 0;
            r.Alpha = 0f;
            r.Aux = 0f;
            r.Skip = 0;
            r.Phase = 2;
        }

        if (r.Phase == 2)
        {
            float a = (fd * fin) + r.Alpha;
            a = 1f < a ? 1f : a;
            r.Alpha = a;
            if (a >= 0.5f)
            {
                float x = (fd * ThirdStep) + r.Aux;
                r.Aux = x > 1f ? 1f : x;
            }

            if (r.Frame >= 60)
            {
                r.Phase = 3;
            }

            return;
        }

        if (r.Phase == 7)
        {
            float x = (fd * ThirdStep) + r.Aux;
            r.Aux = 1f < x ? 1f : x;
            float a = r.Alpha - (fd * r.Fade);
            r.Alpha = a;
            if (0f >= a)
            {
                r.Alpha = 0f;
                r.Phase = 6;
                action = "stop";
            }

            if (action.Length == 0)
            {
                return;
            }

            _movieOut.Add(MovieCommand.Stop(MovieSlots.FullScreen));
            return;
        }

        if (r.Phase == 3)
        {
            if (r.Id == 13)
            {
                if (r.Frame < 210)
                {
                    return;
                }

                r.Phase = 4;
            }
            else if (r.Id == 4)
            {
                float lim = _language == Language.En ? 21900f : 21600f;
                if (!(r.Frame >= lim))
                {
                    if (!(skippable && skipPressed))
                    {
                        return;
                    }

                    r.Skip = 1;
                }

                r.Phase = 4;
            }
            else
            {
                r.Phase = 4;
            }
        }

        if (r.Phase == 4)
        {
            if (r.Id == 13 && r.Frame < 330)
            {
                return;
            }

            r.Phase = 5;
        }

        if (r.Phase == 5)
        {
            if (playing)
            {
                if (skippable && (r.Skip != 0 || skipPressed))
                {
                    r.Frame = (int)fstart;
                    r.Phase = 7;
                    r.Fade = 1f / 60f;
                    return;
                }
            }
            else
            {
                r.Phase = 6;
            }
        }

        if (r.Phase is 5 or 6)
        {
            float x = (fd * ThirdStep) + r.Aux;
            r.Aux = 1f < x ? 1f : x;
            if (r.Frame >= fstart)
            {
                float a = r.Alpha - (fd * fout);
                r.Alpha = 0f > a ? 0f : a;
            }

            if (!playing)
            {
                EndMovie(r);
            }
        }
    }

    /// <summary>The exit callback (VA 0x5F6F0): F36 = 0, the saved skip latch (0x5E35C, 0x5F796), the movie_seen bit, the movie stops.</summary>
    private void EndMovie(MovieRecord r)
    {
        r.Done = true;
        SetF(36, 0);
        _modeWord = (_modeWord & ~2) | (r.SavedModeWord & 2);
        if (MovieSeenBit.TryGetValue(r.Id, out int bit))
        {
            Vars.SetG(5, (ushort)(Vars.GetG(5) | (1 << bit)));
        }

        _movieOut.Add(MovieCommand.Stop(MovieSlots.FullScreen));
        MovieEnded?.Invoke(r.Id);
    }
}

using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Movie;
using Tsuki.Engine.Save;
using Tsuki.Engine.Sound;
using Tsuki.Ui;

namespace Tsuki.App.Services;

// Time, sound, language, configuration and progress (IUiSystemServices) and the title / intro movies.
public sealed partial class AppServices
{
    private const int MenuBgmStream = AudioStreams.Bgm0;
    private int _menuBgm;
    private float _menuBgmGain;
    private float _menuBgmTarget;
    private float _menuBgmStep;
    private bool _musicPaused;
    private long _musicStartedAt;
    private double _musicOffsetSeconds;
    private float _musicVolume = 1f;
    private readonly Dictionary<int, (int Entry, int Character)> _lastVoiceOfSlot = [];
    private int _seenLastVoice = -1;

    /// <inheritdoc/>
    public Language Language => Host.Config.Language is >= 0 and <= 3 ? (Language)Host.Config.Language : HostLanguage;

    private Language HostLanguage { get; set; } = Language.En;

    /// <inheritdoc/>
    public ConfigBlock Config => Host.Config;

    /// <inheritdoc/>
    public bool DecideIsCross => true;

    /// <inheritdoc/>
    public void ConfigChanged(ConfigChange change)
    {
        switch (change)
        {
            case ConfigChange.Language:
                if (Language != HostLanguage)
                {
                    RebuildHost(Language);
                }

                break;
            case ConfigChange.Reset:
                if (Host.Config.Language is < 0 or > 3)
                {
                    Host.Config.Language = (short)HostLanguage;
                }

                ApplySoundConfig();
                break;
            default:
                ApplySoundConfig();
                break;
        }

        Commit();
    }

    private void ApplySoundConfig()
    {
        Host.Sound.Config.ReadFrom(Host.Config);
        Host.Sound.ApplyConfig();
        _parts.Movies?.Perform(MovieCommand.SetVolume(MovieSlots.FullScreen, MovieVolume()));
        if (_menuBgm > 0 && _parts.Audio is { } audio)
        {
            audio.Perform([AudioCommand.SetVolume(MenuBgmStream, _menuBgmGain * MusicGain())]);
        }
    }

    private float MusicGain() => Host.Config.MusicMute != 0 ? 0f : Math.Clamp(Host.Config.MusicVolume / 100f, 0f, 1f);

    private float MovieVolume() => Host.Config.MusicMute != 0 ? 0f : Math.Clamp(Host.Config.MusicVolumeCopy / 100f, 0f, 1f);

    /// <inheritdoc/>
    public void CommitSystemData() => Commit();

    /// <inheritdoc/>
    public int Global(int index) => (uint)index < Vars.GCount ? Host.Vars.GetG(index) : 0;

    /// <inheritdoc/>
    public void SetGlobal(int index, int value)
    {
        if ((uint)index < Vars.GCount)
        {
            Host.Vars.SetG(index, unchecked((ushort)value));
        }
    }

    /// <inheritdoc/>
    public bool SceneBit(int bit) => Host.IsSceneReached(bit);

    /// <inheritdoc/>
    public void PlaySystemSound(UiSound sound)
    {
        Host.Sound.SystemSePlay((int)sound);
        FlushMenuSound();
    }

    private void FlushMenuSound()
    {
        AudioCommand[] commands = Host.Sound.TakeCommands();
        if (commands.Length > 0)
        {
            _parts.Audio?.Perform(commands);
        }
    }

    /// <inheritdoc/>
    public void PlayTestSe()
    {
        IReadOnlyList<string> names = _parts.Data.Eboot.Ui.TestSeNames;
        if (names.Count == 0)
        {
            return;
        }

        string name = names[Random.Shared.Next(names.Count)];
        Host.Sound.Run($"_SEPL(19,0,{name},,`017:100);");
        FlushMenuSound();
    }

    /// <inheritdoc/>
    public bool PlayTestVoice(int row)
    {
        if (!_lastVoiceOfSlot.TryGetValue(row, out (int Entry, int Character) v))
        {
            return false;
        }

        Host.Sound.SetPendingVoice(v.Entry, v.Character);
        bool started = Host.Sound.StartVoiceAtMessage();
        FlushMenuSound();
        return started;
    }

    /// <inheritdoc/>
    public void StopTests()
    {
        Host.Sound.Run("_SEPL(19,0,,,);");
        Host.Sound.StopVoice();
        FlushMenuSound();
    }

    // -- menu music ---------------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public void PlayBgm(int track)
    {
        StartMenuBgm(track, 1f);
    }

    private void StartMenuBgm(int track, float gain)
    {
        _menuBgm = track;
        _menuBgmGain = gain;
        _menuBgmTarget = gain;
        _menuBgmStep = 0;
        _musicPaused = false;
        _musicOffsetSeconds = 0;
        _musicStartedAt = _frame;
        _parts.Audio?.Perform(
        [
            AudioCommand.Stop(MenuBgmStream), AudioCommand.Close(MenuBgmStream),
            AudioCommand.OpenFile(MenuBgmStream, AudioBank.Bgm),
            AudioCommand.Play(MenuBgmStream, AudioBank.Bgm, track, true, gain * MusicGain()),
        ]);
    }

    /// <inheritdoc/>
    public void FadeOutBgm(float seconds)
    {
        if (_menuBgm <= 0)
        {
            return;
        }

        _menuBgmTarget = 0;
        _menuBgmStep = seconds <= 0 ? 1f : 1f / (seconds * 60f);
    }

    private void TickAudio()
    {
        if (_menuBgm > 0 && _menuBgmStep > 0 && _menuBgmGain != _menuBgmTarget)
        {
            float g = _menuBgmGain + (_menuBgmGain > _menuBgmTarget ? -_menuBgmStep : _menuBgmStep);
            _menuBgmGain = _menuBgmTarget == 0 ? MathF.Max(0f, g) : g;
            if (_menuBgmGain <= 0.0001f && _menuBgmTarget == 0)
            {
                _parts.Audio?.Perform([AudioCommand.Stop(MenuBgmStream), AudioCommand.Close(MenuBgmStream)]);
                _menuBgm = 0;
                _menuBgmStep = 0;
            }
            else
            {
                _parts.Audio?.Perform([AudioCommand.SetVolume(MenuBgmStream, _menuBgmGain * MusicGain())]);
            }
        }

        // the last voice of each character (the config screen's voice test plays it): noted when the sound model starts a voice
        int last = Host.Sound.LastVoice;
        if (last >= 0 && last != _seenLastVoice)
        {
            _seenLastVoice = last;
            int ch = Host.Sound.PendingVoiceCharacter;
            _lastVoiceOfSlot[VoiceGroups.SlotOf(ch)] = (last, ch);
        }
    }

    /// <summary>Stops everything the menus started (title music, menu movies); the story's own sounds are stopped by the host.</summary>
    public void StopMenuAudio()
    {
        if (_menuBgm > 0)
        {
            _parts.Audio?.Perform([AudioCommand.Stop(MenuBgmStream), AudioCommand.Close(MenuBgmStream)]);
            _menuBgm = 0;
        }
    }

    // -- title and intro movies ---------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public int TitleMovieSlot => MovieSlots.TitleBackground;

    /// <inheritdoc/>
    public int IntroMovieSlot => MovieSlots.FullScreen;

    /// <inheritdoc/>
    public void StartTitleMovie(int movieIndex) =>
        _parts.Movies?.Perform(MovieCommand.Open(MovieSlots.TitleBackground, movieIndex, loop: true, volume: 0f));

    /// <inheritdoc/>
    public void StopTitleMovie() => _parts.Movies?.Perform(MovieCommand.Stop(MovieSlots.TitleBackground));

    /// <inheritdoc/>
    public void SeekTitleMovie(double seconds)
    {
        // the movie pipeline wraps to the loop point by itself (MoviePipelineOptions.LoopPointSeconds), so there is nothing to seek
    }

    /// <inheritdoc/>
    public double TitleMovieTime => _parts.Movies?.CurrentFrame(MovieSlots.TitleBackground) is { } f ? f.Pts : -1;

    /// <inheritdoc/>
    public void StartIntroMovie(int movieIndex)
    {
        int index = movieIndex is 4 or 5 or 6 ? MovieTable.Remap(movieIndex, Language) : movieIndex;
        _parts.Movies?.Perform(MovieCommand.Open(MovieSlots.FullScreen, index, loop: false, volume: MovieVolume()));
        _introStarted = true;
        IntroEnded = false;
        _introAge = 0;
    }

    private bool _introStarted;
    private int _introAge;

    /// <inheritdoc/>
    public bool IntroMoviePlaying => _introStarted && (_parts.Movies?.OpenSlots ?? 0) > 0 && !IntroEnded;

    private bool IntroEnded { get; set; }

    /// <summary>Called by the runtime with the clocks of the movie host after each step: the intro movie ended when its slot says so.</summary>
    public void UpdateMovieClocks(IReadOnlyList<MovieClock> clocks)
    {
        if (!_introStarted)
        {
            return;
        }

        _introAge++;
        foreach (MovieClock c in clocks)
        {
            if (c.Slot == MovieSlots.FullScreen)
            {
                IntroEnded = c.Ended;
                return;
            }
        }

        // no clock yet: the movie was just opened (the host reports it from the next step)
        IntroEnded = _introAge > 3;
    }

    /// <inheritdoc/>
    public void StopIntroMovie()
    {
        _parts.Movies?.Perform(MovieCommand.Stop(MovieSlots.FullScreen));
        _introStarted = false;
        IntroEnded = false;
        _introAge = 0;
    }
}

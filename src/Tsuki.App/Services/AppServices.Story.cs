using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Movie;
using Tsuki.Engine.Save;
using Tsuki.Engine.Sound;
using Tsuki.Ui;

namespace Tsuki.App.Services;

// The running story (IUiStoryServices), the title's facts and the gallery services (IUiGalleryServices).
public sealed partial class AppServices
{
    private readonly List<List<int>> _backlogRings = [];
    private readonly Dictionary<int, int> _backlogVoiceCharacter = [];

    /// <summary>True while the mode panel's rewind button is held.</summary>
    public bool RewindHeld { get; private set; }

    /// <summary>True while the mode panel's fast-forward button is held.</summary>
    public bool FastForwardHeld { get; private set; }

    /// <summary>The scene the title asked for; set by <see cref="StartScene"/>, read once by the runtime to know a story began.</summary>
    public int LastStartedScene { get; private set; } = -1;

    /// <inheritdoc/>
    public int Phase => Host.F(23);

    /// <inheritdoc/>
    public int SceneTitle => Host.F(22);

    /// <inheritdoc/>
    public bool AutoMode => Host.AutoMode;

    /// <inheritdoc/>
    public bool SkipMode => Host.SkipLatch;

    /// <inheritdoc/>
    public bool SceneJumpEnabled => Host.Config.SceneJumpButtons != 0;

    /// <inheritdoc/>
    public bool PanelAllowed => StoryActive && Host.Phase == GamePhase.Running && !Host.MovieActive && !Host.ChoiceOpen && !Host.ArchiveMode;

    /// <inheritdoc/>
    public UiStoryPrompt? StoryPrompt =>
        StoryActive && Host.Phase == GamePhase.Running && Host.PromptOpen && Host.ChoiceAlpha > 0f
            ? new UiStoryPrompt(Host.PromptMessageId, Host.ChoiceCursor, Host.ChoiceAlpha, Host.PromptScale)
            : null;

    /// <inheritdoc/>
    public void ToggleAuto() => Host.SetAutoMode(!Host.AutoMode);

    /// <inheritdoc/>
    public void ToggleSkip() => Host.SetSkipLatch(!Host.SkipLatch);

    /// <inheritdoc/>
    public void FastForward(bool held) => FastForwardHeld = held;

    /// <inheritdoc/>
    public void Rewind(bool held) => RewindHeld = held;

    /// <inheritdoc/>
    public void ReplayVoice()
    {
        int voice = Host.Sound.LastVoice;
        if (voice < 0)
        {
            return;
        }

        int ch = _lastVoiceOfSlot.Values.FirstOrDefault(v => v.Entry == voice).Character;
        Host.Sound.SetPendingVoice(voice, ch);
        Host.Sound.StartVoiceAtMessage();
        FlushMenuSound();
    }

    /// <inheritdoc/>
    public void NextScene() => Host.RequestNextScene();

    /// <inheritdoc/>
    public void PreviousScene()
    {
        if (Host.PreviousScene())
        {
            Host.SetSkipLatch(false);
        }
    }

    /// <inheritdoc/>
    public void StartScene(int sceneId)
    {
        StopMenuAudio();
        StopIntroMovie();
        StopTitleMovie();                       // the title's looping background movie would otherwise keep demuxing and decoding behind the whole story (package R1)
        Host.BeginStory(sceneId);
        Host.SetSkipLatch(false);
        StoryActive = true;
        LastStartedScene = sceneId;
    }

    /// <inheritdoc/>
    public void ResumeLoaded()
    {
        StoryActive = Host.Phase == GamePhase.Running;
    }

    /// <inheritdoc/>
    public void ReturnToTitle()
    {
        StoryActive = false;
        Commit();
        _pendingTitle = true;
    }

    private bool _pendingTitle;

    /// <summary>True once after the story left for the title (the menu's Title button): the runtime then pushes the title screen.</summary>
    public bool TakePendingTitle()
    {
        bool p = _pendingTitle;
        _pendingTitle = false;
        return p;
    }

    /// <summary>Stops the sounds and movies of a story that is being left for the title.</summary>
    public void StopStoryOutputs()
    {
        _parts.Audio?.Perform(SoundModel.StopEverything());
        _parts.Movies?.Perform(MovieCommand.Stop(MovieSlots.FullScreen));
        _menuBgm = 0;
    }

    /// <inheritdoc/>
    public IReadOnlyList<BacklogPage> Backlog
    {
        get
        {
            _backlogRings.Clear();
            _backlogVoiceCharacter.Clear();
            var pages = new List<BacklogPage>();
            if (!StoryActive || Host.History is not { } history)
            {
                return pages;
            }

            // entries are newest first; a page ends (in reading order) at a record whose F799 is 1, so walking from the newest a page is closed by the
            // next older page-end record
            List<int> ring = history.BacklogEntries();
            var current = new List<int>();
            foreach (int i in ring)
            {
                HistoryRecord rec = history.Record(i);
                current.Add(i);
                bool pageStart = AdvHistory.RecordF8(rec, AdvLiveState.FIndex.PageMessage) <= 1;
                if (pageStart)
                {
                    AddPage(history, pages, current);
                    current = [];
                }
            }

            if (current.Count > 0)
            {
                AddPage(history, pages, current);
            }

            return pages;
        }
    }

    private void AddPage(AdvHistory history, List<BacklogPage> pages, List<int> newestFirst)
    {
        var messages = new List<BacklogMessage>(newestFirst.Count);
        for (int k = newestFirst.Count - 1; k >= 0; k--)
        {
            HistoryRecord rec = history.Record(newestFirst[k]);
            if (rec.Kind != AdvHistory.Kinds.Message)
            {
                continue;
            }

            ReadOnlySpan<byte> raw = rec.MessageText;
            int nul = raw.IndexOf((byte)0);
            byte[] text = (nul >= 0 ? raw[..nul] : raw).ToArray();
            int f823 = AdvHistory.RecordF8(rec, AdvLiveState.FIndex.PageMessage);
            int blank = AdvHistory.RecordF8(rec, 846 + Math.Clamp(f823, 0, 11));
            int voice = rec.VoiceId >= 0 ? rec.VoiceId : -1;
            if (voice >= 0)
            {
                _backlogVoiceCharacter[voice] = rec.VoiceCharacter;
            }

            messages.Add(new BacklogMessage(text, Math.Max(0, blank), voice));
        }

        if (messages.Count == 0)
        {
            return;
        }

        // the page's entry for a jump is its first message (the oldest record of the page)
        _backlogRings.Add([newestFirst[^1]]);
        pages.Add(new BacklogPage(messages));
    }

    /// <inheritdoc/>
    public void PlayBacklogVoice(int voiceId)
    {
        int ch = _backlogVoiceCharacter.GetValueOrDefault(voiceId);
        Host.Sound.SetPendingVoice(voiceId, ch);
        Host.Sound.StartVoiceAtMessage();
        FlushMenuSound();
    }

    /// <inheritdoc/>
    public void BacklogJump(int page)
    {
        if ((uint)page >= (uint)_backlogRings.Count)
        {
            return;
        }

        if (Host.BacklogJump(_backlogRings[page][0]))
        {
            Host.SetSkipLatch(false);
        }
    }

    /// <inheritdoc/>
    public void OpenFlowchart()
    {
    }

    /// <inheritdoc/>
    public void OpenGallery()
    {
        StopTitleMovie();
    }

    /// <inheritdoc/>
    public UiTitleInfo Title
    {
        get
        {
            int latest = -1;
            for (int i = 0; i <= SaveStore.AutoSaveLast; i++)
            {
                if (Store.HasSave(i))
                {
                    latest = Store.SystemData.LatestSlot;
                    if (!Store.HasSave(latest))
                    {
                        latest = i;
                    }

                    break;
                }
            }

            return new UiTitleInfo(SceneBit(485), Global(10) != 0, Math.Clamp(Global(2), 0, 5), latest);
        }
    }

    // -- the gallery and the flowchart (IUiGalleryServices) -----------------------------------------------------------------------------

    /// <inheritdoc/>
    public bool SceneDone(int n) => SystemData.SceneDone(Host.Vars, n);

    /// <inheritdoc/>
    public bool EdgeSeen(int id) => SystemData.EdgeSeen(Host.Vars, id);

    /// <inheritdoc/>
    public bool MovieSeen(int bit) => SystemData.MovieSeen(Host.Vars, bit);

    /// <inheritdoc/>
    public bool PictureSeen(int seenIndex) => Store.SystemData.Block.IsPictureSeen(seenIndex);

    /// <inheritdoc/>
    public bool BgmHeard(int track) => Store.SystemData.Block.IsBgmHeard(track);

    /// <inheritdoc/>
    public bool RouteScene(int n) => n is >= 0 and <= 0x23F && ((Host.Vars.GetF(201 + (n >> 3)) >> (n & 7)) & 1) != 0;

    /// <inheritdoc/>
    public bool RouteEdge(int id)
    {
        int e = id - 564;
        return e is >= 0 and < 704 && ((Host.Vars.GetF(600 + (e >> 3)) >> (e & 7)) & 1) != 0;
    }

    /// <inheritdoc/>
    public int CurrentNode => Host.F(110);

    /// <inheritdoc/>
    public int PlayingBgm => _menuBgm > 0 ? _menuBgm : Math.Max(0, Host.Sound.ScriptTrack);

    /// <inheritdoc/>
    public void MarkBgmHeard(int track) => Store.SystemData.MarkBgmHeard(track);

    /// <inheritdoc/>
    public void PlayMusic(int track, float volume)
    {
        _musicVolume = volume;
        StartMenuBgm(track, volume);
    }

    /// <inheritdoc/>
    public void PauseMusic(bool paused)
    {
        if (_menuBgm <= 0 || paused == _musicPaused)
        {
            return;
        }

        if (paused)
        {
            _musicOffsetSeconds += (_frame - _musicStartedAt) / 60.0;
            _parts.Audio?.Perform([AudioCommand.Stop(MenuBgmStream), AudioCommand.Close(MenuBgmStream)]);
        }
        else
        {
            _musicStartedAt = _frame;
            _parts.Audio?.Perform(
            [
                AudioCommand.OpenFile(MenuBgmStream, AudioBank.Bgm),
                AudioCommand.SetStartSample(MenuBgmStream, (int)(_musicOffsetSeconds * AudioStreamsRate)),
                AudioCommand.Play(MenuBgmStream, AudioBank.Bgm, _menuBgm, true, _musicVolume * MusicGain()),
            ]);
        }

        _musicPaused = paused;
    }

    private const int AudioStreamsRate = 48_000;

    /// <inheritdoc/>
    public void SetMusicVolume(float volume)
    {
        _musicVolume = volume;
        _menuBgmGain = volume;
        _menuBgmTarget = volume;
        if (_menuBgm > 0 && !_musicPaused)
        {
            _parts.Audio?.Perform([AudioCommand.SetVolume(MenuBgmStream, volume * MusicGain())]);
        }
    }

    /// <inheritdoc/>
    public void StopMusic() => StopMenuAudio();

    /// <inheritdoc/>
    public bool ReplayActive => StoryActive && (Host.ArchiveMode || Host.ReplayRequested);

    /// <inheritdoc/>
    public void ReplayScene(int scriptScene, int node)
    {
        StopMenuAudio();
        StopIntroMovie();
        if (Host.BeginReplay(scriptScene))
        {
            StoryActive = true;
        }
    }

    /// <inheritdoc/>
    public void EndReplay()
    {
        StoryActive = false;
        StopStoryOutputs();
        Ui?.ReturnToGallery();
    }

    /// <inheritdoc/>
    public bool FlowchartJump(int column, int node, int scene)
    {
        if (!Host.BeginFlowchartJump(column, node, scene))
        {
            return false;
        }

        StopTitleMovie();
        Host.SetSkipLatch(false);
        StoryActive = true;
        return true;
    }
}

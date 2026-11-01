using Tsuki.Engine.Contracts;

namespace Tsuki.Ui;

/// <summary>The display settings of a preview game (package K19): a window of 1600 x 900 on a monitor that takes every preset; every change is logged.</summary>
public sealed class PreviewDisplaySettings(PreviewServices owner) : IUiDisplaySettings
{
    private bool _fullscreen;
    private (int Width, int Height) _size = (1600, 900);

    /// <inheritdoc/>
    public bool Fullscreen
    {
        get => _fullscreen;
        set
        {
            _fullscreen = value;
            owner.Calls.Add(value ? "display fullscreen" : "display windowed");
        }
    }

    /// <inheritdoc/>
    public (int Width, int Height) WindowSize
    {
        get => _size;
        set
        {
            _size = value;
            _fullscreen = false;
            owner.Calls.Add($"window size {value.Width}x{value.Height}");
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<(int Width, int Height)> SizeChoices { get; set; } = [(1280, 720), (1600, 900), (1920, 1080), (2560, 1440)];
}

/// <summary>The port's switches of a preview game (packages K21, K23 and K24): the game's defaults at first (the hooker output and the screenshot notice on, the rest off); every change is logged.</summary>
public sealed class PreviewPortSettings(PreviewServices owner) : IUiPortSettings
{
    private bool _mute;
    private bool _copy;
    private bool _hooker = true;
    private bool _socket;
    private bool _notice = true;

    /// <inheritdoc/>
    public bool ScreenshotNotice
    {
        get => _notice;
        set
        {
            _notice = value;
            owner.Calls.Add(value ? "screenshot notice on" : "screenshot notice off");
        }
    }

    /// <inheritdoc/>
    public bool TextHookerOutput
    {
        get => _hooker;
        set
        {
            _hooker = value;
            owner.Calls.Add(value ? "text hooker on" : "text hooker off");
        }
    }

    /// <inheritdoc/>
    public bool TextWebSocket
    {
        get => _socket;
        set
        {
            _socket = value;
            owner.Calls.Add(value ? "text websocket on" : "text websocket off");
        }
    }

    /// <inheritdoc/>
    public bool MuteInBackground
    {
        get => _mute;
        set
        {
            _mute = value;
            owner.Calls.Add(value ? "mute in background on" : "mute in background off");
        }
    }

    /// <inheritdoc/>
    public bool CopyTextToClipboard
    {
        get => _copy;
        set
        {
            _copy = value;
            owner.Calls.Add(value ? "copy text on" : "copy text off");
        }
    }
}

/// <summary>
/// An in-memory <see cref="IGameServices"/>: a config block, a few save slots with generated thumbnails, a backlog, and a log of every call. The screenshot
/// tool and the tests drive the screens with it; the game host replaces it with the real engine.
/// </summary>
public sealed class PreviewServices : IGameServices
{
    /// <summary>The display settings the config screen shows (package K19); null plays a game without a window, which shows no display rows.</summary>
    public PreviewDisplaySettings? Display { get; set; }

    /// <inheritdoc/>
    IUiDisplaySettings? IUiSystemServices.Display => Display;

    /// <summary>The port's switches the config screen shows (package K21); null plays a game without them, which shows no clipboard and background-mute rows.</summary>
    public PreviewPortSettings? Port { get; set; }

    /// <inheritdoc/>
    IUiPortSettings? IUiSystemServices.Port => Port;

    private readonly List<UiSlot> _slots = [];
    private readonly Dictionary<int, UiThumbnail> _thumbnails = [];
    private readonly Dictionary<int, int> _globals = [];
    private readonly HashSet<int> _sceneBits = [];

    /// <summary>Creates a preview game: boot-default config, <paramref name="usedSlots"/> manual saves, a prologue-seen player.</summary>
    /// <param name="language">The UI language.</param>
    /// <param name="usedSlots">Number of used manual slots (slot 0 upward) and, as auto saves, up to 3 more from slot 100.</param>
    public PreviewServices(Language language = Language.En, int usedSlots = 6)
    {
        Language = language;
        Display = new PreviewDisplaySettings(this);
        Port = new PreviewPortSettings(this);
        Config = ConfigBlock.CreateBootDefaults();
        Config.Language = (short)language;
        for (int i = 0; i < 140; i++)
        {
            _slots.Add(new UiSlot(i, false, default, 0, 0, string.Empty));
        }

        var rng = new UiRandom(7);
        for (int i = 0; i < usedSlots; i++)
        {
            Fill(i, i, rng);
        }

        Fill(100, 7, rng);
        Fill(101, 5, rng);
        Fill(139, 9, rng);
        Title = new UiTitleInfo(PrologueSeen: true, StoryStarted: true, TitleStage: 0, ContinueSlot: usedSlots > 0 ? 100 : -1);
        for (int row = 0; row < 19; row++)
        {
            _globals[row] = 1;
        }
    }

    /// <summary>Every call the UI made, as text, in order.</summary>
    public List<string> Calls { get; } = [];

    /// <inheritdoc/>
    public Language Language { get; set; }

    /// <inheritdoc/>
    public ConfigBlock Config { get; }

    /// <inheritdoc/>
    public bool DecideIsCross { get; set; }

    /// <inheritdoc/>
    public PromptSetting PromptSetting { get; set; } = PromptSetting.PlayStation;

    /// <summary>The style of the device used last (what <see cref="PromptSetting.Auto"/> follows); a tool or test sets it to play the player's last input.</summary>
    public PromptStyle LastDevice { get; set; } = PromptStyle.Keyboard;

    /// <inheritdoc/>
    public PromptStyle PromptStyle => PromptStyles.Resolve(PromptSetting, LastDevice);

    /// <inheritdoc/>
    public UiTitleInfo Title { get; set; }

    /// <inheritdoc/>
    public double TitleMovieTime { get; set; } = -1;

    /// <inheritdoc/>
    public int TitleMovieSlot => 1;

    /// <inheritdoc/>
    public int IntroMovieSlot => 0;

    /// <inheritdoc/>
    public void SeekTitleMovie(double seconds) => Calls.Add($"seek title movie {seconds}");

    /// <inheritdoc/>
    public bool IntroMoviePlaying { get; set; }

    /// <inheritdoc/>
    public bool SavingDisabled { get; set; }

    /// <inheritdoc/>
    public IReadOnlyList<UiSlot> Slots => _slots;

    /// <inheritdoc/>
    public bool HasQuickSave => _slots[139].Used;

    /// <inheritdoc/>
    public int Phase { get; set; } = 12;

    /// <inheritdoc/>
    public int SceneTitle { get; set; } = 6;

    /// <inheritdoc/>
    public int Script { get; set; } = 4;

    /// <inheritdoc/>
    public bool AutoMode { get; set; }

    /// <inheritdoc/>
    public bool SkipMode { get; set; }

    /// <inheritdoc/>
    public bool SceneJumpEnabled { get; set; } = true;

    /// <inheritdoc/>
    public bool PanelAllowed { get; set; } = true;

    /// <inheritdoc/>
    public UiStoryPrompt? StoryPrompt { get; set; }

    /// <inheritdoc/>
    public IReadOnlyList<BacklogPage> Backlog { get; set; } = [];

    /// <summary>Sets the page list the backlog shows.</summary>
    public void SetBacklog(IEnumerable<BacklogPage> pages) => Backlog = [.. pages];

    /// <inheritdoc/>
    public void ConfigChanged(ConfigChange change) => Calls.Add($"config {change}");

    /// <inheritdoc/>
    public void CommitSystemData() => Calls.Add("commit");

    /// <inheritdoc/>
    public int Global(int index) => _globals.TryGetValue(index, out int v) ? v : 0;

    /// <inheritdoc/>
    public void SetGlobal(int index, int value) => _globals[index] = value;

    /// <inheritdoc/>
    public bool SceneBit(int bit) => _sceneBits.Contains(bit);

    /// <summary>Sets a scene bit.</summary>
    public void SetSceneBit(int bit, bool value)
    {
        if (value)
        {
            _sceneBits.Add(bit);
        }
        else
        {
            _sceneBits.Remove(bit);
        }
    }

    /// <inheritdoc/>
    public void PlaySystemSound(UiSound sound) => Calls.Add($"sound {sound}");

    /// <inheritdoc/>
    public void PlayTestSe() => Calls.Add("test se");

    /// <inheritdoc/>
    public bool PlayTestVoice(int row)
    {
        Calls.Add($"test voice {row}");
        return true;
    }

    /// <inheritdoc/>
    public void StopTests() => Calls.Add("stop tests");

    /// <inheritdoc/>
    public void PlayBgm(int track) => Calls.Add($"bgm {track}");

    /// <inheritdoc/>
    public void FadeOutBgm(float seconds) => Calls.Add($"bgm fade {seconds}");

    /// <inheritdoc/>
    public void StartTitleMovie(int movieIndex) => Calls.Add($"title movie {movieIndex}");

    /// <inheritdoc/>
    public void StopTitleMovie() => Calls.Add("title movie stop");

    /// <inheritdoc/>
    public void StartIntroMovie(int movieIndex)
    {
        Calls.Add($"intro movie {movieIndex}");
        IntroMoviePlaying = true;
    }

    /// <inheritdoc/>
    public void StopIntroMovie()
    {
        Calls.Add("intro movie stop");
        IntroMoviePlaying = false;
    }

    /// <inheritdoc/>
    public UiThumbnail? Thumbnail(int slot) => _thumbnails.GetValueOrDefault(slot);

    /// <inheritdoc/>
    public bool Save(int slot)
    {
        Calls.Add($"save {slot}");
        Fill(slot, Phase, new UiRandom((ulong)slot + 1));
        return true;
    }

    /// <inheritdoc/>
    public bool Load(int slot)
    {
        Calls.Add($"load {slot}");
        return _slots[slot].Used;
    }

    /// <inheritdoc/>
    public bool Delete(int slot)
    {
        Calls.Add($"delete {slot}");
        _slots[slot] = new UiSlot(slot, false, default, 0, 0, string.Empty);
        _thumbnails.Remove(slot);
        return true;
    }

    /// <inheritdoc/>
    public bool Copy(int slot)
    {
        Calls.Add($"copy {slot}");
        int free = _slots.Take(100).FirstOrDefault(s => !s.Used).Index;
        if (_slots[free].Used || !_slots[slot].Used)
        {
            return false;
        }

        _slots[free] = _slots[slot] with { Index = free, Date = DateTime.Now };
        _thumbnails[free] = _thumbnails[slot];
        return true;
    }

    /// <inheritdoc/>
    public bool QuickSave()
    {
        Calls.Add("quick save");
        Fill(139, Phase, new UiRandom(139));
        return true;
    }

    /// <inheritdoc/>
    public bool QuickLoad()
    {
        Calls.Add("quick load");
        return HasQuickSave;
    }

    /// <inheritdoc/>
    public void ToggleAuto()
    {
        AutoMode = !AutoMode;
        Calls.Add("auto");
    }

    /// <inheritdoc/>
    public void ToggleSkip()
    {
        SkipMode = !SkipMode;
        Calls.Add("skip");
    }

    /// <inheritdoc/>
    public void FastForward(bool held) => Calls.Add($"fast forward {held}");

    /// <inheritdoc/>
    public void ReplayVoice() => Calls.Add("replay voice");

    /// <inheritdoc/>
    public void NextScene() => Calls.Add("next scene");

    /// <inheritdoc/>
    public void PreviousScene() => Calls.Add("previous scene");

    /// <inheritdoc/>
    public void Rewind(bool held) => Calls.Add($"rewind {held}");

    /// <inheritdoc/>
    public void StartScene(int sceneId) => Calls.Add($"start scene {sceneId}");

    /// <inheritdoc/>
    public void RequestQuit() => Calls.Add("quit");

    /// <inheritdoc/>
    public void ResumeLoaded() => Calls.Add("resume loaded");

    /// <inheritdoc/>
    public bool AutoSaveForTitle()
    {
        Calls.Add("auto save");
        Fill(100, Phase, new UiRandom(100));
        return true;
    }

    /// <inheritdoc/>
    public void ReturnToTitle() => Calls.Add("return to title");

    /// <inheritdoc/>
    public void PlayBacklogVoice(int voiceId) => Calls.Add($"backlog voice {voiceId}");

    /// <inheritdoc/>
    public void BacklogJump(int page) => Calls.Add($"backlog jump {page}");

    /// <inheritdoc/>
    public void OpenFlowchart() => Calls.Add("open flowchart");

    /// <inheritdoc/>
    public void OpenGallery() => Calls.Add("open gallery");

    // ---- gallery, music player, flowchart (package K5b) ----------------------------------------------------------------------------------------

    /// <summary>The unlock bits the gallery reads (empty: nothing was read yet).</summary>
    public PreviewProgress Progress { get; } = new();

    /// <inheritdoc/>
    public bool SceneDone(int n) => Progress.SceneDone(n);

    /// <inheritdoc/>
    public bool EdgeSeen(int id) => Progress.EdgeSeen(id);

    /// <inheritdoc/>
    public bool MovieSeen(int bit) => Progress.MovieSeen(bit);

    /// <inheritdoc/>
    public bool PictureSeen(int seenIndex) => Progress.PictureSeen(seenIndex);

    /// <inheritdoc/>
    public bool BgmHeard(int track) => Progress.BgmHeard(track);

    /// <inheritdoc/>
    public bool RouteScene(int n) => Progress.RouteScene(n);

    /// <inheritdoc/>
    public bool RouteEdge(int id) => Progress.RouteEdge(id);

    /// <inheritdoc/>
    public int CurrentNode { get; set; }

    /// <inheritdoc/>
    public int PlayingBgm { get; private set; }

    /// <summary>The music player's last volume (0..1).</summary>
    public float MusicVolume { get; private set; } = 1f;

    /// <summary>The BGM is paused.</summary>
    public bool MusicPaused { get; private set; }

    /// <inheritdoc/>
    public bool ReplayActive { get; set; }

    /// <inheritdoc/>
    public void MarkBgmHeard(int track)
    {
        Progress.Bgm.Add(track);
        Calls.Add($"bgm heard {track}");
    }

    /// <inheritdoc/>
    public void PlayMusic(int track, float volume)
    {
        PlayingBgm = track;
        MusicVolume = volume;
        MusicPaused = false;
        Calls.Add($"music {track}");
    }

    /// <inheritdoc/>
    public void PauseMusic(bool paused)
    {
        MusicPaused = paused;
        Calls.Add($"music pause {paused}");
    }

    /// <inheritdoc/>
    public void SetMusicVolume(float volume)
    {
        MusicVolume = volume;
        Calls.Add($"music volume {volume:0.00}");
    }

    /// <inheritdoc/>
    public void StopMusic()
    {
        PlayingBgm = 0;
        MusicPaused = false;
        Calls.Add("music stop");
    }

    /// <inheritdoc/>
    public void ReplayScene(int scriptScene, int node) => Calls.Add($"replay {scriptScene} {node}");

    /// <inheritdoc/>
    public void EndReplay() => Calls.Add("end replay");

    /// <inheritdoc/>
    public bool FlowchartJump(int column, int node, int scene)
    {
        Calls.Add($"flow jump {column} {node} {scene}");
        return scene >= 0;
    }

    /// <summary>
    /// Gives the player a believable progress for the gallery screens: <paramref name="profile"/> is <c>mid</c> (scenes 1 to 190 read), <c>early</c> (scenes 1 to
    /// 30), <c>full</c> (everything finished) or <c>none</c>; the current node is the last scene read. Does nothing when the data has no gallery tables.
    /// </summary>
    public void Believe(IGameData data, string profile = "mid")
    {
        ArgumentNullException.ThrowIfNull(data);
        IUiGalleryTables? tables = data.Eboot.Gallery;
        if (tables is null)
        {
            return;
        }

        switch (profile)
        {
            case "none":
                Progress.Clear();
                CurrentNode = 0;
                break;
            case "early":
                Progress.ReadTo(tables, 30);
                CurrentNode = 30;
                break;
            case "full":
                Progress.Everything(tables);
                CurrentNode = 495;
                break;
            default:
                Progress.ReadTo(tables, 190);
                CurrentNode = 190;
                break;
        }

        foreach (int n in Progress.Seen)
        {
            SetSceneBit(n, true);
        }
    }

    private void Fill(int slot, int phase, UiRandom rng)
    {
        var date = new DateTime(2026, 10, 1 + (slot % 25), 9 + (slot % 12), (slot * 7) % 60, 0, DateTimeKind.Unspecified);
        _slots[slot] = new UiSlot(slot, true, date, Math.Clamp(phase + 2, 0, 37), 4 + slot, "I walked the long corridor.^The moon was bright.");
        const int w = 100;
        const int h = 56;
        var px = new byte[w * h * 4];
        float seed = rng.Next();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = ((y * w) + x) * 4;
                px[o] = (byte)(40 + (x * 150 / w));
                px[o + 1] = (byte)(60 + (y * 120 / h));
                px[o + 2] = (byte)(120 + (int)(seed * 100));
                px[o + 3] = 255;
            }
        }

        _thumbnails[slot] = new UiThumbnail(w, h, 1, px);
    }
}

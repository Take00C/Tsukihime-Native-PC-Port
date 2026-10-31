using Tsuki.Engine.Contracts;

namespace Tsuki.Ui;

/// <summary>
/// The PS4 system sounds (<c>SystemSE_Play(id)</c>, docs/formats/sound_runtime.md 10.1): the ids the menus play. The number is the PS4 id; the
/// implementation of <see cref="IUiSystemServices.PlaySystemSound"/> plays the vag pair of that id.
/// </summary>
public enum UiSound
{
    /// <summary>Decide: confirm a choice, open a screen (id 0, moon_se00002).</summary>
    Decide = 0,

    /// <summary>Done: a save or a delete finished, a dialog answered (id 1, moon_se00003).</summary>
    Done = 1,

    /// <summary>Cursor move (id 2, moon_se00001; docs/formats/system_flow.md 4.4 "a change plays SE 2").</summary>
    Cursor = 2,

    /// <summary>Open a dialog or the backlog (id 3, moon_se00007).</summary>
    Open = 3,

    /// <summary>PRESS ANY BUTTON and NEW GAME (id 5, moon_se00011; system_flow.md 4.3, 4.4).</summary>
    Start = 5,

    /// <summary>Buzzer: the action is not available (id 6, moon_se00003; system_flow.md 4.4).</summary>
    Buzzer = 6,
}

/// <summary>The banner of the mode panel's feedback (DISP_PARTS child 10, four rows; knife <c>Banner</c>).</summary>
public enum BannerKind
{
    /// <summary>Skip on.</summary>
    Skip = 0,

    /// <summary>Quick save done.</summary>
    QuickSave = 1,

    /// <summary>Auto save done.</summary>
    AutoSave = 2,

    /// <summary>Auto mode on.</summary>
    Auto = 3,
}

/// <summary>What the config screen changed, so the game applies it (volumes, language reload, voice gates).</summary>
public enum ConfigChange
{
    /// <summary>A volume or a mute changed (music, SFX, system effects, a character's voice).</summary>
    Volume,

    /// <summary>A text or mode setting changed.</summary>
    Setting,

    /// <summary>The language radio changed: reload text, fonts and the UI sheets.</summary>
    Language,

    /// <summary>The block was reset to the boot values ("Recommended Settings").</summary>
    Reset,
}

/// <summary>One save slot as the list shows it (docs/formats/adv_state.md 7).</summary>
/// <param name="Index">Slot number: 0..99 manual, 100..138 auto, 139 quick.</param>
/// <param name="Used">The slot holds a save.</param>
/// <param name="Date">The local date and time of the save.</param>
/// <param name="Phase">Story phase F023 of the save (selects the chapter title cell and the route card; 0..37).</param>
/// <param name="SceneTitle">F022, the scene title number (ui_text 396 + F022).</param>
/// <param name="Notes">The note text under the route card (the last messages of the page), with <c>^</c> line breaks.</param>
public readonly record struct UiSlot(int Index, bool Used, DateTime Date, int Phase, int SceneTitle, string Notes);

/// <summary>A slot thumbnail: straight-alpha RGBA8 pixels, top-down.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Version">Changes when the pixels change, so a texture cache can refresh.</param>
/// <param name="Rgba">Width x Height x 4 bytes.</param>
public sealed record UiThumbnail(int Width, int Height, int Version, ReadOnlyMemory<byte> Rgba);

/// <summary>One message of a backlog page.</summary>
/// <param name="Text">The message bytes with their markup (<c>$</c> references already expanded).</param>
/// <param name="BlankBefore">F847 + k of the message: blank lines before it (docs/formats/textbox.md 5.8; 0 for none).</param>
/// <param name="VoiceId">The voice entry of the message, or -1 when it has none (the replay mark).</param>
public sealed record BacklogMessage(ReadOnlyMemory<byte> Text, int BlankBefore = 0, int VoiceId = -1);

/// <summary>One page of the history the backlog shows (docs/formats/adv_state.md 5.4).</summary>
/// <param name="Messages">The messages of the page.</param>
/// <param name="Jumpable">The page can be jumped back to.</param>
public sealed record BacklogPage(IReadOnlyList<BacklogMessage> Messages, bool Jumpable = true);

/// <summary>What the title needs to know about the player's progress (docs/formats/system_flow.md 1, 3.4, 4.2).</summary>
/// <param name="PrologueSeen">Scene bit 485: the prologue has been read.</param>
/// <param name="StoryStarted">G010 is set: a story was started.</param>
/// <param name="TitleStage">G002 (0..5): the title background stage.</param>
/// <param name="ContinueSlot">The newest save (slot 0..138), or -1 when there is none (CONTINUE is hidden).</param>
public readonly record struct UiTitleInfo(bool PrologueSeen, bool StoryStarted, int TitleStage, int ContinueSlot);

/// <summary>
/// The PC port's display settings (package K19): the window mode and the windowed size, applied at once and remembered. The config screen's System tab shows
/// a Display Mode row and a Window Size row when the game provides them; the previews and the tests provide <see cref="PreviewDisplaySettings"/>, a game
/// without a window provides nothing and the rows are not shown.
/// </summary>
public interface IUiDisplaySettings
{
    /// <summary>Borderless fullscreen on the window's monitor (true) or a window (false). Setting it switches at once and remembers the choice; Alt+Enter and F11 change the same state.</summary>
    bool Fullscreen { get; set; }

    /// <summary>The client size of the window when it is not fullscreen. Setting it leaves fullscreen, resizes the window (kept inside the monitor) and remembers the size.</summary>
    (int Width, int Height) WindowSize { get; set; }

    /// <summary>The window sizes on offer: the presets that fit the monitor the window is on, smallest first.</summary>
    IReadOnlyList<(int Width, int Height)> SizeChoices { get; }
}

/// <summary>
/// The PC port's own switches (package K21), kept in <c>settings.ini</c> and not in the PS4 config block: the config screen's System tab shows a Copy Text to
/// Clipboard row and its Sound tab a Mute in Background row when the game provides them. Setting a value applies it at once and saves it. The previews and the
/// tests provide <see cref="PreviewPortSettings"/>; a game without them shows no such rows.
/// </summary>
public interface IUiPortSettings
{
    /// <summary>Every sound fades out while the window has no focus and back in when it returns (Off by default).</summary>
    bool MuteInBackground { get; set; }

    /// <summary>Each new message's plain text (and a choice's lines) goes to the clipboard when it starts displaying, for text hookers and dictionaries (Off by default).</summary>
    bool CopyTextToClipboard { get; set; }

    /// <summary>Package K23: each new line goes once through the GDI text calls a text hooker (LunaHook, Textractor) attaches to (On by default).</summary>
    bool TextHookerOutput { get; set; }

    /// <summary>Package K23: a WebSocket on 127.0.0.1 broadcasts each new line as a text frame for browser text hookers (Off by default).</summary>
    bool TextWebSocket { get; set; }

    /// <summary>Package K24: the short "Screenshot saved" notice after F12 (On by default); Off saves the picture silently.</summary>
    bool ScreenshotNotice { get; set; }
}

/// <summary>
/// Time, sound, language, configuration and progress: what every screen reads from the game. Implemented by the game host (package K3) over the real
/// engine state; <see cref="PreviewServices"/> is an in-memory implementation for tests and screenshots.
/// </summary>
public interface IUiSystemServices
{
    /// <summary>The display settings of the PC port (package K19), or null for a game that has no window: the config screen then shows no display rows.</summary>
    IUiDisplaySettings? Display => null;

    /// <summary>The port's own switches (package K21), or null for a game without them: the config screen then shows no clipboard and background-mute rows.</summary>
    IUiPortSettings? Port => null;

    /// <summary>The effective UI language (config +0x24, else the system language; docs/formats/archives_ui_movies.md 7.1).</summary>
    Language Language { get; }

    /// <summary>The live config block (320 bytes at system block +0x04). The config screen edits fields in place and then calls <see cref="ConfigChanged"/>.</summary>
    ConfigBlock Config { get; }

    /// <summary>The config screen changed the block: apply volumes, reload the language, and so on.</summary>
    void ConfigChanged(ConfigChange change);

    /// <summary>Writes the system data (the config and the progress words) after a screen that changed them closed.</summary>
    void CommitSystemData();

    /// <summary>True when decide is CROSS (the console's enter-button setting): the guide icons swap.</summary>
    bool DecideIsCross { get; }

    /// <summary>
    /// The config screen's choice of button prompts (Auto follows the device used last). It is an app setting, kept outside the PS4 config block; setting it
    /// saves it. Default for a game that has no such setting: the PlayStation icons the game was made with.
    /// </summary>
    PromptSetting PromptSetting
    {
        get => PromptSetting.PlayStation;
        set { }
    }

    /// <summary>The prompts the screens draw now: <see cref="PromptSetting"/>, or for Auto the style of the device used last.</summary>
    PromptStyle PromptStyle => PromptStyle.PlayStation;

    /// <summary>Progress word G<paramref name="index"/> (the persistent array at VA 0x164C620).</summary>
    int Global(int index);

    /// <summary>Sets progress word G<paramref name="index"/>.</summary>
    void SetGlobal(int index, int value);

    /// <summary>The flowchart scene bit <paramref name="bit"/> (the words at G + 0xDC; 485, 560, 383, 562 steer the title).</summary>
    bool SceneBit(int bit);

    /// <summary>Plays a system sound.</summary>
    void PlaySystemSound(UiSound sound);

    /// <summary>Plays the config screen's SE preview (a random one of the 14 test names on a free channel at volume 100).</summary>
    void PlayTestSe();

    /// <summary>Plays the last voice of config voice row <paramref name="row"/> (0..18); false when the row has none yet.</summary>
    bool PlayTestVoice(int row);

    /// <summary>Stops the test SE and test voice (the config screen closed).</summary>
    void StopTests();

    /// <summary>Starts a title BGM track (2, or 72 at the last stage) in its slot, looping.</summary>
    void PlayBgm(int track);

    /// <summary>Fades the BGM out over <paramref name="seconds"/>.</summary>
    void FadeOutBgm(float seconds);

    /// <summary>Starts the title background movie (<paramref name="movieIndex"/> in the movie table, titlebg1..4) in a looping slot.</summary>
    void StartTitleMovie(int movieIndex);

    /// <summary>Stops the title background movie.</summary>
    void StopTitleMovie();

    /// <summary>The movie slot of the title background (the display list's <see cref="TextureKind.MovieFrame"/> reference).</summary>
    int TitleMovieSlot { get; }

    /// <summary>The movie slot of the full-screen intro movie.</summary>
    int IntroMovieSlot { get; }

    /// <summary>Seeks the title movie to <paramref name="seconds"/> (the loop point 8.0333 s; docs/formats/system_flow.md 4.2).</summary>
    void SeekTitleMovie(double seconds);

    /// <summary>The playing title movie's time in seconds, or -1 while no frame is ready (the title draws the still then).</summary>
    double TitleMovieTime { get; }

    /// <summary>Starts a full-screen movie that a button skips (TM_CI, movie 38).</summary>
    void StartIntroMovie(int movieIndex);

    /// <summary>True while the intro movie plays.</summary>
    bool IntroMoviePlaying { get; }

    /// <summary>Stops the intro movie.</summary>
    void StopIntroMovie();
}

/// <summary>Saving and loading (docs/formats/adv_state.md 7), implemented over package K4's save container.</summary>
public interface IUiSaveServices
{
    /// <summary>True while saving is disabled (F025 != 0): the system menu greys Save and Load.</summary>
    bool SavingDisabled { get; }

    /// <summary>The slot list: manual slots 0..99, auto 100..138, quick 139. Unused slots have <see cref="UiSlot.Used"/> false.</summary>
    IReadOnlyList<UiSlot> Slots { get; }

    /// <summary>The thumbnail of a used slot, or null.</summary>
    UiThumbnail? Thumbnail(int slot);

    /// <summary>Saves the current page into <paramref name="slot"/> (manual slots only); false on a write error.</summary>
    bool Save(int slot);

    /// <summary>Loads <paramref name="slot"/> in game (or from the title); false when it cannot be read.</summary>
    bool Load(int slot);

    /// <summary>Deletes a manual slot.</summary>
    bool Delete(int slot);

    /// <summary>Copies a used slot into the first free manual slot (the PS4 save list's Copy Data); false when none is free.</summary>
    bool Copy(int slot);

    /// <summary>Quick save (OPTIONS / the panel); false while the cooldown runs or saving is disabled.</summary>
    bool QuickSave();

    /// <summary>Quick load (slot 139); false when there is none.</summary>
    bool QuickLoad();

    /// <summary>True when the quick slot holds a save.</summary>
    bool HasQuickSave { get; }
}

/// <summary>The running story: scene state the system menu and the mode panel read, and the actions they take.</summary>
public interface IUiStoryServices
{
    /// <summary>Story phase F023 (selects the system menu's chapter cell; 0..37).</summary>
    int Phase { get; }

    /// <summary>Scene title number F022 (ui_text 396 + F022).</summary>
    int SceneTitle { get; }

    /// <summary>Auto mode is on.</summary>
    bool AutoMode { get; }

    /// <summary>Latched skip is on.</summary>
    bool SkipMode { get; }

    /// <summary>The previous / next scene buttons are enabled (config +0x32).</summary>
    bool SceneJumpEnabled { get; }

    /// <summary>The mode panel may open now (not in a movie, a choice or a locked scene).</summary>
    bool PanelAllowed { get; }

    /// <summary>Toggles auto mode.</summary>
    void ToggleAuto();

    /// <summary>Toggles latched skip.</summary>
    void ToggleSkip();

    /// <summary>Fast-forwards while true (the mode panel's skip button held).</summary>
    void FastForward(bool held);

    /// <summary>Replays the voice of the current message (the mode panel's last button).</summary>
    void ReplayVoice();

    /// <summary>Jumps to the next scene (after the confirmation); the game runs the script until the next <c>_ATSV</c>.</summary>
    void NextScene();

    /// <summary>Jumps to the previous scene.</summary>
    void PreviousScene();

    /// <summary>Rewinds one page while true (the panel's rewind button held).</summary>
    void Rewind(bool held);

    /// <summary>Starts a new game: scene 110 after the prologue was read, else 260 (<see cref="StartScene"/> with the scene id).</summary>
    void StartScene(int sceneId);

    /// <summary>The title's <c>return 3</c>: resume the loaded save.</summary>
    void ResumeLoaded();

    /// <summary>
    /// The system menu's Title: writes an auto save unless saving is off or the newest auto save already holds this point; true when one was written
    /// (the UI then shows the Auto Save banner).
    /// </summary>
    bool AutoSaveForTitle();

    /// <summary>Back to the title: ends the story (called once the menu and the banner are gone).</summary>
    void ReturnToTitle();

    /// <summary>The history pages for the backlog, newest first; empty when there is nothing to show.</summary>
    IReadOnlyList<BacklogPage> Backlog { get; }

    /// <summary>Replays the voice of a backlog message.</summary>
    void PlayBacklogVoice(int voiceId);

    /// <summary>Jumps back to backlog page <paramref name="page"/> (0 = newest).</summary>
    void BacklogJump(int page);

    /// <summary>
    /// The player chose the system menu's FlowChart. A notification: the UI opens the chart itself (<c>FlowchartScreen</c>, package K5b) when it is available
    /// (<see cref="IUiProgress"/>), and the host needs nothing more.
    /// </summary>
    void OpenFlowchart();

    /// <summary>
    /// The player chose the title's GALLERY and the title went black. A notification: the UI opens the gallery (<c>GalleryScreen</c>, package K5b) over the title
    /// itself; the host may stop the title's movie. A replay started there ends with the host calling <see cref="UiRoot.ReturnToGallery"/>.
    /// </summary>
    void OpenGallery();
}

/// <summary>
/// Everything the menus talk to the game through (package K5a). K3 (game) and K4 (save) implement it over the engine;
/// <see cref="PreviewServices"/> implements it in memory.
/// </summary>
public interface IGameServices : IUiSystemServices, IUiSaveServices, IUiStoryServices, IUiGalleryServices
{
    /// <summary>
    /// The two-button prompt the story has open (<c>_SELS</c>, <c>_SELA</c>), or null: the UI draws it over the story while no screen is open (package K13). The game host
    /// keeps its state and its input; a game with no such prompt keeps the default.
    /// </summary>
    UiStoryPrompt? StoryPrompt => null;

    /// <summary>The title facts: prologue seen, story started, G002, and the newest save.</summary>
    UiTitleInfo Title { get; }

    /// <summary>
    /// The player answered Yes to the title's EXIT question (package K18): the host ends the run the way the window's close button does (it writes the save file and
    /// leaves its loop). A host with no way to quit keeps the default, which does nothing.
    /// </summary>
    void RequestQuit()
    {
    }

    /// <summary>scene_seen(n) is the scene bit of <see cref="IUiSystemServices.SceneBit"/> (package K5b reads it through <see cref="IUiProgress"/>).</summary>
    bool IUiProgress.SceneSeen(int n) => SceneBit(n);
}

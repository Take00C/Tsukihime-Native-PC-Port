// IGameServices over the engine (package K6): the menus talk to the game host, the history ring, the save store, the system data, the sound model and the
// movie host through this object (K5a.md "The game side", K5b.md "IGameServices additions"). One partial class per group of members.
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;
using Tsuki.Media.Movie;
using Tsuki.Media.Sound;
using Tsuki.Ui;

namespace Tsuki.App.Services;

/// <summary>What <see cref="AppServices"/> is made of.</summary>
public sealed class AppServicesParts
{
    /// <summary>The game data (text tables, eboot tables).</summary>
    public required IGameData Data { get; init; }

    /// <summary>Builds a host for a language over a profile (used at start and when the language changes).</summary>
    public required Func<Language, GameProfile, GameHost> HostFactory { get; init; }

    /// <summary>The save slots and the system data; the profile is built over its system block.</summary>
    public required SaveStore Store { get; init; }

    /// <summary>Performs the sound commands (the mixer behind it); null: silent.</summary>
    public AudioCommandPlayer? Audio { get; init; }

    /// <summary>Performs the movie commands; null: no movies.</summary>
    public MovieHost? Movies { get; init; }

    /// <summary>The save file (%APPDATA%/Tsuki/save.dat); null: nothing is written.</summary>
    public string? SavePath { get; init; }

    /// <summary>The clock for slot dates.</summary>
    public Func<DateTime>? Now { get; init; }

    /// <summary>The language of a fresh profile.</summary>
    public Language InitialLanguage { get; init; } = Language.En;

    /// <summary>The settings file next to the save file (package K9: the button prompts); null: nothing is read or written.</summary>
    public string? SettingsPath { get; init; }

    /// <summary>A prompt setting that replaces the saved one for this run (the <c>--prompts</c> option); null: the saved one.</summary>
    public PromptSetting? PromptOverride { get; init; }
}

/// <summary>The game side of the menus.</summary>
public sealed partial class AppServices : IGameServices
{
    private readonly AppServicesParts _parts;
    private readonly Func<DateTime> _now;
    private long _frame;

    /// <summary>Creates the services and the first host.</summary>
    public AppServices(AppServicesParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        _parts = parts;
        _now = parts.Now ?? (() => DateTime.Now);
        Store = parts.Store;
        var vars = new Vars();
        Store.SystemData.LoadGlobalsInto(vars);
        var profile = new GameProfile(Store.SystemData.Block, vars);
        IReadOnlyList<Language> available = AvailableLanguagesOf(parts.Data);
        if (profile.System.Config.Language is < 0 or > 3 || (available.Count > 0 && !available.Contains((Language)profile.System.Config.Language)))
        {
            // a language the data does not carry (package K25: another dump) falls back to the start-up language, else the first the data has
            profile.System.Config.Language = (short)(available.Count == 0 || available.Contains(parts.InitialLanguage) ? parts.InitialLanguage : available[0]);
        }

        HostLanguage = (Language)profile.System.Config.Language;
        Host = parts.HostFactory(HostLanguage, profile);
        AttachHost(Host);
        Store.UpdateLatestSlot();
    }

    /// <summary>The languages the data carries (package K25); every selectable language for a data source that does not say (a test rig).</summary>
    private static IReadOnlyList<Language> AvailableLanguagesOf(IGameData data)
    {
        try
        {
            return data.AvailableLanguages;
        }
        catch (NotSupportedException)
        {
            return [Language.Ja, Language.En, Language.Zc, Language.Zt];
        }
    }

    /// <summary>The save slots and system data.</summary>
    public SaveStore Store { get; }

    /// <summary>The current host (replaced when the language changes).</summary>
    public GameHost Host { get; private set; }

    /// <summary>The UI that shows these services (set by the runtime; used for the gallery replay).</summary>
    public UiRoot? Ui { get; set; }

    /// <summary>The 60 Hz frame of the runtime (slot dates, cooldowns).</summary>
    public long Frame => _frame;

    /// <summary>True once the player answered Yes to the title's EXIT question (package K18); the app ends the run the way the window's close button does.</summary>
    public bool QuitRequested { get; private set; }

    /// <summary>The PC port's display settings (package K19), set by the app once the window exists; null in a game without a window (no display rows in the config screen).</summary>
    public IUiDisplaySettings? Display { get; set; }

    /// <summary>The PC port's own switches (package K21: Mute in Background, Copy Text to Clipboard), set by the app; null in a game without them (no such rows in the config screen).</summary>
    public IUiPortSettings? Port { get; set; }

    /// <inheritdoc/>
    public void RequestQuit() => QuitRequested = true;

    /// <summary>Captures the current frame for a thumbnail (320 x 180 RGBA); null: no thumbnails.</summary>
    public Func<(int Width, int Height, byte[] Rgba)?>? FrameCapture { get; set; }

    /// <summary>Raised when the host object was replaced (the runtime rewires what hangs on it).</summary>
    public event Action<GameHost>? HostReplaced;

    /// <summary>Raised when the story asks for the save screen (the "yes" of a save prompt choice).</summary>
    public event Action? SaveScreenRequested;

    /// <summary>Raised when a story ended and the menus should take over (<c>_END_</c>, <c>_RTM_</c>, a replay's end).</summary>
    public event Action<EndReason>? StoryEnded;

    /// <summary>True while a story (a new game, a loaded save, a replay) is the thing on screen under the menus.</summary>
    public bool StoryActive { get; private set; }

    /// <summary>Messages the services want in the log (save errors, ...).</summary>
    public event Action<string>? Logged;

    private void Log(string message) => Logged?.Invoke(message);

    private void AttachHost(GameHost host)
    {
        host.UseExternalAudio(stream => IsStreamPlaying(host, stream));
        AdvHistorySink sink = host.AttachHistory(Store);
        sink.ThumbnailProvider = CaptureThumbnail;
        // SaveImage_Commit(2) follows the auto save of _ATSV(8) on the PS4 (adv_state.md 7.2); a scene start writes the file too, so that the read marks,
        // the unlocks and the G words of a session survive a crash (R1-engine 3)
        sink.AutoSaved += () => Commit();
        sink.SceneStackPushed += () => Commit();
        host.SavePromptAccepted += () => SaveScreenRequested?.Invoke();
    }

    // The sound model asks is_playing inside the host's step: the script's OpenHandle + Play of a voice are queued in the host, and EndOfVoice asks a few
    // statements later in the same step, before the runtime has performed them. The sound library answers true from the play call on (GameHost.UpdateSound
    // does the same with its virtual clock), so the queued commands go to the player first; otherwise the model closes the voice stream in the same step
    // and the pending play is cancelled before it reaches the mixer (package R1: every script voice was dropped this way, and every _VCWT wait skipped).
    private bool IsStreamPlaying(GameHost host, int stream)
    {
        if (_parts.Audio is not { } audio)
        {
            return false;
        }

        PerformHostAudio(host, audio);
        return audio.IsPlaying(stream);
    }

    /// <summary>Performs the sound commands the host has produced so far (the runtime calls it after each step; the is_playing answer calls it inside one).</summary>
    public void PerformHostAudio()
    {
        if (_parts.Audio is { } audio)
        {
            PerformHostAudio(Host, audio);
        }
        else
        {
            Host.TakeAudioCommands();
        }
    }

    private void PerformHostAudio(GameHost host, AudioCommandPlayer audio)
    {
        AudioCommand[] commands = host.TakeAudioCommands();
        if (commands.Length == 0)
        {
            return;
        }

        audio.Perform(commands);
        CheckVoicePlays(commands, audio);
    }

    /// <summary>Script voice plays handed to the player (a <c>Play</c> on a voice stream that no stop or close of the same batch cancelled).</summary>
    public long VoicePlaysPerformed { get; private set; }

    /// <summary>
    /// Voice plays the player did not take although nothing cancelled them and no decode failure was recorded: the self-check of the verification mode (a
    /// run with such a drop plays its voices silently and skips every voice wait; the app fails an <c>--exit-after</c> run on it).
    /// </summary>
    public long VoicePlaysDropped { get; private set; }

    private void CheckVoicePlays(AudioCommand[] commands, AudioCommandPlayer audio)
    {
        for (int i = 0; i < commands.Length; i++)
        {
            AudioCommand c = commands[i];
            if (c.Op != AudioOp.Play || c.Bank is not (AudioBank.Voice or AudioBank.Voice2))
            {
                continue;
            }

            bool cancelled = false;
            for (int j = i + 1; j < commands.Length && !cancelled; j++)
            {
                cancelled = commands[j].Stream == c.Stream && commands[j].Op is AudioOp.Stop or AudioOp.Close;
            }

            if (cancelled)
            {
                continue;
            }

            VoicePlaysPerformed++;
            if (!audio.IsPlaying(c.Stream) && audio.Failures.Count == _voiceFailuresSeen)
            {
                VoicePlaysDropped++;
                if (VoicePlaysDropped == 1)
                {
                    Log($"verification: audio dead (the play of voice {c.Bank}#{c.Entry} on stream {c.Stream} did not reach the mixer)");
                }
            }

            _voiceFailuresSeen = audio.Failures.Count;
        }
    }

    private int _voiceFailuresSeen;

    /// <summary>Advances the services' own clocks one 60 Hz step (title music fades, voice rows).</summary>
    public void Tick()
    {
        _frame++;
        TickAudio();
    }

    /// <summary>Called by the runtime after a story step: reports the end of the story once.</summary>
    public void CheckStoryEnd()
    {
        if (StoryActive && Host.Phase == GamePhase.Ended)
        {
            StoryActive = false;
            EndReason reason = Host.EndReason;
            Commit();
            StoryEnded?.Invoke(reason);
        }
    }

    /// <summary>
    /// Writes the save file (system data, config, G words, slots). Errors are logged, not thrown; the result says whether the file was written (a save or
    /// quick save whose commit failed reports failure to the player, package R1), and the first failure raises <see cref="SaveFailed"/> with the path and
    /// the operating system's words. Without a save file (<c>--no-save</c>) nothing is written and the result is true.
    /// </summary>
    public bool Commit()
    {
        if (_parts.SavePath is not { } path)
        {
            return true;
        }

        try
        {
            SaveFile.Commit(path, Store, Host.Vars);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log($"save file not written: {e.Message} ({path})");
            if (!_saveFailureReported)
            {
                _saveFailureReported = true;
                SaveFailed?.Invoke(path, e.Message);
            }

            return false;
        }
    }

    private bool _saveFailureReported;

    /// <summary>Raised once, at the first save file write that failed: the file's path and the error (the app shows a notice unless hidden).</summary>
    public event Action<string, string>? SaveFailed;

    private SlotDate Date() => SlotDate.FromDateTime(_now());

    private SlotThumbnail? CaptureThumbnail()
    {
        if (FrameCapture?.Invoke() is not { } f)
        {
            return null;
        }

        return ThumbnailCodec.Encode(f.Width, f.Height, f.Rgba);
    }
}

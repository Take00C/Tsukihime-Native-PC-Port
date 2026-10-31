// The game: window, GPU, audio, movies, saves and the 60 Hz loop around AppRuntime (ARCHITECTURE 3.4, 6.2; package K6). Also the verification mode for agents
// (--hidden --mute --autoplay --script --shot-at ...): the same code with a hidden window, no audio device and scripted input.
using System.Diagnostics;
using System.Text;
using Tsuki.App.Services;
using Tsuki.Data;
using Tsuki.Data.Icons;
using Tsuki.Data.IO;
using Tsuki.Data.Verification;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Flow;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;
using Tsuki.Engine.Stage;
using Tsuki.Engine.Vm;
using Tsuki.Media.Audio;
using Tsuki.Media.Ffmpeg;
using Tsuki.Media.Mixer;
using Tsuki.Media.Movie;
using Tsuki.Media.Sound;
using Tsuki.Platform;
using Tsuki.Platform.Input;
using Tsuki.Platform.Timing;
using Tsuki.Render.Compositor;
using Tsuki.Render.Gl;
using Tsuki.Render.Grade;
using Tsuki.Render.Textures;
using Tsuki.Ui;

namespace Tsuki.App.Play;

/// <summary>
/// The scripts and label tables of a game folder, decoded once (the VM switches scripts at every jump). Decoding a script (MZX and the byte inversion) takes
/// 40..70 ms, so when a script is first used the scripts its <c>_JUMP</c> statements lead to are decoded on a worker (package K8): the switch then finds them ready.
/// </summary>
internal sealed class ScriptCache(GameFolderSource source) : IFlowScripts
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<ScriptImage>> _images = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<LabelTable>> _labels = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _scanned = [];

    /// <summary>Scripts decoded ahead of use by the worker.</summary>
    public int Prefetched => _prefetched;

    private int _prefetched;

    public ScriptImage Script(int k)
    {
        bool first = !_images.ContainsKey(k);
        ScriptImage image = _images.GetOrAdd(k, key => new Lazy<ScriptImage>(() => source.Script(key))).Value;
        if (first || !_scanned.ContainsKey(k))
        {
            PrefetchTargets(k);
        }

        return image;
    }

    public LabelTable Labels(int k) => _labels.GetOrAdd(k, key => new Lazy<LabelTable>(() => source.Scripts.LabelLookup(key))).Value;

    // the targets of the jumps of script k (the first argument of a J statement is a scene id): decoded on the thread pool, labels too
    private void PrefetchTargets(int k)
    {
        if (!_scanned.TryAdd(k, true))
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                IGameFlowTables flow = source.Eboot.GameFlow;
                ScriptImage image = _images[k].Value;
                ReadOnlyMemory<byte> bytes = image.Bytes;
                var targets = new SortedSet<int>();
                int pos = 0;
                while (pos < bytes.Length)
                {
                    ScriptSegment seg = ScriptSegmenter.Read(bytes.Span, pos);
                    pos = seg.End;
                    if (ScriptSegmenter.TryGetStatement(k, bytes, seg, out Statement st) && st.Name.First == (byte)'J')
                    {
                        ReadOnlySpan<byte> args = st.Args.Span;
                        int n = 0;
                        int first = args.Length > 0 && args[0] == (byte)'(' ? 1 : 0;
                        int i = first;
                        while (i < args.Length && args[i] is >= (byte)'0' and <= (byte)'9' && n < 100000)
                        {
                            n = (n * 10) + (args[i] - (byte)'0');
                            i++;
                        }

                        if (i > first && (i == args.Length || args[i] is (byte)',' or (byte)')') && flow.ScriptOfSceneId(n) is >= 0 and var to)
                        {
                            targets.Add(to);
                        }
                    }
                }

                foreach (int to in targets.Take(6))
                {
                    _images.GetOrAdd(to, key => new Lazy<ScriptImage>(() => source.Script(key))).Value.ToString();
                    _labels.GetOrAdd(to, key => new Lazy<LabelTable>(() => source.Scripts.LabelLookup(key))).Value.ToString();
                    Interlocked.Increment(ref _prefetched);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // a prefetch that fails changes nothing: the VM decodes the script itself when it needs it
            }
        });
    }
}

/// <summary>The pictures of <c>parts.mrg</c> for the boot notice.</summary>
internal sealed class DataParts(IGameData data) : IUiPictureSource
{
    public Picture? Parts(int index) => data.Parts(index);
}

/// <summary>A start-up failure with a sentence for the player; the cause keeps the technical message for the log and crash.log (<see cref="GameApp.PlayerMessage"/>).</summary>
internal sealed class StartupFailure(string message, Exception cause) : Exception(message, cause);

internal sealed class GameApp : IDisposable
{
    private const string SaveFileName = "save.dat";
    private readonly AppArgs _args;
    private readonly TextWriter _log;
    private readonly bool _hidden;
    private readonly StartupSettings _startup;
    private bool _mute;
    private GameWindow _window = null!;
    private Placement? _placement;
    private DisplaySettings? _display;
    private GlDevice _device = null!;
    private GpuCompositor _compositor = null!;
    private TextureCache _cache = null!;
    private TextureFeeder _feeder = null!;
    private DisplayListExecutor _executor = null!;
    private GameFolderSource _source = null!;
    private AudioMixer _mixer = null!;
    private AudioCommandPlayer _player = null!;
    private AudioOutput? _output;
    private MovieHost _movies = null!;
    private AppServices _services = null!;
    private AppRuntime _runtime = null!;
    private GamepadSet? _pads;
    private IInputSource _input = null!;
    private PcInput? _pcInput;
    private ScriptedInput? _scripted;
    private AutoPlayer? _auto;
    private SweepRecorder? _sweep;
    private List<int>? _tour;
    private int _tourNext;
    private long _tourStart;
    private int _tourScenes;
    private InputScript? _script;
    private string _savePath = "";
    private SaveStore _store = null!;
    private Language _lang;
    private readonly float[] _muteBuffer = new float[1600];
    private bool _previousStory;
    private bool _choiceShot;
    private bool _quit;
    private PortSettings? _port;
    private ScreenshotWriter _screenshots = null!;
    private bool _shotPending;
    private TextFeed? _feed;
    private TextCopier? _copier;
    private TextHookOutput? _hook;
    private TextWebSocketOutput? _webSocket;
    private Task<string?>? _iconTask;
    private bool _focused = true;
    private long _renderedFrame = -1;
    private Exception? _reportedFault;
    private float _peak;
    private readonly HashSet<TextureRef> _reportedMissing = [];
    private readonly HashSet<long> _shotAt = [];
    private readonly Stopwatch _wall = Stopwatch.StartNew();

    private GameApp(AppArgs args, TextWriter log)
    {
        _args = args;
        _log = log;
        _hidden = args.Hidden;
        _startup = StartupSettings.Load(args);
        _lang = AppIdentity.StartupLanguage(args, _startup.Data, null);
        _mute = args.Mute || args.Hidden;
        foreach (long f in args.ShotAt)
        {
            _shotAt.Add(f);
        }
    }

    /// <summary>The exit code of a game that found another game on the same profile (the launcher knows it; package R1).</summary>
    public const int ExitAlreadyRunning = 3;

    /// <summary>The exit code of a hidden verification run whose script voices did not reach the mixer (AppServices.VoicePlaysDropped; package R1).</summary>
    public const int ExitAudioDead = 5;

    /// <summary>Runs the game; returns the process exit code.</summary>
    public static int Run(AppArgs args)
    {
        if (args.Hidden || args.Mute)
        {
            Environment.SetEnvironmentVariable("SDL_AUDIO_DRIVER", "dummy");
        }

        string dataDir = StartupSettings.DataDirectory(args);
        // the language of the messages before the game is open: --lang, the launcher's pending line, else the Windows display language (package K20)
        Language early = AppIdentity.StartupLanguage(args, AppSettings.Load(args.NoSave ? null : Path.Combine(dataDir, AppSettings.FileName)), null);
        using Mutex? instance = SingleInstance.TryAcquire(dataDir);
        if (instance is null)
        {
            // a second game on the same profile would die on the shared log file or, at its quit, overwrite the first one's save.dat with its own copy of the slots
            Console.Error.WriteLine($"{AppIdentity.ProductName}: the game is already running on the profile {dataDir}");
            if (!args.Hidden)
            {
                MessageBox.ShowError(AppIdentity.ProductName, PortText.Get(PortString.AlreadyRunning, early));
            }

            return ExitAlreadyRunning;
        }

        // a --log file that cannot be opened (a locked or read-only folder) is no reason to die before anything is shown: the run goes on with the console and says so
        TextWriter log = Console.Out;
        string? logFailure = null;
        if (args.LogFile is { } path)
        {
            try
            {
                log = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                logFailure = $"the log file {path} could not be opened: {e.Message}";
            }
        }

        using var app = new GameApp(args, log);
        try
        {
            if (logFailure is not null)
            {
                log.WriteLine(logFailure);
                CrashLog.Append(dataDir, null, logFailure);
                if (!args.Hidden)
                {
                    MessageBox.ShowError(AppIdentity.ProductName, PortText.Format(PortString.LogNotOpened, early, args.LogFile, logFailure[(logFailure.IndexOf(": ", StringComparison.Ordinal) + 2)..]));
                }
            }

            return app.Play();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.WriteLine($"{AppIdentity.ProductName} failed: " + e);
            string? crash = CrashLog.Write(dataDir, args.LogFile, e);
            if (!args.Hidden)
            {
                // the start-up failures a player can meet are told in a sentence (review R1), in the player's language (package K20); the technical message is in the log and in crash.log
                Language lang = app._lang;
                MessageBox.ShowError(AppIdentity.ProductName, PlayerMessage(e, lang) + (crash is null ? string.Empty : Environment.NewLine + Environment.NewLine + PortText.Format(PortString.Details, lang, crash)));
            }

            return 1;
        }
    }

    /// <summary>
    /// The first line of the error box. The start-up failures a player can meet (the FFmpeg DLLs, the graphics driver, the game folder) are told in a sentence of their
    /// own, in <paramref name="language"/>; the technical message stays in the log and in crash.log. Anything else shows its own message.
    /// </summary>
    internal static string PlayerMessage(Exception e, Language language = Language.En) => e switch
    {
        StartupFailure s => s.Message,
        FfmpegMissingException f => PortText.Format(PortString.FfmpegMissing, language, string.Join(", ", f.MissingFiles)),
        _ => e.Message,
    };

    /// <summary>The sentence for a window that <see cref="GameWindow.Create"/> could not make: SDL names the call that failed, and the GL 3.3 context is the one a player's machine refuses.</summary>
    internal static string WindowMessage(PlatformException e, Language language = Language.En) => e.Message.StartsWith("SDL_GL_", StringComparison.Ordinal)
        ? PortText.Get(PortString.NoOpenGl, language)
        : PortText.Get(PortString.NoWindow, language);

    /// <summary>
    /// The sentence for a game folder that <see cref="GameFolderSource.Open(string, IAudioDecoder?, int)"/> could not open: the folder is gone, or one of its files is; a
    /// .pkg, an update alone or a folder without the game (package K25: <see cref="GameLayoutException"/>); a build whose tables could not be found
    /// (<see cref="UnsupportedBuildException"/>, which names the build and the tables and asks for the log).
    /// </summary>
    internal static string GameFolderMessage(string folder, Exception e, Language language = Language.En) => e switch
    {
        UnsupportedBuildException u => PortText.Format(PortString.BuildUnsupported, language, u.Identity.ShortName, u.Identity.EbootSha256[..16], string.Join(", ", u.MissingTables)),
        GameLayoutException { Layout.Kind: GameLayoutKind.PkgFile } => PortText.Format(PortString.FolderIsPkg, language, folder, "CUSA47999"),
        GameLayoutException { Layout.Kind: GameLayoutKind.UpdateOnly } => PortText.Format(PortString.FolderIsUpdateOnly, language, folder),
        GameLayoutException { Layout.Kind: GameLayoutKind.Several } s => PortText.Format(PortString.FolderHoldsSeveral, language, folder, string.Join(", ", s.Layout.CandidateList.Select(Path.GetFileName))),
        GameLayoutException => PortText.Format(PortString.FolderNoGameInside, language, folder) + " " + PortText.Get(PortString.ChooseFolderAgain, language),
        DirectoryNotFoundException => PortText.Format(PortString.FolderGone, language, folder) + " " + PortText.Get(PortString.ChooseFolderAgain, language),
        FileNotFoundException { FileName: { Length: > 0 } file } => PortText.Format(PortString.FolderIncomplete, language, file, folder) + " " + PortText.Get(PortString.ChooseFolderAgain, language),
        _ => PortText.Format(PortString.FolderUnreadable, language, folder, e.Message),
    };

    private void Say(string message) => _log.WriteLine($"[{_runtime?.Frame ?? 0,7}] {message}");

    private int Play()
    {
        FfmpegVersion ff = FfmpegLibrary.Load();
        _log.WriteLine($"{AppIdentity.ProductName} {AppIdentity.Version}; FFmpeg {ff.Build} from {FfmpegLibrary.RootPath}");
        if (AppIdentity.IsPersonalBuild)
        {
            _log.WriteLine("personal build: the executable carries the Arcueid icon cut from the owner's copy of the game (package K28); this build must not be shared");
        }

        // the save file is read before the game folder is looked for: its config block says which language the first-run prompt and the start-up errors use (package K20)
        OpenSaveStore();
        string title = AppIdentity.WindowTitle(null, _lang);
        // the hidden window of the verification mode is 1280 x 720 unless settings.ini names a window size (package K28: a screenshot of the hidden window then
        // shows the frame letterboxed into the size the player would see)
        (int hiddenW, int hiddenH) = _startup.Data.WindowSize ?? (1280, 720);
        var windowOptions = (_hidden
            ? new WindowOptions(title, hiddenW, hiddenH, Hidden: true, Resizable: false, SwapInterval: 0)
            : _startup.WindowOptions(title)) with { IconResource = GameWindow.ApplicationIconResource };
        if (_hidden && _startup.Data.WindowSize is { } hiddenSize)
        {
            _log.WriteLine($"hidden window: {hiddenSize.Width} x {hiddenSize.Height} (settings.ini)");
        }

        try
        {
            _window = GameWindow.Create(windowOptions);
        }
        catch (PlatformException e)
        {
            throw new StartupFailure(WindowMessage(e, _lang), e);
        }

        ApplyWindowIcon();
        if (!_hidden)
        {
            // the launcher's monitor and window size (package K17), kept inside the monitor's work area and centred (package K19); the window is shown once it is
            // placed, and --fullscreen is asked for after that, because borderless fullscreen takes the display the window is on
            Placement placed = _startup.PlaceWindow(_window);
            _placement = placed;
            (int rw, int rh) = _startup.RequestedWindowSize;
            _log.WriteLine($"window: {placed}{(placed.Clamped ? $" (settings asked for {rw} x {rh})" : string.Empty)}{(!placed.MonitorMatched && _startup.Data.Monitor is { } m ? $"; monitor {m} of settings.ini is not attached" : string.Empty)}");
            _window.Show();
            if (_args.Fullscreen)
            {
                _window.SetFullscreen(true);
            }
        }

        _device = GlDevice.Create(_window.GetProcAddress);
        _log.WriteLine($"{_window.Context}; {_device.Info}");
        PresentBlank();                 // a black, responsive window at once: the set-up below takes over a second, and nothing was presented during it (package R1)

        string? folder = ResolveGameFolder();
        if (folder is null)
        {
            _log.WriteLine("no game folder chosen");
            return 2;
        }

        while (true)
        {
            try
            {
                _source = GameFolderSource.Open(folder, new MediaAudioDecoder());
                break;
            }
            catch (UnsupportedBuildException e)
            {
                // a build the port does not know, with tables it could not find: refused with the build's name and the tables, and the folder stays remembered (package K25)
                _log.WriteLine($"game folder {folder} cannot be used: {e.Message}");
                _log.WriteLine(e.Identity.Report());
                _log.WriteLine("tables:");
                _log.Write(e.Result.Report());
                throw new StartupFailure(GameFolderMessage(folder, e, _lang), e);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or GameLayoutException)
            {
                // a remembered folder that became incomplete: forgotten, said in a sentence, and the folder dialog asks again instead of the same error at every start (package R1)
                _log.WriteLine($"game folder {folder} cannot be used: {e.Message}");
                if (_hidden)
                {
                    throw new StartupFailure(GameFolderMessage(folder, e, _lang), e);
                }

                ForgetRememberedFolder(folder);
                MessageBox.ShowError(AppIdentity.ProductName, GameFolderMessage(folder, e, _lang), _window);
                folder = AskGameFolder();
                if (folder is null)
                {
                    _log.WriteLine("no game folder chosen");
                    return 2;
                }
            }
        }

        _log.WriteLine($"game folder {folder} ({_source.Build.EbootSize} byte eboot, analysed build: {_source.Build.IsAnalysedBuild})");
        LogIdentity();
        SetWindowTitle(_lang);          // the game's own name, from the folder's param.sfo, in the start-up language; OpenGame follows the profile's language
        StartFaceIcon(folder);
        PresentBlank();
        StartPageTrimmer();
        OpenMedia();
        OpenRenderer();
        OpenGame();
        PresentBlank();
        SetUpInput();
        if (_source.Identity.Known != KnownBuilds.Verified)
        {
            // not the verified build: said once on the screen too, so a bug report knows what ran (package K25)
            _runtime.Ui.ShowToast(PortText.Format(PortString.BuildOtherVersion, _services.Language, _source.Identity.Describe()), brief: false);
        }

        if (!_args.Fast)
        {
            StartPrewarm();
        }

        return Loop();
    }

    // -- set-up --------------------------------------------------------------------------------------------------------------------

    /// <summary>What the dump is, how it is laid out and where its tables came from (package K25): the lines a bug report needs.</summary>
    private void LogIdentity()
    {
        _log.WriteLine($"layout: {_source.Layout.Describe()}");
        _log.WriteLine($"build: {_source.Identity.Describe()}");
        _log.WriteLine($"languages: {_source.Identity.DescribeLanguages()}");
        if (_source.TableLocations is { } located)
        {
            _log.WriteLine($"tables: {located.Summary()}");
            _log.Write(located.Report());
        }
        else
        {
            _log.WriteLine("tables: at the documented addresses (verified build)");
        }
    }

    /// <summary>The profile folder: <c>--save-dir</c>, else <c>%APPDATA%\Tsuki</c>. The remembered game folder lives there too (package K20: before, always in <c>%APPDATA%\Tsuki</c>, so a verification run on its own <c>--save-dir</c> still took the user's folder; the launcher's <c>--data</c> profile keeps it in the same place).</summary>
    private string ProfileFolder => StartupSettings.DataDirectory(_args);

    private string RememberedFolderFile => Path.Combine(ProfileFolder, "game_dir.txt");

    private string? ResolveGameFolder()
    {
        string remembered = RememberedFolderFile;
        string? env = Environment.GetEnvironmentVariable("TSUKI_GAME_DIR");
        foreach (string? candidate in new[] { _args.GameDir, env, File.Exists(remembered) ? File.ReadAllText(remembered).Trim() : null })
        {
            if (LocateGame(candidate) is { } found)
            {
                return found;
            }
        }

        return _hidden ? null : AskGameFolder();
    }

    /// <summary>
    /// The game folder a path points at (package K25: <see cref="GameLayoutResolver"/>): the path itself when it holds the game, the game inside it (one or two levels
    /// down), the parent when a folder inside the game was picked, the base game when the update folder was picked and the base is beside it. Null when the path holds
    /// no game that can be opened; the dialog then explains why (<see cref="GameFolderMessage"/>).
    /// </summary>
    internal static string? LocateGame(string? path)
    {
        GameLayout layout = GameLayoutResolver.Resolve(path);
        return layout.IsGame ? layout.PrimaryFolder : null;
    }

    /// <summary>A remembered folder that cannot be used is forgotten, so the next start asks instead of failing the same way.</summary>
    private void ForgetRememberedFolder(string folder)
    {
        try
        {
            string remembered = RememberedFolderFile;
            if (File.Exists(remembered) && string.Equals(File.ReadAllText(remembered).Trim(), folder, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(remembered);
                _log.WriteLine($"forgot the remembered game folder {folder}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // it stays remembered; the dialog still asks this time
        }
    }

    /// <summary>
    /// The icon of the window (package K20): the executable's own icon group through the window class when this process is Tsukihime.exe (crisp at every size), else
    /// the embedded 64 px picture through SDL_SetWindowIcon (the dev host, a test host).
    /// </summary>
    private void ApplyWindowIcon()
    {
        if (GameWindow.ExecutableHasApplicationIcon())
        {
            _log.WriteLine("window icon: the executable's icon group");
            return;
        }

        byte[]? icon = AppIdentity.EmbeddedIcon();
        if (icon is null)
        {
            _log.WriteLine("window icon: none (the embedded picture is missing)");
            return;
        }

        bool ok = _window.SetIcon(AppIdentity.IconSize, AppIdentity.IconSize, icon);
        _log.WriteLine(ok ? $"window icon: the embedded {AppIdentity.IconSize} px picture" : $"window icon: refused ({Sdl.LastError})");
    }

    /// <summary>
    /// The Arcueid icon (package K23): built from the player's own files into the profile's icons folder (or found there from an earlier run) on a worker thread, so
    /// the start does not wait for a sprite decode; <see cref="ApplyFaceIcon"/> puts it on the window from the loop once it is ready. A folder without the picture
    /// keeps the crescent.
    /// </summary>
    private void StartFaceIcon(string folder)
    {
        string dataDir = ProfileFolder;
        GameFolderSource source = _source;
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _iconLog = lines;
        _iconTask = Task.Run(() => FaceIconCache.Ensure(dataDir, folder, () => source, lines.Enqueue, disposeSource: false));
    }

    private System.Collections.Concurrent.ConcurrentQueue<string>? _iconLog;

    /// <summary>Called from the loop: when the icon task is done, its lines are logged and the icon goes on the window (SDL for the picture, the .ico file for crisp sizes on Windows).</summary>
    private void ApplyFaceIcon()
    {
        if (_iconTask is not { IsCompleted: true } task)
        {
            return;
        }

        _iconTask = null;
        while (_iconLog is { } q && q.TryDequeue(out string? line))
        {
            Say(line);
        }

        string? path = task.IsCompletedSuccessfully ? task.Result : null;
        if (task.IsFaulted)
        {
            Say($"face icon: not built ({task.Exception?.GetBaseException().Message}); the crescent stays");
        }

        if (path is null)
        {
            Say("window icon: the crescent stays (no face icon)");
            return;
        }

        IconFrame? frame = FaceIconCache.ReadFrame(path, 256);
        bool picture = frame is not null && _window.SetIcon(frame.Size, frame.Size, frame.Rgba);
        bool file = _window.SetIconFile(path);
        Say($"window icon: Arcueid from {path} (SDL {frame?.Size ?? 0} px {(picture ? "set" : "refused")}, per-size frames {(file ? "set" : "not set")})");
    }

    /// <summary>The window title in <paramref name="language"/>: the game's name from the open folder's param.sfo (package K20); logged each time, because the hidden runs check it.</summary>
    private void SetWindowTitle(Language language)
    {
        _lang = language;
        string title = AppIdentity.WindowTitle(_source, language);
        if (title != _window.Title)
        {
            _window.SetTitle(title);
        }

        Say($"window title: {title}");
    }

    // ask: the folder dialog, until the folder holds the game; the first run says what it is asking for first (package K20: localized, the dialog is captioned too)
    private string? AskGameFolder()
    {
        MessageBox.ShowError(AppIdentity.ProductName, PortText.Format(PortString.FolderPrompt, _lang, "CUSA47999"), _window);
        while (true)
        {
            FolderDialogRequest request = FolderDialog.Show(_window, null, PortText.Get(PortString.FolderDialogTitle, _lang));
            var pump = new EventPump(_window, null);
            var events = new List<PlatformEvent>();
            while (!request.IsCompleted)
            {
                events.Clear();
                pump.Pump(events);
                if (events.Any(e => e.Kind == PlatformEventKind.QuitRequested))
                {
                    return null;
                }

                (int w, int h) = _window.PixelSize;
                _device.ClearDefaultFramebuffer(w, h, 0.05f, 0.04f, 0.1f);
                _window.Swap();
                Thread.Sleep(16);
            }

            if (request.Outcome != FolderDialogOutcome.Chosen || request.Path is null)
            {
                return null;
            }

            GameLayout layout = GameLayoutResolver.Resolve(request.Path);
            if (layout.IsGame && layout.PrimaryFolder is { } found)
            {
                try
                {
                    Directory.CreateDirectory(ProfileFolder);
                    File.WriteAllText(RememberedFolderFile, found);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _log.WriteLine($"the game folder could not be remembered: {e.Message}");
                }

                return found;
            }

            // a .pkg, an update alone, several games: the specific sentence; anything else: no eboot.bin here (package K25)
            _log.WriteLine($"chosen path {request.Path}: {layout.Describe()}");
            MessageBox.ShowError(
                AppIdentity.ProductName,
                layout.Kind is GameLayoutKind.PkgFile or GameLayoutKind.UpdateOnly or GameLayoutKind.Several
                    ? GameFolderMessage(request.Path, new GameLayoutException(layout), _lang)
                    : PortText.Get(PortString.FolderNotTheGame, _lang),
                _window);
        }
    }

    /// <summary>Clears the window to black, shows it and pumps the events once: the window answers Windows and is not a white or stale rectangle while the set-up runs.</summary>
    private void PresentBlank()
    {
        if (_hidden)
        {
            return;
        }

        (int w, int h) = _window.PixelSize;
        _device.ClearDefaultFramebuffer(w, h, 0f, 0f, 0f);
        _window.Swap();
        new EventPump(_window, null).Pump(new List<PlatformEvent>());
    }

    private void OpenMedia()
    {
        _mixer = new AudioMixer();
        _player = new AudioCommandPlayer(_source, _mixer, new AudioPlayerOptions(AsyncDecode: true));
        if (!_mute)
        {
            try
            {
                _output = AudioOutput.OpenDevice(_mixer.Render);
                _output.Start();
                _log.WriteLine($"audio: {_output.Driver} \"{_output.DeviceName}\" {_output.DeviceRate} Hz, buffer {_output.DeviceBufferFrames} frames");
            }
            catch (PlatformException e)
            {
                // no sound device: the game still runs, the loop pulls the mixer so the movie clocks keep going
                _log.WriteLine("audio: no device (" + e.Message + "); playing silently");
                _output?.Dispose();
                _output = null;
                _mute = true;
            }
        }
        else
        {
            _log.WriteLine("audio: muted (the mixer is pulled by the loop and the samples are dropped; no device is opened)");
        }

        bool virtualMovies = _args.VirtualMovies || _args.Fast;
        // H.264 frame threads: half the physical cores (ProcessorCount counts the logical ones), 2..6, instead of FFmpeg's default of up to 16 per movie (package R1)
        int videoThreads = Math.Clamp(Environment.ProcessorCount / 4, 2, 6);
        _movies = new MovieHost(_source, _mixer, new MovieHostOptions(VirtualTime: virtualMovies, VideoThreads: videoThreads));
    }

    private void OpenRenderer()
    {
        _compositor = new GpuCompositor(_device.Gl, 1920, 1080);
        // the texture cache cap follows the card's dedicated memory (a third of it, 512..1536 MiB; K11 section 8, package R1): the driver holds about twice the cap for it
        (long Bytes, string Name)? adapter = VideoMemory.Dedicated(_device.Info.ToString());
        long cacheCap = VideoMemory.CacheCapacity(adapter?.Bytes);
        _log.WriteLine(adapter is { } a
            ? $"texture cache: {cacheCap >> 20} MiB (adapter \"{a.Name}\" has {a.Bytes >> 20} MiB dedicated)"
            : $"texture cache: {cacheCap >> 20} MiB (adapter memory unknown)");
        _cache = new TextureCache(_device.Gl, cacheCap, cacheCap / 6)
        {
            // the STEC / SNX colour kernel (package K7) recolours picture tiles whose key carries a grade, on the decode workers
            Grader = TileGrader.FromEboot(_source.Tables.Reader),
        };
        _feeder = new TextureFeeder(_source, _cache);
        _executor = new DisplayListExecutor(_compositor, _cache);
        _feeder.MovieFrames = slot => _movies.CurrentFrame(slot);
    }

    /// <summary>Reads save.dat of the profile (or starts a new one) and settles the start-up language on what its config block says.</summary>
    private void OpenSaveStore()
    {
        string dir = StartupSettings.DataDirectory(_args);
        Directory.CreateDirectory(dir);
        _savePath = Path.Combine(dir, SaveFileName);
        _store = LoadStore();
        _lang = AppIdentity.StartupLanguage(_args, _startup.Data, _store);
    }

    private void OpenGame()
    {
        string dir = StartupSettings.DataDirectory(_args);
        SaveStore store = _store;
        Language initial = _args.Language is { } l ? (Language)l : Language.En;
        var scripts = new ScriptCache(_source);
        var parts = new AppServicesParts
        {
            Data = _source,
            Store = store,
            Audio = _player,
            Movies = _movies,
            SavePath = _args.NoSave ? null : _savePath,
            SettingsPath = _args.NoSave ? null : Path.Combine(dir, AppSettings.FileName),
            PromptOverride = _args.Prompts,
            InitialLanguage = initial,
            HostFactory = (language, profile) => new GameHost(
                new GameServices(_source, scripts)
                {
                    SeLookup = (name, flags) => _source.SoundEffectNames.Lookup(name, flags)?.Index,
                    Layouts = new GamePictureLayouts(_source),
                },
                new GameOptions { Language = language, VirtualMovies = _args.VirtualMovies || _args.Fast },
                profile),
        };
        if (_args.Language is { } forced)
        {
            store.SystemData.Block.Config.Language = (short)forced;
        }

        // the launcher's language (once, package K17) and the window mode of this command line, which becomes the remembered one (package K18)
        bool startupApplied = _startup.ApplyToConfig(store.SystemData.Block.Config, _args.Language is not null, _args.WindowModeChoice);
        _log.WriteLine(_args.WindowModeChoice is { } choice
            ? $"window mode: {(choice ? "fullscreen" : "windowed")} (command line, remembered)"
            : $"window mode: {(ReadWindowMode(store.SystemData.Block.Config) ? "fullscreen" : "windowed")} (remembered)");

        _services = new AppServices(parts);
        if (startupApplied)
        {
            _services.Commit();         // the choice survives a crash of this run
        }

        // the window title follows the game's language: now, and at once when the config screen changes it (the host is rebuilt then; package K20)
        SetWindowTitle(_services.Language);
        _services.HostReplaced += _ => SetWindowTitle(_services.Language);

        // the config screen's Display Mode and Window Size rows (package K19) over the window; the hidden window of a verification run has them too
        _display = new DisplaySettings(new SdlDisplayWindow(_window), _services.Config, () => _services.Commit(), parts.SettingsPath, _placement is { } pl ? (pl.Width, pl.Height) : _window.WindowSize, Say);
        _services.Display = _display;

        // the port's own switches (package K21): Mute in Background acts on the mixer's master gain, Copy Text to Clipboard on the text copier; both live in settings.ini
        _port = new PortSettings(parts.SettingsPath, _startup.Data, Say);
        _port.Changed += ApplyBackgroundMute;
        _services.Port = _port;
        _screenshots = new ScreenshotWriter(_args.ScreenshotDir ?? ScreenshotWriter.DefaultFolder);

        // the text outputs for text hookers and dictionaries (packages K21 and K23) share one producer: the clipboard, the GDI hook point and the WebSocket
        IClipboardSink clipboard = _args.ClipboardFile is { } clipFile ? new FileClipboardSink(clipFile) : new SdlClipboardSink();
        _copier = new TextCopier(() => _port.CopyTextToClipboard, clipboard, Say);
        _copier.FirstCopy += () =>
        {
            // once per run, so the player sees the switch works; the copies themselves are silent (a toast per message would be in the way of reading)
            Say("clipboard: the first text of the run was copied (toast shown)");
            _runtime?.Ui.ShowToast(PortText.Get(PortString.TextCopied, _services.Language));
        };
        _hook = new TextHookOutput(() => _port.TextHookerOutput, new GdiTextHookSink(), Say);
        _webSocket = new TextWebSocketOutput(() => _port.TextWebSocket, () => _port.TextWebSocketPort, Say);
        // the WebSocket toasts carry an address or an error to read: the longer hold (package K24 made the toast brief)
        _webSocket.Listening += port => _runtime?.Ui.ShowToast(PortText.Format(PortString.TextWebSocketListening, _services.Language, port), brief: false);
        _webSocket.Failed += (port, error) => _runtime?.Ui.ShowToast(PortText.Format(PortString.TextWebSocketFailed, _services.Language, port, error), brief: false);
        _feed = new TextFeed(() => _services.Language, Say);
        _feed.Add(_copier);
        _feed.Add(_hook);
        _feed.Add(_webSocket);
        _services.HostReplaced += _ => _feed.Reset();

        _services.Logged += m => Say(m);
        _services.SaveFailed += (path, error) =>
        {
            // once per run: a save folder that cannot be written loses every save of the session otherwise (the save screen says the slot failed; this says why)
            if (!_hidden)
            {
                MessageBox.ShowError(AppIdentity.ProductName, PortText.Format(PortString.SaveFailed, _services.Language, path, error).Replace("\n", Environment.NewLine, StringComparison.Ordinal), _window);
            }
        };
        var ui = new UiRoot(_source, _services, new DataParts(_source));
        _runtime = new AppRuntime(_services, ui, _player, _movies);
        _runtime.Logged += m => Say(m);
        if (_args.ExitAfterEnds > 0)
        {
            int ended = 0;
            _services.StoryEnded += _ =>
            {
                if (++ended >= _args.ExitAfterEnds)
                {
                    Say($"exit-after-ends {_args.ExitAfterEnds} reached");
                    _quit = true;
                }
            };
        }

        _runtime.Grab = GrabThumbnail;
        _feeder.Thumbnails = _services.Thumbnail;
        _feeder.Composed = key => ui.Context.Assets.Composed(key.A, (Language)key.B);       // the title's EXIT cell (package K33)
        _runtime.Start();
        if (_args.SweepDir is not null)
        {
            StartSweep();
        }
    }

    // the route sweep (package K15): events of the host and the services feed the recorder, which is told after every drawn frame
    private void StartSweep()
    {
        _sweep = new SweepRecorder(
            _args,
            () => (_compositor.Width, _compositor.Height, _compositor.Read(_compositor.Frame)),
            k => _source.Scripts.Names[k]);
        void Hook(GameHost host)
        {
            host.SceneEntered += (k, id) =>
            {
                Say($"scene {id} {_source.Scripts.Names[k]}");
                _sweep.SceneEntered(_runtime.Frame);
            };
            host.MovieStarted += id =>
            {
                Say($"movie {id} start");
                _sweep.MovieStarted(_runtime.Frame);
            };
            host.MovieEnded += id => Say($"movie {id} end");
            host.MessageStarted += (_, _, _) => _sweep.MessageStarted();
            host.ChoiceDecided += (kind, point, value, cursor) => Say($"choice-decided {kind} point {point} value {value} line {cursor}");
        }

        Hook(_runtime.Host);
        _services.HostReplaced += Hook;
        _services.StoryEnded += reason =>
        {
            Say($"story ended {reason}");
            _sweep.StoryEnded(_runtime.Frame);
        };
    }

    /// <summary>
    /// Runs the first scene of the story on a scratch host in the background while the boot notice and the title are shown (package K8): the interpreter, the
    /// stage, the text layout, the tables behind them and the picture layouts are compiled and built there, so the first use in the real game does not stall a
    /// frame for 20..60 ms. The host has its own profile; nothing is drawn, played or saved.
    /// </summary>
    private void StartPrewarm()
    {
        Language language = _args.Language is { } l ? (Language)l : Language.En;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new GameHost(
                    new GameServices(_source, _prewarmScripts ??= new ScriptCache(_source))
                    {
                        SeLookup = (name, flags) => _source.SoundEffectNames.Lookup(name, flags)?.Index,
                        Layouts = new GamePictureLayouts(_source),
                    },
                    new GameOptions { Language = language, Sound = false, VirtualMovies = true, RandomSeed = 1 });
                host.NewGame(_source.Eboot.GameFlow.SceneId(1));
                var list = new DisplayList(8192);
                for (int i = 0; i < 1200 && host.Phase == GamePhase.Running && !_prewarmStop; i++)
                {
                    host.SetSkipLatch(true);
                    list.Reset(i);
                    host.Step(new InputFrame { Frame = i, Pressed = (i & 1) == 0 ? PadButtons.Decide : PadButtons.None }, list);
                    list.Seal();
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // a failed warm-up changes nothing: the first use in the game then pays for it
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "tsuki-prewarm",
        };
        _prewarmThread = thread;
        thread.Start();
    }

    // -- the working set (package K11) -----------------------------------------------------------------------------------------------

    private static readonly TimeSpan TrimPeriod = TimeSpan.FromSeconds(2);
    private readonly ManualResetEventSlim _trimStop = new(false);
    private Thread? _trimThread;
    private long _trimCalls;
    private long _trimTicks;
    private long _trimMaxTicks;

    /// <summary>
    /// Drops the pages of the big archive mappings from the working set every two seconds. The archives are mapped whole and a page a decode read stays in the working
    /// set until the system needs the memory: over a long play the working set grew by everything the game had ever read (3.8 GB after 200 s of a fast soak, nothing
    /// leaked). The trimmed pages stay in the system file cache; the thread runs below normal priority and a trim costs about 0.15 ms per MiB touched since the last one.
    /// </summary>
    private void StartPageTrimmer()
    {
        _source.PageTrimmer = MemoryPages.TrimResident;
        _trimThread = new Thread(() =>
        {
            while (!_trimStop.Wait(TrimPeriod))
            {
                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    _source.TrimMappedPages();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // a trim that fails stops the trimming, not the game (the loop logs it; package R1: an unhandled exception here ended the process with no crash.log)
                    _trimFault = e;
                    return;
                }

                long ticks = Stopwatch.GetTimestamp() - t0;
                Interlocked.Increment(ref _trimCalls);
                Interlocked.Add(ref _trimTicks, ticks);
                long seen;
                while (ticks > (seen = Interlocked.Read(ref _trimMaxTicks)) && Interlocked.CompareExchange(ref _trimMaxTicks, ticks, seen) != seen)
                {
                }
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "tsuki-trim",
        };
        _trimThread.Start();
    }

    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(10);
    private volatile Exception? _trimFault;
    private bool _trimFaultLogged;
    private ScriptCache? _prewarmScripts;
    private Thread? _prewarmThread;
    private volatile bool _prewarmStop;

    private SaveStore LoadStore()
    {
        SaveStore? store = null;
        if (!_args.NoSave)
        {
            try
            {
                store = SaveFile.ReadWithFallback(_savePath, out bool backup);
                if (store is not null)
                {
                    _log.WriteLine($"save file {_savePath}: {store.StoredSlots().Count()} slots{(backup ? " (from the backup)" : string.Empty)}");
                }

                if (backup)
                {
                    // the damaged file is set aside, so the next commit does not turn it into the .bak that just saved the profile
                    string broken = Quarantine();
                    _log.WriteLine($"the damaged save file was moved to {broken}");
                    Notify(PortText.Format(PortString.SaveDamagedBackupUsed, LanguageOf(store), Path.GetFileName(_savePath) + ".bak", broken));
                }
            }
            catch (SaveFormatException e)
            {
                string broken = Quarantine();
                _log.WriteLine($"save file damaged ({e.Message}); moved to {broken}, starting a new one");
                Notify(PortText.Format(PortString.SaveDamagedNewProfile, _lang, e.Message, broken));
            }
        }

        if (store is null)
        {
            store = new SaveStore();
            store.SystemData.Block.Magic = SystemBlock.MagicValue;
            store.SystemData.Config.CopyFrom(ConfigBlock.CreateBootDefaults());
            store.SystemData.Config.Language = -1;
            _log.WriteLine("no save file: a new profile");
        }

        return store;
    }

    /// <summary>Moves the damaged save file to <c>save.dat.broken-(timestamp)</c>: a name of its own, so an earlier damaged file is not overwritten (package R1).</summary>
    private string Quarantine()
    {
        string broken = $"{_savePath}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
        for (int n = 2; File.Exists(broken) || Directory.Exists(broken); n++)
        {
            broken = $"{_savePath}.broken-{DateTime.Now:yyyyMMdd-HHmmss}-{n}";
        }

        File.Move(_savePath, broken);
        return broken;
    }

    /// <summary>A notice the player sees (not in a hidden run); the log has the words too.</summary>
    private void Notify(string text)
    {
        if (!_hidden)
        {
            MessageBox.ShowError(AppIdentity.ProductName, text.Replace("\n", Environment.NewLine, StringComparison.Ordinal), _window);
        }
    }

    /// <summary>The language a save store's config block names, else the start-up language (the notice about a damaged file speaks the language of the profile it rescued).</summary>
    private Language LanguageOf(SaveStore? store) => AppIdentity.StartupLanguage(_args, _startup.Data, store);

    private void SetUpInput()
    {
        if (_args.Script is not null)
        {
            _script = InputScript.Parse(_args.Script);
        }

        if (_args.SweepTour is { } tourFile)
        {
            _tour = File.ReadAllLines(tourFile).Select(l => l.Trim()).Where(l => l.Length > 0 && char.IsDigit(l[0])).Select(l => int.Parse(l.Split(' ', '	')[0], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            void HookTour(GameHost host) => host.SceneEntered += (_, _) => _tourScenes++;
            HookTour(_runtime.Host);
            _services.HostReplaced += HookTour;
        }

        if (_args.Autoplay is { } mode)
        {
            _auto = new AutoPlayer(mode, ChoicePolicy.Parse(_args.Choices)) { HoldChoice = _args.ShotOnChoice };
            _auto.Chose += (point, line, count) => Say($"choice {point}: line {line} of {count}");
        }

        if (_script is not null || _auto is not null)
        {
            _scripted = new ScriptedInput(_script, _auto) { Runtime = _runtime };
            _input = _scripted;
        }
        else
        {
            // the pads come after the first frame (Loop): SDL's gamepad subsystem takes about 1.1 s to initialise (the WGI, RawInput and DirectInput back ends), which
            // used to sit between the window's creation and the first picture (package R1); the keyboard and the mouse work from the first step
            _pcInput = new PcInput(_window, null);
            _input = _pcInput;
            _padsPending = true;
        }

        // the window mode of the last run
        if (!_hidden && ReadWindowMode(_services.Config))
        {
            _window.SetFullscreen(true);
        }
    }

    private static bool ReadWindowMode(ConfigBlock config) => config.Bytes[WindowModeOffset] == 1;

    private const int WindowModeOffset = StartupSettings.WindowModeOffset;

    // -- the loop ------------------------------------------------------------------------------------------------------------------

    private int Loop()
    {
        var list = new DisplayList(4096);
        var events = new List<PlatformEvent>();
        var pump = new EventPump(_window, _pads);
        if (_args.MemLogSeconds > 0)
        {
            _alloc = new AllocationSampler();
        }

        _holdMode = !_args.Fast && _shotAt.Count == 0 && !_args.ShotOnChoice && _args.SweepDir is null && (_script is null || !_script.Entries.Any(e => e.Verb == "shot"));
        long start = Stopwatch.GetTimestamp();
        var clock = new FixedStepClock(Stopwatch.Frequency, start);
        bool running = true;
        long frame = 0;
        long limit = _args.Seconds > 0 ? start + (long)(_args.Seconds * Stopwatch.Frequency) : long.MaxValue;
        string stop = "";
        while (running)
        {
            events.Clear();
            pump.Pump(events);
            foreach (PlatformEvent e in events)
            {
                switch (e.Kind)
                {
                    case PlatformEventKind.QuitRequested:
                        // the close button and Alt+F4 ask the game's own quit question first (package K21); a second request while it is up quits at once
                        if (_runtime.RequestClose())
                        {
                            Say("close requested again: quitting");
                        }
                        else
                        {
                            Say("close requested: asking the quit question");
                        }

                        break;
                    case PlatformEventKind.ScreenshotRequested:
                        RequestScreenshot(e.Key ?? "F12");
                        break;
                    case PlatformEventKind.FocusLost:
                        OnFocus(false);
                        break;
                    case PlatformEventKind.FocusGained:
                        OnFocus(true);
                        break;
                    case PlatformEventKind.MouseWheel:
                        _pcInput?.AddWheel(e.Wheel);
                        break;
                    case PlatformEventKind.FullscreenToggled:
                        // Alt+Enter / F11: the pump switched the window; the state is remembered the way the config screen's row remembers it (package K19)
                        _display?.NoteFullscreen(e.Fullscreen);
                        break;
                    case PlatformEventKind.PixelSizeChanged:
                        _display?.NoteResized();
                        break;
                    case PlatformEventKind.GamepadAdded:
                        Say($"gamepad added: {e.Gamepad!.Name} ({e.Gamepad.Type})");
                        break;
                    case PlatformEventKind.GamepadRemoved:
                        Say($"gamepad removed: {e.Gamepad!.Name}");
                        break;
                }
            }

            ApplyFaceIcon();
            bool rendered = false;
            if (_args.Fast)
            {
                frame++;
                StepOnce(new LogicTick(frame, Stopwatch.GetTimestamp()), list);
                running = Render(list, ref stop);
                rendered = true;
            }
            else
            {
                long now = Stopwatch.GetTimestamp();
                while (running && !_held && clock.TryNextTick(now, out LogicTick tick))
                {
                    frame = tick.Frame;
                    StepOnce(tick, list);
                    if (_hidden)
                    {
                        running = Render(list, ref stop);
                        rendered = true;
                    }
                }
            }

            if (running && !rendered)
            {
                running = Render(list, ref stop);
            }

            if (_renderCount == 1 && !_firstFrameLogged)
            {
                _firstFrameLogged = true;
                Say($"first frame presented at {_wall.ElapsedMilliseconds} ms");
            }

            if (running && _padsPending)
            {
                // behind the first frame, under the boot notice; the step clock resyncs after the stall (FixedStepClock maxLagSteps), so no burst of steps follows
                _padsPending = false;
                long tp = Stopwatch.GetTimestamp();
                _pads = GamepadSet.Create();
                pump.Gamepads = _pads;
                _pcInput!.Pads = _pads;
                Say($"gamepads: {_pads.Connected.Count} connected, subsystem ready in {Stopwatch.GetElapsedTime(tp).TotalMilliseconds:F0} ms");
            }

            if (_args.MemLogSeconds > 0 && _wall.Elapsed.TotalSeconds >= _nextMemLog)
            {
                _nextMemLog = _wall.Elapsed.TotalSeconds + _args.MemLogSeconds;
                LogMemory();
            }

            while (_screenshots.TryTakeResult(out ScreenshotResult shot))
            {
                // the thread pool wrote (or failed) a screenshot: the log, and the short toast in the player's language unless Config > System > Screenshot Notice is Off (package K24)
                bool notice = _port?.ScreenshotNotice ?? true;
                if (shot.Saved)
                {
                    Say($"screenshot saved: {shot.Path} ({shot.Width} x {shot.Height}){(notice ? "" : " (notice off)")}");
                }
                else
                {
                    Say($"screenshot failed: {shot.Path}: {shot.Error}{(notice ? "" : " (notice off)")}");
                }

                if (notice)
                {
                    _runtime.Ui.ShowToast(PortText.Get(shot.Saved ? PortString.ScreenshotSaved : PortString.ScreenshotFailed, _services.Language));
                }
            }

            if (running && _runtime.ExitRequested)
            {
                running = false;
                stop = _runtime.CloseConfirmed ? "quit requested (the window's close, confirmed)" : "quit requested (EXIT on the title)";
            }

            if (running && _args.ExitAfter > 0 && _runtime.Frame >= _args.ExitAfter)
            {
                running = false;
                stop = $"exit-after {_args.ExitAfter}";
            }

            if (running && Stopwatch.GetTimestamp() >= limit)
            {
                running = false;
                stop = "time limit";
            }

            if (_trimFault is { } trimFault && !_trimFaultLogged)
            {
                _trimFaultLogged = true;
                Say($"page trimming stopped: {trimFault.GetType().Name}: {trimFault.Message}");
            }

            if (running && _held)
            {
                // waiting for decoded tiles: the next pass uploads the next slice; a millisecond's wait instead of a busy spin, which was a full core for the hold with vsync off (package R1)
                Pacing.WaitUntil(Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 1000));
            }
            else if (running && !_args.Fast && !_window.VsyncPaced)
            {
                Pacing.WaitUntil(clock.NextDueTimestamp);
            }
        }

        _display?.SaveWindowSize();             // a size the player dragged the window to is the next start's (package K19)
        _services.Commit();
        if (!_screenshots.WaitAll(ShutdownWait))
        {
            _log.WriteLine($"shutdown: {_screenshots.Pending} screenshot(s) still being written");
        }

        Summary(stop);
        _sweep?.Dispose();
        if (_hidden && _services.VoicePlaysDropped > 0)
        {
            // the self-check of the verification mode: a run whose voices never reached the mixer played silently and skipped every voice wait (package R1)
            _log.WriteLine($"verification: audio dead ({_services.VoicePlaysDropped} of {_services.VoicePlaysPerformed} voice plays dropped): exit code {ExitAudioDead}");
            return ExitAudioDead;
        }

        return 0;
    }

    private readonly List<TextureRef> _lookAhead = [];
    private double _nextMemLog;
    private AllocationSampler? _alloc;
    private bool _padsPending;
    private bool _firstFrameLogged;

    /// <summary>F12 (package K21; Print Screen is Windows' own since K24): the next fresh frame is read back after it is presented; the toast stays hidden until then, so it is never in the picture.</summary>
    private void RequestScreenshot(string key)
    {
        if (_shotPending)
        {
            return;
        }

        _shotPending = true;
        _runtime.Ui.HideToast = true;
        Say($"screenshot requested ({key})");
    }

    /// <summary>The window lost or regained the keyboard focus (package K21): with Mute in Background on, the mixer's master gain fades out and back in.</summary>
    private void OnFocus(bool focused)
    {
        _focused = focused;
        ApplyBackgroundMute();
    }

    private void ApplyBackgroundMute()
    {
        if (_mixer is null || _port is null)
        {
            return;
        }

        float target = _port.MuteInBackground && !_focused ? 0f : 1f;
        if (_mixer.MasterGainTarget != target)
        {
            _mixer.MasterGainTarget = target;
            Say(target == 0f ? "audio: fading out (the window is in the background)" : "audio: fading in (the window has the focus)");
        }
    }

    /// <summary>One <c>--mem-log</c> line pair (package K11): the process and managed figures, then the caches of the engine.</summary>
    private void LogMemory()
    {
        TextureCacheStats cs = _cache.Stats;
        StagingPool pool = _feeder.Pool;
        _alloc ??= new AllocationSampler();
        Say($"t={_wall.Elapsed.TotalSeconds:F0}s {MemoryProbe.Line()}");
        Say(_alloc.Take(8));
        Say($"  caches: textures {cs.Resident} ({cs.ResidentBytes >> 20} MiB) spare {cs.SpareBytes >> 20} MiB, allocs {cs.Allocations} recycles {cs.Recycles} evictions {cs.Evictions}; " +
            $"staging outstanding {pool.OutstandingBytes >> 20} MiB peak total {pool.PeakTotalBytes >> 20} MiB allocs {pool.Allocations}; feeder pending {_feeder.Pending}; {_feeder.DescribeQueues()}; " +
            $"page trims {Interlocked.Read(ref _trimCalls)} ({Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _trimTicks)).TotalMilliseconds:F0} ms in all, longest {Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _trimMaxTicks)).TotalMilliseconds:F1} ms)");
    }

    // the frame of the last step names textures that are not resident yet: the loop shows the previous picture and does not step until they are (paced mode;
    // the verification runs that take screenshots, and --fast, wait for the textures instead so that every picture is complete)
    private bool _held;
    private long _holdStart;
    private long _lastHeldPresent;
    private long _holds;
    private double _holdMsTotal;
    private double _holdMsMax;
    private bool _holdMode;

    private string PictureLabel(int archive, int entry) => $"a{archive}#{entry}";
    private StepTimes _lastStepTimes;
    private int _audioFailuresLogged;
    private double _frameStepMs;
    private double _frameFeedMs;
    private long _frameTiles;
    private double _frameExecMs;
    private double _framePresentMs;
    private long _hitches33;
    private long _hitches50;
    private long _frames;
    private double _frameMsMax;
    private double _stepMsTotal;
    private double _stepMsMax;
    private double _renderMsTotal;
    private double _renderMsMax;
    private long _renderCount;
    private long _slowFrames;

    private void StepOnce(LogicTick tick, DisplayList list)
    {
        long t0 = Stopwatch.GetTimestamp();
        StepCore(tick, list);
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _stepMsTotal += ms;
        _stepMsMax = Math.Max(_stepMsMax, ms);
        _frameStepMs += ms;
        if (ms > 40)
        {
            StepTimes t = _lastStepTimes;
            Say($"slow step {ms:F0} ms (script {_runtime.Host.Script}, ui {_runtime.Ui.Mode}, phase {_runtime.Host.Phase}; movies {t.Movies:F0} ui {t.Ui:F0} story {t.Story:F0} media {t.Media:F0} rest {t.Rest:F0} uidraw {t.Draw:F0})");
        }
    }

    private void StepCore(LogicTick tick, DisplayList list)
    {
        InputFrame input = _input.Sample(_runtime.Frame + 1);
        _services.ObserveDevice(_input.LastDevice);
        if (_mute)
        {
            // 48000 / 60 frames of the mixer per step: the movie audio clock and the streams run, nothing is played
            _mixer.Render(_muteBuffer);
            foreach (float v in _muteBuffer)
            {
                _peak = MathF.Max(_peak, MathF.Abs(v));
            }
        }

        if (_script is not null && _script.VerbsAt(_runtime.Frame + 1, "screenshot").Any())
        {
            // the screenshot key of a script, before the step draws this frame (as the key's event comes before it): the toast is left out of the frame that is captured
            RequestScreenshot("script");
        }

        _runtime.Step(input, tick.NominalTimestamp, list);
        _lastStepTimes = _runtime.LastTimes;
        _feed?.Step(_runtime.Host);
        for (; _audioFailuresLogged < _player.Failures.Count; _audioFailuresLogged++)
        {
            // a clip that could not be decoded is skipped (the voice stays silent) and the game goes on; the log names it (package K10)
            AudioPlayFailure f = _player.Failures[_audioFailuresLogged];
            Say($"audio failure {f.Bank}#{f.Entry}: {f.Error.GetType().Name}: {f.Error.Message}");
        }

        if (_hidden || _args.LogFile is not null)
        {
            _runtime.Host.SlowStatement ??= (name, script, offset, ms) => Say($"slow statement _{name} {ms:F0} ms (script {script}, offset {offset})");
        }

        if (_services.StoryActive && _runtime.Host.Phase == GamePhase.Running)
        {
            // the pictures of the next statements go to the decode workers before the stage commits them (package K8)
            _lookAhead.Clear();
            _runtime.Host.LookAhead(_lookAhead);
            if (_lookAhead.Count > 0)
            {
                _feeder.Prefetch(_lookAhead);
            }
        }

        if (_tour is not null)
        {
            StepTour();
        }

        if (_script is not null)
        {
            if (_script.LogAt(_runtime.Frame))
            {
                LogState();
            }

            if (_script.QuitAt(_runtime.Frame))
            {
                _quit = true;
            }

            foreach (ScriptEntry e in _script.VerbsAt(_runtime.Frame, "plate"))
            {
                // fills the name plate by hand (retail never does, textbox.md 3.3): the verification of the plate's draw path
                _runtime.Host.SetNamePlate(Encoding.UTF8.GetBytes(e.Argument ?? string.Empty));
                Say($"name plate set to '{e.Argument}'");
            }

            foreach (ScriptEntry e in _script.VerbsAt(_runtime.Frame, "scene"))
            {
                // starts a story scene by its scene id (package K15: the staff roll 4000, the debug menus, a chapter), as the title's New Game does
                _runtime.Ui.Clear();
                _services.StartScene(int.Parse(e.Argument!, System.Globalization.CultureInfo.InvariantCulture));
                Say($"scene {e.Argument} started by the script");
            }

            foreach (ScriptEntry e in _script.VerbsAt(_runtime.Frame, "jumpnode"))
            {
                // the flowchart's jump to a node (package K15): the playthrough is wound back to the node and its scene starts, as the chart's Jump does
                int node = int.Parse(e.Argument!, System.Globalization.CultureInfo.InvariantCulture);
                FlowNodeRow? row = _source.Eboot.GameFlow.Nodes.FirstOrDefault(r => r.Scene == node && r.ScriptScene >= 0);
                _runtime.Ui.Clear();
                bool ok = row is not null && _services.FlowchartJump(row.Column, node, row.ScriptScene);
                Say($"flowchart jump to node {node}: {(ok ? "started" : "refused")}");
            }

            foreach (ScriptEntry e in _script.VerbsAt(_runtime.Frame, "replay"))
            {
                // a gallery replay of a scene through ARCHIVE (package K15): the ending list's Play
                int scene = int.Parse(e.Argument!, System.Globalization.CultureInfo.InvariantCulture);
                _runtime.Ui.Clear();
                _services.ReplayScene(scene, 0);
                Say($"gallery replay of scene {scene}");
            }

            foreach (ScriptEntry e in _script.VerbsAt(_runtime.Frame, "lang"))
            {
                // the config screen's language radio, from a script: the same ConfigChanged call
                _services.Config.Language = e.Argument?.ToLowerInvariant() switch { "ja" => 0, "zc" => 2, "zt" => 3, _ => 1 };
                _services.ConfigChanged(Tsuki.Ui.ConfigChange.Language);
                Say($"language set to {e.Argument}");
            }

            // package K21: the window's close request (through the real event pump, like the title bar's button) and the focus; the screenshot verb ran before the step
            if (_script.VerbsAt(_runtime.Frame, "close").Any())
            {
                EventPump.PushCloseRequest(_window);
            }

            if (_script.VerbsAt(_runtime.Frame, "blur").Any())
            {
                OnFocus(false);
            }

            if (_script.VerbsAt(_runtime.Frame, "focus").Any())
            {
                OnFocus(true);
            }
        }
    }

    // the node tour (package K15): jump to a node, let its scene play, go on to the next node when another scene has started or the cap has passed
    private void StepTour()
    {
        long f = _runtime.Frame;
        bool due = _tourNext == 0 || f - _tourStart >= _args.TourCap || (!_args.TourUntilEnd && _tourScenes > 0 && f - _tourStart > 30) || (_tourNext > 0 && !_services.StoryActive && f - _tourStart > 120);
        if (!due || f < 40)
        {
            return;
        }

        while (_tourNext < _tour!.Count)
        {
            int node = _tour[_tourNext++];
            FlowNodeRow? row = _source.Eboot.GameFlow.Nodes.FirstOrDefault(r => r.Scene == node && r.ScriptScene >= 0);
            _runtime.Ui.Clear();
            if (row is not null && _services.FlowchartJump(row.Column, node, row.ScriptScene))
            {
                Say($"tour: node {node} (scene {row.ScriptScene}) started");
                _tourStart = f;
                _tourScenes = 0;
                return;
            }

            Say($"tour: node {node} refused");
        }

        Say("tour: done");
        _quit = true;
    }

    private bool Render(DisplayList list, ref string stop)
    {
        long t0 = Stopwatch.GetTimestamp();
        bool again = RenderCore(list, ref stop);
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _renderMsTotal += ms;
        _renderMsMax = Math.Max(_renderMsMax, ms);
        _renderCount++;
        if (ms > 16.7)
        {
            _slowFrames++;
        }

        // a frame is the steps that ran since the last draw plus the draw; the first second (title movie open, first textures) is not counted
        double frameMs = _frameStepMs + ms;
        _frames++;
        _frameMsMax = Math.Max(_frameMsMax, frameMs);
        if (frameMs > 33.4 && _wall.Elapsed.TotalSeconds > 1.0)
        {
            _hitches33++;
            if (frameMs > 50)
            {
                _hitches50++;
            }

            StepTimes t = _lastStepTimes;
            string fresh = string.Join(" ", _feeder.LastFresh.Where(k => k.Kind == TextureKind.PictureTile).GroupBy(k => (k.A, k.B, k.D)).Take(4).Select(g => $"{PictureLabel(g.Key.A, g.Key.B)}{(g.Key.D != 0 ? $"/grade{g.Key.D}" : string.Empty)}x{g.Count()}"));
            Say($"hitch {frameMs:F0} ms: steps {_frameStepMs:F0} (last: movies {t.Movies:F0} ui {t.Ui:F0} story {t.Story:F0} media {t.Media:F0} rest {t.Rest:F0} uidraw {t.Draw:F0}), feed {_frameFeedMs:F0} ({_frameTiles} tiles), execute {_frameExecMs:F0}, present {_framePresentMs:F0}; new tiles [{fresh}]; script {_runtime.Host.Script}, ui {_runtime.Ui.Mode}");
        }

        _frameStepMs = 0;
        return again;
    }

    private bool RenderCore(DisplayList list, ref string stop)
    {
        if (_renderedFrame == _runtime.Frame && !_held)
        {
            // no new step since the last draw (the monitor runs faster than 60 Hz): show the frame again; re-running a list would capture its own
            // output as the "previous frame" of a transition
            (int w, int h) = _window.PixelSize;
            _compositor.Present(w, h);
            _window.Swap();
            return !_quit;
        }

        _renderedFrame = _runtime.Frame;
        long tf0 = Stopwatch.GetTimestamp();
        long requested0 = _feeder.Stats.Requested;
        int missing = _feeder.EnsureResident(list, _holdMode ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_hidden ? 4000 : 300));
        if (_holdMode && missing > 0)
        {
            if (!_held)
            {
                _held = true;
                _holdStart = Stopwatch.GetTimestamp();
            }

            // the previous picture stays on the screen (the frame target still holds it); poll for the tiles on the next pass of the loop, and present it again at most
            // once per 60 Hz step (every pass re-presented it before, as fast as the driver allowed with vsync off; package R1)
            long nowHeld = Stopwatch.GetTimestamp();
            if (nowHeld - _lastHeldPresent >= Stopwatch.Frequency / 60)
            {
                _lastHeldPresent = nowHeld;
                (int hw, int hh) = _window.PixelSize;
                _compositor.Present(hw, hh);
                _window.Swap();
            }

            return !_quit;
        }

        if (_held)
        {
            _held = false;
            double heldMs = Stopwatch.GetElapsedTime(_holdStart).TotalMilliseconds;
            _holds++;
            _holdMsTotal += heldMs;
            _holdMsMax = Math.Max(_holdMsMax, heldMs);
            if (heldMs >= 50)
            {
                Say($"held {heldMs:F0} ms for textures (script {_runtime.Host.Script}, {_feeder.Pending} tiles in flight)");
            }
        }

        _frameFeedMs = Stopwatch.GetElapsedTime(tf0).TotalMilliseconds;
        _frameTiles = _feeder.Stats.Requested - requested0;
        if (_feeder.Fault is { } fault && !ReferenceEquals(fault, _reportedFault))
        {
            _reportedFault = fault;
            Say("texture decode failed: " + fault.Message);
        }

        ApplyRuleParameters();
        if (_dumpFrame > 0 && _runtime.Frame == _dumpFrame)
        {
            DumpList(list);
        }

        bool story = _services.StoryActive && _runtime.Host.Phase == GamePhase.Running;
        if (!story || !_previousStory)
        {
            _compositor.Clear(0, 0, 0, 0, 255);
        }

        _previousStory = story;
        long te0 = Stopwatch.GetTimestamp();
        ExecuteStats stats = _executor.Execute(list);
        long te1 = Stopwatch.GetTimestamp();
        (int pw, int ph) = _window.PixelSize;
        _compositor.Present(pw, ph);
        if (_shotPending)
        {
            // the screenshot key (package K21): this fresh frame was drawn without the toast; the back buffer holds it letterboxed at the window's size
            _shotPending = false;
            _runtime.Ui.HideToast = false;
            string path = _screenshots.Save(pw, ph, _compositor.ReadWindow(pw, ph));
            Say($"screenshot: writing {path} ({pw} x {ph})");
        }

        _window.Swap();
        _frameExecMs = (te1 - te0) * 1000.0 / Stopwatch.Frequency;
        _framePresentMs = Stopwatch.GetElapsedTime(te1).TotalMilliseconds;

        long f = _runtime.Frame;
        if (_shotAt.Remove(f))
        {
            Shot($"frame_{f:D6}");
        }

        if (_script is not null)
        {
            foreach (ScriptEntry e in _script.VerbsAt(f, "shot"))
            {
                Shot(e.Argument ?? $"frame_{f:D6}");
            }
        }

        if (_args.ShotOnChoice && !_choiceShot && _auto is { ChoiceSeen: true })
        {
            _choiceShot = true;
            Shot("first_choice");
            LogState();
            _auto.HoldChoice = false;
            if (_args.ExitAfter == 0)
            {
                stop = "first choice";
                return false;
            }
        }

        _sweep?.AfterDraw(f, _runtime.Host, story, _runtime.Ui.Active ? _runtime.Ui.Mode.ToString() : "story", stats.MissingTextures);
        if (stats.MissingTextures > 0)
        {
            TextureCacheStats cacheNow = _cache.Stats;
            Say($"{stats.MissingTextures} textures of this frame are not resident (cache {cacheNow.Resident} textures, {cacheNow.ResidentBytes >> 20} MiB of {_cache.CapacityBytes >> 20}, {cacheNow.Evictions} evictions)");
            foreach (ref readonly DrawCmd c in list.Commands)
            {
                if (c.Kind == DrawKind.TexturedQuad && c.Texture.Kind != TextureKind.Capture && !_cache.Contains(c.Texture) && _reportedMissing.Add(c.Texture))
                {
                    Say($"texture not resident: {c.Texture}");
                }
            }
        }

        if (_quit)
        {
            stop = "script quit";
            return false;
        }

        return true;
    }

    private void ApplyRuleParameters()
    {
        StageRuntime stage = _runtime.Host.Stage;
        if (stage is { TransitionActive: true, TransitionKind: 2 } && stage.RuleSlot > 0)
        {
            var rule = TextureRef.ForPictureTile(4, 85 + stage.RuleSlot, 0);
            _cache.SetTransitionParams(rule, TransitionParams.ForRule(stage.TransitionProgress));
        }
    }

    private readonly long _dumpFrame = long.TryParse(Environment.GetEnvironmentVariable("TSUKI_DUMP_FRAME"), out long d) ? d : 0;

    private void DumpList(DisplayList list)
    {
        Say($"display list of this frame: {list.Count} commands");
        int i = 0;
        foreach (ref readonly DrawCmd c in list.Commands)
        {
            string tex = c.Texture.Kind == TextureKind.None ? "-" : $"{c.Texture.Kind}({c.Texture.A},{c.Texture.B},{c.Texture.C},{c.Texture.D}){(_cache.Contains(c.Texture) || c.Texture.Kind == TextureKind.Capture ? string.Empty : " MISSING")}";
            Say($"  #{i++} {c.Kind} target {c.Target} {tex} word {c.EffectWord} blend {c.BlendState} v0 ({c.V0.X:F0},{c.V0.Y:F0}) v3 ({c.V3.X:F0},{c.V3.Y:F0}) uv ({c.V0.U:F2},{c.V0.V:F2})-({c.V3.U:F2},{c.V3.V:F2}) rgba {c.V0.Rgba:X8}{(c.Clip is { } cl ? $" clip {cl}" : string.Empty)}");
        }
    }

    private (int Width, int Height, byte[] Rgba)? GrabThumbnail()
    {
        byte[] full = _compositor.Read(_compositor.Frame);
        return (ThumbnailCodec.Width, ThumbnailCodec.Height, ThumbnailCodec.Downscale(full, _compositor.Width, _compositor.Height));
    }

    private void Shot(string name)
    {
        string dir = _args.ShotDir ?? Path.Combine(Directory.GetCurrentDirectory(), "shots");
        byte[] pixels = _compositor.Read(_compositor.Frame);
        for (int a = 3; a < pixels.Length; a += 4)
        {
            pixels[a] = 255;
        }

        string file = Path.Combine(dir, name + ".png");
        PngWriter.Write(file, _compositor.Width, _compositor.Height, pixels);
        Say($"screenshot {file}");
    }

    private void LogState()
    {
        GameHost h = _runtime.Host;
        string text = h.CurrentMessage is { } m ? Encoding.UTF8.GetString(m.Text).Replace('\n', '|') : "-";
        if (text.Length > 80)
        {
            text = text[..80];
        }

        Say($"state: ui {_runtime.Ui.Mode} [{string.Join(">", _runtime.Ui.Stack.Select(x => x.GetType().Name))}], story {_services.StoryActive}, phase {h.Phase}, " +
            $"script {h.Script} (scene {(h.Phase == GamePhase.Running ? h.SceneId : -1)}) cursor {h.Machine.Cursor}, F823 {h.F(823)}, F22 {h.F(22)}, auto {h.AutoMode} skip {h.SkipLatch}, " +
            $"cfg [lang {_services.Config.Language} bgm {_services.Config.MusicVolume} se {_services.Config.SfxVolume} sys {_services.Config.SystemSeVolume} voice {_services.Config.PlayAllVoices} unseen {_services.Config.UnseenTextSpeed} seen {_services.Config.SeenTextSpeed} auto {_services.Config.AutoModeWait} ffRead {_services.Config.FastForwardReadOnly} endInstant {_services.Config.PageEndInstantUnseen}/{_services.Config.PageEndInstantSeen} anim {_services.Config.TextAnimations}/{_services.Config.SeenTextAnimations} win {_services.Config.Bytes[WindowModeOffset]}], " +
            $"history {(h.History is { } hist ? hist.Count : 0)}, restores {h.RestoreCount}, page {h.PageMessages.Count} [{PageSummary(h)}], window {h.Wait.WindowAlpha:F2}, text fade {h.TextFade:F2} (step {h.Wait.TextFadeStep:F4}), F1103 {h.F(1103)}, G10 {_services.Global(10)}, choice {(h.ChoiceOpen ? h.ChoiceKind.ToString() : "-")}, plays {_player.PlaysPosted}, voice {h.Sound.LastVoice}, movies {_movies.OpenSlots}, " +
            $"toast {(_runtime.Ui.Toast is { } toast ? $"\"{toast.Text}\" a {toast.Alpha:F2}" : "-")}, master gain {_mixer.MasterGain:F2} -> {_mixer.MasterGainTarget:F0}, copies {_copier?.Copies ?? 0}, hooked {_hook?.Emitted ?? 0}, websocket {_webSocket?.State ?? "-"} sent {_webSocket?.Sent ?? 0}, opacity {h.Config.Hud10}, text \"{text}\"");
    }

    private static string PageSummary(GameHost h)
    {
        var parts = new List<string>();
        foreach (PageMessage pm in h.PageMessages)
        {
            string t = Encoding.UTF8.GetString(pm.Text).Replace((char)10, '|');
            parts.Add(pm.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + (t.Length > 18 ? t[..18] : t));
        }

        return string.Join(" / ", parts);
    }

    private void Summary(string stop)
    {
        FeederStats fs = _feeder.Stats;
        TextureCacheStats cs = _cache.Stats;
        _log.WriteLine(
            $"done: {_runtime.Frame} steps in {_wall.Elapsed.TotalSeconds:F1} s ({stop}); textures {cs.Resident} resident ({cs.ResidentBytes >> 20} MiB), " +
            $"{fs.Requested} tiles decoded ({fs.Prefetched} by the look-ahead), {fs.Waits} frames waited {fs.WaitMilliseconds:F0} ms, {fs.TimedOut} timeouts, {fs.MovieUploads} movie frames; " +
            $"mixer peak {_peak:F3} (muted run: pulled and dropped), audio failures {_player.Failures.Count}, voice plays {_services.VoicePlaysPerformed} ({_services.VoicePlaysDropped} dropped), movie failures {_movies.Failures.Count}, restores {_runtime.Host.RestoreCount}, unknown commands {_runtime.Host.UnknownCommands.Count}");
        _log.WriteLine(
            $"timing: step {_stepMsTotal / Math.Max(1, _runtime.Frame):F2} ms mean, {_stepMsMax:F1} ms max; draw+present {_renderMsTotal / Math.Max(1, _renderCount):F2} ms mean, {_renderMsMax:F1} ms max, " +
            $"{_slowFrames} of {_renderCount} draws over 16.7 ms");
        _log.WriteLine($"frames: {_frames}, longest {_frameMsMax:F1} ms, {_hitches33} over 33 ms and {_hitches50} over 50 ms after the first second");
        if (_holdMode)
        {
            _log.WriteLine($"texture holds: {_holds} times the picture stayed on the screen until its tiles were decoded: {_holdMsTotal:F0} ms in all, longest {_holdMsMax:F0} ms");
        }
        foreach (AudioPlayFailure f in _player.Failures.Take(5))
        {
            _log.WriteLine($"  audio failure {f.Bank} #{f.Entry}: {f.Error.Message}");
        }

        foreach (MovieFailure f in _movies.Failures.Take(5))
        {
            _log.WriteLine($"  movie failure slot {f.Slot} movie {f.MovieIndex}: {f.Error.Message}");
        }
    }

    /// <summary>
    /// Waits for <paramref name="task"/> like <see cref="Task.Wait(TimeSpan)"/>, but a task that faulted counts as finished instead of throwing (package K22): the icon
    /// build is only waited for so that the archives are not unmapped under it, and a run that ends before the loop consumed a faulted build (a start-up failure after
    /// <see cref="StartFaceIcon"/>) must not replace the real exception with the build's. True when the task is no longer running.
    /// </summary>
    internal static bool WaitQuietly(Task task, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            return task.Wait(timeout);
        }
        catch (AggregateException)
        {
            return true;                            // faulted or cancelled: it holds no mapping any more
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Whatever reads the archive mappings stops before the source unmaps them (package K10): a clip decode on the thread pool or the warm-up thread that
        // touches an unmapped page is an access violation, and .NET ends the process on it (event 1026, "unhandled exception", exit 139 under bash).
        // Quitting while a sound effect or a voice was still decoding crashed the game this way.
        _prewarmStop = true;
        _trimStop.Set();
        _webSocket?.Dispose();
        bool trimDone = _trimThread?.Join(ShutdownWait) ?? true;
        bool prewarmDone = _prewarmThread?.Join(ShutdownWait) ?? true;
        // the icon build reads the archive mappings too (package K23); a run that ends before it is done waits for it like the other readers
        bool iconDone = _iconTask is not { } icon || WaitQuietly(icon, ShutdownWait);
        _feeder?.Dispose();
        _movies?.Dispose();
        _output?.Dispose();
        _pads?.Dispose();
        bool audioDone = _player?.Shutdown(ShutdownWait) ?? true;
        _cache?.Dispose();
        _compositor?.Dispose();
        if (trimDone)
        {
            _trimStop.Dispose();
        }

        if (prewarmDone && audioDone && trimDone && iconDone)
        {
            _source?.Dispose();                     // also waits for any decode that still holds a lease on a mapping
        }
        else
        {
            // a thread did not stop in time: leave the mappings to the operating system (they go with the process) rather than unmap them under it
            _log.WriteLine($"shutdown: warm-up thread stopped {prewarmDone}, clip decodes stopped {audioDone}, trim thread stopped {trimDone}, icon build stopped {iconDone}; the archives stay mapped until the process exits");
        }

        _window?.Dispose();
        if (_log != Console.Out)
        {
            _log.Dispose();
        }
    }
}

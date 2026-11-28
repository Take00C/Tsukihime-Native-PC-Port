using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;
using Tsuki.Launcher.Core;
using Tsuki.Ui;

namespace Tsuki.Launcher.Avalonia;

/// <summary>A sidebar entry; <paramref name="Glyph"/> is the path data of its icon (24 × 24).</summary>
public sealed record NavItem(LauncherPage Page, string Label, string Glyph);

public sealed record BackupRow(BackupInfo Info)
{
    public string Title => Info.Created.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
    public string Detail => Info.Save.Error is not null ? "save.dat cannot be read" : $"{Info.Save.Describe()}  ·  {GameFolderCheck.FormatSize(Info.Bytes)}";
}

/// <summary>A yes / no question the window asks (restore, import): its heading, its text and the label of the confirming button.</summary>
public sealed record LauncherQuestion(string Title, string Body, string ConfirmLabel);

public enum UnixUpdateStage { Hidden, Offer, Working, Failed, Restarting }

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed partial class UnixLauncherModel : INotifyPropertyChanged
{
    /// <summary>The toast when a save action is pressed while the game runs (the game writes save.dat when it quits).</summary>
    public const string CloseGameFirst = "Close the game first: it writes save.dat when it quits, over anything changed now.";

    /// <summary>The toast when nothing can ask the player before a save is replaced (no window).</summary>
    public const string NobodyToAsk = "Nothing was changed: the launcher window must ask first.";

    private static readonly Language[] LanguageOrder = [Language.Ja, Language.En, Language.Zc, Language.Zt];
    private readonly LauncherPaths _paths;
    private readonly string _launcherDirectory;
    private string? _gameFolderOverride;
    private LauncherProfile _profile;
    private GameFolderInfo _folder = GameFolderCheck.NotSet;
    private string? _appExe;
    private IReadOnlyList<string> _missingFfmpeg = [];
    private LauncherPage _page;
    private string _folderText = string.Empty;
    private string _folderMessage = string.Empty;
    private string _toast = string.Empty;
    private int _toastVersion;
    private Process? _process;
    private Timer? _externalWatch;
    private WriteableBitmap? _heroArt;
    private WriteableBitmap? _heroLogo;
    private string? _heroArtFolder;
    private WriteableBitmap? _gameIcon;
    private string? _gameIconFolder;
    private bool _portableBusy;
    private bool? _portableOverride;
    private bool _profileRestartRequired;

    public UnixLauncherModel(LauncherPaths paths, string launcherDirectory, string? gameFolder = null)
    {
        _paths = paths;
        _launcherDirectory = launcherDirectory;
        _gameFolderOverride = gameFolder;
        ReadProfile();
        NavItems =
        [
            new(LauncherPage.Play, "Play", NavIcons.Play),
            new(LauncherPage.GameFiles, "Game files", NavIcons.Folder),
            new(LauncherPage.Display, "Display", NavIcons.Display),
            new(LauncherPage.Language, "Language & sound", NavIcons.Globe),
            new(LauncherPage.Controls, "Controls", NavIcons.Pad),
            new(LauncherPage.Saves, "Saves", NavIcons.Save),
            new(LauncherPage.Achievements, LauncherTexts.AchievementsTitle, NavIcons.Cup),
            new(LauncherPage.Help, "Help & about", NavIcons.Help),
        ];
        FixCommand = new RelayCommand(p => { if (p is LauncherPage page) { Page = page; } });
        PlayCommand = new RelayCommand(_ => Play(), _ => !GameRunning && !UpdateBusy && !_portableBusy);
        BrowseCommand = new RelayCommand(async _ => { if (BrowseRequested is { } browse) { await browse(); } });
        OpenGameFolderCommand = new RelayCommand(_ => OpenPath(Folder.Path), _ => Folder.State is not (FolderState.NotSet or FolderState.Missing));
        RecheckCommand = new RelayCommand(async _ =>
        {
            ForgetAchievementFolder();
            await RefreshAsync();
        });
        OpenLogCommand = new RelayCommand(_ => OpenLog());
        OpenGuideCommand = new RelayCommand(_ => OpenGuide());
        OpenSavesFolderCommand = new RelayCommand(_ => OpenPath(EnsureDataDirectory()));
        OpenBackupsFolderCommand = new RelayCommand(_ => OpenPath(Directory.Exists(_paths.BackupsDirectory) ? _paths.BackupsDirectory : EnsureDataDirectory()));
        BackupNowCommand = new RelayCommand(_ => BackupNow(), _ => !GameRunning && !_portableBusy);
        PortableSavesCommand = new RelayCommand(async _ => await SetPortableSavesAsync(!PortableSaves), _ => !_portableBusy && !_profileRestartRequired && !GameRunning && !UpdateBusy);
        ImportCommand = new RelayCommand(async _ => await ImportAsync(), _ => !GameRunning && !_portableBusy);
        RestoreCommand = new RelayCommand(async p => { if (p is BackupRow backup) { await RestoreAsync(backup.Info); } }, _ => !GameRunning && !_portableBusy);
        ApplyFolderCommand = new RelayCommand(async _ => await ApplyFolderText());
        ShortcutCommand = new RelayCommand(_ => CreateShortcut(), _ => ShortcutSupported && !GameRunning);
        _log = LauncherLog.For(paths);
        CreateUpdateCommands();
        // what trophies.ini and save.dat show without a game folder; RefreshAsync reads the chapter names and the chapters' scenes of the folder
        _achievementStamp = Achievements.Stamp(paths);
        _achievements = Achievements.Read(paths, null);
        ShowAchievements();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Func<Task>? BrowseRequested;

    /// <summary>The window's file picker: the chosen PS4 save image, or null.</summary>
    public event Func<Task<string?>>? ImportRequested;

    /// <summary>The window's question (restore, import): true for yes. Without it nothing that replaces save.dat goes ahead.</summary>
    public event Func<LauncherQuestion, Task<bool>>? QuestionRequested;

    /// <summary>The game process started (the window minimises).</summary>
    public event Action? GameStarted;

    /// <summary>The game process ended, with its exit code (the window comes back; it takes the front after an error).</summary>
    public event Action<int>? GameExited;

    public IReadOnlyList<NavItem> NavItems { get; }
    public RelayCommand FixCommand { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand OpenGameFolderCommand { get; }
    public RelayCommand RecheckCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand OpenGuideCommand { get; }
    public RelayCommand OpenSavesFolderCommand { get; }
    public RelayCommand OpenBackupsFolderCommand { get; }
    public RelayCommand BackupNowCommand { get; }
    public RelayCommand PortableSavesCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand ApplyFolderCommand { get; }
    public RelayCommand ShortcutCommand { get; }

    public ObservableCollection<ReadyRow> ReadyRows { get; } = [];
    public ObservableCollection<BackupRow> Backups { get; } = [];

    /// <summary>True when a window answers <see cref="QuestionRequested"/>.</summary>
    internal bool CanAsk => QuestionRequested is not null;

    public LauncherPage Page
    {
        get => _page;
        set
        {
            // before the page is made: its rows carry their tiles when it first binds to them
            if (value == LauncherPage.Achievements) { ShowAchievementTiles(); }
            if (Set(ref _page, value)) { Raise(nameof(SelectedNav)); }
        }
    }

    public NavItem SelectedNav
    {
        get => NavItems.First(n => n.Page == _page);
        set { if (value is not null) { Page = value.Page; } }
    }

    public GameFolderInfo Folder
    {
        get => _folder;
        private set { _folder = value; Raise(); }
    }

    public string FolderText
    {
        get => _folderText;
        set => Set(ref _folderText, value);
    }

    public string FolderMessage
    {
        get => _folderMessage;
        private set { if (Set(ref _folderMessage, value)) { Raise(nameof(FolderMessageVisible)); } }
    }

    public bool FolderMessageVisible => _folderMessage.Length > 0;
    public bool CanPlay => ReadyCheck.CanPlay(_folder, _appExe, _missingFfmpeg);
    public string ReadySummary => LauncherTexts.ReadySummary(ReadyRows);
    public bool ReadyAllGood => ReadyRows.All(r => r.State == CheckState.Ok);
    public string HeroTitle => "TSUKIHIME";
    public string GameChip => LauncherTexts.GameChip(_folder);
    public string StatusText => LauncherTexts.StatusText(_folder);
    public string TitleText => LauncherTexts.TitleText(_folder);
    public string BuildDetail => LauncherTexts.BuildDetail(_folder);
    public string LanguagesDetail => LauncherTexts.LanguagesDetail(_folder);
    public string FolderSize => LauncherTexts.FolderSize(_folder);
    public string ChecksText => LauncherTexts.ChecksText(_folder);
    public string ContentsText => LauncherTexts.ContentsText(_folder);
    public string StatusLine => (IsLinux ? _profile with { Settings = _profile.Settings with { Fullscreen = Fullscreen } } : _profile).StatusLine();
    public string PlayLabel => GameRunning ? "RUNNING" : "PLAY";

    /// <summary>
    /// True while the game this launcher started runs, or any game of the launcher's folder (started by Steam or by hand). Such a game is looked for again every
    /// <see cref="ExternalGameInterval"/> while it runs, so that PLAY and the save actions come back when it ends.
    /// </summary>
    public bool GameRunning
    {
        get
        {
            if (_process is { HasExited: false }) { return true; }
            bool external = GameLauncher.IsGameRunningFrom(_launcherDirectory, PlatformServices.AppName);
            if (external) { WatchExternalGame(); }
            return external;
        }
    }

    /// <summary>How often a game the launcher did not start is looked for while it runs.</summary>
    internal TimeSpan ExternalGameInterval { get; init; } = TimeSpan.FromSeconds(2);

    public bool NoBackups => Backups.Count == 0;
    public bool HasBackups => Backups.Count > 0;
    public string LauncherVersion => LauncherTexts.LauncherVersion(typeof(UnixLauncherModel).Assembly);
    public string PlatformLine => "NATIVE PORT  ·  " + (IsMac ? "MACOS" : "LINUX");
    public string AboutLine => $"A fan-made, native {(IsMac ? "macOS" : "Linux")} version of the PS4 game that runs on the files of your own copy.";
    public string DataDirectory => _paths.DataDirectory;
    public bool PortableSaves => _portableOverride ?? PortableProfile.InUse(_paths, _launcherDirectory);
    public bool PortableSavesAvailable => IsLinux;
    public string SaveText => _profile.Saves.Describe() + (_profile.Saves.LastWrite is { } t ? $"  ·  last changed {t.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)}" : string.Empty);
    public string FolderHelp => $"Choose the folder that holds eboot.bin and image/, or the folder above it. A base game with its update in a second folder beside it (CUSA47999 and CUSA47999-patch, or CUSA47999-UPDATE) is read as one game: the update's files take precedence. A .pkg must be extracted first. The folder is remembered for the game too ({_paths.GameDirFile}). Press Enter in the box to check a typed path.";
    public string ShortcutsText => $"Once the game folder is set, the Tsukihime program starts the game on its own: it remembers the folder, and your saves stay in {_paths.DataDirectory} whichever way you start it. The entry puts Tsukihime in your desktop's application menu.";
    public string Toast { get => _toast; private set { if (Set(ref _toast, value)) { Raise(nameof(ToastVisible)); } } }
    public bool ToastVisible => _toast.Length > 0;
    public WriteableBitmap? HeroArt => _heroArt;
    public WriteableBitmap? HeroLogo => _heroLogo;

    /// <summary>Arcueid's face from the player's own files for the window icon (null until a game folder is ready; the window keeps the crescent then).</summary>
    public WriteableBitmap? GameIcon => _gameIcon;

    public int LanguageIndex
    {
        get => Math.Max(0, Array.IndexOf(LanguageOrder, _profile.Language));
        set { if (value >= 0 && value < LanguageOrder.Length) { UpdateSettings(s => s with { GameLanguage = LanguageOrder[value] }); } }
    }

    /// <summary>
    /// Whether the next start is borderless fullscreen. On Linux the player's explicit choice comes first (display_mode: F11, Config › Display Mode or this switch),
    /// and a launcher on a Steam Deck shows the Deck's own default, fullscreen, until there is one. There the switch is such a choice too: it writes display_mode
    /// as well, which a Steam Deck start follows (PLAY itself never writes it).
    /// </summary>
    public bool Fullscreen
    {
        get => IsLinux ? _profile.Settings.RememberedFullscreen ?? (OnSteamDeck || _profile.Fullscreen) : _profile.Fullscreen;
        set => UpdateSettings(s => IsLinux ? s with { Fullscreen = value, RememberedFullscreen = value } : s with { Fullscreen = value });
    }

    public bool VSync
    {
        get => _profile.VSync;
        set => UpdateSettings(s => s with { VSync = value });
    }

    public int PromptsIndex
    {
        get => (int)_profile.Settings.Prompts;
        set { if (value >= 0 && value < 4) { UpdateSettings(s => s with { Prompts = (PromptSetting)value }); } }
    }

    public WindowSizeChoice? WindowSize
    {
        get => WindowSizeChoice.All.FirstOrDefault(c => c.Width == _profile.WindowSize.Width && c.Height == _profile.WindowSize.Height);
        set { if (value is not null) { UpdateSettings(s => s with { WindowWidth = value.Width, WindowHeight = value.Height }); } }
    }

    public IReadOnlyList<WindowSizeChoice> WindowSizes => WindowSizeChoice.All;

    public string WindowSizeNote
    {
        get
        {
            (int w, int h) = _profile.WindowSize;
            return $"The game opens as a {w} × {h} window. It stays inside the monitor's work area, at most 90 % of it, and remembers a size you drag it to.";
        }
    }

    // a Mac keyboard's F11 is a media key, and Fn+F11 shows the desktop: only Option+Return there
    public string FullscreenDescription => $"Borderless fullscreen; off, the game opens as a window. In the game, {(IsMac ? "Option+Return" : "Alt+Enter, F11")} or Config › System › Display Mode switches it at any time, and the switch here shows what the game remembers.";
    public bool ShortcutSupported => IsLinux;
    public string ShortcutLabel => File.Exists(DesktopEntryPath) ? "Replace" : "Create";
    public string ShortcutText => File.Exists(DesktopEntryPath) ? "Tsukihime is in your application menu. It starts the game without the launcher." : "Start the game without the launcher, from your application menu.";

    /// <summary>The application-menu entry the Game files and Play pages write (~/.local/share/applications/tsukihime.desktop); tests point it elsewhere.</summary>
    internal string DesktopEntryPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications", "tsukihime.desktop");

    /// <summary>Linux's rules (the application-menu entry, display_mode, the Steam Deck): the real platform by default; tests choose either on any host.</summary>
    internal bool IsLinux { get; init; } = OperatingSystem.IsLinux();

    /// <summary>The SteamDeck variable the launcher runs with (Steam sets 1 for what it starts on a Deck, the launcher included); tests set it.</summary>
    internal string? SteamDeck { get; init; } = Environment.GetEnvironmentVariable("SteamDeck");

    /// <summary>A Linux launcher with SteamDeck=1: PLAY leaves the window mode to the game's Steam Deck rule (its display_mode, else fullscreen).</summary>
    internal bool OnSteamDeck => IsLinux && SteamDeck == "1";

    /// <summary>Arcueid's face as a PNG in the profile's icons folder (null until a game folder gives it): the application-menu entry and the Mac Dock take it from there.</summary>
    internal string? GameIconPng { get; private set; }

    /// <summary>Shows a PNG (null: the app's own icon) in the macOS Dock; tests give a stand-in.</summary>
    internal Func<string?, bool> DockIcon { get; init; } = MacDock.SetApplicationIcon;

    /// <summary>
    /// Reads the profile and the game folder again and shows what follows. The achievements come before the banner art and the window icon, as in the Windows
    /// launcher: until they are read with the game folder the page has only what it can tell without one (the stand-in names, too few earned from a save).
    /// A step that fails does not skip the ones after it (a folder that cannot be listed left the Achievements page without its game names, and nothing said
    /// so): each runs, a game folder that could not be looked at stays what it was, and the first error is reported once at the end, like any error nothing
    /// caught (launcher_crash.log, the log, the toast). Nothing is thrown; <see cref="RefreshError"/> keeps that error for the verification modes.
    /// </summary>
    public async Task RefreshAsync()
    {
        Exception? error = null;
        void Step(Action step)
        {
            try { step(); }
            catch (Exception e) { error ??= e; }
        }

        async Task StepAsync(Func<Task> step)
        {
            try { await step(); }
            catch (Exception e) { error ??= e; }
        }

        Step(ReadProfile);
        string? remembered = null;
        GameFolderInfo folder = _folder;
        await StepAsync(async () =>
        {
            remembered = _gameFolderOverride ?? _paths.ReadGameDirectory() ?? Environment.GetEnvironmentVariable("TSUKI_GAME_DIR");
            string? asked = remembered;
            folder = await Task.Run(() => GameFolderCheck.Inspect(asked));
        });
        Step(() =>
        {
            _folder = folder;
            FolderText = remembered ?? folder.Path ?? string.Empty;
        });
        Step(() =>
        {
            _appExe = PlatformServices.FindApp(_launcherDirectory);
            _missingFfmpeg = _appExe is null ? [] : PlatformServices.MissingFfmpeg(_appExe);
        });
        Step(ReloadReadyRows);
        Step(ReloadBackups);
        Step(() => Folder = folder);
        await StepAsync(LoadAchievementsAsync);
        await StepAsync(LoadHeroAsync);
        await StepAsync(LoadGameIconAsync);
        RefreshError = error;
        if (error is not null) { ReportCrash(error); }
    }

    /// <summary>
    /// The first error of the last refresh's steps; null when all of them ran through. The window reports it and goes on. The verification modes end with 1
    /// on it (Program.RefreshFailure): a release's checks must not pass over a refresh that failed, and the error ended them before the refresh caught it.
    /// </summary>
    internal Exception? RefreshError { get; private set; }

    /// <summary>
    /// Reads settings.ini and save.dat (LauncherProfile.Read also probes the profile folder by writing to it). The size and time of settings.ini are kept first,
    /// so that the look of the Achievements page's timer does not read again what this read, or what the launcher wrote itself.
    /// </summary>
    [MemberNotNull(nameof(_profile))]
    private void ReadProfile()
    {
        KeepSettingsLook();
        _profile = LauncherProfile.Read(_paths);
    }

    private void ReloadReadyRows()
    {
        ReadyRows.Clear();
        foreach (ReadyRow row in ReadyCheck.Build(_folder, _appExe, _missingFfmpeg, [], _profile.Saves))
        {
            ReadyRow displayRow = row;
            if (!OperatingSystem.IsWindows() && row.Title is "Game program" or "Saves")
            {
                displayRow = row with { Detail = row.Detail.Replace(GameLauncher.AppFileName, "Tsukihime").Replace("%APPDATA%\\Tsuki", _paths.DataDirectory) };
            }

            ReadyRows.Add(displayRow);
        }
    }

    private void ReloadBackups()
    {
        Backups.Clear();
        foreach (BackupInfo backup in SaveTools.List(_paths)) { Backups.Add(new BackupRow(backup)); }
        Raise(nameof(NoBackups), nameof(HasBackups));
    }

    /// <summary>Reads the banner art of a ready game folder once per folder, on a worker thread; the bitmaps it replaces are released.</summary>
    private async Task LoadHeroAsync()
    {
        if (!_folder.IsReady || _folder.Path is null)
        {
            SetHero(null, null, null);
            return;
        }

        if (_heroArt is not null && _heroArtFolder == _folder.Path) { return; }
        string folder = _folder.Path;
        (WriteableBitmap? art, WriteableBitmap? logo) = await Task.Run(() => UnixLauncherArt.LoadHero(folder));
        SetHero(folder, art, logo);
    }

    private void SetHero(string? folder, WriteableBitmap? art, WriteableBitmap? logo)
    {
        (WriteableBitmap? oldArt, WriteableBitmap? oldLogo) = (_heroArt, _heroLogo);
        (_heroArtFolder, _heroArt, _heroLogo) = (folder, art, logo);
        Raise(nameof(HeroArt), nameof(HeroLogo));
        if (!ReferenceEquals(oldArt, art)) { oldArt?.Dispose(); }
        if (!ReferenceEquals(oldLogo, logo)) { oldLogo?.Dispose(); }
    }

    /// <summary>The window icon: Arcueid's face, built once per game folder from the player's files into the profile's icons folder (the game makes the same file).</summary>
    private async Task LoadGameIconAsync()
    {
        if (!_folder.IsReady || _folder.Path is null)
        {
            SetGameIcon(null, null);
            return;
        }

        if (_gameIcon is not null && _gameIconFolder == _folder.Path) { return; }
        string folder = _folder.Path;
        string data = _paths.DataDirectory;
        await UseGameIconAsync(folder, await Task.Run(() => UnixLauncherArt.LoadFaceIcon(data, folder, _log.Write)));
    }

    /// <summary>
    /// The face of <paramref name="folder"/> becomes the window icon, and its PNG goes into the profile's icons folder (the application-menu entry, the Mac Dock and a
    /// Steam shortcut's icon take it from there).
    /// </summary>
    internal async Task UseGameIconAsync(string folder, WriteableBitmap? icon)
    {
        string data = _paths.DataDirectory;
        string? png = icon is null ? null : await Task.Run(() => UnixLauncherArt.SaveIconPng(icon, data));
        SetGameIcon(folder, icon, png);
    }

    private void SetGameIcon(string? folder, WriteableBitmap? icon, string? png = null)
    {
        // the bitmap the window icon was made from is left to the collector: a window icon may keep it
        (_gameIconFolder, _gameIcon, GameIconPng) = (folder, icon, png);
        Raise(nameof(GameIcon));
    }

    private void Play()
    {
        if (ProfileChangeRefused())
        {
            return;
        }

        if (!CanPlay)
        {
            Page = LauncherPage.GameFiles;
            ShowToast(LauncherTexts.CannotPlay(_folder));
            return;
        }

        if (GameRunning || GameLauncher.IsGameRunning())
        {
            // started by hand, by Steam or by another launcher: a second copy would fight the first one over save.dat
            ShowToast(LauncherTexts.GameAlreadyRunning);
            return;
        }

        if (OnSteamDeck)
        {
            _log.Write($"play: SteamDeck=1 (SteamGamepadUI={Environment.GetEnvironmentVariable("SteamGamepadUI") ?? "not set"}): no --fullscreen or --windowed, the game's Steam Deck rule decides");
        }

        Process started;
        try
        {
            started = GameLauncher.Start(PlayPlan(_appExe!, _folder.Path!), _paths);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            ShowToast(LauncherTexts.GameNotStarted(e.Message));
            return;
        }

        Track(started);
    }

    /// <summary>
    /// What PLAY starts: the game with its folder, the log and the switch's window mode; on a Steam Deck without the window mode, so that the game's Deck rule
    /// applies (the player's display_mode, else fullscreen), as when Steam starts the game itself.
    /// </summary>
    /// <summary>
    /// PLAY's command line, from settings.ini and save.dat as they are now: a game started elsewhere (the application menu, Steam) may have changed the window
    /// mode while the launcher was open without noticing it, and PLAY must not put that choice back.
    /// </summary>
    internal LaunchPlan PlayPlan(string appExe, string gameFolder)
    {
        ReadProfileAgain();
        return GameLauncher.Plan(appExe, gameFolder, _paths, Fullscreen, windowMode: !OnSteamDeck);
    }

    /// <summary>
    /// Reads settings.ini and save.dat again and says what follows from them (the window comes to the front, PLAY). With <paramref name="onlyIfChanged"/>,
    /// only when either file was written since the last such read (the read also probes the profile folder by writing to it); the play state is said either way,
    /// which also notices a game started elsewhere.
    /// </summary>
    internal void ReadProfileAgain(bool onlyIfChanged = false)
    {
        (DateTime, DateTime) stamp = (WriteStamp(_paths.SettingsFile), WriteStamp(_paths.SaveFile));
        if (!onlyIfChanged || stamp != _profileStamp)
        {
            _profileStamp = stamp;
            ReadProfile();
            Raise(nameof(Fullscreen), nameof(StatusLine), nameof(VSync), nameof(PromptsIndex), nameof(WindowSize), nameof(WindowSizeNote), nameof(LanguageIndex), nameof(SaveText), nameof(TrophyNotice), nameof(TrophySound));
        }

        // the game may have earned an achievement meanwhile: trophies.ini and save.dat have their own stamp (the page follows them every two seconds too)
        _ = ReadAchievementsAgainAsync(onlyIfChanged: true);
        RaisePlayState();
    }

    private (DateTime Settings, DateTime Save) _profileStamp;

    /// <summary>
    /// When a file was last written, without ever throwing: this is asked each time the window comes to the front. On macOS and Linux the question itself fails
    /// for a file in a folder the user may not enter (a profile folder left with another owner or without its permissions); such a file has no time.
    /// </summary>
    private static DateTime WriteStamp(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return default;
        }
    }

    /// <summary>
    /// Follows the game process PLAY started: PLAY and the save actions wait, the window steps aside, and when the game ends its exit code is said (on the window's
    /// thread, where the process was started).
    /// </summary>
    internal void Track(Process started)
    {
        _process = started;
        SynchronizationContext? ui = SynchronizationContext.Current;
        started.Exited += (_, _) =>
        {
            int code = ExitCode(started);
            if (ui is null) { OnGameExited(started, code); }
            else { ui.Post(_ => OnGameExited(started, code), null); }
        };
        started.EnableRaisingEvents = true;
        RaisePlayState();
        GameStarted?.Invoke();
    }

    private static int ExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return 0; }
    }

    private async void OnGameExited(Process process, int code)
    {
        if (!ReferenceEquals(_process, process)) { return; }
        _process = null;
        process.Dispose();
        try
        {
            RaisePlayState();
            await RefreshAsync();
        }
        finally
        {
            // whatever went wrong on the way: the window stepped aside for the game and comes back
            GameExited?.Invoke(code);
        }

        if (LauncherTexts.GameExited(code) is { } toast) { ShowToast(toast); }
    }

    /// <summary>
    /// Follows a game of the launcher's folder that the launcher did not start (nothing tells the launcher when it ends): it is looked for again on a timer, and
    /// once it has gone the play state is raised and the profile read again on the window's thread, as when a game PLAY started ends. Started by
    /// <see cref="GameRunning"/>; one timer at a time.
    /// </summary>
    private void WatchExternalGame()
    {
        if (_externalWatch is not null) { return; }
        SynchronizationContext? ui = SynchronizationContext.Current;
        var watch = new Timer(_ => ExternalWatchTick(ui));
        if (Interlocked.CompareExchange(ref _externalWatch, watch, null) is not null)
        {
            watch.Dispose();
            return;
        }

        watch.Change(ExternalGameInterval, ExternalGameInterval);
    }

    /// <summary>
    /// One look of the watch for a game the launcher did not start. Only the look that takes the watch away says the game ended: on a slow machine
    /// two looks can overlap (a timer does not wait for its last callback), and without a window's thread to queue on, two reloads would run at once.
    /// </summary>
    /// <returns>Whether this look said that the game ended.</returns>
    internal bool ExternalWatchTick(SynchronizationContext? ui)
    {
        if (GameLauncher.IsGameRunningFrom(_launcherDirectory, PlatformServices.AppName)) { return false; }
        if (Interlocked.Exchange(ref _externalWatch, null) is not { } watch) { return false; }
        watch.Dispose();
        if (ui is null) { OnExternalGameEnded(); }
        else { ui.Post(_ => OnExternalGameEnded(), null); }
        return true;
    }

    /// <summary>A game the launcher did not start has ended: it may have changed settings.ini (F11, Config › Display Mode) and save.dat, so both are read again.</summary>
    private async void OnExternalGameEnded()
    {
        RaisePlayState();
        await RefreshAsync();
    }

    private void RaisePlayState()
    {
        Raise(nameof(GameRunning), nameof(PlayLabel));
        PlayCommand.Raise();
        BackupNowCommand.Raise();
        PortableSavesCommand.Raise();
        ImportCommand.Raise();
        RestoreCommand.Raise();
        ShortcutCommand.Raise();
    }

    /// <summary>True (and a toast) while a portable switch copies the profile, or after one until the launcher reopens: nothing else may change a profile then.</summary>
    private bool ProfileChangeRefused()
    {
        if (_portableBusy)
        {
            ShowToast(LauncherTexts.PortableCopyRunning);
            return true;
        }

        if (!_profileRestartRequired) { return false; }
        ShowToast("Close and reopen the launcher before using the profile you just copied.");
        return true;
    }

    /// <summary>True (and a toast) while the game runs: it writes save.dat when it quits, over a restore, an import or a backup made now.</summary>
    private bool RefuseWhileGameRuns()
    {
        if (ProfileChangeRefused()) { return true; }
        if (!GameRunning) { return false; }
        ShowToast(CloseGameFirst);
        return true;
    }

    private async Task<bool> AskAsync(LauncherQuestion question)
    {
        if (QuestionRequested is not { } ask)
        {
            _log.Write("saves: \"" + question.Title + "\" was not asked (no window), nothing was changed");
            ShowToast(NobodyToAsk);
            return false;
        }

        return await ask(question);
    }

    private async Task SetPortableSavesAsync(bool enable)
    {
        if (_portableBusy || UpdateBusy || ProfileChangeRefused()) { return; }
        _portableBusy = true;
        RaisePlayState();
        try
        {
            PortableProfileResult result = await Task.Run(() => PortableProfile.Set(enable, _paths, _launcherDirectory, gameRunning: () => GameRunning || AnyGameRunning(), beforeCopy: PortableCopyStep));
            if (result.Changed)
            {
                _portableOverride = enable;
                _profileRestartRequired = result.RestartRequired;
            }

            Raise(nameof(PortableSaves));
            ShowToast(result.Message);
        }
        finally
        {
            _portableBusy = false;
            RaisePlayState();
        }
    }

    private void BackupNow()
    {
        if (RefuseWhileGameRuns()) { return; }
        try
        {
            BackupInfo? backup = SaveTools.Backup(_paths, DateTime.Now);
            ShowToast(backup is null ? "There is nothing to back up yet." : "Backed up to " + backup.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast("Backup failed: " + e.Message);
        }

        AfterSaveChange();
    }

    /// <summary>Asks, then replaces save.dat by <paramref name="backup"/> (the current save is backed up first). Refused while the game runs.</summary>
    public async Task RestoreAsync(BackupInfo backup)
    {
        if (RefuseWhileGameRuns()) { return; }
        (string title, string body, string label) = LauncherTexts.RestoreQuestion(backup);
        if (!await AskAsync(new LauncherQuestion(title, body, label)) || RefuseWhileGameRuns()) { return; }
        try
        {
            // what the replaced save implied is recorded first, with the game folder's chapter scenes, and the backup's record is added (not while a game runs)
            SaveTools.Restore(_paths, backup, DateTime.Now, AchievementFolder, AnyGameRunning);
            ShowToast("Restored " + backup.Name + ". The previous save is in the backup list.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast("Restore failed: " + e.Message);
        }

        AfterSaveChange();
    }

    private async Task ImportAsync()
    {
        if (RefuseWhileGameRuns() || ImportRequested is not { } choose) { return; }
        if (await choose() is { } image) { await ImportAsync(image); }
    }

    /// <summary>Asks, then replaces save.dat by the slots of a PS4 save image (the current save is backed up first). Refused while the game runs.</summary>
    public async Task ImportAsync(string imagePath)
    {
        if (RefuseWhileGameRuns()) { return; }
        (string title, string body, string label) = LauncherTexts.ImportQuestion(imagePath);
        if (!await AskAsync(new LauncherQuestion(title, body, label)) || RefuseWhileGameRuns()) { return; }
        try
        {
            // what the replaced save implied is recorded first, with the game folder's chapter scenes (not while a game runs)
            (BackupInfo? _, int slots) = SaveTools.ImportPs4(_paths, imagePath, DateTime.Now, AchievementFolder, AnyGameRunning);
            ShowToast($"Imported {slots} save slots from the PS4 image.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SaveFormatException or ArgumentException)
        {
            ShowToast("Import failed: " + e.Message);
        }

        AfterSaveChange();
    }

    private void AfterSaveChange()
    {
        ReadProfile();
        ReloadReadyRows();
        ReloadBackups();
        Raise();
        // a restored or imported save may show achievements the old one did not
        _ = ReadAchievementsAgainAsync(onlyIfChanged: true);
    }

    public async Task ApplyFolderText() => await SetGameFolderAsync(FolderText);

    public async Task SetGameFolderAsync(string path, bool picker = false)
    {
        if (ProfileChangeRefused()) { return; }
        try
        {
            LauncherFolderChoice choice = await LauncherFolderChoice.ChooseAsync(_paths, path, picker);
            _gameFolderOverride = null;
            FolderText = choice.DisplayPath;
            FolderMessage = choice.Message;
            if (choice.Accepted) { await RefreshAsync(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            FolderMessage = e.Message + " The folder was not remembered.";
        }
    }

    private void UpdateSettings(Func<AppSettingsData, AppSettingsData> change)
    {
        if (ProfileChangeRefused()) { return; }
        if (!TryEnsureDataDirectory() || !AppSettings.Update(_paths.SettingsFile, change))
        {
            ShowToast("settings.ini could not be written (" + _paths.SettingsFile + ").");
        }

        ReadProfile();
        ReloadReadyRows();
        Raise();
    }

    private string? EnsureDataDirectory() => TryEnsureDataDirectory() ? _paths.DataDirectory : null;

    private bool TryEnsureDataDirectory()
    {
        if (ProfileChangeRefused()) { return false; }
        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            ShowToast("The profile folder cannot be made: " + e.Message);
            return false;
        }
    }

    private void OpenLog()
    {
        if (_paths.LogToShow() is { } log) { OpenPath(log); }
        else { ShowToast("There is no log yet. The game writes one each time it runs."); }
    }

    private void OpenGuide()
    {
        string? guide = PlatformServices.FindGuide(_launcherDirectory);
        if (guide is not null) { OpenPath(guide); }
        else { ShowToast("SETUP_GUIDE.txt was not found next to the launcher. The guide is docs/SETUP_GUIDE.md of the source."); }
    }

    private void CreateShortcut()
    {
        if (!ShortcutSupported) { return; }
        try
        {
            string path = DesktopEntryPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // the entry starts the game in its own folder, as the Windows desktop shortcut does: it finds the game folder in game_dir.txt, which the launcher wrote
            string folder = Path.TrimEndingDirectorySeparator(_launcherDirectory);
            string? icon = GameIconPng ?? UnixLauncherArt.SaveIconPng(_gameIcon, _paths.DataDirectory);
            File.WriteAllText(path, PlatformServices.DesktopEntry(Path.Combine(folder, "Tsukihime"), icon, folder));
            ShowToast("Tsukihime was added to your application menu: " + path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast("The application entry could not be created: " + e.Message);
        }

        Raise(nameof(ShortcutLabel), nameof(ShortcutText));
    }

    private void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return; }
        try
        {
            string opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            var start = new ProcessStartInfo(opener) { UseShellExecute = false };
            start.ArgumentList.Add(path);
            Process.Start(start)!.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowToast("It could not be opened: " + e.Message);
        }
    }

    private void OpenUrl(string url) => OpenPath(url);

    private void ShowToast(string text)
    {
        int version = ++_toastVersion;
        Toast = text;
        _log.Write(text);
        _ = HideToast(version);
    }

    private async Task HideToast(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        if (version == _toastVersion) { Toast = string.Empty; }
    }

    /// <summary>A toast (empty: none).</summary>
    internal void Notify(string text)
    {
        if (text.Length > 0) { ShowToast(text); }
        else { Toast = string.Empty; }
    }

    /// <summary>
    /// An error nothing else caught (an exception of a button, say): appended to launcher_crash.log in the profile folder, and the player is told where it is.
    /// Returns the log, or null when it could not be written. Each one is counted (<see cref="ReportedErrors"/>).
    /// </summary>
    internal string? ReportCrash(Exception error)
    {
        ReportedErrors++;
        FirstReportedError ??= error;
        string? log = UnixLauncherCrash.Write(_paths, error);
        _log.Write("launcher error: " + error.GetType().Name + ": " + error.Message);
        ShowToast(UnixLauncherCrash.Message(error, log));
        return log;
    }

    /// <summary>
    /// How many errors the launcher reported as ones nothing caught (<see cref="ReportCrash"/>: a step of the refresh, the window coming to the front, a job
    /// of the window). The window goes on after each. A verification mode ends with 1 when this is not zero at its end (Program.ReportedFailure): a release's
    /// checks read its exit code, not the launcher_crash.log of its profile.
    /// </summary>
    internal int ReportedErrors { get; private set; }

    /// <summary>The first of those errors; null while there was none.</summary>
    internal Exception? FirstReportedError { get; private set; }

    private void Raise(params string?[] names)
    {
        if (names.Length == 0 || names[0] is null)
        {
            PlayCommand.Raise();
            BackupNowCommand.Raise();
            ImportCommand.Raise();
            RestoreCommand.Raise();
            ShortcutCommand.Raise();
            OpenGameFolderCommand.Raise();
            RaiseUpdateButton();
            foreach (string name in new[] { nameof(Folder), nameof(FolderText), nameof(FolderMessage), nameof(FolderMessageVisible), nameof(CanPlay), nameof(ReadySummary), nameof(ReadyAllGood), nameof(GameChip), nameof(StatusText), nameof(TitleText), nameof(BuildDetail), nameof(LanguagesDetail), nameof(FolderSize), nameof(ChecksText), nameof(ContentsText), nameof(StatusLine), nameof(PlayLabel), nameof(GameRunning), nameof(NoBackups), nameof(HasBackups), nameof(SaveText), nameof(LanguageIndex), nameof(JaAvailable), nameof(EnAvailable), nameof(ZcAvailable), nameof(ZtAvailable), nameof(Fullscreen), nameof(VSync), nameof(TrophyNotice), nameof(TrophySound), nameof(PromptsIndex), nameof(WindowSize), nameof(WindowSizeNote), nameof(ShortcutLabel), nameof(ShortcutText), nameof(CheckForUpdatesOnStart), nameof(UpdateCheckDescription), nameof(UpdateStatus), nameof(AvailableUpdate), nameof(UpdateOverlayTitle), nameof(UpdateVersionLine), nameof(UpdateReleaseTitle), nameof(UpdateNotes) })
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
        else
        {
            foreach (string? name in names) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        if (EqualityComparer<T>.Default.Equals(field, value)) { return false; }
        field = value;
        Raise(propertyName);
        return true;
    }
}

// The state behind every page of the launcher (package K17). The settings pages write settings.ini at once; nothing is applied until the game starts and reads it.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Launcher.Core;
using Tsuki.Ui;

namespace Tsuki.Launcher;

/// <summary>A sidebar entry.</summary>
/// <param name="Page">The page it shows.</param>
/// <param name="Label">Its text.</param>
/// <param name="Glyph">Its Segoe Fluent Icons character.</param>
public sealed record NavItem(LauncherPage Page, string Label, string Glyph);

/// <summary>A backup as the saves page lists it.</summary>
public sealed class BackupRow(BackupInfo info, ICommand restore)
{
    /// <summary>The backup.</summary>
    public BackupInfo Info { get; } = info;

    /// <summary>"27 Oct 2026, 15:30".</summary>
    public string Title { get; } = info.Created.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"12 save slots, 18.2 KB".</summary>
    public string Detail { get; } = info.Save.Error is not null ? "save.dat cannot be read" : $"{info.Save.Describe()}  ·  {GameFolderCheck.FormatSize(info.Bytes)}";

    /// <summary>Restores this backup (asks first).</summary>
    public ICommand Restore { get; } = restore;
}

/// <summary>A question the confirm overlay asks.</summary>
/// <param name="Title">The heading.</param>
/// <param name="Body">The explanation.</param>
/// <param name="ConfirmLabel">The text of the confirming button.</param>
/// <param name="Action">What happens on yes.</param>
public sealed record ConfirmRequest(string Title, string Body, string ConfirmLabel, Action Action);

/// <summary>Simple delegate command.</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc/>
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc/>
    public void Execute(object? parameter) => execute(parameter);

    /// <summary>Tells the buttons to ask again.</summary>
    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>The launcher's view model.</summary>
public sealed class LauncherModel : INotifyPropertyChanged
{
    private static readonly Language[] LanguageOrder = [Language.Ja, Language.En, Language.Zc, Language.Zt];

    private readonly LauncherPaths _paths;
    private readonly string _launcherDirectory;
    private string? _gameFolderOverride;
    private LauncherProfile _profile;
    private GameFolderInfo _folder = GameFolderCheck.NotSet;
    private string _folderText = string.Empty;
    private string? _appExe;
    private IReadOnlyList<string> _missingFfmpeg = [];
    private IReadOnlyList<string> _graphics = [];
    private IReadOnlyList<ReadyRow> _readyRows = [];
    private IReadOnlyList<MonitorEntry> _monitors = [];
    private LauncherPage _page;
    private string _toast = string.Empty;
    private ConfirmRequest? _confirm;
    private Process? _process;
    private string? _shadImage;
    private string _folderMessage = string.Empty;
    private ImageSource? _heroArt;
    private ImageSource? _heroLogo;
    private string? _heroArtFolder;
    private string? _gameIconPath;
    private IReadOnlyList<string> _controllers = [];

    /// <summary>Creates the model; call <see cref="RefreshAsync"/> to fill it.</summary>
    /// <param name="paths">The profile folder.</param>
    /// <param name="launcherDirectory">The folder of TsukiLauncher.exe (the game program is looked for there).</param>
    /// <param name="gameFolder">A game folder that replaces the remembered one (the <c>--game</c> option of the launcher; tests).</param>
    public LauncherModel(LauncherPaths paths, string launcherDirectory, string? gameFolder = null)
    {
        _paths = paths;
        _launcherDirectory = launcherDirectory;
        _gameFolderOverride = gameFolder;
        _profile = LauncherProfile.Read(paths);
        NavItems =
        [
            new(LauncherPage.Play, "Play", ""),
            new(LauncherPage.GameFiles, "Game files", ""),
            new(LauncherPage.Display, "Display", ""),
            new(LauncherPage.Language, "Language & sound", ""),
            new(LauncherPage.Controls, "Controls", ""),
            new(LauncherPage.Saves, "Saves", ""),
            new(LauncherPage.Help, "Help & about", ""),
        ];
        FixCommand = new RelayCommand(p => { if (p is LauncherPage page) { Page = page; } });
        PlayCommand = new RelayCommand(_ => Play(), _ => !GameRunning);
        BrowseCommand = new RelayCommand(_ => BrowseRequested?.Invoke());
        OpenGameFolderCommand = new RelayCommand(_ => OpenPath(Folder.Path), _ => Folder.State is not (FolderState.NotSet or FolderState.Missing));
        RecheckCommand = new RelayCommand(async _ => await RefreshAsync());
        OpenLogCommand = new RelayCommand(_ => OpenLog());
        OpenGuideCommand = new RelayCommand(_ => OpenGuide());
        OpenSavesFolderCommand = new RelayCommand(_ => OpenPath(EnsureDataDirectory()));
        OpenBackupsFolderCommand = new RelayCommand(_ => OpenPath(Directory.Exists(_paths.BackupsDirectory) ? _paths.BackupsDirectory : EnsureDataDirectory()));
        BackupNowCommand = new RelayCommand(_ => BackupNow(), _ => !GameRunning);
        ImportCommand = new RelayCommand(_ => ImportRequested?.Invoke(), _ => !GameRunning);
        ImportFoundCommand = new RelayCommand(_ => AskImport(_shadImage!), _ => !GameRunning && _shadImage is not null);
        ConfirmYesCommand = new RelayCommand(_ => AnswerConfirm(true));
        ConfirmNoCommand = new RelayCommand(_ => AnswerConfirm(false));
        ApplyFolderCommand = new RelayCommand(_ => ApplyFolderText());
        ShortcutFolders = ShortcutFolders.ForUser();
        DesktopShortcutCommand = new RelayCommand(_ => AskShortcut(ShortcutKind.DesktopGame));
        DesktopLauncherShortcutCommand = new RelayCommand(_ => AskShortcut(ShortcutKind.DesktopLauncher));
        StartMenuShortcutsCommand = new RelayCommand(_ => AskShortcut(ShortcutKind.StartMenu));
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the user asked to choose a folder (the window shows the dialog).</summary>
    public event Action? BrowseRequested;

    /// <summary>Raised when the user asked to import a save image from a file (the window shows the dialog).</summary>
    public event Action? ImportRequested;

    /// <summary>Raised when the game process started (the window minimises).</summary>
    public event Action? GameStarted;

    /// <summary>Raised when the game process ended, with its exit code.</summary>
    public event Action<int>? GameExited;

    // -- navigation ---------------------------------------------------------------------------------------------------

    /// <summary>The sidebar entries.</summary>
    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>The page on show.</summary>
    public LauncherPage Page
    {
        get => _page;
        set
        {
            if (Set(ref _page, value))
            {
                Raise(nameof(SelectedNav));
            }
        }
    }

    /// <summary>The sidebar entry of <see cref="Page"/>.</summary>
    public NavItem SelectedNav
    {
        get => NavItems.First(n => n.Page == _page);
        set
        {
            if (value is not null)
            {
                Page = value.Page;
            }
        }
    }

    // -- the game folder ----------------------------------------------------------------------------------------------

    /// <summary>What is known about the game folder.</summary>
    public GameFolderInfo Folder
    {
        get => _folder;
        private set
        {
            _folder = value;
            Raise(null);
        }
    }

    /// <summary>The path box of the Game files page.</summary>
    public string FolderText
    {
        get => _folderText;
        set => Set(ref _folderText, value);
    }

    /// <summary>A sentence under the path box after the user chose or typed a folder.</summary>
    public string FolderMessage
    {
        get => _folderMessage;
        private set => Set(ref _folderMessage, value);
    }

    /// <summary>True when the game can start.</summary>
    public bool CanPlay => ReadyCheck.CanPlay(_folder, _appExe, _missingFfmpeg);

    /// <summary>The ready-check rows.</summary>
    public IReadOnlyList<ReadyRow> ReadyRows => _readyRows;

    /// <summary>"All set" or "2 things need attention".</summary>
    public string ReadySummary
    {
        get
        {
            int n = _readyRows.Count(r => r.State == CheckState.Warning);
            return n == 0 ? "All set" : n == 1 ? "1 thing needs attention" : $"{n} things need attention";
        }
    }

    /// <summary>True when every ready-check row is fine.</summary>
    public bool ReadyAllGood => _readyRows.All(r => r.State == CheckState.Ok);

    /// <summary>The title art of the game on the banner; null until it is read (or without a game folder).</summary>
    public ImageSource? HeroArt
    {
        get => _heroArt;
        private set
        {
            _heroArt = value;
            Raise(nameof(HeroArt));
            Raise(nameof(FallbackVisibility));
        }
    }

    /// <summary>The game's own logo (with alpha) over the banner picture; null without art.</summary>
    public ImageSource? HeroLogo => _heroLogo;

    /// <summary>Visible while the game's own logo is the banner title.</summary>
    public Visibility LogoVisibility => _heroLogo is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Visible while the serif title is the banner title (no logo could be read).</summary>
    public Visibility TitleTextVisibility => _heroLogo is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Visible while the drawn moon is the banner.</summary>
    public Visibility FallbackVisibility => _heroArt is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The few keys of the Help page.</summary>
    public IReadOnlyList<KeyRow> EssentialKeys => KeyTable.Essentials;

    /// <summary>"Ready" or why not (package K25: a usable copy that is not the verified build says so).</summary>
    public string StatusText => !_folder.IsReady ? _folder.Summary
        : _folder.IsVerifiedBuild ? "Ready: this is the game the port was made for"
        : $"Ready, but not the verified build: {_folder.BuildText}";

    /// <summary>What the dump is, in words (package K25): "known build: CUSA47999 01.01 (official 1.01 update eboot)", with the update folder when there is one.</summary>
    public string BuildDetail => _folder.BuildName is null
        ? "unknown"
        : _folder.BuildName + (_folder.PatchFolder is { } patch ? $"; update folder {Path.GetFileName(patch)} beside the game, its files take precedence" : string.Empty)
            + (_folder.IgnoredPatchFolder is { } ignored ? $"; the update folder {Path.GetFileName(ignored)} beside the game is not needed (the game folder already holds an update as new)" : string.Empty)
            + (_folder.TablesNote is { } note ? $"; {note}" : string.Empty);

    /// <summary>The languages the data carries, in words (package K25): "日本語, English, 简体中文, 繁體中文".</summary>
    public string LanguagesDetail => _folder.State == FolderState.Ready
        ? string.Join(", ", _folder.LanguageList.Select(LanguageName))
        : "unknown";

    private static string LanguageName(Language language) => language switch
    {
        Language.Ja => "日本語",
        Language.Zc => "简体中文",
        Language.Zt => "繁體中文",
        Language.Ko => "한국어",
        _ => "English",
    };

    /// <summary>Whether a language tile is shown: the data carries the language, or no folder is ready yet (package K25).</summary>
    public Visibility LanguageVisibility(Language language) => !_folder.IsReady || _folder.LanguageList.Contains(language) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The Japanese tile.</summary>
    public Visibility JaVisibility => LanguageVisibility(Language.Ja);

    /// <summary>The English tile.</summary>
    public Visibility EnVisibility => LanguageVisibility(Language.En);

    /// <summary>The Simplified Chinese tile.</summary>
    public Visibility ZcVisibility => LanguageVisibility(Language.Zc);

    /// <summary>The Traditional Chinese tile.</summary>
    public Visibility ZtVisibility => LanguageVisibility(Language.Zt);

    /// <summary>"CUSA47999, version 01.01".</summary>
    public string TitleText => _folder.TitleId is null
        ? "unknown"
        : $"{_folder.TitleId}  ·  version {_folder.AppVersion ?? "?"}{(_folder.IsReady && _folder.SfoVersion is { } sfo && sfo != _folder.AppVersion ? $" (param.sfo says {sfo})" : string.Empty)}";

    /// <summary>"120 of 120 passed".</summary>
    public string ChecksText => _folder.ChecksTotal == 0 ? "not run" : $"{_folder.ChecksPassed} of {_folder.ChecksTotal} passed";

    /// <summary>"20 archives and data files, 134 movies".</summary>
    public string ContentsText => _folder.ChecksTotal == 0 ? "unknown" : $"{_folder.Archives} archives and data files, {_folder.Movies} movies";

    /// <summary>The connected controllers, in words.</summary>
    public string ControllersText => _controllers.Count == 0 ? "No controller detected" : string.Join("\n", _controllers.Select(c => "•  " + c));

    /// <summary>"CUSA47999 · game version 01.01" (a folder that is set but incomplete says "version unknown", not "not set"; package R1).</summary>
    public string GameChip => _folder.State switch
    {
        FolderState.NotSet or FolderState.Missing => "CUSA47999  ·  game folder not set",
        FolderState.NotTheGame or FolderState.PkgFile or FolderState.UpdateOnly or FolderState.Several => "CUSA47999  ·  not the game folder",
        FolderState.Ready when !_folder.IsVerifiedBuild => $"{_folder.TitleId ?? "CUSA47999"}  ·  game version {_folder.AppVersion ?? "unknown"}  ·  not the verified build",
        _ => $"{_folder.TitleId ?? "CUSA47999"}  ·  game version {_folder.AppVersion ?? "unknown"}",
    };

    /// <summary>The launcher's own version.</summary>
    public string LauncherVersion => "Launcher " + (typeof(LauncherModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0");

    /// <summary>"46.3 GB".</summary>
    public string FolderSize => _folder.TotalBytes > 0 ? GameFolderCheck.FormatSize(_folder.TotalBytes) : "unknown";

    /// <summary>The adapter name for the Display page.</summary>
    public string GraphicsName => _graphics.Count > 0 ? string.Join(", ", _graphics) : "not detected";

    // -- settings -----------------------------------------------------------------------------------------------------

    /// <summary>The language of the next start as an index into ja, en, zc, zt.</summary>
    public int LanguageIndex
    {
        get => Math.Max(0, Array.IndexOf(LanguageOrder, _profile.Language));
        set
        {
            if (value is >= 0 and < 4)
            {
                UpdateSettings(s => s with { GameLanguage = LanguageOrder[value] });
            }
        }
    }

    /// <summary>Borderless fullscreen at the next start.</summary>
    public bool Fullscreen
    {
        get => _profile.Fullscreen;
        set => UpdateSettings(s => s with { Fullscreen = value });
    }

    /// <summary>VSync.</summary>
    public bool VSync
    {
        get => _profile.VSync;
        set => UpdateSettings(s => s with { VSync = value });
    }

    /// <summary>The button prompts as an index into auto, keyboard, playstation, xbox.</summary>
    public int PromptsIndex
    {
        get => (int)_profile.Settings.Prompts;
        set
        {
            if (value is >= 0 and < 4)
            {
                UpdateSettings(s => s with { Prompts = (PromptSetting)value });
            }
        }
    }

    /// <summary>The window size presets the Display page offers: the ones that fit the work area of the chosen monitor (every preset while no monitor is known); package K19.</summary>
    public IReadOnlyList<WindowSizeChoice> WindowSizes => SelectedMonitor is { } m ? WindowSizeChoice.For(m.WorkArea) : WindowSizeChoice.All;

    /// <summary>The preset in use, or null for a size that is none of the offered ones (a size the player dragged the window to, or one too big for the monitor, which the game clamps).</summary>
    public WindowSizeChoice? SelectedWindowSize
    {
        get
        {
            (int w, int h) = _profile.WindowSize;
            return WindowSizes.FirstOrDefault(c => c.Width == w && c.Height == h);
        }
        set
        {
            if (value is not null)
            {
                UpdateSettings(s => s with { WindowWidth = value.Width, WindowHeight = value.Height });
            }
        }
    }

    /// <summary>"The game opens as a 1600 × 900 window on Display 1." (the size the game will use, after its clamp to the monitor).</summary>
    public string WindowSizeNote
    {
        get
        {
            MonitorEntry? m = SelectedMonitor;
            (int w, int h) = _profile.EffectiveWindowSize(m?.WorkArea);
            (int rw, int rh) = _profile.WindowSize;
            string clamp = (w, h) != (rw, rh) ? $" (settings.ini asks for {rw} × {rh}, which does not fit)" : string.Empty;
            return $"The game opens as a {w} × {h} window{(m is null ? string.Empty : $" on Display {m.Index}")}{clamp}. It stays inside the monitor's work area, at most 90 % of it, and remembers a size you drag it to.";
        }
    }

    /// <summary>The attached monitors.</summary>
    public IReadOnlyList<MonitorEntry> Monitors => _monitors;

    /// <summary>The monitor to open on (the primary one when the file names none that is attached).</summary>
    public MonitorEntry? SelectedMonitor
    {
        get => _profile.Settings.Monitor is { } m
            ? _monitors.FirstOrDefault(x => x.X == m.X && x.Y == m.Y) ?? _monitors.FirstOrDefault(x => x.Primary)
            : _monitors.FirstOrDefault(x => x.Primary);
        set
        {
            if (value is not null)
            {
                UpdateSettings(s => s with { Monitor = new MonitorOrigin(value.X, value.Y) });
            }
        }
    }

    /// <summary>The footer's status line.</summary>
    public string StatusLine => _profile.StatusLine(SelectedMonitor is { } m ? (m.Width, m.Height) : null, SelectedMonitor?.WorkArea);

    /// <summary>The key table.</summary>
    public IReadOnlyList<KeyRow> Keys => KeyTable.Rows;

    // -- saves --------------------------------------------------------------------------------------------------------

    /// <summary>What save.dat holds.</summary>
    public SaveSummary Saves => _profile.Saves;

    /// <summary>The sentence under "Save data".</summary>
    public string SaveText => _profile.Saves.Describe() + (_profile.Saves.LastWrite is { } t ? $"  ·  last changed {t.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)}" : string.Empty);

    /// <summary>The save folder, shown in short.</summary>
    public string DataDirectory => _paths.DataDirectory;

    /// <summary>The backups, newest first.</summary>
    public ObservableCollection<BackupRow> Backups { get; } = [];

    /// <summary>True when there are no backups.</summary>
    public bool NoBackups => Backups.Count == 0;

    /// <summary>A found shadPS4 image, or null.</summary>
    public string? ShadImage => _shadImage;

    /// <summary>True when a shadPS4 image was found.</summary>
    public bool HasShadImage => _shadImage is not null;

    // -- overlays and state -------------------------------------------------------------------------------------------

    /// <summary>The message at the bottom (empty: none).</summary>
    public string Toast
    {
        get => _toast;
        private set
        {
            if (Set(ref _toast, value))
            {
                Raise(nameof(ToastVisible));
            }
        }
    }

    /// <summary>True while a message is shown.</summary>
    public bool ToastVisible => _toast.Length > 0;

    /// <summary>The pending question, or null.</summary>
    public ConfirmRequest? Confirm
    {
        get => _confirm;
        private set
        {
            if (Set(ref _confirm, value))
            {
                Raise(nameof(ConfirmVisible));
            }
        }
    }

    /// <summary>True while a question is shown.</summary>
    public bool ConfirmVisible => _confirm is not null;

    /// <summary>True while the game process runs.</summary>
    public bool GameRunning => _process is { HasExited: false };

    /// <summary>The text of the big button.</summary>
    public string PlayLabel => GameRunning ? "RUNNING" : "PLAY";

    // -- commands -----------------------------------------------------------------------------------------------------

    /// <summary>Opens the page in <c>CommandParameter</c>.</summary>
    public ICommand FixCommand { get; }

    /// <summary>Starts the game.</summary>
    public ICommand PlayCommand { get; }

    /// <summary>Asks for a folder.</summary>
    public ICommand BrowseCommand { get; }

    /// <summary>Opens the game folder in Explorer.</summary>
    public ICommand OpenGameFolderCommand { get; }

    /// <summary>Checks the folder again.</summary>
    public ICommand RecheckCommand { get; }

    /// <summary>Opens the log.</summary>
    public ICommand OpenLogCommand { get; }

    /// <summary>Opens the player's setup guide, <c>SETUP_GUIDE.txt</c> next to the launcher (package K29).</summary>
    public ICommand OpenGuideCommand { get; }

    /// <summary>The setup guide next to the launcher, or null when the folder has none (a development output without it).</summary>
    public string? SetupGuidePath => GameLauncher.FindSetupGuide(AppContext.BaseDirectory);

    /// <summary>Opens the save folder.</summary>
    public ICommand OpenSavesFolderCommand { get; }

    /// <summary>Opens the backup folder.</summary>
    public ICommand OpenBackupsFolderCommand { get; }

    /// <summary>Backs up save.dat and settings.ini.</summary>
    public ICommand BackupNowCommand { get; }

    /// <summary>Asks for a PS4 image file.</summary>
    public ICommand ImportCommand { get; }

    /// <summary>Imports the image that was found under shadPS4.</summary>
    public ICommand ImportFoundCommand { get; }

    /// <summary>Answers the question yes.</summary>
    public ICommand ConfirmYesCommand { get; }

    /// <summary>Answers the question no.</summary>
    public ICommand ConfirmNoCommand { get; }

    /// <summary>Applies the path box.</summary>
    public ICommand ApplyFolderCommand { get; }

    /// <summary>Creates Tsukihime.lnk on the desktop (asks before replacing one); package K20.</summary>
    public ICommand DesktopShortcutCommand { get; }

    /// <summary>Creates Tsukihime Launcher.lnk on the desktop.</summary>
    public ICommand DesktopLauncherShortcutCommand { get; }

    /// <summary>Creates both shortcuts in Start menu > Programs > Tsukihime.</summary>
    public ICommand StartMenuShortcutsCommand { get; }

    /// <summary>Where the shortcuts go: the user's desktop and Start menu, or a temporary folder in the tests and the self-test (never the real desktop there).</summary>
    public ShortcutFolders ShortcutFolders { get; set; }

    /// <summary>The game program the shortcuts point at (null until the refresh found it).</summary>
    public string? GameExe => _appExe;

    /// <summary>The launcher program next to the game, or null in a development build of the game alone.</summary>
    public string? LauncherExe => GameLauncher.FindLauncher(_launcherDirectory);

    /// <summary>"Tsukihime.lnk is on your desktop" or "No desktop shortcut yet": the state of the desktop shortcut for the Play page.</summary>
    public string DesktopShortcutText =>
        File.Exists(Path.Combine(ShortcutFolders.Desktop, Shortcuts.GameLinkName)) ? $"{Shortcuts.GameLinkName} is on your desktop. It starts the game without the launcher." : "Start the game without the launcher, from your desktop.";

    /// <summary>The label of the desktop shortcut button: "Create" or "Replace".</summary>
    public string DesktopShortcutLabel => File.Exists(Path.Combine(ShortcutFolders.Desktop, Shortcuts.GameLinkName)) ? "Replace" : "Create";

    // -- refresh ------------------------------------------------------------------------------------------------------

    /// <summary>Reads everything again: the profile, the game folder (on a worker thread), the machine.</summary>
    public async Task RefreshAsync()
    {
        _profile = LauncherProfile.Read(_paths);
        string? remembered = _gameFolderOverride ?? _paths.ReadGameDirectory() ?? Environment.GetEnvironmentVariable("TSUKI_GAME_DIR");
        string appDir = _launcherDirectory;
        (GameFolderInfo folder, IReadOnlyList<string> graphics, IReadOnlyList<MonitorEntry> monitors, string? shad, IReadOnlyList<string> pads) = await Task.Run(() =>
        {
            GameFolderInfo info = GameFolderCheck.Inspect(remembered);
            return (info, SystemInfo.VideoAdapters(), SystemInfo.Monitors(), SaveTools.FindShadPs4Image(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)), Controllers.Connected());
        });
        _controllers = pads;
        _graphics = graphics;
        _monitors = monitors;
        _shadImage = shad;
        _appExe = GameLauncher.FindApp(appDir);
        _missingFfmpeg = _appExe is null ? [] : GameLauncher.MissingFfmpegDlls(Path.GetDirectoryName(_appExe)!);
        if (_folderText.Length == 0 || remembered is not null)
        {
            _folderText = folder.Path ?? string.Empty;
        }

        _folder = folder;
        RebuildDerived();
        Folder = folder;
        FolderMessage = string.Empty;
        ReloadBackups();
        await LoadHeroAsync();
        await LoadGameIconAsync();
    }

    /// <summary>
    /// The Arcueid icon of a ready game folder (package K23): <c>icons\tsukihime.ico</c> in the profile folder, built from the player's files on a worker thread when it
    /// is missing or was made from another folder; null while the folder is not ready or the picture is not there (the window and the shortcuts keep the crescent).
    /// </summary>
    public string? GameIconPath
    {
        get => _gameIconPath;
        private set => Set(ref _gameIconPath, value);
    }

    /// <summary>Builds or finds the cached Arcueid icon for the current folder (see <see cref="GameIconPath"/>).</summary>
    public async Task LoadGameIconAsync()
    {
        if (!_folder.IsReady || _folder.Path is null)
        {
            GameIconPath = null;
            return;
        }

        string folder = _folder.Path;
        string data = _paths.DataDirectory;
        GameIconPath = await Task.Run(() => Tsuki.Data.Icons.FaceIconCache.Ensure(data, folder, () => Tsuki.Data.GameFolderSource.Open(folder)));
    }

    /// <summary>Reads the banner art of a ready game folder (once per folder) on a worker thread.</summary>
    public async Task LoadHeroAsync()
    {
        if (!_folder.IsReady || _folder.Path is null)
        {
            _heroArtFolder = null;
            _heroLogo = null;
            HeroArt = null;
            return;
        }

        if (_heroArt is not null && _heroArtFolder == _folder.Path)
        {
            return;
        }

        string folder = _folder.Path;
        (BitmapSource? art, BitmapSource? logo) = await Task.Run(() => LauncherArt.LoadHero(folder));
        _heroArtFolder = folder;
        _heroLogo = logo;
        Raise(nameof(HeroLogo));
        Raise(nameof(LogoVisibility));
        Raise(nameof(TitleTextVisibility));
        HeroArt = art;
    }

    private void RebuildDerived()
    {
        _readyRows = ReadyCheck.Build(_folder, _appExe, _missingFfmpeg, _graphics, _profile.Saves);
    }

    private void ReloadBackups()
    {
        Backups.Clear();
        foreach (BackupInfo b in SaveTools.List(_paths))
        {
            Backups.Add(new BackupRow(b, new RelayCommand(_ => AskRestore(b), _ => !GameRunning)));
        }

        Raise(nameof(NoBackups));
    }

    private void UpdateSettings(Func<AppSettingsData, AppSettingsData> change)
    {
        EnsureDataDirectory();
        if (!AppSettings.Update(_paths.SettingsFile, change))
        {
            ShowToast("settings.ini could not be written (" + _paths.SettingsFile + ").");
        }

        _profile = LauncherProfile.Read(_paths);
        RebuildDerived();
        Raise(null);
    }

    // -- game folder --------------------------------------------------------------------------------------------------

    /// <summary>Takes the folder the user chose or typed: checks it and, when it holds the game, remembers it for the game and the launcher.</summary>
    public async Task SetGameFolderAsync(string path)
    {
        string resolved = GameFolderCheck.Resolve(path);
        GameFolderInfo info = await Task.Run(() => GameFolderCheck.Inspect(resolved));
        _gameFolderOverride = null;           // the user chose: from now on the remembered folder is the one that counts, not the --game option of this launch
        _folderText = info.Path ?? path;
        Raise(nameof(FolderText));
        if (info.State is FolderState.NotTheGame or FolderState.Missing or FolderState.NotSet or FolderState.PkgFile or FolderState.UpdateOnly or FolderState.Several)
        {
            // the rejected folder is not taken as the model's folder: the remembered one still holds the game, so the Ready check, the banner and PLAY keep telling
            // the truth about it (package R1); the sentence under the box says what was wrong with the typed one
            FolderMessage = info.Summary + " The folder was not remembered.";
            return;
        }

        bool saved = _paths.WriteGameDirectory(resolved);
        FolderMessage = saved ? (resolved == path ? string.Empty : "Using " + resolved) : "The folder could not be remembered (" + _paths.GameDirFile + ").";
        await RefreshAsync();
    }

    private async void ApplyFolderText()
    {
        await SetGameFolderAsync(_folderText);
    }

    // -- play ---------------------------------------------------------------------------------------------------------

    private void Play()
    {
        if (!CanPlay)
        {
            Page = LauncherPage.GameFiles;
            ShowToast(_folder.IsReady
                ? "The game program or its libraries are missing (see Help)."
                : _folder.State == FolderState.Incomplete ? "The game files are not complete: see the problems on this page." : "Choose your game folder first.");
            return;
        }

        if (GameLauncher.IsGameRunning())
        {
            // started by hand, or by another launcher: a second copy would die on the shared log file or overwrite the first one's save.dat at quit (package R1)
            ShowToast("The game is already running.");
            return;
        }

        try
        {
            LaunchPlan plan = GameLauncher.Plan(_appExe!, _folder.Path!, _paths, Fullscreen);
            _process = GameLauncher.Start(plan, _paths);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            ShowToast("The game could not be started: " + e.Message);
            return;
        }

        _process.EnableRaisingEvents = true;
        var started = _process;
        started.Exited += (_, _) =>
        {
            int code = started.ExitCode;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnGameExited(code));
        };
        Raise(nameof(GameRunning));
        Raise(nameof(PlayLabel));
        (PlayCommand as RelayCommand)?.Raise();
        GameStarted?.Invoke();
    }

    private async void OnGameExited(int code)
    {
        _process = null;
        Raise(nameof(GameRunning));
        Raise(nameof(PlayLabel));
        (PlayCommand as RelayCommand)?.Raise();
        await RefreshAsync();
        GameExited?.Invoke(code);
        if (code == GameLauncher.ExitAlreadyRunning)
        {
            ShowToast("The game is already running in another window.");
        }
        else if (code != 0)
        {
            ShowToast($"The game closed with an error (code {code}). Press Log to see why.");
        }
    }

    // -- saves --------------------------------------------------------------------------------------------------------

    private void BackupNow()
    {
        try
        {
            BackupInfo? b = SaveTools.Backup(_paths, DateTime.Now);
            ShowToast(b is null ? "There is nothing to back up yet." : "Backed up to " + b.Name);
            ReloadBackups();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast("Backup failed: " + e.Message);
        }
    }

    private void AskRestore(BackupInfo backup)
    {
        Confirm = new ConfirmRequest(
            "Restore this backup?",
            $"save.dat will be replaced by the backup of {backup.Created.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)} ({backup.Save.Describe()}). " +
            "Your current save is backed up first, so you can undo this. Settings are kept as they are.",
            "Restore",
            () => DoRestore(backup));
    }

    private void DoRestore(BackupInfo backup)
    {
        try
        {
            SaveTools.Restore(_paths, backup, DateTime.Now);
            ShowToast("Restored " + backup.Name + ". The previous save is in the backup list.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast("Restore failed: " + e.Message);
        }

        AfterSaveChange();
    }

    /// <summary>Asks before replacing the save by a PS4 image.</summary>
    public void AskImport(string imagePath)
    {
        Confirm = new ConfirmRequest(
            "Import this PS4 save?",
            $"{Path.GetFileName(imagePath)} is read (the file itself is never changed). Its slots and system data replace save.dat; your current save is backed up first.",
            "Import",
            () => DoImport(imagePath));
    }

    private void DoImport(string imagePath)
    {
        try
        {
            (BackupInfo? _, int slots) = SaveTools.ImportPs4(_paths, imagePath, DateTime.Now);
            ShowToast($"Imported {slots} save slots from the PS4 image.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Tsuki.Engine.Save.SaveFormatException)
        {
            ShowToast("Import failed: " + e.Message);
        }

        AfterSaveChange();
    }

    private void AfterSaveChange()
    {
        _profile = LauncherProfile.Read(_paths);
        RebuildDerived();
        ReloadBackups();
        Raise(null);
    }

    // -- shortcuts (package K20) --------------------------------------------------------------------------------------

    /// <summary>Which shortcuts a command makes.</summary>
    public enum ShortcutKind
    {
        /// <summary>Tsukihime.lnk on the desktop.</summary>
        DesktopGame,

        /// <summary>Tsukihime Launcher.lnk on the desktop.</summary>
        DesktopLauncher,

        /// <summary>Both, in Start menu > Programs > Tsukihime.</summary>
        StartMenu,
    }

    /// <summary>The shortcut files a kind writes, or the reason none can be written.</summary>
    public (IReadOnlyList<ShortcutSpec> Specs, string? Problem) PlanShortcuts(ShortcutKind kind)
    {
        string? game = _appExe;
        string? launcher = LauncherExe;
        switch (kind)
        {
            case ShortcutKind.DesktopGame:
                return game is null ? ([], $"{GameLauncher.AppFileName} was not found next to the launcher.") : ([Shortcuts.Game(ShortcutFolders.Desktop, game, ShortcutIcon)], null);
            case ShortcutKind.DesktopLauncher:
                return launcher is null ? ([], $"{GameLauncher.LauncherFileName} was not found (a development build).") : ([Shortcuts.Launcher(ShortcutFolders.Desktop, launcher)], null);
            default:
                if (game is null)
                {
                    return ([], $"{GameLauncher.AppFileName} was not found next to the launcher.");
                }

                var specs = new List<ShortcutSpec> { Shortcuts.Game(ShortcutFolders.StartMenu, game, ShortcutIcon) };
                if (launcher is not null)
                {
                    specs.Add(Shortcuts.Launcher(ShortcutFolders.StartMenu, launcher));
                }

                return (specs, null);
        }
    }

    /// <summary>The icon the game shortcuts take: the cached Arcueid icon when it exists (package K23), else null for the executable's own crescent.</summary>
    private string? ShortcutIcon => _gameIconPath is { } icon && File.Exists(icon) ? icon : null;

    private void AskShortcut(ShortcutKind kind)
    {
        (IReadOnlyList<ShortcutSpec> specs, string? problem) = PlanShortcuts(kind);
        if (problem is not null)
        {
            ShowToast("No shortcut was made: " + problem);
            return;
        }

        string[] existing = [.. specs.Where(s => File.Exists(s.LinkPath)).Select(s => Path.GetFileName(s.LinkPath))];
        if (existing.Length == 0)
        {
            CreateShortcuts(specs);
            return;
        }

        string where = kind == ShortcutKind.StartMenu ? "in the Start menu" : "on your desktop";
        Confirm = new ConfirmRequest(
            existing.Length == 1 ? "Replace the shortcut?" : "Replace the shortcuts?",
            $"{string.Join(" and ", existing)} already {(existing.Length == 1 ? "exists" : "exist")} {where}. The new shortcut points at {Path.GetFileName(specs[0].TargetPath)} in {specs[0].WorkingDirectory}.",
            "Replace",
            () => CreateShortcuts(specs));
    }

    /// <summary>Writes the shortcuts and says what happened (a toast); the Play page's text follows.</summary>
    public void CreateShortcuts(IReadOnlyList<ShortcutSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        try
        {
            foreach (ShortcutSpec spec in specs)
            {
                Shortcuts.Create(spec);
            }

            string folder = Path.GetDirectoryName(specs[0].LinkPath)!;
            string where = string.Equals(folder, ShortcutFolders.Desktop, StringComparison.OrdinalIgnoreCase) ? "on your desktop" : "in the Start menu (Programs > Tsukihime)";
            ShowToast(specs.Count == 1
                ? $"{Path.GetFileName(specs[0].LinkPath)} was created {where}."
                : $"{string.Join(" and ", specs.Select(s => Path.GetFileName(s.LinkPath)))} were created {where}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidCastException or NotSupportedException)
        {
            ShowToast("The shortcut could not be created: " + e.Message);
        }

        Raise(nameof(DesktopShortcutText));
        Raise(nameof(DesktopShortcutLabel));
    }

    // -- helpers ------------------------------------------------------------------------------------------------------

    private void AnswerConfirm(bool yes)
    {
        ConfirmRequest? request = _confirm;
        Confirm = null;
        if (yes)
        {
            request?.Action();
        }
    }

    /// <summary>Shows a message for a few seconds.</summary>
    public void ShowToast(string text)
    {
        Toast = text;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_toast == text)
            {
                Toast = string.Empty;
            }
        };
        timer.Start();
    }

    private string EnsureDataDirectory()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        return _paths.DataDirectory;
    }

    private void OpenLog()
    {
        string? log = _paths.LogToShow();
        if (log is null)
        {
            ShowToast("There is no log yet. The game writes one each time it runs.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { log }, UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            OpenPath(_paths.DataDirectory);
        }
    }

    /// <summary>The setup guide in Notepad (as the log), else its folder; a missing file is said in a toast (package K29).</summary>
    private void OpenGuide()
    {
        string? guide = SetupGuidePath;
        if (guide is null)
        {
            ShowToast($"{GameLauncher.SetupGuideFileName} was not found next to the launcher. The guide is docs/SETUP_GUIDE.md of the source.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { guide }, UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            OpenPath(Path.GetDirectoryName(guide));
        }
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Explorer could not open it; nothing else to try
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
